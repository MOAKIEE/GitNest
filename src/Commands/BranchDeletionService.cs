using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace SourceGit.Commands
{
    /// <summary>
    /// Handles safe local and remote branch deletion with real upstream resolution, worktree checks,
    /// backup refs, explicit --force-with-lease, partial failure reporting, and restore support.
    /// </summary>
    public static class BranchDeletionService
    {
        public static Task<Models.BranchDeletePreflight> InspectLocalBranchAsync(
            string repoPath,
            string branchName,
            bool probeLinkedRemote = false,
            CancellationToken cancellationToken = default)
        {
            return InspectLocalBranchCoreAsync(repoPath, branchName, probeLinkedRemote, null, cancellationToken);
        }

        internal static async Task<Models.BranchDeletePreflight> InspectLocalBranchCoreAsync(
            string repoPath,
            string branchName,
            bool probeLinkedRemote,
            Models.WorktreeInspectPreflight attachedWorktreeOverride,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(branchName) || IsOptionLike(branchName))
                throw new ArgumentException("无效的本地分支名称。", nameof(branchName));

            var (_, commonDir) = await Models.RepositoryWriteGuard.ResolveGitDirectoriesAsync(repoPath, cancellationToken)
                .ConfigureAwait(false);
            var fullRef = "refs/heads/" + branchName;
            var localTipSha = await ResolveRefShaAsync(repoPath, fullRef, cancellationToken).ConfigureAwait(false);

            var currentBranch = await ReadCurrentBranchNameAsync(repoPath, cancellationToken).ConfigureAwait(false);
            var isCurrent = string.Equals(currentBranch, branchName, StringComparison.Ordinal);

            var isCheckedOutElsewhere = false;
            var attachedWtPath = string.Empty;
            var attachedWtLocked = false;
            var attachedWtLockReason = string.Empty;
            var attachedWtPrunable = false;
            var attachedWtHasChanges = false;
            var attachedWtPreflight = attachedWorktreeOverride;

            if (attachedWtPreflight != null)
            {
                isCheckedOutElsewhere = !attachedWtPreflight.IsMainWorktree;
                attachedWtPath = attachedWtPreflight.TargetWorktreePath;
                attachedWtLocked = attachedWtPreflight.IsLocked;
                attachedWtLockReason = attachedWtPreflight.LockReason;
                attachedWtPrunable = !string.IsNullOrEmpty(attachedWtPreflight.PrunableReason);
                attachedWtHasChanges = attachedWtPreflight.RequiresForceToDeleteFiles;
            }
            else
            {
                var worktreeInfo = await InspectAttachedWorktreeAsync(repoPath, fullRef, cancellationToken).ConfigureAwait(false);
                if (!isCurrent && worktreeInfo.IsCurrentWorktree)
                    isCurrent = true;

                if (worktreeInfo.IsCheckedOutElsewhere)
                {
                    isCheckedOutElsewhere = true;
                    attachedWtPath = worktreeInfo.WorktreePath;
                    attachedWtPreflight = await WorktreeDeletionService.InspectWorktreeAsync(
                        repoPath,
                        worktreeInfo.WorktreePath,
                        probeUpstreamRemote: false,
                        includeAttachedBranchPreflight: false,
                        cancellationToken: cancellationToken).ConfigureAwait(false);
                    attachedWtLocked = attachedWtPreflight.IsLocked;
                    attachedWtLockReason = attachedWtPreflight.LockReason;
                    attachedWtPrunable = !string.IsNullOrEmpty(attachedWtPreflight.PrunableReason);
                    attachedWtHasChanges = attachedWtPreflight.RequiresForceToDeleteFiles;
                }
            }

            var unmerged = await InspectUnmergedVsHeadAsync(repoPath, fullRef, cancellationToken).ConfigureAwait(false);
            var upstream = await ResolveConfiguredUpstreamAsync(repoPath, branchName, cancellationToken).ConfigureAwait(false);

            Models.RemoteBranchProbeResult remoteProbe = null;
            if (probeLinkedRemote && upstream.HasConfiguredUpstream && !string.IsNullOrEmpty(upstream.RemoteName))
            {
                remoteProbe = await ProbeRemoteBranchAsync(
                    repoPath,
                    upstream.RemoteName,
                    upstream.RemoteBranchName,
                    cancellationToken: cancellationToken).ConfigureAwait(false);
            }

            var watchedRefs = string.IsNullOrEmpty(localTipSha) ? Array.Empty<string>() : new[] { fullRef };
            var snapshot = await CaptureOperationSnapshot.CaptureAsync(
                repoPath,
                new Models.OperationSnapshotCaptureOptions { RefsToWatch = watchedRefs },
                cancellationToken).ConfigureAwait(false);

            var blockReason = string.Empty;
            var protection = await BranchProtectionPolicy.InspectAsync(repoPath, branchName, cancellationToken).ConfigureAwait(false);
            if (protection.IsEffectivelyProtected)
                blockReason = protection.Summary;
            else if (isCurrent)
                blockReason = $"不能直接删除当前检出的分支 '{branchName}'，请先切换到其他分支。";
            else if (isCheckedOutElsewhere)
                blockReason = $"分支 '{branchName}' 正在被工作树 '{attachedWtPath}' 使用，不能直接删除；可先移除该工作树后再删除分支。";

            return new Models.BranchDeletePreflight
            {
                RepoPath = repoPath,
                CanonicalCommonDir = commonDir,
                IsLocal = true,
                BranchName = branchName,
                FullRefName = fullRef,
                TargetTipSha = localTipSha,
                IsCurrent = isCurrent,
                IsCheckedOutInOtherWorktree = isCheckedOutElsewhere,
                AttachedWorktreePath = attachedWtPath,
                AttachedWorktreeLocked = attachedWtLocked,
                AttachedWorktreeLockReason = attachedWtLockReason,
                AttachedWorktreePrunable = attachedWtPrunable,
                AttachedWorktreeHasChanges = attachedWtHasChanges,
                AttachedWorktreePreflight = attachedWtPreflight,
                IsMergedIntoHead = unmerged.IsMerged,
                HasUnmergedCommits = !unmerged.IsMerged && unmerged.CommitCount > 0,
                UnmergedCommitCount = unmerged.CommitCount,
                UnmergedChangedFilesCount = unmerged.ChangedFilesCount,
                UnmergedCommits = unmerged.Commits,
                HasMultipleRemotes = upstream.HasMultipleRemotes,
                HasConfiguredUpstream = upstream.HasConfiguredUpstream,
                ConfiguredUpstreamRef = upstream.FullTrackingRef,
                RemoteName = upstream.RemoteName,
                RemoteBranchName = upstream.RemoteBranchName,
                RedactedRemoteUrl = remoteProbe?.RedactedRemoteUrl ?? upstream.RedactedRemoteUrl,
                IsUpstreamNameDifferent = upstream.HasConfiguredUpstream &&
                    !string.Equals(upstream.RemoteBranchName, branchName, StringComparison.Ordinal),
                HasLocalTrackingCache = !string.IsNullOrEmpty(upstream.LocalTrackingTipSha),
                LocalTrackingTipSha = upstream.LocalTrackingTipSha,
                RemoteProbe = remoteProbe,
                ConfirmationSnapshot = snapshot,
                DirectBlockReason = blockReason,
                Protection = protection,
            };
        }

        public static async Task<Models.BranchDeletePreflight> InspectRemoteBranchAsync(
            string repoPath,
            string remoteName,
            string remoteBranchName,
            bool queryServer = true,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(remoteName) || IsOptionLike(remoteName))
                throw new ArgumentException("无效的远端名称。", nameof(remoteName));
            if (string.IsNullOrWhiteSpace(remoteBranchName) || IsOptionLike(remoteBranchName))
                throw new ArgumentException("无效的远程分支名称。", nameof(remoteBranchName));

            var (_, commonDir) = await Models.RepositoryWriteGuard.ResolveGitDirectoriesAsync(repoPath, cancellationToken)
                .ConfigureAwait(false);
            var remotes = await new QueryRemotes(repoPath).GetResultAsync().ConfigureAwait(false);
            var remote = remotes.Find(r => string.Equals(r.Name, remoteName, StringComparison.Ordinal));
            var rawUrl = remote != null ? (remote.PushURL ?? remote.URL) : await ReadRemoteUrlAsync(repoPath, remoteName, cancellationToken).ConfigureAwait(false);
            var redactedUrl = Models.GitConfigSnapshot.RedactText(rawUrl);

            var fullTrackingRef = $"refs/remotes/{remoteName}/{remoteBranchName}";
            var localTrackingSha = await ResolveRefShaAsync(repoPath, fullTrackingRef, cancellationToken).ConfigureAwait(false);

            var probe = queryServer
                ? await ProbeRemoteBranchAsync(repoPath, remoteName, remoteBranchName, cancellationToken: cancellationToken).ConfigureAwait(false)
                : null;

            // Intentionally do NOT watch refs/remotes/* so background fetch does not alter
            // or invalidate the confirmed lease SHA captured during preflight.
            var snapshot = await CaptureOperationSnapshot.CaptureAsync(
                repoPath,
                new Models.OperationSnapshotCaptureOptions(),
                cancellationToken).ConfigureAwait(false);

            return new Models.BranchDeletePreflight
            {
                RepoPath = repoPath,
                CanonicalCommonDir = commonDir,
                IsLocal = false,
                BranchName = remoteBranchName,
                FullRefName = fullTrackingRef,
                TargetTipSha = probe?.LiveTipSha ?? localTrackingSha,
                HasMultipleRemotes = remotes.Count > 1,
                RemoteName = remoteName,
                RemoteBranchName = remoteBranchName,
                RedactedRemoteUrl = probe?.RedactedRemoteUrl ?? redactedUrl,
                HasLocalTrackingCache = !string.IsNullOrEmpty(localTrackingSha),
                LocalTrackingTipSha = localTrackingSha,
                RemoteProbe = probe,
                ConfirmationSnapshot = snapshot,
                Protection = await BranchProtectionPolicy.InspectAsync(repoPath, remoteBranchName, cancellationToken).ConfigureAwait(false),
            };
        }

        public static async Task<Models.RemoteBranchProbeResult> ProbeRemoteBranchAsync(
            string repoPath,
            string remoteName,
            string remoteBranchName,
            Models.ICommandLog log = null,
            CancellationToken cancellationToken = default,
            string expectedPushUrl = null)
        {
            if (string.IsNullOrWhiteSpace(remoteName) || IsOptionLike(remoteName) ||
                string.IsNullOrWhiteSpace(remoteBranchName) || IsOptionLike(remoteBranchName))
            {
                return new Models.RemoteBranchProbeResult
                {
                    RemoteName = remoteName ?? string.Empty,
                    RemoteBranchName = remoteBranchName ?? string.Empty,
                    QuerySucceeded = false,
                    ErrorSummary = "无效的远端或分支名称。",
                };
            }

            var remotes = await new QueryRemotes(repoPath).GetResultAsync().ConfigureAwait(false);
            var remote = remotes.Find(r => string.Equals(r.Name, remoteName, StringComparison.Ordinal));
            var target = await RemotePushTarget.ResolveAsync(repoPath, remoteName, cancellationToken).ConfigureAwait(false);
            var redactedUrl = target.RedactedUrl;

            if (!target.IsValid || (expectedPushUrl != null && !string.Equals(expectedPushUrl, target.Url, StringComparison.Ordinal)))
            {
                return new Models.RemoteBranchProbeResult
                {
                    RemoteName = remoteName,
                    RemoteBranchName = remoteBranchName,
                    QuerySucceeded = false,
                    ErrorSummary = target.IsValid ? "远端推送地址在确认后已变化，请刷新并重新确认。" : target.Error,
                };
            }

            var targetRef = $"refs/heads/{remoteBranchName}";
            var cmd = new RemoteGitCommand(
                repoPath,
                $"ls-remote --heads -- {CommandArgumentHelper.Quote(target.Url)} {CommandArgumentHelper.Quote(targetRef)}",
                remote?.PrivateSSHKey ?? string.Empty)
            {
                CancellationToken = cancellationToken,
            };
            if (log != null)
                cmd.Use(log);

            var res = await cmd.ExecuteAsync().ConfigureAwait(false);
            if (!res.IsSuccess)
            {
                var err = Models.GitConfigSnapshot.RedactText(FirstNonEmpty(res.StdErr, res.StdOut, "无法连接远端查询分支最新提交。"));
                return new Models.RemoteBranchProbeResult
                {
                    RemoteName = remoteName,
                    RemoteBranchName = remoteBranchName,
                    RedactedRemoteUrl = redactedUrl,
                    QuerySucceeded = false,
                    ErrorSummary = err,
                };
            }

            var tipSha = ParseLsRemoteExactTip(res.StdOut, targetRef);
            return new Models.RemoteBranchProbeResult
            {
                RemoteName = remoteName,
                RemoteBranchName = remoteBranchName,
                RedactedRemoteUrl = redactedUrl,
                PushUrl = target.Url,
                QuerySucceeded = true,
                ExistsOnServer = !string.IsNullOrEmpty(tipSha),
                LiveTipSha = tipSha,
            };
        }

        public static async Task<Models.BranchDeleteOutcome> DeleteLocalBranchAsync(
            string repoPath,
            string branchName,
            Models.BranchDeleteOptions options = null,
            Models.OperationJournalStore journalStore = null,
            Models.ICommandLog outerLog = null,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(branchName) || IsOptionLike(branchName))
            {
                return new Models.BranchDeleteOutcome
                {
                    BranchName = branchName ?? string.Empty,
                    BlockedByProtection = true,
                    Summary = "无效的本地分支名称。",
                };
            }

            options ??= new Models.BranchDeleteOptions();
            var preflight = options.Preflight ?? await InspectLocalBranchAsync(
                repoPath,
                branchName,
                probeLinkedRemote: options.DeleteLinkedUpstreamRemote,
                cancellationToken: cancellationToken).ConfigureAwait(false);

            if (preflight.IsCurrent)
            {
                return new Models.BranchDeleteOutcome
                {
                    BranchName = branchName,
                    BlockedByProtection = true,
                    Summary = $"不能直接删除当前所在分支 '{branchName}'，请先切换到其他分支。",
                };
            }

            if (preflight.IsCheckedOutInOtherWorktree && !options.DeleteAttachedWorktree)
            {
                return new Models.BranchDeleteOutcome
                {
                    BranchName = branchName,
                    BlockedByProtection = true,
                    Summary = $"分支 '{branchName}' 正在被工作树 '{preflight.AttachedWorktreePath}' 使用，不能直接删除。请先移除该工作树，或确认执行“先删除工作树，再删除分支”。",
                };
            }

            var deleteLinkedRemote = options.DeleteLinkedUpstreamRemote;
            if (deleteLinkedRemote && (!preflight.HasConfiguredUpstream || string.IsNullOrEmpty(preflight.RemoteName)))
            {
                return new Models.BranchDeleteOutcome
                {
                    BranchName = branchName,
                    BlockedByProtection = true,
                    Summary = $"分支 '{branchName}' 未配置有效 upstream 远端分支，不能执行关联远端删除。",
                };
            }

            var confirmedExpectedSha = options.ConfirmedExpectedRemoteTipSha ?? preflight.RemoteProbe?.LiveTipSha ?? string.Empty;
            var confirmedRemoteExists = options.ConfirmedRemoteExistsOnServer ?? preflight.RemoteProbe?.ExistsOnServer;
            if (deleteLinkedRemote)
            {
                if (preflight.RemoteProbe is { QuerySucceeded: false })
                {
                    return new Models.BranchDeleteOutcome
                    {
                        BranchName = branchName,
                        RemoteName = preflight.RemoteName,
                        RemoteBranchName = preflight.RemoteBranchName,
                        BlockedByProtection = true,
                        Summary = $"关联远端状态查询失败：{preflight.RemoteProbe.ErrorSummary}",
                    };
                }

                if (!confirmedRemoteExists.HasValue)
                {
                    var probeNow = await ProbeRemoteBranchAsync(
                        repoPath,
                        preflight.RemoteName,
                        preflight.RemoteBranchName,
                        outerLog,
                        cancellationToken).ConfigureAwait(false);
                    if (!probeNow.QuerySucceeded)
                    {
                        return new Models.BranchDeleteOutcome
                        {
                            BranchName = branchName,
                            RemoteName = preflight.RemoteName,
                            RemoteBranchName = preflight.RemoteBranchName,
                            BlockedByProtection = true,
                            Summary = $"关联远端状态查询失败：{probeNow.ErrorSummary}",
                        };
                    }

                    confirmedRemoteExists = probeNow.ExistsOnServer;
                    confirmedExpectedSha = probeNow.LiveTipSha;
                }

                if (confirmedRemoteExists.Value && string.IsNullOrEmpty(confirmedExpectedSha))
                {
                    return new Models.BranchDeleteOutcome
                    {
                        BranchName = branchName,
                        RemoteName = preflight.RemoteName,
                        RemoteBranchName = preflight.RemoteBranchName,
                        BlockedByProtection = true,
                        Summary = "缺少远端分支预期提交哈希，已阻止无 lease 约束的远端删除。",
                    };
                }
            }

            var verifyOptions = new Models.OperationSnapshotVerifyOptions
            {
                RequireSameRepoIdentity = true,
                RequireSameHead = true,
                RequireSameInProgressState = true,
                RequireCleanWorkingTree = false,
                RequireSameStagedFingerprint = false,
                RequireSameWatchedRefs = true,
                CheckGitLocks = true,
                CheckIndexLock = true,
                BranchLockToCheck = branchName,
            };

            var steps = new List<Models.BranchDeleteStepResult>();
            var capturingLog = new CapturingCommandLog(outerLog);
            var operationId = Models.OperationJournalStore.GenerateOperationId();
            var opKind = deleteLinkedRemote ? Models.GitOperationKind.DeleteRemoteBranch : Models.GitOperationKind.DeleteBranch;
            var opTitle = deleteLinkedRemote
                ? $"删除本地分支 {branchName} 及关联远端 {preflight.RemoteName}/{preflight.RemoteBranchName}"
                : $"删除本地分支 {branchName}";

            await using var guard = await Models.RepositoryWriteGuard.TryEnterAsync(
                repoPath,
                opTitle,
                checkIndexLock: true,
                branchNameToUpdate: branchName,
                cancellationToken: cancellationToken).ConfigureAwait(false);

            if (!guard.Acquired)
            {
                return new Models.BranchDeleteOutcome
                {
                    OperationId = operationId,
                    BranchName = branchName,
                    BlockedByLock = true,
                    Summary = guard.LockDetection.Summary,
                    DetailMessage = string.Join(Environment.NewLine, guard.LockDetection.BlockingLockFiles),
                };
            }

            var policyError = await ValidateDeletionPolicyAsync(repoPath, branchName, deleteLinkedRemote,
                preflight, options.ConfirmedPushUrl, cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(policyError))
                return new Models.BranchDeleteOutcome { BranchName = branchName, BlockedByProtection = true, Summary = policyError };

            if (preflight.ConfirmationSnapshot != null)
            {
                var verify = await CaptureOperationSnapshot.VerifyAsync(
                    preflight.ConfirmationSnapshot,
                    verifyOptions,
                    cancellationToken).ConfigureAwait(false);
                if (!verify.IsValid)
                {
                    return new Models.BranchDeleteOutcome
                    {
                        OperationId = operationId,
                        BranchName = branchName,
                        BlockedBySnapshot = true,
                        BlockedByLock = verify.LockDetection.IsLocked,
                        Summary = "仓库状态已变化，请重新查看，原确认已失效。",
                        DetailMessage = verify.Summary + (verify.Details.Count > 0 ? Environment.NewLine + string.Join(Environment.NewLine, verify.Details) : string.Empty),
                    };
                }
            }

            var liveTipSha = await ResolveRefShaAsync(repoPath, "refs/heads/" + branchName, cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrEmpty(liveTipSha))
            {
                return new Models.BranchDeleteOutcome
                {
                    OperationId = operationId,
                    BranchName = branchName,
                    BlockedBySnapshot = true,
                    Summary = $"本地分支 '{branchName}' 已不存在，请刷新后核对当前分支状态。",
                };
            }

            var journalRecord = new Models.GitOperationRecord
            {
                OperationId = operationId,
                RepositoryCommonDir = guard.Lease.CanonicalCommonDir,
                WorktreePath = repoPath,
                Kind = opKind,
                Title = opTitle,
                BranchName = branchName,
                RemoteName = deleteLinkedRemote ? preflight.RemoteName : string.Empty,
                RemoteBranchName = deleteLinkedRemote ? preflight.RemoteBranchName : string.Empty,
                OriginalTipSha = liveTipSha,
                ExpectedRemoteTipSha = deleteLinkedRemote ? (confirmedExpectedSha ?? string.Empty) : string.Empty,
                Stage = Models.GitOperationStage.Executing,
                StepSummary = $"正在执行：{opTitle}",
            };
            TrySaveJournal(journalStore, journalRecord);

            // Step 1 (optional): Explicitly remove attached worktree first through unified WorktreeDeletionService
            var worktreeRemoved = false;
            if (preflight.IsCheckedOutInOtherWorktree && options.DeleteAttachedWorktree)
            {
                var wtSteps = new List<Models.WorktreeDeleteStepResult>();
                var wtMainRoot = !string.IsNullOrWhiteSpace(preflight.AttachedWorktreePreflight?.MainRepoPath)
                    ? preflight.AttachedWorktreePreflight.MainRepoPath
                    : repoPath;
                var wtRes = await WorktreeDeletionService.RemoveWorktreeInLockAsync(
                    wtMainRoot,
                    preflight.AttachedWorktreePath,
                    preflight.AttachedWorktreePreflight,
                    options.ForceRemoveWorktree,
                    allowDetachedBackup: false,
                    options.BeforeWorktreeRemoveAsync,
                    operationId,
                    journalRecord,
                    capturingLog,
                    wtSteps,
                    cancellationToken).ConfigureAwait(false);

                foreach (var ws in wtSteps)
                {
                    steps.Add(new Models.BranchDeleteStepResult
                    {
                        Step = ws.Step == Models.WorktreeDeleteStepKind.DetachedBackupCreate
                            ? Models.BranchDeleteStepKind.BackupRefCreate
                            : Models.BranchDeleteStepKind.WorktreeRemove,
                        Succeeded = ws.Succeeded,
                        Skipped = ws.Skipped,
                        Label = ws.Label,
                        Detail = ws.Detail,
                    });
                }

                if (!wtRes.Succeeded)
                {
                    FinalizeJournalFailure(journalStore, journalRecord, capturingLog, wtRes.Summary, wtRes.DetailMessage);
                    return new Models.BranchDeleteOutcome
                    {
                        OperationId = operationId,
                        BranchName = branchName,
                        Succeeded = false,
                        BlockedByProtection = wtRes.BlockedByProtection,
                        BlockedByLock = wtRes.BlockedByLock,
                        BlockedByNestedRepoOrSubmodule = wtRes.BlockedByNestedRepoOrSubmodule,
                        BlockedBySnapshot = wtRes.BlockedBySnapshot,
                        RequiresForceWorktreeConfirmation = wtRes.RequiresForceWorktreeConfirmation,
                        Summary = $"工作树移除失败，已停止删除分支：{wtRes.Summary}",
                        DetailMessage = wtRes.DetailMessage,
                        StepResults = steps,
                        OperationRecord = journalRecord,
                    };
                }

                worktreeRemoved = true;
            }

            return await ExecuteLocalBranchAndLinkedRemoteInLockAsync(
                repoPath,
                branchName,
                liveTipSha,
                preflight,
                options,
                confirmedRemoteExists,
                confirmedExpectedSha,
                deleteLinkedRemote,
                operationId,
                journalStore,
                journalRecord,
                steps,
                capturingLog,
                worktreeRemoved,
                cancellationToken).ConfigureAwait(false);
        }

        internal static async Task<Models.BranchDeleteOutcome> ExecuteLocalBranchAndLinkedRemoteInLockAsync(
            string repoPath,
            string branchName,
            string liveTipSha,
            Models.BranchDeletePreflight preflight,
            Models.BranchDeleteOptions options,
            bool? confirmedRemoteExists,
            string confirmedExpectedSha,
            bool deleteLinkedRemote,
            string operationId,
            Models.OperationJournalStore journalStore,
            Models.GitOperationRecord journalRecord,
            List<Models.BranchDeleteStepResult> steps,
            CapturingCommandLog capturingLog,
            bool worktreeRemoved,
            CancellationToken cancellationToken)
        {
            var policyError = await ValidateDeletionPolicyAsync(repoPath, branchName, deleteLinkedRemote,
                preflight, options.ConfirmedPushUrl, cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(policyError))
            {
                if (worktreeRemoved)
                    FinalizeJournalPartialSuccess(journalStore, journalRecord, capturingLog, $"工作树已移除，本地分支未删除：{policyError}", string.Empty);
                else
                    FinalizeJournalFailure(journalStore, journalRecord, capturingLog, policyError, string.Empty);
                return new Models.BranchDeleteOutcome
                {
                    BranchName = branchName,
                    BlockedByProtection = true,
                    WorktreeRemoved = worktreeRemoved,
                    IsPartialSuccess = worktreeRemoved,
                    Summary = policyError,
                };
            }
            journalRecord.RemotePushUrlFingerprint = deleteLinkedRemote
                ? RemotePushTarget.Fingerprint(options.ConfirmedPushUrl ?? preflight.RemoteProbe?.PushUrl)
                : string.Empty;
            // Step 2: Create local backup ref before deleting the local branch
            var backupRes = await BackupRefs.CreateAsync(repoPath, operationId, liveTipSha, capturingLog, cancellationToken)
                .ConfigureAwait(false);
            if (!backupRes.Succeeded)
            {
                steps.Add(new Models.BranchDeleteStepResult
                {
                    Step = Models.BranchDeleteStepKind.BackupRefCreate,
                    Succeeded = false,
                    Label = "创建本地恢复引用失败",
                    Detail = backupRes.Summary,
                });
                if (worktreeRemoved)
                    FinalizeJournalPartialSuccess(journalStore, journalRecord, capturingLog, $"工作树已移除，未能创建分支备份引用：{backupRes.Summary}", backupRes.Summary);
                else
                    FinalizeJournalFailure(journalStore, journalRecord, capturingLog, backupRes.Summary, string.Empty);

                return new Models.BranchDeleteOutcome
                {
                    OperationId = operationId,
                    BranchName = branchName,
                    WorktreeRemoved = worktreeRemoved,
                    IsPartialSuccess = worktreeRemoved,
                    Succeeded = false,
                    Summary = $"未能创建本地恢复引用，已停止删除：{backupRes.Summary}",
                    StepResults = steps,
                    OperationRecord = journalRecord,
                };
            }

            journalRecord.BackupRefName = backupRes.BackupRefName;
            TrySaveJournal(journalStore, journalRecord);
            steps.Add(new Models.BranchDeleteStepResult
            {
                Step = Models.BranchDeleteStepKind.BackupRefCreate,
                Succeeded = true,
                Label = $"已保存本地恢复引用：{backupRes.BackupRefName}（指向 {ShortSha(liveTipSha)}）",
            });

            // Step 3: Delete local branch (-d by default; -D only when explicitly requested)
            var deleteFlag = options.ForceDeleteLocal ? "-D" : "-d";
            var localDelRes = await Command.RunGitDirectAsync(
                repoPath,
                cancellationToken,
                "branch",
                deleteFlag,
                "--",
                branchName).ConfigureAwait(false);
            capturingLog.AppendLine($"$ git branch {deleteFlag} -- {branchName}");
            AppendRedactedCommandOutput(capturingLog, localDelRes);

            if (!localDelRes.IsSuccess)
            {
                // Local branch was NOT deleted, so remove unused temporary backup ref to keep backups clean.
                await BackupRefs.DeleteBackupRefAsync(repoPath, backupRes.BackupRefName, liveTipSha, capturingLog, cancellationToken)
                    .ConfigureAwait(false);

                var errText = Models.GitConfigSnapshot.RedactText(FirstNonEmpty(localDelRes.StdErr, localDelRes.StdOut, "删除本地分支失败。"));
                var isUnmerged = !options.ForceDeleteLocal &&
                    errText.Contains("not fully merged", StringComparison.OrdinalIgnoreCase);

                var unmergedNow = isUnmerged
                    ? await InspectUnmergedVsHeadAsync(repoPath, "refs/heads/" + branchName, cancellationToken).ConfigureAwait(false)
                    : (IsMerged: false, CommitCount: preflight.UnmergedCommitCount, ChangedFilesCount: preflight.UnmergedChangedFilesCount, Commits: preflight.UnmergedCommits);

                steps.Add(new Models.BranchDeleteStepResult
                {
                    Step = Models.BranchDeleteStepKind.LocalBranchDelete,
                    Succeeded = false,
                    Label = $"本地分支 '{branchName}' 未删除",
                    Detail = isUnmerged
                        ? $"分支包含 {unmergedNow.CommitCount} 个尚未合并到当前 HEAD 的提交，常规 `-d` 已阻止删除。"
                        : errText,
                });

                journalRecord.BackupRefName = string.Empty;
                var summary = isUnmerged
                    ? $"本地分支 '{branchName}' 包含未合并提交，已按常规检查阻止删除；如需强删请使用“仍然删除（`-D`）”。"
                    : errText;
                if (worktreeRemoved)
                    FinalizeJournalPartialSuccess(journalStore, journalRecord, capturingLog, $"工作树已移除，本地分支未删除：{summary}", errText);
                else
                    FinalizeJournalFailure(journalStore, journalRecord, capturingLog, summary, errText);

                return new Models.BranchDeleteOutcome
                {
                    OperationId = operationId,
                    BranchName = branchName,
                    WorktreeRemoved = worktreeRemoved,
                    IsPartialSuccess = worktreeRemoved,
                    RequiresForceLocalConfirmation = isUnmerged,
                    UnmergedCommitCount = unmergedNow.CommitCount,
                    UnmergedChangedFilesCount = unmergedNow.ChangedFilesCount,
                    UnmergedCommits = unmergedNow.Commits,
                    Succeeded = false,
                    Summary = summary,
                    DetailMessage = errText,
                    StepResults = steps,
                    OperationRecord = journalRecord,
                };
            }

            steps.Add(new Models.BranchDeleteStepResult
            {
                Step = Models.BranchDeleteStepKind.LocalBranchDelete,
                Succeeded = true,
                Label = $"本地分支 '{branchName}' 已删除",
            });

            if (!deleteLinkedRemote)
            {
                FinalizeJournalSuccess(
                    journalStore,
                    journalRecord,
                    capturingLog,
                    $"已安全删除本地分支 '{branchName}'（恢复引用：{backupRes.BackupRefName}）。");
                return new Models.BranchDeleteOutcome
                {
                    OperationId = operationId,
                    BranchName = branchName,
                    WorktreeRemoved = worktreeRemoved,
                    LocalBranchDeleted = true,
                    BackupRefName = backupRes.BackupRefName,
                    LocalBackupSha = liveTipSha,
                    Succeeded = true,
                    Summary = journalRecord.StepSummary,
                    StepResults = steps,
                    OperationRecord = journalRecord,
                };
            }

            // Step 4: Linked remote delete using frozen expected remote tip SHA
            if (confirmedRemoteExists == false)
            {
                var prunedCache = false;
                if (preflight.HasLocalTrackingCache)
                {
                    prunedCache = await TryDeleteLocalRemoteTrackingRefAsync(
                        repoPath,
                        preflight.RemoteName,
                        preflight.RemoteBranchName,
                        options.ConfirmedPushUrl ?? preflight.RemoteProbe?.PushUrl,
                        preflight.LocalTrackingTipSha,
                        capturingLog,
                        cancellationToken).ConfigureAwait(false);
                }

                steps.Add(new Models.BranchDeleteStepResult
                {
                    Step = Models.BranchDeleteStepKind.RemoteBranchDelete,
                    Succeeded = true,
                    Skipped = true,
                    Label = $"远端分支 '{preflight.RemoteName}/{preflight.RemoteBranchName}' 在服务器上已不存在",
                    Detail = prunedCache ? "已清理残留的本地远程跟踪缓存。" : string.Empty,
                });

                var doneSummary = $"已删除本地分支 '{branchName}'；远端分支 '{preflight.RemoteName}/{preflight.RemoteBranchName}' 在服务器上已不存在" +
                    (prunedCache ? "（已清理本地跟踪缓存）。" : "。");
                FinalizeJournalSuccess(journalStore, journalRecord, capturingLog, doneSummary);

                return new Models.BranchDeleteOutcome
                {
                    OperationId = operationId,
                    BranchName = branchName,
                    WorktreeRemoved = worktreeRemoved,
                    LocalBranchDeleted = true,
                    RemoteBranchDeleted = false,
                    LocalTrackingCachePruned = prunedCache,
                    BackupRefName = backupRes.BackupRefName,
                    LocalBackupSha = liveTipSha,
                    RemoteName = preflight.RemoteName,
                    RemoteBranchName = preflight.RemoteBranchName,
                    Succeeded = true,
                    Summary = doneSummary,
                    StepResults = steps,
                    OperationRecord = journalRecord,
                };
            }

            var remoteDelete = await ExecuteRemoteLeaseDeleteAsync(
                repoPath,
                preflight.RemoteName,
                preflight.RemoteBranchName,
                confirmedExpectedSha,
                options.ConfirmedPushUrl ?? preflight.RemoteProbe?.PushUrl,
                capturingLog,
                cancellationToken).ConfigureAwait(false);

            if (!remoteDelete.Succeeded)
            {
                steps.Add(new Models.BranchDeleteStepResult
                {
                    Step = Models.BranchDeleteStepKind.RemoteBranchDelete,
                    Succeeded = false,
                    Label = $"远端分支 '{preflight.RemoteName}/{preflight.RemoteBranchName}' 未删除",
                    Detail = remoteDelete.UserMessage,
                });

                var partialSummary = $"本地已删除，远程未删除：{remoteDelete.UserMessage}";
                journalRecord.Stage = Models.GitOperationStage.PartialSuccess;
                journalRecord.StepSummary = partialSummary;
                journalRecord.CommandSummary = capturingLog.GetCombinedText();
                journalRecord.DiagnosticDetail = remoteDelete.RedactedDetail;
                TrySaveJournal(journalStore, journalRecord);

                return new Models.BranchDeleteOutcome
                {
                    OperationId = operationId,
                    BranchName = branchName,
                    WorktreeRemoved = worktreeRemoved,
                    LocalBranchDeleted = true,
                    RemoteBranchDeleted = false,
                    IsPartialSuccess = true,
                    RejectedByRemoteLease = remoteDelete.RejectedByLease,
                    CollaboratorCommitsPreserved = remoteDelete.RejectedByLease,
                    BackupRefName = backupRes.BackupRefName,
                    LocalBackupSha = liveTipSha,
                    RemoteName = preflight.RemoteName,
                    RemoteBranchName = preflight.RemoteBranchName,
                    ExpectedRemoteTipSha = confirmedExpectedSha,
                    Succeeded = false,
                    Summary = partialSummary,
                    DetailMessage = remoteDelete.RedactedDetail,
                    StepResults = steps,
                    OperationRecord = journalRecord,
                };
            }

            var trackingPruned = await TryDeleteLocalRemoteTrackingRefAsync(
                repoPath,
                preflight.RemoteName,
                preflight.RemoteBranchName,
                options.ConfirmedPushUrl ?? preflight.RemoteProbe?.PushUrl,
                preflight.LocalTrackingTipSha,
                capturingLog,
                cancellationToken).ConfigureAwait(false);

            steps.Add(new Models.BranchDeleteStepResult
            {
                Step = Models.BranchDeleteStepKind.RemoteBranchDelete,
                Succeeded = true,
                Label = $"远端分支 '{preflight.RemoteName}/{preflight.RemoteBranchName}' 已按预期提交 {ShortSha(confirmedExpectedSha)} 安全删除",
            });

            var linkedSuccessSummary = $"已删除本地分支 '{branchName}' 及关联远端分支 '{preflight.RemoteName}/{preflight.RemoteBranchName}'。";
            FinalizeJournalSuccess(journalStore, journalRecord, capturingLog, linkedSuccessSummary);

            return new Models.BranchDeleteOutcome
            {
                OperationId = operationId,
                BranchName = branchName,
                WorktreeRemoved = worktreeRemoved,
                LocalBranchDeleted = true,
                RemoteBranchDeleted = true,
                LocalTrackingCachePruned = trackingPruned,
                BackupRefName = backupRes.BackupRefName,
                LocalBackupSha = liveTipSha,
                RemoteName = preflight.RemoteName,
                RemoteBranchName = preflight.RemoteBranchName,
                ExpectedRemoteTipSha = confirmedExpectedSha,
                Succeeded = true,
                Summary = linkedSuccessSummary,
                StepResults = steps,
                OperationRecord = journalRecord,
            };
        }

        public static async Task<Models.BranchDeleteOutcome> DeleteRemoteBranchAsync(
            string repoPath,
            string remoteName,
            string remoteBranchName,
            Models.BranchDeleteOptions options = null,
            Models.OperationJournalStore journalStore = null,
            Models.ICommandLog outerLog = null,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(remoteName) || IsOptionLike(remoteName) ||
                string.IsNullOrWhiteSpace(remoteBranchName) || IsOptionLike(remoteBranchName))
            {
                return new Models.BranchDeleteOutcome
                {
                    BranchName = remoteBranchName ?? string.Empty,
                    RemoteName = remoteName ?? string.Empty,
                    BlockedByProtection = true,
                    Summary = "无效的远端或远程分支名称。",
                };
            }

            options ??= new Models.BranchDeleteOptions();
            var preflight = options.Preflight ?? await InspectRemoteBranchAsync(
                repoPath,
                remoteName,
                remoteBranchName,
                queryServer: true,
                cancellationToken: cancellationToken).ConfigureAwait(false);

            if (preflight.RemoteProbe is { QuerySucceeded: false })
            {
                return new Models.BranchDeleteOutcome
                {
                    BranchName = remoteBranchName,
                    RemoteName = remoteName,
                    RemoteBranchName = remoteBranchName,
                    BlockedByProtection = true,
                    Summary = $"查询远端分支失败，未删除任何本地跟踪缓存：{preflight.RemoteProbe.ErrorSummary}",
                    DetailMessage = preflight.RemoteProbe.ErrorSummary,
                };
            }

            var confirmedExists = options.ConfirmedRemoteExistsOnServer ?? preflight.RemoteProbe?.ExistsOnServer;
            var confirmedExpectedSha = options.ConfirmedExpectedRemoteTipSha ?? preflight.RemoteProbe?.LiveTipSha ?? string.Empty;

            if (!confirmedExists.HasValue)
            {
                var liveProbe = await ProbeRemoteBranchAsync(repoPath, remoteName, remoteBranchName, outerLog, cancellationToken)
                    .ConfigureAwait(false);
                if (!liveProbe.QuerySucceeded)
                {
                    return new Models.BranchDeleteOutcome
                    {
                        BranchName = remoteBranchName,
                        RemoteName = remoteName,
                        RemoteBranchName = remoteBranchName,
                        BlockedByProtection = true,
                        Summary = $"查询远端分支失败，未删除任何本地跟踪缓存：{liveProbe.ErrorSummary}",
                        DetailMessage = liveProbe.ErrorSummary,
                    };
                }

                confirmedExists = liveProbe.ExistsOnServer;
                confirmedExpectedSha = liveProbe.LiveTipSha;
            }

            if (confirmedExists.Value && string.IsNullOrEmpty(confirmedExpectedSha))
            {
                return new Models.BranchDeleteOutcome
                {
                    BranchName = remoteBranchName,
                    RemoteName = remoteName,
                    RemoteBranchName = remoteBranchName,
                    BlockedByProtection = true,
                    Summary = "缺少远端分支预期提交哈希，已阻止无 lease 约束的远端删除。",
                };
            }

            if (!confirmedExists.Value && !options.PruneStaleTrackingCacheWhenRemoteAbsent)
            {
                return new Models.BranchDeleteOutcome
                {
                    BranchName = remoteBranchName,
                    RemoteName = remoteName,
                    RemoteBranchName = remoteBranchName,
                    RequiresRemoteCachePruneConfirmation = true,
                    Summary = $"远端服务器 '{remoteName}' 上已不存在分支 '{remoteBranchName}'。请确认是否仅清理本地远程跟踪缓存（不冒充服务器删除）。",
                };
            }

            var operationId = Models.OperationJournalStore.GenerateOperationId();
            var capturingLog = new CapturingCommandLog(outerLog);
            var steps = new List<Models.BranchDeleteStepResult>();
            var title = confirmedExists.Value
                ? $"删除远程分支 {remoteName}/{remoteBranchName}"
                : $"清理失效本地跟踪缓存 {remoteName}/{remoteBranchName}";

            await using var guard = await Models.RepositoryWriteGuard.TryEnterAsync(
                repoPath,
                title,
                checkIndexLock: false,
                branchNameToUpdate: null,
                cancellationToken: cancellationToken).ConfigureAwait(false);

            if (!guard.Acquired)
            {
                return new Models.BranchDeleteOutcome
                {
                    OperationId = operationId,
                    BranchName = remoteBranchName,
                    RemoteName = remoteName,
                    RemoteBranchName = remoteBranchName,
                    BlockedByLock = true,
                    Summary = guard.LockDetection.Summary,
                    DetailMessage = string.Join(Environment.NewLine, guard.LockDetection.BlockingLockFiles),
                };
            }

            if (preflight.ConfirmationSnapshot != null)
            {
                var verify = await CaptureOperationSnapshot.VerifyAsync(
                    preflight.ConfirmationSnapshot,
                    new Models.OperationSnapshotVerifyOptions
                    {
                        RequireSameRepoIdentity = true,
                        RequireSameHead = false,
                        RequireSameInProgressState = false,
                        RequireCleanWorkingTree = false,
                        RequireSameStagedFingerprint = false,
                        RequireSameWatchedRefs = false,
                        CheckGitLocks = true,
                        CheckIndexLock = false,
                    },
                    cancellationToken).ConfigureAwait(false);

                if (!verify.IsValid)
                {
                    return new Models.BranchDeleteOutcome
                    {
                        OperationId = operationId,
                        BranchName = remoteBranchName,
                        RemoteName = remoteName,
                        RemoteBranchName = remoteBranchName,
                        BlockedBySnapshot = true,
                        BlockedByLock = verify.LockDetection.IsLocked,
                        Summary = verify.Summary,
                    };
                }
            }

            var protection = await BranchProtectionPolicy.InspectAsync(repoPath, remoteBranchName, cancellationToken).ConfigureAwait(false);
            var targetError = await RemotePushTarget.VerifyAsync(repoPath, remoteName,
                options.ConfirmedPushUrl ?? preflight.RemoteProbe?.PushUrl, cancellationToken).ConfigureAwait(false);
            if (protection.IsEffectivelyProtected || !string.IsNullOrEmpty(targetError))
                return new Models.BranchDeleteOutcome
                {
                    BranchName = remoteBranchName,
                    RemoteName = remoteName,
                    BlockedByProtection = true,
                    Summary = protection.IsEffectivelyProtected ? protection.Summary : targetError,
                };

            var record = new Models.GitOperationRecord
            {
                OperationId = operationId,
                RepositoryCommonDir = guard.Lease.CanonicalCommonDir,
                WorktreePath = repoPath,
                Kind = Models.GitOperationKind.DeleteRemoteBranch,
                RemotePushUrlFingerprint = RemotePushTarget.Fingerprint(options.ConfirmedPushUrl ?? preflight.RemoteProbe?.PushUrl),
                Title = title,
                BranchName = string.Empty,
                RemoteName = remoteName,
                RemoteBranchName = remoteBranchName,
                OriginalTipSha = confirmedExpectedSha,
                ExpectedRemoteTipSha = confirmedExpectedSha,
                Stage = Models.GitOperationStage.Executing,
                StepSummary = $"正在执行：{title}",
            };
            TrySaveJournal(journalStore, record);

            if (!confirmedExists.Value)
            {
                var pruned = await TryDeleteLocalRemoteTrackingRefAsync(
                    repoPath,
                    remoteName,
                    remoteBranchName,
                    options.ConfirmedPushUrl ?? preflight.RemoteProbe?.PushUrl,
                    preflight.LocalTrackingTipSha,
                    capturingLog,
                    cancellationToken).ConfigureAwait(false);

                steps.Add(new Models.BranchDeleteStepResult
                {
                    Step = Models.BranchDeleteStepKind.TrackingCachePrune,
                    Succeeded = pruned,
                    Label = pruned
                        ? $"已清理本地失效远程跟踪缓存：{remoteName}/{remoteBranchName}"
                        : $"本地跟踪缓存 '{remoteName}/{remoteBranchName}' 的来源或提交与确认状态不一致，已保留",
                });

                if (pruned)
                    FinalizeJournalSuccess(journalStore, record, capturingLog, $"已确认仅清理本地失效跟踪缓存 '{remoteName}/{remoteBranchName}'（服务器此前已无该分支）。");
                else
                    FinalizeJournalFailure(journalStore, record, capturingLog, $"未能清理本地跟踪缓存 '{remoteName}/{remoteBranchName}'。", string.Empty);

                return new Models.BranchDeleteOutcome
                {
                    OperationId = operationId,
                    BranchName = remoteBranchName,
                    RemoteName = remoteName,
                    RemoteBranchName = remoteBranchName,
                    LocalTrackingCachePruned = pruned,
                    Succeeded = pruned,
                    Summary = record.StepSummary,
                    StepResults = steps,
                    OperationRecord = record,
                };
            }

            // Create local backup ref if target remote commit exists in local object database
            var createdBackupRef = string.Empty;
            if (await CommitObjectExistsLocallyAsync(repoPath, confirmedExpectedSha, cancellationToken).ConfigureAwait(false))
            {
                var backupRes = await BackupRefs.CreateAsync(repoPath, operationId, confirmedExpectedSha, capturingLog, cancellationToken)
                    .ConfigureAwait(false);
                if (backupRes.Succeeded)
                {
                    createdBackupRef = backupRes.BackupRefName;
                    record.BackupRefName = createdBackupRef;
                    TrySaveJournal(journalStore, record);
                    steps.Add(new Models.BranchDeleteStepResult
                    {
                        Step = Models.BranchDeleteStepKind.BackupRefCreate,
                        Succeeded = true,
                        Label = $"已保存本地恢复引用：{createdBackupRef}（指向 {ShortSha(confirmedExpectedSha)}）",
                    });
                }
            }

            var remoteDelete = await ExecuteRemoteLeaseDeleteAsync(
                repoPath,
                remoteName,
                remoteBranchName,
                confirmedExpectedSha,
                options.ConfirmedPushUrl ?? preflight.RemoteProbe?.PushUrl,
                capturingLog,
                cancellationToken).ConfigureAwait(false);

            if (!remoteDelete.Succeeded)
            {
                if (!string.IsNullOrEmpty(createdBackupRef))
                {
                    await BackupRefs.DeleteBackupRefAsync(repoPath, createdBackupRef, confirmedExpectedSha, capturingLog, cancellationToken)
                        .ConfigureAwait(false);
                    record.BackupRefName = string.Empty;
                }

                steps.Add(new Models.BranchDeleteStepResult
                {
                    Step = Models.BranchDeleteStepKind.RemoteBranchDelete,
                    Succeeded = false,
                    Label = $"远端分支 '{remoteName}/{remoteBranchName}' 未删除",
                    Detail = remoteDelete.UserMessage,
                });

                FinalizeJournalFailure(journalStore, record, capturingLog, remoteDelete.UserMessage, remoteDelete.RedactedDetail);

                return new Models.BranchDeleteOutcome
                {
                    OperationId = operationId,
                    BranchName = remoteBranchName,
                    RemoteName = remoteName,
                    RemoteBranchName = remoteBranchName,
                    ExpectedRemoteTipSha = confirmedExpectedSha,
                    RejectedByRemoteLease = remoteDelete.RejectedByLease,
                    CollaboratorCommitsPreserved = remoteDelete.RejectedByLease,
                    Succeeded = false,
                    Summary = remoteDelete.UserMessage,
                    DetailMessage = remoteDelete.RedactedDetail,
                    StepResults = steps,
                    OperationRecord = record,
                };
            }

            var trackingPruned = await TryDeleteLocalRemoteTrackingRefAsync(
                repoPath,
                remoteName,
                remoteBranchName,
                options.ConfirmedPushUrl ?? preflight.RemoteProbe?.PushUrl,
                preflight.LocalTrackingTipSha,
                capturingLog,
                cancellationToken).ConfigureAwait(false);

            steps.Add(new Models.BranchDeleteStepResult
            {
                Step = Models.BranchDeleteStepKind.RemoteBranchDelete,
                Succeeded = true,
                Label = $"已按预期提交 {ShortSha(confirmedExpectedSha)} 安全删除远端分支 '{remoteName}/{remoteBranchName}'（本地同名分支保留不变）",
            });

            var okSummary = $"已安全删除远端分支 '{remoteName}/{remoteBranchName}'，本地分支保持不变。";
            FinalizeJournalSuccess(journalStore, record, capturingLog, okSummary);

            return new Models.BranchDeleteOutcome
            {
                OperationId = operationId,
                BranchName = remoteBranchName,
                RemoteName = remoteName,
                RemoteBranchName = remoteBranchName,
                ExpectedRemoteTipSha = confirmedExpectedSha,
                BackupRefName = createdBackupRef,
                LocalBackupSha = string.IsNullOrEmpty(createdBackupRef) ? string.Empty : confirmedExpectedSha,
                RemoteBranchDeleted = true,
                LocalTrackingCachePruned = trackingPruned,
                Succeeded = true,
                Summary = okSummary,
                StepResults = steps,
                OperationRecord = record,
            };
        }

        public static async Task<Models.BatchBranchDeleteOutcome> DeleteMultipleBranchesAsync(
            string repoPath,
            IReadOnlyList<Models.Branch> branches,
            bool isLocal,
            bool forceLocal = false,
            Models.OperationJournalStore journalStore = null,
            Models.ICommandLog outerLog = null,
            CancellationToken cancellationToken = default)
        {
            var outcomes = new List<Models.BranchDeleteOutcome>();
            if (branches == null || branches.Count == 0)
            {
                return new Models.BatchBranchDeleteOutcome
                {
                    Summary = "没有选中要删除的分支。",
                    Items = outcomes,
                };
            }

            foreach (var branch in branches)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (isLocal)
                {
                    var preflight = await InspectLocalBranchAsync(repoPath, branch.Name, probeLinkedRemote: false, cancellationToken)
                        .ConfigureAwait(false);
                    var outcome = await DeleteLocalBranchAsync(
                        repoPath,
                        branch.Name,
                        new Models.BranchDeleteOptions
                        {
                            Preflight = preflight,
                            ForceDeleteLocal = forceLocal,
                            DeleteLinkedUpstreamRemote = false,
                        },
                        journalStore,
                        outerLog,
                        cancellationToken).ConfigureAwait(false);
                    outcomes.Add(outcome);
                }
                else
                {
                    var preflight = await InspectRemoteBranchAsync(repoPath, branch.Remote, branch.Name, queryServer: true, cancellationToken)
                        .ConfigureAwait(false);
                    var outcome = await DeleteRemoteBranchAsync(
                        repoPath,
                        branch.Remote,
                        branch.Name,
                        new Models.BranchDeleteOptions
                        {
                            Preflight = preflight,
                            PruneStaleTrackingCacheWhenRemoteAbsent = false,
                        },
                        journalStore,
                        outerLog,
                        cancellationToken).ConfigureAwait(false);
                    outcomes.Add(outcome);
                }
            }

            var succeededCount = outcomes.Count(o => o.Succeeded);
            var skippedCount = outcomes.Count(o => !o.Succeeded && (o.BlockedByProtection || o.RequiresForceLocalConfirmation || o.RequiresRemoteCachePruneConfirmation));
            var failedCount = outcomes.Count - succeededCount - skippedCount;
            var summary = $"批量删除完成：成功 {succeededCount} 项，受保护/需确认跳过 {skippedCount} 项，失败 {failedCount} 项。";

            return new Models.BatchBranchDeleteOutcome
            {
                SucceededCount = succeededCount,
                FailedCount = failedCount,
                SkippedCount = skippedCount,
                Summary = summary,
                Items = outcomes,
            };
        }

        public static async Task<Models.GitBackupRestoreResult> RestoreDeletedLocalBranchAsync(
            string repoPath,
            string branchName,
            string backupRefName,
            string operationId = null,
            Models.OperationJournalStore journalStore = null,
            Models.ICommandLog log = null,
            CancellationToken cancellationToken = default)
        {
            var result = await BackupRefs.RestoreBranchFromBackupAsync(repoPath, branchName, backupRefName, log, cancellationToken)
                .ConfigureAwait(false);

            if (result.Succeeded && !string.IsNullOrWhiteSpace(operationId) && journalStore != null)
            {
                await OperationJournalService.VerifyRecordStateAsync(
                    repoPath,
                    operationId,
                    journalStore: journalStore,
                    acknowledgeResolved: true,
                    cancellationToken: cancellationToken).ConfigureAwait(false);
            }

            return result;
        }

        internal static async Task<string> ValidateDeletionPolicyAsync(
            string repoPath,
            string branchName,
            bool deleteLinkedRemote,
            Models.BranchDeletePreflight preflight,
            string confirmedPushUrl,
            CancellationToken cancellationToken)
        {
            var protection = await BranchProtectionPolicy.InspectAsync(repoPath, branchName, cancellationToken).ConfigureAwait(false);
            if (protection.IsEffectivelyProtected)
                return protection.Summary;
            if (!deleteLinkedRemote)
                return string.Empty;

            var remoteProtection = await BranchProtectionPolicy.InspectAsync(repoPath, preflight.RemoteBranchName, cancellationToken).ConfigureAwait(false);
            if (remoteProtection.IsEffectivelyProtected)
                return remoteProtection.Summary;
            return await RemotePushTarget.VerifyAsync(repoPath, preflight.RemoteName,
                confirmedPushUrl ?? preflight.RemoteProbe?.PushUrl, cancellationToken).ConfigureAwait(false);
        }

        private static async Task<(bool Succeeded, bool RejectedByLease, string UserMessage, string RedactedDetail)> ExecuteRemoteLeaseDeleteAsync(
            string repoPath,
            string remoteName,
            string remoteBranchName,
            string expectedTipSha,
            string confirmedPushUrl,
            Models.ICommandLog log,
            CancellationToken cancellationToken)
        {
            var policyError = await RemotePushTarget.VerifyAsync(repoPath, remoteName, confirmedPushUrl, cancellationToken).ConfigureAwait(false);
            var protection = await BranchProtectionPolicy.InspectAsync(repoPath, remoteBranchName, cancellationToken).ConfigureAwait(false);
            if (protection.IsEffectivelyProtected)
                policyError = protection.Summary;
            if (!string.IsNullOrEmpty(policyError))
                return (false, false, policyError, policyError);

            var remotes = await new QueryRemotes(repoPath).GetResultAsync().ConfigureAwait(false);
            var remote = remotes.Find(r => string.Equals(r.Name, remoteName, StringComparison.Ordinal));
            var refspec = $":refs/heads/{remoteBranchName}";
            var leaseArg = $"--force-with-lease=refs/heads/{remoteBranchName}:{expectedTipSha}";
            var cmd = new RemoteGitCommand(
                repoPath,
                RemotePushTarget.BuildPushArguments(confirmedPushUrl, $"--progress --no-follow-tags {CommandArgumentHelper.Quote(leaseArg)}", refspec),
                remote?.PrivateSSHKey ?? string.Empty)
            {
                CancellationToken = cancellationToken,
            }.Use(log);

            var res = await cmd.ExecuteAsync().ConfigureAwait(false);
            var redactedDetail = Models.GitConfigSnapshot.RedactText(FirstNonEmpty(res.StdErr, res.StdOut));
            if (res.IsSuccess)
                return (true, false, string.Empty, redactedDetail);

            var isLeaseRejected = redactedDetail.Contains("stale info", StringComparison.OrdinalIgnoreCase) ||
                                  redactedDetail.Contains("[rejected]", StringComparison.OrdinalIgnoreCase);
            var userMsg = isLeaseRejected
                ? $"远端分支 '{remoteName}/{remoteBranchName}' 在确认后已有新提交（显式 lease 校验拒绝覆盖），已保留协作者提交。"
                : FirstNonEmpty(redactedDetail, $"删除远端分支 '{remoteName}/{remoteBranchName}' 被服务器或网络拒绝。");

            return (false, isLeaseRejected, userMsg, redactedDetail);
        }

        private static async Task<bool> TryDeleteLocalRemoteTrackingRefAsync(
            string repoPath,
            string remoteName,
            string remoteBranchName,
            string pushUrl,
            string expectedTrackingSha,
            Models.ICommandLog log,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrEmpty(expectedTrackingSha) ||
                !await RemotePushTarget.CanUpdateTrackingRefAsync(repoPath, remoteName, pushUrl, cancellationToken).ConfigureAwait(false))
                return false;

            var trackingTarget = $"refs/remotes/{remoteName}/{remoteBranchName}";
            var res = await Command.RunGitDirectAsync(
                repoPath,
                cancellationToken,
                "update-ref", "-d", trackingTarget, expectedTrackingSha).ConfigureAwait(false);
            log?.AppendLine($"$ git update-ref -d {trackingTarget} {expectedTrackingSha}");
            AppendRedactedCommandOutput(log, res);
            return res.IsSuccess;
        }

        private static async Task<(bool HasMultipleRemotes, bool HasConfiguredUpstream, string RemoteName, string RemoteBranchName, string FullTrackingRef, string RedactedRemoteUrl, string LocalTrackingTipSha)> ResolveConfiguredUpstreamAsync(
            string repoPath,
            string localBranchName,
            CancellationToken cancellationToken)
        {
            var remotes = await new QueryRemotes(repoPath).GetResultAsync().ConfigureAwait(false);
            var hasMultipleRemotes = remotes.Count > 1;

            var remoteCfg = await Command.RunGitDirectAsync(repoPath, cancellationToken, "config", "--get", "--", $"branch.{localBranchName}.remote")
                .ConfigureAwait(false);
            var mergeCfg = await Command.RunGitDirectAsync(repoPath, cancellationToken, "config", "--get", "--", $"branch.{localBranchName}.merge")
                .ConfigureAwait(false);

            var remoteName = remoteCfg.IsSuccess ? remoteCfg.StdOut.Trim() : string.Empty;
            var mergeRef = mergeCfg.IsSuccess ? mergeCfg.StdOut.Trim() : string.Empty;

            if (string.IsNullOrEmpty(remoteName) || string.IsNullOrEmpty(mergeRef) || remoteName == ".")
                return (hasMultipleRemotes, false, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty);

            string remoteBranchName;
            if (mergeRef.StartsWith("refs/heads/", StringComparison.Ordinal))
                remoteBranchName = mergeRef.Substring("refs/heads/".Length);
            else if (mergeRef.StartsWith($"refs/remotes/{remoteName}/", StringComparison.Ordinal))
                remoteBranchName = mergeRef.Substring($"refs/remotes/{remoteName}/".Length);
            else
                remoteBranchName = mergeRef;

            if (string.IsNullOrEmpty(remoteBranchName) || IsOptionLike(remoteBranchName))
                return (hasMultipleRemotes, false, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty);

            var matchedRemote = remotes.Find(r => string.Equals(r.Name, remoteName, StringComparison.Ordinal));
            var rawUrl = matchedRemote != null
                ? (matchedRemote.PushURL ?? matchedRemote.URL)
                : await ReadRemoteUrlAsync(repoPath, remoteName, cancellationToken).ConfigureAwait(false);
            var redactedUrl = Models.GitConfigSnapshot.RedactText(rawUrl);

            var trackingRef = $"refs/remotes/{remoteName}/{remoteBranchName}";
            var trackingSha = await ResolveRefShaAsync(repoPath, trackingRef, cancellationToken).ConfigureAwait(false);

            return (hasMultipleRemotes, true, remoteName, remoteBranchName, trackingRef, redactedUrl, trackingSha);
        }

        private static async Task<(bool IsCurrentWorktree, bool IsCheckedOutElsewhere, string WorktreePath)> InspectAttachedWorktreeAsync(
            string repoPath,
            string fullBranchRef,
            CancellationToken cancellationToken)
        {
            var worktrees = await new Worktree(repoPath).ReadAllAsync().ConfigureAwait(false);
            var normalizedRepo = NormalizeDirectoryPath(repoPath);
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

            foreach (var wt in worktrees)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (wt.IsBare || !string.Equals(wt.Branch, fullBranchRef, StringComparison.Ordinal))
                    continue;

                if (string.Equals(NormalizeDirectoryPath(wt.FullPath), normalizedRepo, comparison))
                    return (true, false, wt.FullPath);

                return (false, true, wt.FullPath);
            }

            return (false, false, string.Empty);
        }

        private static async Task<(bool IsMerged, int CommitCount, int ChangedFilesCount, List<Models.UnmergedCommitSummary> Commits)> InspectUnmergedVsHeadAsync(
            string repoPath,
            string fullBranchRef,
            CancellationToken cancellationToken)
        {
            var headSha = await ResolveRefShaAsync(repoPath, "HEAD", cancellationToken).ConfigureAwait(false);
            var branchSha = await ResolveRefShaAsync(repoPath, fullBranchRef, cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrEmpty(headSha) || string.IsNullOrEmpty(branchSha))
                return (true, 0, 0, []);

            var ancestorRes = await Command.RunGitDirectAsync(
                repoPath,
                cancellationToken,
                "merge-base",
                "--is-ancestor",
                fullBranchRef,
                "HEAD").ConfigureAwait(false);
            if (ancestorRes.IsSuccess)
                return (true, 0, 0, []);

            var range = $"HEAD..{fullBranchRef}";
            var countRes = await Command.RunGitDirectAsync(repoPath, cancellationToken, "rev-list", "--count", range)
                .ConfigureAwait(false);
            var commitCount = countRes.IsSuccess && int.TryParse(countRes.StdOut.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedCount)
                ? parsedCount
                : 0;

            var logRes = await Command.RunGitDirectAsync(
                repoPath,
                cancellationToken,
                "log",
                "-n",
                "10",
                "--format=%H%x00%s",
                range).ConfigureAwait(false);
            var commits = new List<Models.UnmergedCommitSummary>();
            if (logRes.IsSuccess && !string.IsNullOrWhiteSpace(logRes.StdOut))
            {
                foreach (var line in logRes.StdOut.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
                {
                    var parts = line.Split('\0', 2);
                    if (parts.Length == 2)
                    {
                        commits.Add(new Models.UnmergedCommitSummary
                        {
                            Sha = parts[0].Trim(),
                            Subject = parts[1].Trim(),
                        });
                    }
                }
            }

            var diffRes = await Command.RunGitDirectAsync(
                repoPath,
                cancellationToken,
                "diff",
                "--name-only",
                "-z",
                $"HEAD...{fullBranchRef}").ConfigureAwait(false);
            if (!diffRes.IsSuccess)
            {
                diffRes = await Command.RunGitDirectAsync(
                    repoPath,
                    cancellationToken,
                    "diff",
                    "--name-only",
                    "-z",
                    "HEAD",
                    fullBranchRef).ConfigureAwait(false);
            }

            var changedFilesCount = diffRes.IsSuccess && !string.IsNullOrEmpty(diffRes.StdOut)
                ? diffRes.StdOut.Split('\0', StringSplitOptions.RemoveEmptyEntries).Length
                : 0;

            return (false, commitCount, changedFilesCount, commits);
        }

        internal static async Task<string> ReadCurrentBranchNameAsync(string repoPath, CancellationToken cancellationToken)
        {
            var res = await Command.RunGitDirectAsync(repoPath, cancellationToken, "symbolic-ref", "--quiet", "--short", "HEAD")
                .ConfigureAwait(false);
            return res.IsSuccess ? res.StdOut.Trim() : string.Empty;
        }

        private static async Task<string> ReadRemoteUrlAsync(string repoPath, string remoteName, CancellationToken cancellationToken)
        {
            var res = await Command.RunGitDirectAsync(repoPath, cancellationToken, "remote", "get-url", "--", remoteName)
                .ConfigureAwait(false);
            return res.IsSuccess ? res.StdOut.Trim() : string.Empty;
        }

        internal static async Task<string> ResolveRefShaAsync(string repoPath, string fullRef, CancellationToken cancellationToken)
        {
            var res = await Command.RunGitDirectAsync(repoPath, cancellationToken, "rev-parse", "--verify", "--quiet", fullRef)
                .ConfigureAwait(false);
            return res.IsSuccess ? res.StdOut.Trim() : string.Empty;
        }

        private static async Task<bool> CommitObjectExistsLocallyAsync(string repoPath, string sha, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(sha))
                return false;

            var res = await Command.RunGitDirectAsync(repoPath, cancellationToken, "cat-file", "-e", $"{sha}^{{commit}}")
                .ConfigureAwait(false);
            return res.IsSuccess;
        }

        private static string ParseLsRemoteExactTip(string stdout, string fullRef)
        {
            if (string.IsNullOrWhiteSpace(stdout))
                return string.Empty;

            foreach (var line in stdout.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
            {
                var idx = line.IndexOf('\t');
                if (idx <= 0)
                    continue;

                var sha = line[..idx].Trim();
                var refName = line[(idx + 1)..].Trim();
                if (string.Equals(refName, fullRef, StringComparison.Ordinal))
                    return sha;
            }

            return string.Empty;
        }

        internal static void FinalizeJournalSuccess(
            Models.OperationJournalStore journalStore,
            Models.GitOperationRecord record,
            CapturingCommandLog log,
            string summary)
        {
            record.Stage = Models.GitOperationStage.Succeeded;
            record.StepSummary = Models.GitConfigSnapshot.RedactText(summary);
            record.CommandSummary = log.GetCombinedText();
            record.CompletedAtUtc = DateTime.UtcNow;
            record.IsUnfinishedOnStartup = false;
            TrySaveJournal(journalStore, record);
        }

        internal static void FinalizeJournalPartialSuccess(
            Models.OperationJournalStore journalStore,
            Models.GitOperationRecord record,
            CapturingCommandLog log,
            string summary,
            string diagnosticDetail)
        {
            record.Stage = Models.GitOperationStage.PartialSuccess;
            record.StepSummary = Models.GitConfigSnapshot.RedactText(summary);
            record.CommandSummary = log.GetCombinedText();
            record.DiagnosticDetail = Models.GitConfigSnapshot.RedactText(diagnosticDetail ?? string.Empty);
            record.IsUnfinishedOnStartup = false;
            TrySaveJournal(journalStore, record);
        }

        internal static void FinalizeJournalFailure(
            Models.OperationJournalStore journalStore,
            Models.GitOperationRecord record,
            CapturingCommandLog log,
            string summary,
            string diagnosticDetail)
        {
            record.Stage = Models.GitOperationStage.Failed;
            record.StepSummary = Models.GitConfigSnapshot.RedactText(summary);
            record.CommandSummary = log.GetCombinedText();
            record.DiagnosticDetail = Models.GitConfigSnapshot.RedactText(diagnosticDetail ?? string.Empty);
            record.CompletedAtUtc = DateTime.UtcNow;
            record.IsUnfinishedOnStartup = false;
            TrySaveJournal(journalStore, record);
        }

        internal static void TrySaveJournal(Models.OperationJournalStore journalStore, Models.GitOperationRecord record)
        {
            journalStore?.AppendOrUpdate(record);
        }

        internal static void AppendRedactedCommandOutput(Models.ICommandLog log, Command.Result res)
        {
            if (log == null || res == null)
                return;

            if (!string.IsNullOrWhiteSpace(res.StdOut))
                log.AppendLine(Models.GitConfigSnapshot.RedactText(res.StdOut.TrimEnd()));
            if (!string.IsNullOrWhiteSpace(res.StdErr))
                log.AppendLine(Models.GitConfigSnapshot.RedactText(res.StdErr.TrimEnd()));
        }

        internal static string NormalizeDirectoryPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return string.Empty;

            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path).Replace('\\', '/'));
        }

        internal static bool IsOptionLike(string value)
        {
            return value.TrimStart().StartsWith('-');
        }

        internal static string ShortSha(string sha)
        {
            return string.IsNullOrEmpty(sha) ? string.Empty : (sha.Length > 8 ? sha[..8] : sha);
        }

        internal static string FirstNonEmpty(params string[] candidates)
        {
            foreach (var c in candidates)
            {
                if (!string.IsNullOrWhiteSpace(c))
                    return c.Trim();
            }

            return string.Empty;
        }

        private sealed class RemoteGitCommand : Command
        {
            public RemoteGitCommand(string repoPath, string args, string sshKey)
            {
                WorkingDirectory = repoPath;
                Context = repoPath;
                SSHKey = sshKey ?? string.Empty;
                RaiseError = false;
                Args = args;
            }

            public async Task<Result> ExecuteAsync()
            {
                Log?.AppendLine($"$ git {Models.GitConfigSnapshot.RedactText(Args)}");
                var res = await ReadToEndAsync().ConfigureAwait(false);
                AppendRedactedCommandOutput(Log, res);
                return RedactResult(res);
            }
        }
    }
}
