using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace SourceGit.Models
{
    public enum BranchDeleteStepKind
    {
        WorktreeRemove = 0,
        BackupRefCreate,
        LocalBranchDelete,
        RemoteBranchDelete,
        TrackingCachePrune,
    }

    public sealed class BranchDeleteStepResult
    {
        public BranchDeleteStepKind Step { get; init; }
        public bool Succeeded { get; init; }
        public bool Skipped { get; init; }
        public string Label { get; init; } = string.Empty;
        public string Detail { get; init; } = string.Empty;
    }

    public sealed class UnmergedCommitSummary
    {
        public string Sha { get; init; } = string.Empty;
        public string ShortSha => Sha.Length > 8 ? Sha[..8] : Sha;
        public string Subject { get; init; } = string.Empty;
    }

    public sealed class RemoteBranchProbeResult
    {
        [System.Text.Json.Serialization.JsonIgnore]
        public string PushUrl { get; init; } = string.Empty;
        public string RemoteName { get; init; } = string.Empty;
        public string RemoteBranchName { get; init; } = string.Empty;
        public string RedactedRemoteUrl { get; init; } = string.Empty;
        public bool QuerySucceeded { get; init; }
        public bool ExistsOnServer { get; init; }
        public string LiveTipSha { get; init; } = string.Empty;
        public string ErrorSummary { get; init; } = string.Empty;
    }

    public sealed class BranchDeletePreflight
    {
        public string RepoPath { get; init; } = string.Empty;
        public string CanonicalCommonDir { get; init; } = string.Empty;
        public bool IsLocal { get; init; }
        public string BranchName { get; init; } = string.Empty;
        public string FullRefName { get; init; } = string.Empty;
        public string TargetTipSha { get; init; } = string.Empty;
        public bool IsCurrent { get; init; }

        // Attached non-current worktree state (when IsLocal == true)
        public bool IsCheckedOutInOtherWorktree { get; init; }
        public string AttachedWorktreePath { get; init; } = string.Empty;
        public bool AttachedWorktreeLocked { get; init; }
        public string AttachedWorktreeLockReason { get; init; } = string.Empty;
        public bool AttachedWorktreePrunable { get; init; }
        public bool AttachedWorktreeHasChanges { get; init; }
        public WorktreeInspectPreflight AttachedWorktreePreflight { get; init; }

        // Local unmerged commit inspection vs HEAD
        public bool IsMergedIntoHead { get; init; }
        public bool HasUnmergedCommits { get; init; }
        public int UnmergedCommitCount { get; init; }
        public int UnmergedChangedFilesCount { get; init; }
        public IReadOnlyList<UnmergedCommitSummary> UnmergedCommits { get; init; } = [];

        // Configured upstream or target remote info
        public bool HasMultipleRemotes { get; init; }
        public bool HasConfiguredUpstream { get; init; }
        public string ConfiguredUpstreamRef { get; init; } = string.Empty;
        public string RemoteName { get; init; } = string.Empty;
        public string RemoteBranchName { get; init; } = string.Empty;
        public string RedactedRemoteUrl { get; init; } = string.Empty;
        public bool IsUpstreamNameDifferent { get; init; }
        public bool HasLocalTrackingCache { get; init; }
        public string LocalTrackingTipSha { get; init; } = string.Empty;
        public RemoteBranchProbeResult RemoteProbe { get; init; }

        public OperationConfirmationSnapshot ConfirmationSnapshot { get; init; }
        public BranchProtectionStatus Protection { get; init; } = new();
        public bool CanDeleteDirectly => !Protection.IsEffectivelyProtected && (IsLocal ? (!IsCurrent && !IsCheckedOutInOtherWorktree) : true);
        public string DirectBlockReason { get; init; } = string.Empty;
    }

    public sealed class BranchDeleteOptions
    {
        public BranchDeletePreflight Preflight { get; init; }
        public bool DeleteAttachedWorktree { get; init; }
        public bool ForceRemoveWorktree { get; init; }
        public bool ForceDeleteLocal { get; init; }
        public bool DeleteLinkedUpstreamRemote { get; init; }
        public bool PruneStaleTrackingCacheWhenRemoteAbsent { get; init; }
        public string ConfirmedExpectedRemoteTipSha { get; init; }
        [System.Text.Json.Serialization.JsonIgnore]
        public string ConfirmedPushUrl { get; init; }
        public bool? ConfirmedRemoteExistsOnServer { get; init; }
        public Func<string, string, CancellationToken, Task> BeforeWorktreeRemoveAsync { get; init; }
    }

    public sealed class BranchDeleteOutcome
    {
        public string OperationId { get; init; } = string.Empty;
        public string BranchName { get; init; } = string.Empty;
        public bool Succeeded { get; init; }
        public bool IsPartialSuccess { get; init; }
        public bool BlockedByLock { get; init; }
        public bool BlockedBySnapshot { get; init; }
        public bool BlockedByProtection { get; init; }
        public bool BlockedByNestedRepoOrSubmodule { get; init; }
        public bool RequiresForceWorktreeConfirmation { get; init; }
        public bool RequiresForceLocalConfirmation { get; init; }
        public bool RequiresRemoteCachePruneConfirmation { get; init; }
        public bool RejectedByRemoteLease { get; init; }
        public bool CollaboratorCommitsPreserved { get; init; }

        public bool WorktreeRemoved { get; init; }
        public bool LocalBranchDeleted { get; init; }
        public bool RemoteBranchDeleted { get; init; }
        public bool LocalTrackingCachePruned { get; init; }

        public string BackupRefName { get; init; } = string.Empty;
        public string LocalBackupSha { get; init; } = string.Empty;
        public string RemoteName { get; init; } = string.Empty;
        public string RemoteBranchName { get; init; } = string.Empty;
        public string ExpectedRemoteTipSha { get; init; } = string.Empty;

        public string Summary { get; init; } = string.Empty;
        public string DetailMessage { get; init; } = string.Empty;
        public IReadOnlyList<BranchDeleteStepResult> StepResults { get; init; } = [];
        public int UnmergedCommitCount { get; init; }
        public int UnmergedChangedFilesCount { get; init; }
        public IReadOnlyList<UnmergedCommitSummary> UnmergedCommits { get; init; } = [];
        public GitOperationRecord OperationRecord { get; init; }
    }

    public sealed class BatchBranchDeleteOutcome
    {
        public bool AllSucceeded => FailedCount == 0 && SkippedCount == 0;
        public int SucceededCount { get; init; }
        public int FailedCount { get; init; }
        public int SkippedCount { get; init; }
        public string Summary { get; init; } = string.Empty;
        public IReadOnlyList<BranchDeleteOutcome> Items { get; init; } = [];
    }
}
