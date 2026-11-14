using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace SourceGit.Models
{
    public enum WorktreeDeleteStepKind
    {
        CloseOpenViews = 0,
        DetachedBackupCreate,
        WorktreeRemove,
        LocalBackupCreate,
        LocalBranchDelete,
        RemoteBranchDelete,
        TrackingCachePrune,
    }

    public sealed class WorktreeDeleteStepResult
    {
        public WorktreeDeleteStepKind Step { get; init; }
        public bool Succeeded { get; init; }
        public bool Skipped { get; init; }
        public string Label { get; init; } = string.Empty;
        public string Detail { get; init; } = string.Empty;
    }

    public sealed class WorktreeInspectPreflight
    {
        public string RepoPath { get; init; } = string.Empty;
        public string MainRepoPath { get; init; } = string.Empty;
        public string CanonicalCommonDir { get; init; } = string.Empty;
        public string TargetWorktreePath { get; init; } = string.Empty;
        public string TargetGitDir { get; init; } = string.Empty;
        public string WorktreeName { get; init; } = string.Empty;

        public bool ExistsInRegistry { get; init; }
        public bool IsMainWorktree { get; init; }
        public bool IsBare { get; init; }
        public bool IdentityValid { get; init; }
        public bool IsDirectoryMissing { get; init; }

        public bool IsLocked { get; init; }
        public string LockReason { get; init; } = string.Empty;
        public string PrunableReason { get; init; } = string.Empty;

        public bool IsDetached { get; init; }
        public string BranchName { get; init; } = string.Empty;
        public string FullBranchRef { get; init; } = string.Empty;
        public string HeadSha { get; init; } = string.Empty;

        public WorktreeSyncState SyncState { get; init; } = WorktreeSyncState.Unknown;
        public string SyncUpstream { get; init; } = string.Empty;
        public int? SyncAhead { get; init; }
        public int? SyncBehind { get; init; }
        public DateTime? LastFetchAt { get; init; }

        public IReadOnlyList<string> StagedFiles { get; init; } = [];
        public IReadOnlyList<string> UnstagedFiles { get; init; } = [];
        public IReadOnlyList<string> ConflictedFiles { get; init; } = [];
        public IReadOnlyList<string> UntrackedFiles { get; init; } = [];
        public IReadOnlyList<string> IgnoredFiles { get; init; } = [];
        public IReadOnlyList<string> NestedRepoOrSubmodulePaths { get; init; } = [];

        public bool HasDirtyOrUntracked =>
            StagedFiles.Count > 0 || UnstagedFiles.Count > 0 || ConflictedFiles.Count > 0 || UntrackedFiles.Count > 0;
        public bool HasIgnoredFiles => IgnoredFiles.Count > 0;
        public bool HasNestedRepoOrSubmodule => NestedRepoOrSubmodulePaths.Count > 0;
        public int TotalBlockingFiles =>
            StagedFiles.Count + UnstagedFiles.Count + ConflictedFiles.Count + UntrackedFiles.Count + IgnoredFiles.Count;
        public bool RequiresForceToDeleteFiles => HasDirtyOrUntracked || HasIgnoredFiles;
        public bool RequiresDetachedBackup =>
            !IsMainWorktree && !IsBare && IdentityValid && !IsDirectoryMissing && IsDetached && !string.IsNullOrEmpty(HeadSha);

        public bool CanDeleteWithoutForce =>
            ExistsInRegistry &&
            !IsMainWorktree &&
            !IsBare &&
            !IsDirectoryMissing &&
            IdentityValid &&
            !IsLocked &&
            !HasNestedRepoOrSubmodule &&
            !RequiresForceToDeleteFiles;

        public bool CanForceDeleteWorktree =>
            ExistsInRegistry &&
            !IsMainWorktree &&
            !IsBare &&
            !IsDirectoryMissing &&
            IdentityValid &&
            !IsLocked &&
            !HasNestedRepoOrSubmodule;

        public BranchDeletePreflight AttachedBranchPreflight { get; init; }
        public OperationConfirmationSnapshot ConfirmationSnapshot { get; init; }
        public string StateDigest { get; init; } = string.Empty;
        public string DirectBlockReason { get; init; } = string.Empty;
    }

    public sealed class WorktreeDeleteOptions
    {
        public WorktreeInspectPreflight Preflight { get; init; }
        public bool ForceRemoveWorktree { get; init; }
        public bool DeleteLocalBranch { get; init; }
        public bool ForceDeleteLocalBranch { get; init; }
        public bool DeleteLinkedUpstreamRemote { get; init; }
        public bool PruneStaleTrackingCacheWhenRemoteAbsent { get; init; }
        public string ConfirmedExpectedRemoteTipSha { get; init; }
        public bool? ConfirmedRemoteExistsOnServer { get; init; }
        public Func<string, string, CancellationToken, Task> BeforeWorktreeRemoveAsync { get; init; }
    }

    public sealed class WorktreeDeleteOutcome
    {
        public string OperationId { get; init; } = string.Empty;
        public string MainRepoPath { get; init; } = string.Empty;
        public string TargetWorktreePath { get; init; } = string.Empty;
        public string BranchName { get; init; } = string.Empty;
        public bool IsDetached { get; init; }

        public bool Succeeded { get; init; }
        public bool IsPartialSuccess { get; init; }
        public bool BlockedByProtection { get; init; }
        public bool BlockedByLock { get; init; }
        public bool BlockedByNestedRepoOrSubmodule { get; init; }
        public bool BlockedByMissingDirectory { get; init; }
        public bool BlockedBySnapshot { get; init; }
        public bool RequiresForceWorktreeConfirmation { get; init; }
        public bool RequiresForceLocalBranchConfirmation { get; init; }
        public bool RequiresRemoteCachePruneConfirmation { get; init; }
        public bool RejectedByRemoteLease { get; init; }
        public bool CollaboratorCommitsPreserved { get; init; }

        public bool WorktreeRemoved { get; init; }
        public bool LocalBranchDeleted { get; init; }
        public bool RemoteBranchDeleted { get; init; }
        public bool LocalTrackingCachePruned { get; init; }

        public string DetachedBackupRefName { get; init; } = string.Empty;
        public string DetachedBackupSha { get; init; } = string.Empty;
        public string LocalBranchBackupRefName { get; init; } = string.Empty;
        public string LocalBranchBackupSha { get; init; } = string.Empty;
        public string RemoteName { get; init; } = string.Empty;
        public string RemoteBranchName { get; init; } = string.Empty;
        public string ExpectedRemoteTipSha { get; init; } = string.Empty;

        public int UnmergedCommitCount { get; init; }
        public int UnmergedChangedFilesCount { get; init; }
        public IReadOnlyList<UnmergedCommitSummary> UnmergedCommits { get; init; } = [];
        public WorktreeInspectPreflight PreflightAfterFailure { get; init; }

        public string Summary { get; init; } = string.Empty;
        public string DetailMessage { get; init; } = string.Empty;
        public IReadOnlyList<WorktreeDeleteStepResult> StepResults { get; init; } = [];
        public GitOperationRecord OperationRecord { get; init; }
    }

    public sealed class WorktreeUnlockResult
    {
        public bool Succeeded { get; init; }
        public string Summary { get; init; } = string.Empty;
        public WorktreeInspectPreflight Preflight { get; init; }
    }

    public sealed class WorktreePruneCandidate
    {
        public string WorktreeName { get; init; } = string.Empty;
        public string WorktreePath { get; init; } = string.Empty;
        public string AdminDir { get; init; } = string.Empty;
        public string Reason { get; init; } = string.Empty;
        public string SummaryLine { get; init; } = string.Empty;
    }

    public sealed class WorktreeLockedMissingItem
    {
        public string WorktreePath { get; init; } = string.Empty;
        public string LockReason { get; init; } = string.Empty;
        public string PrunableReason { get; init; } = string.Empty;
    }

    public sealed class WorktreePrunePreview
    {
        public string MainRepoPath { get; init; } = string.Empty;
        public string CanonicalCommonDir { get; init; } = string.Empty;
        public IReadOnlyList<WorktreePruneCandidate> Candidates { get; init; } = [];
        public IReadOnlyList<WorktreeLockedMissingItem> LockedRetainedItems { get; init; } = [];
        public bool HasCandidates => Candidates.Count > 0;
        public string RawDryRunOutput { get; init; } = string.Empty;
        public string ConfirmationNote { get; init; } = string.Empty;
    }

    public sealed class WorktreePruneResult
    {
        public bool Succeeded { get; init; }
        public int RemovedCount { get; init; }
        public IReadOnlyList<WorktreePruneCandidate> RemovedCandidates { get; init; } = [];
        public string Summary { get; init; } = string.Empty;
        public string DetailMessage { get; init; } = string.Empty;
    }
}
