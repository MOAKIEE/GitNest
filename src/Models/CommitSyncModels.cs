using System;
using System.Collections.Generic;
using System.Globalization;

namespace SourceGit.Models
{
    public enum CommitFailureKind
    {
        None,
        EmptyMessage,
        MissingCommitIdentity,
        RepositoryLocked,
        StagedSnapshotChanged,
        NothingStaged,
        DetachedHead,
        ConflictsPresent,
        HookRejected,
        SigningFailed,
        Canceled,
        GitError,
    }

    public sealed class CommitExecutionOptions
    {
        public string Message { get; init; } = string.Empty;
        public bool SignOff { get; init; }
        public bool NoVerify { get; init; }
        public bool Amend { get; init; }
        public bool ResetAuthor { get; init; }
        public bool AllowEmpty { get; init; }
        public bool CheckIdentity { get; init; } = true;
        public StagedSnapshot ExpectedSnapshot { get; init; }
    }

    public sealed class CommitExecutionResult
    {
        public bool Succeeded { get; init; }
        public CommitFailureKind FailureKind { get; init; } = CommitFailureKind.None;
        public string CommitSha { get; init; } = string.Empty;
        public string ShortSha { get; init; } = string.Empty;
        public string BranchName { get; init; } = string.Empty;
        public string Subject { get; init; } = string.Empty;
        public string Summary { get; init; } = string.Empty;
        public string DiagnosticDetail { get; init; } = string.Empty;
        public StagedSnapshot CurrentSnapshot { get; init; }
        public CommitIdentity Identity { get; init; }
    }

    public enum FetchOutcomeKind
    {
        Success,
        AuthFailed,
        AuthCanceled,
        OfflineOrUnreachable,
        RemoteNotFound,
        RepositoryLocked,
        Canceled,
        GitError,
    }

    public sealed class FetchExecutionResult
    {
        public bool Succeeded => Outcome == FetchOutcomeKind.Success;
        public FetchOutcomeKind Outcome { get; init; } = FetchOutcomeKind.Success;
        public IReadOnlyList<string> FetchedRemotes { get; init; } = [];
        public DateTimeOffset? FetchedAtUtc { get; init; }
        public bool UpdatedAnyRefs { get; init; }
        public string Summary { get; init; } = string.Empty;
        public string DiagnosticDetail { get; init; } = string.Empty;
    }

    public enum PullStrategyMode
    {
        FastForwardOnly,
    }

    public enum PullOutcomeKind
    {
        UpdatedFastForward,
        AlreadyUpToDate,
        Diverged,
        LocalChangesConflict,
        NoUpstreamConfigured,
        UpstreamGone,
        DetachedHead,
        AuthFailed,
        AuthCanceled,
        OfflineOrUnreachable,
        RemoteNotFound,
        RepositoryLocked,
        Canceled,
        GitError,
    }

    public sealed class PullExecutionResult
    {
        public PullStrategyMode Strategy { get; init; } = PullStrategyMode.FastForwardOnly;
        public bool Succeeded => Outcome is PullOutcomeKind.UpdatedFastForward or PullOutcomeKind.AlreadyUpToDate;
        public PullOutcomeKind Outcome { get; init; } = PullOutcomeKind.AlreadyUpToDate;
        public bool RequiresMergeHandoff => Outcome == PullOutcomeKind.Diverged;
        public string RemoteName { get; init; } = string.Empty;
        public string RemoteBranchName { get; init; } = string.Empty;
        public string RemoteTrackingRef { get; init; } = string.Empty;
        public string LocalBranchName { get; init; } = string.Empty;
        public string BeforeHead { get; init; } = string.Empty;
        public string AfterHead { get; init; } = string.Empty;
        public int LocalAheadCount { get; init; }
        public int RemoteBehindCount { get; init; }
        public IReadOnlyList<string> BlockingPaths { get; init; } = [];
        public string Summary { get; init; } = string.Empty;
        public string DiagnosticDetail { get; init; } = string.Empty;
    }

    public enum PushOutcomeKind
    {
        Success,
        UpToDate,
        NonFastForwardRejected,
        RemoteBranchExistsWithoutUpstream,
        ProtectedOrHookRejected,
        AuthFailed,
        AuthCanceled,
        OfflineOrUnreachable,
        RemoteNotFound,
        DetachedHead,
        RepositoryLocked,
        Canceled,
        GitError,
    }

    public sealed class PushExecutionOptions
    {
        public string LocalBranch { get; init; } = string.Empty;
        public Remote Remote { get; init; }
        public string RemoteBranch { get; init; } = string.Empty;
        public bool SetUpstream { get; init; }
        public bool CheckSubmodules { get; init; }
        public bool NoVerify { get; init; }
        public bool ConfirmOverwriteExistingRemoteBranchWithoutUpstream { get; init; }
    }

    public sealed class PushExecutionResult
    {
        public bool Succeeded => Outcome is PushOutcomeKind.Success or PushOutcomeKind.UpToDate;
        public PushOutcomeKind Outcome { get; init; } = PushOutcomeKind.Success;
        public string RemoteName { get; init; } = string.Empty;
        public string LocalBranch { get; init; } = string.Empty;
        public string RemoteBranch { get; init; } = string.Empty;
        public string Refspec { get; init; } = string.Empty;
        public bool SetUpstreamApplied { get; init; }
        public string HeadSha { get; init; } = string.Empty;
        public string Summary { get; init; } = string.Empty;
        public string DiagnosticDetail { get; init; } = string.Empty;
    }

