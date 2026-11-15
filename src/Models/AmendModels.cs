using System;
using System.Collections.Generic;

namespace SourceGit.Models
{
    public enum AmendRemotePublishState
    {
        UnpushedLocalOnly,
        AlreadyPushed,
        UnfetchedOrStaleRemote,
        UnknownRemoteState,
        DetachedHead,
    }

    public sealed class AmendLastCommitPreflight
    {
        public string RepoPath { get; init; } = string.Empty;
        public string CanonicalGitDir { get; init; } = string.Empty;
        public string CanonicalCommonDir { get; init; } = string.Empty;
        public string BlockReason { get; init; } = string.Empty;
        public bool HasHardBlocker => !string.IsNullOrEmpty(BlockReason);

        public string CurrentBranchName { get; init; } = string.Empty;
        public bool IsDetachedHead { get; init; }
        public string HeadSha { get; init; } = string.Empty;
        public string HeadShortSha { get; init; } = string.Empty;
        public string HeadSubject { get; init; } = string.Empty;
        public string HeadFullMessage { get; init; } = string.Empty;
        public string HeadAuthorDisplay { get; init; } = string.Empty;
        public int HeadParentCount { get; init; }

        public StagedSnapshot Snapshot { get; init; }
        public IReadOnlyList<Change> NewlyStagedChanges { get; init; } = Array.Empty<Change>();
        public IReadOnlyList<Change> ResultingAmendChanges { get; init; } = Array.Empty<Change>();

        public CommitIdentity Identity { get; init; }
        public GitConfigSigningStatus SigningStatus { get; init; }
        public string SigningSummary { get; init; } = string.Empty;
        public bool HasCommitHooks { get; init; }
        public string HookSummary { get; init; } = string.Empty;

        public AmendRemotePublishState RemotePublishState { get; init; } = AmendRemotePublishState.UnknownRemoteState;
        public string RemotePublishSummary { get; init; } = string.Empty;
        public string RemotePublishDetail { get; init; } = string.Empty;
        public string RemoteName { get; init; } = string.Empty;
        public string RemoteBranchName { get; init; } = string.Empty;
        public string TrackingRefName { get; init; } = string.Empty;
        public string LocalTrackingTipSha { get; init; } = string.Empty;
        public string LiveRemoteTipSha { get; init; } = string.Empty;
        public string ConfirmedPushUrl { get; init; } = string.Empty;
        public string RedactedRemoteUrl { get; init; } = string.Empty;
        public bool HasRemotes { get; init; }

        public bool RemoteMovedAheadOfHead { get; init; }
        public IReadOnlyList<ResetCommitSummary> RemoteCommitsAheadOfHead { get; init; } = Array.Empty<ResetCommitSummary>();
        public bool HasOtherAuthorsOnRemote { get; init; }
        public string RemoteAuthorsSummary { get; init; } = string.Empty;

        public BranchProtectionStatus LocalBranchProtection { get; init; }
        public BranchProtectionStatus RemoteBranchProtection { get; init; }
        public bool BlockedByBranchProtection =>
            LocalBranchProtection is { IsEffectivelyProtected: true } ||
            RemoteBranchProtection is { IsEffectivelyProtected: true };

        public bool CanAmendDirectly =>
            !HasHardBlocker &&
            RemotePublishState == AmendRemotePublishState.UnpushedLocalOnly;

        public bool CanOptInPushedRewrite =>
            !HasHardBlocker &&
            RemotePublishState == AmendRemotePublishState.AlreadyPushed &&
            !IsDetachedHead &&
            !RemoteMovedAheadOfHead &&
            !BlockedByBranchProtection &&
            !string.IsNullOrEmpty(RemoteName) &&
            !string.IsNullOrEmpty(RemoteBranchName) &&
            !string.IsNullOrEmpty(LiveRemoteTipSha) &&
            !string.IsNullOrEmpty(ConfirmedPushUrl);

        public bool MustGuideNewCommit =>
            !HasHardBlocker &&
            RemotePublishState != AmendRemotePublishState.UnpushedLocalOnly;
    }

    public sealed class AmendLastCommitExecutionOptions
    {
        public AmendLastCommitPreflight Preflight { get; init; }
        public string Message { get; init; } = string.Empty;
        public bool SignOff { get; init; }
        public bool NoVerify { get; init; }
        public bool ResetAuthor { get; init; }
        public bool RewritePushedRemoteWithLease { get; init; }
    }

    public sealed class AmendLastCommitOutcome
    {
        public bool Succeeded { get; init; }
        public bool AmendSucceeded { get; init; }
        public bool PushAttempted { get; init; }
        public bool PushSucceeded { get; init; }
        public bool IsPartialSuccess { get; init; }
        public bool BlockedBySnapshot { get; init; }
        public bool BlockedByProtection { get; init; }
        public bool BlockedByRemoteState { get; init; }
        public bool RejectedByLease { get; init; }

        public string OperationId { get; init; } = string.Empty;
        public string OriginalHeadSha { get; init; } = string.Empty;
        public string NewHeadSha { get; init; } = string.Empty;
        public string BackupRefName { get; init; } = string.Empty;
        public string ExpectedRemoteTipSha { get; init; } = string.Empty;
        public string RemoteName { get; init; } = string.Empty;
        public string RemoteBranchName { get; init; } = string.Empty;
        public string Summary { get; init; } = string.Empty;
        public string DetailMessage { get; init; } = string.Empty;
        public CommitAndPushSession RetrySession { get; init; }

        public bool CanRetryPushOnly => RetrySession is { CanRetryPushOnly: true };
    }
}
