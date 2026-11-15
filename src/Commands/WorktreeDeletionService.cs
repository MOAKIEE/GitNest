using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SourceGit.Commands
{
    /// <summary>
    /// Handles worktree preflight inspection, safe locked/ignored/nested protection,
    /// detached-HEAD backup recovery, ordered cleanup (worktree -> local branch -> remote branch),
    /// unlock-and-reinspect flow, and dry-run metadata pruning.
    /// </summary>
    public static class WorktreeDeletionService
    {
        public static async Task<Models.WorktreeInspectPreflight> InspectWorktreeAsync(
            string repoPath,
            string targetWorktreePath,
            bool probeUpstreamRemote = false,
            bool includeAttachedBranchPreflight = true,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(repoPath))
                throw new ArgumentException("无效的仓库路径。", nameof(repoPath));
            if (string.IsNullOrWhiteSpace(targetWorktreePath))
                throw new ArgumentException("无效的工作树路径。", nameof(targetWorktreePath));

            var core = await InspectWorktreeCoreAsync(repoPath, targetWorktreePath, cancellationToken)
                .ConfigureAwait(false);

            if (!includeAttachedBranchPreflight ||
                core.IsMainWorktree ||
                core.IsBare ||
                core.IsDirectoryMissing ||
                !core.IdentityValid ||
                core.IsDetached ||
                string.IsNullOrWhiteSpace(core.BranchName))
            {
                return core;
            }

            var branchPreflight = await BranchDeletionService.InspectLocalBranchCoreAsync(
                core.MainRepoPath,
                core.BranchName,
                probeUpstreamRemote,
                core,
                cancellationToken).ConfigureAwait(false);

            return WithAttachedBranchPreflight(core, branchPreflight);
        }

        public static async Task<Models.WorktreeDeleteOutcome> DeleteWorktreeAsync(
            string repoPath,
            string targetWorktreePath,
            Models.WorktreeDeleteOptions options = null,
            Models.OperationJournalStore journalStore = null,
            Models.ICommandLog outerLog = null,
            CancellationToken cancellationToken = default)
        {
            options ??= new Models.WorktreeDeleteOptions();
            var preflight = options.Preflight ?? await InspectWorktreeAsync(
                repoPath,
                targetWorktreePath,
                probeUpstreamRemote: options.DeleteLocalBranch && options.DeleteLinkedUpstreamRemote,
                includeAttachedBranchPreflight: options.DeleteLocalBranch,
                cancellationToken: cancellationToken).ConfigureAwait(false);

            var executionRoot = string.IsNullOrWhiteSpace(preflight.MainRepoPath) ? repoPath : preflight.MainRepoPath;
            if (preflight.IsMainWorktree || preflight.IsBare)
            {
                return new Models.WorktreeDeleteOutcome
                {
                    MainRepoPath = executionRoot,
                    TargetWorktreePath = preflight.TargetWorktreePath,
                    BranchName = preflight.BranchName,
                    IsDetached = preflight.IsDetached,
                    BlockedByProtection = true,
                    Summary = preflight.IsBare ? "不能删除裸仓库主体。" : "主工作树不提供删除操作。",
                };
            }

            var deleteLocal = options.DeleteLocalBranch;
            var branchPreflight = preflight.AttachedBranchPreflight;
            if (deleteLocal && (preflight.IsDetached || string.IsNullOrWhiteSpace(preflight.BranchName) || branchPreflight == null))
            {
                return new Models.WorktreeDeleteOutcome
                {
                    MainRepoPath = executionRoot,
                    TargetWorktreePath = preflight.TargetWorktreePath,
                    BranchName = preflight.BranchName,
                    IsDetached = preflight.IsDetached,
                    BlockedByProtection = true,
                    Summary = preflight.IsDetached
                        ? "目标工作树处于 detached HEAD 状态，没有可关联删除的本地分支。"
                        : "无法解析工作树当前检出的本地分支信息。",
                };
            }

            var deleteRemote = deleteLocal && options.DeleteLinkedUpstreamRemote;
            var confirmedExpectedSha = options.ConfirmedExpectedRemoteTipSha ?? branchPreflight?.RemoteProbe?.LiveTipSha ?? string.Empty;
            var confirmedRemoteExists = options.ConfirmedRemoteExistsOnServer ?? branchPreflight?.RemoteProbe?.ExistsOnServer;

            if (deleteRemote)
            {
                if (!branchPreflight.HasConfiguredUpstream || string.IsNullOrEmpty(branchPreflight.RemoteName))
                {
                    return new Models.WorktreeDeleteOutcome
                    {
                        MainRepoPath = executionRoot,
                        TargetWorktreePath = preflight.TargetWorktreePath,
                        BranchName = preflight.BranchName,
                        BlockedByProtection = true,
                        Summary = $"关联本地分支 '{preflight.BranchName}' 未配置有效 upstream 远端分支。",
                    };
                }

                if (branchPreflight.RemoteProbe is { QuerySucceeded: false })
                {
                    return new Models.WorktreeDeleteOutcome
                    {
                        MainRepoPath = executionRoot,
                        TargetWorktreePath = preflight.TargetWorktreePath,
                        BranchName = preflight.BranchName,
                        RemoteName = branchPreflight.RemoteName,
                        RemoteBranchName = branchPreflight.RemoteBranchName,
                        BlockedByProtection = true,
                        Summary = $"关联远端状态查询失败：{branchPreflight.RemoteProbe.ErrorSummary}",
                    };
                }

                if (!confirmedRemoteExists.HasValue)
                {
                    var probeNow = await BranchDeletionService.ProbeRemoteBranchAsync(
                        executionRoot,
                        branchPreflight.RemoteName,
                        branchPreflight.RemoteBranchName,
                        outerLog,
                        cancellationToken).ConfigureAwait(false);
                    if (!probeNow.QuerySucceeded)
                    {
                        return new Models.WorktreeDeleteOutcome
                        {
                            MainRepoPath = executionRoot,
                            TargetWorktreePath = preflight.TargetWorktreePath,
                            BranchName = preflight.BranchName,
                            RemoteName = branchPreflight.RemoteName,
                            RemoteBranchName = branchPreflight.RemoteBranchName,
                            BlockedByProtection = true,
                            Summary = $"关联远端状态查询失败：{probeNow.ErrorSummary}",
                        };
                    }

                    confirmedRemoteExists = probeNow.ExistsOnServer;
                    confirmedExpectedSha = probeNow.LiveTipSha;
                }

                if (confirmedRemoteExists.Value && string.IsNullOrEmpty(confirmedExpectedSha))
                {
                    return new Models.WorktreeDeleteOutcome
                    {
                        MainRepoPath = executionRoot,
                        TargetWorktreePath = preflight.TargetWorktreePath,
                        BranchName = preflight.BranchName,
                        RemoteName = branchPreflight.RemoteName,
                        RemoteBranchName = branchPreflight.RemoteBranchName,
                        BlockedByProtection = true,
                        Summary = "缺少远端分支预期提交哈希，已阻止无 lease 约束的远端删除。",
                    };
                }
            }

            var operationId = Models.OperationJournalStore.GenerateOperationId();
            var opTitle = deleteRemote
                ? $"清理工作树 {preflight.WorktreeName} 并删除分支 {preflight.BranchName} 及远端 {branchPreflight.RemoteName}/{branchPreflight.RemoteBranchName}"
                : deleteLocal
                    ? $"清理工作树 {preflight.WorktreeName} 并删除本地分支 {preflight.BranchName}"
                    : $"删除工作树 {preflight.WorktreeName}";

            await using var guard = await Models.RepositoryWriteGuard.TryEnterAsync(
                executionRoot,
                opTitle,
                checkIndexLock: true,
                branchNameToUpdate: deleteLocal ? preflight.BranchName : null,
                cancellationToken: cancellationToken).ConfigureAwait(false);

            if (!guard.Acquired)
            {
                return new Models.WorktreeDeleteOutcome
                {
                    OperationId = operationId,
                    MainRepoPath = executionRoot,
                    TargetWorktreePath = preflight.TargetWorktreePath,
                    BranchName = preflight.BranchName,
                    IsDetached = preflight.IsDetached,
                    BlockedByLock = true,
                    Summary = guard.LockDetection.Summary,
                    DetailMessage = string.Join(Environment.NewLine, guard.LockDetection.BlockingLockFiles),
                };
            }

            if (deleteLocal)
            {
                var policyError = await BranchDeletionService.ValidateDeletionPolicyAsync(executionRoot, preflight.BranchName,
                    deleteRemote, branchPreflight, options.ConfirmedPushUrl, cancellationToken).ConfigureAwait(false);
                if (!string.IsNullOrEmpty(policyError))
                    return new Models.WorktreeDeleteOutcome
                    {
                        MainRepoPath = executionRoot,
                        TargetWorktreePath = preflight.TargetWorktreePath,
                        BranchName = preflight.BranchName,
                        BlockedByProtection = true,
                        Summary = policyError,
                    };
            }

            if (deleteLocal && branchPreflight?.ConfirmationSnapshot != null)
            {
                var verify = await CaptureOperationSnapshot.VerifyAsync(
                    branchPreflight.ConfirmationSnapshot,
                    new Models.OperationSnapshotVerifyOptions
                    {
                        RequireSameRepoIdentity = true,
                        RequireSameHead = true,
                        RequireSameInProgressState = true,
                        RequireCleanWorkingTree = false,
                        RequireSameStagedFingerprint = false,
                        RequireSameWatchedRefs = true,
                        CheckGitLocks = true,
                        CheckIndexLock = true,
                        BranchLockToCheck = preflight.BranchName,
                    },
                    cancellationToken).ConfigureAwait(false);

                if (!verify.IsValid)
                {
                    return new Models.WorktreeDeleteOutcome
                    {
                        OperationId = operationId,
                        MainRepoPath = executionRoot,
                        TargetWorktreePath = preflight.TargetWorktreePath,
                        BranchName = preflight.BranchName,
                        IsDetached = preflight.IsDetached,
                        BlockedBySnapshot = true,
                        BlockedByLock = verify.LockDetection.IsLocked,
                        Summary = "关联分支状态在确认后已发生变化，已停止删除，请重新检查。",
                        DetailMessage = verify.Summary + (verify.Details.Count > 0 ? Environment.NewLine + string.Join(Environment.NewLine, verify.Details) : string.Empty),
                    };
                }
            }

            var capturingLog = new CapturingCommandLog(outerLog);
            var journalRecord = new Models.GitOperationRecord
            {
                OperationId = operationId,
                RepositoryCommonDir = guard.Lease.CanonicalCommonDir,
                WorktreePath = executionRoot,
                Kind = Models.GitOperationKind.RemoveWorktree,
                Title = opTitle,
                BranchName = preflight.BranchName,
                RemoteName = deleteRemote ? branchPreflight.RemoteName : string.Empty,
                RemoteBranchName = deleteRemote ? branchPreflight.RemoteBranchName : string.Empty,
                OriginalTipSha = preflight.HeadSha,
                ExpectedRemoteTipSha = deleteRemote ? confirmedExpectedSha : string.Empty,
                Stage = Models.GitOperationStage.Executing,
                StepSummary = $"正在执行：{opTitle}",
            };
            BranchDeletionService.TrySaveJournal(journalStore, journalRecord);

            // Step 1: Validate & remove worktree safely
            var wtSteps = new List<Models.WorktreeDeleteStepResult>();
            var wtRes = await RemoveWorktreeInLockAsync(
                executionRoot,
                preflight.TargetWorktreePath,
                preflight,
                options.ForceRemoveWorktree,
                allowDetachedBackup: !deleteLocal,
                options.BeforeWorktreeRemoveAsync,
                operationId,
                journalRecord,
                capturingLog,
                wtSteps,
                cancellationToken).ConfigureAwait(false);

            if (!wtRes.Succeeded)
            {
                BranchDeletionService.FinalizeJournalFailure(journalStore, journalRecord, capturingLog, wtRes.Summary, wtRes.DetailMessage);

                return new Models.WorktreeDeleteOutcome
                {
                    OperationId = operationId,
                    MainRepoPath = executionRoot,
                    TargetWorktreePath = preflight.TargetWorktreePath,
                    BranchName = preflight.BranchName,
                    IsDetached = wtRes.IsDetached,
                    Succeeded = false,
                    BlockedByProtection = wtRes.BlockedByProtection,
                    BlockedByLock = wtRes.BlockedByLock,
                    BlockedByNestedRepoOrSubmodule = wtRes.BlockedByNestedRepoOrSubmodule,
                    BlockedByMissingDirectory = wtRes.BlockedByMissingDirectory,
                    BlockedBySnapshot = wtRes.BlockedBySnapshot,
                    RequiresForceWorktreeConfirmation = wtRes.RequiresForceWorktreeConfirmation,
                    PreflightAfterFailure = wtRes.LivePreflight,
                    Summary = wtRes.Summary,
                    DetailMessage = wtRes.DetailMessage,
                    StepResults = wtSteps,
                    OperationRecord = journalRecord,
                };
            }

            if (!deleteLocal)
            {
                var summary = string.IsNullOrEmpty(wtRes.DetachedBackupRefName)
                    ? $"已安全移除工作树 '{preflight.WorktreeName}'（保留本地与远端分支）。"
                    : $"已安全移除 detached HEAD 工作树 '{preflight.WorktreeName}'（已创建提交恢复引用 {wtRes.DetachedBackupRefName}，未提交文件不包含在备份中）。";
                BranchDeletionService.FinalizeJournalSuccess(journalStore, journalRecord, capturingLog, summary);

                return new Models.WorktreeDeleteOutcome
                {
                    OperationId = operationId,
                    MainRepoPath = executionRoot,
                    TargetWorktreePath = preflight.TargetWorktreePath,
                    BranchName = preflight.BranchName,
                    IsDetached = wtRes.IsDetached,
                    Succeeded = true,
                    WorktreeRemoved = true,
                    DetachedBackupRefName = wtRes.DetachedBackupRefName,
                    DetachedBackupSha = wtRes.DetachedBackupSha,
                    Summary = summary,
                    StepResults = wtSteps,
                    OperationRecord = journalRecord,
                };
            }

            // Steps 2 + 3: Execute shared local branch & linked remote branch deletion inside the same lock
            var liveBranchSha = await BranchDeletionService.ResolveRefShaAsync(
                executionRoot,
                "refs/heads/" + preflight.BranchName,
                cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrEmpty(liveBranchSha))
            {
                var partialMissingMsg = $"工作树 '{preflight.WorktreeName}' 已移除，但本地分支 '{preflight.BranchName}' 已不存在，未继续远端操作。";
                BranchDeletionService.FinalizeJournalPartialSuccess(journalStore, journalRecord, capturingLog, partialMissingMsg, partialMissingMsg);
                wtSteps.Add(new Models.WorktreeDeleteStepResult
                {
                    Step = Models.WorktreeDeleteStepKind.LocalBranchDelete,
                    Succeeded = false,
                    Label = $"本地分支 '{preflight.BranchName}' 未删除",
                    Detail = "分支已不存在或被外部移除。",
                });

                return new Models.WorktreeDeleteOutcome
                {
                    OperationId = operationId,
                    MainRepoPath = executionRoot,
                    TargetWorktreePath = preflight.TargetWorktreePath,
                    BranchName = preflight.BranchName,
                    IsDetached = wtRes.IsDetached,
                    Succeeded = false,
                    IsPartialSuccess = true,
                    WorktreeRemoved = true,
                    BlockedBySnapshot = true,
                    Summary = partialMissingMsg,
                    StepResults = wtSteps,
                    OperationRecord = journalRecord,
                };
            }

            var branchSteps = wtSteps.Select(ConvertStepToBranchStep).ToList();
            journalRecord.OriginalTipSha = liveBranchSha;
            var branchOutcome = await BranchDeletionService.ExecuteLocalBranchAndLinkedRemoteInLockAsync(
                executionRoot,
                preflight.BranchName,
                liveBranchSha,
                branchPreflight,
                new Models.BranchDeleteOptions
                {
                    Preflight = branchPreflight,
                    ForceDeleteLocal = options.ForceDeleteLocalBranch,
                    DeleteLinkedUpstreamRemote = deleteRemote,
                    PruneStaleTrackingCacheWhenRemoteAbsent = options.PruneStaleTrackingCacheWhenRemoteAbsent,
                    ConfirmedExpectedRemoteTipSha = confirmedExpectedSha,
                    ConfirmedPushUrl = options.ConfirmedPushUrl ?? branchPreflight.RemoteProbe?.PushUrl,
                    ConfirmedRemoteExistsOnServer = confirmedRemoteExists,
                },
                confirmedRemoteExists,
                confirmedExpectedSha,
                deleteRemote,
                operationId,
                journalStore,
                journalRecord,
                branchSteps,
                capturingLog,
                worktreeRemoved: true,
                cancellationToken).ConfigureAwait(false);

            var allSteps = branchSteps.Select(ConvertStepFromBranchStep).ToList();
            return new Models.WorktreeDeleteOutcome
            {
                OperationId = branchOutcome.OperationId,
                MainRepoPath = executionRoot,
                TargetWorktreePath = preflight.TargetWorktreePath,
                BranchName = preflight.BranchName,
                IsDetached = wtRes.IsDetached,
                Succeeded = branchOutcome.Succeeded,
                IsPartialSuccess = branchOutcome.IsPartialSuccess || (!branchOutcome.Succeeded && branchOutcome.WorktreeRemoved),
                BlockedByLock = branchOutcome.BlockedByLock,
                BlockedBySnapshot = branchOutcome.BlockedBySnapshot,
                BlockedByProtection = branchOutcome.BlockedByProtection,
                RequiresForceLocalBranchConfirmation = branchOutcome.RequiresForceLocalConfirmation,
                RequiresRemoteCachePruneConfirmation = branchOutcome.RequiresRemoteCachePruneConfirmation,
                RejectedByRemoteLease = branchOutcome.RejectedByRemoteLease,
                CollaboratorCommitsPreserved = branchOutcome.CollaboratorCommitsPreserved,
                WorktreeRemoved = true,
                LocalBranchDeleted = branchOutcome.LocalBranchDeleted,
                RemoteBranchDeleted = branchOutcome.RemoteBranchDeleted,
                LocalTrackingCachePruned = branchOutcome.LocalTrackingCachePruned,
                DetachedBackupRefName = wtRes.DetachedBackupRefName,
                DetachedBackupSha = wtRes.DetachedBackupSha,
                LocalBranchBackupRefName = branchOutcome.BackupRefName,
                LocalBranchBackupSha = branchOutcome.LocalBackupSha,
                RemoteName = branchOutcome.RemoteName,
                RemoteBranchName = branchOutcome.RemoteBranchName,
                ExpectedRemoteTipSha = branchOutcome.ExpectedRemoteTipSha,
                UnmergedCommitCount = branchOutcome.UnmergedCommitCount,
                UnmergedChangedFilesCount = branchOutcome.UnmergedChangedFilesCount,
                UnmergedCommits = branchOutcome.UnmergedCommits,
                Summary = branchOutcome.Succeeded
                    ? (deleteRemote
                        ? $"已完成清理：工作树 '{preflight.WorktreeName}'、本地分支 '{preflight.BranchName}' 与远端分支 '{branchOutcome.RemoteName}/{branchOutcome.RemoteBranchName}' 均已删除。"
                        : $"已完成清理：工作树 '{preflight.WorktreeName}' 与本地分支 '{preflight.BranchName}' 均已删除。")
                    : (branchOutcome.LocalBranchDeleted
                        ? branchOutcome.Summary
                        : $"工作树已移除，本地分支未删除：{branchOutcome.Summary}"),
                DetailMessage = branchOutcome.DetailMessage,
                StepResults = allSteps,
                OperationRecord = branchOutcome.OperationRecord,
            };
        }

        internal static async Task<(
            bool Succeeded,
            bool IsDetached,
            bool BlockedByProtection,
            bool BlockedByLock,
            bool BlockedByNestedRepoOrSubmodule,
            bool BlockedByMissingDirectory,
            bool BlockedBySnapshot,
            bool RequiresForceWorktreeConfirmation,
            string DetachedBackupRefName,
            string DetachedBackupSha,
            Models.WorktreeInspectPreflight LivePreflight,
            string Summary,
            string DetailMessage)> RemoveWorktreeInLockAsync(
            string mainRepoPath,
            string targetWorktreePath,
            Models.WorktreeInspectPreflight inspectedPreflight,
            bool forceRemoveWorktree,
            bool allowDetachedBackup,
            Func<string, string, CancellationToken, Task> beforeWorktreeRemoveAsync,
            string operationId,
            Models.GitOperationRecord journalRecord,
            CapturingCommandLog capturingLog,
            List<Models.WorktreeDeleteStepResult> steps,
            CancellationToken cancellationToken)
        {
            var live = await InspectWorktreeCoreAsync(mainRepoPath, targetWorktreePath, cancellationToken)
                .ConfigureAwait(false);
            var executionRoot = string.IsNullOrWhiteSpace(live.MainRepoPath) ? mainRepoPath : live.MainRepoPath;

            if (live.IsMainWorktree || live.IsBare)
            {
                var msg = live.IsBare ? "不能删除裸仓库主体。" : "主工作树不允许删除。";
                steps.Add(FailedStep(Models.WorktreeDeleteStepKind.WorktreeRemove, "工作树移除已阻止", msg));
                return (false, live.IsDetached, true, false, false, false, false, false, string.Empty, string.Empty, live, msg, msg);
            }

            if (!live.ExistsInRegistry)
            {
                var msg = $"目录 '{live.TargetWorktreePath}' 已不在当前仓库的工作树注册记录中。";
                steps.Add(FailedStep(Models.WorktreeDeleteStepKind.WorktreeRemove, "工作树不在注册列表中", msg));
                return (false, live.IsDetached, true, false, false, false, false, false, string.Empty, string.Empty, live, msg, msg);
            }

            if (live.IsDirectoryMissing)
            {
                var msg = $"工作树目录 '{live.TargetWorktreePath}' 不存在或磁盘离线；请改用“清理失效工作树记录”先预览后处理。";
                steps.Add(FailedStep(Models.WorktreeDeleteStepKind.WorktreeRemove, "目录缺失或离线", msg));
                return (false, live.IsDetached, false, false, false, true, false, false, string.Empty, string.Empty, live, msg, msg);
            }

            if (!live.IdentityValid)
            {
                var msg = $"目标目录 '{live.TargetWorktreePath}' 的 Git 仓库归属校验未通过，已阻止删除。";
                steps.Add(FailedStep(Models.WorktreeDeleteStepKind.WorktreeRemove, "工作树归属校验失败", msg));
                return (false, live.IsDetached, true, false, false, false, false, false, string.Empty, string.Empty, live, msg, msg);
            }

            if (live.IsLocked)
            {
                var reasonText = string.IsNullOrWhiteSpace(live.LockReason) ? "未填写原因" : live.LockReason;
                var msg = $"工作树已锁定（原因：{reasonText}），不能直接删除或自动绕过锁定；请先确认目录不再需要并手动解锁后再重新检查。";
                steps.Add(FailedStep(Models.WorktreeDeleteStepKind.WorktreeRemove, "工作树已锁定", msg));
                return (false, live.IsDetached, false, true, false, false, false, false, string.Empty, string.Empty, live, msg, msg);
            }

            var targetLockCheck = await Models.RepositoryWriteGuard.InspectLocksAsync(
                live.TargetWorktreePath,
                checkIndexLock: true,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            if (targetLockCheck.IsLocked)
            {
                var msg = targetLockCheck.Summary;
                var detail = string.Join(Environment.NewLine, targetLockCheck.BlockingLockFiles);
                steps.Add(FailedStep(Models.WorktreeDeleteStepKind.WorktreeRemove, "目标工作树存在 Git 操作锁", detail));
                return (false, live.IsDetached, false, true, false, false, false, false, string.Empty, string.Empty, live, msg, detail);
            }

            if (live.HasNestedRepoOrSubmodule)
            {
                var nestedList = string.Join(", ", live.NestedRepoOrSubmodulePaths.Take(8));
                var msg = $"目标工作树内包含嵌套 Git 仓库或子模块（{nestedList}），首版阻止强制或直接删除，请先在外部处理。";
                steps.Add(FailedStep(Models.WorktreeDeleteStepKind.WorktreeRemove, "包含嵌套仓库或子模块", msg));
                return (false, live.IsDetached, true, false, true, false, false, false, string.Empty, string.Empty, live, msg, msg);
            }

            if (inspectedPreflight != null &&
                !string.IsNullOrEmpty(inspectedPreflight.StateDigest) &&
                !string.Equals(inspectedPreflight.StateDigest, live.StateDigest, StringComparison.Ordinal))
            {
                var msg = "工作树内容、提交或忽略文件在确认后发生变化，原确认已失效，请重新检查确认。";
                steps.Add(FailedStep(Models.WorktreeDeleteStepKind.WorktreeRemove, "确认状态已过期", msg));
                return (false, live.IsDetached, false, false, false, false, true, false, string.Empty, string.Empty, live, msg, msg);
            }

            if (live.RequiresForceToDeleteFiles && !forceRemoveWorktree)
            {
                var msg = $"工作树包含未保留安全内容（暂存 {live.StagedFiles.Count}、未暂存 {live.UnstagedFiles.Count}、冲突 {live.ConflictedFiles.Count}、未跟踪 {live.UntrackedFiles.Count}、忽略 {live.IgnoredFiles.Count}），默认已停止；请先暂存/移走文件或显式启用强制删除。";
                steps.Add(FailedStep(Models.WorktreeDeleteStepKind.WorktreeRemove, "检测到未保存或被忽略的文件", msg));
                return (false, live.IsDetached, false, false, false, false, false, true, string.Empty, string.Empty, live, msg, msg);
            }

            if (beforeWorktreeRemoveAsync != null)
            {
                await beforeWorktreeRemoveAsync(executionRoot, live.TargetWorktreePath, cancellationToken).ConfigureAwait(false);
                steps.Add(new Models.WorktreeDeleteStepResult
                {
                    Step = Models.WorktreeDeleteStepKind.CloseOpenViews,
                    Succeeded = true,
                    Label = "已切换出并关闭关联工作树视图",
                });
            }

            var detachedBackupRef = string.Empty;
            var detachedBackupSha = string.Empty;
            if (allowDetachedBackup && live.RequiresDetachedBackup)
            {
                var backupRes = await BackupRefs.CreateAsync(executionRoot, operationId, live.HeadSha, capturingLog, cancellationToken)
                    .ConfigureAwait(false);
                if (!backupRes.Succeeded)
                {
                    steps.Add(FailedStep(Models.WorktreeDeleteStepKind.DetachedBackupCreate, "创建 detached HEAD 备份引用失败", backupRes.Summary));
                    return (false, true, false, false, false, false, false, false, string.Empty, string.Empty, live,
                        $"未能为 detached HEAD 提交创建本地备份引用，已停止删除：{backupRes.Summary}", backupRes.Summary);
                }

                detachedBackupRef = backupRes.BackupRefName;
                detachedBackupSha = live.HeadSha;
                if (journalRecord != null)
                {
                    journalRecord.BackupRefName = detachedBackupRef;
                    journalRecord.OriginalTipSha = live.HeadSha;
                }

                steps.Add(new Models.WorktreeDeleteStepResult
                {
                    Step = Models.WorktreeDeleteStepKind.DetachedBackupCreate,
                    Succeeded = true,
                    Label = $"已保存 detached HEAD 恢复引用：{detachedBackupRef}（指向 {BranchDeletionService.ShortSha(live.HeadSha)}；不包含未提交文件）",
                });
            }

            var canonicalTarget = Path.GetFullPath(live.TargetWorktreePath);
            if (canonicalTarget.StartsWith('-'))
            {
                var msg = "目标工作树路径格式无效，已阻止传递给 Git 命令。";
                steps.Add(FailedStep(Models.WorktreeDeleteStepKind.WorktreeRemove, "无效工作树路径", msg));
                return (false, live.IsDetached, true, false, false, false, false, false, detachedBackupRef, detachedBackupSha, live, msg, msg);
            }

            // Never use targetWorktreePath as CWD when deleting it; always execute from mainRepoPath.
            var args = forceRemoveWorktree
                ? new[] { "worktree", "remove", "--force", canonicalTarget }
                : new[] { "worktree", "remove", canonicalTarget };
            var removeRes = await Command.RunGitDirectAsync(executionRoot, cancellationToken, args).ConfigureAwait(false);
            capturingLog.AppendLine($"$ git {string.Join(' ', args)}");
            BranchDeletionService.AppendRedactedCommandOutput(capturingLog, removeRes);

            if (!removeRes.IsSuccess)
            {
                if (!string.IsNullOrEmpty(detachedBackupRef))
                {
                    await BackupRefs.DeleteBackupRefAsync(executionRoot, detachedBackupRef, detachedBackupSha, capturingLog, cancellationToken)
                        .ConfigureAwait(false);
                    if (journalRecord != null)
                        journalRecord.BackupRefName = string.Empty;
                }

                var err = Models.GitConfigSnapshot.RedactText(BranchDeletionService.FirstNonEmpty(removeRes.StdErr, removeRes.StdOut, "移除工作树失败。"));
                steps.Add(FailedStep(Models.WorktreeDeleteStepKind.WorktreeRemove, $"移除工作树失败：{live.TargetWorktreePath}", err));
                return (false, live.IsDetached, false, false, false, false, false, false, string.Empty, string.Empty, live, err, err);
            }

            steps.Add(new Models.WorktreeDeleteStepResult
            {
                Step = Models.WorktreeDeleteStepKind.WorktreeRemove,
                Succeeded = true,
                Label = $"已移除工作树：{live.TargetWorktreePath}",
            });

            return (true, live.IsDetached, false, false, false, false, false, false, detachedBackupRef, detachedBackupSha, live,
                $"已移除工作树 '{live.WorktreeName}'。", string.Empty);
        }

        public static async Task<Models.WorktreeUnlockResult> UnlockWorktreeAsync(
            string repoPath,
            string targetWorktreePath,
            Models.ICommandLog log = null,
            CancellationToken cancellationToken = default)
        {
            var inspectedBefore = await InspectWorktreeCoreAsync(repoPath, targetWorktreePath, cancellationToken)
                .ConfigureAwait(false);
            var executionRoot = string.IsNullOrWhiteSpace(inspectedBefore.MainRepoPath) ? repoPath : inspectedBefore.MainRepoPath;

            if (inspectedBefore.IsMainWorktree || !inspectedBefore.ExistsInRegistry)
            {
                return new Models.WorktreeUnlockResult
                {
                    Succeeded = false,
                    Summary = "仅支持对已注册的关联工作树执行解锁。",
                    Preflight = inspectedBefore,
                };
            }

            await using var guard = await Models.RepositoryWriteGuard.TryEnterAsync(
                executionRoot,
                $"解锁工作树 {inspectedBefore.WorktreeName}",
                checkIndexLock: true,
                cancellationToken: cancellationToken).ConfigureAwait(false);

            if (!guard.Acquired)
            {
                return new Models.WorktreeUnlockResult
                {
                    Succeeded = false,
                    Summary = guard.LockDetection.Summary,
                    Preflight = inspectedBefore,
                };
            }

            var canonicalTarget = Path.GetFullPath(inspectedBefore.TargetWorktreePath);
            var res = await Command.RunGitDirectAsync(executionRoot, cancellationToken, "worktree", "unlock", canonicalTarget)
                .ConfigureAwait(false);
            log?.AppendLine($"$ git worktree unlock {canonicalTarget}");
            BranchDeletionService.AppendRedactedCommandOutput(log, res);

            var refreshed = await InspectWorktreeAsync(executionRoot, targetWorktreePath, probeUpstreamRemote: false, includeAttachedBranchPreflight: true, cancellationToken)
                .ConfigureAwait(false);

            if (!res.IsSuccess)
            {
                var err = Models.GitConfigSnapshot.RedactText(BranchDeletionService.FirstNonEmpty(res.StdErr, res.StdOut, "解锁工作树失败。"));
                return new Models.WorktreeUnlockResult
                {
                    Succeeded = false,
                    Summary = err,
                    Preflight = refreshed,
                };
            }

            return new Models.WorktreeUnlockResult
            {
                Succeeded = true,
                Summary = $"已解除工作树 '{refreshed.WorktreeName}' 的锁定并重新完成状态检查。",
                Preflight = refreshed,
            };
        }

        public static async Task<Models.WorktreePrunePreview> PreviewPruneWorktreesAsync(
            string repoPath,
            CancellationToken cancellationToken = default)
        {
            var (_, commonDir) = await Models.RepositoryWriteGuard.ResolveGitDirectoriesAsync(repoPath, cancellationToken)
                .ConfigureAwait(false);
            var worktrees = await new Worktree(repoPath).ReadAllAsync().ConfigureAwait(false);
            var mainRepoPath = worktrees.Count > 0 ? worktrees[0].FullPath : repoPath;

            var dryRun = await Command.RunGitDirectAsync(mainRepoPath, cancellationToken, "worktree", "prune", "--dry-run", "-v")
                .ConfigureAwait(false);
            var rawOut = Models.GitConfigSnapshot.RedactText(BranchDeletionService.FirstNonEmpty(dryRun.StdOut, dryRun.StdErr, string.Empty));
            var candidates = ParseDryRunCandidates(commonDir, dryRun.StdOut, dryRun.StdErr);

            var retained = new List<Models.WorktreeLockedMissingItem>();
            foreach (var wt in worktrees.Skip(1))
            {
                var missing = !Directory.Exists(wt.FullPath);
                if (!missing)
                    continue;

                var isCandidate = candidates.Any(c =>
                    !string.IsNullOrEmpty(c.WorktreePath) &&
                    Models.WorktreeTaskStore.PathsEqual(c.WorktreePath, wt.FullPath));
                if (!isCandidate)
                {
                    retained.Add(new Models.WorktreeLockedMissingItem
                    {
                        WorktreePath = wt.FullPath,
                        LockReason = wt.IsLocked ? (string.IsNullOrWhiteSpace(wt.LockReason) ? "已锁定（未填写原因）" : wt.LockReason) : string.Empty,
                        PrunableReason = wt.PrunableReason ?? string.Empty,
                    });
                }
            }

            var note = candidates.Count > 0
                ? $"以下 {candidates.Count} 条失效工作树元数据将被清理（仅移除 $GIT_COMMON_DIR/worktrees 下的过期注册记录，符合 Git 锁定与过期规则，不误删处于锁定状态或暂时离线的记录）。"
                : "当前没有符合 Git 清理条件的失效工作树记录；处于锁定状态或尚未过期的离线目录不会被自动清理。";

            return new Models.WorktreePrunePreview
            {
                MainRepoPath = mainRepoPath,
                CanonicalCommonDir = commonDir,
                Candidates = candidates,
                LockedRetainedItems = retained,
                RawDryRunOutput = rawOut,
                ConfirmationNote = note,
            };
        }

        public static async Task<Models.WorktreePruneResult> PruneWorktreesAsync(
            string repoPath,
            Models.OperationJournalStore journalStore = null,
            Models.ICommandLog outerLog = null,
            CancellationToken cancellationToken = default)
        {
            var previewBefore = await PreviewPruneWorktreesAsync(repoPath, cancellationToken).ConfigureAwait(false);
            var executionRoot = string.IsNullOrWhiteSpace(previewBefore.MainRepoPath) ? repoPath : previewBefore.MainRepoPath;

            await using var guard = await Models.RepositoryWriteGuard.TryEnterAsync(
                executionRoot,
                "清理失效工作树记录",
                checkIndexLock: true,
                cancellationToken: cancellationToken).ConfigureAwait(false);

            if (!guard.Acquired)
            {
                return new Models.WorktreePruneResult
                {
                    Succeeded = false,
                    Summary = guard.LockDetection.Summary,
                    DetailMessage = string.Join(Environment.NewLine, guard.LockDetection.BlockingLockFiles),
                };
            }

            var liveDryRun = await PreviewPruneWorktreesAsync(executionRoot, cancellationToken).ConfigureAwait(false);
            var capturingLog = new CapturingCommandLog(outerLog);
            var res = await Command.RunGitDirectAsync(executionRoot, cancellationToken, "worktree", "prune", "-v")
                .ConfigureAwait(false);
            capturingLog.AppendLine("$ git worktree prune -v");
            BranchDeletionService.AppendRedactedCommandOutput(capturingLog, res);

            if (!res.IsSuccess)
            {
                var err = Models.GitConfigSnapshot.RedactText(BranchDeletionService.FirstNonEmpty(res.StdErr, res.StdOut, "清理失效工作树记录失败。"));
                return new Models.WorktreePruneResult
                {
                    Succeeded = false,
                    Summary = err,
                    DetailMessage = err,
                };
            }

            var actualPruned = ParseDryRunCandidates(liveDryRun.CanonicalCommonDir, res.StdOut, res.StdErr);
            var removedList = actualPruned.Count > 0 ? actualPruned : liveDryRun.Candidates.ToList();
            var summary = removedList.Count > 0
                ? $"已清理 {removedList.Count} 条失效工作树管理记录。"
                : "未发现需要清理的过期工作树记录。";

            if (journalStore != null)
            {
                var rec = new Models.GitOperationRecord
                {
                    OperationId = Models.OperationJournalStore.GenerateOperationId(),
                    RepositoryCommonDir = guard.Lease.CanonicalCommonDir,
                    WorktreePath = executionRoot,
                    Kind = Models.GitOperationKind.RemoveWorktree,
                    Title = "清理失效工作树记录",
                    Stage = Models.GitOperationStage.Succeeded,
                    StepSummary = summary,
                    CommandSummary = capturingLog.GetCombinedText(),
                    CompletedAtUtc = DateTime.UtcNow,
                };
                BranchDeletionService.TrySaveJournal(journalStore, rec);
            }

            return new Models.WorktreePruneResult
            {
                Succeeded = true,
                RemovedCount = removedList.Count,
                RemovedCandidates = removedList,
                Summary = summary,
                DetailMessage = capturingLog.GetCombinedText(),
            };
        }

        private static async Task<Models.WorktreeInspectPreflight> InspectWorktreeCoreAsync(
            string repoPath,
            string targetWorktreePath,
            CancellationToken cancellationToken)
        {
            var (_, commonDir) = await Models.RepositoryWriteGuard.ResolveGitDirectoriesAsync(repoPath, cancellationToken)
                .ConfigureAwait(false);
            var worktrees = await new Worktree(repoPath).ReadAllAsync().ConfigureAwait(false);

            var mainWorktree = worktrees.FirstOrDefault();
            var mainRepoPath = mainWorktree?.FullPath ?? repoPath;
            var normalizedTarget = BranchDeletionService.NormalizeDirectoryPath(targetWorktreePath);
            var matchedIndex = -1;
            Models.Worktree matched = null;
            for (var i = 0; i < worktrees.Count; i++)
            {
                if (Models.WorktreeTaskStore.PathsEqual(worktrees[i].FullPath, targetWorktreePath))
                {
                    matchedIndex = i;
                    matched = worktrees[i];
                    normalizedTarget = BranchDeletionService.NormalizeDirectoryPath(matched.FullPath);
                    break;
                }
            }

            var existsInRegistry = matched != null;
            var isMainWorktree = matchedIndex == 0 ||
                (mainWorktree != null && Models.WorktreeTaskStore.PathsEqual(mainWorktree.FullPath, targetWorktreePath));
            var isBare = matched?.IsBare ?? (mainWorktree?.IsBare == true && isMainWorktree);
            var isLocked = matched?.IsLocked ?? false;
            var lockReason = matched?.LockReason ?? string.Empty;
            var prunableReason = matched?.PrunableReason ?? string.Empty;
            var wtName = Path.GetFileName(normalizedTarget);

            var isMissing = !Directory.Exists(normalizedTarget);
            if (isMissing)
            {
                var blockMsg = isMainWorktree
                    ? "主工作树不允许删除。"
                    : !existsInRegistry
                        ? "目录已不在工作树注册列表中。"
                        : "工作树目录不存在或磁盘离线，请使用“清理失效工作树记录”预览后处理。";

                return new Models.WorktreeInspectPreflight
                {
                    RepoPath = repoPath,
                    MainRepoPath = mainRepoPath,
                    CanonicalCommonDir = commonDir,
                    TargetWorktreePath = normalizedTarget,
                    WorktreeName = wtName,
                    ExistsInRegistry = existsInRegistry,
                    IsMainWorktree = isMainWorktree,
                    IsBare = isBare,
                    IdentityValid = false,
                    IsDirectoryMissing = true,
                    IsLocked = isLocked,
                    LockReason = lockReason,
                    PrunableReason = prunableReason,
                    IsDetached = matched?.IsDetached ?? false,
                    BranchName = TrimHeadsPrefix(matched?.Branch),
                    FullBranchRef = matched?.Branch ?? string.Empty,
                    HeadSha = matched?.Head ?? string.Empty,
                    StateDigest = BuildStateDigest(existsInRegistry, isMainWorktree, true, false, isLocked, lockReason, matched?.Head ?? string.Empty, matched?.IsDetached ?? false, string.Empty, [], [], [], [], [], []),
                    DirectBlockReason = blockMsg,
                };
            }

            var cmdRoot = !string.IsNullOrWhiteSpace(mainRepoPath) && Directory.Exists(mainRepoPath)
                ? mainRepoPath
                : Path.GetDirectoryName(commonDir) ?? normalizedTarget;

            var identityValid = false;
            var targetGitDir = string.Empty;
            DateTime? lastFetchAt = null;
            var identityRes = await Command.RunGitDirectAsync(
                cmdRoot,
                cancellationToken,
                "-C",
                normalizedTarget,
                "rev-parse",
                "--path-format=absolute",
                "--show-toplevel",
                "--git-common-dir",
                "--git-dir",
                "--git-path",
                "FETCH_HEAD").ConfigureAwait(false);

            if (identityRes.IsSuccess)
            {
                var lines = identityRes.StdOut.TrimEnd('\r', '\n').Split('\n');
                if (lines.Length >= 4)
                {
                    var topLevel = lines[0].Trim();
                    var reportedCommonDir = lines[1].Trim();
                    targetGitDir = lines[2].Trim();
                    var fetchHeadPath = lines[3].Trim();
                    identityValid =
                        Models.WorktreeTaskStore.PathsEqual(topLevel, normalizedTarget) &&
                        Models.WorktreeTaskStore.PathsEqual(reportedCommonDir, commonDir);
                    if (File.Exists(fetchHeadPath))
                        lastFetchAt = File.GetLastWriteTime(fetchHeadPath);
                }
            }

            var staged = new List<string>();
            var unstaged = new List<string>();
            var conflicted = new List<string>();
            var untracked = new List<string>();
            var ignored = new List<string>();
            var nestedSet = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

            var headSha = matched?.Head ?? string.Empty;
            var branchName = TrimHeadsPrefix(matched?.Branch);
            var fullBranchRef = matched?.Branch ?? string.Empty;
            var isDetached = matched?.IsDetached ?? false;
            var syncState = Models.WorktreeSyncState.Unknown;
            var upstream = string.Empty;
            int? ahead = null;
            int? behind = null;

            if (identityValid)
            {
                var statusRes = await Command.RunGitDirectAsync(
                    cmdRoot,
                    cancellationToken,
                    "-C",
                    normalizedTarget,
                    "--no-optional-locks",
                    "status",
                    "--porcelain=v2",
                    "--branch",
                    "--ahead-behind",
                    "-z",
                    "--untracked-files=all",
                    "--ignored=matching",
                    "--ignore-submodules=none").ConfigureAwait(false);

                if (statusRes.IsSuccess)
                {
                    var parsed = ParseDetailedStatus(statusRes.StdOut, lastFetchAt);
                    headSha = parsed.HeadSha;
                    isDetached = parsed.IsDetached;
                    branchName = parsed.BranchName;
                    fullBranchRef = string.IsNullOrEmpty(branchName) || isDetached ? string.Empty : "refs/heads/" + branchName;
                    syncState = parsed.SyncState;
                    upstream = parsed.Upstream;
                    ahead = parsed.Ahead;
                    behind = parsed.Behind;
                    staged.AddRange(parsed.Staged);
                    unstaged.AddRange(parsed.Unstaged);
                    conflicted.AddRange(parsed.Conflicted);
                    untracked.AddRange(parsed.Untracked);
                    ignored.AddRange(parsed.Ignored);
                    foreach (var n in parsed.NestedSubmodules)
                        nestedSet.Add(n);
                }

                // Also catch clean gitlink submodules in index (mode 160000)
                var stageRes = await Command.RunGitDirectAsync(
                    cmdRoot,
                    cancellationToken,
                    "-C",
                    normalizedTarget,
                    "ls-files",
                    "--stage",
                    "-z").ConfigureAwait(false);
                if (stageRes.IsSuccess && !string.IsNullOrEmpty(stageRes.StdOut))
                {
                    foreach (var entry in stageRes.StdOut.Split('\0', StringSplitOptions.RemoveEmptyEntries))
                    {
                        if (entry.StartsWith("160000 ", StringComparison.Ordinal))
                        {
                            var tabIdx = entry.IndexOf('\t');
                            if (tabIdx > 0 && tabIdx + 1 < entry.Length)
                                nestedSet.Add(entry[(tabIdx + 1)..]);
                        }
                    }
                }

                // Also scan directory tree safely for nested untracked/ignored .git repositories
                ScanForNestedGitRepos(normalizedTarget, nestedSet);
            }

            var nestedList = nestedSet.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
            var blockReason = string.Empty;
            if (isMainWorktree || isBare)
                blockReason = isBare ? "裸仓库主体不支持工作树删除。" : "主工作树不提供删除操作。";
            else if (!existsInRegistry)
                blockReason = "该目录已不在当前仓库的工作树注册列表中。";
            else if (!identityValid)
                blockReason = "目标目录的 Git 仓库归属校验失败，已阻止删除。";
            else if (isLocked)
                blockReason = $"工作树已锁定（{(string.IsNullOrWhiteSpace(lockReason) ? "未填写原因" : lockReason)}）；请确认不再需要后先手动解锁再重新检查。";
            else if (nestedList.Count > 0)
                blockReason = $"检测到嵌套 Git 仓库或子模块（{string.Join(", ", nestedList.Take(5))}），首版阻止直接删除，请先在外部处理。";
            else if (staged.Count + unstaged.Count + conflicted.Count + untracked.Count + ignored.Count > 0)
                blockReason = $"工作树内含暂存/未暂存/未跟踪或忽略文件（共 {staged.Count + unstaged.Count + conflicted.Count + untracked.Count + ignored.Count} 项），默认已停止；请先处理或显式勾选强制删除。";

            Models.OperationConfirmationSnapshot snapshot = null;
            if (identityValid && !isBare)
            {
                var watchedRefs = string.IsNullOrEmpty(fullBranchRef) ? Array.Empty<string>() : new[] { fullBranchRef };
                snapshot = await CaptureOperationSnapshot.CaptureAsync(
                    normalizedTarget,
                    new Models.OperationSnapshotCaptureOptions { RefsToWatch = watchedRefs },
                    cancellationToken).ConfigureAwait(false);
            }

            var digest = BuildStateDigest(
                existsInRegistry,
                isMainWorktree,
                false,
                identityValid,
                isLocked,
                lockReason,
                headSha,
                isDetached,
                branchName,
                staged,
                unstaged,
                conflicted,
                untracked,
                ignored,
                nestedList);

            return new Models.WorktreeInspectPreflight
            {
                RepoPath = repoPath,
                MainRepoPath = mainRepoPath,
                CanonicalCommonDir = commonDir,
                TargetWorktreePath = normalizedTarget,
                TargetGitDir = targetGitDir,
                WorktreeName = wtName,
                ExistsInRegistry = existsInRegistry,
                IsMainWorktree = isMainWorktree,
                IsBare = isBare,
                IdentityValid = identityValid,
                IsDirectoryMissing = false,
                IsLocked = isLocked,
                LockReason = lockReason,
                PrunableReason = prunableReason,
                IsDetached = isDetached,
                BranchName = branchName,
                FullBranchRef = fullBranchRef,
                HeadSha = headSha,
                SyncState = syncState,
                SyncUpstream = upstream,
                SyncAhead = ahead,
                SyncBehind = behind,
                LastFetchAt = lastFetchAt,
                StagedFiles = staged,
                UnstagedFiles = unstaged,
                ConflictedFiles = conflicted,
                UntrackedFiles = untracked,
                IgnoredFiles = ignored,
                NestedRepoOrSubmodulePaths = nestedList,
                ConfirmationSnapshot = snapshot,
                StateDigest = digest,
                DirectBlockReason = blockReason,
            };
        }

        private static Models.WorktreeInspectPreflight WithAttachedBranchPreflight(
            Models.WorktreeInspectPreflight source,
            Models.BranchDeletePreflight branchPreflight)
        {
            return new Models.WorktreeInspectPreflight
            {
                RepoPath = source.RepoPath,
                MainRepoPath = source.MainRepoPath,
                CanonicalCommonDir = source.CanonicalCommonDir,
                TargetWorktreePath = source.TargetWorktreePath,
                TargetGitDir = source.TargetGitDir,
                WorktreeName = source.WorktreeName,
                ExistsInRegistry = source.ExistsInRegistry,
                IsMainWorktree = source.IsMainWorktree,
                IsBare = source.IsBare,
                IdentityValid = source.IdentityValid,
                IsDirectoryMissing = source.IsDirectoryMissing,
                IsLocked = source.IsLocked,
                LockReason = source.LockReason,
                PrunableReason = source.PrunableReason,
                IsDetached = source.IsDetached,
                BranchName = source.BranchName,
                FullBranchRef = source.FullBranchRef,
                HeadSha = source.HeadSha,
                SyncState = source.SyncState,
                SyncUpstream = source.SyncUpstream,
                SyncAhead = source.SyncAhead,
                SyncBehind = source.SyncBehind,
                LastFetchAt = source.LastFetchAt,
                StagedFiles = source.StagedFiles,
                UnstagedFiles = source.UnstagedFiles,
                ConflictedFiles = source.ConflictedFiles,
                UntrackedFiles = source.UntrackedFiles,
                IgnoredFiles = source.IgnoredFiles,
                NestedRepoOrSubmodulePaths = source.NestedRepoOrSubmodulePaths,
                AttachedBranchPreflight = branchPreflight,
                ConfirmationSnapshot = source.ConfirmationSnapshot,
                StateDigest = source.StateDigest,
                DirectBlockReason = source.DirectBlockReason,
            };
        }

        private static (
            string HeadSha,
            bool IsDetached,
            string BranchName,
            Models.WorktreeSyncState SyncState,
            string Upstream,
            int? Ahead,
            int? Behind,
            List<string> Staged,
            List<string> Unstaged,
            List<string> Conflicted,
            List<string> Untracked,
            List<string> Ignored,
            List<string> NestedSubmodules) ParseDetailedStatus(string output, DateTime? fetchTime)
        {
            var headSha = string.Empty;
            var rawBranch = string.Empty;
            var upstream = string.Empty;
            int? ahead = null;
            int? behind = null;
            var staged = new List<string>();
            var unstaged = new List<string>();
            var conflicted = new List<string>();
            var untracked = new List<string>();
            var ignored = new List<string>();
            var nested = new List<string>();

            var records = output.Split('\0');
            for (var i = 0; i < records.Length; i++)
            {
                var record = records[i];
                if (string.IsNullOrEmpty(record))
                    continue;

                if (record.StartsWith("# branch.oid ", StringComparison.Ordinal))
                    headSha = record[13..];
                else if (record.StartsWith("# branch.head ", StringComparison.Ordinal))
                    rawBranch = record[14..];
                else if (record.StartsWith("# branch.upstream ", StringComparison.Ordinal))
                    upstream = record[18..];
                else if (record.StartsWith("# branch.ab ", StringComparison.Ordinal))
                {
                    var counts = record[12..].Split(' ');
                    if (counts.Length == 2 &&
                        int.TryParse(counts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var a) &&
                        int.TryParse(counts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var b))
                    {
                        ahead = a;
                        behind = -b;
                    }
                }
                else if (record.StartsWith("? ", StringComparison.Ordinal))
                {
                    untracked.Add(record[2..]);
                }
                else if (record.StartsWith("! ", StringComparison.Ordinal))
                {
                    ignored.Add(record[2..]);
                }
                else if (record.StartsWith("1 ", StringComparison.Ordinal))
                {
                    var parts = record.Split(' ', 9);
                    if (parts.Length == 9)
                    {
                        var xy = parts[1];
                        var sub = parts[2];
                        var path = parts[8];
                        if (sub.StartsWith('S'))
                            nested.Add(path);
                        if (xy.Length >= 2)
                        {
                            if (xy[0] != '.')
                                staged.Add(path);
                            if (xy[1] != '.')
                                unstaged.Add(path);
                        }
                    }
                }
                else if (record.StartsWith("2 ", StringComparison.Ordinal))
                {
                    var parts = record.Split(' ', 10);
                    if (parts.Length == 10)
                    {
                        var xy = parts[1];
                        var sub = parts[2];
                        var path = parts[9];
                        if (sub.StartsWith('S'))
                            nested.Add(path);
                        if (xy.Length >= 2)
                        {
                            if (xy[0] != '.')
                                staged.Add(path);
                            if (xy[1] != '.')
                                unstaged.Add(path);
                        }
                    }

                    // Consume NUL-terminated origPath record
                    i++;
                }
                else if (record.StartsWith("u ", StringComparison.Ordinal))
                {
                    var parts = record.Split(' ', 11);
                    if (parts.Length == 11)
                    {
                        var sub = parts[2];
                        var path = parts[10];
                        if (sub.StartsWith('S'))
                            nested.Add(path);
                        conflicted.Add(path);
                    }
                }
            }

            var isUnborn = headSha == "(initial)";
            var isDetached = rawBranch == "(detached)";
            var syncState = isUnborn ? Models.WorktreeSyncState.Unborn :
                isDetached ? Models.WorktreeSyncState.Detached :
                upstream.Length == 0 ? Models.WorktreeSyncState.NoUpstream :
                ahead == null ? Models.WorktreeSyncState.UpstreamGone :
                fetchTime == null ? Models.WorktreeSyncState.NotFetched : Models.WorktreeSyncState.Cached;

            var normalizedHead = isUnborn ? string.Empty : headSha;
            var branchName = (isDetached || rawBranch == "(initial)" || string.IsNullOrEmpty(rawBranch)) ? string.Empty : rawBranch;

            return (
                normalizedHead,
                isDetached,
                branchName,
                syncState,
                upstream,
                syncState == Models.WorktreeSyncState.Cached ? ahead : null,
                syncState == Models.WorktreeSyncState.Cached ? behind : null,
                staged,
                unstaged,
                conflicted,
                untracked,
                ignored,
                nested);
        }

        private static void ScanForNestedGitRepos(string rootWorktreePath, HashSet<string> nestedPaths)
        {
            var stack = new Stack<string>();
            stack.Push(rootWorktreePath);

            while (stack.Count > 0)
            {
                var current = stack.Pop();
                IEnumerable<string> dirs;
                try
                {
                    dirs = Directory.EnumerateDirectories(current);
                }
                catch (IOException)
                {
                    continue;
                }
                catch (UnauthorizedAccessException)
                {
                    continue;
                }

                foreach (var dir in dirs)
                {
                    var name = Path.GetFileName(dir);
                    if (string.Equals(name, ".git", StringComparison.OrdinalIgnoreCase))
                        continue;

                    try
                    {
                        var attrs = File.GetAttributes(dir);
                        if ((attrs & FileAttributes.ReparsePoint) != 0)
                            continue;
                    }
                    catch (IOException)
                    {
                        continue;
                    }
                    catch (UnauthorizedAccessException)
                    {
                        continue;
                    }

                    var nestedGitMarker = Path.Combine(dir, ".git");
                    if (Directory.Exists(nestedGitMarker) || File.Exists(nestedGitMarker))
                    {
                        var rel = Path.GetRelativePath(rootWorktreePath, dir).Replace('\\', '/');
                        nestedPaths.Add(rel);
                        continue;
                    }

                    stack.Push(dir);
                }
            }
        }

        private static List<Models.WorktreePruneCandidate> ParseDryRunCandidates(
            string commonDir,
            string stdout,
            string stderr)
        {
            var candidates = new List<Models.WorktreePruneCandidate>();
            var combined = $"{stdout}\n{stderr}";
            foreach (var rawLine in combined.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
            {
                var line = rawLine.Trim();
                if (!line.StartsWith("Removing ", StringComparison.OrdinalIgnoreCase))
                    continue;

                var afterRemoving = line["Removing ".Length..];
                var colonIdx = afterRemoving.IndexOf(':');
                var adminRel = colonIdx >= 0 ? afterRemoving[..colonIdx].Trim() : afterRemoving.Trim();
                var reason = colonIdx >= 0 && colonIdx + 1 < afterRemoving.Length
                    ? afterRemoving[(colonIdx + 1)..].Trim()
                    : string.Empty;

                var wtName = adminRel.StartsWith("worktrees/", StringComparison.OrdinalIgnoreCase)
                    ? adminRel["worktrees/".Length..]
                    : Path.GetFileName(adminRel);
                var adminDir = !string.IsNullOrEmpty(commonDir)
                    ? Path.GetFullPath(Path.Combine(commonDir, adminRel.Replace('/', Path.DirectorySeparatorChar)))
                    : string.Empty;

                var wtPath = TryResolveAdminGitDirTarget(adminDir);
                candidates.Add(new Models.WorktreePruneCandidate
                {
                    WorktreeName = wtName,
                    WorktreePath = wtPath,
                    AdminDir = adminDir,
                    Reason = reason,
                    SummaryLine = line,
                });
            }

            return candidates;
        }

        private static string TryResolveAdminGitDirTarget(string adminDir)
        {
            if (string.IsNullOrEmpty(adminDir))
                return string.Empty;

            var gitdirFile = Path.Combine(adminDir, "gitdir");
            if (!File.Exists(gitdirFile))
                return string.Empty;

            try
            {
                var content = File.ReadAllText(gitdirFile, Encoding.UTF8).Trim();
                if (content.EndsWith("/.git", StringComparison.OrdinalIgnoreCase) ||
                    content.EndsWith("\\.git", StringComparison.OrdinalIgnoreCase))
                    return BranchDeletionService.NormalizeDirectoryPath(content[..^5]);
                return BranchDeletionService.NormalizeDirectoryPath(content);
            }
            catch (IOException)
            {
                return string.Empty;
            }
            catch (UnauthorizedAccessException)
            {
                return string.Empty;
            }
        }

        private static string BuildStateDigest(
            bool existsInRegistry,
            bool isMainWorktree,
            bool isMissing,
            bool identityValid,
            bool isLocked,
            string lockReason,
            string headSha,
            bool isDetached,
            string branchName,
            IEnumerable<string> staged,
            IEnumerable<string> unstaged,
            IEnumerable<string> conflicted,
            IEnumerable<string> untracked,
            IEnumerable<string> ignored,
            IEnumerable<string> nested)
        {
            var sb = new StringBuilder();
            sb.Append(existsInRegistry).Append('|')
              .Append(isMainWorktree).Append('|')
              .Append(isMissing).Append('|')
              .Append(identityValid).Append('|')
              .Append(isLocked).Append('|')
              .Append(lockReason ?? string.Empty).Append('|')
              .Append(headSha ?? string.Empty).Append('|')
              .Append(isDetached).Append('|')
              .Append(branchName ?? string.Empty);

            AppendSortedList(sb, "S", staged);
            AppendSortedList(sb, "U", unstaged);
            AppendSortedList(sb, "C", conflicted);
            AppendSortedList(sb, "?", untracked);
            AppendSortedList(sb, "!", ignored);
            AppendSortedList(sb, "N", nested);

            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()));
            return Convert.ToHexString(hash);
        }

        private static void AppendSortedList(StringBuilder sb, string tag, IEnumerable<string> items)
        {
            sb.Append('|').Append(tag).Append(':');
            foreach (var item in items.OrderBy(x => x, StringComparer.Ordinal))
                sb.Append(item).Append('\0');
        }

        private static string TrimHeadsPrefix(string fullBranch)
        {
            if (string.IsNullOrEmpty(fullBranch))
                return string.Empty;

            return fullBranch.StartsWith("refs/heads/", StringComparison.Ordinal)
                ? fullBranch["refs/heads/".Length..]
                : fullBranch;
        }

        private static Models.WorktreeDeleteStepResult FailedStep(
            Models.WorktreeDeleteStepKind kind,
            string label,
            string detail)
        {
            return new Models.WorktreeDeleteStepResult
            {
                Step = kind,
                Succeeded = false,
                Label = label,
                Detail = detail,
            };
        }

        private static Models.BranchDeleteStepResult ConvertStepToBranchStep(Models.WorktreeDeleteStepResult s)
        {
            return new Models.BranchDeleteStepResult
            {
                Step = s.Step switch
                {
                    Models.WorktreeDeleteStepKind.DetachedBackupCreate => Models.BranchDeleteStepKind.BackupRefCreate,
                    _ => Models.BranchDeleteStepKind.WorktreeRemove,
                },
                Succeeded = s.Succeeded,
                Skipped = s.Skipped,
                Label = s.Label,
                Detail = s.Detail,
            };
        }

        private static Models.WorktreeDeleteStepResult ConvertStepFromBranchStep(Models.BranchDeleteStepResult s)
        {
            return new Models.WorktreeDeleteStepResult
            {
                Step = s.Step switch
                {
                    Models.BranchDeleteStepKind.WorktreeRemove => Models.WorktreeDeleteStepKind.WorktreeRemove,
                    Models.BranchDeleteStepKind.BackupRefCreate => Models.WorktreeDeleteStepKind.LocalBackupCreate,
                    Models.BranchDeleteStepKind.LocalBranchDelete => Models.WorktreeDeleteStepKind.LocalBranchDelete,
                    Models.BranchDeleteStepKind.RemoteBranchDelete => Models.WorktreeDeleteStepKind.RemoteBranchDelete,
                    _ => Models.WorktreeDeleteStepKind.TrackingCachePrune,
                },
                Succeeded = s.Succeeded,
                Skipped = s.Skipped,
                Label = s.Label,
                Detail = s.Detail,
            };
        }
    }
}
