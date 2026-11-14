using System;
using System.Collections.Generic;

namespace SourceGit.Models
{
    public enum StashApplyStatus
    {
        CleanApplied = 0,
        AppliedWithConflictsRetained,
        PartialDropFailed,
        Blocked,
        Failed,
    }

    public sealed class StashPushPreviewItem
    {
        public string Path { get; init; } = string.Empty;
        public ChangeState Index { get; init; } = ChangeState.None;
        public ChangeState WorkTree { get; init; } = ChangeState.None;
        public bool IsUntracked { get; init; }
        public bool Included { get; init; }
    }

    public sealed class StashPushPreflight
    {
        public bool CanProceed { get; init; }
        public bool IsUnbornHead { get; init; }
        public bool HasInProgressConflict { get; init; }
        public bool HasChangesToStash { get; init; }
        public string BlockingReason { get; init; } = string.Empty;
        public string Summary { get; init; } = string.Empty;
        public List<StashPushPreviewItem> CandidateFiles { get; init; } = [];
        public int IncludedCount { get; init; }
        public int UntrackedIncludedCount { get; init; }
        public int IgnoredFileCount { get; init; }
        public List<string> IgnoredSamplePaths { get; init; } = [];
    }

    public sealed class StashPushResult
    {
        public bool Success { get; init; }
        public bool WasNoOp { get; init; }
        public string CreatedStashSha { get; init; } = string.Empty;
        public string CreatedStashName { get; init; } = string.Empty;
        public string Message { get; init; } = string.Empty;
        public string OperationRecordId { get; init; } = string.Empty;
    }

    public sealed class StashApplyPreflight
    {
        public bool CanProceed { get; init; }
        public bool StashExists { get; init; }
        public string StashName { get; init; } = string.Empty;
        public string StashSha { get; init; } = string.Empty;
        public string StashSubject { get; init; } = string.Empty;
        public bool IsWorkingTreeClean { get; init; }
        public bool IsIndexClean { get; init; }
        public bool HasInProgressConflict { get; init; }
        public int DirtyWorkingTreeCount { get; init; }
        public int DirtyIndexCount { get; init; }
        public List<string> EstimatedAffectedPaths { get; init; } = [];
        public List<string> CollidingUntrackedPaths { get; init; } = [];
        public List<string> CollidingIgnoredPaths { get; init; } = [];
        public List<string> BlockingReasons { get; init; } = [];
        public string Summary { get; init; } = string.Empty;
        public OperationConfirmationSnapshot Snapshot { get; init; }
    }

    public sealed class StashApplyResult
    {
        public StashApplyStatus Status { get; init; }
        public bool Success => Status == StashApplyStatus.CleanApplied;
        public bool StashRetained { get; init; }
        public bool DropSucceeded { get; init; }
        public string StashName { get; init; } = string.Empty;
        public string StashSha { get; init; } = string.Empty;
        public List<string> ConflictedPaths { get; init; } = [];
        public string Summary { get; init; } = string.Empty;
        public string OperationRecordId { get; init; } = string.Empty;
    }

    public sealed class BranchSwitchResult
    {
        public bool Success { get; init; }
        public bool CheckoutSucceeded { get; init; }
        public bool StashCreated { get; init; }
        public string StashSha { get; init; } = string.Empty;
        public bool ReapplyAttempted { get; init; }
        public bool ReapplySucceeded { get; init; }
        public bool StashRetained { get; init; }
        public bool HasConflicts { get; init; }
        public List<string> ConflictedPaths { get; init; } = [];
        public string Summary { get; init; } = string.Empty;
    }

    public enum DiscardBaselineKind
    {
        RestoreWorktreeToIndex = 0,
        RestoreToHead,
        MoveUntrackedToRecycleBin,
    }

    public sealed class DiscardTargetItem
    {
        public string Path { get; init; } = string.Empty;
        public string OriginalPath { get; init; } = string.Empty;
        public ChangeState Index { get; init; } = ChangeState.None;
        public ChangeState WorkTree { get; init; } = ChangeState.None;
        public bool IsSubmodule { get; init; }
        public bool IsFolder { get; init; }
        public bool IsUntracked => Index == ChangeState.Untracked || WorkTree == ChangeState.Untracked;
        public bool IsIndexAddedOnly { get; set; }
    }

    public sealed class DiscardPreflight
    {
        public DiscardBaselineKind Baseline { get; init; }
        public bool CanProceed { get; init; }
        public bool HasUnbornHead { get; init; }
        public string BlockingReason { get; init; } = string.Empty;
        public List<DiscardTargetItem> TrackedTargets { get; init; } = [];
        public List<DiscardTargetItem> UntrackedTargets { get; init; } = [];
        public List<string> SkippedSubmodulePaths { get; init; } = [];
        public List<string> IndexAddedOnlyPaths { get; init; } = [];
    }

