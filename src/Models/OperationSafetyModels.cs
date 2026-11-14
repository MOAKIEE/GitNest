using System;
using System.Collections.Generic;

namespace SourceGit.Models
{
    public enum GitInProgressKind
    {
        None = 0,
        Merge,
        Rebase,
        CherryPick,
        Revert,
        Am,
        Bisect,
    }

    public sealed class OperationTargetFileSnapshot
    {
        public string RelativePath { get; init; } = string.Empty;
        public bool ExistsOnDisk { get; init; }
        public long DiskLength { get; init; }
        public string DiskSha256 { get; init; } = string.Empty;
        public bool ExistsInIndex { get; init; }
        public string IndexSha1 { get; init; } = string.Empty;
        public bool ExistsInHead { get; init; }
        public string HeadSha1 { get; init; } = string.Empty;
    }

    public sealed class OperationConfirmationSnapshot
    {
        public DateTime CapturedAtUtc { get; init; } = DateTime.UtcNow;
        public string RepoPath { get; init; } = string.Empty;
        public string CanonicalGitDir { get; init; } = string.Empty;
        public string CanonicalCommonDir { get; init; } = string.Empty;
        public string HeadSha { get; init; } = string.Empty;
        public string HeadRefName { get; init; } = string.Empty;
        public bool IsDetachedHead { get; init; }
        public GitInProgressKind InProgressKind { get; init; }
        public string StagedFingerprint { get; init; } = string.Empty;
        public bool HasStagedChanges { get; init; }
        public bool HasUnstagedChanges { get; init; }
        public bool HasUntrackedFiles { get; init; }
        public bool HasConflicts { get; init; }
        public bool IsWorkingTreeClean => !HasStagedChanges && !HasUnstagedChanges && !HasUntrackedFiles && !HasConflicts;
        public Dictionary<string, string> WatchedRefs { get; init; } = new(StringComparer.Ordinal);
        public IReadOnlyList<OperationTargetFileSnapshot> TargetFiles { get; init; } = [];
    }

    public sealed class OperationSnapshotCaptureOptions
    {
        public IEnumerable<string> RefsToWatch { get; init; }
        public IEnumerable<string> TargetPaths { get; init; }
        public bool IncludeTargetFileDetails { get; init; }
    }

    public sealed class OperationSnapshotVerifyOptions
    {
        public bool RequireSameRepoIdentity { get; init; } = true;
        public bool RequireSameHead { get; init; } = true;
        public bool RequireSameInProgressState { get; init; } = true;
        public bool RequireCleanWorkingTree { get; init; }
        public bool RequireSameStagedFingerprint { get; init; }
        public bool RequireSameWatchedRefs { get; init; } = true;
        public bool RequireSameTargetFiles { get; init; }
        public bool CheckGitLocks { get; init; } = true;
        public bool CheckIndexLock { get; init; } = true;
        public string BranchLockToCheck { get; init; }
    }

    public enum OperationSnapshotMismatchReason
    {
        RepoNotAccessible = 0,
        RepoIdentityChanged,
        HeadChanged,
        InProgressStateChanged,
        WorkingTreeNoLongerClean,
        StagedSnapshotChanged,
        WatchedRefChanged,
        TargetFileChanged,
        GitLockPresent,
    }

    public sealed class OperationSnapshotVerificationResult
    {
        public bool IsValid { get; init; }
        public IReadOnlyList<OperationSnapshotMismatchReason> Reasons { get; init; } = [];
        public string Summary { get; init; } = string.Empty;
        public IReadOnlyList<string> Details { get; init; } = [];
        public OperationConfirmationSnapshot CurrentSnapshot { get; init; }
        public GitLockDetectionResult LockDetection { get; init; } = new();
    }

    public sealed class GitBackupRefInfo
    {
        public string FullRefName { get; init; } = string.Empty;
        public string OperationId { get; init; } = string.Empty;
        public string CommitSha { get; init; } = string.Empty;
        public DateTime CreatedAtUtc { get; init; }
        public string Subject { get; init; } = string.Empty;
        public double AgeDays { get; init; }
        public bool IsExpired { get; init; }
    }

    public sealed class GitBackupCreateResult
    {
        public bool Succeeded { get; init; }
        public string BackupRefName { get; init; } = string.Empty;
        public string CommitSha { get; init; } = string.Empty;
        public string Summary { get; init; } = string.Empty;
    }

