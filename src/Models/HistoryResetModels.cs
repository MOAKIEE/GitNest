using System;
using System.Collections.Generic;

namespace SourceGit.Models
{
    public enum ResetCommitMode
    {
        KeepCodeMixed = 0,
        DiscardCodeHard = 1,
    }

    public sealed class BranchProtectionStatus
    {
        public string BranchName { get; init; } = string.Empty;
        public bool IsDefaultProtectedName { get; init; }
        public bool IsRemoteDefaultBranch { get; init; }
        public IReadOnlyList<string> DefaultBranchRemotes { get; init; } = [];
        public bool IsProtectedByDefault => IsDefaultProtectedName || IsRemoteDefaultBranch;
        public bool IsLocallyUnlocked { get; init; }
        public bool IsEffectivelyProtected => IsProtectedByDefault && !IsLocallyUnlocked;
        public IReadOnlyList<string> Reasons { get; init; } = [];
        public string Summary { get; init; } = string.Empty;
    }

    public sealed class ResetCommitSummary
    {
        public string Sha { get; init; } = string.Empty;
        public string ShortSha { get; init; } = string.Empty;
        public string Subject { get; init; } = string.Empty;
        public string AuthorName { get; init; } = string.Empty;
        public string AuthorEmail { get; init; } = string.Empty;
        public DateTimeOffset? AuthoredAt { get; init; }
        public bool IsFirstParent { get; init; }
        public bool IsMergeCommit { get; init; }
    }

    public sealed class ResetAuthorAttribution
    {
        public string Name { get; init; } = string.Empty;
        public string Email { get; init; } = string.Empty;
        public int CommitCount { get; init; }
        public string DisplayText => string.IsNullOrEmpty(Email)
            ? $"{Name} ({CommitCount})"
            : $"{Name} <{Email}> ({CommitCount})";
    }

    public sealed class ResetUpstreamRemoteStatus
    {
        public string RemoteName { get; init; } = string.Empty;
        public string RemoteBranchName { get; init; } = string.Empty;
        public string TrackingRefName { get; init; } = string.Empty;
        public bool ConfiguredUpstream { get; init; }
        public bool IsPushTarget { get; init; }
        public string RedactedRemoteUrl { get; init; } = string.Empty;
        public string LocalTrackingTipSha { get; init; } = string.Empty;
        public bool? ExistsOnRemote { get; init; }
        public bool RemoteQuerySucceeded { get; init; }
        public bool RemoteStateUnknown { get; init; }
        public string RemoteQueryError { get; init; } = string.Empty;
        public string LiveRemoteTipSha { get; init; } = string.Empty;
        public bool RemoteTipExistsLocally { get; init; }
        public bool RemoteMatchesLocalHead { get; init; }
        public bool LocalIsAheadOfRemote { get; init; }
        public int UnpushedCommitCount { get; init; }
        public bool RemoteIncludesTarget { get; init; }
        public bool RemoteDivergedWithUnintegratedCommits { get; init; }
        public string RemoteStatusLabel { get; init; } = string.Empty;
        public string Detail { get; init; } = string.Empty;
    }

    public sealed class HistoryResetPreflight
    {
        public string RepoPath { get; init; } = string.Empty;
        public string CanonicalGitDir { get; init; } = string.Empty;
        public string CanonicalCommonDir { get; init; } = string.Empty;
        public string LocalBranchName { get; init; } = string.Empty;
        public string LocalBranchFullRef { get; init; } = string.Empty;
        public string CurrentHeadSha { get; init; } = string.Empty;
        public string TargetCommitSha { get; init; } = string.Empty;
        public string TargetCommitSubject { get; init; } = string.Empty;
        public bool IsUnbornHead { get; init; }
        public bool IsDetachedHead { get; init; }
        public bool HeadIsRootCommit { get; init; }
        public bool IsRootCommitTarget { get; init; }
        public bool IsSameAsHead { get; init; }
        public bool IsOnFirstParentChain { get; init; }
        public GitInProgressKind InProgressKind { get; init; }
        public bool HasStagedChanges { get; init; }
        public bool HasUnstagedChanges { get; init; }
        public bool HasUntrackedFiles { get; init; }
        public bool HasConflicts { get; init; }
        public bool IsCleanWorkingTreeAndIndex => !HasStagedChanges && !HasUnstagedChanges && !HasUntrackedFiles && !HasConflicts;
        public IReadOnlyList<string> CollisionPaths { get; init; } = [];
        public bool HasUntrackedOrIgnoredCollisions => CollisionPaths.Count > 0;
        public bool CanResetLocally { get; init; }
        public string BlockReason { get; init; } = string.Empty;
        public BranchProtectionStatus Protection { get; init; } = new();
        public IReadOnlyList<ResetCommitSummary> LocalCommitsToRemove { get; init; } = [];
        public int FirstParentCommitsToRemoveCount { get; init; }
        public int MergedSideCommitsToRemoveCount { get; init; }
        public IReadOnlyList<ResetAuthorAttribution> LocalAuthors { get; init; } = [];
        public bool HasOtherAuthorsInLocal { get; init; }
        public string CurrentUserName { get; init; } = string.Empty;
        public string CurrentUserEmail { get; init; } = string.Empty;
        public ResetUpstreamRemoteStatus ConfiguredUpstream { get; init; }
        public IReadOnlyList<ResetUpstreamRemoteStatus> AdditionalTrackedRemotes { get; init; } = [];
        public OperationConfirmationSnapshot Snapshot { get; init; }
        public string Summary { get; init; } = string.Empty;
    }

