using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace SourceGit.Commands
{
    public static partial class GitSyncService
    {
        public static async Task<Models.AmendLastCommitPreflight> InspectAmendLastCommitAsync(
            string repoPath,
            CancellationToken cancellationToken = default)
        {
            var (gitDir, commonDir) = await Models.RepositoryWriteGuard
                .ResolveGitDirectoriesAsync(repoPath, cancellationToken)
                .ConfigureAwait(false);

            var snapshot = await QueryStagedSnapshot.CaptureAsync(repoPath, cancellationToken).ConfigureAwait(false);
            var inProgress = CaptureOperationSnapshot.DetectInProgressKind(snapshot.GitDir);

            string blockReason = string.Empty;
            if (inProgress != Models.GitInProgressKind.None)
                blockReason = $"当前仓库正在进行 {inProgress} 操作，请先在对应入口完成或中止后再执行补充提交。";
            else if (snapshot.HasConflicts)
                blockReason = "当前工作区存在未解决的冲突文件，请先解决冲突并暂存后再执行补充提交。";
            else if (snapshot.IsUnborn || string.IsNullOrEmpty(snapshot.HeadOid))
                blockReason = "当前分支尚未创建任何提交（Unborn HEAD），没有可补充的历史提交。";

            var identity = await new Config(repoPath).ReadCommitIdentityAsync().ConfigureAwait(false);
            var (signingStatus, signingSummary) = await InspectAmendSigningAsync(repoPath, cancellationToken).ConfigureAwait(false);
            var (hasHooks, hookSummary) = await InspectCommitHooksAsync(repoPath, commonDir, cancellationToken).ConfigureAwait(false);

            if (!string.IsNullOrEmpty(blockReason))
            {
                return new Models.AmendLastCommitPreflight
                {
                    RepoPath = repoPath,
                    CanonicalGitDir = gitDir,
                    CanonicalCommonDir = commonDir,
                    BlockReason = blockReason,
                    CurrentBranchName = snapshot.CurrentBranchName,
                    IsDetachedHead = snapshot.IsDetachedHead,
                    Snapshot = snapshot,
                    Identity = identity,
                    SigningStatus = signingStatus,
                    SigningSummary = signingSummary,
                    HasCommitHooks = hasHooks,
                    HookSummary = hookSummary,
                    RemotePublishState = Models.AmendRemotePublishState.UnknownRemoteState,
                };
            }

            var headSha = snapshot.HeadOid;
            var headCommit = await new QuerySingleCommit(repoPath, headSha).GetResultAsync().ConfigureAwait(false);
            var headFullMessage = await new QueryCommitFullMessage(repoPath, headSha).GetResultAsync().ConfigureAwait(false);

            var allLocalChanges = await new QueryLocalChanges(repoPath, includeUntracked: false, noOptionalLocks: true)
                .WithCancellation(cancellationToken)
                .GetResultAsync()
                .ConfigureAwait(false);
            var newlyStaged = allLocalChanges
                .Where(c => c.Index != Models.ChangeState.None)
                .ToList();
            newlyStaged.Sort((l, r) => Models.NumericSort.Compare(l.Path, r.Path));

            var resultingChanges = new QueryStagedChangesWithAmend(repoPath).GetResult();
            resultingChanges.Sort((l, r) => Models.NumericSort.Compare(l.Path, r.Path));

            var publishInfo = await InspectAmendPublishStateAsync(repoPath, snapshot, identity, cancellationToken)
                .ConfigureAwait(false);

            return new Models.AmendLastCommitPreflight
            {
                RepoPath = repoPath,
                CanonicalGitDir = gitDir,
                CanonicalCommonDir = commonDir,
                CurrentBranchName = snapshot.CurrentBranchName,
                IsDetachedHead = snapshot.IsDetachedHead,
                HeadSha = headSha,
                HeadShortSha = ShortShaForSummary(headSha),
                HeadSubject = headCommit?.Subject ?? FirstLine(headFullMessage),
                HeadFullMessage = headFullMessage,
                HeadAuthorDisplay = FormatAuthor(headCommit?.Author),
                HeadParentCount = headCommit?.Parents?.Count ?? 0,
                Snapshot = snapshot,
                NewlyStagedChanges = newlyStaged,
                ResultingAmendChanges = resultingChanges,
                Identity = identity,
                SigningStatus = signingStatus,
                SigningSummary = signingSummary,
                HasCommitHooks = hasHooks,
                HookSummary = hookSummary,
                RemotePublishState = publishInfo.State,
                RemotePublishSummary = publishInfo.Summary,
                RemotePublishDetail = publishInfo.Detail,
                RemoteName = publishInfo.RemoteName,
                RemoteBranchName = publishInfo.RemoteBranchName,
                TrackingRefName = publishInfo.TrackingRefName,
                LocalTrackingTipSha = publishInfo.LocalTrackingTipSha,
                LiveRemoteTipSha = publishInfo.LiveRemoteTipSha,
                ConfirmedPushUrl = publishInfo.ConfirmedPushUrl,
                RedactedRemoteUrl = publishInfo.RedactedRemoteUrl,
                HasRemotes = publishInfo.HasRemotes,
                RemoteMovedAheadOfHead = publishInfo.RemoteMovedAhead,
                RemoteCommitsAheadOfHead = publishInfo.CommitsAhead,
                HasOtherAuthorsOnRemote = publishInfo.HasOtherAuthors,
                RemoteAuthorsSummary = publishInfo.AuthorsSummary,
                LocalBranchProtection = publishInfo.LocalProtection,
                RemoteBranchProtection = publishInfo.RemoteProtection,
            };
        }

        public static async Task<Models.AmendLastCommitOutcome> ExecuteAmendLastCommitAsync(
            string repoPath,
            Models.AmendLastCommitExecutionOptions options,
            Models.ICommandLog log = null,
            CancellationToken cancellationToken = default,
            Models.OperationJournalStore journalStore = null)
        {
            ArgumentNullException.ThrowIfNull(options);
            var pre = options.Preflight ?? throw new ArgumentNullException(nameof(options.Preflight));

            if (pre.HasHardBlocker)
            {
                return new Models.AmendLastCommitOutcome
                {
                    Succeeded = false,
                    Summary = pre.BlockReason,
                };
            }

            if (string.IsNullOrWhiteSpace(options.Message))
            {
                return new Models.AmendLastCommitOutcome
                {
                    Succeeded = false,
                    Summary = "提交说明不能为空。",
                };
            }

            if (pre.RemotePublishState is Models.AmendRemotePublishState.UnknownRemoteState or Models.AmendRemotePublishState.UnfetchedOrStaleRemote)
            {
                return new Models.AmendLastCommitOutcome
                {
                    Succeeded = false,
                    BlockedByRemoteState = true,
                    OriginalHeadSha = pre.HeadSha,
                    Summary = pre.RemotePublishSummary,
                    DetailMessage = pre.RemotePublishDetail,
                };
            }

            if (pre.RemotePublishState == Models.AmendRemotePublishState.DetachedHead && pre.HasRemotes)
            {
                return new Models.AmendLastCommitOutcome
                {
                    Succeeded = false,
                    BlockedByRemoteState = true,
                    OriginalHeadSha = pre.HeadSha,
                    Summary = pre.RemotePublishSummary,
                    DetailMessage = pre.RemotePublishDetail,
                };
            }

            if (pre.RemotePublishState == Models.AmendRemotePublishState.AlreadyPushed)
            {
                if (!options.RewritePushedRemoteWithLease)
                {
                    return new Models.AmendLastCommitOutcome
                    {
                        Succeeded = false,
                        BlockedByRemoteState = true,
                        OriginalHeadSha = pre.HeadSha,
                        Summary = "该提交已推送至远端，默认引导改为创建新提交；如需改写已推送提交，必须显式确认带 --force-with-lease 改写远端。",
                    };
                }

                if (!pre.CanOptInPushedRewrite)
                {
                    var protectMsg = pre.BlockedByBranchProtection
                        ? (pre.RemoteBranchProtection?.IsEffectivelyProtected == true
                            ? pre.RemoteBranchProtection.Summary
                            : pre.LocalBranchProtection?.Summary ?? "受保护分支已锁定，禁止改写远端提交历史。")
                        : pre.RemotePublishSummary;
                    return new Models.AmendLastCommitOutcome
                    {
                        Succeeded = false,
                        BlockedByProtection = pre.BlockedByBranchProtection,
                        BlockedByRemoteState = !pre.BlockedByBranchProtection,
                        OriginalHeadSha = pre.HeadSha,
                        Summary = protectMsg,
                        DetailMessage = pre.RemotePublishDetail,
                    };
                }

                var localProt = await BranchProtectionPolicy.InspectAsync(repoPath, pre.CurrentBranchName, cancellationToken)
                    .ConfigureAwait(false);
                var remoteProt = await BranchProtectionPolicy.InspectAsync(repoPath, pre.RemoteBranchName, cancellationToken)
                    .ConfigureAwait(false);
                if (localProt.IsEffectivelyProtected || remoteProt.IsEffectivelyProtected)
                {
                    return new Models.AmendLastCommitOutcome
                    {
                        Succeeded = false,
                        BlockedByProtection = true,
                        OriginalHeadSha = pre.HeadSha,
                        Summary = remoteProt.IsEffectivelyProtected ? remoteProt.Summary : localProt.Summary,
                    };
                }

                var urlErr = await RemotePushTarget.VerifyAsync(repoPath, pre.RemoteName, pre.ConfirmedPushUrl, cancellationToken)
                    .ConfigureAwait(false);
                if (!string.IsNullOrEmpty(urlErr))
                {
                    return new Models.AmendLastCommitOutcome
                    {
                        Succeeded = false,
                        BlockedByRemoteState = true,
                        OriginalHeadSha = pre.HeadSha,
                        Summary = urlErr,
                    };
                }
            }

            var operationId = $"amend-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid():N}"[..29];
            var backupRes = await BackupRefs.CreateAsync(repoPath, operationId, pre.HeadSha, log, cancellationToken)
                .ConfigureAwait(false);
            if (!backupRes.Succeeded)
            {
                return new Models.AmendLastCommitOutcome
                {
                    Succeeded = false,
                    OperationId = operationId,
                    OriginalHeadSha = pre.HeadSha,
                    Summary = backupRes.Summary,
                };
            }

            Models.CommitExecutionResult commitRes;
            try
            {
                commitRes = await CommitAsync(
                    repoPath,
                    new Models.CommitExecutionOptions
                    {
                        Message = options.Message,
                        SignOff = options.SignOff,
                        NoVerify = options.NoVerify,
                        Amend = true,
                        ResetAuthor = options.ResetAuthor,
                        AllowEmpty = false,
                        CheckIdentity = true,
                        ExpectedSnapshot = pre.Snapshot,
                    },
                    log,
                    cancellationToken,
                    journalStore).ConfigureAwait(false);
            }
            catch
            {
                await BackupRefs.DeleteBackupRefAsync(repoPath, backupRes.BackupRefName, pre.HeadSha, log, CancellationToken.None)
                    .ConfigureAwait(false);
                throw;
            }

            if (!commitRes.Succeeded)
            {
                await BackupRefs.DeleteBackupRefAsync(repoPath, backupRes.BackupRefName, pre.HeadSha, log, CancellationToken.None)
                    .ConfigureAwait(false);
                return new Models.AmendLastCommitOutcome
                {
                    Succeeded = false,
                    AmendSucceeded = false,
                    BlockedBySnapshot = commitRes.FailureKind == Models.CommitFailureKind.StagedSnapshotChanged,
                    OperationId = operationId,
                    OriginalHeadSha = pre.HeadSha,
                    Summary = commitRes.Summary,
                    DetailMessage = commitRes.DiagnosticDetail,
                };
            }

            var newHeadSha = commitRes.CommitSha;
            if (!options.RewritePushedRemoteWithLease)
            {
                return new Models.AmendLastCommitOutcome
                {
                    Succeeded = true,
                    AmendSucceeded = true,
                    PushAttempted = false,
                    OperationId = operationId,
                    OriginalHeadSha = pre.HeadSha,
                    NewHeadSha = newHeadSha,
                    BackupRefName = backupRes.BackupRefName,
                    Summary = $"已补充修订上一次提交为 {commitRes.ShortSha}（原提交恢复引用 {backupRes.BackupRefName} 保留 30 天）。",
                    DetailMessage = commitRes.DiagnosticDetail,
                };
            }

            var remotes = await new QueryRemotes(repoPath).GetResultAsync().ConfigureAwait(false);
            var matchedRemote = remotes.Find(r => string.Equals(r.Name, pre.RemoteName, StringComparison.Ordinal));
            var pendingPushOpts = new Models.PushExecutionOptions
            {
                LocalBranch = pre.CurrentBranchName,
                SourceRevisionSha = newHeadSha,
                Remote = matchedRemote,
                RemoteBranch = pre.RemoteBranchName,
                SetUpstream = false,
                NoVerify = options.NoVerify,
                ExpectedPushUrl = pre.ConfirmedPushUrl,
                ForceWithLeaseExpectSha = pre.LiveRemoteTipSha,
                BackupRefName = backupRes.BackupRefName,
            };

            if (journalStore != null)
            {
                await TryRecordOperationAsync(
                    repoPath,
                    journalStore,
                    Models.GitOperationKind.RewriteRemoteHistory,
                    $"修订并带租约同步远端 '{pre.RemoteName}/{pre.RemoteBranchName}'",
                    Models.GitOperationStage.Executing,
                    $"本地已 Amend 至 {commitRes.ShortSha}，正在通过 --force-with-lease 同步远端。",
                    branchName: pre.CurrentBranchName,
                    remoteName: pre.RemoteName,
                    remoteBranchName: pre.RemoteBranchName,
                    originalTipSha: pre.HeadSha,
                    targetTipSha: newHeadSha,
                    expectedRemoteTipSha: pre.LiveRemoteTipSha,
                    backupRefName: backupRes.BackupRefName,
                    commandSummary: $"git push --force-with-lease=refs/heads/{pre.RemoteBranchName}:{pre.LiveRemoteTipSha}",
                    diagnosticDetail: string.Empty).ConfigureAwait(false);
            }

            var pushRes = await PushSingleBranchAsync(repoPath, pendingPushOpts, log, cancellationToken, journalStore)
                .ConfigureAwait(false);

            var retrySession = new Models.CommitAndPushSession
            {
                RepoPath = repoPath,
                CommitResult = commitRes,
                PushAttempted = true,
                PushResult = pushRes,
                PendingPushOptions = pushRes.Succeeded ? null : pendingPushOpts,
            };

            if (pushRes.Succeeded)
            {
                if (journalStore != null)
                {
                    await TryRecordOperationAsync(
                        repoPath,
                        journalStore,
                        Models.GitOperationKind.RewriteRemoteHistory,
                        $"修订并带租约同步远端 '{pre.RemoteName}/{pre.RemoteBranchName}'",
                        Models.GitOperationStage.Succeeded,
                        pushRes.Summary,
                        branchName: pre.CurrentBranchName,
                        remoteName: pre.RemoteName,
                        remoteBranchName: pre.RemoteBranchName,
                        originalTipSha: pre.HeadSha,
                        targetTipSha: newHeadSha,
                        expectedRemoteTipSha: pre.LiveRemoteTipSha,
                        backupRefName: backupRes.BackupRefName,
                        commandSummary: $"git push --force-with-lease=refs/heads/{pre.RemoteBranchName}:{pre.LiveRemoteTipSha}",
                        diagnosticDetail: pushRes.DiagnosticDetail).ConfigureAwait(false);
                }

                return new Models.AmendLastCommitOutcome
                {
                    Succeeded = true,
                    AmendSucceeded = true,
                    PushAttempted = true,
                    PushSucceeded = true,
                    OperationId = operationId,
                    OriginalHeadSha = pre.HeadSha,
                    NewHeadSha = newHeadSha,
                    BackupRefName = backupRes.BackupRefName,
                    ExpectedRemoteTipSha = pre.LiveRemoteTipSha,
                    RemoteName = pre.RemoteName,
                    RemoteBranchName = pre.RemoteBranchName,
                    Summary = $"本地已补充提交为 {commitRes.ShortSha}，且已通过 --force-with-lease 同步更新远端 '{pre.RemoteName}/{pre.RemoteBranchName}'（原提交恢复引用 {backupRes.BackupRefName} 保留 30 天）。",
                    DetailMessage = pushRes.DiagnosticDetail,
                    RetrySession = retrySession,
                };
            }

            if (journalStore != null)
            {
                await TryRecordOperationAsync(
                    repoPath,
                    journalStore,
                    Models.GitOperationKind.RewriteRemoteHistory,
                    $"修订并带租约同步远端 '{pre.RemoteName}/{pre.RemoteBranchName}'",
                    Models.GitOperationStage.PartialSuccess,
                    pushRes.Summary,
                    branchName: pre.CurrentBranchName,
                    remoteName: pre.RemoteName,
                    remoteBranchName: pre.RemoteBranchName,
                    originalTipSha: pre.HeadSha,
                    targetTipSha: newHeadSha,
                    expectedRemoteTipSha: pre.LiveRemoteTipSha,
                    backupRefName: backupRes.BackupRefName,
                    commandSummary: $"git push --force-with-lease=refs/heads/{pre.RemoteBranchName}:{pre.LiveRemoteTipSha}",
                    diagnosticDetail: pushRes.DiagnosticDetail).ConfigureAwait(false);
            }

            return new Models.AmendLastCommitOutcome
            {
                Succeeded = false,
                AmendSucceeded = true,
                PushAttempted = true,
                PushSucceeded = false,
                IsPartialSuccess = true,
                RejectedByLease = pushRes.RejectedByLease,
                OperationId = operationId,
                OriginalHeadSha = pre.HeadSha,
                NewHeadSha = newHeadSha,
                BackupRefName = backupRes.BackupRefName,
                ExpectedRemoteTipSha = pre.LiveRemoteTipSha,
                RemoteName = pre.RemoteName,
                RemoteBranchName = pre.RemoteBranchName,
                Summary = $"本地 Amend 已成功生成 {commitRes.ShortSha}，但远端推送失败（原提交恢复引用 {backupRes.BackupRefName} 已保留）。请仅重试推送，禁止重复执行 Amend。",
                DetailMessage = string.IsNullOrWhiteSpace(pushRes.DiagnosticDetail) ? pushRes.Summary : pushRes.DiagnosticDetail,
                RetrySession = retrySession,
            };
        }

        private sealed class AmendPublishInspection
        {
            public Models.AmendRemotePublishState State { get; init; } = Models.AmendRemotePublishState.UnknownRemoteState;
            public string Summary { get; init; } = string.Empty;
            public string Detail { get; init; } = string.Empty;
            public string RemoteName { get; init; } = string.Empty;
            public string RemoteBranchName { get; init; } = string.Empty;
            public string TrackingRefName { get; init; } = string.Empty;
            public string LocalTrackingTipSha { get; init; } = string.Empty;
            public string LiveRemoteTipSha { get; init; } = string.Empty;
            public string ConfirmedPushUrl { get; init; } = string.Empty;
            public string RedactedRemoteUrl { get; init; } = string.Empty;
            public bool HasRemotes { get; init; }
            public bool RemoteMovedAhead { get; init; }
            public IReadOnlyList<Models.ResetCommitSummary> CommitsAhead { get; init; } = Array.Empty<Models.ResetCommitSummary>();
            public bool HasOtherAuthors { get; init; }
            public string AuthorsSummary { get; init; } = string.Empty;
            public Models.BranchProtectionStatus LocalProtection { get; init; }
            public Models.BranchProtectionStatus RemoteProtection { get; init; }
        }

        private static async Task<AmendPublishInspection> InspectAmendPublishStateAsync(
            string repoPath,
            Models.StagedSnapshot snapshot,
            Models.CommitIdentity identity,
            CancellationToken cancellationToken)
        {
            var headSha = snapshot.HeadOid;
            var remotes = await new QueryRemotes(repoPath).GetResultAsync().ConfigureAwait(false);
            var containingRemoteRefs = await ReadRemoteRefsContainingCommitAsync(repoPath, headSha, cancellationToken)
                .ConfigureAwait(false);
            var hasRemoteTrackingContainingHead = containingRemoteRefs.Count > 0;

            if (snapshot.IsDetachedHead)
            {
                if (hasRemoteTrackingContainingHead)
                {
                    return new AmendPublishInspection
                    {
                        State = Models.AmendRemotePublishState.AlreadyPushed,
                        HasRemotes = remotes.Count > 0,
                        Summary = $"当前游离 HEAD（{ShortShaForSummary(headSha)}）已包含在远端跟踪引用中（{string.Join("、", containingRemoteRefs.Take(3))}），禁止直接改写；建议改为创建新提交或切换回本地分支。",
                        Detail = $"包含该提交的远端引用：{string.Join(", ", containingRemoteRefs)}",
                    };
                }

                if (remotes.Count > 0)
                {
                    return new AmendPublishInspection
                    {
                        State = Models.AmendRemotePublishState.DetachedHead,
                        HasRemotes = true,
                        Summary = "当前处于游离 HEAD 且仓库配置了远端，无法核实远端分支对应状态；禁止当作未推送直接 Amend，建议先创建本地分支或改为新建提交。",
                    };
                }

                return new AmendPublishInspection
                {
                    State = Models.AmendRemotePublishState.UnpushedLocalOnly,
                    HasRemotes = false,
                    Summary = "当前仓库未配置远端且处于游离 HEAD，可补充修订本次本地提交。",
                };
            }

            var branchName = snapshot.CurrentBranchName;
            var localProtection = await BranchProtectionPolicy.InspectAsync(repoPath, branchName, cancellationToken).ConfigureAwait(false);
            if (remotes.Count == 0 && !hasRemoteTrackingContainingHead)
            {
                return new AmendPublishInspection
                {
                    State = Models.AmendRemotePublishState.UnpushedLocalOnly,
                    HasRemotes = false,
                    LocalProtection = localProtection,
                    Summary = $"当前仓库未配置远端，提交 {ShortShaForSummary(headSha)} 仅存在于本地，可确认后直接补充提交。",
                };
            }

            var endpoint = await HistoryResetService.ResolveRemoteEndpointAsync(
                repoPath, branchName, null, null, cancellationToken).ConfigureAwait(false);

            string targetRemote = endpoint.RemoteName;
            string targetRemoteBranch = endpoint.RemoteBranchName;
            string trackingRef = endpoint.TrackingRefName;
            string localTrackingTip = endpoint.LocalTrackingTipSha;
            string redactedUrl = endpoint.RedactedUrl;

            if (string.IsNullOrEmpty(targetRemote) && remotes.Count > 0)
            {
                var candidate = await FindCandidateRemoteWithoutUpstreamAsync(
                    repoPath, branchName, headSha, remotes, containingRemoteRefs, cancellationToken).ConfigureAwait(false);
                if (candidate != null)
                    return candidate.WithProtection(localProtection);

                return new AmendPublishInspection
                {
                    State = Models.AmendRemotePublishState.UnpushedLocalOnly,
                    HasRemotes = true,
                    LocalProtection = localProtection,
                    Summary = $"已联机核实所有已配置远端均未发布分支 '{branchName}' 的当前提交，可确认后补充提交。",
                };
            }

            var remoteProtection = await BranchProtectionPolicy.InspectAsync(repoPath, targetRemoteBranch, cancellationToken)
                .ConfigureAwait(false);
            var probe = await BranchDeletionService.ProbeRemoteBranchAsync(
                repoPath, targetRemote, targetRemoteBranch, cancellationToken: cancellationToken).ConfigureAwait(false);

            if (!probe.QuerySucceeded)
            {
                return new AmendPublishInspection
                {
                    State = Models.AmendRemotePublishState.UnknownRemoteState,
                    HasRemotes = true,
                    RemoteName = targetRemote,
                    RemoteBranchName = targetRemoteBranch,
                    TrackingRefName = trackingRef,
                    LocalTrackingTipSha = localTrackingTip,
                    RedactedRemoteUrl = !string.IsNullOrEmpty(probe.RedactedRemoteUrl) ? probe.RedactedRemoteUrl : redactedUrl,
                    LocalProtection = localProtection,
                    RemoteProtection = remoteProtection,
                    Summary = $"远端状态未知：无法连接 '{targetRemote}' 核实提交状态（{probe.ErrorSummary}），禁止静默当作未推送直接 Amend。",
                    Detail = probe.ErrorSummary,
                };
            }

            if (!probe.ExistsOnServer || string.IsNullOrEmpty(probe.LiveTipSha))
            {
                if (hasRemoteTrackingContainingHead)
                {
                    return new AmendPublishInspection
                    {
                        State = Models.AmendRemotePublishState.AlreadyPushed,
                        HasRemotes = true,
                        RemoteName = targetRemote,
                        RemoteBranchName = targetRemoteBranch,
                        TrackingRefName = trackingRef,
                        LocalTrackingTipSha = localTrackingTip,
                        RedactedRemoteUrl = probe.RedactedRemoteUrl,
                        LocalProtection = localProtection,
                        RemoteProtection = remoteProtection,
                        Summary = $"目标提交 {ShortShaForSummary(headSha)} 已被本地跟踪的远端引用（{string.Join("、", containingRemoteRefs.Take(3))}）包含，默认建议改为新建提交。",
                        Detail = $"包含该提交的远端引用：{string.Join(", ", containingRemoteRefs)}",
                    };
                }

                if (!string.IsNullOrEmpty(localTrackingTip))
                {
                    return new AmendPublishInspection
                    {
                        State = Models.AmendRemotePublishState.UnfetchedOrStaleRemote,
                        HasRemotes = true,
                        RemoteName = targetRemote,
                        RemoteBranchName = targetRemoteBranch,
                        TrackingRefName = trackingRef,
                        LocalTrackingTipSha = localTrackingTip,
                        RedactedRemoteUrl = probe.RedactedRemoteUrl,
                        LocalProtection = localProtection,
                        RemoteProtection = remoteProtection,
                        Summary = $"本地跟踪引用 {trackingRef} 与服务器状态不一致（服务器端分支已不存在，本地尚未 Fetch 同步），禁止当作未推送直接 Amend。",
                    };
                }

                return new AmendPublishInspection
                {
                    State = Models.AmendRemotePublishState.UnpushedLocalOnly,
                    HasRemotes = true,
                    RemoteName = targetRemote,
                    RemoteBranchName = targetRemoteBranch,
                    TrackingRefName = trackingRef,
                    RedactedRemoteUrl = probe.RedactedRemoteUrl,
                    ConfirmedPushUrl = probe.PushUrl,
                    LocalProtection = localProtection,
                    RemoteProtection = remoteProtection,
                    Summary = $"已联机确认 '{targetRemote}/{targetRemoteBranch}' 尚未创建，提交 {ShortShaForSummary(headSha)} 仅存在于本地。",
                };
            }

            var liveTip = probe.LiveTipSha;
            var liveExistsLocally = await HistoryResetService.CommitObjectExistsLocallyAsync(repoPath, liveTip, cancellationToken)
                .ConfigureAwait(false);
            var trackingSynced = !string.IsNullOrEmpty(localTrackingTip) &&
                                 string.Equals(localTrackingTip, liveTip, StringComparison.Ordinal);

            if (!liveExistsLocally || !trackingSynced)
            {
                return new AmendPublishInspection
                {
                    State = Models.AmendRemotePublishState.UnfetchedOrStaleRemote,
                    HasRemotes = true,
                    RemoteName = targetRemote,
                    RemoteBranchName = targetRemoteBranch,
                    TrackingRefName = trackingRef,
                    LocalTrackingTipSha = localTrackingTip,
                    LiveRemoteTipSha = liveTip,
                    ConfirmedPushUrl = probe.PushUrl,
                    RedactedRemoteUrl = probe.RedactedRemoteUrl,
                    LocalProtection = localProtection,
                    RemoteProtection = remoteProtection,
                    Summary = $"远端分支 '{targetRemote}/{targetRemoteBranch}' 当前指向 {ShortShaForSummary(liveTip)}，尚未完整 Fetch 至本地跟踪引用；在执行 Fetch 核实前禁止当作未推送直接 Amend。",
                    Detail = string.IsNullOrEmpty(localTrackingTip)
                        ? $"本地尚无 {trackingRef}，服务器端最新提交为 {ShortShaForSummary(liveTip)}。"
                        : $"本地 {trackingRef} 为 {ShortShaForSummary(localTrackingTip)}，服务器现为 {ShortShaForSummary(liveTip)}。",
                };
            }

            var headInRemoteTip = string.Equals(headSha, liveTip, StringComparison.Ordinal) ||
                                  await HistoryResetService.IsAncestorAsync(repoPath, headSha, liveTip, cancellationToken).ConfigureAwait(false);
            if (headInRemoteTip || hasRemoteTrackingContainingHead)
            {
                var remoteMovedAhead = headInRemoteTip && !string.Equals(headSha, liveTip, StringComparison.Ordinal);
                IReadOnlyList<Models.ResetCommitSummary> commitsAhead = Array.Empty<Models.ResetCommitSummary>();
                bool hasOtherAuthors = false;
                string authorsSummary = string.Empty;

                if (remoteMovedAhead)
                {
                    var chain = await HistoryResetService.ReadFirstParentChainAsync(repoPath, liveTip, cancellationToken).ConfigureAwait(false);
                    commitsAhead = await HistoryResetService.ReadCommitRangeAsync(
                        repoPath, headSha, liveTip, new HashSet<string>(chain, StringComparer.Ordinal), cancellationToken).ConfigureAwait(false);
                    var authors = HistoryResetService.AggregateAuthors(commitsAhead);
                    hasOtherAuthors = HistoryResetService.HasOtherAuthors(authors, identity?.Name, identity?.Email);
                    authorsSummary = string.Join("、", authors.Select(a => $"{a.Name} ({a.CommitCount})"));
                }

                return new AmendPublishInspection
                {
                    State = Models.AmendRemotePublishState.AlreadyPushed,
                    HasRemotes = true,
                    RemoteName = targetRemote,
                    RemoteBranchName = targetRemoteBranch,
                    TrackingRefName = trackingRef,
                    LocalTrackingTipSha = localTrackingTip,
                    LiveRemoteTipSha = liveTip,
                    ConfirmedPushUrl = probe.PushUrl,
                    RedactedRemoteUrl = probe.RedactedRemoteUrl,
                    RemoteMovedAhead = remoteMovedAhead,
                    CommitsAhead = commitsAhead,
                    HasOtherAuthors = hasOtherAuthors,
                    AuthorsSummary = authorsSummary,
                    LocalProtection = localProtection,
                    RemoteProtection = remoteProtection,
                    Summary = remoteMovedAhead
                        ? $"目标提交 {ShortShaForSummary(headSha)} 已推送至 '{targetRemote}/{targetRemoteBranch}'，且远端已有 {commitsAhead.Count} 个更新提交（现指向 {ShortShaForSummary(liveTip)}），禁止直接改写；请改为新建提交或先 Pull。"
                        : $"目标提交 {ShortShaForSummary(headSha)} 已推送至远端 '{targetRemote}/{targetRemoteBranch}'，默认建议改为创建新提交，避免破坏协作者历史。",
                    Detail = $"远端当前提交：{ShortShaForSummary(liveTip)}",
                };
            }

            var remoteIsAncestorOfHead = await HistoryResetService.IsAncestorAsync(repoPath, liveTip, headSha, cancellationToken)
                .ConfigureAwait(false);
            if (remoteIsAncestorOfHead)
            {
                return new AmendPublishInspection
                {
                    State = Models.AmendRemotePublishState.UnpushedLocalOnly,
                    HasRemotes = true,
                    RemoteName = targetRemote,
                    RemoteBranchName = targetRemoteBranch,
                    TrackingRefName = trackingRef,
                    LocalTrackingTipSha = localTrackingTip,
                    LiveRemoteTipSha = liveTip,
                    ConfirmedPushUrl = probe.PushUrl,
                    RedactedRemoteUrl = probe.RedactedRemoteUrl,
                    LocalProtection = localProtection,
                    RemoteProtection = remoteProtection,
                    Summary = $"已核实远端 '{targetRemote}/{targetRemoteBranch}' 停留在祖先提交 {ShortShaForSummary(liveTip)}，当前 HEAD（{ShortShaForSummary(headSha)}）尚未推送，可确认后直接补充提交。",
                };
            }

            return new AmendPublishInspection
            {
                State = Models.AmendRemotePublishState.UnfetchedOrStaleRemote,
                HasRemotes = true,
                RemoteName = targetRemote,
                RemoteBranchName = targetRemoteBranch,
                TrackingRefName = trackingRef,
                LocalTrackingTipSha = localTrackingTip,
                LiveRemoteTipSha = liveTip,
                ConfirmedPushUrl = probe.PushUrl,
                RedactedRemoteUrl = probe.RedactedRemoteUrl,
                LocalProtection = localProtection,
                RemoteProtection = remoteProtection,
                Summary = $"当前分支与远端 '{targetRemote}/{targetRemoteBranch}'（{ShortShaForSummary(liveTip)}）已分叉，禁止当作未推送直接 Amend；请改为创建新提交或先整合远端变更。",
            };
        }

        private static async Task<AmendPublishInspection> FindCandidateRemoteWithoutUpstreamAsync(
            string repoPath,
            string branchName,
            string headSha,
            List<Models.Remote> remotes,
            IReadOnlyList<string> containingRemoteRefs,
            CancellationToken cancellationToken)
        {
            if (containingRemoteRefs.Count > 0)
            {
                return new AmendPublishInspection
                {
                    State = Models.AmendRemotePublishState.AlreadyPushed,
                    HasRemotes = true,
                    Summary = $"当前未配置上游，但提交 {ShortShaForSummary(headSha)} 已包含在本地已知远端引用（{string.Join("、", containingRemoteRefs.Take(3))}）中，默认建议改为新建提交。",
                    Detail = $"包含该提交的远端引用：{string.Join(", ", containingRemoteRefs)}",
                };
            }

            var ordered = remotes
                .OrderByDescending(r => string.Equals(r.Name, "origin", StringComparison.Ordinal))
                .ThenBy(r => r.Name, StringComparer.Ordinal);

            foreach (var r in ordered)
            {
                var trackingRef = $"refs/remotes/{r.Name}/{branchName}";
                var localTrack = await HistoryResetService.ResolveRefShaAsync(repoPath, trackingRef, cancellationToken)
                    .ConfigureAwait(false);
                var remoteProt = await BranchProtectionPolicy.InspectAsync(repoPath, branchName, cancellationToken)
                    .ConfigureAwait(false);
                var probe = await BranchDeletionService.ProbeRemoteBranchAsync(
                    repoPath, r.Name, branchName, cancellationToken: cancellationToken).ConfigureAwait(false);

                if (!probe.QuerySucceeded)
                {
                    return new AmendPublishInspection
                    {
                        State = Models.AmendRemotePublishState.UnknownRemoteState,
                        HasRemotes = true,
                        RemoteName = r.Name,
                        RemoteBranchName = branchName,
                        TrackingRefName = trackingRef,
                        LocalTrackingTipSha = localTrack,
                        RedactedRemoteUrl = !string.IsNullOrEmpty(probe.RedactedRemoteUrl) ? probe.RedactedRemoteUrl : Models.GitConfigSnapshot.RedactText(r.PushURL ?? r.URL),
                        RemoteProtection = remoteProt,
                        Summary = $"分支未设置上游且无法连接远端 '{r.Name}'（{probe.ErrorSummary}），禁止在未核实远端状态时当作未推送直接 Amend。",
                        Detail = probe.ErrorSummary,
                    };
                }

                if (probe.ExistsOnServer && !string.IsNullOrEmpty(probe.LiveTipSha))
                {
                    var liveLocally = await HistoryResetService.CommitObjectExistsLocallyAsync(repoPath, probe.LiveTipSha, cancellationToken)
                        .ConfigureAwait(false);
                    var synced = !string.IsNullOrEmpty(localTrack) && string.Equals(localTrack, probe.LiveTipSha, StringComparison.Ordinal);
                    if (!liveLocally || !synced)
                    {
                        return new AmendPublishInspection
                        {
                            State = Models.AmendRemotePublishState.UnfetchedOrStaleRemote,
                            HasRemotes = true,
                            RemoteName = r.Name,
                            RemoteBranchName = branchName,
                            TrackingRefName = trackingRef,
                            LocalTrackingTipSha = localTrack,
                            LiveRemoteTipSha = probe.LiveTipSha,
                            ConfirmedPushUrl = probe.PushUrl,
                            RedactedRemoteUrl = probe.RedactedRemoteUrl,
                            RemoteProtection = remoteProt,
                            Summary = $"分支 '{branchName}' 未配置上游，但服务器 '{r.Name}/{branchName}' 已存在（{ShortShaForSummary(probe.LiveTipSha)}）且尚未完整 Fetch 至本地，禁止直接 Amend。",
                        };
                    }

                    var headInLive = string.Equals(headSha, probe.LiveTipSha, StringComparison.Ordinal) ||
                                     await HistoryResetService.IsAncestorAsync(repoPath, headSha, probe.LiveTipSha, cancellationToken).ConfigureAwait(false);
                    if (headInLive)
                    {
                        return new AmendPublishInspection
                        {
                            State = Models.AmendRemotePublishState.AlreadyPushed,
                            HasRemotes = true,
                            RemoteName = r.Name,
                            RemoteBranchName = branchName,
                            TrackingRefName = trackingRef,
                            LocalTrackingTipSha = localTrack,
                            LiveRemoteTipSha = probe.LiveTipSha,
                            ConfirmedPushUrl = probe.PushUrl,
                            RedactedRemoteUrl = probe.RedactedRemoteUrl,
                            RemoteMovedAhead = !string.Equals(headSha, probe.LiveTipSha, StringComparison.Ordinal),
                            RemoteProtection = remoteProt,
                            Summary = $"提交 {ShortShaForSummary(headSha)} 已存在于远端 '{r.Name}/{branchName}'，默认建议改为新建提交。",
                        };
                    }

                    var liveAncestor = await HistoryResetService.IsAncestorAsync(repoPath, probe.LiveTipSha, headSha, cancellationToken)
                        .ConfigureAwait(false);
                    if (!liveAncestor)
                    {
                        return new AmendPublishInspection
                        {
                            State = Models.AmendRemotePublishState.UnfetchedOrStaleRemote,
                            HasRemotes = true,
                            RemoteName = r.Name,
                            RemoteBranchName = branchName,
                            TrackingRefName = trackingRef,
                            LocalTrackingTipSha = localTrack,
                            LiveRemoteTipSha = probe.LiveTipSha,
                            ConfirmedPushUrl = probe.PushUrl,
                            RedactedRemoteUrl = probe.RedactedRemoteUrl,
                            RemoteProtection = remoteProt,
                            Summary = $"远端 '{r.Name}/{branchName}' 与当前分支历史分叉，禁止直接 Amend。",
                        };
                    }
                }
                else if (!string.IsNullOrEmpty(localTrack))
                {
                    return new AmendPublishInspection
                    {
                        State = Models.AmendRemotePublishState.UnfetchedOrStaleRemote,
                        HasRemotes = true,
                        RemoteName = r.Name,
                        RemoteBranchName = branchName,
                        TrackingRefName = trackingRef,
                        LocalTrackingTipSha = localTrack,
                        RedactedRemoteUrl = probe.RedactedRemoteUrl,
                        RemoteProtection = remoteProt,
                        Summary = $"本地残留跟踪引用 {trackingRef} 但服务器已不存在对应分支，请在 Fetch 清理核实后再操作。",
                    };
                }
            }

            return null;
        }

        private static AmendPublishInspection WithProtection(this AmendPublishInspection inspection, Models.BranchProtectionStatus localProtection)
        {
            return new AmendPublishInspection
            {
                State = inspection.State,
                Summary = inspection.Summary,
                Detail = inspection.Detail,
                RemoteName = inspection.RemoteName,
                RemoteBranchName = inspection.RemoteBranchName,
                TrackingRefName = inspection.TrackingRefName,
                LocalTrackingTipSha = inspection.LocalTrackingTipSha,
                LiveRemoteTipSha = inspection.LiveRemoteTipSha,
                ConfirmedPushUrl = inspection.ConfirmedPushUrl,
                RedactedRemoteUrl = inspection.RedactedRemoteUrl,
                HasRemotes = inspection.HasRemotes,
                RemoteMovedAhead = inspection.RemoteMovedAhead,
                CommitsAhead = inspection.CommitsAhead,
                HasOtherAuthors = inspection.HasOtherAuthors,
                AuthorsSummary = inspection.AuthorsSummary,
                LocalProtection = localProtection,
                RemoteProtection = inspection.RemoteProtection,
            };
        }

        private static async Task<IReadOnlyList<string>> ReadRemoteRefsContainingCommitAsync(
            string repoPath,
            string commitSha,
            CancellationToken cancellationToken)
        {
            var res = await Command.RunGitDirectAsync(
                repoPath,
                cancellationToken,
                "for-each-ref",
                "--contains",
                commitSha,
                "--format=%(refname:short)",
                "refs/remotes").ConfigureAwait(false);

            if (!res.IsSuccess || string.IsNullOrWhiteSpace(res.StdOut))
                return Array.Empty<string>();

            return res.StdOut
                .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                .Where(r => !r.EndsWith("/HEAD", StringComparison.Ordinal))
                .ToList();
        }

        private static async Task<(Models.GitConfigSigningStatus Status, string Summary)> InspectAmendSigningAsync(
            string repoPath,
            CancellationToken cancellationToken)
        {
            var cfgSnapshot = await new Config(repoPath).ReadSnapshotAsync().ConfigureAwait(false);
            if (!cfgSnapshot.IsReadable)
                return (null, "无法读取签名配置（将以 Git 实际提交行为为准）。");

            var status = Config.InspectSigningStatus(cfgSnapshot, Native.OS.GitInstallation);
            var enabled = status.CommitSigningEnabled;
            if (!enabled)
                return (status, "提交签名：未启用");

            if (status.IsReady)
                return (status, $"提交签名：已就绪（{status.Format.ToUpperInvariant()}）");

            var detail = string.IsNullOrWhiteSpace(status.IssueDetail) ? status.IssueKey : $"{status.IssueKey}: {status.IssueDetail}";
            return (status, $"提交签名：配置存在问题（{detail}）");
        }

        private static async Task<(bool HasHooks, string Summary)> InspectCommitHooksAsync(
            string repoPath,
            string commonDir,
            CancellationToken cancellationToken)
        {
            var cfgHooksPath = await Command.RunGitDirectAsync(repoPath, cancellationToken, "config", "--get", "core.hooksPath")
                .ConfigureAwait(false);
            var hooksDir = cfgHooksPath.IsSuccess && !string.IsNullOrWhiteSpace(cfgHooksPath.StdOut)
                ? cfgHooksPath.StdOut.Trim()
                : Path.Combine(commonDir, "hooks");

            if (!Path.IsPathRooted(hooksDir))
                hooksDir = Path.GetFullPath(Path.Combine(repoPath, hooksDir));

            var names = new[] { "pre-commit", "prepare-commit-msg", "commit-msg", "post-commit" };
            var active = new List<string>();
            if (Directory.Exists(hooksDir))
            {
                foreach (var n in names)
                {
                    if (File.Exists(Path.Combine(hooksDir, n)))
                        active.Add(n);
                }
            }

            return active.Count > 0
                ? (true, $"检测到激活 Hook：{string.Join("、", active)}")
                : (false, "未检测到激活的本地提交 Hook");
        }

        private static string FormatAuthor(Models.User user)
        {
            if (user == null || user == Models.User.Invalid || (string.IsNullOrWhiteSpace(user.Name) && string.IsNullOrWhiteSpace(user.Email)))
                return string.Empty;

            return string.IsNullOrWhiteSpace(user.Email) ? user.Name : $"{user.Name} <{user.Email}>";
        }

        private static string FirstLine(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return string.Empty;

            var idx = text.IndexOfAny(['\r', '\n']);
            return idx >= 0 ? text[..idx].Trim() : text.Trim();
        }
    }
}