    public sealed class GitBackupRestoreResult
    {
        public bool Succeeded { get; init; }
        public bool BlockedByExistingBranch { get; init; }
        public bool BlockedByLock { get; init; }
        public string BranchName { get; init; } = string.Empty;
        public string BackupRefName { get; init; } = string.Empty;
        public string RestoredCommitSha { get; init; } = string.Empty;
        public string Summary { get; init; } = string.Empty;
    }

    public sealed class GitBackupPrunePreview
    {
        public int RetentionDays { get; init; } = 30;
        public IReadOnlyList<GitBackupRefInfo> ExpiredBackups { get; init; } = [];
        public IReadOnlyList<GitBackupRefInfo> RetainedBackups { get; init; } = [];
        public string Summary { get; init; } = string.Empty;
    }

    public sealed class GitBackupPruneResult
    {
        public bool Succeeded { get; init; }
        public IReadOnlyList<string> DeletedRefs { get; init; } = [];
        public IReadOnlyList<string> FailedRefs { get; init; } = [];
        public string Summary { get; init; } = string.Empty;
    }

    public enum GitOperationKind
    {
        Commit = 0,
        CommitAndPush,
        Fetch,
        Pull,
        Push,
        DeleteBranch,
        DeleteRemoteBranch,
        RemoveWorktree,
        Merge,
        Revert,
        Reset,
        RewriteRemoteHistory,
        Stash,
        Discard,
        RestoreBackup,
        PruneBackups,
        SwitchBranch,
    }

    public enum GitOperationStage
    {
        Preparing = 0,
        AwaitingConfirmation,
        Executing,
        Succeeded,
        Failed,
        PartialSuccess,
        PendingVerification,
        CanceledUncertain,
    }

    public sealed class GitOperationRecord
    {
        public string OperationId { get; set; } = string.Empty;
        public string RepositoryCommonDir { get; set; } = string.Empty;
        public string WorktreePath { get; set; } = string.Empty;
        public GitOperationKind Kind { get; set; }
        public string Title { get; set; } = string.Empty;
        public string BranchName { get; set; } = string.Empty;
        public string RemoteName { get; set; } = string.Empty;
        public string RemoteBranchName { get; set; } = string.Empty;
        public string OriginalTipSha { get; set; } = string.Empty;
        public string TargetTipSha { get; set; } = string.Empty;
        public string ExpectedPartnerSha { get; set; } = string.Empty;
        public string ExpectedRemoteTipSha { get; set; } = string.Empty;
        public string BackupRefName { get; set; } = string.Empty;
        public GitOperationStage Stage { get; set; }
        public string StepSummary { get; set; } = string.Empty;
        public string CommandSummary { get; set; } = string.Empty;
        public string DiagnosticDetail { get; set; } = string.Empty;
        public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
        public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
        public DateTime? CompletedAtUtc { get; set; }
        public bool IsUnfinishedOnStartup { get; set; }
        public bool IsAcknowledged { get; set; }
        public string ReconciliationNote { get; set; } = string.Empty;
    }

    public sealed class OperationExecutionResult<T>
    {
        public bool Succeeded { get; init; }
        public bool BlockedBySnapshot { get; init; }
        public bool BlockedByLock { get; init; }
        public GitOperationRecord Record { get; init; }
        public OperationSnapshotVerificationResult VerificationFailure { get; init; }
        public GitLockDetectionResult LockDetection { get; init; } = new();
        public T Payload { get; init; }
        public string Summary { get; init; } = string.Empty;
    }

    public sealed class OperationReconciliationReport
    {
        public string RepositoryCommonDir { get; init; } = string.Empty;
        public string CurrentHeadSha { get; init; } = string.Empty;
        public string CurrentBranchName { get; init; } = string.Empty;
        public GitInProgressKind CurrentInProgressKind { get; init; }
        public IReadOnlyList<GitOperationRecord> UnfinishedRecords { get; init; } = [];
        public IReadOnlyList<string> ActiveBackupRefs { get; init; } = [];
        public bool HasPendingAttention => UnfinishedRecords.Count > 0;
        public string Summary { get; init; } = string.Empty;
    }
}
