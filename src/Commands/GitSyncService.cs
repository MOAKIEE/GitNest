using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace SourceGit.Commands
{
    /// <summary>
    /// Provides serialized, snapshot-checked, and failure-classified Git staging, commit, fetch,
    /// fast-forward pull, and single-branch push operations.
    /// </summary>
    public static partial class GitSyncService
    {
        private sealed class SyncCommand : Command
        {
            public SyncCommand(string repoPath, string args, string sshKey = "")
            {
                WorkingDirectory = repoPath;
                Context = repoPath;
                SSHKey = sshKey ?? string.Empty;
                RaiseError = false;
                Args = args;
            }

            public Task<Result> RunReadToEndAsync() => ReadToEndAsync();
        }

        public static async Task<(bool Succeeded, Models.GitLockDetectionResult LockStatus, string Diagnostic)> StagePathsAsync(
            string repoPath,
            IEnumerable<string> paths,
            Models.ICommandLog log = null,
            bool force = false,
            CancellationToken cancellationToken = default)
        {
            var pathList = NormalizeNonEmptyPaths(paths);
            if (pathList.Count == 0)
                return (true, new Models.GitLockDetectionResult(), string.Empty);

            await using var guard = await Models.RepositoryWriteGuard
                .TryEnterAsync(repoPath, "Stage", checkIndexLock: true, branchNameToUpdate: null, cancellationToken)
                .ConfigureAwait(false);

            if (!guard.Acquired)
                return (false, guard.LockDetection, guard.LockDetection.Summary);

            var capture = new CapturingCommandLog(log);
            var pathSpecFile = await CommandArgumentHelper
                .WriteNulPathspecFileAsync(pathList, cancellationToken)
                .ConfigureAwait(false);

            try
            {
                var cmd = new Add(repoPath, pathSpecFile, isNulDelimited: true, force: force)
                {
                    RaiseError = false,
                }.WithCancellation(cancellationToken).Use(capture);

                var ok = await cmd.ExecAsync().ConfigureAwait(false);
                var diag = capture.GetCombinedText();
                return (ok, guard.LockDetection, diag);
            }
            finally
            {
                CommandArgumentHelper.TryDeleteTempFile(pathSpecFile);
            }
        }

        public static async Task<(bool Succeeded, Models.GitLockDetectionResult LockStatus, string Diagnostic)> UnstagePathsAsync(
            string repoPath,
            IEnumerable<string> paths,
            bool useAmend = false,
            List<Models.Change> amendChanges = null,
            Models.ICommandLog log = null,
            CancellationToken cancellationToken = default)
        {
            var pathList = NormalizeNonEmptyPaths(paths);
            if (pathList.Count == 0)
                return (true, new Models.GitLockDetectionResult(), string.Empty);

            await using var guard = await Models.RepositoryWriteGuard
                .TryEnterAsync(repoPath, "Unstage", checkIndexLock: true, branchNameToUpdate: null, cancellationToken)
                .ConfigureAwait(false);

            if (!guard.Acquired)
                return (false, guard.LockDetection, guard.LockDetection.Summary);

            var capture = new CapturingCommandLog(log);

            if (useAmend && amendChanges is { Count: > 0 })
            {
                capture.AppendLine("$ git update-index -z --index-info");
                var ok = await new UpdateIndexInfo(repoPath, amendChanges).ExecAsync().ConfigureAwait(false);
                return (ok, guard.LockDetection, capture.GetCombinedText());
            }

            var hasHead = await QueryStagedSnapshot.HasHeadCommitAsync(repoPath, cancellationToken).ConfigureAwait(false);
            var pathSpecFile = await CommandArgumentHelper
                .WriteNulPathspecFileAsync(pathList, cancellationToken)
                .ConfigureAwait(false);

            try
            {
                Command cmd = hasHead
                    ? new Reset(repoPath, pathSpecFile, isNulDelimited: true)
                    : new SyncCommand(repoPath,
                        $"--literal-pathspecs rm --cached --quiet --pathspec-file-nul --pathspec-from-file={CommandArgumentHelper.Quote(pathSpecFile)}");

                cmd.RaiseError = false;
                cmd.WithCancellation(cancellationToken).Use(capture);

                var ok = await cmd.ExecAsync().ConfigureAwait(false);
                return (ok, guard.LockDetection, capture.GetCombinedText());
            }
            finally
            {
                CommandArgumentHelper.TryDeleteTempFile(pathSpecFile);
            }
        }

        public static async Task<Models.CommitExecutionResult> CommitAsync(
            string repoPath,
            Models.CommitExecutionOptions options,
            Models.ICommandLog log = null,
            CancellationToken cancellationToken = default,
            Models.OperationJournalStore journalStore = null)
        {
            var originalHead = options?.ExpectedSnapshot?.HeadOid;
            var result = await CommitCoreAsync(repoPath, options, log, cancellationToken).ConfigureAwait(false);
            if (journalStore != null && result.FailureKind != Models.CommitFailureKind.EmptyMessage)
            {
                var stage = result.Succeeded
                    ? Models.GitOperationStage.Succeeded
                    : (result.FailureKind == Models.CommitFailureKind.Canceled
                        ? Models.GitOperationStage.CanceledUncertain
                        : Models.GitOperationStage.Failed);

                await TryRecordOperationAsync(
                    repoPath,
                    journalStore,
                    Models.GitOperationKind.Commit,
                    options?.Amend == true ? "修订提交 (Amend)" : "本地提交",
                    stage,
                    result.Summary,
                    branchName: !string.IsNullOrWhiteSpace(result.BranchName) ? result.BranchName : options?.ExpectedSnapshot?.CurrentBranchName,
                    originalTipSha: originalHead,
                    targetTipSha: result.CommitSha,
                    commandSummary: options?.Amend == true ? "git commit --amend" : "git commit",
                    diagnosticDetail: result.DiagnosticDetail).ConfigureAwait(false);
            }

            return result;
        }

        private static async Task<Models.CommitExecutionResult> CommitCoreAsync(
            string repoPath,
            Models.CommitExecutionOptions options,
            Models.ICommandLog log = null,
            CancellationToken cancellationToken = default)
        {
            if (options == null || string.IsNullOrWhiteSpace(options.Message))
            {
                return new Models.CommitExecutionResult
                {
                    Succeeded = false,
                    FailureKind = Models.CommitFailureKind.EmptyMessage,
                    Summary = "提交说明不能为空。",
                };
            }

            if (cancellationToken.IsCancellationRequested)
            {
                return new Models.CommitExecutionResult
                {
                    Succeeded = false,
                    FailureKind = Models.CommitFailureKind.Canceled,
                    Summary = "提交已取消。",
                };
            }

            var branchHint = options.ExpectedSnapshot is { IsDetachedHead: false }
                ? options.ExpectedSnapshot.CurrentBranchName
                : null;

            await using var guard = await Models.RepositoryWriteGuard
                .TryEnterAsync(repoPath, "Commit", checkIndexLock: true, branchNameToUpdate: branchHint, cancellationToken)
                .ConfigureAwait(false);

            if (!guard.Acquired)
            {
                return new Models.CommitExecutionResult
                {
                    Succeeded = false,
                    FailureKind = Models.CommitFailureKind.RepositoryLocked,
                    Summary = guard.LockDetection.Summary,
                    DiagnosticDetail = guard.LockDetection.Summary,
                };
            }

            var currentSnapshot = await QueryStagedSnapshot.CaptureAsync(repoPath, cancellationToken).ConfigureAwait(false);

            if (currentSnapshot.HasConflicts)
            {
                return new Models.CommitExecutionResult
                {
                    Succeeded = false,
                    FailureKind = Models.CommitFailureKind.ConflictsPresent,
                    BranchName = currentSnapshot.CurrentBranchName,
                    Summary = "存在未解决的合并冲突，无法提交。",
                    CurrentSnapshot = currentSnapshot,
                };
            }

            if (options.ExpectedSnapshot != null && !currentSnapshot.MatchesCommitBaseline(options.ExpectedSnapshot))
            {
                return new Models.CommitExecutionResult
                {
                    Succeeded = false,
                    FailureKind = Models.CommitFailureKind.StagedSnapshotChanged,
                    BranchName = currentSnapshot.CurrentBranchName,
                    Summary = "暂存区或当前分支状态已在确认前发生外部变更，已中止本次提交，请重新核对暂存区后再提交。",
                    CurrentSnapshot = currentSnapshot,
                };
            }

            if (!options.AllowEmpty && !options.Amend && !currentSnapshot.HasStagedChanges)
            {
                return new Models.CommitExecutionResult
                {
                    Succeeded = false,
                    FailureKind = Models.CommitFailureKind.NothingStaged,
                    BranchName = currentSnapshot.CurrentBranchName,
                    Summary = "暂存区没有待提交变更。",
                    CurrentSnapshot = currentSnapshot,
                };
            }

            var identity = await new Config(repoPath).ReadCommitIdentityAsync().ConfigureAwait(false);
            if (options.CheckIdentity && !identity.IsComplete)
            {
                return new Models.CommitExecutionResult
                {
                    Succeeded = false,
                    FailureKind = Models.CommitFailureKind.MissingCommitIdentity,
                    BranchName = currentSnapshot.CurrentBranchName,
                    Summary = "尚未配置当前仓库或全局提交身份（user.name / user.email），请先补全提交者身份。",
                    CurrentSnapshot = currentSnapshot,
                    Identity = identity,
                };
            }

            var capture = new CapturingCommandLog(log);
            var cmd = new Commit(
                repoPath,
                options.Message,
                options.SignOff,
                options.NoVerify,
                options.Amend,
                options.ResetAuthor,
                allowEmpty: options.AllowEmpty)
            {
                RaiseError = false,
            };

            cmd.WithCancellation(cancellationToken).Use(capture);
            var ok = await cmd.RunAsync().ConfigureAwait(false);
            var diag = capture.GetCombinedText();

            if (cancellationToken.IsCancellationRequested)
            {
                return new Models.CommitExecutionResult
                {
                    Succeeded = false,
                    FailureKind = Models.CommitFailureKind.Canceled,
                    BranchName = currentSnapshot.CurrentBranchName,
                    Summary = "提交已取消。",
                    DiagnosticDetail = diag,
                    CurrentSnapshot = await QueryStagedSnapshot.CaptureAsync(repoPath, CancellationToken.None).ConfigureAwait(false),
                    Identity = identity,
                };
            }

            if (!ok)
            {
                var failureKind = ClassifyCommitFailure(diag, identity);
                return new Models.CommitExecutionResult
                {
                    Succeeded = false,
                    FailureKind = failureKind,
                    BranchName = currentSnapshot.CurrentBranchName,
                    Summary = FormatCommitFailureSummary(failureKind, diag),
                    DiagnosticDetail = diag,
                    CurrentSnapshot = await QueryStagedSnapshot.CaptureAsync(repoPath, CancellationToken.None).ConfigureAwait(false),
                    Identity = identity,
                };
            }

            var headRes = await Command.RunGitDirectAsync(
                repoPath,
                cancellationToken,
                "rev-parse",
                "--verify",
                "HEAD").ConfigureAwait(false);

            var subjectRes = await Command.RunGitDirectAsync(
                repoPath,
                cancellationToken,
                "log",
                "-1",
                "--pretty=%s").ConfigureAwait(false);

            var fullSha = headRes.IsSuccess ? headRes.StdOut.Trim() : string.Empty;
            var shortSha = fullSha.Length >= 8 ? fullSha[..8] : fullSha;
            var subject = subjectRes.IsSuccess ? subjectRes.StdOut.Trim() : string.Empty;
            var updatedSnapshot = await QueryStagedSnapshot.CaptureAsync(repoPath, cancellationToken).ConfigureAwait(false);

            return new Models.CommitExecutionResult
            {
                Succeeded = true,
                FailureKind = Models.CommitFailureKind.None,
                CommitSha = fullSha,
                ShortSha = shortSha,
                BranchName = updatedSnapshot.CurrentBranchName,
                Subject = subject,
                Summary = $"本地提交成功（{shortSha}）：{subject}",
                DiagnosticDetail = diag,
                CurrentSnapshot = updatedSnapshot,
                Identity = identity,
            };
        }

        public static async Task<Models.FetchExecutionResult> FetchAsync(
            string repoPath,
            IReadOnlyList<Models.Remote> remotes,
            bool noTags = false,
            bool force = false,
            Models.ICommandLog log = null,
            CancellationToken cancellationToken = default,
            Models.OperationJournalStore journalStore = null)
        {
            var result = await FetchCoreAsync(repoPath, remotes, noTags, force, log, cancellationToken).ConfigureAwait(false);
            if (journalStore != null)
            {
                var stage = result.Succeeded
                    ? Models.GitOperationStage.Succeeded
                    : (result.Outcome == Models.FetchOutcomeKind.Canceled
                        ? Models.GitOperationStage.CanceledUncertain
                        : Models.GitOperationStage.Failed);

                var remoteNames = result.FetchedRemotes is { Count: > 0 }
                    ? string.Join(", ", result.FetchedRemotes)
                    : (remotes is { Count: > 0 } ? string.Join(", ", remotes.Where(r => r != null && !string.IsNullOrWhiteSpace(r.Name)).Select(r => r.Name)) : string.Empty);

                await TryRecordOperationAsync(
                    repoPath,
                    journalStore,
                    Models.GitOperationKind.Fetch,
                    "获取远端更新 (Fetch)",
                    stage,
                    result.Summary,
                    remoteName: remoteNames,
                    commandSummary: $"git fetch {(noTags ? "--no-tags " : string.Empty)}{(force ? "--force " : string.Empty)}{remoteNames}".Trim(),
                    diagnosticDetail: result.DiagnosticDetail).ConfigureAwait(false);
            }

            return result;
        }

        private static async Task<Models.FetchExecutionResult> FetchCoreAsync(
            string repoPath,
            IReadOnlyList<Models.Remote> remotes,
            bool noTags = false,
            bool force = false,
            Models.ICommandLog log = null,
            CancellationToken cancellationToken = default)
        {
            if (remotes == null || remotes.Count == 0)
            {
                return new Models.FetchExecutionResult
                {
                    Outcome = Models.FetchOutcomeKind.RemoteNotFound,
                    Summary = "当前仓库没有可获取的远端配置。",
                };
            }

            await using var guard = await Models.RepositoryWriteGuard
                .TryEnterAsync(repoPath, "Fetch", checkIndexLock: false, branchNameToUpdate: null, cancellationToken)
                .ConfigureAwait(false);

            if (!guard.Acquired)
            {
                return new Models.FetchExecutionResult
                {
                    Outcome = Models.FetchOutcomeKind.RepositoryLocked,
                    Summary = guard.LockDetection.Summary,
                    DiagnosticDetail = guard.LockDetection.Summary,
                };
            }

            var fetchedNames = new List<string>(remotes.Count);
            var combinedDiag = new StringBuilder();
            var updatedAny = false;

            foreach (var remote in remotes)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    return new Models.FetchExecutionResult
                    {
                        Outcome = Models.FetchOutcomeKind.Canceled,
                        FetchedRemotes = fetchedNames,
                        Summary = "获取操作已取消。",
                        DiagnosticDetail = combinedDiag.ToString(),
                    };
                }

                var args = new StringBuilder("fetch --verbose --progress ");
                if (noTags)
                    args.Append("--no-tags ");
                if (force)
                    args.Append("--force ");
                args.Append("-- ").Append(CommandArgumentHelper.Quote(remote.Name));

                var capture = new CapturingCommandLog(log);
                var cmd = new SyncCommand(repoPath, args.ToString(), remote.PrivateSSHKey)
                {
                    RaiseError = false,
                };

                cmd.WithCancellation(cancellationToken).Use(capture);
                var ok = await cmd.ExecAsync().ConfigureAwait(false);
                var diag = capture.GetCombinedText();
                if (!string.IsNullOrEmpty(diag))
                    combinedDiag.AppendLine(diag);

                if (cancellationToken.IsCancellationRequested)
                {
                    return new Models.FetchExecutionResult
                    {
                        Outcome = Models.FetchOutcomeKind.Canceled,
                        FetchedRemotes = fetchedNames,
                        Summary = "获取操作已取消。",
                        DiagnosticDetail = combinedDiag.ToString(),
                    };
                }

                if (!ok)
                {
                    var outcome = ClassifyFetchFailure(diag);
                    return new Models.FetchExecutionResult
                    {
                        Outcome = outcome,
                        FetchedRemotes = fetchedNames,
                        UpdatedAnyRefs = updatedAny,
                        Summary = FormatFetchFailureSummary(remote.Name, outcome, diag),
                        DiagnosticDetail = combinedDiag.ToString(),
                    };
                }

                fetchedNames.Add(remote.Name);
                if (!diag.Contains("up to date", StringComparison.OrdinalIgnoreCase) &&
                    diag.Contains("->", StringComparison.Ordinal))
                {
                    updatedAny = true;
                }
            }

            return new Models.FetchExecutionResult
            {
                Outcome = Models.FetchOutcomeKind.Success,
                FetchedRemotes = fetchedNames,
                FetchedAtUtc = DateTimeOffset.UtcNow,
                UpdatedAnyRefs = updatedAny,
                Summary = fetchedNames.Count == 1
                    ? $"已从 {fetchedNames[0]} 获取远端最新引用。"
                    : $"已从 {fetchedNames.Count} 个远端获取最新引用。",
                DiagnosticDetail = combinedDiag.ToString().Trim(),
            };
        }

        public static async Task<Models.PullExecutionResult> PullFastForwardAsync(
            string repoPath,
            Models.Remote remote,
            string remoteBranchName,
            Models.ICommandLog log = null,
            CancellationToken cancellationToken = default,
            Models.OperationJournalStore journalStore = null)
        {
            var result = await PullFastForwardCoreAsync(repoPath, remote, remoteBranchName, log, cancellationToken).ConfigureAwait(false);
            if (journalStore != null)
            {
                var stage = result.Succeeded
                    ? Models.GitOperationStage.Succeeded
                    : (result.Outcome == Models.PullOutcomeKind.Canceled
                        ? Models.GitOperationStage.CanceledUncertain
                        : Models.GitOperationStage.Failed);

                await TryRecordOperationAsync(
                    repoPath,
                    journalStore,
                    Models.GitOperationKind.Pull,
                    "快进拉取 (Pull --ff-only)",
                    stage,
                    result.Summary,
                    branchName: result.LocalBranchName,
                    remoteName: !string.IsNullOrWhiteSpace(result.RemoteName) ? result.RemoteName : remote?.Name,
                    remoteBranchName: !string.IsNullOrWhiteSpace(result.RemoteBranchName) ? result.RemoteBranchName : remoteBranchName,
                    originalTipSha: result.BeforeHead,
                    targetTipSha: result.AfterHead,
                    commandSummary: $"git pull --ff-only {remote?.Name} {remoteBranchName}".Trim(),
                    diagnosticDetail: result.DiagnosticDetail).ConfigureAwait(false);
            }

            return result;
        }

        private static async Task<Models.PullExecutionResult> PullFastForwardCoreAsync(
            string repoPath,
            Models.Remote remote,
            string remoteBranchName,
            Models.ICommandLog log = null,
            CancellationToken cancellationToken = default)
        {
            if (remote == null || string.IsNullOrWhiteSpace(remote.Name))
            {
                return new Models.PullExecutionResult
                {
                    Outcome = Models.PullOutcomeKind.NoUpstreamConfigured,
                    Summary = "未配置或未选择要拉取的远端。",
                };
            }

            if (string.IsNullOrWhiteSpace(remoteBranchName))
            {
                return new Models.PullExecutionResult
                {
                    Outcome = Models.PullOutcomeKind.NoUpstreamConfigured,
                    RemoteName = remote.Name,
                    Summary = "未指定远端目标分支，无法执行快进拉取。",
                };
            }

            var snapshot = await QueryStagedSnapshot.CaptureAsync(repoPath, cancellationToken).ConfigureAwait(false);
            if (snapshot.IsDetachedHead)
            {
                return new Models.PullExecutionResult
                {
                    Outcome = Models.PullOutcomeKind.DetachedHead,
                    RemoteName = remote.Name,
                    RemoteBranchName = remoteBranchName,
                    Summary = "当前处于游离 HEAD 状态，请先切换到本地分支后再拉取。",
                };
            }

            await using var guard = await Models.RepositoryWriteGuard
                .TryEnterAsync(repoPath, "Pull", checkIndexLock: true, branchNameToUpdate: snapshot.CurrentBranchName, cancellationToken)
                .ConfigureAwait(false);

            if (!guard.Acquired)
            {
                return new Models.PullExecutionResult
                {
                    Outcome = Models.PullOutcomeKind.RepositoryLocked,
                    RemoteName = remote.Name,
                    RemoteBranchName = remoteBranchName,
                    LocalBranchName = snapshot.CurrentBranchName,
                    Summary = guard.LockDetection.Summary,
                    DiagnosticDetail = guard.LockDetection.Summary,
                };
            }

            var beforeHead = snapshot.HeadOid;
            var capture = new CapturingCommandLog(log);
            var cmd = new Pull(repoPath, remote, remoteBranchName, fastForwardOnly: true)
            {
                RaiseError = false,
            };

            cmd.WithCancellation(cancellationToken).Use(capture);
            var ok = await cmd.ExecAsync().ConfigureAwait(false);
            var diag = capture.GetCombinedText();

            if (cancellationToken.IsCancellationRequested)
            {
                return new Models.PullExecutionResult
                {
                    Outcome = Models.PullOutcomeKind.Canceled,
                    RemoteName = remote.Name,
                    RemoteBranchName = remoteBranchName,
                    LocalBranchName = snapshot.CurrentBranchName,
                    BeforeHead = beforeHead,
                    Summary = "拉取操作已取消。",
                    DiagnosticDetail = diag,
                };
            }

            var afterSnapshot = await QueryStagedSnapshot.CaptureAsync(repoPath, CancellationToken.None).ConfigureAwait(false);
            var remoteTrackingRef = $"refs/remotes/{remote.Name}/{remoteBranchName}";

            if (ok)
            {
                var updated = !string.Equals(beforeHead, afterSnapshot.HeadOid, StringComparison.Ordinal);
                return new Models.PullExecutionResult
                {
                    Outcome = updated ? Models.PullOutcomeKind.UpdatedFastForward : Models.PullOutcomeKind.AlreadyUpToDate,
                    RemoteName = remote.Name,
                    RemoteBranchName = remoteBranchName,
                    RemoteTrackingRef = remoteTrackingRef,
                    LocalBranchName = snapshot.CurrentBranchName,
                    BeforeHead = beforeHead,
                    AfterHead = afterSnapshot.HeadOid,
                    Summary = updated
                        ? $"已快进拉取更新至 {remote.Name}/{remoteBranchName}。"
                        : $"当前分支已是最新（未发生分叉或额外提交，无需快进）。",
                    DiagnosticDetail = diag,
                };
            }

            // Inspect divergence between HEAD and fetched remote branch or FETCH_HEAD.
            var (hasCounts, ahead, behind) = await QueryStagedSnapshot
                .TryGetDivergenceCountsAsync(repoPath, "HEAD", remoteTrackingRef, CancellationToken.None)
                .ConfigureAwait(false);

            if (!hasCounts)
            {
                (hasCounts, ahead, behind) = await QueryStagedSnapshot
                    .TryGetDivergenceCountsAsync(repoPath, "HEAD", "FETCH_HEAD", CancellationToken.None)
                    .ConfigureAwait(false);
            }

            if (IsLocalChangesConflict(diag))
            {
                return new Models.PullExecutionResult
                {
                    Outcome = Models.PullOutcomeKind.LocalChangesConflict,
                    RemoteName = remote.Name,
                    RemoteBranchName = remoteBranchName,
                    RemoteTrackingRef = remoteTrackingRef,
                    LocalBranchName = snapshot.CurrentBranchName,
                    BeforeHead = beforeHead,
                    AfterHead = afterSnapshot.HeadOid,
                    LocalAheadCount = ahead,
                    RemoteBehindCount = behind,
                    BlockingPaths = ExtractBlockingPaths(diag),
                    Summary = "工作区存在将被远程快进更新覆盖的未提交修改，已阻止覆盖并保持当前工作区原样。",
                    DiagnosticDetail = diag,
                };
            }

            if ((hasCounts && ahead > 0 && behind > 0) || IsFastForwardRejectedMessage(diag))
            {
                var countsText = hasCounts
                    ? $"（本地领先 {ahead} 个提交，远端领先 {behind} 个提交）"
                    : string.Empty;

                return new Models.PullExecutionResult
                {
                    Outcome = Models.PullOutcomeKind.Diverged,
                    RemoteName = remote.Name,
                    RemoteBranchName = remoteBranchName,
                    RemoteTrackingRef = remoteTrackingRef,
                    LocalBranchName = snapshot.CurrentBranchName,
                    BeforeHead = beforeHead,
                    AfterHead = afterSnapshot.HeadOid,
                    LocalAheadCount = ahead,
                    RemoteBehindCount = behind,
                    Summary = $"本地与远端都有新提交{countsText}，默认仅接受快进拉取。请进入明确合并流程，或交给外部工具处理。",
                    DiagnosticDetail = diag,
                };
            }

            var kind = ClassifyPullFailure(diag);
            return new Models.PullExecutionResult
            {
                Outcome = kind,
                RemoteName = remote.Name,
                RemoteBranchName = remoteBranchName,
                RemoteTrackingRef = remoteTrackingRef,
                LocalBranchName = snapshot.CurrentBranchName,
                BeforeHead = beforeHead,
                AfterHead = afterSnapshot.HeadOid,
                LocalAheadCount = ahead,
                RemoteBehindCount = behind,
                Summary = FormatPullFailureSummary(remote.Name, remoteBranchName, kind, diag),
                DiagnosticDetail = diag,
            };
        }

        public static async Task<Models.PushExecutionResult> PushSingleBranchAsync(
            string repoPath,
            Models.PushExecutionOptions options,
            Models.ICommandLog log = null,
            CancellationToken cancellationToken = default,
            Models.OperationJournalStore journalStore = null)
        {
            var result = await PushSingleBranchCoreAsync(repoPath, options, log, cancellationToken).ConfigureAwait(false);
            if (journalStore != null)
            {
                var stage = result.Succeeded
                    ? Models.GitOperationStage.Succeeded
                    : (result.Outcome == Models.PushOutcomeKind.Canceled
                        ? Models.GitOperationStage.CanceledUncertain
                        : Models.GitOperationStage.Failed);

                await TryRecordOperationAsync(
                    repoPath,
                    journalStore,
                    Models.GitOperationKind.Push,
                    "单分支推送 (Push)",
                    stage,
                    result.Summary,
                    branchName: !string.IsNullOrWhiteSpace(result.LocalBranch) ? result.LocalBranch : options?.LocalBranch,
                    remoteName: !string.IsNullOrWhiteSpace(result.RemoteName) ? result.RemoteName : options?.Remote?.Name,
                    remoteBranchName: !string.IsNullOrWhiteSpace(result.RemoteBranch) ? result.RemoteBranch : options?.RemoteBranch,
                    originalTipSha: result.HeadSha,
                    targetTipSha: result.Succeeded ? result.HeadSha : string.Empty,
                    commandSummary: $"git push {options?.Remote?.Name} {result.Refspec}".Trim(),
                    diagnosticDetail: result.DiagnosticDetail).ConfigureAwait(false);
            }

            return result;
        }

        private static async Task<Models.PushExecutionResult> PushSingleBranchCoreAsync(
            string repoPath,
            Models.PushExecutionOptions options,
            Models.ICommandLog log = null,
            CancellationToken cancellationToken = default)
        {
            if (options?.Remote == null || string.IsNullOrWhiteSpace(options.Remote.Name))
            {
                return new Models.PushExecutionResult
                {
                    Outcome = Models.PushOutcomeKind.RemoteNotFound,
                    Summary = "未选择目标远端仓库。",
                };
            }

            if (string.IsNullOrWhiteSpace(options.LocalBranch) ||
                string.IsNullOrWhiteSpace(options.RemoteBranch) ||
                options.LocalBranch.Contains(':', StringComparison.Ordinal) ||
                options.RemoteBranch.Contains(':', StringComparison.Ordinal))
            {
                return new Models.PushExecutionResult
                {
                    Outcome = Models.PushOutcomeKind.DetachedHead,
                    RemoteName = options.Remote.Name,
                    Summary = "必须指定有效的本地分支和单个远端分支名称，不允许隐式批量推送。",
                };
            }

            var sourceRevision = string.IsNullOrEmpty(options.SourceRevisionSha)
                ? $"refs/heads/{options.LocalBranch}"
                : CommandArgumentHelper.ValidateNonOptionToken(options.SourceRevisionSha, nameof(options.SourceRevisionSha));
            var refspec = $"{sourceRevision}:refs/heads/{options.RemoteBranch}";

            var snapshot = await QueryStagedSnapshot.CaptureAsync(repoPath, cancellationToken).ConfigureAwait(false);
            if (snapshot.IsDetachedHead)
            {
                return new Models.PushExecutionResult
                {
                    Outcome = Models.PushOutcomeKind.DetachedHead,
                    RemoteName = options.Remote.Name,
                    LocalBranch = options.LocalBranch,
                    RemoteBranch = options.RemoteBranch,
                    Refspec = refspec,
                    Summary = "当前处于游离 HEAD 状态，无法作为普通单分支推送。",
                };
            }

            if (snapshot.IsUnborn)
            {
                return new Models.PushExecutionResult
                {
                    Outcome = Models.PushOutcomeKind.GitError,
                    RemoteName = options.Remote.Name,
                    LocalBranch = options.LocalBranch,
                    RemoteBranch = options.RemoteBranch,
                    Refspec = refspec,
                    Summary = "当前分支尚未创建任何提交，无法推送到远端。",
                };
            }

            await using var guard = await Models.RepositoryWriteGuard
                .TryEnterAsync(repoPath, "Push", checkIndexLock: false, branchNameToUpdate: null, cancellationToken)
                .ConfigureAwait(false);

            if (!guard.Acquired)
            {
                return new Models.PushExecutionResult
                {
                    Outcome = Models.PushOutcomeKind.RepositoryLocked,
                    RemoteName = options.Remote.Name,
                    LocalBranch = options.LocalBranch,
                    RemoteBranch = options.RemoteBranch,
                    Refspec = refspec,
                    HeadSha = snapshot.HeadOid,
                    Summary = guard.LockDetection.Summary,
                    DiagnosticDetail = guard.LockDetection.Summary,
                };
            }

            // When creating upstream tracking, guard against unexpectedly matching an existing unrelated remote branch.
            var pushTarget = await RemotePushTarget.ResolveAsync(repoPath, options.Remote.Name, cancellationToken).ConfigureAwait(false);
            if (!pushTarget.IsValid)
                return new Models.PushExecutionResult
                {
                    Outcome = Models.PushOutcomeKind.GitError,
                    RemoteName = options.Remote.Name,
                    Summary = pushTarget.Error,
                };

            if (options.SetUpstream && !options.ConfirmOverwriteExistingRemoteBranchWithoutUpstream)
            {
                var lsArgs = $"ls-remote --heads -- {CommandArgumentHelper.Quote(pushTarget.Url)} {CommandArgumentHelper.Quote($"refs/heads/{options.RemoteBranch}")}";
                var lsCmd = new SyncCommand(repoPath, lsArgs, options.Remote.PrivateSSHKey)
                    .WithCancellation(cancellationToken);

                var lsRes = await ((SyncCommand)lsCmd).RunReadToEndAsync().ConfigureAwait(false);
                if (cancellationToken.IsCancellationRequested)
                {
                    return new Models.PushExecutionResult
                    {
                        Outcome = Models.PushOutcomeKind.Canceled,
                        RemoteName = options.Remote.Name,
                        LocalBranch = options.LocalBranch,
                        RemoteBranch = options.RemoteBranch,
                        Refspec = refspec,
                        HeadSha = snapshot.HeadOid,
                        Summary = "推送操作已取消。",
                    };
                }

                if (!lsRes.IsSuccess)
                {
                    var preflightDiag = lsRes.StdErr;
                    var preflightOutcome = ClassifyPushFailure(preflightDiag);
                    return new Models.PushExecutionResult
                    {
                        Outcome = preflightOutcome,
                        RemoteName = options.Remote.Name,
                        LocalBranch = options.LocalBranch,
                        RemoteBranch = options.RemoteBranch,
                        Refspec = refspec,
                        HeadSha = snapshot.HeadOid,
                        Summary = FormatPushFailureSummary(options.Remote.Name, options.RemoteBranch, preflightOutcome, preflightDiag),
                        DiagnosticDetail = preflightDiag,
                    };
                }

                var targetFullRef = $"refs/heads/{options.RemoteBranch}";
                if (!string.IsNullOrWhiteSpace(lsRes.StdOut) &&
                    lsRes.StdOut.Contains(targetFullRef, StringComparison.Ordinal))
                {
                    return new Models.PushExecutionResult
                    {
                        Outcome = Models.PushOutcomeKind.RemoteBranchExistsWithoutUpstream,
                        RemoteName = options.Remote.Name,
                        LocalBranch = options.LocalBranch,
                        RemoteBranch = options.RemoteBranch,
                        Refspec = refspec,
                        HeadSha = snapshot.HeadOid,
                        Summary = $"远端 {options.Remote.Name}/{options.RemoteBranch} 已存在，且当前分支尚未绑定上游；请确认目标分支后再推送，避免误与已有分支混淆。",
                    };
                }
            }

            var pushedSha = await BranchDeletionService.ResolveRefShaAsync(repoPath, $"{sourceRevision}^{{commit}}", cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrEmpty(pushedSha))
                return new Models.PushExecutionResult { Outcome = Models.PushOutcomeKind.GitError, Summary = "要推送的本地提交已不存在，请刷新后重试。" };
            refspec = $"{pushedSha}:refs/heads/{options.RemoteBranch}";
            var oldTrackingSha = await BranchDeletionService.ResolveRefShaAsync(repoPath,
                $"refs/remotes/{options.Remote.Name}/{options.RemoteBranch}", cancellationToken).ConfigureAwait(false);
            var capture = new CapturingCommandLog(log);
            var cmd = new Push(
                repoPath,
                pushedSha,
                options.Remote,
                options.RemoteBranch,
                options.CheckSubmodules,
                options.NoVerify,
                pushTarget.Url)
            {
                RaiseError = false,
            };

            cmd.WithCancellation(cancellationToken).Use(capture);
            var ok = await cmd.ExecAsync().ConfigureAwait(false);
            var diag = capture.GetCombinedText();

            if (cancellationToken.IsCancellationRequested)
            {
                return new Models.PushExecutionResult
                {
                    Outcome = Models.PushOutcomeKind.Canceled,
                    RemoteName = options.Remote.Name,
                    LocalBranch = options.LocalBranch,
                    RemoteBranch = options.RemoteBranch,
                    Refspec = refspec,
                    HeadSha = snapshot.HeadOid,
                    Summary = "推送操作已取消。",
                    DiagnosticDetail = diag,
                };
            }

            if (ok)
            {
                await RemotePushTarget.UpdateTrackingRefAsync(repoPath, options.Remote.Name, options.RemoteBranch,
                    pushedSha, oldTrackingSha, pushTarget.Url, CancellationToken.None).ConfigureAwait(false);
                var upToDate = diag.Contains("Everything up-to-date", StringComparison.OrdinalIgnoreCase);
                var upstreamApplied = false;
                if (options.SetUpstream)
                {
                    var upstream = await new Branch(repoPath, options.LocalBranch).SetUpstreamTargetedAsync(
                        options.Remote.Name, options.RemoteBranch, cancellationToken: cancellationToken, acquireWriteGuard: false).ConfigureAwait(false);
                    upstreamApplied = upstream.IsSuccess;
                    if (!upstreamApplied)
                        diag += $"\n推送成功，但设置上游失败：{Models.GitConfigSnapshot.RedactText(upstream.StdErr)}";
                }
                return new Models.PushExecutionResult
                {
                    Outcome = upToDate ? Models.PushOutcomeKind.UpToDate : Models.PushOutcomeKind.Success,
                    RemoteName = options.Remote.Name,
                    LocalBranch = options.LocalBranch,
                    RemoteBranch = options.RemoteBranch,
                    Refspec = refspec,
                    SetUpstreamApplied = upstreamApplied,
                    HeadSha = pushedSha,
                    Summary = options.SetUpstream && !upstreamApplied
                        ? $"已推送分支 {options.LocalBranch} 至 {options.Remote.Name}/{options.RemoteBranch}，但设置上游失败，请在仓库配置中重试。"
                        : upToDate
                            ? $"远端 {options.Remote.Name}/{options.RemoteBranch} 已是最新。"
                            : string.IsNullOrEmpty(options.SourceRevisionSha)
                                ? $"已推送分支 {options.LocalBranch} 至 {options.Remote.Name}/{options.RemoteBranch}。"
                                : $"已推送提交 {pushedSha[..10]} 至 {options.Remote.Name}/{options.RemoteBranch}。",
                    DiagnosticDetail = diag,
                };
            }

            var kind = ClassifyPushFailure(diag);
            return new Models.PushExecutionResult
            {
                Outcome = kind,
                RemoteName = options.Remote.Name,
                LocalBranch = options.LocalBranch,
                RemoteBranch = options.RemoteBranch,
                Refspec = refspec,
                HeadSha = snapshot.HeadOid,
                Summary = FormatPushFailureSummary(options.Remote.Name, options.RemoteBranch, kind, diag),
                DiagnosticDetail = diag,
            };
        }

        public static async Task<Models.CommitAndPushSession> CommitAndPushAsync(
            string repoPath,
            Models.CommitExecutionOptions commitOptions,
            Models.PushExecutionOptions pushOptions,
            Models.ICommandLog log = null,
            CancellationToken cancellationToken = default,
            Models.OperationJournalStore journalStore = null)
        {
            var commitRes = await CommitAsync(repoPath, commitOptions, log, cancellationToken, journalStore).ConfigureAwait(false);
            if (!commitRes.Succeeded)
            {
                return new Models.CommitAndPushSession
                {
                    RepoPath = repoPath,
                    CommitResult = commitRes,
                    PushAttempted = false,
                    PendingPushOptions = pushOptions,
                };
            }

            if (pushOptions == null || pushOptions.Remote == null)
            {
                return new Models.CommitAndPushSession
                {
                    RepoPath = repoPath,
                    CommitResult = commitRes,
                    PushAttempted = false,
                    PendingPushOptions = pushOptions,
                };
            }

            var pushRes = await PushSingleBranchAsync(repoPath, pushOptions, log, cancellationToken, journalStore).ConfigureAwait(false);
            return new Models.CommitAndPushSession
            {
                RepoPath = repoPath,
                CommitResult = commitRes,
                PushAttempted = true,
                PushResult = pushRes,
                PendingPushOptions = pushRes.Succeeded ? null : pushOptions,
            };
        }

        public static async Task<Models.CommitAndPushSession> RetryPushSessionAsync(
            Models.CommitAndPushSession session,
            Models.PushExecutionOptions overridePushOptions = null,
            Models.ICommandLog log = null,
            CancellationToken cancellationToken = default,
            Models.OperationJournalStore journalStore = null)
        {
            if (session == null || !session.CommitSucceeded)
                throw new InvalidOperationException("只有本地提交成功后才可仅重试推送。");

            var effectivePush = overridePushOptions ?? session.PendingPushOptions;
            var pushRes = await PushSingleBranchAsync(session.RepoPath, effectivePush, log, cancellationToken, journalStore).ConfigureAwait(false);

            return new Models.CommitAndPushSession
            {
                RepoPath = session.RepoPath,
                CommitResult = session.CommitResult,
                PushAttempted = true,
                PushResult = pushRes,
                PendingPushOptions = pushRes.Succeeded ? null : effectivePush,
            };
        }

        private static List<string> NormalizeNonEmptyPaths(IEnumerable<string> paths)
        {
            var list = new List<string>();
            if (paths == null)
                return list;

            var seen = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
            foreach (var raw in paths)
            {
                if (string.IsNullOrWhiteSpace(raw))
                    continue;

                var normalized = raw.Replace('\\', '/');
                if (seen.Add(normalized))
                    list.Add(normalized);
            }

            return list;
        }

        private static Models.CommitFailureKind ClassifyCommitFailure(string diag, Models.CommitIdentity identity)
        {
            if (!identity.IsComplete ||
                ContainsAny(diag, "Author identity unknown", "Please tell me who you are", "unable to auto-detect email address"))
            {
                return Models.CommitFailureKind.MissingCommitIdentity;
            }

            if (ContainsAny(diag, "index.lock", "HEAD.lock", "Unable to create", "Another git process seems to be running"))
                return Models.CommitFailureKind.RepositoryLocked;

            if (ContainsAny(diag, "gpg failed", "failed to sign the data", "signing failed", "no secret key"))
                return Models.CommitFailureKind.SigningFailed;

            if (ContainsAny(diag, "pre-commit", "commit-msg", "prepare-commit-msg", "hook declined", "hook failed"))
                return Models.CommitFailureKind.HookRejected;

            if (ContainsAny(diag, "nothing to commit", "no changes added to commit", "Changes not staged for commit"))
                return Models.CommitFailureKind.NothingStaged;

            return Models.CommitFailureKind.GitError;
        }

        private static string FormatCommitFailureSummary(Models.CommitFailureKind kind, string diag)
        {
            return kind switch
            {
                Models.CommitFailureKind.MissingCommitIdentity => "缺少提交身份（user.name / user.email），请配置后再提交。",
                Models.CommitFailureKind.RepositoryLocked => "仓库正被其他 Git 进程锁定，无法完成提交。",
                Models.CommitFailureKind.SigningFailed => "提交签名失败，请检查 GPG/SSH 签名密钥或配置。",
                Models.CommitFailureKind.HookRejected => "Git 提交钩子（hook）检查未通过，提交已中止。",
                Models.CommitFailureKind.NothingStaged => "暂存区没有可提交的变更。",
                _ => FirstErrorLineOrDefault(diag, "本地提交失败。"),
            };
        }

        private static Models.FetchOutcomeKind ClassifyFetchFailure(string diag)
        {
            if (IsAuthCanceled(diag))
                return Models.FetchOutcomeKind.AuthCanceled;
            if (IsAuthFailed(diag))
                return Models.FetchOutcomeKind.AuthFailed;
            if (IsLockError(diag))
                return Models.FetchOutcomeKind.RepositoryLocked;
            if (IsRemoteNotFound(diag))
                return Models.FetchOutcomeKind.RemoteNotFound;
            if (IsNetworkOrOffline(diag))
                return Models.FetchOutcomeKind.OfflineOrUnreachable;
            return Models.FetchOutcomeKind.GitError;
        }

        private static string FormatFetchFailureSummary(string remoteName, Models.FetchOutcomeKind kind, string diag)
        {
            return kind switch
            {
                Models.FetchOutcomeKind.AuthCanceled => $"从 {remoteName} 获取时取消了凭据输入。",
                Models.FetchOutcomeKind.AuthFailed => $"从 {remoteName} 获取时认证失败，请检查凭据或 SSH 密钥。",
                Models.FetchOutcomeKind.OfflineOrUnreachable => $"无法连接到远端 {remoteName}（网络离线或远端不可达）。",
                Models.FetchOutcomeKind.RemoteNotFound => $"远端 {remoteName} 不存在或路径无效。",
                Models.FetchOutcomeKind.RepositoryLocked => "仓库存在其他 Git 锁文件，获取已中止。",
                _ => FirstErrorLineOrDefault(diag, $"从 {remoteName} 获取失败。"),
            };
        }

        private static Models.PullOutcomeKind ClassifyPullFailure(string diag)
        {
            if (IsAuthCanceled(diag))
                return Models.PullOutcomeKind.AuthCanceled;
            if (IsAuthFailed(diag))
                return Models.PullOutcomeKind.AuthFailed;
            if (IsLockError(diag))
                return Models.PullOutcomeKind.RepositoryLocked;
            if (ContainsAny(diag, "couldn't find remote ref", "no tracking information", "no such ref was fetched", "unable to find remote ref"))
                return Models.PullOutcomeKind.UpstreamGone;
            if (IsRemoteNotFound(diag))
                return Models.PullOutcomeKind.RemoteNotFound;
            if (IsNetworkOrOffline(diag))
                return Models.PullOutcomeKind.OfflineOrUnreachable;
            return Models.PullOutcomeKind.GitError;
        }

        private static string FormatPullFailureSummary(string remoteName, string branchName, Models.PullOutcomeKind kind, string diag)
        {
            return kind switch
            {
                Models.PullOutcomeKind.AuthCanceled => $"从 {remoteName}/{branchName} 拉取时取消了认证。",
                Models.PullOutcomeKind.AuthFailed => $"从 {remoteName}/{branchName} 拉取时认证失败，请检查账号凭据或 SSH 密钥。",
                Models.PullOutcomeKind.UpstreamGone => $"远端分支 {remoteName}/{branchName} 不存在或已被删除。",
                Models.PullOutcomeKind.OfflineOrUnreachable => $"无法连接远端 {remoteName}（网络不可达或已离线）。",
                Models.PullOutcomeKind.RemoteNotFound => $"远端仓库 {remoteName} 不存在或地址无效。",
                Models.PullOutcomeKind.RepositoryLocked => "仓库正被其他 Git 锁文件占用，拉取已中止。",
                _ => FirstErrorLineOrDefault(diag, $"快进拉取 {remoteName}/{branchName} 失败。"),
            };
        }

        private static Models.PushOutcomeKind ClassifyPushFailure(string diag)
        {
            if (IsAuthCanceled(diag))
                return Models.PushOutcomeKind.AuthCanceled;
            if (IsAuthFailed(diag))
                return Models.PushOutcomeKind.AuthFailed;
            if (IsLockError(diag))
                return Models.PushOutcomeKind.RepositoryLocked;
            if (ContainsAny(diag, "pre-receive hook declined", "hook declined", "protected branch", "GH006", "pre-push hook", "denied by", "insufficient permission"))
                return Models.PushOutcomeKind.ProtectedOrHookRejected;
            if (ContainsAny(diag, "non-fast-forward", "fetch first", "tip of your current branch is behind", "[rejected]"))
                return Models.PushOutcomeKind.NonFastForwardRejected;
            if (IsRemoteNotFound(diag))
                return Models.PushOutcomeKind.RemoteNotFound;
            if (IsNetworkOrOffline(diag))
                return Models.PushOutcomeKind.OfflineOrUnreachable;
            return Models.PushOutcomeKind.GitError;
        }

        private static string FormatPushFailureSummary(string remoteName, string remoteBranch, Models.PushOutcomeKind kind, string diag)
        {
            return kind switch
            {
                Models.PushOutcomeKind.AuthCanceled => $"推送至 {remoteName}/{remoteBranch} 时取消了认证。",
                Models.PushOutcomeKind.AuthFailed => $"推送至 {remoteName}/{remoteBranch} 认证失败，请检查账号凭据或 SSH 密钥。",
                Models.PushOutcomeKind.ProtectedOrHookRejected => $"推送至 {remoteName}/{remoteBranch} 被远端受保护分支规则或钩子拒绝。",
                Models.PushOutcomeKind.NonFastForwardRejected => $"远端分支 {remoteName}/{remoteBranch} 已有新提交，普通推送被拒绝，请先拉取/合并后再推送。",
                Models.PushOutcomeKind.OfflineOrUnreachable => $"无法连接远端 {remoteName}（网络离线或目标不可达），本地提交保留完好。",
                Models.PushOutcomeKind.RemoteNotFound => $"远端 {remoteName} 不存在或无权访问该仓库路径。",
                Models.PushOutcomeKind.RepositoryLocked => "仓库被其他 Git 进程锁定，推送未执行。",
                _ => FirstErrorLineOrDefault(diag, $"推送至 {remoteName}/{remoteBranch} 失败。"),
            };
        }

        private static bool IsFastForwardRejectedMessage(string diag)
        {
            return ContainsAny(diag,
                "Not possible to fast-forward",
                "Need to specify how to reconcile divergent branches",
                "diverging branches");
        }

        private static bool IsLocalChangesConflict(string diag)
        {
            return ContainsAny(diag,
                "would be overwritten by merge",
                "would be overwritten by checkout",
                "untracked working tree files would be overwritten");
        }

        private static bool IsAuthCanceled(string diag)
        {
            return ContainsAny(diag,
                "User canceled",
                "cancelled",
                "canceled",
                "ASKPASS_CANCELLED",
                "prompt disabled");
        }

        private static bool IsAuthFailed(string diag)
        {
            return ContainsAny(diag,
                "Authentication failed",
                "could not read Username",
                "could not read Password",
                "Permission denied (publickey)",
                "Invalid username or token",
                "Invalid username or password",
                "The requested URL returned error: 401",
                "The requested URL returned error: 403",
                "fatal: HTTPBasic: Access denied",
                "Host key verification failed");
        }

        private static bool IsLockError(string diag)
        {
            return ContainsAny(diag, ".lock': File exists", "Unable to create", "Another git process seems to be running");
        }

        private static bool IsRemoteNotFound(string diag)
        {
            return ContainsAny(diag,
                "does not appear to be a git repository",
                "Repository not found",
                "No such remote",
                "fatal: Could not read from remote repository") &&
                !ContainsAny(diag, "Permission denied", "Could not resolve host");
        }

        private static bool IsNetworkOrOffline(string diag)
        {
            return ContainsAny(diag,
                "Could not resolve host",
                "Failed to connect",
                "Connection timed out",
                "Operation timed out",
                "Network is unreachable",
                "Name or service not known",
                "Temporary failure in name resolution",
                "unable to access");
        }

        private static IReadOnlyList<string> ExtractBlockingPaths(string diag)
        {
            var paths = new List<string>();
            if (string.IsNullOrWhiteSpace(diag))
                return paths;

            var captureNextIndented = false;
            foreach (var rawLine in diag.Split(['\r', '\n'], StringSplitOptions.None))
            {
                if (rawLine.Contains("would be overwritten", StringComparison.OrdinalIgnoreCase))
                {
                    captureNextIndented = true;
                    continue;
                }

                if (!captureNextIndented)
                    continue;

                if (rawLine.StartsWith('\t') || rawLine.StartsWith("        ", StringComparison.Ordinal))
                {
                    var trimmed = rawLine.Trim();
                    if (!string.IsNullOrEmpty(trimmed))
                        paths.Add(trimmed);
                }
                else if (paths.Count > 0)
                {
                    break;
                }
            }

            return paths;
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

        private static string FirstErrorLineOrDefault(string diag, string fallback)
        {
            if (string.IsNullOrWhiteSpace(diag))
                return fallback;

            foreach (var line in diag.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
            {
                var trimmed = line.Trim();
                if (trimmed.StartsWith("$ git", StringComparison.Ordinal))
                    continue;
                if (trimmed.StartsWith("fatal:", StringComparison.OrdinalIgnoreCase) ||
                    trimmed.StartsWith("error:", StringComparison.OrdinalIgnoreCase))
                    return trimmed;
            }

            foreach (var line in diag.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
            {
                var trimmed = line.Trim();
                if (!trimmed.StartsWith("$ git", StringComparison.Ordinal))
                    return trimmed;
            }

            return fallback;
        }

        private static async Task TryRecordOperationAsync(
            string repoPath,
            Models.OperationJournalStore journalStore,
            Models.GitOperationKind kind,
            string title,
            Models.GitOperationStage stage,
            string summary,
            string branchName = null,
            string remoteName = null,
            string remoteBranchName = null,
            string originalTipSha = null,
            string targetTipSha = null,
            string commandSummary = null,
            string diagnosticDetail = null)
        {
            if (journalStore == null || string.IsNullOrWhiteSpace(repoPath))
                return;

            try
            {
                var (_, commonDir) = await Models.RepositoryWriteGuard
                    .ResolveGitDirectoriesAsync(repoPath, CancellationToken.None)
                    .ConfigureAwait(false);

                OperationJournalService.RecordCompletedOperation(
                    repoPath: repoPath,
                    commonDir: commonDir,
                    kind: kind,
                    title: title,
                    stage: stage,
                    summary: summary,
                    commandLogText: commandSummary,
                    branchName: branchName,
                    remoteName: remoteName,
                    remoteBranchName: remoteBranchName,
                    originalTipSha: originalTipSha,
                    targetTipSha: targetTipSha,
                    journalStore: journalStore,
                    diagnosticDetail: diagnosticDetail);
            }
            catch
            {
                // Keep sync operations non-fatal if journal persistence fails.
            }
        }
    }
}