    public sealed class RemoteRewritePreflight
    {
        [System.Text.Json.Serialization.JsonIgnore]
        public string PushUrl { get; init; } = string.Empty;
        public string RepoPath { get; init; } = string.Empty;
        public string CanonicalGitDir { get; init; } = string.Empty;
        public string CanonicalCommonDir { get; init; } = string.Empty;
        public string LocalBranchName { get; init; } = string.Empty;
        public string LocalBranchFullRef { get; init; } = string.Empty;
        public string LocalTipSha { get; init; } = string.Empty;
        public string TargetTipSha { get; init; } = string.Empty;
        public string TargetCommitSubject { get; init; } = string.Empty;
        public string RemoteName { get; init; } = string.Empty;
        public string RemoteBranchName { get; init; } = string.Empty;
        public string TrackingRefName { get; init; } = string.Empty;
        public string LocalTrackingTipSha { get; init; } = string.Empty;
        public string RedactedRemoteUrl { get; init; } = string.Empty;
        public bool QuerySucceeded { get; init; }
        public bool RemoteStateUnknown { get; init; }
        public string RemoteQueryError { get; init; } = string.Empty;
        public bool ExistsOnServer { get; init; }
        public string LiveRemoteTipSha { get; init; } = string.Empty;
        public bool RemoteTipExistsLocally { get; init; }
        public bool RemoteAlreadyMatchesTarget { get; init; }
        public bool RemoteIsStrictAncestorOfTarget { get; init; }
        public bool TargetIsAncestorOfRemote { get; init; }
        public bool RemoteDivergedWithUnintegratedCommits { get; init; }
        public BranchProtectionStatus LocalBranchProtection { get; init; } = new();
        public BranchProtectionStatus RemoteBranchProtection { get; init; } = new();
        public bool BlockedByProtection =>
            (LocalBranchProtection?.IsEffectivelyProtected ?? false) ||
            (RemoteBranchProtection?.IsEffectivelyProtected ?? false);
        public IReadOnlyList<ResetCommitSummary> CommitsToRemoveOnRemote { get; init; } = [];
        public IReadOnlyList<ResetAuthorAttribution> RemoteAuthors { get; init; } = [];
        public bool HasOtherAuthorsOnRemote { get; init; }
        public bool CanRewriteRemote { get; init; }
        public string BlockReason { get; init; } = string.Empty;
        public OperationConfirmationSnapshot Snapshot { get; init; }
        public string Summary { get; init; } = string.Empty;
    }

    public sealed class ResetWithoutCheckoutPreflight
    {
        public string RepoPath { get; init; } = string.Empty;
        public string CanonicalGitDir { get; init; } = string.Empty;
        public string CanonicalCommonDir { get; init; } = string.Empty;
        public string BranchName { get; init; } = string.Empty;
        public string BranchFullRef { get; init; } = string.Empty;
        public string CurrentBranchTipSha { get; init; } = string.Empty;
        public string TargetCommitSha { get; init; } = string.Empty;
        public string TargetCommitSubject { get; init; } = string.Empty;
        public bool IsCurrentBranch { get; init; }
        public bool IsCheckedOutInOtherWorktree { get; init; }
        public string CheckedOutWorktreePath { get; init; } = string.Empty;
        public bool IsSameTip { get; init; }
        public bool IsFastForward { get; init; }
        public bool TargetIsInFirstParentChain { get; init; }
        public BranchProtectionStatus Protection { get; init; } = new();
        public bool BlockedByProtection => !IsFastForward && (Protection?.IsEffectivelyProtected ?? false);
        public IReadOnlyList<ResetCommitSummary> CommitsToRemove { get; init; } = [];
        public IReadOnlyList<ResetAuthorAttribution> Authors { get; init; } = [];
        public bool CanExecute { get; init; }
        public string BlockReason { get; init; } = string.Empty;
        public OperationConfirmationSnapshot Snapshot { get; init; }
        public string Summary { get; init; } = string.Empty;
    }

    public sealed class HistoryResetOptions
    {
        public ResetCommitMode Mode { get; init; } = ResetCommitMode.KeepCodeMixed;
        public HistoryResetPreflight Preflight { get; init; }
        public bool AlsoRewriteRemote { get; init; }
        public RemoteRewritePreflight RemoteRewritePreflight { get; init; }
    }

