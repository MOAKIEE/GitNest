using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SourceGit.Commands
{
    /// <summary>
    /// Coordinates safe first-parent commit reset (--mixed / --hard), branch-protection checks,
    /// untracked/ignored collision detection, explicit lease-pinned remote history rewrite,
    /// backup ref creation/recovery, and operation journal verification.
    /// </summary>
    public static class HistoryResetService
    {
        private static readonly HashSet<string> EmptyTreeHashes = new(StringComparer.OrdinalIgnoreCase)
        {
            "4b825dc642cb6eb9a060e54bf8d69288fbee4904",
            "6ef19b41225c5369f1c104d45d8d85efa9b057b53b14b4b9b939dd74decc5321",
        };

        internal sealed class RemotePushRawResult
        {
            public bool IsSuccess { get; init; }
            public bool TimedOutOrConnectionLost { get; init; }
            public int ExitCode { get; init; }
            public string StdOut { get; init; } = string.Empty;
            public string StdErr { get; init; } = string.Empty;
        }

        internal static Func<string, string, IReadOnlyList<string>, Models.ICommandLog, CancellationToken, Task<RemotePushRawResult>> RemotePushExecutorOverride { get; set; }

        public static async Task<Models.HistoryResetPreflight> InspectCommitResetAsync(
            string repoPath,
            string targetCommitRef,
            bool probeRemote = true,
            CancellationToken cancellationToken = default)
        {
            var (gitDir, commonDir) = await Models.RepositoryWriteGuard.ResolveGitDirectoriesAsync(repoPath, cancellationToken)
                .ConfigureAwait(false);
            var inProgress = CaptureOperationSnapshot.DetectInProgressKind(gitDir);
            var snapshot = await CaptureOperationSnapshot.CaptureAsync(repoPath, null, cancellationToken).ConfigureAwait(false);
            var headSha = snapshot.HeadSha;
            var isUnborn = string.IsNullOrEmpty(headSha);

            var currentBranch = string.Empty;
            var currentBranchRef = string.Empty;
            if (!isUnborn && !snapshot.IsDetachedHead &&
                snapshot.HeadRefName.StartsWith("refs/heads/", StringComparison.Ordinal))
            {
                currentBranchRef = snapshot.HeadRefName;
                currentBranch = snapshot.HeadRefName.Substring("refs/heads/".Length);
            }

            if (isUnborn)
            {
                return new Models.HistoryResetPreflight
                {
                    RepoPath = repoPath,
                    CanonicalGitDir = gitDir,
                    CanonicalCommonDir = commonDir,
                    IsUnbornHead = true,
                    InProgressKind = inProgress,
                    HasStagedChanges = snapshot.HasStagedChanges,
                    HasUnstagedChanges = snapshot.HasUnstagedChanges,
                    HasUntrackedFiles = snapshot.HasUntrackedFiles,
                    HasConflicts = snapshot.HasConflicts,
                    CanResetLocally = false,
                    BlockReason = "当前仓库尚无任何提交（Unborn HEAD），不支持撤回提交。",
                    Summary = "当前仓库尚无提交记录，无法执行提交撤回。",
                };
            }

            var headParents = await ReadCommitParentsAsync(repoPath, headSha, cancellationToken).ConfigureAwait(false);
            var headIsRoot = headParents.Count == 0;

            var protection = string.IsNullOrEmpty(currentBranch)
                ? new Models.BranchProtectionStatus()
                : await BranchProtectionPolicy.InspectAsync(repoPath, currentBranch, cancellationToken).ConfigureAwait(false);

            var (userName, userEmail) = await ReadCurrentGitIdentityAsync(repoPath, cancellationToken).ConfigureAwait(false);

            if (string.IsNullOrWhiteSpace(targetCommitRef))
            {
                return BuildBlockedResetPreflight(
                    repoPath, gitDir, commonDir, currentBranch, currentBranchRef, headSha,
                    targetSha: string.Empty, targetSubject: string.Empty,
                    isDetached: snapshot.IsDetachedHead, headIsRoot: headIsRoot, isRootTarget: false,
                    isSameAsHead: false, isFirstParent: false, inProgress, snapshot, protection,
                    userName, userEmail,
                    blockedByRoot: headIsRoot,
                    blockReason: "未指定要回退到的目标提交；不支持将仓库首个提交一起撤回为空仓库。");
            }

            var trimmedTarget = targetCommitRef.Trim();
            if (trimmedTarget.StartsWith("-", StringComparison.Ordinal))
                throw new ArgumentException("目标提交标识不能以 '-' 开头。", nameof(targetCommitRef));

            if (EmptyTreeHashes.Contains(trimmedTarget))
            {
                return BuildBlockedResetPreflight(
                    repoPath, gitDir, commonDir, currentBranch, currentBranchRef, headSha,
                    targetSha: trimmedTarget, targetSubject: string.Empty,
                    isDetached: snapshot.IsDetachedHead, headIsRoot: headIsRoot, isRootTarget: false,
                    isSameAsHead: false, isFirstParent: false, inProgress, snapshot, protection,
                    userName, userEmail,
                    blockedByRoot: true,
                    blockReason: "不支持把仓库首个提交一起撤回成空仓库。");
            }

            var resolvedTarget = await ResolveCommitShaAsync(repoPath, trimmedTarget, cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrEmpty(resolvedTarget))
            {
                var looksLikeRootParent = trimmedTarget.EndsWith("^", StringComparison.Ordinal) ||
                    trimmedTarget.EndsWith("~1", StringComparison.Ordinal);
                return BuildBlockedResetPreflight(
                    repoPath, gitDir, commonDir, currentBranch, currentBranchRef, headSha,
                    targetSha: string.Empty, targetSubject: string.Empty,
                    isDetached: snapshot.IsDetachedHead, headIsRoot: headIsRoot, isRootTarget: false,
                    isSameAsHead: false, isFirstParent: false, inProgress, snapshot, protection,
                    userName, userEmail,
                    blockedByRoot: headIsRoot || looksLikeRootParent,
                    blockReason: (headIsRoot || looksLikeRootParent)
                        ? "不存在更早的父提交，不支持将首个提交一起撤回成空仓库。"
                        : $"无法解析目标提交 '{trimmedTarget}'。");
            }

            var targetParents = await ReadCommitParentsAsync(repoPath, resolvedTarget, cancellationToken).ConfigureAwait(false);
            var isRootCommitTarget = targetParents.Count == 0;
            var targetSubject = await ReadCommitSubjectAsync(repoPath, resolvedTarget, cancellationToken).ConfigureAwait(false);
            var isSameAsHead = string.Equals(resolvedTarget, headSha, StringComparison.Ordinal);

            var firstParentChain = await ReadFirstParentChainAsync(repoPath, headSha, cancellationToken).ConfigureAwait(false);
            var firstParentSet = new HashSet<string>(firstParentChain, StringComparer.Ordinal);
            var isOnFirstParent = !isSameAsHead && firstParentSet.Contains(resolvedTarget);

            var localCommitsToRemove = Array.Empty<Models.ResetCommitSummary>() as IReadOnlyList<Models.ResetCommitSummary>;
            var authors = Array.Empty<Models.ResetAuthorAttribution>() as IReadOnlyList<Models.ResetAuthorAttribution>;
            if (isOnFirstParent)
            {
                localCommitsToRemove = await ReadCommitRangeAsync(repoPath, resolvedTarget, headSha, firstParentSet, cancellationToken)
                    .ConfigureAwait(false);
                authors = AggregateAuthors(localCommitsToRemove);
            }

            var firstParentRemovedCount = localCommitsToRemove.Count(c => c.IsFirstParent);
            var mergedSideRemovedCount = localCommitsToRemove.Count - firstParentRemovedCount;
            var hasOtherAuthors = HasOtherAuthors(authors, userName, userEmail);

            var collisions = isSameAsHead
                ? Array.Empty<string>()
                : await DetectUntrackedOrIgnoredPathCollisionsAsync(repoPath, resolvedTarget, cancellationToken).ConfigureAwait(false);

            var (configuredUpstream, additionalRemotes) = string.IsNullOrEmpty(currentBranch)
                ? (null, (IReadOnlyList<Models.ResetUpstreamRemoteStatus>)Array.Empty<Models.ResetUpstreamRemoteStatus>())
                : await InspectBranchRemotesAsync(
                    repoPath,
                    currentBranch,
                    headSha,
                    resolvedTarget,
                    probeRemote,
                    cancellationToken).ConfigureAwait(false);

            var watchedRefs = string.IsNullOrEmpty(currentBranchRef)
                ? Array.Empty<string>()
                : new[] { currentBranchRef };
            var confirmationSnapshot = await CaptureOperationSnapshot.CaptureAsync(
                repoPath,
                new Models.OperationSnapshotCaptureOptions { RefsToWatch = watchedRefs },
                cancellationToken).ConfigureAwait(false);

            var blockReason = DetermineCommitResetBlockReason(
                snapshot.IsDetachedHead,
                inProgress,
                isSameAsHead,
                headIsRoot,
                isOnFirstParent,
                snapshot,
                collisions);

            var canReset = string.IsNullOrEmpty(blockReason);
            var summary = canReset
                ? $"准备将当前分支 '{currentBranch}' 撤回到 {ShortSha(resolvedTarget)}（将从当前分支历史移除 {localCommitsToRemove.Count} 个提交，其中主线 {firstParentRemovedCount} 个、合并引入提交 {mergedSideRemovedCount} 个）。"
                : blockReason;

            return new Models.HistoryResetPreflight
            {
                RepoPath = repoPath,
                CanonicalGitDir = gitDir,
                CanonicalCommonDir = commonDir,
                LocalBranchName = currentBranch,
                LocalBranchFullRef = currentBranchRef,
                CurrentHeadSha = headSha,
                TargetCommitSha = resolvedTarget,
                TargetCommitSubject = targetSubject,
                IsUnbornHead = false,
                IsDetachedHead = snapshot.IsDetachedHead,
                HeadIsRootCommit = headIsRoot,
                IsRootCommitTarget = isRootCommitTarget,
                IsSameAsHead = isSameAsHead,
                IsOnFirstParentChain = isOnFirstParent,
                InProgressKind = inProgress,
                HasStagedChanges = snapshot.HasStagedChanges,
                HasUnstagedChanges = snapshot.HasUnstagedChanges,
                HasUntrackedFiles = snapshot.HasUntrackedFiles,
                HasConflicts = snapshot.HasConflicts,
                CollisionPaths = collisions,
                CanResetLocally = canReset,
                BlockReason = blockReason,
                Protection = protection,
                LocalCommitsToRemove = localCommitsToRemove,
                FirstParentCommitsToRemoveCount = firstParentRemovedCount,
                MergedSideCommitsToRemoveCount = mergedSideRemovedCount,
                LocalAuthors = authors,
                HasOtherAuthorsInLocal = hasOtherAuthors,
                CurrentUserName = userName,
                CurrentUserEmail = userEmail,
                ConfiguredUpstream = configuredUpstream,
                AdditionalTrackedRemotes = additionalRemotes,
                Snapshot = confirmationSnapshot,
                Summary = summary,
            };
        }

        public static async Task<Models.RemoteRewritePreflight> InspectRemoteRewriteForTargetAsync(
            string repoPath,
            string localBranchName,
            string targetCommitRef,
            string remoteName = null,
            string remoteBranchName = null,
            CancellationToken cancellationToken = default)
        {
            var (gitDir, commonDir) = await Models.RepositoryWriteGuard.ResolveGitDirectoriesAsync(repoPath, cancellationToken)
                .ConfigureAwait(false);
            var validLocalBranch = CommandArgumentHelper.ValidateNonOptionToken(localBranchName, nameof(localBranchName));
            var fullLocalRef = "refs/heads/" + validLocalBranch;
            var localTipSha = await ResolveRefShaAsync(repoPath, fullLocalRef, cancellationToken).ConfigureAwait(false);
            var targetSha = await ResolveCommitShaAsync(repoPath, targetCommitRef, cancellationToken).ConfigureAwait(false);
            var targetSubject = string.IsNullOrEmpty(targetSha)
                ? string.Empty
                : await ReadCommitSubjectAsync(repoPath, targetSha, cancellationToken).ConfigureAwait(false);

            var localProtection = await BranchProtectionPolicy.InspectAsync(repoPath, validLocalBranch, cancellationToken)
                .ConfigureAwait(false);

            var targetEndpoint = await ResolveRemoteEndpointAsync(
                repoPath,
                validLocalBranch,
                remoteName,
                remoteBranchName,
                cancellationToken).ConfigureAwait(false);

            var remoteBranchForCheck = string.IsNullOrEmpty(targetEndpoint.RemoteBranchName)
                ? validLocalBranch
                : targetEndpoint.RemoteBranchName;
            var remoteProtection = await BranchProtectionPolicy.InspectAsync(repoPath, remoteBranchForCheck, cancellationToken)
                .ConfigureAwait(false);

            if (string.IsNullOrEmpty(localTipSha) || string.IsNullOrEmpty(targetSha))
            {
                return new Models.RemoteRewritePreflight
                {
                    RepoPath = repoPath,
                    CanonicalGitDir = gitDir,
                    CanonicalCommonDir = commonDir,
                    LocalBranchName = validLocalBranch,
                    LocalBranchFullRef = fullLocalRef,
                    LocalTipSha = localTipSha,
                    TargetTipSha = targetSha,
                    TargetCommitSubject = targetSubject,
                    RemoteName = targetEndpoint.RemoteName,
                    RemoteBranchName = remoteBranchForCheck,
                    TrackingRefName = targetEndpoint.TrackingRefName,
                    LocalTrackingTipSha = targetEndpoint.LocalTrackingTipSha,
                    RedactedRemoteUrl = targetEndpoint.RedactedUrl,
                    LocalBranchProtection = localProtection,
                    RemoteBranchProtection = remoteProtection,
                    CanRewriteRemote = false,
                    BlockReason = string.IsNullOrEmpty(localTipSha)
                        ? $"本地分支 '{validLocalBranch}' 不存在。"
                        : $"无法解析目标提交 '{targetCommitRef}'。",
                    Summary = "无法解析本地分支或目标提交。",
                };
            }

            if (string.IsNullOrEmpty(targetEndpoint.RemoteName) || string.IsNullOrEmpty(targetEndpoint.RemoteBranchName))
            {
                return new Models.RemoteRewritePreflight
                {
                    RepoPath = repoPath,
                    CanonicalGitDir = gitDir,
                    CanonicalCommonDir = commonDir,
                    LocalBranchName = validLocalBranch,
                    LocalBranchFullRef = fullLocalRef,
                    LocalTipSha = localTipSha,
                    TargetTipSha = targetSha,
                    TargetCommitSubject = targetSubject,
                    LocalBranchProtection = localProtection,
                    RemoteBranchProtection = remoteProtection,
                    CanRewriteRemote = false,
                    BlockReason = $"分支 '{validLocalBranch}' 尚未配置有效远端跟踪目标，无需改写远程分支历史。",
                    Summary = "未配置远端跟踪目标。",
                };
            }

            var probe = await BranchDeletionService.ProbeRemoteBranchAsync(
                repoPath,
                targetEndpoint.RemoteName,
                targetEndpoint.RemoteBranchName,
                cancellationToken: cancellationToken).ConfigureAwait(false);

            if (!probe.QuerySucceeded)
            {
                return new Models.RemoteRewritePreflight
                {
                    RepoPath = repoPath,
                    CanonicalGitDir = gitDir,
                    CanonicalCommonDir = commonDir,
                    LocalBranchName = validLocalBranch,
                    LocalBranchFullRef = fullLocalRef,
                    LocalTipSha = localTipSha,
                    TargetTipSha = targetSha,
                    TargetCommitSubject = targetSubject,
                    RemoteName = targetEndpoint.RemoteName,
                    RemoteBranchName = targetEndpoint.RemoteBranchName,
                    TrackingRefName = targetEndpoint.TrackingRefName,
                    LocalTrackingTipSha = targetEndpoint.LocalTrackingTipSha,
                    RedactedRemoteUrl = !string.IsNullOrEmpty(probe.RedactedRemoteUrl) ? probe.RedactedRemoteUrl : targetEndpoint.RedactedUrl,
                    QuerySucceeded = false,
                    RemoteStateUnknown = true,
                    RemoteQueryError = probe.ErrorSummary,
                    LocalBranchProtection = localProtection,
                    RemoteBranchProtection = remoteProtection,
                    CanRewriteRemote = false,
                    BlockReason = $"远端状态未知：无法连接 '{targetEndpoint.RemoteName}' 查询当前分支提交（{probe.ErrorSummary}），已禁止改写远程分支历史。",
                    Summary = "远端状态未知（无法联网或查询失败）。",
                };
            }

            if (!probe.ExistsOnServer || string.IsNullOrEmpty(probe.LiveTipSha))
            {
                return new Models.RemoteRewritePreflight
                {
                    RepoPath = repoPath,
                    CanonicalGitDir = gitDir,
                    CanonicalCommonDir = commonDir,
                    LocalBranchName = validLocalBranch,
                    LocalBranchFullRef = fullLocalRef,
                    LocalTipSha = localTipSha,
                    TargetTipSha = targetSha,
                    TargetCommitSubject = targetSubject,
                    RemoteName = targetEndpoint.RemoteName,
                    RemoteBranchName = targetEndpoint.RemoteBranchName,
                    TrackingRefName = targetEndpoint.TrackingRefName,
                    LocalTrackingTipSha = targetEndpoint.LocalTrackingTipSha,
                    RedactedRemoteUrl = probe.RedactedRemoteUrl,
                    QuerySucceeded = true,
                    ExistsOnServer = false,
                    LocalBranchProtection = localProtection,
                    RemoteBranchProtection = remoteProtection,
                    CanRewriteRemote = false,
                    BlockReason = $"远端分支 '{targetEndpoint.RemoteName}/{targetEndpoint.RemoteBranchName}' 在服务器上尚不存在，无需改写远程分支历史。",
                    Summary = "远端分支尚未推送到服务器。",
                };
            }

            var liveRemoteTip = probe.LiveTipSha;
            var remoteTipExistsLocally = await CommitObjectExistsLocallyAsync(repoPath, liveRemoteTip, cancellationToken).ConfigureAwait(false);
            var remoteMatchesTarget = string.Equals(liveRemoteTip, targetSha, StringComparison.Ordinal);

            bool remoteIsStrictAncestorOfTarget = false;
            bool targetIsAncestorOfRemote = false;
            bool divergedUnintegrated = false;

            if (!remoteTipExistsLocally)
            {
                divergedUnintegrated = true;
            }
            else if (!remoteMatchesTarget)
            {
                remoteIsStrictAncestorOfTarget = await IsAncestorAsync(repoPath, liveRemoteTip, targetSha, cancellationToken).ConfigureAwait(false);
                targetIsAncestorOfRemote = await IsAncestorAsync(repoPath, targetSha, liveRemoteTip, cancellationToken).ConfigureAwait(false);

                var localAlreadyAtTarget = string.Equals(localTipSha, targetSha, StringComparison.Ordinal);
                if (!localAlreadyAtTarget)
                {
                    // Before local reset: remote tip MUST already be explained inside local branch history.
                    var remoteExplainedByLocal = await IsAncestorAsync(repoPath, liveRemoteTip, localTipSha, cancellationToken).ConfigureAwait(false);
                    if (!remoteExplainedByLocal || !targetIsAncestorOfRemote)
                        divergedUnintegrated = !remoteIsStrictAncestorOfTarget;
                }
                else
                {
                    // After local reset: targetSha must be ancestor of liveRemoteTip AND remote must not have
                    // moved onto commits outside our known local tracking ref when tracking ref exists.
                    if (!targetIsAncestorOfRemote)
                    {
                        divergedUnintegrated = true;
                    }
                    else if (!string.IsNullOrEmpty(targetEndpoint.LocalTrackingTipSha) &&
                             !string.Equals(liveRemoteTip, targetEndpoint.LocalTrackingTipSha, StringComparison.Ordinal))
                    {
                        var remoteBehindTracking = await IsAncestorAsync(repoPath, liveRemoteTip, targetEndpoint.LocalTrackingTipSha, cancellationToken)
                            .ConfigureAwait(false);
                        if (!remoteBehindTracking)
                            divergedUnintegrated = true;
                    }
                }
            }

            IReadOnlyList<Models.ResetCommitSummary> remoteCommitsToRemove = Array.Empty<Models.ResetCommitSummary>();
            IReadOnlyList<Models.ResetAuthorAttribution> remoteAuthors = Array.Empty<Models.ResetAuthorAttribution>();
            if (remoteTipExistsLocally && targetIsAncestorOfRemote && !remoteMatchesTarget)
            {
                var remoteFirstParents = await ReadFirstParentChainAsync(repoPath, liveRemoteTip, cancellationToken).ConfigureAwait(false);
                remoteCommitsToRemove = await ReadCommitRangeAsync(
                    repoPath,
                    targetSha,
                    liveRemoteTip,
                    new HashSet<string>(remoteFirstParents, StringComparer.Ordinal),
                    cancellationToken).ConfigureAwait(false);
                remoteAuthors = AggregateAuthors(remoteCommitsToRemove);
            }

            var (userName, userEmail) = await ReadCurrentGitIdentityAsync(repoPath, cancellationToken).ConfigureAwait(false);
            var hasOtherRemoteAuthors = HasOtherAuthors(remoteAuthors, userName, userEmail);

            var protectionBlocked = localProtection.IsEffectivelyProtected || remoteProtection.IsEffectivelyProtected;
            string blockReason = string.Empty;
            if (!remoteTipExistsLocally)
            {
                blockReason = $"远端分支 '{targetEndpoint.RemoteName}/{targetEndpoint.RemoteBranchName}' 当前提交 {ShortSha(liveRemoteTip)} 尚未存在于本地对象库中（可能包含他人新推送提交），已阻止改写远程分支历史；请先 Fetch 并整合远端分支。";
            }
            else if (remoteMatchesTarget)
            {
                blockReason = $"远端分支 '{targetEndpoint.RemoteName}/{targetEndpoint.RemoteBranchName}' 已经停留在目标提交 {ShortSha(targetSha)}，无需改写历史。";
            }
            else if (remoteIsStrictAncestorOfTarget)
            {
                blockReason = $"远端分支落后于目标提交，可直接通过常规 Push 快进同步，无需改写远程分支历史。";
            }
            else if (divergedUnintegrated)
            {
                blockReason = $"远端分支 '{targetEndpoint.RemoteName}/{targetEndpoint.RemoteBranchName}'（{ShortSha(liveRemoteTip)}）包含本地尚未整合或已分叉的新提交，已阻止改写远程分支历史；请先整合远端提交后再处理。";
            }
            else if (protectionBlocked)
            {
                blockReason = remoteProtection.IsEffectivelyProtected ? remoteProtection.Summary : localProtection.Summary;
            }

            // Pin refs/heads/<localBranch> in ConfirmationSnapshot; remote lease tip is explicitly frozen in LiveRemoteTipSha.
            var snapshot = await CaptureOperationSnapshot.CaptureAsync(
                repoPath,
                new Models.OperationSnapshotCaptureOptions { RefsToWatch = [fullLocalRef] },
                cancellationToken).ConfigureAwait(false);

            var canRewrite = string.IsNullOrEmpty(blockReason);
            var summary = canRewrite
                ? $"准备以租约保护将远端 '{targetEndpoint.RemoteName}/{targetEndpoint.RemoteBranchName}'（当前 {ShortSha(liveRemoteTip)}）改写至 {ShortSha(targetSha)}，将移除远端历史中的 {remoteCommitsToRemove.Count} 个提交。"
                : blockReason;

            return new Models.RemoteRewritePreflight
            {
                RepoPath = repoPath,
                CanonicalGitDir = gitDir,
                CanonicalCommonDir = commonDir,
                LocalBranchName = validLocalBranch,
                LocalBranchFullRef = fullLocalRef,
                LocalTipSha = localTipSha,
                TargetTipSha = targetSha,
                TargetCommitSubject = targetSubject,
                RemoteName = targetEndpoint.RemoteName,
                RemoteBranchName = targetEndpoint.RemoteBranchName,
                TrackingRefName = targetEndpoint.TrackingRefName,
                LocalTrackingTipSha = targetEndpoint.LocalTrackingTipSha,
                RedactedRemoteUrl = probe.RedactedRemoteUrl,
                QuerySucceeded = true,
                RemoteStateUnknown = false,
                ExistsOnServer = true,
                LiveRemoteTipSha = liveRemoteTip,
                PushUrl = probe.PushUrl,
                RemoteTipExistsLocally = remoteTipExistsLocally,
                RemoteAlreadyMatchesTarget = remoteMatchesTarget,
                RemoteIsStrictAncestorOfTarget = remoteIsStrictAncestorOfTarget,
                TargetIsAncestorOfRemote = targetIsAncestorOfRemote,
                RemoteDivergedWithUnintegratedCommits = divergedUnintegrated,
                LocalBranchProtection = localProtection,
                RemoteBranchProtection = remoteProtection,
                CommitsToRemoveOnRemote = remoteCommitsToRemove,
                RemoteAuthors = remoteAuthors,
                HasOtherAuthorsOnRemote = hasOtherRemoteAuthors,
                CanRewriteRemote = canRewrite,
                BlockReason = blockReason,
                Snapshot = snapshot,
                Summary = summary,
            };
        }

        public static async Task<Models.HistoryResetOutcome> ExecuteCommitResetAsync(
            string repoPath,
            Models.HistoryResetOptions options,
            Models.OperationJournalStore journalStore = null,
            Models.ICommandLog outerLog = null,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(options);
            ArgumentNullException.ThrowIfNull(options.Preflight);

            var preflight = options.Preflight;
            if (!preflight.CanResetLocally)
            {
                return BuildBlockedResetOutcome(preflight);
            }

            if (options.AlsoRewriteRemote)
            {
                var remotePre = options.RemoteRewritePreflight;
                if (remotePre == null || !remotePre.CanRewriteRemote)
                {
                    return new Models.HistoryResetOutcome
                    {
                        BlockedByProtection = remotePre?.BlockedByProtection ?? false,
                        BlockedByUnknownRemoteState = remotePre?.RemoteStateUnknown ?? true,
                        BlockedByDivergedRemote = remotePre?.RemoteDivergedWithUnintegratedCommits ?? false,
                        LocalBranchName = preflight.LocalBranchName,
                        OriginalTipSha = preflight.CurrentHeadSha,
                        TargetTipSha = preflight.TargetCommitSha,
                        RemoteName = remotePre?.RemoteName ?? string.Empty,
                        RemoteBranchName = remotePre?.RemoteBranchName ?? string.Empty,
                        ExpectedRemoteTipSha = remotePre?.LiveRemoteTipSha ?? string.Empty,
                        Summary = remotePre?.BlockReason ?? "缺少有效的远端改写预检结果，已阻止本地连带改写远端。",
                    };
                }

                // Re-verify live protection override right before executing
                var freshLocalProt = await BranchProtectionPolicy.InspectAsync(repoPath, remotePre.LocalBranchName, cancellationToken)
                    .ConfigureAwait(false);
                var freshRemoteProt = await BranchProtectionPolicy.InspectAsync(repoPath, remotePre.RemoteBranchName, cancellationToken)
                    .ConfigureAwait(false);
                if (freshLocalProt.IsEffectivelyProtected || freshRemoteProt.IsEffectivelyProtected)
                {
                    return new Models.HistoryResetOutcome
                    {
                        BlockedByProtection = true,
                        LocalBranchName = preflight.LocalBranchName,
                        OriginalTipSha = preflight.CurrentHeadSha,
                        TargetTipSha = preflight.TargetCommitSha,
                        RemoteName = remotePre.RemoteName,
                        RemoteBranchName = remotePre.RemoteBranchName,
                        ExpectedRemoteTipSha = remotePre.LiveRemoteTipSha,
                        Summary = freshRemoteProt.IsEffectivelyProtected ? freshRemoteProt.Summary : freshLocalProt.Summary,
                    };
                }
            }

            var kind = options.AlsoRewriteRemote
                ? Models.GitOperationKind.RewriteRemoteHistory
                : Models.GitOperationKind.Reset;
            var title = options.AlsoRewriteRemote
                ? $"撤回提交并改写远程历史 ({preflight.LocalBranchName})"
                : $"撤回本地提交 ({preflight.LocalBranchName})";

            var remotePreValid = options.AlsoRewriteRemote ? options.RemoteRewritePreflight : null;

            var execRes = await OperationJournalService.ExecuteGuardedWriteAsync(
                repoPath,
                kind,
                title,
                async ctx =>
                {
                    var steps = new List<Models.HistoryResetStepResult>();

                    if (remotePreValid != null)
                    {
                        var targetError = await RemotePushTarget.VerifyAsync(repoPath, remotePreValid.RemoteName, remotePreValid.PushUrl, cancellationToken).ConfigureAwait(false);
                        if (!string.IsNullOrEmpty(targetError))
                            return RemoteTargetBlocked(targetError);
                        ctx.Record.RemotePushUrlFingerprint = RemotePushTarget.Fingerprint(remotePreValid.PushUrl);
                    }

                    // Re-check untracked / ignored collisions immediately inside the lock
                    var liveCollisions = await DetectUntrackedOrIgnoredPathCollisionsAsync(
                        repoPath,
                        preflight.TargetCommitSha,
                        cancellationToken).ConfigureAwait(false);
                    if (liveCollisions.Count > 0)
                    {
                        var collisionMsg = $"检测到工作区中存在会被目标提交覆盖的未跟踪或被 .gitignore 忽略路径：{string.Join("、", liveCollisions.Take(5))}。为避免丢失文件已阻止执行（GitNest 不会自动运行 git clean）。";
                        return new OperationActionOutcome<Models.HistoryResetOutcome>
                        {
                            Stage = Models.GitOperationStage.Failed,
                            Summary = collisionMsg,
                            DiagnosticDetail = string.Join(Environment.NewLine, liveCollisions),
                            Payload = new Models.HistoryResetOutcome
                            {
                                OperationId = ctx.OperationId,
                                BlockedByUntrackedOrIgnoredCollision = true,
                                LocalBranchName = preflight.LocalBranchName,
                                OriginalTipSha = preflight.CurrentHeadSha,
                                TargetTipSha = preflight.TargetCommitSha,
                                Summary = collisionMsg,
                                DetailMessage = string.Join(Environment.NewLine, liveCollisions),
                                StepResults = steps,
                            },
                        };
                    }

                    // Step 1: Create immutable backup ref before any mutation
                    var backupRes = await BackupRefs.CreateAsync(
                        repoPath,
                        ctx.OperationId,
                        preflight.CurrentHeadSha,
                        ctx.Log,
                        cancellationToken).ConfigureAwait(false);
                    if (!backupRes.Succeeded)
                    {
                        steps.Add(new Models.HistoryResetStepResult
                        {
                            Kind = Models.HistoryResetStepKind.BackupRefCreate,
                            Succeeded = false,
                            Label = "创建本地恢复引用失败",
                            Detail = backupRes.Summary,
                        });
                        return new OperationActionOutcome<Models.HistoryResetOutcome>
                        {
                            Stage = Models.GitOperationStage.Failed,
                            Summary = $"未能创建本地恢复引用，已停止操作：{backupRes.Summary}",
                            DiagnosticDetail = backupRes.Summary,
                            Payload = new Models.HistoryResetOutcome
                            {
                                OperationId = ctx.OperationId,
                                LocalBranchName = preflight.LocalBranchName,
                                OriginalTipSha = preflight.CurrentHeadSha,
                                TargetTipSha = preflight.TargetCommitSha,
                                Summary = $"未能创建本地恢复引用，已停止操作：{backupRes.Summary}",
                                StepResults = steps,
                            },
                        };
                    }

                    ctx.Record.BackupRefName = backupRes.BackupRefName;
                    OperationJournalService.TrySaveRecord(journalStore ?? Models.OperationJournalStore.Instance, ctx.Record);
                    steps.Add(new Models.HistoryResetStepResult
                    {
                        Kind = Models.HistoryResetStepKind.BackupRefCreate,
                        Succeeded = true,
                        Label = $"已创建本地恢复引用：{backupRes.BackupRefName}（指向 {ShortSha(preflight.CurrentHeadSha)}）",
                    });

                    // Step 2: Execute git reset (--mixed or --hard only)
                    var modeFlag = options.Mode == Models.ResetCommitMode.DiscardCodeHard ? "--hard" : "--mixed";
                    var resetArgs = new[] { "reset", modeFlag, preflight.TargetCommitSha };
                    ctx.Log?.AppendLine($"$ git reset {modeFlag} {preflight.TargetCommitSha}");
                    var resetRes = await Command.RunGitDirectAsync(repoPath, cancellationToken, resetArgs).ConfigureAwait(false);
                    AppendRedactedCommandOutput(ctx.Log, resetRes);

                    var headAfterReset = await ResolveRefShaAsync(repoPath, "HEAD", cancellationToken).ConfigureAwait(false);
                    if (!resetRes.IsSuccess || !string.Equals(headAfterReset, preflight.TargetCommitSha, StringComparison.Ordinal))
                    {
                        if (string.Equals(headAfterReset, preflight.CurrentHeadSha, StringComparison.Ordinal))
                        {
                            await BackupRefs.DeleteBackupRefAsync(repoPath, backupRes.BackupRefName, preflight.CurrentHeadSha, ctx.Log, cancellationToken)
                                .ConfigureAwait(false);
                            ctx.Record.BackupRefName = string.Empty;
                        }

                        var errText = Models.GitConfigSnapshot.RedactText(FirstNonEmpty(resetRes.StdErr, resetRes.StdOut, "git reset 执行失败。"));
                        steps.Add(new Models.HistoryResetStepResult
                        {
                            Kind = Models.HistoryResetStepKind.LocalBranchReset,
                            Succeeded = false,
                            Label = $"本地分支 '{preflight.LocalBranchName}' 撤回失败",
                            Detail = errText,
                        });
                        return new OperationActionOutcome<Models.HistoryResetOutcome>
                        {
                            Stage = Models.GitOperationStage.Failed,
                            BackupRefName = ctx.Record.BackupRefName,
                            Summary = errText,
                            DiagnosticDetail = errText,
                            Payload = new Models.HistoryResetOutcome
                            {
                                OperationId = ctx.OperationId,
                                LocalBranchName = preflight.LocalBranchName,
                                OriginalTipSha = preflight.CurrentHeadSha,
                                TargetTipSha = preflight.TargetCommitSha,
                                BackupRefName = ctx.Record.BackupRefName,
                                Summary = errText,
                                DetailMessage = errText,
                                StepResults = steps,
                            },
                        };
                    }

                    var modeDisplay = options.Mode == Models.ResetCommitMode.DiscardCodeHard
                        ? "已回到目标提交并丢弃后续代码修改（--hard）"
                        : "已撤回提交并将差异保留在工作区（--mixed）";
                    steps.Add(new Models.HistoryResetStepResult
                    {
                        Kind = Models.HistoryResetStepKind.LocalBranchReset,
                        Succeeded = true,
                        Label = $"本地分支 '{preflight.LocalBranchName}' 已回到 {ShortSha(preflight.TargetCommitSha)}（{modeDisplay}）",
                    });

                    if (!options.AlsoRewriteRemote)
                    {
                        var localSummary = $"已将分支 '{preflight.LocalBranchName}' 撤回到 {ShortSha(preflight.TargetCommitSha)}（{modeDisplay}；恢复引用 {backupRes.BackupRefName} 保留 30 天）。";
                        return new OperationActionOutcome<Models.HistoryResetOutcome>
                        {
                            Stage = Models.GitOperationStage.Succeeded,
                            TargetTipSha = preflight.TargetCommitSha,
                            BackupRefName = backupRes.BackupRefName,
                            Summary = localSummary,
                            Payload = new Models.HistoryResetOutcome
                            {
                                OperationId = ctx.OperationId,
                                Succeeded = true,
                                LocalBranchReset = true,
                                LocalBranchName = preflight.LocalBranchName,
                                OriginalTipSha = preflight.CurrentHeadSha,
                                TargetTipSha = preflight.TargetCommitSha,
                                BackupRefName = backupRes.BackupRefName,
                                Summary = localSummary,
                                StepResults = steps,
                            },
                        };
                    }

                    // Step 3: Execute explicit --force-with-lease remote push using frozen LiveRemoteTipSha
                    return await ExecuteLeasePushAndEvaluateOutcomeAsync(
                        repoPath,
                        ctx,
                        preflight.LocalBranchName,
                        preflight.CurrentHeadSha,
                        preflight.TargetCommitSha,
                        remotePreValid.RemoteName,
                        remotePreValid.RemoteBranchName,
                        remotePreValid.LiveRemoteTipSha,
                        remotePreValid.PushUrl,
                        backupRes.BackupRefName,
                        localResetAlreadyPerformed: true,
                        steps,
                        cancellationToken).ConfigureAwait(false);
                },
                expectedSnapshot: preflight.Snapshot,
                verifyOptions: new Models.OperationSnapshotVerifyOptions
                {
                    RequireSameRepoIdentity = true,
                    RequireSameHead = true,
                    RequireSameInProgressState = true,
                    RequireCleanWorkingTree = true,
                    RequireSameWatchedRefs = true,
                    CheckGitLocks = true,
                    CheckIndexLock = true,
                    BranchLockToCheck = preflight.LocalBranchName,
                },
                branchName: preflight.LocalBranchName,
                remoteName: remotePreValid?.RemoteName,
                remoteBranchName: remotePreValid?.RemoteBranchName,
                originalTipSha: preflight.CurrentHeadSha,
                expectedTargetTipSha: preflight.TargetCommitSha,
                expectedRemoteTipSha: remotePreValid?.LiveRemoteTipSha,
                checkIndexLock: true,
                branchLockToCheck: preflight.LocalBranchName,
                journalStore: journalStore,
                outerLog: outerLog,
                cancellationToken: cancellationToken).ConfigureAwait(false);

            return MapExecutionResultToOutcome(execRes, preflight.LocalBranchName, preflight.CurrentHeadSha, preflight.TargetCommitSha, remotePreValid);
        }

        public static async Task<Models.HistoryResetOutcome> ExecuteRemoteRewriteOnlyAsync(
            string repoPath,
            Models.RemoteRewriteOptions options,
            Models.OperationJournalStore journalStore = null,
            Models.ICommandLog outerLog = null,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(options);
            ArgumentNullException.ThrowIfNull(options.Preflight);

            var preflight = options.Preflight;
            if (!preflight.CanRewriteRemote)
            {
                return new Models.HistoryResetOutcome
                {
                    BlockedByProtection = preflight.BlockedByProtection,
                    BlockedByUnknownRemoteState = preflight.RemoteStateUnknown,
                    BlockedByDivergedRemote = preflight.RemoteDivergedWithUnintegratedCommits,
                    LocalBranchName = preflight.LocalBranchName,
                    OriginalTipSha = preflight.LiveRemoteTipSha,
                    TargetTipSha = preflight.TargetTipSha,
                    RemoteName = preflight.RemoteName,
                    RemoteBranchName = preflight.RemoteBranchName,
                    ExpectedRemoteTipSha = preflight.LiveRemoteTipSha,
                    Summary = preflight.BlockReason,
                };
            }

            var freshLocalProt = await BranchProtectionPolicy.InspectAsync(repoPath, preflight.LocalBranchName, cancellationToken).ConfigureAwait(false);
            var freshRemoteProt = await BranchProtectionPolicy.InspectAsync(repoPath, preflight.RemoteBranchName, cancellationToken).ConfigureAwait(false);
            if (freshLocalProt.IsEffectivelyProtected || freshRemoteProt.IsEffectivelyProtected)
            {
                return new Models.HistoryResetOutcome
                {
                    BlockedByProtection = true,
                    LocalBranchName = preflight.LocalBranchName,
                    OriginalTipSha = preflight.LiveRemoteTipSha,
                    TargetTipSha = preflight.TargetTipSha,
                    RemoteName = preflight.RemoteName,
                    RemoteBranchName = preflight.RemoteBranchName,
                    ExpectedRemoteTipSha = preflight.LiveRemoteTipSha,
                    Summary = freshRemoteProt.IsEffectivelyProtected ? freshRemoteProt.Summary : freshLocalProt.Summary,
                };
            }

            var title = $"改写远程分支历史 ({preflight.RemoteName}/{preflight.RemoteBranchName})";
            var execRes = await OperationJournalService.ExecuteGuardedWriteAsync(
                repoPath,
                Models.GitOperationKind.RewriteRemoteHistory,
                title,
                async ctx =>
                {
                    var steps = new List<Models.HistoryResetStepResult>();
                    var targetError = await RemotePushTarget.VerifyAsync(repoPath, preflight.RemoteName, preflight.PushUrl, cancellationToken).ConfigureAwait(false);
                    if (!string.IsNullOrEmpty(targetError))
                        return RemoteTargetBlocked(targetError);
                    ctx.Record.RemotePushUrlFingerprint = RemotePushTarget.Fingerprint(preflight.PushUrl);
                    var backupRes = await BackupRefs.CreateAsync(
                        repoPath,
                        ctx.OperationId,
                        preflight.LiveRemoteTipSha,
                        ctx.Log,
                        cancellationToken).ConfigureAwait(false);
                    if (!backupRes.Succeeded)
                    {
                        steps.Add(new Models.HistoryResetStepResult
                        {
                            Kind = Models.HistoryResetStepKind.BackupRefCreate,
                            Succeeded = false,
                            Label = "创建远端提交本地恢复引用失败",
                            Detail = backupRes.Summary,
                        });
                        return new OperationActionOutcome<Models.HistoryResetOutcome>
                        {
                            Stage = Models.GitOperationStage.Failed,
                            Summary = $"未能保存远端原提交恢复引用：{backupRes.Summary}",
                            Payload = new Models.HistoryResetOutcome
                            {
                                OperationId = ctx.OperationId,
                                LocalBranchName = preflight.LocalBranchName,
                                OriginalTipSha = preflight.LiveRemoteTipSha,
                                TargetTipSha = preflight.TargetTipSha,
                                RemoteName = preflight.RemoteName,
                                RemoteBranchName = preflight.RemoteBranchName,
                                ExpectedRemoteTipSha = preflight.LiveRemoteTipSha,
                                Summary = backupRes.Summary,
                                StepResults = steps,
                            },
                        };
                    }

                    ctx.Record.BackupRefName = backupRes.BackupRefName;
                    OperationJournalService.TrySaveRecord(journalStore ?? Models.OperationJournalStore.Instance, ctx.Record);
                    steps.Add(new Models.HistoryResetStepResult
                    {
                        Kind = Models.HistoryResetStepKind.BackupRefCreate,
                        Succeeded = true,
                        Label = $"已保存原远端提交恢复引用：{backupRes.BackupRefName}（指向 {ShortSha(preflight.LiveRemoteTipSha)}）",
                    });

                    return await ExecuteLeasePushAndEvaluateOutcomeAsync(
                        repoPath,
                        ctx,
                        preflight.LocalBranchName,
                        preflight.LiveRemoteTipSha,
                        preflight.TargetTipSha,
                        preflight.RemoteName,
                        preflight.RemoteBranchName,
                        preflight.LiveRemoteTipSha,
                        preflight.PushUrl,
                        backupRes.BackupRefName,
                        localResetAlreadyPerformed: false,
                        steps,
                        cancellationToken).ConfigureAwait(false);
                },
                expectedSnapshot: preflight.Snapshot,
                verifyOptions: new Models.OperationSnapshotVerifyOptions
                {
                    RequireSameRepoIdentity = true,
                    RequireSameHead = false,
                    RequireSameInProgressState = false,
                    RequireCleanWorkingTree = false,
                    RequireSameWatchedRefs = true,
                    CheckGitLocks = true,
                    CheckIndexLock = false,
                    BranchLockToCheck = preflight.LocalBranchName,
                },
                branchName: preflight.LocalBranchName,
                remoteName: preflight.RemoteName,
                remoteBranchName: preflight.RemoteBranchName,
                originalTipSha: preflight.LiveRemoteTipSha,
                expectedTargetTipSha: preflight.TargetTipSha,
                expectedRemoteTipSha: preflight.LiveRemoteTipSha,
                checkIndexLock: false,
                branchLockToCheck: preflight.LocalBranchName,
                journalStore: journalStore,
                outerLog: outerLog,
                cancellationToken: cancellationToken).ConfigureAwait(false);

            return MapExecutionResultToOutcome(execRes, preflight.LocalBranchName, preflight.LiveRemoteTipSha, preflight.TargetTipSha, preflight);
        }

        public static async Task<Models.ResetWithoutCheckoutPreflight> InspectWithoutCheckoutResetAsync(
            string repoPath,
            string branchName,
            string targetCommitRef,
            CancellationToken cancellationToken = default)
        {
            var validBranch = CommandArgumentHelper.ValidateNonOptionToken(branchName, nameof(branchName));
            var (gitDir, commonDir) = await Models.RepositoryWriteGuard.ResolveGitDirectoriesAsync(repoPath, cancellationToken)
                .ConfigureAwait(false);
            var fullRef = "refs/heads/" + validBranch;
            var branchTip = await ResolveRefShaAsync(repoPath, fullRef, cancellationToken).ConfigureAwait(false);
            var targetSha = await ResolveCommitShaAsync(repoPath, targetCommitRef, cancellationToken).ConfigureAwait(false);
            var targetSubject = string.IsNullOrEmpty(targetSha)
                ? string.Empty
                : await ReadCommitSubjectAsync(repoPath, targetSha, cancellationToken).ConfigureAwait(false);

            var protection = await BranchProtectionPolicy.InspectAsync(repoPath, validBranch, cancellationToken)
                .ConfigureAwait(false);

            var currentBranch = await ReadCurrentBranchNameAsync(repoPath, cancellationToken).ConfigureAwait(false);
            var isCurrentBranch = string.Equals(currentBranch, validBranch, StringComparison.Ordinal);

            var worktrees = await new Worktree(repoPath).ReadAllAsync().ConfigureAwait(false);
            var normalizedRepo = NormalizeDirectoryPath(repoPath);
            var pathComp = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            var otherWorktreePath = string.Empty;
            foreach (var wt in worktrees)
            {
                if (!wt.IsBare && string.Equals(wt.Branch, fullRef, StringComparison.Ordinal) &&
                    !string.Equals(NormalizeDirectoryPath(wt.FullPath), normalizedRepo, pathComp))
                {
                    otherWorktreePath = wt.FullPath;
                    break;
                }
            }

            var isCheckedOutElsewhere = !string.IsNullOrEmpty(otherWorktreePath);
            var isSameTip = !string.IsNullOrEmpty(branchTip) && string.Equals(branchTip, targetSha, StringComparison.Ordinal);
            var isFastForward = !string.IsNullOrEmpty(branchTip) && !string.IsNullOrEmpty(targetSha) &&
                !isSameTip && await IsAncestorAsync(repoPath, branchTip, targetSha, cancellationToken).ConfigureAwait(false);
            var firstParentChain = string.IsNullOrEmpty(branchTip)
                ? new List<string>()
                : await ReadFirstParentChainAsync(repoPath, branchTip, cancellationToken).ConfigureAwait(false);
            var firstParentSet = new HashSet<string>(firstParentChain, StringComparer.Ordinal);
            var inFirstParent = !string.IsNullOrEmpty(targetSha) && firstParentSet.Contains(targetSha);

            IReadOnlyList<Models.ResetCommitSummary> commitsToRemove = Array.Empty<Models.ResetCommitSummary>();
            IReadOnlyList<Models.ResetAuthorAttribution> authors = Array.Empty<Models.ResetAuthorAttribution>();
            if (!string.IsNullOrEmpty(branchTip) && !string.IsNullOrEmpty(targetSha) && !isSameTip && !isFastForward)
            {
                commitsToRemove = await ReadCommitRangeAsync(repoPath, targetSha, branchTip, firstParentSet, cancellationToken)
                    .ConfigureAwait(false);
                authors = AggregateAuthors(commitsToRemove);
            }

            string blockReason = string.Empty;
            if (string.IsNullOrEmpty(branchTip))
                blockReason = $"本地分支 '{validBranch}' 不存在。";
            else if (string.IsNullOrEmpty(targetSha))
                blockReason = $"无法解析目标提交 '{targetCommitRef}'。";
            else if (isCurrentBranch)
                blockReason = $"分支 '{validBranch}' 当前正在本工作区检出，请使用当前分支的“回到此提交”流程。";
            else if (isCheckedOutElsewhere)
                blockReason = $"分支 '{validBranch}' 正在另一工作树 '{otherWorktreePath}' 中检出，禁止在未切换的情况下强行改写其分支指针，以免导致该工作树索引/工作区错乱。";
            else if (isSameTip)
                blockReason = $"分支 '{validBranch}' 已指向目标提交 {ShortSha(targetSha)}。";
            else if (!isFastForward && protection.IsEffectivelyProtected)
                blockReason = protection.Summary;

            var snapshot = await CaptureOperationSnapshot.CaptureAsync(
                repoPath,
                new Models.OperationSnapshotCaptureOptions { RefsToWatch = string.IsNullOrEmpty(branchTip) ? Array.Empty<string>() : [fullRef] },
                cancellationToken).ConfigureAwait(false);

            var canExecute = string.IsNullOrEmpty(blockReason);
            var summary = canExecute
                ? (isFastForward
                    ? $"可将未检出分支 '{validBranch}' 快进移动至 {ShortSha(targetSha)}。"
                    : $"将把未检出分支 '{validBranch}' 从 {ShortSha(branchTip)} 重置到 {ShortSha(targetSha)}（将从该分支移除 {commitsToRemove.Count} 个提交）。")
                : blockReason;

            return new Models.ResetWithoutCheckoutPreflight
            {
                RepoPath = repoPath,
                CanonicalGitDir = gitDir,
                CanonicalCommonDir = commonDir,
                BranchName = validBranch,
                BranchFullRef = fullRef,
                CurrentBranchTipSha = branchTip,
                TargetCommitSha = targetSha,
                TargetCommitSubject = targetSubject,
                IsCurrentBranch = isCurrentBranch,
                IsCheckedOutInOtherWorktree = isCheckedOutElsewhere,
                CheckedOutWorktreePath = otherWorktreePath,
                IsSameTip = isSameTip,
                IsFastForward = isFastForward,
                TargetIsInFirstParentChain = inFirstParent,
                Protection = protection,
                CommitsToRemove = commitsToRemove,
                Authors = authors,
                CanExecute = canExecute,
                BlockReason = blockReason,
                Snapshot = snapshot,
                Summary = summary,
            };
        }

        public static async Task<Models.HistoryResetOutcome> ExecuteWithoutCheckoutResetAsync(
            string repoPath,
            Models.ResetWithoutCheckoutOptions options,
            Models.OperationJournalStore journalStore = null,
            Models.ICommandLog outerLog = null,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(options);
            ArgumentNullException.ThrowIfNull(options.Preflight);

            var preflight = options.Preflight;
            if (!preflight.CanExecute)
            {
                return new Models.HistoryResetOutcome
                {
                    BlockedByProtection = preflight.BlockedByProtection || preflight.IsCheckedOutInOtherWorktree || preflight.IsCurrentBranch,
                    LocalBranchName = preflight.BranchName,
                    OriginalTipSha = preflight.CurrentBranchTipSha,
                    TargetTipSha = preflight.TargetCommitSha,
                    Summary = preflight.BlockReason,
                };
            }

            var execRes = await OperationJournalService.ExecuteGuardedWriteAsync(
                repoPath,
                Models.GitOperationKind.Reset,
                $"调整未检出分支 ({preflight.BranchName})",
                async ctx =>
                {
                    var steps = new List<Models.HistoryResetStepResult>();

                    // Re-verify no worktree checked out this branch after preflight
                    var currentBranchNow = await ReadCurrentBranchNameAsync(repoPath, cancellationToken).ConfigureAwait(false);
                    var worktrees = await new Worktree(repoPath).ReadAllAsync().ConfigureAwait(false);
                    if (string.Equals(currentBranchNow, preflight.BranchName, StringComparison.Ordinal) ||
                        worktrees.Any(w => !w.IsBare && string.Equals(w.Branch, preflight.BranchFullRef, StringComparison.Ordinal)))
                    {
                        const string busyMsg = "分支当前已被工作树检出，已阻止直接改写分支指针。";
                        return new OperationActionOutcome<Models.HistoryResetOutcome>
                        {
                            Stage = Models.GitOperationStage.Failed,
                            Summary = busyMsg,
                            Payload = new Models.HistoryResetOutcome
                            {
                                OperationId = ctx.OperationId,
                                BlockedByProtection = true,
                                LocalBranchName = preflight.BranchName,
                                OriginalTipSha = preflight.CurrentBranchTipSha,
                                TargetTipSha = preflight.TargetCommitSha,
                                Summary = busyMsg,
                            },
                        };
                    }

                    string createdBackupRef = string.Empty;
                    if (!preflight.IsFastForward)
                    {
                        var backupRes = await BackupRefs.CreateAsync(
                            repoPath,
                            ctx.OperationId,
                            preflight.CurrentBranchTipSha,
                            ctx.Log,
                            cancellationToken).ConfigureAwait(false);
                        if (!backupRes.Succeeded)
                        {
                            return new OperationActionOutcome<Models.HistoryResetOutcome>
                            {
                                Stage = Models.GitOperationStage.Failed,
                                Summary = $"未能创建本地恢复引用：{backupRes.Summary}",
                                Payload = new Models.HistoryResetOutcome
                                {
                                    OperationId = ctx.OperationId,
                                    LocalBranchName = preflight.BranchName,
                                    OriginalTipSha = preflight.CurrentBranchTipSha,
                                    TargetTipSha = preflight.TargetCommitSha,
                                    Summary = backupRes.Summary,
                                },
                            };
                        }

                        createdBackupRef = backupRes.BackupRefName;
                        ctx.Record.BackupRefName = createdBackupRef;
                        OperationJournalService.TrySaveRecord(journalStore ?? Models.OperationJournalStore.Instance, ctx.Record);
                        steps.Add(new Models.HistoryResetStepResult
                        {
                            Kind = Models.HistoryResetStepKind.BackupRefCreate,
                            Succeeded = true,
                            Label = $"已保存恢复引用：{createdBackupRef}",
                        });
                    }

                    ctx.Log?.AppendLine($"$ git update-ref {preflight.BranchFullRef} {preflight.TargetCommitSha} {preflight.CurrentBranchTipSha}");
                    var updRes = await Command.RunGitDirectAsync(
                        repoPath,
                        cancellationToken,
                        "update-ref",
                        "-m",
                        "GitNest: reset branch without checkout",
                        preflight.BranchFullRef,
                        preflight.TargetCommitSha,
                        preflight.CurrentBranchTipSha).ConfigureAwait(false);
                    AppendRedactedCommandOutput(ctx.Log, updRes);

                    if (!updRes.IsSuccess)
                    {
                        if (!string.IsNullOrEmpty(createdBackupRef))
                        {
                            await BackupRefs.DeleteBackupRefAsync(repoPath, createdBackupRef, preflight.CurrentBranchTipSha, ctx.Log, cancellationToken)
                                .ConfigureAwait(false);
                            ctx.Record.BackupRefName = string.Empty;
                        }

                        var errText = Models.GitConfigSnapshot.RedactText(FirstNonEmpty(updRes.StdErr, updRes.StdOut, "更新分支指针失败。"));
                        return new OperationActionOutcome<Models.HistoryResetOutcome>
                        {
                            Stage = Models.GitOperationStage.Failed,
                            Summary = errText,
                            Payload = new Models.HistoryResetOutcome
                            {
                                OperationId = ctx.OperationId,
                                LocalBranchName = preflight.BranchName,
                                OriginalTipSha = preflight.CurrentBranchTipSha,
                                TargetTipSha = preflight.TargetCommitSha,
                                Summary = errText,
                            },
                        };
                    }

                    steps.Add(new Models.HistoryResetStepResult
                    {
                        Kind = Models.HistoryResetStepKind.LocalBranchReset,
                        Succeeded = true,
                        Label = $"分支 '{preflight.BranchName}' 已更新至 {ShortSha(preflight.TargetCommitSha)}",
                    });

                    var okSummary = $"已将分支 '{preflight.BranchName}' 指针调整为 {ShortSha(preflight.TargetCommitSha)}" +
                        (string.IsNullOrEmpty(createdBackupRef) ? "。" : $"（恢复引用：{createdBackupRef}）。");
                    return new OperationActionOutcome<Models.HistoryResetOutcome>
                    {
                        Stage = Models.GitOperationStage.Succeeded,
                        TargetTipSha = preflight.TargetCommitSha,
                        BackupRefName = createdBackupRef,
                        Summary = okSummary,
                        Payload = new Models.HistoryResetOutcome
                        {
                            OperationId = ctx.OperationId,
                            Succeeded = true,
                            LocalBranchReset = true,
                            LocalBranchName = preflight.BranchName,
                            OriginalTipSha = preflight.CurrentBranchTipSha,
                            TargetTipSha = preflight.TargetCommitSha,
                            BackupRefName = createdBackupRef,
                            Summary = okSummary,
                            StepResults = steps,
                        },
                    };
                },
                expectedSnapshot: preflight.Snapshot,
                verifyOptions: new Models.OperationSnapshotVerifyOptions
                {
                    RequireSameRepoIdentity = true,
                    RequireSameHead = false,
                    RequireSameInProgressState = false,
                    RequireCleanWorkingTree = false,
                    RequireSameWatchedRefs = true,
                    CheckGitLocks = true,
                    CheckIndexLock = false,
                    BranchLockToCheck = preflight.BranchName,
                },
                branchName: preflight.BranchName,
                originalTipSha: preflight.CurrentBranchTipSha,
                expectedTargetTipSha: preflight.TargetCommitSha,
                checkIndexLock: false,
                branchLockToCheck: preflight.BranchName,
                journalStore: journalStore,
                outerLog: outerLog,
                cancellationToken: cancellationToken).ConfigureAwait(false);

            return MapExecutionResultToOutcome(execRes, preflight.BranchName, preflight.CurrentBranchTipSha, preflight.TargetCommitSha, null);
        }

        public static async Task<Models.BackupResetRestorePreflight> InspectBackupRestoreForResetAsync(
            string repoPath,
            string backupRefName,
            string targetBranchName = null,
            string sourceOperationId = null,
            Models.OperationJournalStore journalStore = null,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(backupRefName) ||
                !backupRefName.StartsWith(BackupRefs.NamespacePrefix, StringComparison.Ordinal))
            {
                return new Models.BackupResetRestorePreflight
                {
                    RepoPath = repoPath,
                    BackupRefName = backupRefName ?? string.Empty,
                    BlockReason = "无效的本地恢复引用名称。",
                    Summary = "无效的本地恢复引用名称。",
                };
            }

            var store = journalStore ?? Models.OperationJournalStore.Instance;
            Models.GitOperationRecord linkedRecord = null;
            if (!string.IsNullOrWhiteSpace(sourceOperationId))
                linkedRecord = store?.TryGet(sourceOperationId);

            string commonDirLookup = string.Empty;
            if (store != null)
            {
                (_, commonDirLookup) = await Models.RepositoryWriteGuard.ResolveGitDirectoriesAsync(repoPath, cancellationToken)
                    .ConfigureAwait(false);
                if (linkedRecord == null)
                {
                    linkedRecord = store.GetByRepository(commonDirLookup)
                        .FirstOrDefault(r => string.Equals(r.BackupRefName, backupRefName, StringComparison.Ordinal));
                }
            }

            var resolvedBranchName = !string.IsNullOrWhiteSpace(targetBranchName)
                ? targetBranchName.Trim()
                : linkedRecord?.BranchName ?? string.Empty;

            var backupSha = await ResolveRefShaAsync(repoPath, backupRefName, cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrEmpty(backupSha))
            {
                return new Models.BackupResetRestorePreflight
                {
                    RepoPath = repoPath,
                    BackupRefName = backupRefName,
                    BackupExists = false,
                    TargetBranchName = resolvedBranchName,
                    BlockReason = $"恢复引用 '{backupRefName}' 不存在或已被清理。",
                    Summary = "恢复引用不存在。",
                };
            }

            var backupSubject = await ReadCommitSubjectAsync(repoPath, backupSha, cancellationToken).ConfigureAwait(false);
            var suggestedNewBranch = await GenerateUniqueRecoveryBranchNameAsync(repoPath, resolvedBranchName, backupSha, cancellationToken)
                .ConfigureAwait(false);

            var (gitDir, resolvedCommonDir) = await Models.RepositoryWriteGuard.ResolveGitDirectoriesAsync(repoPath, cancellationToken)
                .ConfigureAwait(false);
            if (string.IsNullOrEmpty(commonDirLookup))
                commonDirLookup = resolvedCommonDir;
            var inProgress = CaptureOperationSnapshot.DetectInProgressKind(gitDir);
            var snapshot = await CaptureOperationSnapshot.CaptureAsync(
                repoPath,
                new Models.OperationSnapshotCaptureOptions
                {
                    RefsToWatch = string.IsNullOrEmpty(resolvedBranchName)
                        ? Array.Empty<string>()
                        : new[] { "refs/heads/" + resolvedBranchName },
                },
                cancellationToken).ConfigureAwait(false);

            if (string.IsNullOrEmpty(resolvedBranchName))
            {
                return new Models.BackupResetRestorePreflight
                {
                    RepoPath = repoPath,
                    BackupRefName = backupRefName,
                    BackupExists = true,
                    BackupCommitSha = backupSha,
                    BackupCommitSubject = backupSubject,
                    CanCreateNewRecoveryBranch = true,
                    SuggestedNewBranchName = suggestedNewBranch,
                    CanUpdateExistingBranchSafely = false,
                    BlockReason = "未指定目标分支名，请选择恢复到新分支。",
                    Snapshot = snapshot,
                    Summary = $"可从 {backupRefName} 创建恢复分支 '{suggestedNewBranch}'（{ShortSha(backupSha)}）。",
                };
            }

            var fullTargetRef = "refs/heads/" + resolvedBranchName;
            var targetBranchTip = await ResolveRefShaAsync(repoPath, fullTargetRef, cancellationToken).ConfigureAwait(false);
            var branchExists = !string.IsNullOrEmpty(targetBranchTip);

            var currentBranch = await ReadCurrentBranchNameAsync(repoPath, cancellationToken).ConfigureAwait(false);
            var isCurrentHead = string.Equals(currentBranch, resolvedBranchName, StringComparison.Ordinal);

            var worktrees = await new Worktree(repoPath).ReadAllAsync().ConfigureAwait(false);
            var normalizedRepo = NormalizeDirectoryPath(repoPath);
            var pathComp = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            var otherWorktree = worktrees.FirstOrDefault(w =>
                !w.IsBare &&
                string.Equals(w.Branch, fullTargetRef, StringComparison.Ordinal) &&
                !string.Equals(NormalizeDirectoryPath(w.FullPath), normalizedRepo, pathComp));

            var isCheckedOutElsewhere = otherWorktree != null;
            var expectedPostResetTip = linkedRecord?.TargetTipSha ?? string.Empty;

            bool hasNewerCommits = false;
            IReadOnlyList<Models.ResetCommitSummary> newerCommits = Array.Empty<Models.ResetCommitSummary>();
            if (branchExists && !string.Equals(targetBranchTip, backupSha, StringComparison.Ordinal))
            {
                if (!string.IsNullOrEmpty(expectedPostResetTip))
                {
                    if (!string.Equals(targetBranchTip, expectedPostResetTip, StringComparison.Ordinal))
                    {
                        hasNewerCommits = true;
                        var baseForDiff = await IsAncestorAsync(repoPath, expectedPostResetTip, targetBranchTip, cancellationToken)
                            .ConfigureAwait(false)
                            ? expectedPostResetTip
                            : backupSha;
                        var headSet = new HashSet<string>(
                            await ReadFirstParentChainAsync(repoPath, targetBranchTip, cancellationToken).ConfigureAwait(false),
                            StringComparer.Ordinal);
                        newerCommits = await ReadCommitRangeAsync(repoPath, baseForDiff, targetBranchTip, headSet, cancellationToken)
                            .ConfigureAwait(false);
                    }
                }
                else
                {
                    // Without journal target, any non-ancestor tip implies branch has unrelated/newer work.
                    var branchBehindBackup = await IsAncestorAsync(repoPath, targetBranchTip, backupSha, cancellationToken)
                        .ConfigureAwait(false);
                    if (!branchBehindBackup)
                    {
                        hasNewerCommits = true;
                        var headSet = new HashSet<string>(
                            await ReadFirstParentChainAsync(repoPath, targetBranchTip, cancellationToken).ConfigureAwait(false),
                            StringComparer.Ordinal);
                        newerCommits = await ReadCommitRangeAsync(repoPath, backupSha, targetBranchTip, headSet, cancellationToken)
                            .ConfigureAwait(false);
                    }
                }
            }

            IReadOnlyList<string> collisions = Array.Empty<string>();
            if (isCurrentHead && snapshot.IsWorkingTreeClean)
            {
                collisions = await DetectUntrackedOrIgnoredPathCollisionsAsync(repoPath, backupSha, cancellationToken)
                    .ConfigureAwait(false);
            }

            string blockReason = string.Empty;
            if (!branchExists)
            {
                // Branch can be restored cleanly by recreating at its exact name or new name.
            }
            else if (isCheckedOutElsewhere)
            {
                blockReason = $"分支 '{resolvedBranchName}' 正在工作树 '{otherWorktree.FullPath}' 中检出，已禁止直接覆盖该分支；可改用新建恢复分支 '{suggestedNewBranch}'。";
            }
            else if (hasNewerCommits)
            {
                blockReason = $"分支 '{resolvedBranchName}' 在撤回后已有新提交或外部变动（现指向 {ShortSha(targetBranchTip)}），为避免静默覆盖新提交已阻止覆盖原分支；请改为创建新恢复分支 '{suggestedNewBranch}'。";
            }
            else if (isCurrentHead && inProgress != Models.GitInProgressKind.None)
            {
                blockReason = $"当前工作区正处于 {inProgress} 状态，无法直接恢复当前分支。";
            }
            else if (isCurrentHead && !snapshot.IsWorkingTreeClean)
            {
                blockReason = "当前工作区或暂存区不干净，为避免覆盖未提交改动，已阻止直接恢复当前分支；请先清理工作区或恢复为新分支。";
            }
            else if (isCurrentHead && collisions.Count > 0)
            {
                blockReason = $"恢复操作会覆盖未跟踪或被忽略的路径：{string.Join("、", collisions.Take(5))}。";
            }

            var canUpdateExistingSafely = string.IsNullOrEmpty(blockReason);
            var summary = canUpdateExistingSafely
                ? $"检查通过：可将分支 '{resolvedBranchName}' 安全恢复到备份提交 {ShortSha(backupSha)}。"
                : blockReason;

            return new Models.BackupResetRestorePreflight
            {
                RepoPath = repoPath,
                CanonicalCommonDir = commonDirLookup,
                BackupRefName = backupRefName,
                BackupExists = true,
                BackupCommitSha = backupSha,
                BackupCommitSubject = backupSubject,
                TargetBranchName = resolvedBranchName,
                TargetBranchExists = branchExists,
                TargetBranchCurrentTipSha = targetBranchTip,
                ExpectedPostResetTipSha = expectedPostResetTip,
                IsTargetBranchCurrentHead = isCurrentHead,
                IsCheckedOutInOtherWorktree = isCheckedOutElsewhere,
                CheckedOutWorktreePath = otherWorktree?.FullPath ?? string.Empty,
                InProgressKind = inProgress,
                IsWorkingTreeAndIndexClean = snapshot.IsWorkingTreeClean,
                CollisionPaths = collisions,
                HasNewerCommitsAfterReset = hasNewerCommits,
                NewerCommits = newerCommits,
                CanCreateNewRecoveryBranch = true,
                SuggestedNewBranchName = suggestedNewBranch,
                CanUpdateExistingBranchSafely = canUpdateExistingSafely,
                BlockReason = blockReason,
                Snapshot = snapshot,
                Summary = summary,
            };
        }

        public static async Task<Models.GitBackupRestoreResult> RestoreFromBackupAsync(
            string repoPath,
            Models.BackupResetRestoreOptions options,
            Models.OperationJournalStore journalStore = null,
            Models.ICommandLog outerLog = null,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(options);
            ArgumentNullException.ThrowIfNull(options.Preflight);

            var preflight = options.Preflight;
            if (!preflight.BackupExists || string.IsNullOrEmpty(preflight.BackupCommitSha))
            {
                return new Models.GitBackupRestoreResult
                {
                    Succeeded = false,
                    BranchName = preflight.TargetBranchName,
                    BackupRefName = preflight.BackupRefName,
                    Summary = "恢复引用不存在或已过期。",
                };
            }

            var store = journalStore ?? Models.OperationJournalStore.Instance;

            var commonDirForAck = preflight.CanonicalCommonDir;

            if (!options.RestoreOntoExistingBranch)
            {
                var chosenNewBranch = string.IsNullOrWhiteSpace(options.NewBranchName)
                    ? preflight.SuggestedNewBranchName
                    : options.NewBranchName.Trim();

                var createRes = await BackupRefs.RestoreBranchFromBackupAsync(
                    repoPath,
                    chosenNewBranch,
                    preflight.BackupRefName,
                    outerLog,
                    cancellationToken).ConfigureAwait(false);

                if (createRes.Succeeded)
                    AcknowledgeSourceOperation(store, options.SourceOperationId, preflight.BackupRefName, commonDirForAck);

                return createRes;
            }

            if (!preflight.TargetBranchExists)
            {
                var createMissingRes = await BackupRefs.RestoreBranchFromBackupAsync(
                    repoPath,
                    preflight.TargetBranchName,
                    preflight.BackupRefName,
                    outerLog,
                    cancellationToken).ConfigureAwait(false);

                if (createMissingRes.Succeeded)
                    AcknowledgeSourceOperation(store, options.SourceOperationId, preflight.BackupRefName, commonDirForAck);

                return createMissingRes;
            }

            if (!preflight.CanUpdateExistingBranchSafely)
            {
                return new Models.GitBackupRestoreResult
                {
                    Succeeded = false,
                    BlockedByExistingBranch = true,
                    BlockedByNewerCommits = preflight.HasNewerCommitsAfterReset,
                    BlockedByDirtyState = preflight.IsTargetBranchCurrentHead && (!preflight.IsWorkingTreeAndIndexClean || preflight.CollisionPaths.Count > 0),
                    BranchName = preflight.TargetBranchName,
                    BackupRefName = preflight.BackupRefName,
                    RestoredCommitSha = preflight.BackupCommitSha,
                    Summary = preflight.BlockReason,
                };
            }

            var execRes = await OperationJournalService.ExecuteGuardedWriteAsync(
                repoPath,
                Models.GitOperationKind.RestoreBackup,
                $"从备份恢复分支 ({preflight.TargetBranchName})",
                async ctx =>
                {
                    if (preflight.IsTargetBranchCurrentHead)
                    {
                        var liveCollisions = await DetectUntrackedOrIgnoredPathCollisionsAsync(
                            repoPath,
                            preflight.BackupCommitSha,
                            cancellationToken).ConfigureAwait(false);
                        if (liveCollisions.Count > 0)
                        {
                            var msg = $"检测到会覆盖未跟踪或被忽略文件的路径：{string.Join("、", liveCollisions.Take(5))}。";
                            return new OperationActionOutcome<Models.GitBackupRestoreResult>
                            {
                                Stage = Models.GitOperationStage.Failed,
                                Summary = msg,
                                Payload = new Models.GitBackupRestoreResult
                                {
                                    Succeeded = false,
                                    BlockedByDirtyState = true,
                                    BranchName = preflight.TargetBranchName,
                                    BackupRefName = preflight.BackupRefName,
                                    RestoredCommitSha = preflight.BackupCommitSha,
                                    Summary = msg,
                                },
                            };
                        }

                        ctx.Log?.AppendLine($"$ git reset --hard {preflight.BackupCommitSha}");
                        var resetRes = await Command.RunGitDirectAsync(
                            repoPath,
                            cancellationToken,
                            "reset",
                            "--hard",
                            preflight.BackupCommitSha).ConfigureAwait(false);
                        AppendRedactedCommandOutput(ctx.Log, resetRes);

                        if (!resetRes.IsSuccess)
                        {
                            var err = Models.GitConfigSnapshot.RedactText(FirstNonEmpty(resetRes.StdErr, resetRes.StdOut, "恢复当前分支失败。"));
                            return new OperationActionOutcome<Models.GitBackupRestoreResult>
                            {
                                Stage = Models.GitOperationStage.Failed,
                                Summary = err,
                                Payload = new Models.GitBackupRestoreResult
                                {
                                    Succeeded = false,
                                    BranchName = preflight.TargetBranchName,
                                    BackupRefName = preflight.BackupRefName,
                                    RestoredCommitSha = preflight.BackupCommitSha,
                                    Summary = err,
                                },
                            };
                        }
                    }
                    else
                    {
                        var fullTargetRef = "refs/heads/" + preflight.TargetBranchName;
                        ctx.Log?.AppendLine($"$ git update-ref {fullTargetRef} {preflight.BackupCommitSha} {preflight.TargetBranchCurrentTipSha}");
                        var updRes = await Command.RunGitDirectAsync(
                            repoPath,
                            cancellationToken,
                            "update-ref",
                            "-m",
                            "GitNest: restore branch from backup ref",
                            fullTargetRef,
                            preflight.BackupCommitSha,
                            preflight.TargetBranchCurrentTipSha).ConfigureAwait(false);
                        AppendRedactedCommandOutput(ctx.Log, updRes);

                        if (!updRes.IsSuccess)
                        {
                            var err = Models.GitConfigSnapshot.RedactText(FirstNonEmpty(updRes.StdErr, updRes.StdOut, "恢复原分支指针失败。"));
                            return new OperationActionOutcome<Models.HistoryResetOutcome>
                            {
                                Stage = Models.GitOperationStage.Failed,
                                Summary = err,
                            } is var _
                                ? new OperationActionOutcome<Models.GitBackupRestoreResult>
                                {
                                    Stage = Models.GitOperationStage.Failed,
                                    Summary = err,
                                    Payload = new Models.GitBackupRestoreResult
                                    {
                                        Succeeded = false,
                                        BranchName = preflight.TargetBranchName,
                                        BackupRefName = preflight.BackupRefName,
                                        RestoredCommitSha = preflight.BackupCommitSha,
                                        Summary = err,
                                    },
                                }
                                : null;
                        }
                    }

                    var okMsg = $"已将分支 '{preflight.TargetBranchName}' 从备份引用 {preflight.BackupRefName} 恢复至提交 {ShortSha(preflight.BackupCommitSha)}。";
                    return new OperationActionOutcome<Models.GitBackupRestoreResult>
                    {
                        Stage = Models.GitOperationStage.Succeeded,
                        TargetTipSha = preflight.BackupCommitSha,
                        BackupRefName = preflight.BackupRefName,
                        Summary = okMsg,
                        Payload = new Models.GitBackupRestoreResult
                        {
                            Succeeded = true,
                            UpdatedExistingBranch = true,
                            CheckedOutWorktreeUpdated = preflight.IsTargetBranchCurrentHead,
                            BranchName = preflight.TargetBranchName,
                            BackupRefName = preflight.BackupRefName,
                            RestoredCommitSha = preflight.BackupCommitSha,
                            Summary = okMsg,
                        },
                    };
                },
                expectedSnapshot: preflight.Snapshot,
                verifyOptions: new Models.OperationSnapshotVerifyOptions
                {
                    RequireSameRepoIdentity = true,
                    RequireSameHead = preflight.IsTargetBranchCurrentHead,
                    RequireSameInProgressState = preflight.IsTargetBranchCurrentHead,
                    RequireCleanWorkingTree = preflight.IsTargetBranchCurrentHead,
                    RequireSameWatchedRefs = true,
                    CheckGitLocks = true,
                    CheckIndexLock = preflight.IsTargetBranchCurrentHead,
                    BranchLockToCheck = preflight.TargetBranchName,
                },
                branchName: preflight.TargetBranchName,
                originalTipSha: preflight.TargetBranchCurrentTipSha,
                expectedTargetTipSha: preflight.BackupCommitSha,
                checkIndexLock: preflight.IsTargetBranchCurrentHead,
                branchLockToCheck: preflight.TargetBranchName,
                journalStore: store,
                outerLog: outerLog,
                cancellationToken: cancellationToken).ConfigureAwait(false);

            if (execRes.Succeeded && execRes.Payload != null)
            {
                AcknowledgeSourceOperation(store, options.SourceOperationId, preflight.BackupRefName, commonDirForAck);
                return execRes.Payload;
            }

            return execRes.Payload ?? new Models.GitBackupRestoreResult
            {
                Succeeded = false,
                BlockedByLock = execRes.BlockedByLock,
                BlockedBySnapshot = execRes.BlockedBySnapshot,
                BranchName = preflight.TargetBranchName,
                BackupRefName = preflight.BackupRefName,
                RestoredCommitSha = preflight.BackupCommitSha,
                Summary = execRes.Summary,
            };
        }

        public static async Task<Models.RemoteRewriteJournalVerificationResult> VerifyRemoteRewriteJournalRecordAsync(
            string repoPath,
            Models.GitOperationRecord record,
            Models.OperationJournalStore journalStore = null,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(record);

            if (string.IsNullOrWhiteSpace(record.RemoteName) || string.IsNullOrWhiteSpace(record.RemoteBranchName))
            {
                return new Models.RemoteRewriteJournalVerificationResult
                {
                    QuerySucceeded = false,
                    UpdatedRecord = record,
                    Summary = "日志记录缺少远端信息，无法联网核对。",
                };
            }

            var currentTarget = await RemotePushTarget.ResolveAsync(repoPath, record.RemoteName, cancellationToken).ConfigureAwait(false);
            if (!currentTarget.IsValid || (!string.IsNullOrEmpty(record.RemotePushUrlFingerprint) &&
                !string.Equals(record.RemotePushUrlFingerprint, RemotePushTarget.Fingerprint(currentTarget.Url), StringComparison.Ordinal)))
                return new Models.RemoteRewriteJournalVerificationResult
                {
                    UpdatedRecord = record,
                    Summary = currentTarget.IsValid ? "远端推送地址与操作记录不一致，无法核对原操作结果。" : currentTarget.Error,
                };

            var probe = await BranchDeletionService.ProbeRemoteBranchAsync(
                repoPath,
                record.RemoteName,
                record.RemoteBranchName,
                cancellationToken: cancellationToken,
                expectedPushUrl: currentTarget.Url).ConfigureAwait(false);

            if (!probe.QuerySucceeded)
            {
                return new Models.RemoteRewriteJournalVerificationResult
                {
                    QuerySucceeded = false,
                    RemoteName = record.RemoteName,
                    RemoteBranchName = record.RemoteBranchName,
                    UpdatedRecord = record,
                    Summary = $"联网核对远端状态失败：{probe.ErrorSummary}",
                };
            }

            var liveTip = probe.LiveTipSha ?? string.Empty;
            if (string.Equals(liveTip, record.TargetTipSha, StringComparison.Ordinal))
            {
                record.Stage = Models.GitOperationStage.Succeeded;
                record.StepSummary = $"经联网核对确认：远端 '{record.RemoteName}/{record.RemoteBranchName}' 已完成更新至 {ShortSha(record.TargetTipSha)}。";
                record.IsUnfinishedOnStartup = false;
                record.IsAcknowledged = true;
                record.CompletedAtUtc ??= DateTime.UtcNow;
            }
            else if (string.Equals(liveTip, record.ExpectedRemoteTipSha, StringComparison.Ordinal))
            {
                record.Stage = Models.GitOperationStage.PartialSuccess;
                record.StepSummary = $"经联网核对确认：本地已撤回，远端未更新（仍停留在原提交 {ShortSha(liveTip)}）；可通过本地恢复引用还原。";
            }
            else
            {
                record.Stage = Models.GitOperationStage.PartialSuccess;
                record.StepSummary = $"经联网核对确认：本地已撤回，远端未更新（服务器现指向新提交 {ShortSha(liveTip)}，协作者提交已保留）。";
            }

            record.ReconciliationNote = $"联网核对远端实测 tip：{ShortSha(liveTip)}（目标 {ShortSha(record.TargetTipSha)}，原预期 {ShortSha(record.ExpectedRemoteTipSha)}）";
            OperationJournalService.TrySaveRecord(journalStore ?? Models.OperationJournalStore.Instance, record);

            return new Models.RemoteRewriteJournalVerificationResult
            {
                QuerySucceeded = true,
                RemoteName = record.RemoteName,
                RemoteBranchName = record.RemoteBranchName,
                LiveRemoteTipSha = liveTip,
                UpdatedRecord = record,
                Summary = record.StepSummary,
            };
        }

        private static async Task<OperationActionOutcome<Models.HistoryResetOutcome>> ExecuteLeasePushAndEvaluateOutcomeAsync(
            string repoPath,
            OperationExecutionContext ctx,
            string localBranchName,
            string originalTipSha,
            string targetSha,
            string remoteName,
            string remoteBranchName,
            string expectedRemoteTipSha,
            string confirmedPushUrl,
            string backupRefName,
            bool localResetAlreadyPerformed,
            List<Models.HistoryResetStepResult> steps,
            CancellationToken cancellationToken)
        {
            var targetError = await RemotePushTarget.VerifyAsync(repoPath, remoteName, confirmedPushUrl, cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(targetError))
            {
                return new OperationActionOutcome<Models.HistoryResetOutcome>
                {
                    Stage = localResetAlreadyPerformed ? Models.GitOperationStage.PartialSuccess : Models.GitOperationStage.Failed,
                    BackupRefName = backupRefName,
                    Summary = localResetAlreadyPerformed ? $"本地已撤回，远端未更新：{targetError}" : targetError,
                    Payload = new Models.HistoryResetOutcome
                    {
                        LocalBranchReset = localResetAlreadyPerformed,
                        IsPartialSuccess = localResetAlreadyPerformed,
                        BlockedBySnapshot = true,
                        BackupRefName = backupRefName,
                        Summary = localResetAlreadyPerformed ? $"本地已撤回，远端未更新：{targetError}" : targetError,
                    },
                };
            }
            var leaseArg = $"--force-with-lease=refs/heads/{remoteBranchName}:{expectedRemoteTipSha}";
            var refspecArg = $"{targetSha}:refs/heads/{remoteBranchName}";
            var remotes = await new QueryRemotes(repoPath).GetResultAsync().ConfigureAwait(false);
            var matchedRemote = remotes.Find(r => string.Equals(r.Name, remoteName, StringComparison.Ordinal));
            var pushConfig = RemotePushTarget.CreatePushRemoteConfiguration(confirmedPushUrl, out var pushRemoteName);
            var rawArgs = new[] { "-c", pushConfig, "push", "--progress", "--no-follow-tags", leaseArg, "--", pushRemoteName, refspecArg };

            var pushRaw = await ExecuteRemotePushRawAsync(
                repoPath,
                matchedRemote?.PrivateSSHKey ?? string.Empty,
                rawArgs,
                ctx.Log,
                cancellationToken).ConfigureAwait(false);

            var combinedOutput = Models.GitConfigSnapshot.RedactText($"{pushRaw.StdOut}\n{pushRaw.StdErr}".Trim());
            var prefixText = localResetAlreadyPerformed ? "本地已撤回，远端未更新" : "远端未改写";

            if (pushRaw.IsSuccess)
            {
                await RemotePushTarget.UpdateTrackingRefAsync(repoPath, remoteName, remoteBranchName,
                    targetSha, expectedRemoteTipSha, confirmedPushUrl, CancellationToken.None).ConfigureAwait(false);
                steps.Add(new Models.HistoryResetStepResult
                {
                    Kind = Models.HistoryResetStepKind.RemoteHistoryRewrite,
                    Succeeded = true,
                    Label = $"已按租约保护改写远端 '{remoteName}/{remoteBranchName}' 至 {ShortSha(targetSha)}",
                });
                var okMsg = localResetAlreadyPerformed
                    ? $"已将本地与远端 '{remoteName}/{remoteBranchName}' 同步改写至 {ShortSha(targetSha)}（恢复引用：{backupRefName}，保留 30 天）。"
                    : $"已改写远端分支 '{remoteName}/{remoteBranchName}' 历史至 {ShortSha(targetSha)}（原远端提交恢复引用：{backupRefName}）。";
                return new OperationActionOutcome<Models.HistoryResetOutcome>
                {
                    Stage = Models.GitOperationStage.Succeeded,
                    TargetTipSha = targetSha,
                    BackupRefName = backupRefName,
                    Summary = okMsg,
                    Payload = new Models.HistoryResetOutcome
                    {
                        OperationId = ctx.OperationId,
                        Succeeded = true,
                        LocalBranchReset = localResetAlreadyPerformed,
                        RemoteBranchRewritten = true,
                        LocalBranchName = localBranchName,
                        OriginalTipSha = originalTipSha,
                        TargetTipSha = targetSha,
                        ExpectedRemoteTipSha = expectedRemoteTipSha,
                        VerifiedRemoteTipSha = targetSha,
                        RemoteName = remoteName,
                        RemoteBranchName = remoteBranchName,
                        BackupRefName = backupRefName,
                        Summary = okMsg,
                        DetailMessage = combinedOutput,
                        StepResults = steps,
                    },
                };
            }

            // If error is stale lease / explicit rejection: never misreport or retry; preserve collaborator work.
            var isLeaseReject = IsRemoteLeaseRejection(combinedOutput);
            var isAmbiguousTransport = !isLeaseReject && (pushRaw.TimedOutOrConnectionLost || IsAmbiguousNetworkFailure(combinedOutput));

            if (isAmbiguousTransport)
            {
                // Do NOT blindly retry push! Immediately probe live remote via ls-remote.
                var probeAfter = await BranchDeletionService.ProbeRemoteBranchAsync(
                    repoPath,
                    remoteName,
                    remoteBranchName,
                    ctx.Log,
                    cancellationToken,
                    expectedPushUrl: confirmedPushUrl).ConfigureAwait(false);

                if (probeAfter.QuerySucceeded)
                {
                    var liveSha = probeAfter.LiveTipSha ?? string.Empty;
                    steps.Add(new Models.HistoryResetStepResult
                    {
                        Kind = Models.HistoryResetStepKind.RemoteStateProbe,
                        Succeeded = true,
                        Label = $"网络超时后已通过 ls-remote 复核远端实际提交：{ShortSha(liveSha)}",
                    });

                    if (string.Equals(liveSha, targetSha, StringComparison.Ordinal))
                    {
                        var verifiedMsg = $"{(localResetAlreadyPerformed ? "本地已撤回，且经 ls-remote 核实远端已更新至" : "经 ls-remote 核实远端已改写至")} {ShortSha(targetSha)}（推送响应阶段连接曾异常，未盲目重试）。";
                        return new OperationActionOutcome<Models.HistoryResetOutcome>
                        {
                            Stage = Models.GitOperationStage.Succeeded,
                            TargetTipSha = targetSha,
                            BackupRefName = backupRefName,
                            Summary = verifiedMsg,
                            DiagnosticDetail = combinedOutput,
                            Payload = new Models.HistoryResetOutcome
                            {
                                OperationId = ctx.OperationId,
                                Succeeded = true,
                                LocalBranchReset = localResetAlreadyPerformed,
                                RemoteBranchRewritten = true,
                                LocalBranchName = localBranchName,
                                OriginalTipSha = originalTipSha,
                                TargetTipSha = targetSha,
                                ExpectedRemoteTipSha = expectedRemoteTipSha,
                                VerifiedRemoteTipSha = liveSha,
                                RemoteName = remoteName,
                                RemoteBranchName = remoteBranchName,
                                BackupRefName = backupRefName,
                                Summary = verifiedMsg,
                                DetailMessage = combinedOutput,
                                StepResults = steps,
                            },
                        };
                    }

                    var movedElsewhere = !string.Equals(liveSha, expectedRemoteTipSha, StringComparison.Ordinal);
                    var probeNotUpdatedMsg = movedElsewhere
                        ? $"{prefixText}：远端当前已变为 {ShortSha(liveSha)}（协作者新提交已保留）；未进行盲目重试，可通过本地恢复引用 {backupRefName} 还原。"
                        : $"{prefixText}：经 ls-remote 核实远端仍停留在原提交 {ShortSha(expectedRemoteTipSha)}；可通过本地恢复引用 {backupRefName} 还原或确认后重新推送。";

                    return new OperationActionOutcome<Models.HistoryResetOutcome>
                    {
                        Stage = localResetAlreadyPerformed ? Models.GitOperationStage.PartialSuccess : Models.GitOperationStage.Failed,
                        TargetTipSha = targetSha,
                        BackupRefName = backupRefName,
                        Summary = probeNotUpdatedMsg,
                        DiagnosticDetail = combinedOutput,
                        Payload = new Models.HistoryResetOutcome
                        {
                            OperationId = ctx.OperationId,
                            Succeeded = false,
                            IsPartialSuccess = localResetAlreadyPerformed,
                            RejectedByRemoteLease = movedElsewhere,
                            CollaboratorCommitsPreserved = movedElsewhere,
                            LocalBranchReset = localResetAlreadyPerformed,
                            RemoteBranchRewritten = false,
                            LocalBranchName = localBranchName,
                            OriginalTipSha = originalTipSha,
                            TargetTipSha = targetSha,
                            ExpectedRemoteTipSha = expectedRemoteTipSha,
                            VerifiedRemoteTipSha = liveSha,
                            RemoteName = remoteName,
                            RemoteBranchName = remoteBranchName,
                            BackupRefName = backupRefName,
                            Summary = probeNotUpdatedMsg,
                            DetailMessage = combinedOutput,
                            StepResults = steps,
                        },
                    };
                }

                // Remote is also unreachable for ls-remote: mark PendingVerification cleanly!
                steps.Add(new Models.HistoryResetStepResult
                {
                    Kind = Models.HistoryResetStepKind.RemoteStateProbe,
                    Succeeded = false,
                    Label = "连接中断且无法通过 ls-remote 确认远端是否已改写",
                    Detail = probeAfter.ErrorSummary,
                });
                var uncertainMsg = $"{(localResetAlreadyPerformed ? "本地已撤回，但" : string.Empty)}远端推送信道中断且 ls-remote 复核失败（结果待核实）：请勿盲目重试，请联网后通过日志“核对远端状态”或通过 {backupRefName} 恢复。";
                return new OperationActionOutcome<Models.HistoryResetOutcome>
                {
                    Stage = Models.GitOperationStage.PendingVerification,
                    TargetTipSha = targetSha,
                    BackupRefName = backupRefName,
                    Summary = uncertainMsg,
                    DiagnosticDetail = $"{combinedOutput}\nls-remote: {probeAfter.ErrorSummary}".Trim(),
                    Payload = new Models.HistoryResetOutcome
                    {
                        OperationId = ctx.OperationId,
                        Succeeded = false,
                        IsPartialSuccess = localResetAlreadyPerformed,
                        RemotePushOutcomeUncertain = true,
                        LocalBranchReset = localResetAlreadyPerformed,
                        RemoteBranchRewritten = false,
                        LocalBranchName = localBranchName,
                        OriginalTipSha = originalTipSha,
                        TargetTipSha = targetSha,
                        ExpectedRemoteTipSha = expectedRemoteTipSha,
                        RemoteName = remoteName,
                        RemoteBranchName = remoteBranchName,
                        BackupRefName = backupRefName,
                        Summary = uncertainMsg,
                        DetailMessage = $"{combinedOutput}\nls-remote: {probeAfter.ErrorSummary}".Trim(),
                        StepResults = steps,
                    },
                };
            }

            // Explicit rejection / non-ambiguous remote failure
            steps.Add(new Models.HistoryResetStepResult
            {
                Kind = Models.HistoryResetStepKind.RemoteHistoryRewrite,
                Succeeded = false,
                Label = isLeaseReject
                    ? $"租约检查拒绝改写远端 '{remoteName}/{remoteBranchName}'"
                    : $"推送改写远端 '{remoteName}/{remoteBranchName}' 失败",
                Detail = combinedOutput,
            });

            var rejectMsg = isLeaseReject
                ? $"{prefixText}：远端分支在确认后已有其他提交（协作者提交已保留），已按 --force-with-lease 租约拒绝覆写。可通过恢复引用 {backupRefName} 找回原本地提交。"
                : $"{prefixText}：推送远端失败（{(string.IsNullOrEmpty(combinedOutput) ? "远端拒绝或网络失败" : combinedOutput)}）。可通过恢复引用 {backupRefName} 恢复。";

            return new OperationActionOutcome<Models.HistoryResetOutcome>
            {
                Stage = localResetAlreadyPerformed ? Models.GitOperationStage.PartialSuccess : Models.GitOperationStage.Failed,
                TargetTipSha = targetSha,
                BackupRefName = backupRefName,
                Summary = rejectMsg,
                DiagnosticDetail = combinedOutput,
                Payload = new Models.HistoryResetOutcome
                {
                    OperationId = ctx.OperationId,
                    Succeeded = false,
                    IsPartialSuccess = localResetAlreadyPerformed,
                    RejectedByRemoteLease = isLeaseReject,
                    CollaboratorCommitsPreserved = isLeaseReject,
                    LocalBranchReset = localResetAlreadyPerformed,
                    RemoteBranchRewritten = false,
                    LocalBranchName = localBranchName,
                    OriginalTipSha = originalTipSha,
                    TargetTipSha = targetSha,
                    ExpectedRemoteTipSha = expectedRemoteTipSha,
                    RemoteName = remoteName,
                    RemoteBranchName = remoteBranchName,
                    BackupRefName = backupRefName,
                    Summary = rejectMsg,
                    DetailMessage = combinedOutput,
                    StepResults = steps,
                },
            };
        }

        private static OperationActionOutcome<Models.HistoryResetOutcome> RemoteTargetBlocked(string reason) => new()
        {
            Stage = Models.GitOperationStage.Failed,
            Summary = reason,
            Payload = new Models.HistoryResetOutcome { BlockedBySnapshot = true, Summary = reason },
        };

        private static async Task<RemotePushRawResult> ExecuteRemotePushRawAsync(
            string repoPath,
            string sshKey,
            IReadOnlyList<string> args,
            Models.ICommandLog log,
            CancellationToken cancellationToken)
        {
            if (RemotePushExecutorOverride != null)
            {
                return await RemotePushExecutorOverride(repoPath, sshKey, args, log, cancellationToken)
                    .ConfigureAwait(false);
            }

            var quotedArgs = string.Join(" ", args.Select(CommandArgumentHelper.Quote));
            log?.AppendLine($"$ git {Models.GitConfigSnapshot.RedactText(quotedArgs)}");

            var cmd = new LeasePushCommand(repoPath, quotedArgs, sshKey)
            {
                CancellationToken = cancellationToken,
            };
            var res = await cmd.ExecuteAsync().ConfigureAwait(false);
            AppendRedactedCommandOutput(log, res);

            return new RemotePushRawResult
            {
                IsSuccess = res.IsSuccess,
                StdOut = res.StdOut,
                StdErr = res.StdErr,
            };
        }

        private static async Task<IReadOnlyList<string>> DetectUntrackedOrIgnoredPathCollisionsAsync(
            string repoPath,
            string targetCommitSha,
            CancellationToken cancellationToken)
        {
            var diffRes = await Command.RunGitDirectAsync(
                repoPath,
                cancellationToken,
                "-c",
                "core.quotePath=false",
                "diff",
                "--no-renames",
                "--name-status",
                "-z",
                "HEAD",
                targetCommitSha,
                "--").ConfigureAwait(false);

            if (!diffRes.IsSuccess || string.IsNullOrEmpty(diffRes.StdOut))
                return Array.Empty<string>();

            var targetPaths = new List<(string Status, string Path)>();
            var tokens = diffRes.StdOut.Split('\0', StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i + 1 < tokens.Length; i += 2)
            {
                var status = tokens[i];
                var path = NormalizeRepoRelativePath(tokens[i + 1]);
                if (!string.IsNullOrEmpty(path) && !status.StartsWith("D", StringComparison.Ordinal))
                    targetPaths.Add((status, path));
            }

            if (targetPaths.Count == 0)
                return Array.Empty<string>();

            var diskPaths = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
            await CollectUntrackedListAsync(repoPath, ["--others", "--exclude-standard"], diskPaths, cancellationToken).ConfigureAwait(false);
            await CollectUntrackedListAsync(repoPath, ["--others", "--ignored", "--exclude-standard"], diskPaths, cancellationToken).ConfigureAwait(false);

            var pathComp = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            var collisions = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

            foreach (var (status, targetPath) in targetPaths)
            {
                foreach (var diskPath in diskPaths)
                {
                    if (string.Equals(targetPath, diskPath, pathComp) ||
                        diskPath.StartsWith(targetPath + "/", pathComp) ||
                        targetPath.StartsWith(diskPath + "/", pathComp))
                    {
                        collisions.Add(diskPath);
                    }
                }

                // Guard empty ignored/untracked directories or filesystem entities at added/type-changed paths
                if (status.StartsWith("A", StringComparison.Ordinal) || status.StartsWith("T", StringComparison.Ordinal))
                {
                    var fullDiskPath = Path.Combine(repoPath, targetPath.Replace('/', Path.DirectorySeparatorChar));
                    if (File.Exists(fullDiskPath) || Directory.Exists(fullDiskPath))
                        collisions.Add(targetPath);
                }
            }

            return collisions.OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList();
        }

        private static async Task CollectUntrackedListAsync(
            string repoPath,
            string[] extraFlags,
            HashSet<string> sink,
            CancellationToken cancellationToken)
        {
            var args = new List<string> { "-c", "core.quotePath=false", "ls-files", "-z" };
            args.AddRange(extraFlags);
            var res = await Command.RunGitDirectAsync(repoPath, cancellationToken, args.ToArray()).ConfigureAwait(false);
            if (!res.IsSuccess || string.IsNullOrEmpty(res.StdOut))
                return;

            foreach (var raw in res.StdOut.Split('\0', StringSplitOptions.RemoveEmptyEntries))
            {
                var norm = NormalizeRepoRelativePath(raw);
                if (!string.IsNullOrEmpty(norm))
                    sink.Add(norm);
            }
        }

        private static async Task<(Models.ResetUpstreamRemoteStatus ConfiguredUpstream, IReadOnlyList<Models.ResetUpstreamRemoteStatus> AdditionalRemotes)> InspectBranchRemotesAsync(
            string repoPath,
            string branchName,
            string headSha,
            string targetSha,
            bool probeRemote,
            CancellationToken cancellationToken)
        {
            var endpoint = await ResolveRemoteEndpointAsync(repoPath, branchName, null, null, cancellationToken).ConfigureAwait(false);
            Models.ResetUpstreamRemoteStatus configured = null;

            if (!string.IsNullOrEmpty(endpoint.RemoteName) && !string.IsNullOrEmpty(endpoint.RemoteBranchName))
            {
                bool? existsOnRemote = null;
                bool querySucceeded = false;
                bool stateUnknown = !probeRemote;
                string queryError = probeRemote ? string.Empty : "未执行联网查询";
                string liveRemoteTip = string.Empty;
                bool remoteTipLocallyAvailable = false;
                bool remoteMatchesHead = false;
                bool localAhead = false;
                int unpushedCount = 0;
                bool remoteIncludesTarget = false;
                bool diverged = false;

                if (probeRemote)
                {
                    var probe = await BranchDeletionService.ProbeRemoteBranchAsync(
                        repoPath,
                        endpoint.RemoteName,
                        endpoint.RemoteBranchName,
                        cancellationToken: cancellationToken).ConfigureAwait(false);

                    querySucceeded = probe.QuerySucceeded;
                    if (!probe.QuerySucceeded)
                    {
                        stateUnknown = true;
                        queryError = probe.ErrorSummary;
                    }
                    else
                    {
                        stateUnknown = false;
                        existsOnRemote = probe.ExistsOnServer;
                        liveRemoteTip = probe.LiveTipSha ?? string.Empty;
                        if (probe.ExistsOnServer && !string.IsNullOrEmpty(liveRemoteTip))
                        {
                            remoteTipLocallyAvailable = await CommitObjectExistsLocallyAsync(repoPath, liveRemoteTip, cancellationToken)
                                .ConfigureAwait(false);
                            remoteMatchesHead = string.Equals(liveRemoteTip, headSha, StringComparison.Ordinal);
                            if (remoteTipLocallyAvailable)
                            {
                                remoteIncludesTarget = !string.IsNullOrEmpty(targetSha) &&
                                    await IsAncestorAsync(repoPath, targetSha, liveRemoteTip, cancellationToken).ConfigureAwait(false);
                                var remoteInHead = await IsAncestorAsync(repoPath, liveRemoteTip, headSha, cancellationToken).ConfigureAwait(false);
                                diverged = !remoteInHead;
                                if (remoteInHead && !remoteMatchesHead)
                                {
                                    localAhead = true;
                                    unpushedCount = await CountCommitsBetweenAsync(repoPath, liveRemoteTip, headSha, cancellationToken)
                                        .ConfigureAwait(false);
                                }
                            }
                            else
                            {
                                diverged = true;
                            }
                        }
                    }
                }
                else if (!string.IsNullOrEmpty(endpoint.LocalTrackingTipSha))
                {
                    remoteMatchesHead = string.Equals(endpoint.LocalTrackingTipSha, headSha, StringComparison.Ordinal);
                    if (!remoteMatchesHead && await IsAncestorAsync(repoPath, endpoint.LocalTrackingTipSha, headSha, cancellationToken).ConfigureAwait(false))
                    {
                        localAhead = true;
                        unpushedCount = await CountCommitsBetweenAsync(repoPath, endpoint.LocalTrackingTipSha, headSha, cancellationToken)
                            .ConfigureAwait(false);
                    }
                }

                string statusLabel;
                if (stateUnknown)
                    statusLabel = "远端状态未知";
                else if (existsOnRemote == false)
                    statusLabel = "远端尚无此分支";
                else if (diverged)
                    statusLabel = "远端包含本地未整合提交";
                else if (remoteMatchesHead)
                    statusLabel = "本地与远端已同步";
                else if (localAhead)
                    statusLabel = $"本地超前 {unpushedCount} 个未推送提交";
                else
                    statusLabel = "远端落后或可快进";

                configured = new Models.ResetUpstreamRemoteStatus
                {
                    RemoteName = endpoint.RemoteName,
                    RemoteBranchName = endpoint.RemoteBranchName,
                    TrackingRefName = endpoint.TrackingRefName,
                    ConfiguredUpstream = endpoint.HasConfiguredUpstream,
                    IsPushTarget = endpoint.IsPushTarget,
                    RedactedRemoteUrl = endpoint.RedactedUrl,
                    LocalTrackingTipSha = endpoint.LocalTrackingTipSha,
                    ExistsOnRemote = existsOnRemote,
                    RemoteQuerySucceeded = querySucceeded,
                    RemoteStateUnknown = stateUnknown,
                    RemoteQueryError = queryError,
                    LiveRemoteTipSha = liveRemoteTip,
                    RemoteTipExistsLocally = remoteTipLocallyAvailable,
                    RemoteMatchesLocalHead = remoteMatchesHead,
                    LocalIsAheadOfRemote = localAhead,
                    UnpushedCommitCount = unpushedCount,
                    RemoteIncludesTarget = remoteIncludesTarget,
                    RemoteDivergedWithUnintegratedCommits = diverged,
                    RemoteStatusLabel = statusLabel,
                    Detail = stateUnknown ? queryError : $"{endpoint.RemoteName}/{endpoint.RemoteBranchName}: {statusLabel}",
                };
            }

            // Collect additional non-primary remotes strictly for explicit multi-remote visibility (never conflated with ConfiguredUpstream)
            var additional = new List<Models.ResetUpstreamRemoteStatus>();
            var remotes = await new QueryRemotes(repoPath).GetResultAsync().ConfigureAwait(false);
            foreach (var r in remotes)
            {
                if (configured != null && string.Equals(r.Name, configured.RemoteName, StringComparison.Ordinal))
                    continue;

                var candidateTrackingRef = $"refs/remotes/{r.Name}/{branchName}";
                var candidateTip = await ResolveRefShaAsync(repoPath, candidateTrackingRef, cancellationToken).ConfigureAwait(false);
                if (string.IsNullOrEmpty(candidateTip))
                    continue;

                var matches = string.Equals(candidateTip, headSha, StringComparison.Ordinal);
                var aheadCount = !matches && await IsAncestorAsync(repoPath, candidateTip, headSha, cancellationToken).ConfigureAwait(false)
                    ? await CountCommitsBetweenAsync(repoPath, candidateTip, headSha, cancellationToken).ConfigureAwait(false)
                    : 0;

                additional.Add(new Models.ResetUpstreamRemoteStatus
                {
                    RemoteName = r.Name,
                    RemoteBranchName = branchName,
                    TrackingRefName = candidateTrackingRef,
                    ConfiguredUpstream = false,
                    IsPushTarget = false,
                    RedactedRemoteUrl = Models.GitConfigSnapshot.RedactText(r.PushURL ?? r.URL),
                    LocalTrackingTipSha = candidateTip,
                    RemoteMatchesLocalHead = matches,
                    LocalIsAheadOfRemote = aheadCount > 0,
                    UnpushedCommitCount = aheadCount,
                    RemoteStatusLabel = matches
                        ? "非当前主跟踪远端（仅本地缓存匹配）"
                        : $"非当前主跟踪远端（相对其本地缓存超前 {aheadCount}）",
                });
            }

            return (configured, additional);
        }

        internal static async Task<(bool HasConfiguredUpstream, bool IsPushTarget, string RemoteName, string RemoteBranchName, string TrackingRefName, string RedactedUrl, string LocalTrackingTipSha)> ResolveRemoteEndpointAsync(
            string repoPath,
            string localBranchName,
            string explicitRemoteName,
            string explicitRemoteBranchName,
            CancellationToken cancellationToken)
        {
            var remotes = await new QueryRemotes(repoPath).GetResultAsync().ConfigureAwait(false);

            if (!string.IsNullOrWhiteSpace(explicitRemoteName))
            {
                var rName = CommandArgumentHelper.ValidateNonOptionToken(explicitRemoteName, nameof(explicitRemoteName));
                var rBranch = string.IsNullOrWhiteSpace(explicitRemoteBranchName)
                    ? localBranchName
                    : CommandArgumentHelper.ValidateNonOptionToken(explicitRemoteBranchName, nameof(explicitRemoteBranchName));
                var remote = remotes.Find(r => string.Equals(r.Name, rName, StringComparison.Ordinal));
                var url = remote != null
                    ? Models.GitConfigSnapshot.RedactText(remote.PushURL ?? remote.URL)
                    : await ReadRedactedRemoteUrlAsync(repoPath, rName, cancellationToken).ConfigureAwait(false);
                var trackRef = $"refs/remotes/{rName}/{rBranch}";
                var trackSha = await ResolveRefShaAsync(repoPath, trackRef, cancellationToken).ConfigureAwait(false);
                return (true, true, rName, rBranch, trackRef, url, trackSha);
            }

            var pushRemoteCfg = await ReadConfigValueAsync(repoPath, $"branch.{localBranchName}.pushRemote", cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrEmpty(pushRemoteCfg))
                pushRemoteCfg = await ReadConfigValueAsync(repoPath, "remote.pushDefault", cancellationToken).ConfigureAwait(false);

            var upstreamRemoteCfg = await ReadConfigValueAsync(repoPath, $"branch.{localBranchName}.remote", cancellationToken).ConfigureAwait(false);
            var mergeCfg = await ReadConfigValueAsync(repoPath, $"branch.{localBranchName}.merge", cancellationToken).ConfigureAwait(false);

            var remoteName = !string.IsNullOrEmpty(pushRemoteCfg) ? pushRemoteCfg : upstreamRemoteCfg;
            if (string.IsNullOrEmpty(remoteName) || remoteName == ".")
                return (false, false, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty);

            string remoteBranchName;
            if (!string.IsNullOrEmpty(pushRemoteCfg) && !string.Equals(pushRemoteCfg, upstreamRemoteCfg, StringComparison.Ordinal))
            {
                remoteBranchName = localBranchName;
            }
            else if (!string.IsNullOrEmpty(mergeCfg))
            {
                remoteBranchName = ExtractShortBranchName(mergeCfg, remoteName);
            }
            else
            {
                return (false, false, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty);
            }

            if (string.IsNullOrEmpty(remoteBranchName) || remoteBranchName.StartsWith("-", StringComparison.Ordinal))
                return (false, false, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty);

            var matched = remotes.Find(r => string.Equals(r.Name, remoteName, StringComparison.Ordinal));
            var redactedUrl = matched != null
                ? Models.GitConfigSnapshot.RedactText(matched.PushURL ?? matched.URL)
                : await ReadRedactedRemoteUrlAsync(repoPath, remoteName, cancellationToken).ConfigureAwait(false);
            var trackingRef = $"refs/remotes/{remoteName}/{remoteBranchName}";
            var trackingSha = await ResolveRefShaAsync(repoPath, trackingRef, cancellationToken).ConfigureAwait(false);

            return (true, !string.IsNullOrEmpty(pushRemoteCfg), remoteName, remoteBranchName, trackingRef, redactedUrl, trackingSha);
        }

        private static string DetermineCommitResetBlockReason(
            bool isDetachedHead,
            Models.GitInProgressKind inProgress,
            bool isSameAsHead,
            bool headIsRoot,
            bool isOnFirstParent,
            Models.OperationConfirmationSnapshot snapshot,
            IReadOnlyList<string> collisions)
        {
            if (isDetachedHead)
                return "当前处于游离 HEAD（Detached HEAD）状态，不对应本地分支主线，已阻止直接撤回提交；请先检出到目标本地分支。";

            if (inProgress != Models.GitInProgressKind.None)
                return $"当前仓库正处于 {inProgress} 进行中状态，请先完成或中止当前操作后再撤回提交。";

            if (isSameAsHead)
            {
                return headIsRoot
                    ? "当前 HEAD 已是仓库首个提交，不支持继续向前回退为空仓库。"
                    : "目标提交已是当前 HEAD，无需撤回。";
            }

            if (!isOnFirstParent)
                return "目标提交不在当前分支的第一父链（--first-parent）主线历史上；仅允许沿当前分支主线记录回退，已阻止直接撤回合并支线或分叉提交。";

            if (collisions.Count > 0)
                return $"目标提交与当前工作区中的未跟踪或被 .gitignore 忽略文件发生路径冲突：{string.Join("、", collisions.Take(5))}。为防止数据丢失已阻止操作（不会执行 git clean）。";

            if (!snapshot.IsWorkingTreeClean)
                return "当前工作区或暂存区包含未提交更改、冲突或未跟踪文件，请先提交、储藏或清理后再执行提交撤回。";

            return string.Empty;
        }

        private static Models.HistoryResetPreflight BuildBlockedResetPreflight(
            string repoPath,
            string gitDir,
            string commonDir,
            string currentBranch,
            string currentBranchRef,
            string headSha,
            string targetSha,
            string targetSubject,
            bool isDetached,
            bool headIsRoot,
            bool isRootTarget,
            bool isSameAsHead,
            bool isFirstParent,
            Models.GitInProgressKind inProgress,
            Models.OperationConfirmationSnapshot snapshot,
            Models.BranchProtectionStatus protection,
            string userName,
            string userEmail,
            bool blockedByRoot,
            string blockReason)
        {
            return new Models.HistoryResetPreflight
            {
                RepoPath = repoPath,
                CanonicalGitDir = gitDir,
                CanonicalCommonDir = commonDir,
                LocalBranchName = currentBranch,
                LocalBranchFullRef = currentBranchRef,
                CurrentHeadSha = headSha,
                TargetCommitSha = targetSha,
                TargetCommitSubject = targetSubject,
                IsDetachedHead = isDetached,
                HeadIsRootCommit = headIsRoot,
                IsRootCommitTarget = isRootTarget,
                IsSameAsHead = isSameAsHead,
                IsOnFirstParentChain = isFirstParent,
                InProgressKind = inProgress,
                HasStagedChanges = snapshot.HasStagedChanges,
                HasUnstagedChanges = snapshot.HasUnstagedChanges,
                HasUntrackedFiles = snapshot.HasUntrackedFiles,
                HasConflicts = snapshot.HasConflicts,
                CanResetLocally = false,
                BlockReason = blockReason,
                Protection = protection,
                CurrentUserName = userName,
                CurrentUserEmail = userEmail,
                Snapshot = snapshot,
                Summary = blockedByRoot ? blockReason : blockReason,
            };
        }

        private static Models.HistoryResetOutcome BuildBlockedResetOutcome(Models.HistoryResetPreflight preflight)
        {
            return new Models.HistoryResetOutcome
            {
                BlockedByDirtyWorkingTree = !preflight.IsCleanWorkingTreeAndIndex && !preflight.HasUntrackedOrIgnoredCollisions,
                BlockedByUntrackedOrIgnoredCollision = preflight.HasUntrackedOrIgnoredCollisions,
                BlockedByNonFirstParent = !preflight.IsOnFirstParentChain && !preflight.IsSameAsHead && !preflight.IsUnbornHead && !preflight.IsDetachedHead && !string.IsNullOrEmpty(preflight.TargetCommitSha),
                BlockedByRootCommit = preflight.HeadIsRootCommit || string.IsNullOrEmpty(preflight.TargetCommitSha) || EmptyTreeHashes.Contains(preflight.TargetCommitSha),
                LocalBranchName = preflight.LocalBranchName,
                OriginalTipSha = preflight.CurrentHeadSha,
                TargetTipSha = preflight.TargetCommitSha,
                Summary = preflight.BlockReason,
            };
        }

        private static Models.HistoryResetOutcome MapExecutionResultToOutcome(
            Models.OperationExecutionResult<Models.HistoryResetOutcome> execRes,
            string localBranchName,
            string originalTipSha,
            string targetTipSha,
            Models.RemoteRewritePreflight remotePre)
        {
            if (execRes.Payload != null)
            {
                var p = execRes.Payload;
                return new Models.HistoryResetOutcome
                {
                    OperationId = p.OperationId,
                    Succeeded = p.Succeeded,
                    IsPartialSuccess = p.IsPartialSuccess,
                    BlockedByProtection = p.BlockedByProtection,
                    BlockedBySnapshot = execRes.BlockedBySnapshot || p.BlockedBySnapshot,
                    BlockedByLock = execRes.BlockedByLock || p.BlockedByLock,
                    BlockedByDirtyWorkingTree = p.BlockedByDirtyWorkingTree,
                    BlockedByUntrackedOrIgnoredCollision = p.BlockedByUntrackedOrIgnoredCollision,
                    BlockedByNonFirstParent = p.BlockedByNonFirstParent,
                    BlockedByRootCommit = p.BlockedByRootCommit,
                    BlockedByDivergedRemote = p.BlockedByDivergedRemote,
                    BlockedByUnknownRemoteState = p.BlockedByUnknownRemoteState,
                    RejectedByRemoteLease = p.RejectedByRemoteLease,
                    CollaboratorCommitsPreserved = p.CollaboratorCommitsPreserved,
                    RemotePushOutcomeUncertain = p.RemotePushOutcomeUncertain,
                    LocalBranchReset = p.LocalBranchReset,
                    RemoteBranchRewritten = p.RemoteBranchRewritten,
                    LocalBranchName = p.LocalBranchName,
                    OriginalTipSha = p.OriginalTipSha,
                    TargetTipSha = p.TargetTipSha,
                    ExpectedRemoteTipSha = p.ExpectedRemoteTipSha,
                    VerifiedRemoteTipSha = p.VerifiedRemoteTipSha,
                    RemoteName = p.RemoteName,
                    RemoteBranchName = p.RemoteBranchName,
                    BackupRefName = p.BackupRefName,
                    Summary = p.Summary,
                    DetailMessage = p.DetailMessage,
                    StepResults = p.StepResults,
                    OperationRecord = execRes.Record,
                };
            }

            return new Models.HistoryResetOutcome
            {
                OperationId = execRes.Record?.OperationId ?? string.Empty,
                Succeeded = execRes.Succeeded,
                BlockedBySnapshot = execRes.BlockedBySnapshot,
                BlockedByLock = execRes.BlockedByLock,
                LocalBranchName = localBranchName,
                OriginalTipSha = originalTipSha,
                TargetTipSha = targetTipSha,
                RemoteName = remotePre?.RemoteName ?? string.Empty,
                RemoteBranchName = remotePre?.RemoteBranchName ?? string.Empty,
                ExpectedRemoteTipSha = remotePre?.LiveRemoteTipSha ?? string.Empty,
                Summary = execRes.Summary,
                OperationRecord = execRes.Record,
            };
        }

        internal static async Task<IReadOnlyList<Models.ResetCommitSummary>> ReadCommitRangeAsync(
            string repoPath,
            string excludedBaseSha,
            string tipSha,
            HashSet<string> firstParentSet,
            CancellationToken cancellationToken)
        {
            var res = await Command.RunGitDirectAsync(
                repoPath,
                cancellationToken,
                "log",
                "-z",
                "--format=%H%x1f%P%x1f%an%x1f%ae%x1f%aI%x1f%s",
                $"{excludedBaseSha}..{tipSha}").ConfigureAwait(false);

            var list = new List<Models.ResetCommitSummary>();
            if (!res.IsSuccess || string.IsNullOrEmpty(res.StdOut))
                return list;

            var entries = res.StdOut.Split('\0', StringSplitOptions.RemoveEmptyEntries);
            foreach (var entry in entries)
            {
                var fields = entry.Split('\x1f');
                if (fields.Length < 6)
                    continue;

                var sha = fields[0].Trim();
                var parents = fields[1].Split(' ', StringSplitOptions.RemoveEmptyEntries);
                DateTimeOffset? authoredAt = DateTimeOffset.TryParse(fields[4].Trim(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var dto)
                    ? dto
                    : null;

                list.Add(new Models.ResetCommitSummary
                {
                    Sha = sha,
                    ShortSha = ShortSha(sha),
                    AuthorName = fields[2].Trim(),
                    AuthorEmail = fields[3].Trim(),
                    AuthoredAt = authoredAt,
                    Subject = fields[5].Trim(),
                    IsFirstParent = firstParentSet.Contains(sha),
                    IsMergeCommit = parents.Length > 1,
                });
            }

            return list;
        }

        internal static IReadOnlyList<Models.ResetAuthorAttribution> AggregateAuthors(IReadOnlyList<Models.ResetCommitSummary> commits)
        {
            if (commits == null || commits.Count == 0)
                return Array.Empty<Models.ResetAuthorAttribution>();

            return commits
                .GroupBy(
                    c => $"{c.AuthorName}\x1f{c.AuthorEmail}",
                    StringComparer.OrdinalIgnoreCase)
                .Select(g =>
                {
                    var first = g.First();
                    return new Models.ResetAuthorAttribution
                    {
                        Name = string.IsNullOrEmpty(first.AuthorName) ? "(未知作者)" : first.AuthorName,
                        Email = first.AuthorEmail,
                        CommitCount = g.Count(),
                    };
                })
                .OrderByDescending(a => a.CommitCount)
                .ThenBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        internal static bool HasOtherAuthors(
            IReadOnlyList<Models.ResetAuthorAttribution> authors,
            string currentUserName,
            string currentUserEmail)
        {
            if (authors == null || authors.Count == 0)
                return false;

            if (authors.Count > 1)
                return true;

            var hasEmail = !string.IsNullOrWhiteSpace(currentUserEmail);
            var hasName = !string.IsNullOrWhiteSpace(currentUserName);
            if (!hasEmail && !hasName)
                return false;

            var only = authors[0];
            if (hasEmail && !string.IsNullOrWhiteSpace(only.Email))
                return !string.Equals(only.Email, currentUserEmail.Trim(), StringComparison.OrdinalIgnoreCase);

            if (hasName)
                return !string.Equals(only.Name, currentUserName.Trim(), StringComparison.OrdinalIgnoreCase);

            return false;
        }

        internal static async Task<List<string>> ReadFirstParentChainAsync(string repoPath, string tipSha, CancellationToken cancellationToken)
        {
            var res = await Command.RunGitDirectAsync(repoPath, cancellationToken, "rev-list", "--first-parent", tipSha)
                .ConfigureAwait(false);
            if (!res.IsSuccess || string.IsNullOrWhiteSpace(res.StdOut))
                return new List<string>();

            return res.StdOut
                .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(l => l.Trim())
                .ToList();
        }

        private static async Task<List<string>> ReadCommitParentsAsync(string repoPath, string commitSha, CancellationToken cancellationToken)
        {
            var res = await Command.RunGitDirectAsync(repoPath, cancellationToken, "rev-list", "--parents", "-n", "1", commitSha)
                .ConfigureAwait(false);
            if (!res.IsSuccess || string.IsNullOrWhiteSpace(res.StdOut))
                return new List<string>();

            var parts = res.StdOut.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            return parts.Length <= 1 ? new List<string>() : parts.Skip(1).ToList();
        }

        private static async Task<string> ReadCommitSubjectAsync(string repoPath, string commitSha, CancellationToken cancellationToken)
        {
            var res = await Command.RunGitDirectAsync(repoPath, cancellationToken, "log", "-n", "1", "--format=%s", commitSha)
                .ConfigureAwait(false);
            return res.IsSuccess ? res.StdOut.Trim() : string.Empty;
        }

        private static async Task<(string Name, string Email)> ReadCurrentGitIdentityAsync(string repoPath, CancellationToken cancellationToken)
        {
            var nameRes = await Command.RunGitDirectAsync(repoPath, cancellationToken, "config", "--get", "user.name").ConfigureAwait(false);
            var emailRes = await Command.RunGitDirectAsync(repoPath, cancellationToken, "config", "--get", "user.email").ConfigureAwait(false);
            return (
                nameRes.IsSuccess ? nameRes.StdOut.Trim() : string.Empty,
                emailRes.IsSuccess ? emailRes.StdOut.Trim() : string.Empty);
        }

        private static async Task<string> GenerateUniqueRecoveryBranchNameAsync(
            string repoPath,
            string branchName,
            string backupSha,
            CancellationToken cancellationToken)
        {
            var sanitized = string.IsNullOrWhiteSpace(branchName)
                ? "branch"
                : branchName.Trim().Replace('/', '-');
            var baseName = $"recover/{sanitized}-{ShortSha(backupSha)}";
            var candidate = baseName;

            for (int suffix = 2; suffix < 100; suffix++)
            {
                var existing = await ResolveRefShaAsync(repoPath, "refs/heads/" + candidate, cancellationToken).ConfigureAwait(false);
                if (string.IsNullOrEmpty(existing))
                    return candidate;

                candidate = $"{baseName}-{suffix}";
            }

            return $"{baseName}-{Guid.NewGuid().ToString("N")[..6]}";
        }

        private static void AcknowledgeSourceOperation(
            Models.OperationJournalStore store,
            string sourceOperationId,
            string backupRefName,
            string commonDir)
        {
            if (store == null)
                return;

            try
            {
                var rec = !string.IsNullOrWhiteSpace(sourceOperationId)
                    ? store.TryGet(sourceOperationId)
                    : null;
                if (rec == null && !string.IsNullOrWhiteSpace(backupRefName) && !string.IsNullOrWhiteSpace(commonDir))
                {
                    rec = store.GetByRepository(commonDir)
                        .FirstOrDefault(r => string.Equals(r.BackupRefName, backupRefName, StringComparison.Ordinal));
                }

                if (rec != null)
                {
                    rec.IsAcknowledged = true;
                    rec.IsUnfinishedOnStartup = false;
                    store.AppendOrUpdate(rec);
                }
            }
            catch
            {
                // Keep restore path resilient if journal update fails.
            }
        }

        private static async Task<string> ResolveCommitShaAsync(string repoPath, string rev, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(rev))
                return string.Empty;

            var res = await Command.RunGitDirectAsync(repoPath, cancellationToken, "rev-parse", "--verify", "--quiet", $"{rev.Trim()}^{{commit}}")
                .ConfigureAwait(false);
            return res.IsSuccess ? res.StdOut.Trim() : string.Empty;
        }

        internal static async Task<string> ResolveRefShaAsync(string repoPath, string fullRef, CancellationToken cancellationToken)
        {
            var res = await Command.RunGitDirectAsync(repoPath, cancellationToken, "show-ref", "--verify", "--", fullRef)
                .ConfigureAwait(false);
            if (!res.IsSuccess || string.IsNullOrWhiteSpace(res.StdOut))
                return string.Empty;

            var line = res.StdOut.Trim();
            var idx = line.IndexOf(' ');
            return idx > 0 ? line[..idx] : line;
        }

        private static async Task<string> ReadCurrentBranchNameAsync(string repoPath, CancellationToken cancellationToken)
        {
            var res = await Command.RunGitDirectAsync(repoPath, cancellationToken, "symbolic-ref", "--short", "-q", "HEAD")
                .ConfigureAwait(false);
            return res.IsSuccess ? res.StdOut.Trim() : string.Empty;
        }

        internal static async Task<bool> CommitObjectExistsLocallyAsync(string repoPath, string sha, CancellationToken cancellationToken)
        {
            var res = await Command.RunGitDirectAsync(repoPath, cancellationToken, "cat-file", "-e", $"{sha}^{{commit}}")
                .ConfigureAwait(false);
            return res.IsSuccess;
        }

        internal static async Task<bool> IsAncestorAsync(string repoPath, string ancestorSha, string descendantSha, CancellationToken cancellationToken)
        {
            var res = await Command.RunGitDirectAsync(repoPath, cancellationToken, "merge-base", "--is-ancestor", ancestorSha, descendantSha)
                .ConfigureAwait(false);
            return res.IsSuccess;
        }

        private static async Task<int> CountCommitsBetweenAsync(string repoPath, string baseSha, string tipSha, CancellationToken cancellationToken)
        {
            var res = await Command.RunGitDirectAsync(repoPath, cancellationToken, "rev-list", "--count", $"{baseSha}..{tipSha}")
                .ConfigureAwait(false);
            return res.IsSuccess && int.TryParse(res.StdOut.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var count)
                ? count
                : 0;
        }

        private static async Task<string> ReadConfigValueAsync(string repoPath, string key, CancellationToken cancellationToken)
        {
            var res = await Command.RunGitDirectAsync(repoPath, cancellationToken, "config", "--get", "--", key)
                .ConfigureAwait(false);
            return res.IsSuccess ? res.StdOut.Trim() : string.Empty;
        }

        private static async Task<string> ReadRedactedRemoteUrlAsync(string repoPath, string remoteName, CancellationToken cancellationToken)
        {
            var url = await ReadConfigValueAsync(repoPath, $"remote.{remoteName}.url", cancellationToken).ConfigureAwait(false);
            return Models.GitConfigSnapshot.RedactText(url);
        }

        private static string ExtractShortBranchName(string mergeRef, string remoteName)
        {
            if (mergeRef.StartsWith("refs/heads/", StringComparison.Ordinal))
                return mergeRef.Substring("refs/heads/".Length);
            var remotePrefix = $"refs/remotes/{remoteName}/";
            if (mergeRef.StartsWith(remotePrefix, StringComparison.Ordinal))
                return mergeRef.Substring(remotePrefix.Length);
            return mergeRef;
        }

        private static string NormalizeRepoRelativePath(string path)
        {
            return string.IsNullOrWhiteSpace(path)
                ? string.Empty
                : path.Trim().Replace('\\', '/').TrimStart('/');
        }

        private static string NormalizeDirectoryPath(string path)
        {
            return string.IsNullOrWhiteSpace(path)
                ? string.Empty
                : Path.TrimEndingDirectorySeparator(Path.GetFullPath(path).Replace('\\', '/'));
        }

        internal static bool IsRemoteLeaseRejection(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return false;

            return text.Contains("stale info", StringComparison.OrdinalIgnoreCase) ||
                   text.Contains("[rejected]", StringComparison.OrdinalIgnoreCase) ||
                   text.Contains("failed to push some refs", StringComparison.OrdinalIgnoreCase) &&
                   text.Contains("fetch first", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsAmbiguousNetworkFailure(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return true;

            return text.Contains("timed out", StringComparison.OrdinalIgnoreCase) ||
                   text.Contains("timeout", StringComparison.OrdinalIgnoreCase) ||
                   text.Contains("connection reset", StringComparison.OrdinalIgnoreCase) ||
                   text.Contains("early EOF", StringComparison.OrdinalIgnoreCase) ||
                   text.Contains("unexpected disconnect", StringComparison.OrdinalIgnoreCase) ||
                   text.Contains("RPC failed", StringComparison.OrdinalIgnoreCase);
        }

        private static void AppendRedactedCommandOutput(Models.ICommandLog log, Command.Result res)
        {
            if (log == null || res == null)
                return;

            if (!string.IsNullOrWhiteSpace(res.StdOut))
                log.AppendLine(Models.GitConfigSnapshot.RedactText(res.StdOut.TrimEnd()));
            if (!string.IsNullOrWhiteSpace(res.StdErr))
                log.AppendLine(Models.GitConfigSnapshot.RedactText(res.StdErr.TrimEnd()));
        }

        private static string FirstNonEmpty(params string[] values)
        {
            foreach (var v in values)
            {
                if (!string.IsNullOrWhiteSpace(v))
                    return v.Trim();
            }
            return string.Empty;
        }

        private static string ShortSha(string sha)
        {
            if (string.IsNullOrWhiteSpace(sha))
                return string.Empty;
            return sha.Length > 8 ? sha[..8] : sha;
        }

        private sealed class LeasePushCommand : Command
        {
            public LeasePushCommand(string repoPath, string args, string sshKey)
            {
                WorkingDirectory = repoPath;
                Context = repoPath;
                SSHKey = sshKey ?? string.Empty;
                RaiseError = false;
                Args = args;
            }

            public async Task<Result> ExecuteAsync()
            {
                var res = await ReadToEndAsync().ConfigureAwait(false);
                return RedactResult(res);
            }
        }
    }
}