    public sealed class DiscardBackupFileEntry
    {
        public string RelativePath { get; set; } = string.Empty;
        public string OriginalPath { get; set; } = string.Empty;
        public DiscardBaselineKind Baseline { get; set; } = DiscardBaselineKind.RestoreWorktreeToIndex;
        public bool ExistsInWorktree { get; set; }
        public bool IsSymlink { get; set; }
        public string SymlinkTarget { get; set; } = string.Empty;
        public string WorktreeSha256 { get; set; } = string.Empty;
        public long WorktreeByteLength { get; set; }
        public string BackupPayloadRelativePath { get; set; } = string.Empty;
        public bool ExistsInIndex { get; set; }
        public string IndexMode { get; set; } = string.Empty;
        public string IndexBlobOid { get; set; } = string.Empty;
        public string IndexPayloadRelativePath { get; set; } = string.Empty;
        public string PostDiscardWorktreeSha256 { get; set; } = string.Empty;
        public string PostDiscardIndexBlobOid { get; set; } = string.Empty;
    }

    public sealed class DiscardBackupEntry
    {
        public string BackupId { get; set; } = string.Empty;
        public DateTime CreatedAtUtc { get; set; }
        public DateTime ExpiresAtUtc { get; set; }
        public string RepoKey { get; set; } = string.Empty;
        public string WorktreePath { get; set; } = string.Empty;
        public string GitCommonDir { get; set; } = string.Empty;
        public string HeadSha { get; set; } = string.Empty;
        public DiscardBaselineKind Baseline { get; set; } = DiscardBaselineKind.RestoreWorktreeToIndex;
        public string Summary { get; set; } = string.Empty;
        public List<DiscardBackupFileEntry> Files { get; set; } = [];
        public DateTime? RestoredAtUtc { get; set; }
    }

    public sealed class DiscardBackupIndex
    {
        public int Version { get; set; } = 1;
        public List<DiscardBackupEntry> Entries { get; set; } = [];
    }

    public sealed class DiscardBackupRestoreCheck
    {
        public bool CanRestore { get; init; }
        public bool Found { get; init; }
        public bool IsExpired { get; init; }
        public bool HasPayloadIntegrityError { get; init; }
        public bool HasSubsequentModifications { get; init; }
        public DiscardBackupEntry Entry { get; init; }
        public List<string> ConflictingPaths { get; init; } = [];
        public List<string> ConflictDetails { get; init; } = [];
        public List<string> MissingPayloadPaths { get; init; } = [];
        public string BlockingReason { get; init; } = string.Empty;
    }

    public sealed class DiscardBackupRestoreResult
    {
        public bool Success { get; init; }
        public string BackupId { get; init; } = string.Empty;
        public List<string> RestoredPaths { get; init; } = [];
        public int IndexRestoredCount { get; init; }
        public string Summary { get; init; } = string.Empty;
    }

    public sealed class DiscardBackupPruneResult
    {
        public int ExpiredCount { get; init; }
        public int RemovedCount { get; init; }
        public List<string> RemovedBackupIds { get; init; } = [];
        public string Summary { get; init; } = string.Empty;
    }

    public sealed class DiscardExecutionResult
    {
        public bool Success { get; init; }
        public DiscardBaselineKind Baseline { get; init; }
        public string BackupId { get; init; } = string.Empty;
        public List<string> ProcessedPaths { get; init; } = [];
        public List<string> RecycledPaths { get; init; } = [];
        public List<string> FailedPaths { get; init; } = [];
        public string Summary { get; init; } = string.Empty;
        public string OperationRecordId { get; init; } = string.Empty;
    }

    public enum IgnoreRulePresetKind
    {
        ExactFile = 0,
        ByExtension,
        ByExtensionInFolder,
        ParentFolder,
        Custom,
    }

    public enum IgnoreRuleTargetKind
    {
        RootGitIgnore = 0,
        ParentGitIgnore,
        LocalExclude,
    }

    public sealed class IgnoreRulePresetOption
    {
        public IgnoreRulePresetKind Kind { get; init; }
        public string Pattern { get; init; } = string.Empty;
        public string DisplayLabel { get; init; } = string.Empty;
    }

    public sealed class IgnoreRuleTargetOption
    {
        public IgnoreRuleTargetKind Kind { get; init; }
        public string DisplayName { get; init; } = string.Empty;
        public string FullPath { get; init; } = string.Empty;
        public string BaseRelativeDir { get; init; } = string.Empty;
        public bool IsLocalOnly { get; init; }
    }

    public sealed class IgnoreRuleInspection
    {
        public bool IsValid { get; init; }
        public string ValidationError { get; init; } = string.Empty;
        public string RawPattern { get; init; } = string.Empty;
        public string EffectivePattern { get; init; } = string.Empty;
        public string TargetFilePath { get; init; } = string.Empty;
        public bool IsLocalOnly { get; init; }
        public bool AlreadyExists { get; init; }
        public List<string> NegationConflicts { get; init; } = [];
        public List<string> MatchedUntrackedPaths { get; init; } = [];
        public List<string> MatchedTrackedPaths { get; init; } = [];
        public List<string> Warnings { get; init; } = [];
    }

    public sealed class IgnoreRuleApplyResult
    {
        public bool Success { get; init; }
        public bool SkippedDuplicate { get; init; }
        public string WrittenPattern { get; init; } = string.Empty;
        public string TargetFilePath { get; init; } = string.Empty;
        public IgnoreRuleInspection Inspection { get; init; }
        public string Summary { get; init; } = string.Empty;
    }
}
