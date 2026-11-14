using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace SourceGit.Models
{
    public enum MergeSourceKind
    {
        LocalBranch = 0,
        RemoteBranch,
        Tag,
        Commit,
    }

    public enum MergeRelationKind
    {
        Unknown = 0,
        AlreadyUpToDate,
        FastForward,
        Diverged,
        UnrelatedHistories,
        InvalidTargetOrSource,
    }

    public enum MergeConflictPrediction
    {
        Clean = 0,
        HasConflicts,
        BlockedUnrelatedHistories,
        Unavailable,
    }

    public enum MergeRevertFailureKind
    {
        None = 0,
        InvalidRequest,
        PreconditionFailed,
        TargetBranchCheckedOutElsewhere,
        SnapshotStale,
        RepositoryLocked,
        MergeAlreadyUpToDate,
        MergeFastForwardRequired,
        MergeCommitNotSupported,
        ConflictAbortedCleanly,
        ConflictOwnershipExternalIntervention,
        AbortFailed,
        HookRejected,
        SigningFailed,
        GitError,
    }

    public enum ConflictOwnershipOrigin
    {
        None = 0,
        GitNestOwnedClean,
        GitNestOwnedWithExternalIntervention,
        ExternalOrUnknown,
    }

    public sealed class MergePreflight
    {
        public bool IsReady { get; init; }
        public string RepoPath { get; init; } = string.Empty;
        public string CanonicalCommonDir { get; init; } = string.Empty;
        public string CurrentBranchName { get; init; } = string.Empty;
        public bool IsDetachedHead { get; init; }
        public string HeadSha { get; init; } = string.Empty;

        public MergeSourceKind SourceKind { get; init; }
        public string SourceDisplayName { get; init; } = string.Empty;
        public string SourceRefOrSha { get; init; } = string.Empty;
        public string SourceFullRef { get; init; } = string.Empty;
        public string SourceResolvedSha { get; init; } = string.Empty;

        public string TargetBranchName { get; init; } = string.Empty;
        public string TargetFullRef { get; init; } = string.Empty;
        public string TargetResolvedSha { get; init; } = string.Empty;
        public bool NeedsCheckoutTargetBranch { get; init; }
        public string TargetCheckedOutWorktreePath { get; init; } = string.Empty;

        public string DirectionSummary =>
            $"将【{SourceDisplayName}】的改动合并到【{TargetBranchName}】";

        public GitInProgressKind InProgressKind { get; init; }
        public bool HasDirtyWorkingTree { get; init; }
        public bool HasStagedChanges { get; init; }
        public bool HasUntrackedFiles { get; init; }
        public bool HasConflicts { get; init; }
        public string BlockingReason { get; init; } = string.Empty;

        public MergeRelationKind Relation { get; init; }
        public MergeConflictPrediction ConflictPrediction { get; init; }
        public IReadOnlyList<string> PredictedConflictPaths { get; init; } = [];
        public string ConflictPredictionDetail { get; init; } = string.Empty;

        public IReadOnlyList<Commit> IncomingCommits { get; init; } = [];
        public IReadOnlyList<Change> IncomingChanges { get; init; } = [];

        public OperationConfirmationSnapshot Snapshot { get; init; }
    }

    public sealed class MergeExecutionOptions
    {
        public MergeSourceKind SourceKind { get; init; }
        public string SourceRefOrSha { get; init; } = string.Empty;
        public string SourceDisplayName { get; init; } = string.Empty;
        public string TargetBranchName { get; init; } = string.Empty;
        public MergeMode Mode { get; init; } = MergeMode.Default;
        public bool EditCommitMessage { get; init; }
        public MergePreflight Preflight { get; init; }

        /// <summary>
        /// Optional hook executed immediately after GitNest records its post-conflict ownership baseline
        /// and before ownership verification + auto-abort run. Intended for deterministic integration verification.
        /// </summary>
        internal Func<ConflictOwnershipContext, Task> BeforeAutoAbortAsync { get; init; }
    }

    public sealed class MergeExecutionResult
    {
        public bool Succeeded { get; init; }
        public bool BlockedBySnapshot { get; init; }
        public bool BlockedByLock { get; init; }
        public MergeRevertFailureKind FailureKind { get; init; }
        public string OperationId { get; init; } = string.Empty;
        public string Summary { get; init; } = string.Empty;
        public string DiagnosticDetail { get; init; } = string.Empty;

        public string SourceDisplayName { get; init; } = string.Empty;
        public string TargetBranchName { get; init; } = string.Empty;
        public string OriginalTargetSha { get; init; } = string.Empty;
        public string ResultHeadSha { get; init; } = string.Empty;
        public bool StayedOnTargetBranch { get; init; }
        public bool WasFastForward { get; init; }

        public bool ConflictAbortedCleanly { get; init; }
        public bool AutoAbortStoppedForExternalIntervention { get; init; }
        public IReadOnlyList<string> ConflictedPaths { get; init; } = [];
        public IReadOnlyList<string> InterventionReasons { get; init; } = [];
        public ConflictOwnershipContext OwnershipContext { get; init; }
        public OperationExecutionResult<string> ExecutionResult { get; init; }
    }

    public sealed class RevertPreflight
    {
        public bool IsReady { get; init; }
        public string RepoPath { get; init; } = string.Empty;
        public string CanonicalCommonDir { get; init; } = string.Empty;
        public string CurrentBranchName { get; init; } = string.Empty;
        public string CurrentBranchFullRef { get; init; } = string.Empty;
        public bool IsDetachedHead { get; init; }
        public string HeadSha { get; init; } = string.Empty;

        public Commit TargetCommit { get; init; }
        public int ParentCount { get; init; }
        public bool IsMergeCommit { get; init; }
        public IReadOnlyList<Change> AffectedChanges { get; init; } = [];

        public GitInProgressKind InProgressKind { get; init; }
        public bool HasDirtyWorkingTree { get; init; }
        public bool HasStagedChanges { get; init; }
        public bool HasUntrackedFiles { get; init; }
        public bool HasConflicts { get; init; }
        public string BlockingReason { get; init; } = string.Empty;

        public OperationConfirmationSnapshot Snapshot { get; init; }
    }

    public sealed class RevertExecutionOptions
    {
        public string CommitSha { get; init; } = string.Empty;
        public bool AutoCommit { get; init; } = true;
        public RevertPreflight Preflight { get; init; }

        internal Func<ConflictOwnershipContext, Task> BeforeAutoAbortAsync { get; init; }
    }

    public sealed class RevertExecutionResult
    {
        public bool Succeeded { get; init; }
        public bool BlockedBySnapshot { get; init; }
        public bool BlockedByLock { get; init; }
        public MergeRevertFailureKind FailureKind { get; init; }
        public string OperationId { get; init; } = string.Empty;
        public string Summary { get; init; } = string.Empty;
        public string DiagnosticDetail { get; init; } = string.Empty;

        public string TargetCommitSha { get; init; } = string.Empty;
        public string OriginalHeadSha { get; init; } = string.Empty;
        public string ResultHeadSha { get; init; } = string.Empty;

        public bool ConflictAbortedCleanly { get; init; }
        public bool AutoAbortStoppedForExternalIntervention { get; init; }
        public IReadOnlyList<string> ConflictedPaths { get; init; } = [];
        public IReadOnlyList<string> InterventionReasons { get; init; } = [];
        public ConflictOwnershipContext OwnershipContext { get; init; }
        public OperationExecutionResult<string> ExecutionResult { get; init; }
    }

    public sealed class ConflictBaselineEntry
    {
        public string RelativePath { get; init; } = string.Empty;
        public bool IsUnmerged { get; init; }
        public string IndexStagesSignature { get; init; } = string.Empty;
        public string StagedWorkTreeFlags { get; init; } = string.Empty;
        public bool ExistsOnDisk { get; init; }
        public long DiskLength { get; init; }
        public string DiskSha256 { get; init; } = string.Empty;
    }

    public sealed class ConflictOwnershipContext
    {
        public string OperationId { get; init; } = string.Empty;
        public GitOperationKind OperationKind { get; init; }
        public string RepoPath { get; init; } = string.Empty;
        public string CanonicalGitDir { get; init; } = string.Empty;
        public string CanonicalCommonDir { get; init; } = string.Empty;
        public string TargetBranchName { get; init; } = string.Empty;
        public string ExpectedHeadSha { get; init; } = string.Empty;
        public string ExpectedPartnerSha { get; init; } = string.Empty;
        public string MarkerFilePath { get; init; } = string.Empty;
        public string MarkerFileSha256 { get; init; } = string.Empty;
        public IReadOnlyList<string> ActiveMarkers { get; init; } = [];
        public string StagedFingerprint { get; init; } = string.Empty;
        public string UnmergedEntriesSignature { get; init; } = string.Empty;
        public IReadOnlyList<string> ConflictedPaths { get; init; } = [];
        public IReadOnlyList<ConflictBaselineEntry> Entries { get; init; } = [];
    }

    public sealed class ConflictOwnershipVerification
    {
        public bool CanSafelyAutoAbort { get; init; }
        public ConflictOwnershipOrigin Origin { get; init; }
        public IReadOnlyList<string> Reasons { get; init; } = [];
        public IReadOnlyList<string> ConflictedPaths { get; init; } = [];
    }

    public sealed class RepositoryInProgressInspection
    {
        public GitInProgressKind InProgressKind { get; init; }
        public ConflictOwnershipOrigin Origin { get; init; }
        public string OriginLabel { get; init; } = string.Empty;
        public string MarkerTargetSha { get; init; } = string.Empty;
        public IReadOnlyList<string> ActiveMarkers { get; init; } = [];
        public IReadOnlyList<string> ConflictedPaths { get; init; } = [];
        public GitOperationRecord UnfinishedOperation { get; init; }
        public string Headline { get; init; } = string.Empty;
        public string Detail { get; init; } = string.Empty;
        public bool CanOfferSafeAbort { get; init; }
    }
}