    public sealed class CommitAndPushSession
    {
        public string RepoPath { get; init; } = string.Empty;
        public CommitExecutionResult CommitResult { get; init; }
        public bool PushAttempted { get; init; }
        public PushExecutionResult PushResult { get; init; }
        public PushExecutionOptions PendingPushOptions { get; init; }

        public bool CommitSucceeded => CommitResult is { Succeeded: true };
        public bool PushSucceeded => PushResult is { Succeeded: true };
        public bool IsFullySucceeded => CommitSucceeded && PushSucceeded;
        public bool CanRetryPushOnly => CommitSucceeded && !PushSucceeded && PendingPushOptions != null;

        public string Summary
        {
            get
            {
                if (!CommitSucceeded)
                    return CommitResult?.Summary ?? "本地提交失败。";

                if (!PushAttempted)
                    return $"本地提交已成功（{CommitResult.ShortSha}），尚未执行推送。";

                if (PushSucceeded)
                    return $"本地提交（{CommitResult.ShortSha}）与单分支推送均已完成。";

                return $"本地提交已成功（{CommitResult.ShortSha}），但推送失败：{PushResult?.Summary ?? "请重试推送。"}";
            }
        }
    }

    public sealed class RepositorySyncOverview
    {
        public string SummaryText { get; init; } = string.Empty;
        public string DetailText { get; init; } = string.Empty;
        public bool HasVerifiedFetch { get; init; }
        public bool HasUnknownRemoteState { get; init; }
    }

    public static class RepositorySyncOverviewBuilder
    {
        public static RepositorySyncOverview Build(
            int uncommittedCount,
            string currentBranchName,
            string upstreamFriendlyName,
            int aheadCount,
            int behindCount,
            bool hasRemotes,
            bool isUpstreamGone,
            DateTime? lastFetchAt,
            bool lastFetchFailed,
            string lastFetchError)
        {
            var uncommittedText = $"{uncommittedCount} 个文件未提交";
            var details = new List<string>
            {
                $"工作区未提交变更：{uncommittedCount} 个文件"
            };

            if (string.IsNullOrWhiteSpace(currentBranchName))
            {
                return new RepositorySyncOverview
                {
                    SummaryText = $"{uncommittedText} · 游离 HEAD 状态",
                    DetailText = string.Join(Environment.NewLine, details),
                    HasVerifiedFetch = lastFetchAt.HasValue && !lastFetchFailed,
                    HasUnknownRemoteState = true,
                };
            }

            if (!hasRemotes)
            {
                details.Add("远端同步状态：尚未配置远端仓库");
                return new RepositorySyncOverview
                {
                    SummaryText = $"{uncommittedText} · 未配置远端",
                    DetailText = string.Join(Environment.NewLine, details),
                    HasVerifiedFetch = false,
                    HasUnknownRemoteState = true,
                };
            }

            if (string.IsNullOrWhiteSpace(upstreamFriendlyName))
            {
                details.Add($"当前分支：{currentBranchName}（尚未设置上游跟踪分支）");
                return new RepositorySyncOverview
                {
                    SummaryText = $"{uncommittedText} · 未设置上游分支",
                    DetailText = string.Join(Environment.NewLine, details),
                    HasVerifiedFetch = lastFetchAt.HasValue && !lastFetchFailed,
                    HasUnknownRemoteState = true,
                };
            }

            if (isUpstreamGone)
            {
                details.Add($"上游分支：{upstreamFriendlyName}（已在远端删除）");
                return new RepositorySyncOverview
                {
                    SummaryText = $"{uncommittedText} · 上游 {upstreamFriendlyName} 已删除",
                    DetailText = string.Join(Environment.NewLine, details),
                    HasVerifiedFetch = lastFetchAt.HasValue && !lastFetchFailed,
                    HasUnknownRemoteState = true,
                };
            }

            var hasFetched = lastFetchAt.HasValue;
            var pushPart = aheadCount > 0
                ? $"{aheadCount} 次提交待推送至 {upstreamFriendlyName}"
                : $"无待推送提交 ({upstreamFriendlyName})";

            string pullPart;
            bool unknownRemote;
            if (!hasFetched)
            {
                pullPart = "远端状态未获取";
                unknownRemote = true;
                details.Add($"远端获取：尚未获取（不将未知远端提交记为 0）");
            }
            else if (lastFetchFailed)
            {
                var timeLabel = lastFetchAt.Value.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
                pullPart = behindCount > 0
                    ? $"{behindCount} 次远端提交待拉取（缓存于 {timeLabel}，最新获取失败）"
                    : $"远端缓存于 {timeLabel}（最新获取失败）";
                unknownRemote = true;
                details.Add(string.IsNullOrWhiteSpace(lastFetchError)
                    ? $"远端获取：最近一次获取失败（使用 {timeLabel} 缓存）"
                    : $"远端获取失败：{lastFetchError}（使用 {timeLabel} 缓存）");
            }
            else
            {
                var timeLabel = lastFetchAt.Value.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
                pullPart = $"{behindCount} 次远端提交待拉取";
                unknownRemote = false;
                details.Add($"远端获取：已更新于 {timeLabel}");
            }

            details.Add($"同步计数（{upstreamFriendlyName}）：本地待推送 {aheadCount} · 远端待拉取 {(hasFetched ? behindCount.ToString(CultureInfo.InvariantCulture) : "未知（未获取）")}");

            return new RepositorySyncOverview
            {
                SummaryText = $"{uncommittedText} · {pushPart} · {pullPart}",
                DetailText = string.Join(Environment.NewLine, details),
                HasVerifiedFetch = hasFetched && !lastFetchFailed,
                HasUnknownRemoteState = unknownRemote,
            };
        }
    }
}