    public sealed class RemoteRewriteOptions
    {
        public RemoteRewritePreflight Preflight { get; init; }
    }

    public sealed class ResetWithoutCheckoutOptions
    {
        public ResetWithoutCheckoutPreflight Preflight { get; init; }
    }

    public enum HistoryResetStepKind
    {
        BackupRefCreate = 0,
        LocalBranchReset,
        RemoteHistoryRewrite,
        RemoteStateProbe,
        BackupRestore,
    }

    public sealed class HistoryResetStepResult
    {
        public HistoryResetStepKind Kind { get; init; }
        public bool Succeeded { get; init; }
        public bool Skipped { get; init; }
        public string Label { get; init; } = string.Empty;
        public string Detail { get; init; } = string.Empty;
    }

    public sealed class HistoryResetOutcome
    {
        public string OperationId { get; init; } = string.Empty;
        public bool Succeeded { get; init; }
        public bool IsPartialSuccess { get; init; }
        public bool BlockedByProtection { get; init; }
        public bool BlockedBySnapshot { get; init; }
        public bool BlockedByLock { get; init; }
        public bool BlockedByDirtyWorkingTree { get; init; }
        public bool BlockedByUntrackedOrIgnoredCollision { get; init; }
        public bool BlockedByNonFirstParent { get; init; }
        public bool BlockedByRootCommit { get; init; }
        public bool BlockedByDivergedRemote { get; init; }
        public bool BlockedByUnknownRemoteState { get; init; }
        public bool RejectedByRemoteLease { get; init; }
        public bool CollaboratorCommitsPreserved { get; init; }
        public bool RemotePushOutcomeUncertain { get; init; }
        public bool LocalBranchReset { get; init; }
        public bool RemoteBranchRewritten { get; init; }
        public string LocalBranchName { get; init; } = string.Empty;
        public string OriginalTipSha { get; init; } = string.Empty;
        public string TargetTipSha { get; init; } = string.Empty;
        public string ExpectedRemoteTipSha { get; init; } = string.Empty;
        public string VerifiedRemoteTipSha { get; init; } = string.Empty;
        public string RemoteName { get; init; } = string.Empty;
        public string RemoteBranchName { get; init; } = string.Empty;
        public string BackupRefName { get; init; } = string.Empty;
        public string Summary { get; init; } = string.Empty;
        public string DetailMessage { get; init; } = string.Empty;
        public IReadOnlyList<HistoryResetStepResult> StepResults { get; init; } = [];
        public GitOperationRecord OperationRecord { get; init; }
    }

    public sealed class BackupResetRestorePreflight
    {
        public string RepoPath { get; init; } = string.Empty;
        public string CanonicalCommonDir { get; init; } = string.Empty;
        public string BackupRefName { get; init; } = string.Empty;
        public bool BackupExists { get; init; }
        public string BackupCommitSha { get; init; } = string.Empty;
        public string BackupCommitSubject { get; init; } = string.Empty;
        public string TargetBranchName { get; init; } = string.Empty;
        public bool TargetBranchExists { get; init; }
        public string TargetBranchCurrentTipSha { get; init; } = string.Empty;
        public string ExpectedPostResetTipSha { get; init; } = string.Empty;
        public bool IsTargetBranchCurrentHead { get; init; }
        public bool IsCheckedOutInOtherWorktree { get; init; }
        public string CheckedOutWorktreePath { get; init; } = string.Empty;
        public GitInProgressKind InProgressKind { get; init; }
        public bool IsWorkingTreeAndIndexClean { get; init; }
        public IReadOnlyList<string> CollisionPaths { get; init; } = [];
        public bool HasNewerCommitsAfterReset { get; init; }
        public IReadOnlyList<ResetCommitSummary> NewerCommits { get; init; } = [];
        public bool CanCreateNewRecoveryBranch { get; init; }
        public string SuggestedNewBranchName { get; init; } = string.Empty;
        public bool CanUpdateExistingBranchSafely { get; init; }
        public string BlockReason { get; init; } = string.Empty;
        public OperationConfirmationSnapshot Snapshot { get; init; }
        public string Summary { get; init; } = string.Empty;
    }

    public sealed class BackupResetRestoreOptions
    {
        public bool RestoreOntoExistingBranch { get; init; }
        public string NewBranchName { get; init; } = string.Empty;
        public BackupResetRestorePreflight Preflight { get; init; }
        public string SourceOperationId { get; init; } = string.Empty;
    }

    public sealed class RemoteRewriteJournalVerificationResult
    {
        public bool QuerySucceeded { get; init; }
        public string RemoteName { get; init; } = string.Empty;
        public string RemoteBranchName { get; init; } = string.Empty;
        public string LiveRemoteTipSha { get; init; } = string.Empty;
        public GitOperationRecord UpdatedRecord { get; init; }
        public string Summary { get; init; } = string.Empty;
    }
}
