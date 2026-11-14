using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace SourceGit.Commands
{
    /// <summary>
    /// Coordinates preflight inspection, snapshot verification, write-lock execution,
    /// non-mutating conflict prediction, strict conflict-ownership verification before auto-abort,
    /// hook/signing failure classification, and safe single-commit revert operations.
    /// </summary>
    public static partial class MergeRevertService
    {
        private static readonly TimeSpan MutationCommandTimeout = TimeSpan.FromMinutes(10);

        [GeneratedRegex(@"^[0-9a-fA-F]{40,64}$")]
        private static partial Regex REG_HEX_OID();

        [GeneratedRegex(@"^CONFLICT\s*\([^)]+\):\s*(?:Merge conflict in|[^:]+ in|Deleted in .*?, modified in .*?:|Rename/rename\s+conflict in)\s+(.+)$", RegexOptions.IgnoreCase)]
        private static partial Regex REG_MERGE_TREE_CONFLICT_PATH();

        [GeneratedRegex(@"^([MADT])\t(.+)$")]
        private static partial Regex REG_DIFF_LINE();

        [GeneratedRegex(@"^([CR])[0-9]{0,4}\t(.+)\t(.+)$")]
        private static partial Regex REG_DIFF_RENAME_LINE();

        private readonly record struct ResolvedSourceInfo(
            bool IsValid,
            string DisplayName,
            string RefOrSha,
            string FullRef,
            string CommitSha,
            string Error);

        private readonly record struct LiveConflictSnapshot(
            string HeadSha,
            IReadOnlyList<string> ActiveMarkers,
            Models.GitInProgressKind InProgressKind,
            string MarkerFilePath,
            string MarkerFileSha256,
            string MarkerOid,
            string UnmergedSignature,
            string StagedFingerprint,
            IReadOnlyList<string> ConflictedPaths,
            IReadOnlyList<Models.ConflictBaselineEntry> Entries);

        public static async Task<Models.MergePreflight> InspectMergeAsync(
            string repoPath,
            string sourceRefOrSha,
            string targetLocalBranch = null,
            Models.MergeSourceKind sourceKind = Models.MergeSourceKind.LocalBranch,
            string sourceDisplayName = null,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(repoPath))
                throw new ArgumentException("无效的仓库路径。", nameof(repoPath));

            var (_, commonDir) = await Models.RepositoryWriteGuard
                .ResolveGitDirectoriesAsync(repoPath, cancellationToken)
                .ConfigureAwait(false);

            var currentBranch = await ReadCurrentBranchShortAsync(repoPath, cancellationToken).ConfigureAwait(false);
            var resolvedTarget = string.IsNullOrWhiteSpace(targetLocalBranch)
                ? currentBranch
                : targetLocalBranch.Trim();

            var snapshotInitial = await CaptureOperationSnapshot
                .CaptureAsync(repoPath, new Models.OperationSnapshotCaptureOptions(), cancellationToken)
                .ConfigureAwait(false);

            if (string.IsNullOrWhiteSpace(resolvedTarget))
            {
                return BuildBlockedMergePreflight(
                    repoPath,
                    commonDir,
                    snapshotInitial,
                    sourceKind,
                    sourceDisplayName ?? sourceRefOrSha,
                    sourceRefOrSha,
                    targetLocalBranch ?? string.Empty,
                    "当前仓库处于 detached HEAD 状态且未指定目标本地分支，请先选择或创建目标本地分支。");
            }

            if (!IsValidBranchNameToken(resolvedTarget))
            {
                return BuildBlockedMergePreflight(
                    repoPath,
                    commonDir,
                    snapshotInitial,
                    sourceKind,
                    sourceDisplayName ?? sourceRefOrSha,
                    sourceRefOrSha,
                    resolvedTarget,
                    $"目标本地分支名称无效：{resolvedTarget}");
            }

            var targetFullRef = "refs/heads/" + resolvedTarget;
            var targetSha = await ResolveCommitOidAsync(repoPath, targetFullRef, cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrEmpty(targetSha))
            {
                return BuildBlockedMergePreflight(
                    repoPath,
                    commonDir,
                    snapshotInitial,
                    sourceKind,
                    sourceDisplayName ?? sourceRefOrSha,
                    sourceRefOrSha,
                    resolvedTarget,
                    $"目标本地分支 {resolvedTarget} 不存在或尚无提交。");
            }

            var sourceInfo = await ResolveMergeSourceAsync(repoPath, sourceRefOrSha, sourceKind, sourceDisplayName, cancellationToken)
                .ConfigureAwait(false);
            if (!sourceInfo.IsValid)
            {
                return BuildBlockedMergePreflight(
                    repoPath,
                    commonDir,
                    snapshotInitial,
                    sourceKind,
                    sourceInfo.DisplayName,
                    sourceRefOrSha,
                    resolvedTarget,
                    sourceInfo.Error,
                    targetFullRef,
                    targetSha);
            }

            var watchedRefs = new List<string> { targetFullRef };
            if (!string.IsNullOrEmpty(sourceInfo.FullRef) &&
                !string.Equals(sourceInfo.FullRef, targetFullRef, StringComparison.Ordinal))
            {
                watchedRefs.Add(sourceInfo.FullRef);
            }

            var snapshot = await CaptureOperationSnapshot.CaptureAsync(
                repoPath,
                new Models.OperationSnapshotCaptureOptions { RefsToWatch = watchedRefs },
                cancellationToken).ConfigureAwait(false);

            var needsCheckout = snapshot.IsDetachedHead ||
                                !string.Equals(snapshot.HeadRefName, targetFullRef, StringComparison.Ordinal);

            var checkedOutElsewherePath = await FindOtherWorktreeCheckingOutBranchAsync(repoPath, targetFullRef, cancellationToken)
                .ConfigureAwait(false);

            var relation = await DetermineMergeRelationAsync(repoPath, targetSha, sourceInfo.CommitSha, cancellationToken)
                .ConfigureAwait(false);

            var conflictPrediction = Models.MergeConflictPrediction.Clean;
            IReadOnlyList<string> predictedConflictPaths = [];
            var conflictPredictionDetail = string.Empty;

            if (relation == Models.MergeRelationKind.UnrelatedHistories)
            {
                conflictPrediction = Models.MergeConflictPrediction.BlockedUnrelatedHistories;
                conflictPredictionDetail = "来源与目标分支没有共同提交祖先，首版阻止合并无共同历史的分支。";
            }
            else if (relation is Models.MergeRelationKind.Diverged or Models.MergeRelationKind.FastForward)
            {
                var treeCheck = await RunNonMutatingMergeTreeCheckAsync(repoPath, targetSha, sourceInfo.CommitSha, cancellationToken)
                    .ConfigureAwait(false);
                conflictPrediction = treeCheck.Prediction;
                predictedConflictPaths = treeCheck.ConflictPaths;
                conflictPredictionDetail = treeCheck.Detail;
            }

            var incomingCommits = await ReadIncomingCommitsAsync(repoPath, targetSha, sourceInfo.CommitSha, cancellationToken)
                .ConfigureAwait(false);
            var incomingChanges = relation == Models.MergeRelationKind.UnrelatedHistories
                ? (IReadOnlyList<Models.Change>)[]
                : await ReadThreeDotChangesAsync(repoPath, targetSha, sourceInfo.CommitSha, cancellationToken).ConfigureAwait(false);

            var blockingReason = BuildMergeBlockingReason(
                snapshot,
                checkedOutElsewherePath,
                relation,
                sourceInfo.FullRef,
                targetFullRef);

            return new Models.MergePreflight
            {
                IsReady = string.IsNullOrEmpty(blockingReason),
                RepoPath = repoPath,
                CanonicalCommonDir = commonDir,
                CurrentBranchName = currentBranch,
                IsDetachedHead = snapshot.IsDetachedHead,
                HeadSha = snapshot.HeadSha,
                SourceKind = sourceKind,
                SourceDisplayName = sourceInfo.DisplayName,
                SourceRefOrSha = sourceInfo.RefOrSha,
                SourceFullRef = sourceInfo.FullRef,
                SourceResolvedSha = sourceInfo.CommitSha,
                TargetBranchName = resolvedTarget,
                TargetFullRef = targetFullRef,
                TargetResolvedSha = targetSha,
                NeedsCheckoutTargetBranch = needsCheckout,
                TargetCheckedOutWorktreePath = checkedOutElsewherePath,
                InProgressKind = snapshot.InProgressKind,
                HasDirtyWorkingTree = snapshot.HasUnstagedChanges,
                HasStagedChanges = snapshot.HasStagedChanges,
                HasUntrackedFiles = snapshot.HasUntrackedFiles,
                HasConflicts = snapshot.HasConflicts,
                BlockingReason = blockingReason,
                Relation = relation,
                ConflictPrediction = conflictPrediction,
                PredictedConflictPaths = predictedConflictPaths,
                ConflictPredictionDetail = conflictPredictionDetail,
                IncomingCommits = incomingCommits,
                IncomingChanges = incomingChanges,
                Snapshot = snapshot,
            };
        }

        public static async Task<Models.MergeExecutionResult> ExecuteMergeAsync(
            string repoPath,
            Models.MergeExecutionOptions options,
            Models.OperationJournalStore journalStore = null,
            Models.ICommandLog outerLog = null,
            CancellationToken cancellationToken = default)
        {
            if (options == null)
                throw new ArgumentNullException(nameof(options));

            var effectiveStore = journalStore ?? Models.OperationJournalStore.Instance;
            var preflight = options.Preflight ?? await InspectMergeAsync(
                repoPath,
                options.SourceRefOrSha,
                options.TargetBranchName,
                options.SourceKind,
                options.SourceDisplayName,
                cancellationToken).ConfigureAwait(false);

            if (!preflight.IsReady)
            {
                var failureKind = ResolveStaticMergeFailureKind(preflight);
                return new Models.MergeExecutionResult
                {
                    Succeeded = false,
                    FailureKind = failureKind,
                    SourceDisplayName = preflight.SourceDisplayName,
                    TargetBranchName = preflight.TargetBranchName,
                    OriginalTargetSha = preflight.TargetResolvedSha,
                    ResultHeadSha = preflight.HeadSha,
                    Summary = string.IsNullOrWhiteSpace(preflight.BlockingReason)
                        ? "当前状态不满足合并前置条件。"
                        : preflight.BlockingReason,
                };
            }

            var mode = options.Mode ?? Models.MergeMode.Default;
            if (mode == Models.MergeMode.FastForward && preflight.Relation == Models.MergeRelationKind.Diverged)
            {
                return new Models.MergeExecutionResult
                {
                    Succeeded = false,
                    FailureKind = Models.MergeRevertFailureKind.MergeFastForwardRequired,
                    SourceDisplayName = preflight.SourceDisplayName,
                    TargetBranchName = preflight.TargetBranchName,
                    OriginalTargetSha = preflight.TargetResolvedSha,
                    ResultHeadSha = preflight.HeadSha,
                    Summary = $"目标分支 {preflight.TargetBranchName} 与 {preflight.SourceDisplayName} 已产生分叉，无法仅通过快进完成合并。",
                };
            }

            MergeInnerOutcome innerOutcome = null;
            var execRes = await OperationJournalService.ExecuteGuardedWriteAsync(
                repoPath: repoPath,
                kind: Models.GitOperationKind.Merge,
                title: preflight.DirectionSummary,
                action: async ctx =>
                {
                    // Re-verify inside repository write lock that target branch wasn't just checked out elsewhere.
                    var otherWt = await FindOtherWorktreeCheckingOutBranchAsync(repoPath, preflight.TargetFullRef, cancellationToken)
                        .ConfigureAwait(false);
                    if (!string.IsNullOrEmpty(otherWt))
                    {
                        innerOutcome = new MergeInnerOutcome
                        {
                            FailureKind = Models.MergeRevertFailureKind.TargetBranchCheckedOutElsewhere,
                            Summary = $"目标分支 {preflight.TargetBranchName} 已在另一工作树（{otherWt}）中检出，已阻止合并。",
                            ResultHeadSha = preflight.HeadSha,
                        };
                        return new OperationActionOutcome<string>
                        {
                            Stage = Models.GitOperationStage.Failed,
                            Summary = innerOutcome.Summary,
                        };
                    }

                    ctx.Record.BranchName = preflight.TargetBranchName;
                    ctx.Record.OriginalTipSha = preflight.TargetResolvedSha;
                    ctx.Record.ExpectedPartnerSha = preflight.SourceResolvedSha;
                    OperationJournalService.TrySaveRecord(effectiveStore, ctx.Record);

                    if (preflight.NeedsCheckoutTargetBranch)
                    {
                        var checkoutRes = await RunGitMutationLoggedAsync(
                            repoPath,
                            ctx.Log,
                            cancellationToken,
                            "--no-pager",
                            "-c",
                            "core.quotepath=off",
                            "checkout",
                            preflight.TargetBranchName).ConfigureAwait(false);

                        if (!checkoutRes.IsSuccess)
                        {
                            var diag = CombineDiag(checkoutRes);
                            innerOutcome = new MergeInnerOutcome
                            {
                                FailureKind = Models.MergeRevertFailureKind.GitError,
                                Summary = $"切换到目标分支 {preflight.TargetBranchName} 失败：{FirstLineOrFallback(diag, "checkout 失败")}",
                                DiagnosticDetail = diag,
                                ResultHeadSha = await ReadHeadShaAsync(repoPath, cancellationToken).ConfigureAwait(false),
                            };
                            return new OperationActionOutcome<string>
                            {
                                Stage = Models.GitOperationStage.Failed,
                                Summary = innerOutcome.Summary,
                                DiagnosticDetail = diag,
                            };
                        }

                        var switchedHead = await ReadHeadShaAsync(repoPath, cancellationToken).ConfigureAwait(false);
                        var switchedRef = await ReadHeadSymbolicRefAsync(repoPath, cancellationToken).ConfigureAwait(false);
                        if (!string.Equals(switchedHead, preflight.TargetResolvedSha, StringComparison.Ordinal) ||
                            !string.Equals(switchedRef, preflight.TargetFullRef, StringComparison.Ordinal))
                        {
                            innerOutcome = new MergeInnerOutcome
                            {
                                FailureKind = Models.MergeRevertFailureKind.SnapshotStale,
                                Summary = $"切换后目标分支 {preflight.TargetBranchName} 的指向与确认快照不一致，已停止合并。",
                                ResultHeadSha = switchedHead,
                            };
                            return new OperationActionOutcome<string>
                            {
                                Stage = Models.GitOperationStage.Failed,
                                Summary = innerOutcome.Summary,
                            };
                        }
                    }

                    var mergeArgs = BuildMergeArgumentList(preflight, mode, options.EditCommitMessage);
                    var mergeRes = await RunGitMutationLoggedAsync(repoPath, ctx.Log, cancellationToken, mergeArgs)
                        .ConfigureAwait(false);

                    if (mergeRes.IsSuccess)
                    {
                        var afterHead = await ReadHeadShaAsync(repoPath, cancellationToken).ConfigureAwait(false);
                        var afterRef = await ReadHeadSymbolicRefAsync(repoPath, cancellationToken).ConfigureAwait(false);
                        var stayedOnTarget = string.Equals(afterRef, preflight.TargetFullRef, StringComparison.Ordinal);
                        var wasFf = preflight.Relation == Models.MergeRelationKind.FastForward &&
                                    (mode == Models.MergeMode.Default || mode == Models.MergeMode.FastForward) &&
                                    string.Equals(afterHead, preflight.SourceResolvedSha, StringComparison.Ordinal);

                        var msg = wasFf
                            ? $"已快进合并：{preflight.DirectionSummary}（停留在 {preflight.TargetBranchName}，未自动推送）。"
                            : $"已完成合并：{preflight.DirectionSummary}（停留在 {preflight.TargetBranchName}，未自动推送）。";

                        innerOutcome = new MergeInnerOutcome
                        {
                            Succeeded = true,
                            FailureKind = Models.MergeRevertFailureKind.None,
                            Summary = msg,
                            DiagnosticDetail = CombineDiag(mergeRes),
                            ResultHeadSha = afterHead,
                            StayedOnTargetBranch = stayedOnTarget,
                            WasFastForward = wasFf,
                        };

                        return new OperationActionOutcome<string>
                        {
                            Stage = Models.GitOperationStage.Succeeded,
                            Summary = msg,
                            TargetTipSha = afterHead,
                            Payload = afterHead,
                        };
                    }

                    var failureResult = await HandlePostMutationFailureAsync(
                        ctx,
                        Models.GitOperationKind.Merge,
                        preflight.TargetBranchName,
                        preflight.TargetResolvedSha,
                        preflight.SourceResolvedSha,
                        mergeRes,
                        effectiveStore,
                        options.BeforeAutoAbortAsync,
                        cancellationToken).ConfigureAwait(false);

                    innerOutcome = new MergeInnerOutcome
                    {
                        Succeeded = false,
                        FailureKind = failureResult.FailureKind,
                        Summary = failureResult.Summary,
                        DiagnosticDetail = failureResult.DiagnosticDetail,
                        ResultHeadSha = failureResult.ResultHeadSha,
                        StayedOnTargetBranch = string.Equals(
                            await ReadHeadSymbolicRefAsync(repoPath, cancellationToken).ConfigureAwait(false),
                            preflight.TargetFullRef,
                            StringComparison.Ordinal),
                        ConflictAbortedCleanly = failureResult.ConflictAbortedCleanly,
                        AutoAbortStoppedForExternalIntervention = failureResult.AutoAbortStoppedForExternalIntervention,
                        ConflictedPaths = failureResult.ConflictedPaths,
                        InterventionReasons = failureResult.InterventionReasons,
                        OwnershipContext = failureResult.OwnershipContext,
                    };

                    return failureResult.ActionOutcome;
                },
                expectedSnapshot: preflight.Snapshot,
                verifyOptions: new Models.OperationSnapshotVerifyOptions
                {
                    RequireSameRepoIdentity = true,
                    RequireSameHead = true,
                    RequireSameInProgressState = true,
                    RequireCleanWorkingTree = true,
                    RequireSameWatchedRefs = true,
                    BranchLockToCheck = preflight.TargetBranchName,
                },
                branchName: preflight.TargetBranchName,
                originalTipSha: preflight.TargetResolvedSha,
                branchLockToCheck: preflight.TargetBranchName,
                journalStore: effectiveStore,
                outerLog: outerLog,
                cancellationToken: cancellationToken).ConfigureAwait(false);

            if (execRes.BlockedByLock || execRes.BlockedBySnapshot)
            {
                return new Models.MergeExecutionResult
                {
                    Succeeded = false,
                    BlockedByLock = execRes.BlockedByLock,
                    BlockedBySnapshot = execRes.BlockedBySnapshot,
                    FailureKind = execRes.BlockedByLock
                        ? Models.MergeRevertFailureKind.RepositoryLocked
                        : Models.MergeRevertFailureKind.SnapshotStale,
                    OperationId = execRes.Record?.OperationId ?? string.Empty,
                    SourceDisplayName = preflight.SourceDisplayName,
                    TargetBranchName = preflight.TargetBranchName,
                    OriginalTargetSha = preflight.TargetResolvedSha,
                    ResultHeadSha = preflight.HeadSha,
                    Summary = execRes.Summary,
                    DiagnosticDetail = execRes.Record?.DiagnosticDetail ?? string.Empty,
                    ExecutionResult = execRes,
                };
            }

            return new Models.MergeExecutionResult
            {
                Succeeded = innerOutcome?.Succeeded ?? execRes.Succeeded,
                FailureKind = innerOutcome?.FailureKind ?? Models.MergeRevertFailureKind.GitError,
                OperationId = execRes.Record?.OperationId ?? string.Empty,
                Summary = innerOutcome?.Summary ?? execRes.Summary,
                DiagnosticDetail = innerOutcome?.DiagnosticDetail ?? execRes.Record?.DiagnosticDetail ?? string.Empty,
                SourceDisplayName = preflight.SourceDisplayName,
                TargetBranchName = preflight.TargetBranchName,
                OriginalTargetSha = preflight.TargetResolvedSha,
                ResultHeadSha = innerOutcome?.ResultHeadSha ?? string.Empty,
                StayedOnTargetBranch = innerOutcome?.StayedOnTargetBranch ?? false,
                WasFastForward = innerOutcome?.WasFastForward ?? false,
                ConflictAbortedCleanly = innerOutcome?.ConflictAbortedCleanly ?? false,
                AutoAbortStoppedForExternalIntervention = innerOutcome?.AutoAbortStoppedForExternalIntervention ?? false,
                ConflictedPaths = innerOutcome?.ConflictedPaths ?? [],
                InterventionReasons = innerOutcome?.InterventionReasons ?? [],
                OwnershipContext = innerOutcome?.OwnershipContext,
                ExecutionResult = execRes,
            };
        }

        public static async Task<Models.RevertPreflight> InspectRevertAsync(
            string repoPath,
            string commitSha,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(repoPath))
                throw new ArgumentException("无效的仓库路径。", nameof(repoPath));

            var (_, commonDir) = await Models.RepositoryWriteGuard
                .ResolveGitDirectoriesAsync(repoPath, cancellationToken)
                .ConfigureAwait(false);

            var currentBranch = await ReadCurrentBranchShortAsync(repoPath, cancellationToken).ConfigureAwait(false);
            var watchedRefs = string.IsNullOrEmpty(currentBranch)
                ? Array.Empty<string>()
                : new[] { "refs/heads/" + currentBranch };

            var snapshot = await CaptureOperationSnapshot
                .CaptureAsync(repoPath, new Models.OperationSnapshotCaptureOptions { RefsToWatch = watchedRefs }, cancellationToken)
                .ConfigureAwait(false);

            string safeCommit;
            try
            {
                safeCommit = CommandArgumentHelper.ValidateNonOptionToken(commitSha, nameof(commitSha));
            }
            catch (ArgumentException ex)
            {
                return BuildBlockedRevertPreflight(repoPath, commonDir, currentBranch, snapshot, null, [], ex.Message);
            }

            var resolvedSha = await ResolveCommitOidAsync(repoPath, safeCommit, cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrEmpty(resolvedSha))
            {
                return BuildBlockedRevertPreflight(
                    repoPath,
                    commonDir,
                    currentBranch,
                    snapshot,
                    null,
                    [],
                    $"找不到待撤销的提交：{safeCommit}");
            }

            var targetCommit = await new QuerySingleCommit(repoPath, resolvedSha).GetResultAsync().ConfigureAwait(false);
            if (targetCommit == null)
            {
                return BuildBlockedRevertPreflight(
                    repoPath,
                    commonDir,
                    currentBranch,
                    snapshot,
                    null,
                    [],
                    $"无法读取提交 {resolvedSha} 的详细信息。");
            }

            var isMergeCommit = targetCommit.Parents.Count > 1;
            var changes = await ReadSingleCommitChangesAsync(repoPath, targetCommit, cancellationToken).ConfigureAwait(false);

            var blockingReason = BuildRevertBlockingReason(snapshot, targetCommit, isMergeCommit);

            return new Models.RevertPreflight
            {
                IsReady = string.IsNullOrEmpty(blockingReason),
                RepoPath = repoPath,
                CanonicalCommonDir = commonDir,
                CurrentBranchName = currentBranch,
                CurrentBranchFullRef = string.IsNullOrEmpty(currentBranch) ? string.Empty : "refs/heads/" + currentBranch,
                IsDetachedHead = snapshot.IsDetachedHead,
                HeadSha = snapshot.HeadSha,
                TargetCommit = targetCommit,
                ParentCount = targetCommit.Parents.Count,
                IsMergeCommit = isMergeCommit,
                AffectedChanges = changes,
                InProgressKind = snapshot.InProgressKind,
                HasDirtyWorkingTree = snapshot.HasUnstagedChanges,
                HasStagedChanges = snapshot.HasStagedChanges,
                HasUntrackedFiles = snapshot.HasUntrackedFiles,
                HasConflicts = snapshot.HasConflicts,
                BlockingReason = blockingReason,
                Snapshot = snapshot,
            };
        }

        public static async Task<Models.RevertExecutionResult> ExecuteRevertAsync(
            string repoPath,
            Models.RevertExecutionOptions options,
            Models.OperationJournalStore journalStore = null,
            Models.ICommandLog outerLog = null,
            CancellationToken cancellationToken = default)
        {
            if (options == null)
                throw new ArgumentNullException(nameof(options));

            var effectiveStore = journalStore ?? Models.OperationJournalStore.Instance;
            var preflight = options.Preflight ?? await InspectRevertAsync(
                repoPath,
                options.CommitSha,
                cancellationToken).ConfigureAwait(false);

            if (!preflight.IsReady)
            {
                var failureKind = ResolveStaticRevertFailureKind(preflight);
                return new Models.RevertExecutionResult
                {
                    Succeeded = false,
                    FailureKind = failureKind,
                    TargetCommitSha = preflight.TargetCommit?.SHA ?? options.CommitSha,
                    OriginalHeadSha = preflight.HeadSha,
                    ResultHeadSha = preflight.HeadSha,
                    Summary = string.IsNullOrWhiteSpace(preflight.BlockingReason)
                        ? "当前状态不满足撤销提交前置条件。"
                        : preflight.BlockingReason,
                };
            }

            var targetSha = preflight.TargetCommit.SHA;
            RevertInnerOutcome innerOutcome = null;

            var execRes = await OperationJournalService.ExecuteGuardedWriteAsync(
                repoPath: repoPath,
                kind: Models.GitOperationKind.Revert,
                title: $"撤销这次提交的改动（保留历史）：{ShortSha(targetSha)}",
                action: async ctx =>
                {
                    ctx.Record.BranchName = preflight.CurrentBranchName;
                    ctx.Record.OriginalTipSha = preflight.HeadSha;
                    ctx.Record.ExpectedPartnerSha = targetSha;
                    OperationJournalService.TrySaveRecord(effectiveStore, ctx.Record);

                    var revertArgs = options.AutoCommit
                        ? new[] { "--no-pager", "-c", "core.quotepath=off", "-c", "core.editor=true", "revert", "--no-edit", targetSha }
                        : new[] { "--no-pager", "-c", "core.quotepath=off", "-c", "core.editor=true", "revert", "--no-edit", "--no-commit", targetSha };

                    var revertRes = await RunGitMutationLoggedAsync(repoPath, ctx.Log, cancellationToken, revertArgs)
                        .ConfigureAwait(false);

                    if (revertRes.IsSuccess)
                    {
                        var afterHead = await ReadHeadShaAsync(repoPath, cancellationToken).ConfigureAwait(false);
                        var msg = options.AutoCommit
                            ? $"已生成反向提交以撤销 {ShortSha(targetSha)} 的改动（原始提交历史已保留）。"
                            : $"已应用撤销 {ShortSha(targetSha)} 的改动到暂存区（未提交）。";

                        innerOutcome = new RevertInnerOutcome
                        {
                            Succeeded = true,
                            FailureKind = Models.MergeRevertFailureKind.None,
                            Summary = msg,
                            DiagnosticDetail = CombineDiag(revertRes),
                            ResultHeadSha = afterHead,
                        };

                        return new OperationActionOutcome<string>
                        {
                            Stage = Models.GitOperationStage.Succeeded,
                            Summary = msg,
                            TargetTipSha = afterHead,
                            Payload = afterHead,
                        };
                    }

                    var failureResult = await HandlePostMutationFailureAsync(
                        ctx,
                        Models.GitOperationKind.Revert,
                        preflight.CurrentBranchName,
                        preflight.HeadSha,
                        targetSha,
                        revertRes,
                        effectiveStore,
                        options.BeforeAutoAbortAsync,
                        cancellationToken).ConfigureAwait(false);

                    innerOutcome = new RevertInnerOutcome
                    {
                        Succeeded = false,
                        FailureKind = failureResult.FailureKind,
                        Summary = failureResult.Summary,
                        DiagnosticDetail = failureResult.DiagnosticDetail,
                        ResultHeadSha = failureResult.ResultHeadSha,
                        ConflictAbortedCleanly = failureResult.ConflictAbortedCleanly,
                        AutoAbortStoppedForExternalIntervention = failureResult.AutoAbortStoppedForExternalIntervention,
                        ConflictedPaths = failureResult.ConflictedPaths,
                        InterventionReasons = failureResult.InterventionReasons,
                        OwnershipContext = failureResult.OwnershipContext,
                    };

                    return failureResult.ActionOutcome;
                },
                expectedSnapshot: preflight.Snapshot,
                verifyOptions: new Models.OperationSnapshotVerifyOptions
                {
                    RequireSameRepoIdentity = true,
                    RequireSameHead = true,
                    RequireSameInProgressState = true,
                    RequireCleanWorkingTree = true,
                    RequireSameWatchedRefs = true,
                    BranchLockToCheck = preflight.CurrentBranchName,
                },
                branchName: preflight.CurrentBranchName,
                originalTipSha: preflight.HeadSha,
                branchLockToCheck: preflight.CurrentBranchName,
                journalStore: effectiveStore,
                outerLog: outerLog,
                cancellationToken: cancellationToken).ConfigureAwait(false);

            if (execRes.BlockedByLock || execRes.BlockedBySnapshot)
            {
                return new Models.RevertExecutionResult
                {
                    Succeeded = false,
                    BlockedByLock = execRes.BlockedByLock,
                    BlockedBySnapshot = execRes.BlockedBySnapshot,
                    FailureKind = execRes.BlockedByLock
                        ? Models.MergeRevertFailureKind.RepositoryLocked
                        : Models.MergeRevertFailureKind.SnapshotStale,
                    OperationId = execRes.Record?.OperationId ?? string.Empty,
                    TargetCommitSha = targetSha,
                    OriginalHeadSha = preflight.HeadSha,
                    ResultHeadSha = preflight.HeadSha,
                    Summary = execRes.Summary,
                    DiagnosticDetail = execRes.Record?.DiagnosticDetail ?? string.Empty,
                    ExecutionResult = execRes,
                };
            }

            return new Models.RevertExecutionResult
            {
                Succeeded = innerOutcome?.Succeeded ?? execRes.Succeeded,
                FailureKind = innerOutcome?.FailureKind ?? Models.MergeRevertFailureKind.GitError,
                OperationId = execRes.Record?.OperationId ?? string.Empty,
                TargetCommitSha = targetSha,
                OriginalHeadSha = preflight.HeadSha,
                ResultHeadSha = innerOutcome?.ResultHeadSha ?? string.Empty,
                Summary = innerOutcome?.Summary ?? execRes.Summary,
                DiagnosticDetail = innerOutcome?.DiagnosticDetail ?? execRes.Record?.DiagnosticDetail ?? string.Empty,
                ConflictAbortedCleanly = innerOutcome?.ConflictAbortedCleanly ?? false,
                AutoAbortStoppedForExternalIntervention = innerOutcome?.AutoAbortStoppedForExternalIntervention ?? false,
                ConflictedPaths = innerOutcome?.ConflictedPaths ?? [],
                InterventionReasons = innerOutcome?.InterventionReasons ?? [],
                OwnershipContext = innerOutcome?.OwnershipContext,
                ExecutionResult = execRes,
            };
        }

        /// <summary>
        /// Verifies whether a recorded GitNest post-conflict baseline still has exclusive GitNest
        /// ownership with zero external or AI-agent modification before any automatic --abort.
        /// </summary>
        public static async Task<Models.ConflictOwnershipVerification> VerifyConflictOwnershipForAbortAsync(
            Models.ConflictOwnershipContext baseline,
            Models.OperationJournalStore journalStore = null,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(baseline);

            var effectiveStore = journalStore ?? Models.OperationJournalStore.Instance;
            var reasons = new List<string>();
            var expectedKind = baseline.OperationKind == Models.GitOperationKind.Revert
                ? Models.GitInProgressKind.Revert
                : Models.GitInProgressKind.Merge;
            var expectedMarkerName = expectedKind == Models.GitInProgressKind.Revert ? "REVERT_HEAD" : "MERGE_HEAD";

            // 1. Journal ownership confirmation
            var record = effectiveStore.TryGet(baseline.OperationId);
            if (record == null)
            {
                reasons.Add("未找到与本次冲突匹配的 GitNest 操作流水记录，无法确认冲突归属。");
            }
            else
            {
                if (record.Kind != baseline.OperationKind)
                    reasons.Add($"操作流水类型不匹配（记录为 {record.Kind}，预期为 {baseline.OperationKind}）。");

                if (!PathsEqual(record.WorktreePath, baseline.RepoPath))
                    reasons.Add("操作流水所属工作树路径与当前仓库不一致。");

                if (!string.Equals(record.OriginalTipSha, baseline.ExpectedHeadSha, StringComparison.Ordinal))
                    reasons.Add("操作流水记录的原提交基线与当前冲突基线不一致。");

                if (!string.Equals(record.ExpectedPartnerSha, baseline.ExpectedPartnerSha, StringComparison.Ordinal))
                    reasons.Add("操作流水记录的来源/撤销目标提交与当前不一致。");
            }

            // 2. Capture live conflict state and verify strictly
            var live = await CaptureLiveConflictSnapshotAsync(
                baseline.RepoPath,
                baseline.CanonicalGitDir,
                expectedMarkerName,
                cancellationToken).ConfigureAwait(false);

            if (live.ActiveMarkers.Count != 1 ||
                !string.Equals(live.ActiveMarkers[0], expectedMarkerName, StringComparison.Ordinal) ||
                live.InProgressKind != expectedKind)
            {
                var foundMarkers = live.ActiveMarkers.Count == 0 ? "无" : string.Join(", ", live.ActiveMarkers);
                reasons.Add($"仓库进行中控制标记异常（预期仅存在 {expectedMarkerName}，实际检测到：{foundMarkers}）。");
            }

            if (!string.Equals(live.HeadSha, baseline.ExpectedHeadSha, StringComparison.Ordinal))
                reasons.Add("冲突后 HEAD 提交已发生移动，存在外部提交或重置动作。");

            if (!string.Equals(live.MarkerFileSha256, baseline.MarkerFileSha256, StringComparison.Ordinal) ||
                !string.Equals(live.MarkerOid, baseline.ExpectedPartnerSha, StringComparison.Ordinal))
            {
                reasons.Add($"控制文件 {expectedMarkerName} 内容或 SHA256 在冲突后被外部修改。");
            }

            if (!string.Equals(live.UnmergedSignature, baseline.UnmergedEntriesSignature, StringComparison.Ordinal))
                reasons.Add("暂存区未合并条目（git ls-files -u）已变更，冲突可能已被外部部分解决或取消。");

            if (!string.Equals(live.StagedFingerprint, baseline.StagedFingerprint, StringComparison.Ordinal))
                reasons.Add("暂存区快照已发生变化，存在外部暂存或取消暂存动作。");

            CompareBaselineEntries(baseline.Entries, live.Entries, reasons);

            var origin = reasons.Count == 0
                ? Models.ConflictOwnershipOrigin.GitNestOwnedClean
                : record == null
                    ? Models.ConflictOwnershipOrigin.ExternalOrUnknown
                    : Models.ConflictOwnershipOrigin.GitNestOwnedWithExternalIntervention;

            return new Models.ConflictOwnershipVerification
            {
                CanSafelyAutoAbort = reasons.Count == 0,
                Origin = origin,
                Reasons = reasons,
                ConflictedPaths = live.ConflictedPaths,
            };
        }

        /// <summary>
        /// Inspects any existing in-progress Git state or conflict markers in read-only mode.
        /// Never triggers --abort during open or refresh so external/AI work cannot be destroyed.
        /// </summary>
        public static async Task<Models.RepositoryInProgressInspection> InspectRepositoryInProgressAsync(
            string repoPath,
            Models.OperationJournalStore journalStore = null,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(repoPath))
                return new Models.RepositoryInProgressInspection { InProgressKind = Models.GitInProgressKind.None };

            var (gitDir, commonDir) = await Models.RepositoryWriteGuard
                .ResolveGitDirectoriesAsync(repoPath, cancellationToken)
                .ConfigureAwait(false);

            var markers = DetectAllInProgressMarkers(gitDir);
            var kind = CaptureOperationSnapshot.DetectInProgressKind(gitDir);
            var unmerged = await ReadUnmergedEntriesAsync(repoPath, cancellationToken).ConfigureAwait(false);
            var conflictedPaths = unmerged.Select(x => x.Path).Distinct(PathComparer).OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList();

            if (kind == Models.GitInProgressKind.None && conflictedPaths.Count == 0 && markers.Count == 0)
            {
                return new Models.RepositoryInProgressInspection
                {
                    InProgressKind = Models.GitInProgressKind.None,
                    Origin = Models.ConflictOwnershipOrigin.None,
                };
            }

            var headSha = await ReadHeadShaAsync(repoPath, cancellationToken).ConfigureAwait(false);
            var primaryMarker = kind switch
            {
                Models.GitInProgressKind.Merge => "MERGE_HEAD",
                Models.GitInProgressKind.Revert => "REVERT_HEAD",
                Models.GitInProgressKind.CherryPick => "CHERRY_PICK_HEAD",
                _ => string.Empty,
            };
            var markerFullPath = string.IsNullOrEmpty(primaryMarker) ? string.Empty : Path.Combine(gitDir, primaryMarker);
            var (_, markerOid) = string.IsNullOrEmpty(markerFullPath)
                ? (string.Empty, string.Empty)
                : await ReadMarkerFileHashAndOidAsync(markerFullPath, cancellationToken).ConfigureAwait(false);
            DateTime? markerLastWriteUtc = !string.IsNullOrEmpty(markerFullPath) && File.Exists(markerFullPath)
                ? File.GetLastWriteTimeUtc(markerFullPath)
                : null;

            var effectiveStore = journalStore ?? Models.OperationJournalStore.Instance;
            var recent = effectiveStore.GetByRepository(commonDir, 30);
            var matchingRecord = FindMatchingJournalRecord(recent, repoPath, kind, headSha, markerOid, markerLastWriteUtc);

            var origin = Models.ConflictOwnershipOrigin.ExternalOrUnknown;
            var originLabel = "外部操作进行中";
            var headline = BuildExternalHeadline(kind, conflictedPaths.Count);
            var detail = "检测到非 GitNest 发起（或已被外部流程接管）的进行中状态。GitNest 已暂停保护类写入且绝不自动执行 --abort；请在外部工具/命令行完成或解决后点击刷新。";

            if (matchingRecord != null)
            {
                origin = Models.ConflictOwnershipOrigin.GitNestOwnedWithExternalIntervention;
                originLabel = "冲突状态已保留（等待外部处理）";
                headline = BuildGitNestRetainedHeadline(kind, matchingRecord);
                detail = string.IsNullOrWhiteSpace(matchingRecord.StepSummary)
                    ? detail
                    : matchingRecord.StepSummary;
            }

            return new Models.RepositoryInProgressInspection
            {
                InProgressKind = kind,
                Origin = origin,
                OriginLabel = originLabel,
                MarkerTargetSha = markerOid,
                ActiveMarkers = markers,
                ConflictedPaths = conflictedPaths,
                UnfinishedOperation = matchingRecord,
                Headline = headline,
                Detail = detail,
                CanOfferSafeAbort = false,
            };
        }

        private static async Task<FailureDispositionResult> HandlePostMutationFailureAsync(
            OperationExecutionContext ctx,
            Models.GitOperationKind kind,
            string branchName,
            string originalHeadSha,
            string expectedPartnerSha,
            Command.Result failedCmd,
            Models.OperationJournalStore journalStore,
            Func<Models.ConflictOwnershipContext, Task> beforeAutoAbortAsync,
            CancellationToken cancellationToken)
        {
            var diag = CombineDiag(failedCmd);
            var repoPath = ctx.RepoPath;
            var gitDir = string.IsNullOrEmpty(ctx.CanonicalGitDir)
                ? (await Models.RepositoryWriteGuard.ResolveGitDirectoriesAsync(repoPath, cancellationToken).ConfigureAwait(false)).GitDir
                : ctx.CanonicalGitDir;

            var expectedInProgress = kind == Models.GitOperationKind.Revert
                ? Models.GitInProgressKind.Revert
                : Models.GitInProgressKind.Merge;
            var expectedMarkerName = expectedInProgress == Models.GitInProgressKind.Revert ? "REVERT_HEAD" : "MERGE_HEAD";
            var abortSubcommand = expectedInProgress == Models.GitInProgressKind.Revert ? "revert" : "merge";

            var unmergedEntries = await ReadUnmergedEntriesAsync(repoPath, cancellationToken).ConfigureAwait(false);
            var conflictedPaths = unmergedEntries
                .Select(x => x.Path)
                .Distinct(PathComparer)
                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var liveInProgress = CaptureOperationSnapshot.DetectInProgressKind(gitDir);
            var hasConflictEvidence = conflictedPaths.Count > 0 || ContainsConflictBanner(diag);

            // 1. Distinguish non-conflict failures (Hook rejected, GPG/SSH signing failed, lock, ff-only, etc.)
            if (!hasConflictEvidence)
            {
                var nonConflictKind = ClassifyNonConflictFailure(diag, liveInProgress, expectedInProgress);
                var currentHead = await ReadHeadShaAsync(repoPath, cancellationToken).ConfigureAwait(false);
                var stillInProgress = liveInProgress != Models.GitInProgressKind.None;

                var summary = nonConflictKind switch
                {
                    Models.MergeRevertFailureKind.MergeFastForwardRequired =>
                        $"无法快进合并到 {branchName}，请检查分叉情况后重试。",
                    Models.MergeRevertFailureKind.SigningFailed =>
                        stillInProgress
                            ? $"提交签名失败，当前 {abortSubcommand} 的待提交改动已保留：{FirstLineOrFallback(diag, "GPG/SSH 签名失败")}"
                            : $"提交签名失败：{FirstLineOrFallback(diag, "GPG/SSH 签名失败")}",
                    Models.MergeRevertFailureKind.HookRejected =>
                        stillInProgress
                            ? $"Git 钩子（hook）阻止了提交完成，当前 {abortSubcommand} 进度已保留：{FirstLineOrFallback(diag, "钩子执行失败")}"
                            : $"Git 钩子（hook）检查未通过：{FirstLineOrFallback(diag, "钩子拒绝提交")}",
                    Models.MergeRevertFailureKind.RepositoryLocked =>
                        "仓库锁文件阻止完成操作，请等待其他 Git 进程结束。",
                    Models.MergeRevertFailureKind.PreconditionFailed =>
                        FirstLineOrFallback(diag, "缺少提交身份或前置条件未满足。"),
                    _ => FirstLineOrFallback(diag, kind == Models.GitOperationKind.Revert ? "撤销提交失败。" : "合并失败。"),
                };

                var stage = stillInProgress ? Models.GitOperationStage.PendingVerification : Models.GitOperationStage.Failed;
                return new FailureDispositionResult
                {
                    FailureKind = nonConflictKind,
                    Summary = summary,
                    DiagnosticDetail = diag,
                    ResultHeadSha = currentHead,
                    ActionOutcome = new OperationActionOutcome<string>
                    {
                        Stage = stage,
                        Summary = summary,
                        DiagnosticDetail = diag,
                        Payload = currentHead,
                    },
                };
            }

            // 2. Conflict path: capture strict baseline owned by GitNest right now
            var liveSnapshot = await CaptureLiveConflictSnapshotAsync(repoPath, gitDir, expectedMarkerName, cancellationToken)
                .ConfigureAwait(false);

            var ownership = new Models.ConflictOwnershipContext
            {
                OperationId = ctx.OperationId,
                OperationKind = kind,
                RepoPath = repoPath,
                CanonicalGitDir = gitDir,
                CanonicalCommonDir = ctx.CanonicalCommonDir,
                TargetBranchName = branchName ?? string.Empty,
                ExpectedHeadSha = originalHeadSha,
                ExpectedPartnerSha = expectedPartnerSha,
                MarkerFilePath = liveSnapshot.MarkerFilePath,
                MarkerFileSha256 = liveSnapshot.MarkerFileSha256,
                ActiveMarkers = liveSnapshot.ActiveMarkers,
                StagedFingerprint = liveSnapshot.StagedFingerprint,
                UnmergedEntriesSignature = liveSnapshot.UnmergedSignature,
                ConflictedPaths = liveSnapshot.ConflictedPaths,
                Entries = liveSnapshot.Entries,
            };

            if (beforeAutoAbortAsync != null)
                await beforeAutoAbortAsync(ownership).ConfigureAwait(false);

            var verify = await VerifyConflictOwnershipForAbortAsync(ownership, journalStore, cancellationToken)
                .ConfigureAwait(false);

            if (!verify.CanSafelyAutoAbort)
            {
                var headAfter = await ReadHeadShaAsync(repoPath, cancellationToken).ConfigureAwait(false);
                var reasonsText = string.Join("；", verify.Reasons);
                var stoppedMsg = kind == Models.GitOperationKind.Revert
                    ? $"检测到外部介入或状态不匹配，已停止自动执行 git revert --abort 并保留当前文件状态：{reasonsText}"
                    : $"检测到外部介入或状态不匹配，已停止自动执行 git merge --abort 并保留当前文件状态：{reasonsText}";

                return new FailureDispositionResult
                {
                    FailureKind = Models.MergeRevertFailureKind.ConflictOwnershipExternalIntervention,
                    Summary = stoppedMsg,
                    DiagnosticDetail = diag + (string.IsNullOrEmpty(reasonsText) ? string.Empty : "\n" + reasonsText),
                    ResultHeadSha = headAfter,
                    AutoAbortStoppedForExternalIntervention = true,
                    ConflictedPaths = verify.ConflictedPaths,
                    InterventionReasons = verify.Reasons,
                    OwnershipContext = ownership,
                    ActionOutcome = new OperationActionOutcome<string>
                    {
                        Stage = Models.GitOperationStage.PendingVerification,
                        Summary = stoppedMsg,
                        DiagnosticDetail = reasonsText,
                        Payload = headAfter,
                    },
                };
            }

            // 3. Ownership and clean baseline confirmed -> execute --abort and strictly re-verify clean state
            var abortRes = await RunGitMutationLoggedAsync(
                repoPath,
                ctx.Log,
                cancellationToken,
                "--no-pager",
                "-c",
                "core.quotepath=off",
                abortSubcommand,
                "--abort").ConfigureAwait(false);

            var postAbort = await CaptureOperationSnapshot
                .CaptureAsync(repoPath, new Models.OperationSnapshotCaptureOptions(), cancellationToken)
                .ConfigureAwait(false);
            var postAbortMarkers = DetectAllInProgressMarkers(gitDir);
            var targetBranchAfter = string.IsNullOrEmpty(branchName)
                ? postAbort.HeadSha
                : await ResolveCommitOidAsync(repoPath, "refs/heads/" + branchName, cancellationToken).ConfigureAwait(false);

            var abortRestoredCleanly = abortRes.IsSuccess &&
                                       postAbort.InProgressKind == Models.GitInProgressKind.None &&
                                       postAbortMarkers.Count == 0 &&
                                       postAbort.IsWorkingTreeClean &&
                                       string.Equals(postAbort.HeadSha, originalHeadSha, StringComparison.Ordinal) &&
                                       (string.IsNullOrEmpty(branchName) ||
                                        string.Equals(targetBranchAfter, originalHeadSha, StringComparison.Ordinal));

            if (!abortRestoredCleanly)
            {
                var abortDiag = CombineDiag(abortRes);
                var failSummary = $"尝试自动取消冲突失败，仓库可能仍处于未干净恢复状态：{FirstLineOrFallback(abortDiag, "请检查仓库状态")}";
                return new FailureDispositionResult
                {
                    FailureKind = Models.MergeRevertFailureKind.AbortFailed,
                    Summary = failSummary,
                    DiagnosticDetail = diag + "\n--- abort ---\n" + abortDiag,
                    ResultHeadSha = postAbort.HeadSha,
                    ConflictedPaths = verify.ConflictedPaths,
                    OwnershipContext = ownership,
                    ActionOutcome = new OperationActionOutcome<string>
                    {
                        Stage = Models.GitOperationStage.PendingVerification,
                        Summary = failSummary,
                        DiagnosticDetail = abortDiag,
                        Payload = postAbort.HeadSha,
                    },
                };
            }

            var cleanAbortSummary = kind == Models.GitOperationKind.Revert
                ? "撤销提交存在冲突，已取消，当前分支未改动"
                : "合并存在冲突，已取消，目标分支未合并";

            return new FailureDispositionResult
            {
                FailureKind = Models.MergeRevertFailureKind.ConflictAbortedCleanly,
                Summary = cleanAbortSummary,
                DiagnosticDetail = diag,
                ResultHeadSha = postAbort.HeadSha,
                ConflictAbortedCleanly = true,
                ConflictedPaths = conflictedPaths,
                OwnershipContext = ownership,
                ActionOutcome = new OperationActionOutcome<string>
                {
                    Stage = Models.GitOperationStage.Failed,
                    Summary = cleanAbortSummary,
                    TargetTipSha = postAbort.HeadSha,
                    DiagnosticDetail = diag,
                    Payload = postAbort.HeadSha,
                },
            };
        }

        private static async Task<ResolvedSourceInfo> ResolveMergeSourceAsync(
            string repoPath,
            string rawSource,
            Models.MergeSourceKind sourceKind,
            string displayName,
            CancellationToken cancellationToken)
        {
            string safe;
            try
            {
                safe = CommandArgumentHelper.ValidateNonOptionToken(rawSource, nameof(rawSource));
            }
            catch (ArgumentException ex)
            {
                return new ResolvedSourceInfo(false, displayName ?? string.Empty, rawSource ?? string.Empty, string.Empty, string.Empty, ex.Message);
            }

            switch (sourceKind)
            {
                case Models.MergeSourceKind.LocalBranch:
                {
                    var shortName = safe.StartsWith("refs/heads/", StringComparison.Ordinal)
                        ? safe["refs/heads/".Length..]
                        : safe;
                    var fullRef = "refs/heads/" + shortName;
                    var sha = await ResolveCommitOidAsync(repoPath, fullRef, cancellationToken).ConfigureAwait(false);
                    return string.IsNullOrEmpty(sha)
                        ? new ResolvedSourceInfo(false, displayName ?? shortName, shortName, fullRef, string.Empty, $"来源本地分支 {shortName} 不存在。")
                        : new ResolvedSourceInfo(true, displayName ?? shortName, shortName, fullRef, sha, string.Empty);
                }
                case Models.MergeSourceKind.RemoteBranch:
                {
                    // Must resolve against an explicit fetched remote-tracking snapshot; never guess default remote or push target.
                    var relName = safe.StartsWith("refs/remotes/", StringComparison.Ordinal)
                        ? safe["refs/remotes/".Length..]
                        : safe;
                    if (!relName.Contains('/', StringComparison.Ordinal))
                    {
                        return new ResolvedSourceInfo(
                            false,
                            displayName ?? relName,
                            relName,
                            string.Empty,
                            string.Empty,
                            $"远端分支快照 \"{relName}\" 必须包含远端名与分支名（例如 origin/feature）。");
                    }

                    var fullRef = "refs/remotes/" + relName;
                    var sha = await ResolveCommitOidAsync(repoPath, fullRef, cancellationToken).ConfigureAwait(false);
                    return string.IsNullOrEmpty(sha)
                        ? new ResolvedSourceInfo(
                            false,
                            displayName ?? relName,
                            relName,
                            fullRef,
                            string.Empty,
                            $"未找到远端分支本地快照 {relName}，请先抓取（Fetch）后再合并。")
                        : new ResolvedSourceInfo(true, displayName ?? relName, relName, fullRef, sha, string.Empty);
                }
                case Models.MergeSourceKind.Tag:
                {
                    var tagName = safe.StartsWith("refs/tags/", StringComparison.Ordinal)
                        ? safe["refs/tags/".Length..]
                        : safe;
                    var fullRef = "refs/tags/" + tagName;
                    var sha = await ResolveCommitOidAsync(repoPath, fullRef, cancellationToken).ConfigureAwait(false);
                    return string.IsNullOrEmpty(sha)
                        ? new ResolvedSourceInfo(false, displayName ?? tagName, tagName, fullRef, string.Empty, $"来源标签 {tagName} 不存在。")
                        : new ResolvedSourceInfo(true, displayName ?? tagName, tagName, fullRef, sha, string.Empty);
                }
                case Models.MergeSourceKind.Commit:
                {
                    var sha = await ResolveCommitOidAsync(repoPath, safe, cancellationToken).ConfigureAwait(false);
                    var shown = string.IsNullOrWhiteSpace(displayName) ? ShortSha(sha.Length > 0 ? sha : safe) : displayName;
                    return string.IsNullOrEmpty(sha)
                        ? new ResolvedSourceInfo(false, shown, safe, string.Empty, string.Empty, $"找不到来源提交 {safe}。")
                        : new ResolvedSourceInfo(true, shown, sha, string.Empty, sha, string.Empty);
                }
                default:
                    return new ResolvedSourceInfo(false, displayName ?? safe, safe, string.Empty, string.Empty, "不支持的合并来源类型。");
            }
        }

        private static string[] BuildMergeArgumentList(
            Models.MergePreflight preflight,
            Models.MergeMode mode,
            bool editMessage)
        {
            var args = new List<string>
            {
                "--no-pager",
                "-c",
                "core.quotepath=off",
                "-c",
                "core.editor=true",
                "merge",
                "--progress",
                editMessage ? "--edit" : "--no-edit",
            };

            if (!string.IsNullOrWhiteSpace(mode.Arg))
            {
                foreach (var flag in mode.Arg.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                    args.Add(flag);
            }

            // Refs have already been verified against the exact resolved OID under watched snapshot;
            // using the non-option full ref (or resolved SHA for Commit) keeps Git's native merge commit message.
            var sourceToken = string.IsNullOrEmpty(preflight.SourceFullRef)
                ? preflight.SourceResolvedSha
                : preflight.SourceRefOrSha;
            args.Add(CommandArgumentHelper.ValidateNonOptionToken(sourceToken, nameof(sourceToken)));

            return args.ToArray();
        }

        private static async Task<Models.MergeRelationKind> DetermineMergeRelationAsync(
            string repoPath,
            string targetSha,
            string sourceSha,
            CancellationToken cancellationToken)
        {
            if (string.Equals(targetSha, sourceSha, StringComparison.Ordinal))
                return Models.MergeRelationKind.AlreadyUpToDate;

            var sourceInTarget = await Command.RunGitDirectAsync(
                repoPath,
                cancellationToken,
                "merge-base",
                "--is-ancestor",
                sourceSha,
                targetSha).ConfigureAwait(false);
            if (sourceInTarget.IsSuccess)
                return Models.MergeRelationKind.AlreadyUpToDate;

            var targetInSource = await Command.RunGitDirectAsync(
                repoPath,
                cancellationToken,
                "merge-base",
                "--is-ancestor",
                targetSha,
                sourceSha).ConfigureAwait(false);
            if (targetInSource.IsSuccess)
                return Models.MergeRelationKind.FastForward;

            var mergeBase = await Command.RunGitDirectAsync(
                repoPath,
                cancellationToken,
                "merge-base",
                targetSha,
                sourceSha).ConfigureAwait(false);
            return mergeBase.IsSuccess
                ? Models.MergeRelationKind.Diverged
                : Models.MergeRelationKind.UnrelatedHistories;
        }

        private static async Task<(Models.MergeConflictPrediction Prediction, IReadOnlyList<string> ConflictPaths, string Detail)>
            RunNonMutatingMergeTreeCheckAsync(
                string repoPath,
                string targetSha,
                string sourceSha,
                CancellationToken cancellationToken)
        {
            var res = await Command.RunGitDirectAsync(
                repoPath,
                TimeSpan.FromSeconds(30),
                cancellationToken,
                "--no-pager",
                "-c",
                "core.quotepath=off",
                "merge-tree",
                "--write-tree",
                targetSha,
                sourceSha).ConfigureAwait(false);

            if (res.IsSuccess)
            {
                return (Models.MergeConflictPrediction.Clean, [], "无冲突预计。");
            }

            var diag = CombineDiag(res);
            if (res.ExitCode == 1)
            {
                var paths = ParseMergeTreeConflictPaths(res.StdOut + "\n" + res.StdErr);
                var detail = paths.Count > 0
                    ? $"预计存在 {paths.Count} 处冲突，执行时若冲突且无外部介入将自动取消合并。"
                    : "预计存在合并冲突，执行时若冲突且无外部介入将自动取消合并。";
                return (Models.MergeConflictPrediction.HasConflicts, paths, detail);
            }

            if (diag.Contains("unrelated histories", StringComparison.OrdinalIgnoreCase))
            {
                return (Models.MergeConflictPrediction.BlockedUnrelatedHistories, [], "两个分支没有共同提交祖先，不能直接合并。");
            }

            if (diag.Contains("unknown option", StringComparison.OrdinalIgnoreCase) || res.ExitCode == 129)
            {
                return (Models.MergeConflictPrediction.Unavailable, [], "当前 Git 版本不支持无副作用冲突预检。");
            }

            return (Models.MergeConflictPrediction.Unavailable, [], FirstLineOrFallback(diag, "冲突预检不可用。"));
        }

        private static IReadOnlyList<string> ParseMergeTreeConflictPaths(string output)
        {
            var set = new SortedSet<string>(PathComparer);
            if (string.IsNullOrWhiteSpace(output))
                return [];

            foreach (var raw in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
            {
                var line = raw.Trim();
                var m = REG_MERGE_TREE_CONFLICT_PATH().Match(line);
                if (m.Success)
                {
                    var path = m.Groups[1].Value.Trim();
                    if (!string.IsNullOrEmpty(path))
                        set.Add(path);
                }
            }

            return set.ToList();
        }

        private static async Task<IReadOnlyList<Models.Commit>> ReadIncomingCommitsAsync(
            string repoPath,
            string targetSha,
            string sourceSha,
            CancellationToken cancellationToken)
        {
            if (!IsHexOid(targetSha) || !IsHexOid(sourceSha))
                return [];

            try
            {
                return await new QueryCommits(repoPath, $"{targetSha}..{sourceSha} -n 100", markMerged: false)
                {
                    CancellationToken = cancellationToken,
                }.GetResultAsync().ConfigureAwait(false);
            }
            catch
            {
                return [];
            }
        }

        private static async Task<IReadOnlyList<Models.Change>> ReadThreeDotChangesAsync(
            string repoPath,
            string targetSha,
            string sourceSha,
            CancellationToken cancellationToken)
        {
            if (!IsHexOid(targetSha) || !IsHexOid(sourceSha))
                return [];

            var res = await Command.RunGitDirectAsync(
                repoPath,
                TimeSpan.FromSeconds(30),
                cancellationToken,
                "--no-pager",
                "-c",
                "core.quotepath=off",
                "diff",
                "--name-status",
                $"{targetSha}...{sourceSha}").ConfigureAwait(false);

            return res.IsSuccess ? ParseNameStatusChanges(res.StdOut) : [];
        }

        private static async Task<IReadOnlyList<Models.Change>> ReadSingleCommitChangesAsync(
            string repoPath,
            Models.Commit commit,
            CancellationToken cancellationToken)
        {
            if (commit == null || !IsHexOid(commit.SHA))
                return [];

            var args = commit.Parents.Count == 0
                ? new[] { "--no-pager", "-c", "core.quotepath=off", "diff-tree", "--no-commit-id", "--name-status", "-r", "--root", commit.SHA }
                : new[] { "--no-pager", "-c", "core.quotepath=off", "diff-tree", "--no-commit-id", "--name-status", "-r", commit.SHA };

            var res = await Command.RunGitDirectAsync(repoPath, TimeSpan.FromSeconds(30), cancellationToken, args)
                .ConfigureAwait(false);

            return res.IsSuccess ? ParseNameStatusChanges(res.StdOut) : [];
        }

        private static List<Models.Change> ParseNameStatusChanges(string output)
        {
            var changes = new List<Models.Change>();
            if (string.IsNullOrWhiteSpace(output))
                return changes;

            foreach (var rawLine in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
            {
                var renameMatch = REG_DIFF_RENAME_LINE().Match(rawLine);
                if (renameMatch.Success)
                {
                    var c = new Models.Change { Path = renameMatch.Groups[3].Value, OriginalPath = renameMatch.Groups[2].Value };
                    c.Set(renameMatch.Groups[1].Value == "R" ? Models.ChangeState.Renamed : Models.ChangeState.Copied);
                    changes.Add(c);
                    continue;
                }

                var match = REG_DIFF_LINE().Match(rawLine);
                if (!match.Success)
                    continue;

                var ch = new Models.Change { Path = match.Groups[2].Value };
                switch (match.Groups[1].Value[0])
                {
                    case 'M': ch.Set(Models.ChangeState.Modified); break;
                    case 'A': ch.Set(Models.ChangeState.Added); break;
                    case 'D': ch.Set(Models.ChangeState.Deleted); break;
                    case 'T': ch.Set(Models.ChangeState.TypeChanged); break;
                    default: continue;
                }

                changes.Add(ch);
            }

            changes.Sort((l, r) => Models.NumericSort.Compare(l.Path, r.Path));
            return changes;
        }

        private static async Task<string> FindOtherWorktreeCheckingOutBranchAsync(
            string repoPath,
            string fullBranchRef,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var worktrees = await new Worktree(repoPath).ReadAllAsync().ConfigureAwait(false);
            foreach (var wt in worktrees)
            {
                if (!wt.IsBare &&
                    string.Equals(wt.Branch, fullBranchRef, StringComparison.Ordinal) &&
                    !PathsEqual(wt.FullPath, repoPath))
                {
                    return wt.FullPath;
                }
            }

            return string.Empty;
        }

        private static string BuildMergeBlockingReason(
            Models.OperationConfirmationSnapshot snapshot,
            string checkedOutElsewherePath,
            Models.MergeRelationKind relation,
            string sourceFullRef,
            string targetFullRef)
        {
            if (snapshot.InProgressKind != Models.GitInProgressKind.None)
                return $"当前仓库存在正在进行的 {snapshot.InProgressKind} 状态，禁止启动新的合并。";

            if (!snapshot.IsWorkingTreeClean)
                return "合并前要求工作区与暂存区完全干净（不能有未提交修改、未暂存变动或未跟踪文件）。";

            if (!string.IsNullOrEmpty(checkedOutElsewherePath))
                return $"目标分支已在另一工作树（{checkedOutElsewherePath}）中检出，请在对应工作树执行合并。";

            if (!string.IsNullOrEmpty(sourceFullRef) &&
                string.Equals(sourceFullRef, targetFullRef, StringComparison.Ordinal))
                return "来源分支与目标分支相同，无需合并。";

            if (relation == Models.MergeRelationKind.AlreadyUpToDate)
                return "目标分支已包含来源的所有改动，没有可并入的新提交。";

            if (relation == Models.MergeRelationKind.UnrelatedHistories)
                return "来源与目标没有共同提交祖先，首版阻止合并无共同历史的分支。";

            return string.Empty;
        }

        private static string BuildRevertBlockingReason(
            Models.OperationConfirmationSnapshot snapshot,
            Models.Commit targetCommit,
            bool isMergeCommit)
        {
            if (isMergeCommit)
                return "首版仅支持撤销单个普通提交；合并提交存在多个父提交选择，请使用外部 Git 工具处理。";

            if (snapshot.InProgressKind != Models.GitInProgressKind.None)
                return $"当前仓库存在正在进行的 {snapshot.InProgressKind} 状态，禁止启动撤销提交操作。";

            if (!snapshot.IsWorkingTreeClean)
                return "撤销提交前要求工作区与暂存区完全干净（不能有未提交修改、未暂存变动或未跟踪文件）。";

            if (string.IsNullOrEmpty(snapshot.HeadSha))
                return "当前分支尚无提交，无法执行 revert。";

            if (targetCommit == null)
                return "未能识别目标提交。";

            return string.Empty;
        }

        private static Models.MergeRevertFailureKind ResolveStaticMergeFailureKind(Models.MergePreflight preflight)
        {
            if (!string.IsNullOrEmpty(preflight.TargetCheckedOutWorktreePath))
                return Models.MergeRevertFailureKind.TargetBranchCheckedOutElsewhere;
            if (preflight.Relation == Models.MergeRelationKind.AlreadyUpToDate)
                return Models.MergeRevertFailureKind.MergeAlreadyUpToDate;
            if (preflight.InProgressKind != Models.GitInProgressKind.None || preflight.Snapshot is { IsWorkingTreeClean: false })
                return Models.MergeRevertFailureKind.PreconditionFailed;
            return Models.MergeRevertFailureKind.InvalidRequest;
        }

        private static Models.MergeRevertFailureKind ResolveStaticRevertFailureKind(Models.RevertPreflight preflight)
        {
            if (preflight.IsMergeCommit)
                return Models.MergeRevertFailureKind.MergeCommitNotSupported;
            if (preflight.InProgressKind != Models.GitInProgressKind.None || preflight.Snapshot is { IsWorkingTreeClean: false })
                return Models.MergeRevertFailureKind.PreconditionFailed;
            return Models.MergeRevertFailureKind.InvalidRequest;
        }

        private static Models.MergePreflight BuildBlockedMergePreflight(
            string repoPath,
            string commonDir,
            Models.OperationConfirmationSnapshot snapshot,
            Models.MergeSourceKind sourceKind,
            string sourceDisplayName,
            string sourceRefOrSha,
            string targetBranch,
            string reason,
            string targetFullRef = "",
            string targetSha = "")
        {
            return new Models.MergePreflight
            {
                IsReady = false,
                RepoPath = repoPath,
                CanonicalCommonDir = commonDir,
                CurrentBranchName = snapshot.HeadRefName.StartsWith("refs/heads/", StringComparison.Ordinal)
                    ? snapshot.HeadRefName["refs/heads/".Length..]
                    : string.Empty,
                IsDetachedHead = snapshot.IsDetachedHead,
                HeadSha = snapshot.HeadSha,
                SourceKind = sourceKind,
                SourceDisplayName = sourceDisplayName ?? string.Empty,
                SourceRefOrSha = sourceRefOrSha ?? string.Empty,
                TargetBranchName = targetBranch ?? string.Empty,
                TargetFullRef = targetFullRef,
                TargetResolvedSha = targetSha,
                InProgressKind = snapshot.InProgressKind,
                HasDirtyWorkingTree = snapshot.HasUnstagedChanges,
                HasStagedChanges = snapshot.HasStagedChanges,
                HasUntrackedFiles = snapshot.HasUntrackedFiles,
                HasConflicts = snapshot.HasConflicts,
                BlockingReason = reason,
                Relation = Models.MergeRelationKind.InvalidTargetOrSource,
                Snapshot = snapshot,
            };
        }

        private static Models.RevertPreflight BuildBlockedRevertPreflight(
            string repoPath,
            string commonDir,
            string currentBranch,
            Models.OperationConfirmationSnapshot snapshot,
            Models.Commit targetCommit,
            IReadOnlyList<Models.Change> changes,
            string reason)
        {
            return new Models.RevertPreflight
            {
                IsReady = false,
                RepoPath = repoPath,
                CanonicalCommonDir = commonDir,
                CurrentBranchName = currentBranch,
                CurrentBranchFullRef = string.IsNullOrEmpty(currentBranch) ? string.Empty : "refs/heads/" + currentBranch,
                IsDetachedHead = snapshot.IsDetachedHead,
                HeadSha = snapshot.HeadSha,
                TargetCommit = targetCommit,
                ParentCount = targetCommit?.Parents.Count ?? 0,
                IsMergeCommit = (targetCommit?.Parents.Count ?? 0) > 1,
                AffectedChanges = changes ?? [],
                InProgressKind = snapshot.InProgressKind,
                HasDirtyWorkingTree = snapshot.HasUnstagedChanges,
                HasStagedChanges = snapshot.HasStagedChanges,
                HasUntrackedFiles = snapshot.HasUntrackedFiles,
                HasConflicts = snapshot.HasConflicts,
                BlockingReason = reason,
                Snapshot = snapshot,
            };
        }

        private static Models.MergeRevertFailureKind ClassifyNonConflictFailure(
            string diag,
            Models.GitInProgressKind liveInProgress,
            Models.GitInProgressKind expectedInProgress)
        {
            if (ContainsAny(diag, "Not possible to fast-forward", "cannot fast-forward"))
                return Models.MergeRevertFailureKind.MergeFastForwardRequired;

            if (ContainsAny(diag, "index.lock", "HEAD.lock", "Unable to create", "Another git process seems to be running"))
                return Models.MergeRevertFailureKind.RepositoryLocked;

            if (ContainsAny(diag, "Author identity unknown", "Please tell me who you are", "unable to auto-detect email address"))
                return Models.MergeRevertFailureKind.PreconditionFailed;

            if (ContainsAny(diag,
                "gpg failed",
                "failed to sign the data",
                "signing failed",
                "no secret key",
                "cannot run gpg",
                "bad key",
                "error: gpg.ssh",
                "Couldn't sign"))
            {
                return Models.MergeRevertFailureKind.SigningFailed;
            }

            if (ContainsAny(diag,
                "pre-commit",
                "pre-merge-commit",
                "prepare-commit-msg",
                "commit-msg",
                "post-rewrite",
                "Not committing merge",
                "hook declined",
                "hook failed",
                "hook exited") ||
                liveInProgress == expectedInProgress)
            {
                return Models.MergeRevertFailureKind.HookRejected;
            }

            return Models.MergeRevertFailureKind.GitError;
        }

        private static bool ContainsConflictBanner(string diag) =>
            !string.IsNullOrEmpty(diag) &&
            (diag.Contains("CONFLICT (", StringComparison.OrdinalIgnoreCase) ||
             diag.Contains("Automatic merge failed", StringComparison.OrdinalIgnoreCase) ||
             diag.Contains("could not revert", StringComparison.OrdinalIgnoreCase) &&
             diag.Contains("after resolving the conflicts", StringComparison.OrdinalIgnoreCase));

        private static async Task<LiveConflictSnapshot> CaptureLiveConflictSnapshotAsync(
            string repoPath,
            string gitDir,
            string expectedMarkerName,
            CancellationToken cancellationToken)
        {
            var headSha = await ReadHeadShaAsync(repoPath, cancellationToken).ConfigureAwait(false);
            var markers = DetectAllInProgressMarkers(gitDir);
            var inProgress = CaptureOperationSnapshot.DetectInProgressKind(gitDir);

            var markerPath = string.IsNullOrEmpty(expectedMarkerName) || string.IsNullOrEmpty(gitDir)
                ? string.Empty
                : Path.Combine(gitDir, expectedMarkerName);
            var (markerSha256, markerOid) = await ReadMarkerFileHashAndOidAsync(markerPath, cancellationToken)
                .ConfigureAwait(false);

            var unmerged = await ReadUnmergedEntriesAsync(repoPath, cancellationToken).ConfigureAwait(false);
            var unmergedGroups = unmerged
                .GroupBy(u => u.Path, PathComparer)
                .ToDictionary(
                    g => g.Key,
                    g => string.Join("|", g.OrderBy(e => e.Stage).Select(e => $"{e.Stage}:{e.Mode}:{e.Oid}")),
                    PathComparer);

            var unmergedSignature = ComputeSha256Hex(string.Join("\n",
                unmergedGroups.OrderBy(kv => kv.Key, PathComparer).Select(kv => $"{kv.Key}={kv.Value}")));

            var stagedSnap = await QueryStagedSnapshot.CaptureAsync(repoPath, cancellationToken).ConfigureAwait(false);
            var changeMap = stagedSnap.AllChanges
                .GroupBy(c => c.Path.Replace('\\', '/'), PathComparer)
                .ToDictionary(g => g.Key, g => g.First(), PathComparer);

            var untrackedExtra = await ReadUntrackedPathsAsync(repoPath, cancellationToken).ConfigureAwait(false);

            var allPaths = new SortedSet<string>(PathComparer);
            foreach (var p in unmergedGroups.Keys)
                allPaths.Add(p);
            foreach (var p in changeMap.Keys)
                allPaths.Add(p);
            foreach (var p in untrackedExtra)
                allPaths.Add(p);

            var entries = new List<Models.ConflictBaselineEntry>(allPaths.Count);
            foreach (var rel in allPaths)
            {
                var hasUnmerged = unmergedGroups.TryGetValue(rel, out var stageSig);
                var changeFlag = changeMap.TryGetValue(rel, out var ch)
                    ? $"{(int)ch.IndexState}|{(int)ch.WorkTreeState}"
                    : "0|0";

                var (exists, len, sha256) = await HashWorktreeFileAsync(repoPath, rel, cancellationToken)
                    .ConfigureAwait(false);

                entries.Add(new Models.ConflictBaselineEntry
                {
                    RelativePath = rel,
                    IsUnmerged = hasUnmerged,
                    IndexStagesSignature = stageSig ?? string.Empty,
                    StagedWorkTreeFlags = changeFlag,
                    ExistsOnDisk = exists,
                    DiskLength = len,
                    DiskSha256 = sha256,
                });
            }

            return new LiveConflictSnapshot(
                headSha,
                markers,
                inProgress,
                markerPath,
                markerSha256,
                markerOid,
                unmergedSignature,
                stagedSnap.IndexFingerprint,
                unmergedGroups.Keys.OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList(),
                entries);
        }

        private static void CompareBaselineEntries(
            IReadOnlyList<Models.ConflictBaselineEntry> expected,
            IReadOnlyList<Models.ConflictBaselineEntry> live,
            List<string> reasons)
        {
            var expectedMap = expected.ToDictionary(e => e.RelativePath, PathComparer);
            var liveMap = live.ToDictionary(e => e.RelativePath, PathComparer);

            foreach (var kv in liveMap)
            {
                if (!expectedMap.TryGetValue(kv.Key, out var baseEntry))
                {
                    reasons.Add($"冲突后工作区出现新的外部修改或未跟踪文件：{kv.Key}");
                    continue;
                }

                if (!string.Equals(baseEntry.IndexStagesSignature, kv.Value.IndexStagesSignature, StringComparison.Ordinal) ||
                    !string.Equals(baseEntry.StagedWorkTreeFlags, kv.Value.StagedWorkTreeFlags, StringComparison.Ordinal))
                {
                    reasons.Add($"文件 {kv.Key} 的暂存或冲突状态在冲突后发生变化。");
                }
                else if (baseEntry.ExistsOnDisk != kv.Value.ExistsOnDisk ||
                         baseEntry.DiskLength != kv.Value.DiskLength ||
                         !string.Equals(baseEntry.DiskSha256, kv.Value.DiskSha256, StringComparison.Ordinal))
                {
                    reasons.Add($"文件 {kv.Key} 的磁盘内容在冲突后被外部修改，停止自动中止以防覆盖外部成果。");
                }
            }

            foreach (var kv in expectedMap)
            {
                if (!liveMap.ContainsKey(kv.Key))
                    reasons.Add($"原冲突基线中的文件 {kv.Key} 已被外部移除或解决。");
            }
        }

        private static async Task<List<UnmergedEntry>> ReadUnmergedEntriesAsync(
            string repoPath,
            CancellationToken cancellationToken)
        {
            var res = await Command.RunGitDirectAsync(
                repoPath,
                cancellationToken,
                "--no-pager",
                "-c",
                "core.quotepath=off",
                "ls-files",
                "-u",
                "-z").ConfigureAwait(false);

            var entries = new List<UnmergedEntry>();
            if (!res.IsSuccess || string.IsNullOrEmpty(res.StdOut))
                return entries;

            foreach (var rec in res.StdOut.Split('\0', StringSplitOptions.RemoveEmptyEntries))
            {
                var tabIdx = rec.IndexOf('\t');
                if (tabIdx <= 0)
                    continue;

                var meta = rec[..tabIdx].Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (meta.Length < 3)
                    continue;

                var path = rec[(tabIdx + 1)..].Replace('\\', '/');
                entries.Add(new UnmergedEntry(meta[0], meta[1], meta[2], path));
            }

            return entries;
        }

        private static async Task<List<string>> ReadUntrackedPathsAsync(
            string repoPath,
            CancellationToken cancellationToken)
        {
            var res = await Command.RunGitDirectAsync(
                repoPath,
                cancellationToken,
                "--no-pager",
                "-c",
                "core.quotepath=off",
                "ls-files",
                "--others",
                "--exclude-standard",
                "-z").ConfigureAwait(false);

            if (!res.IsSuccess || string.IsNullOrEmpty(res.StdOut))
                return [];

            return res.StdOut
                .Split('\0', StringSplitOptions.RemoveEmptyEntries)
                .Select(p => p.Replace('\\', '/'))
                .ToList();
        }

        private static async Task<(bool Exists, long Length, string Sha256)> HashWorktreeFileAsync(
            string repoPath,
            string relPath,
            CancellationToken cancellationToken)
        {
            try
            {
                var fullPath = Path.GetFullPath(Path.Combine(repoPath, relPath.Replace('/', Path.DirectorySeparatorChar)));
                var repoRoot = Path.GetFullPath(repoPath).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                if (!fullPath.StartsWith(repoRoot, PathComparison))
                    return (false, 0, string.Empty);

                if (!File.Exists(fullPath))
                    return (false, 0, string.Empty);

                var bytes = await File.ReadAllBytesAsync(fullPath, cancellationToken).ConfigureAwait(false);
                return (true, bytes.LongLength, Convert.ToHexString(SHA256.HashData(bytes)));
            }
            catch
            {
                return (false, 0, string.Empty);
            }
        }

        private static async Task<(string Sha256, string SingleOid)> ReadMarkerFileHashAndOidAsync(
            string markerPath,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(markerPath) || !File.Exists(markerPath))
                return (string.Empty, string.Empty);

            try
            {
                var bytes = await File.ReadAllBytesAsync(markerPath, cancellationToken).ConfigureAwait(false);
                var sha256 = Convert.ToHexString(SHA256.HashData(bytes));
                var lines = Encoding.UTF8.GetString(bytes)
                    .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                    .Select(l => l.Trim())
                    .ToList();

                var oid = lines.Count == 1 && IsHexOid(lines[0]) ? lines[0] : string.Empty;
                return (sha256, oid);
            }
            catch
            {
                return (string.Empty, string.Empty);
            }
        }

        private static List<string> DetectAllInProgressMarkers(string gitDir)
        {
            var markers = new List<string>();
            if (string.IsNullOrWhiteSpace(gitDir) || !Directory.Exists(gitDir))
                return markers;

            if (File.Exists(Path.Combine(gitDir, "MERGE_HEAD")))
                markers.Add("MERGE_HEAD");
            if (File.Exists(Path.Combine(gitDir, "REVERT_HEAD")))
                markers.Add("REVERT_HEAD");
            if (File.Exists(Path.Combine(gitDir, "CHERRY_PICK_HEAD")))
                markers.Add("CHERRY_PICK_HEAD");
            if (Directory.Exists(Path.Combine(gitDir, "rebase-merge")))
                markers.Add("rebase-merge");
            if (Directory.Exists(Path.Combine(gitDir, "rebase-apply")))
                markers.Add("rebase-apply");
            if (File.Exists(Path.Combine(gitDir, "BISECT_LOG")))
                markers.Add("BISECT_LOG");

            return markers;
        }

        private static Models.GitOperationRecord FindMatchingJournalRecord(
            IReadOnlyList<Models.GitOperationRecord> recent,
            string repoPath,
            Models.GitInProgressKind kind,
            string headSha,
            string markerTargetSha,
            DateTime? markerLastWriteUtc)
        {
            if (recent == null || recent.Count == 0 || string.IsNullOrEmpty(headSha))
                return null;

            Models.GitOperationKind? targetKind = kind switch
            {
                Models.GitInProgressKind.Merge => Models.GitOperationKind.Merge,
                Models.GitInProgressKind.Revert => Models.GitOperationKind.Revert,
                _ => null,
            };

            if (!targetKind.HasValue)
                return null;

            foreach (var rec in recent)
            {
                if (rec.Kind != targetKind.Value)
                    continue;
                if (!PathsEqual(rec.WorktreePath, repoPath))
                    continue;
                if (!string.Equals(rec.OriginalTipSha, headSha, StringComparison.Ordinal))
                    continue;
                if (!string.IsNullOrEmpty(markerTargetSha) &&
                    !string.IsNullOrEmpty(rec.ExpectedPartnerSha) &&
                    !string.Equals(rec.ExpectedPartnerSha, markerTargetSha, StringComparison.Ordinal))
                {
                    continue;
                }

                // If the on-disk MERGE_HEAD/REVERT_HEAD file was created or overwritten strictly
                // after GitNest persisted this record, it belongs to a new external CLI invocation
                // rather than the retained GitNest conflict.
                if (markerLastWriteUtc.HasValue && markerLastWriteUtc.Value > rec.UpdatedAtUtc)
                    continue;

                if (rec.Stage == Models.GitOperationStage.PendingVerification ||
                    rec.Stage == Models.GitOperationStage.Executing ||
                    rec.IsUnfinishedOnStartup)
                {
                    return rec;
                }
            }

            return null;
        }

        private static string BuildExternalHeadline(Models.GitInProgressKind kind, int conflictCount)
        {
            var op = kind switch
            {
                Models.GitInProgressKind.Merge => "合并（MERGE）",
                Models.GitInProgressKind.Rebase => "变基（REBASE）",
                Models.GitInProgressKind.CherryPick => "拣选（CHERRY-PICK）",
                Models.GitInProgressKind.Revert => "撤销提交（REVERT）",
                Models.GitInProgressKind.Am => "补丁应用（AM）",
                Models.GitInProgressKind.Bisect => "二分定位（BISECT）",
                _ => "冲突状态",
            };

            return conflictCount > 0
                ? $"外部 {op} 进行中，检测到 {conflictCount} 个冲突文件"
                : $"外部 {op} 正在进行中（非 GitNest 发起）";
        }

        private static string BuildGitNestRetainedHeadline(Models.GitInProgressKind kind, Models.GitOperationRecord rec)
        {
            var kindText = kind == Models.GitInProgressKind.Revert ? "撤销提交（REVERT）" : "合并（MERGE）";
            return $"保留中的 {kindText} 冲突/挂起现场（操作：{rec.Title}）";
        }

        private static async Task<Command.Result> RunGitMutationLoggedAsync(
            string repoPath,
            Models.ICommandLog log,
            CancellationToken cancellationToken,
            params string[] args)
        {
            if (log != null)
            {
                var printable = string.Join(" ", args.Select(a =>
                    a.Contains(' ') ? CommandArgumentHelper.Quote(a) : a));
                log.AppendLine($"$ git {printable}");
            }

            var res = await Command.RunGitDirectAsync(repoPath, MutationCommandTimeout, cancellationToken, args)
                .ConfigureAwait(false);

            if (log != null)
            {
                if (!string.IsNullOrEmpty(res.StdOut))
                    log.AppendLine(res.StdOut.TrimEnd());
                if (!string.IsNullOrEmpty(res.StdErr))
                    log.AppendLine(res.StdErr.TrimEnd());
            }

            return res;
        }

        private static async Task<string> ResolveCommitOidAsync(
            string repoPath,
            string refOrSha,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(refOrSha))
                return string.Empty;

            var res = await Command.RunGitDirectAsync(
                repoPath,
                cancellationToken,
                "rev-parse",
                "--verify",
                "--end-of-options",
                $"{refOrSha}^{{commit}}").ConfigureAwait(false);

            return res.IsSuccess ? res.StdOut.Trim() : string.Empty;
        }

        private static async Task<string> ReadHeadShaAsync(string repoPath, CancellationToken cancellationToken)
        {
            var res = await Command.RunGitDirectAsync(repoPath, cancellationToken, "rev-parse", "--verify", "-q", "HEAD")
                .ConfigureAwait(false);
            return res.IsSuccess ? res.StdOut.Trim() : string.Empty;
        }

        private static async Task<string> ReadHeadSymbolicRefAsync(string repoPath, CancellationToken cancellationToken)
        {
            var res = await Command.RunGitDirectAsync(repoPath, cancellationToken, "symbolic-ref", "--quiet", "HEAD")
                .ConfigureAwait(false);
            return res.IsSuccess ? res.StdOut.Trim() : string.Empty;
        }

        private static async Task<string> ReadCurrentBranchShortAsync(string repoPath, CancellationToken cancellationToken)
        {
            var res = await Command.RunGitDirectAsync(repoPath, cancellationToken, "symbolic-ref", "--short", "-q", "HEAD")
                .ConfigureAwait(false);
            return res.IsSuccess ? res.StdOut.Trim() : string.Empty;
        }

        private static bool IsValidBranchNameToken(string branchName)
        {
            if (string.IsNullOrWhiteSpace(branchName))
                return false;
            var trimmed = branchName.Trim();
            if (trimmed.StartsWith("-", StringComparison.Ordinal) ||
                trimmed.IndexOfAny(['\0', '\r', '\n']) >= 0)
            {
                return false;
            }
            return true;
        }

        private static bool IsHexOid(string text) => !string.IsNullOrEmpty(text) && REG_HEX_OID().IsMatch(text);

        private static string ComputeSha256Hex(string content) =>
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content ?? string.Empty)));

        private static string CombineDiag(Command.Result res)
        {
            if (res == null)
                return string.Empty;

            var parts = new List<string>(2);
            if (!string.IsNullOrWhiteSpace(res.StdOut))
                parts.Add(res.StdOut.TrimEnd());
            if (!string.IsNullOrWhiteSpace(res.StdErr))
                parts.Add(res.StdErr.TrimEnd());
            return Models.GitConfigSnapshot.RedactText(string.Join("\n", parts));
        }

        private static string FirstLineOrFallback(string text, string fallback)
        {
            if (string.IsNullOrWhiteSpace(text))
                return fallback;

            foreach (var line in text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
            {
                var trimmed = line.Trim();
                if (trimmed.StartsWith("fatal:", StringComparison.OrdinalIgnoreCase) ||
                    trimmed.StartsWith("error:", StringComparison.OrdinalIgnoreCase))
                    return trimmed;
            }

            foreach (var line in text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
                return line.Trim();

            return fallback;
        }

        private static bool ContainsAny(string text, params string[] needles)
        {
            if (string.IsNullOrEmpty(text))
                return false;
            foreach (var needle in needles)
            {
                if (text.Contains(needle, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        private static string ShortSha(string sha) =>
            string.IsNullOrWhiteSpace(sha) ? string.Empty : (sha.Length > 8 ? sha[..8] : sha);

        private static bool PathsEqual(string left, string right) =>
            string.Equals(NormalizePathKey(left), NormalizePathKey(right), PathComparison);

        private static string NormalizePathKey(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return string.Empty;
            try
            {
                return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)).Replace('\\', '/');
            }
            catch
            {
                return path.Trim().Replace('\\', '/').TrimEnd('/');
            }
        }

        private static StringComparer PathComparer =>
            OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

        private static StringComparison PathComparison =>
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

        private readonly record struct UnmergedEntry(string Mode, string Oid, string Stage, string Path);

        private sealed class MergeInnerOutcome
        {
            public bool Succeeded { get; init; }
            public Models.MergeRevertFailureKind FailureKind { get; init; }
            public string Summary { get; init; } = string.Empty;
            public string DiagnosticDetail { get; init; } = string.Empty;
            public string ResultHeadSha { get; init; } = string.Empty;
            public bool StayedOnTargetBranch { get; init; }
            public bool WasFastForward { get; init; }
            public bool ConflictAbortedCleanly { get; init; }
            public bool AutoAbortStoppedForExternalIntervention { get; init; }
            public IReadOnlyList<string> ConflictedPaths { get; init; } = [];
            public IReadOnlyList<string> InterventionReasons { get; init; } = [];
            public Models.ConflictOwnershipContext OwnershipContext { get; init; }
        }

        private sealed class RevertInnerOutcome
        {
            public bool Succeeded { get; init; }
            public Models.MergeRevertFailureKind FailureKind { get; init; }
            public string Summary { get; init; } = string.Empty;
            public string DiagnosticDetail { get; init; } = string.Empty;
            public string ResultHeadSha { get; init; } = string.Empty;
            public bool ConflictAbortedCleanly { get; init; }
            public bool AutoAbortStoppedForExternalIntervention { get; init; }
            public IReadOnlyList<string> ConflictedPaths { get; init; } = [];
            public IReadOnlyList<string> InterventionReasons { get; init; } = [];
            public Models.ConflictOwnershipContext OwnershipContext { get; init; }
        }

        private sealed class FailureDispositionResult
        {
            public Models.MergeRevertFailureKind FailureKind { get; init; }
            public string Summary { get; init; } = string.Empty;
            public string DiagnosticDetail { get; init; } = string.Empty;
            public string ResultHeadSha { get; init; } = string.Empty;
            public bool ConflictAbortedCleanly { get; init; }
            public bool AutoAbortStoppedForExternalIntervention { get; init; }
            public IReadOnlyList<string> ConflictedPaths { get; init; } = [];
            public IReadOnlyList<string> InterventionReasons { get; init; } = [];
            public Models.ConflictOwnershipContext OwnershipContext { get; init; }
            public OperationActionOutcome<string> ActionOutcome { get; init; }
        }
    }
}
