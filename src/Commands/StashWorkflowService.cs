using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace SourceGit.Commands
{
    /// <summary>
    /// Transactional, collision-aware Stash and branch-switch preservation service.
    /// Keeps stashes on apply conflict (never dropping or auto-aborting) and blocks blind
    /// overwrites against dirty working trees or colliding untracked/ignored files.
    /// </summary>
    public static partial class StashWorkflowService
    {
        private static readonly StringComparer PathComparer = OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;

        [GeneratedRegex("^[0-9a-fA-F]{40,64}$", RegexOptions.Compiled)]
        private static partial Regex HexOidRegex();

        public static async Task<Models.StashPushPreflight> InspectPushAsync(
            string repoPath,
            IReadOnlyList<Models.Change> selectedChanges = null,
            bool includeUntracked = true,
            bool stagedOnly = false,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(repoPath) || !Directory.Exists(repoPath))
            {
                return new Models.StashPushPreflight
                {
                    CanProceed = false,
                    BlockingReason = "仓库路径不存在或不可访问。",
                };
            }

            var hasHead = await HasValidHeadAsync(repoPath, cancellationToken).ConfigureAwait(false);
            if (!hasHead)
            {
                return new Models.StashPushPreflight
                {
                    CanProceed = false,
                    IsUnbornHead = true,
                    BlockingReason = "当前仓库尚无首次提交（unborn HEAD），请先创建初始提交后再暂存修改。",
                    Summary = "Unborn HEAD 仓库不支持 git stash。",
                };
            }

            var status = await InspectStatusWithIgnoredAsync(repoPath, cancellationToken).ConfigureAwait(false);
            if (status.ConflictedPaths.Count > 0 || await HasInProgressMarkerAsync(repoPath, cancellationToken).ConfigureAwait(false))
            {
                return new Models.StashPushPreflight
                {
                    CanProceed = false,
                    HasInProgressConflict = true,
                    BlockingReason = "当前仓库存在未解决的冲突或进行中操作（MERGE/REVERT/REBASE 等），请先处理完毕后再创建暂存。",
                    Summary = $"检测到 {status.ConflictedPaths.Count} 个未解决冲突文件。",
                };
            }

            var selectedSet = selectedChanges is { Count: > 0 }
                ? new HashSet<string>(selectedChanges.Select(c => NormalizeRelPath(c.Path)), PathComparer)
                : null;

            var localChanges = await new QueryLocalChanges(repoPath).GetResultAsync().ConfigureAwait(false);
            var preview = new List<Models.StashPushPreviewItem>(localChanges.Count);
            var includedCount = 0;
            var untrackedIncludedCount = 0;

            foreach (var c in localChanges)
            {
                var relPath = NormalizeRelPath(c.Path);
                if (selectedSet != null && !selectedSet.Contains(relPath))
                    continue;

                var isUntracked = c.WorkTree == Models.ChangeState.Untracked || c.Index == Models.ChangeState.Untracked;
                var hasStaged = c.Index != Models.ChangeState.None && c.Index != Models.ChangeState.Untracked;

                var included = stagedOnly
                    ? hasStaged
                    : (!isUntracked || includeUntracked);

                if (included)
                {
                    includedCount++;
                    if (isUntracked)
                        untrackedIncludedCount++;
                }

                preview.Add(new Models.StashPushPreviewItem
                {
                    Path = relPath,
                    Index = c.Index,
                    WorkTree = c.WorkTree,
                    IsUntracked = isUntracked,
                    Included = included,
                });
            }

            var hasChanges = includedCount > 0;
            var blocking = hasChanges
                ? string.Empty
                : stagedOnly
                    ? "当前暂存区没有已暂存的修改可供保存。"
                    : "没有符合当前暂存范围的可保存修改。";

            var sampleIgnored = status.IgnoredPaths.Take(8).ToList();
            var summary = !hasChanges
                ? blocking
                : status.IgnoredPaths.Count > 0
                    ? $"将暂存 {includedCount} 项修改（其中未跟踪 {untrackedIncludedCount} 项）；另有 {status.IgnoredPaths.Count} 个被忽略文件不会被纳入暂存。"
                    : $"将暂存 {includedCount} 项修改（其中未跟踪 {untrackedIncludedCount} 项）。";

            return new Models.StashPushPreflight
            {
                CanProceed = hasChanges,
                IsUnbornHead = false,
                HasInProgressConflict = false,
                HasChangesToStash = hasChanges,
                BlockingReason = blocking,
                Summary = summary,
                CandidateFiles = preview,
                IncludedCount = includedCount,
                UntrackedIncludedCount = untrackedIncludedCount,
                IgnoredFileCount = status.IgnoredPaths.Count,
                IgnoredSamplePaths = sampleIgnored,
            };
        }

        public static async Task<Models.StashPushResult> ExecutePushAsync(
            string repoPath,
            string message,
            IReadOnlyList<Models.Change> selectedChanges = null,
            bool includeUntracked = true,
            bool keepIndex = false,
            bool keepAll = false,
            bool stagedOnly = false,
            Models.OperationJournalStore journalStore = null,
            Models.ICommandLog outerLog = null,
            CancellationToken cancellationToken = default)
        {
            var preflight = await InspectPushAsync(repoPath, selectedChanges, includeUntracked, stagedOnly, cancellationToken)
                .ConfigureAwait(false);
            if (!preflight.CanProceed)
            {
                return new Models.StashPushResult
                {
                    Success = false,
                    WasNoOp = !preflight.IsUnbornHead && !preflight.HasInProgressConflict && !preflight.HasChangesToStash,
                    Message = preflight.BlockingReason,
                };
            }

            var cleanMessage = SanitizeStashMessage(message);
            var targetPaths = selectedChanges is { Count: > 0 }
                ? preflight.CandidateFiles.Where(f => f.Included).Select(f => f.Path).ToList()
                : [];

            var exec = await OperationJournalService.ExecuteGuardedWriteAsync(
                repoPath: repoPath,
                kind: Models.GitOperationKind.Stash,
                title: string.IsNullOrWhiteSpace(cleanMessage) ? "暂存工作区修改" : $"暂存修改：{cleanMessage}",
                action: async ctx =>
                {
                    var beforeStashes = await new QueryStashes(repoPath).GetResultAsync().ConfigureAwait(false);
                    var beforeTopSha = beforeStashes.FirstOrDefault()?.SHA ?? string.Empty;

                    var args = new List<string>();
                    byte[] stdinBytes = null;

                    if (targetPaths.Count > 0)
                        args.Add("--literal-pathspecs");

                    args.Add("stash");
                    args.Add("push");

                    if (stagedOnly)
                    {
                        args.Add("--staged");
                    }
                    else
                    {
                        if (includeUntracked)
                            args.Add("--include-untracked");
                    }

                    if (keepIndex || keepAll)
                        args.Add("--keep-index");

                    if (!string.IsNullOrEmpty(cleanMessage))
                    {
                        args.Add("-m");
                        args.Add(cleanMessage);
                    }

                    if (targetPaths.Count > 0)
                    {
                        args.Add("--pathspec-from-file=-");
                        args.Add("--pathspec-file-nul");
                        stdinBytes = CommandArgumentHelper.BuildNulPathspecBytes(targetPaths);
                    }

                    ctx.Log.AppendLine($"$ git {string.Join(" ", args.Select(CommandArgumentHelper.Quote))}");
                    var pushRes = await Command.RunGitDirectWithInputAsync(
                        repoPath,
                        TimeSpan.FromSeconds(60),
                        stdinBytes,
                        cancellationToken,
                        args.ToArray()).ConfigureAwait(false);

                    AppendCommandStreams(ctx.Log, pushRes);
                    if (!pushRes.IsSuccess)
                    {
                        return new OperationActionOutcome<Models.StashPushResult>
                        {
                            Stage = Models.GitOperationStage.Failed,
                            Summary = CombineError(pushRes, "执行 git stash push 失败。"),
                            Payload = new Models.StashPushResult
                            {
                                Success = false,
                                Message = CombineError(pushRes, "执行 git stash push 失败。"),
                                OperationRecordId = ctx.OperationId,
                            },
                        };
                    }

                    var afterStashes = await new QueryStashes(repoPath).GetResultAsync().ConfigureAwait(false);
                    var created = afterStashes.FirstOrDefault();
                    if (created == null || string.Equals(created.SHA, beforeTopSha, StringComparison.Ordinal))
                    {
                        return new OperationActionOutcome<Models.StashPushResult>
                        {
                            Stage = Models.GitOperationStage.Succeeded,
                            Summary = "没有可生成的新暂存条目。",
                            Payload = new Models.StashPushResult
                            {
                                Success = true,
                                WasNoOp = true,
                                Message = "没有检测到实际变化，未生成新的 stash。",
                                OperationRecordId = ctx.OperationId,
                            },
                        };
                    }

                    if (keepAll)
                    {
                        ctx.Log.AppendLine($"$ git stash apply -q --index {created.SHA}");
                        var reapplyRes = await Command.RunGitDirectAsync(
                            repoPath,
                            TimeSpan.FromSeconds(30),
                            cancellationToken,
                            "stash",
                            "apply",
                            "-q",
                            "--index",
                            created.SHA).ConfigureAwait(false);
                        AppendCommandStreams(ctx.Log, reapplyRes);

                        if (!reapplyRes.IsSuccess)
                        {
                            return new OperationActionOutcome<Models.StashPushResult>
                            {
                                Stage = Models.GitOperationStage.PartialSuccess,
                                TargetTipSha = created.SHA,
                                Summary = $"已创建暂存 {ShortSha(created.SHA)}，但回填工作区保留改动失败。",
                                DiagnosticDetail = CombineError(reapplyRes, "stash apply failed"),
                                Payload = new Models.StashPushResult
                                {
                                    Success = false,
                                    CreatedStashSha = created.SHA,
                                    CreatedStashName = created.Name,
                                    Message = $"暂存已保存为 {created.Name}（{ShortSha(created.SHA)}），但保留工作区回填失败：{CombineError(reapplyRes, "")}",
                                    OperationRecordId = ctx.OperationId,
                                },
                            };
                        }
                    }

                    return new OperationActionOutcome<Models.StashPushResult>
                    {
                        Stage = Models.GitOperationStage.Succeeded,
                        TargetTipSha = created.SHA,
                        Summary = $"已创建暂存 {created.Name}（{ShortSha(created.SHA)}）。",
                        Payload = new Models.StashPushResult
                        {
                            Success = true,
                            CreatedStashSha = created.SHA,
                            CreatedStashName = created.Name,
                            Message = $"已创建暂存 {created.Name}（{ShortSha(created.SHA)}）。",
                            OperationRecordId = ctx.OperationId,
                        },
                    };
                },
                journalStore: journalStore,
                outerLog: outerLog,
                cancellationToken: cancellationToken).ConfigureAwait(false);

            return exec.Payload ?? new Models.StashPushResult
            {
                Success = exec.Succeeded,
                Message = exec.Summary,
                OperationRecordId = exec.Record?.OperationId ?? string.Empty,
            };
        }

        public static async Task<Models.StashApplyPreflight> InspectApplyAsync(
            string repoPath,
            Models.Stash stash,
            bool requireCleanWorkingTree = true,
            CancellationToken cancellationToken = default)
        {
            if (stash == null || (string.IsNullOrWhiteSpace(stash.Name) && string.IsNullOrWhiteSpace(stash.SHA)))
            {
                return new Models.StashApplyPreflight
                {
                    CanProceed = false,
                    BlockingReasons = ["未指定要恢复的暂存项。"],
                    Summary = "未指定要恢复的暂存项。",
                };
            }

            var resolved = await ResolveLiveStashAsync(repoPath, stash.Name, stash.SHA, cancellationToken).ConfigureAwait(false);
            if (resolved == null)
            {
                return new Models.StashApplyPreflight
                {
                    CanProceed = false,
                    StashExists = false,
                    StashName = stash.Name,
                    StashSha = stash.SHA,
                    BlockingReasons = ["找不到对应的暂存条目（可能已在外部被删除或索引已失效）。"],
                    Summary = "找不到指定的暂存条目。",
                };
            }

            var blocking = new List<string>();
            var status = await InspectStatusWithIgnoredAsync(repoPath, cancellationToken).ConfigureAwait(false);
            var hasMarkerConflict = await HasInProgressMarkerAsync(repoPath, cancellationToken).ConfigureAwait(false);
            if (status.ConflictedPaths.Count > 0 || hasMarkerConflict)
                blocking.Add("当前仓库正处于冲突或进行中合并/变基状态，请先解决或完成后再应用暂存。");

            var isIndexClean = status.StagedPaths.Count == 0;
            var isWorktreeClean = status.WorktreeDirtyPaths.Count == 0 && status.UntrackedPaths.Count == 0;
            if (requireCleanWorkingTree && (!isIndexClean || status.WorktreeDirtyPaths.Count > 0 || status.UntrackedPaths.Count > 0))
            {
                var dirtyTotal = status.StagedPaths.Count + status.WorktreeDirtyPaths.Count + status.UntrackedPaths.Count;
                blocking.Add($"当前工作区/暂存区并不干净（检测到 {dirtyTotal} 项未提交修改），直接恢复可能导致修改混淆或覆盖。");
            }

            var affected = await ReadStashAffectedPathsAsync(repoPath, resolved, cancellationToken).ConfigureAwait(false);
            var collidingUntracked = status.UntrackedPaths
                .Where(u => affected.Any(a => PathsCollide(u, a)))
                .Distinct(PathComparer)
                .ToList();
            var collidingIgnored = status.IgnoredPaths
                .Where(i => affected.Any(a => PathsCollide(i, a)))
                .Distinct(PathComparer)
                .ToList();

            if (collidingUntracked.Count > 0)
                blocking.Add($"工作区中有 {collidingUntracked.Count} 个未跟踪文件与该暂存恢复路径重名（如 {collidingUntracked[0]}），应用暂存会将其覆盖。");
            if (collidingIgnored.Count > 0)
                blocking.Add($"工作区中有 {collidingIgnored.Count} 个被忽略文件与该暂存恢复路径重名（如 {collidingIgnored[0]}），应用暂存会将其覆盖或触发拒绝。");

            var snapshot = await CaptureOperationSnapshot.CaptureAsync(
                repoPath,
                new Models.OperationSnapshotCaptureOptions(),
                cancellationToken).ConfigureAwait(false);

            var summary = blocking.Count > 0
                ? string.Join(" ", blocking)
                : $"安全预检通过，预计写入或更新 {affected.Count} 个文件。";

            return new Models.StashApplyPreflight
            {
                CanProceed = blocking.Count == 0,
                StashExists = true,
                StashName = resolved.Name,
                StashSha = resolved.SHA,
                StashSubject = resolved.DisplaySubject,
                IsWorkingTreeClean = isWorktreeClean,
                IsIndexClean = isIndexClean,
                HasInProgressConflict = status.ConflictedPaths.Count > 0 || hasMarkerConflict,
                DirtyWorkingTreeCount = status.WorktreeDirtyPaths.Count + status.UntrackedPaths.Count,
                DirtyIndexCount = status.StagedPaths.Count,
                EstimatedAffectedPaths = affected,
                CollidingUntrackedPaths = collidingUntracked,
                CollidingIgnoredPaths = collidingIgnored,
                BlockingReasons = blocking,
                Summary = summary,
                Snapshot = snapshot,
            };
        }

        public static async Task<Models.StashApplyResult> ExecuteApplyAsync(
            string repoPath,
            string stashName,
            string expectedStashSha,
            bool restoreIndex = true,
            bool dropAfterApply = false,
            bool allowDirtyWorkingTree = false,
            Models.OperationConfirmationSnapshot expectedSnapshot = null,
            Models.OperationJournalStore journalStore = null,
            Models.ICommandLog outerLog = null,
            CancellationToken cancellationToken = default)
        {
            var preflight = await InspectApplyAsync(
                repoPath,
                new Models.Stash { Name = stashName ?? string.Empty, SHA = expectedStashSha ?? string.Empty },
                requireCleanWorkingTree: !allowDirtyWorkingTree,
                cancellationToken).ConfigureAwait(false);

            if (!preflight.CanProceed)
            {
                return new Models.StashApplyResult
                {
                    Status = Models.StashApplyStatus.Blocked,
                    StashRetained = preflight.StashExists,
                    StashName = preflight.StashName,
                    StashSha = preflight.StashSha,
                    Summary = preflight.Summary,
                };
            }

            var targetSha = preflight.StashSha;
            var verifyOptions = expectedSnapshot == null
                ? null
                : new Models.OperationSnapshotVerifyOptions
                {
                    RequireSameRepoIdentity = true,
                    RequireSameHead = true,
                    RequireSameInProgressState = true,
                    RequireSameStagedFingerprint = true,
                };

            var title = dropAfterApply
                ? $"弹出应用暂存 {ShortSha(targetSha)}"
                : $"应用暂存 {ShortSha(targetSha)}";

            var exec = await OperationJournalService.ExecuteGuardedWriteAsync(
                repoPath: repoPath,
                kind: Models.GitOperationKind.Stash,
                title: title,
                expectedSnapshot: expectedSnapshot ?? preflight.Snapshot,
                verifyOptions: verifyOptions,
                action: async ctx =>
                {
                    var live = await ResolveLiveStashAsync(repoPath, stashName, targetSha, cancellationToken).ConfigureAwait(false);
                    if (live == null)
                    {
                        return new OperationActionOutcome<Models.StashApplyResult>
                        {
                            Stage = Models.GitOperationStage.Failed,
                            Summary = "执行前暂存条目已消失或失效。",
                            Payload = new Models.StashApplyResult
                            {
                                Status = Models.StashApplyStatus.Failed,
                                StashRetained = false,
                                StashName = stashName ?? string.Empty,
                                StashSha = targetSha,
                                Summary = "执行前暂存条目已消失或失效。",
                                OperationRecordId = ctx.OperationId,
                            },
                        };
                    }

                    ctx.Record.ExpectedPartnerSha = live.SHA;

                    var applyArgs = new List<string> { "stash", "apply" };
                    if (restoreIndex)
                        applyArgs.Add("--index");
                    applyArgs.Add(live.SHA);

                    ctx.Log.AppendLine($"$ git {string.Join(" ", applyArgs)}");
                    var applyRes = await Command.RunGitDirectAsync(
                        repoPath,
                        TimeSpan.FromSeconds(60),
                        cancellationToken,
                        applyArgs.ToArray()).ConfigureAwait(false);
                    AppendCommandStreams(ctx.Log, applyRes);

                    if (!applyRes.IsSuccess)
                    {
                        var conflicts = await ReadUnmergedPathsAsync(repoPath, cancellationToken).ConfigureAwait(false);
                        if (conflicts.Count > 0)
                        {
                            var conflictSummary = $"暂存恢复未完成，检测到 {conflicts.Count} 个文件冲突；保存项 {ShortSha(live.SHA)}（{live.Name}）仍已完整保留，绝未自动丢弃或回滚。";
                            return new OperationActionOutcome<Models.StashApplyResult>
                            {
                                Stage = Models.GitOperationStage.PendingVerification,
                                TargetTipSha = live.SHA,
                                Summary = conflictSummary,
                                DiagnosticDetail = CombineError(applyRes, conflictSummary),
                                Payload = new Models.StashApplyResult
                                {
                                    Status = Models.StashApplyStatus.AppliedWithConflictsRetained,
                                    StashRetained = true,
                                    DropSucceeded = false,
                                    StashName = live.Name,
                                    StashSha = live.SHA,
                                    ConflictedPaths = conflicts,
                                    Summary = conflictSummary,
                                    OperationRecordId = ctx.OperationId,
                                },
                            };
                        }

                        var errMsg = CombineError(applyRes, "应用暂存失败，原保存项仍已保留。");
                        return new OperationActionOutcome<Models.StashApplyResult>
                        {
                            Stage = Models.GitOperationStage.Failed,
                            Summary = errMsg,
                            DiagnosticDetail = errMsg,
                            Payload = new Models.StashApplyResult
                            {
                                Status = Models.StashApplyStatus.Failed,
                                StashRetained = true,
                                DropSucceeded = false,
                                StashName = live.Name,
                                StashSha = live.SHA,
                                Summary = errMsg,
                                OperationRecordId = ctx.OperationId,
                            },
                        };
                    }

                    if (!dropAfterApply)
                    {
                        var okMsg = $"已应用暂存 {ShortSha(live.SHA)}（仍保留原暂存记录）。";
                        return new OperationActionOutcome<Models.StashApplyResult>
                        {
                            Stage = Models.GitOperationStage.Succeeded,
                            TargetTipSha = live.SHA,
                            Summary = okMsg,
                            Payload = new Models.StashApplyResult
                            {
                                Status = Models.StashApplyStatus.CleanApplied,
                                StashRetained = true,
                                DropSucceeded = false,
                                StashName = live.Name,
                                StashSha = live.SHA,
                                Summary = okMsg,
                                OperationRecordId = ctx.OperationId,
                            },
                        };
                    }

                    var refreshedLive = await ResolveLiveStashAsync(repoPath, live.Name, live.SHA, cancellationToken).ConfigureAwait(false);
                    if (refreshedLive == null)
                    {
                        return new OperationActionOutcome<Models.StashApplyResult>
                        {
                            Stage = Models.GitOperationStage.Succeeded,
                            TargetTipSha = live.SHA,
                            Summary = $"已恢复暂存 {ShortSha(live.SHA)}（对应引用已不在栈中）。",
                            Payload = new Models.StashApplyResult
                            {
                                Status = Models.StashApplyStatus.CleanApplied,
                                StashRetained = false,
                                DropSucceeded = true,
                                StashName = live.Name,
                                StashSha = live.SHA,
                                Summary = $"已恢复暂存 {ShortSha(live.SHA)}。",
                                OperationRecordId = ctx.OperationId,
                            },
                        };
                    }

                    ctx.Log.AppendLine($"$ git stash drop -q {refreshedLive.Name}");
                    var dropRes = await Command.RunGitDirectAsync(
                        repoPath,
                        TimeSpan.FromSeconds(15),
                        cancellationToken,
                        "stash",
                        "drop",
                        "-q",
                        refreshedLive.Name).ConfigureAwait(false);
                    AppendCommandStreams(ctx.Log, dropRes);

                    if (!dropRes.IsSuccess)
                    {
                        var warnMsg = $"内容已成功恢复到工作区，但移除暂存条目 {refreshedLive.Name} 失败，该暂存仍被保留。";
                        return new OperationActionOutcome<Models.StashApplyResult>
                        {
                            Stage = Models.GitOperationStage.PartialSuccess,
                            TargetTipSha = live.SHA,
                            Summary = warnMsg,
                            DiagnosticDetail = CombineError(dropRes, warnMsg),
                            Payload = new Models.StashApplyResult
                            {
                                Status = Models.StashApplyStatus.PartialDropFailed,
                                StashRetained = true,
                                DropSucceeded = false,
                                StashName = refreshedLive.Name,
                                StashSha = live.SHA,
                                Summary = warnMsg,
                                OperationRecordId = ctx.OperationId,
                            },
                        };
                    }

                    var popMsg = $"已应用并移除暂存 {refreshedLive.Name}（{ShortSha(live.SHA)}）。";
                    return new OperationActionOutcome<Models.StashApplyResult>
                    {
                        Stage = Models.GitOperationStage.Succeeded,
                        TargetTipSha = live.SHA,
                        Summary = popMsg,
                        Payload = new Models.StashApplyResult
                        {
                            Status = Models.StashApplyStatus.CleanApplied,
                            StashRetained = false,
                            DropSucceeded = true,
                            StashName = refreshedLive.Name,
                            StashSha = live.SHA,
                            Summary = popMsg,
                            OperationRecordId = ctx.OperationId,
                        },
                    };
                },
                journalStore: journalStore,
                outerLog: outerLog,
                cancellationToken: cancellationToken).ConfigureAwait(false);

            if (exec.BlockedBySnapshot || exec.BlockedByLock)
            {
                return new Models.StashApplyResult
                {
                    Status = Models.StashApplyStatus.Blocked,
                    StashRetained = true,
                    StashName = preflight.StashName,
                    StashSha = preflight.StashSha,
                    Summary = exec.Summary,
                    OperationRecordId = exec.Record?.OperationId ?? string.Empty,
                };
            }

            return exec.Payload ?? new Models.StashApplyResult
            {
                Status = exec.Succeeded ? Models.StashApplyStatus.CleanApplied : Models.StashApplyStatus.Failed,
                StashRetained = !exec.Succeeded || !dropAfterApply,
                StashName = preflight.StashName,
                StashSha = preflight.StashSha,
                Summary = exec.Summary,
                OperationRecordId = exec.Record?.OperationId ?? string.Empty,
            };
        }

        public static async Task<Models.OperationExecutionResult<bool>> DropStashAsync(
            string repoPath,
            string stashName,
            string expectedStashSha = null,
            Models.OperationJournalStore journalStore = null,
            Models.ICommandLog outerLog = null,
            CancellationToken cancellationToken = default)
        {
            return await OperationJournalService.ExecuteGuardedWriteAsync(
                repoPath: repoPath,
                kind: Models.GitOperationKind.Stash,
                title: $"删除暂存 {stashName}",
                action: async ctx =>
                {
                    var live = await ResolveLiveStashAsync(repoPath, stashName, expectedStashSha, cancellationToken).ConfigureAwait(false);
                    if (live == null)
                    {
                        return new OperationActionOutcome<bool>
                        {
                            Stage = Models.GitOperationStage.Failed,
                            Summary = "未找到对应的暂存条目（可能已发生变化或被删除）。",
                            Payload = false,
                        };
                    }

                    ctx.Log.AppendLine($"$ git stash drop -q {live.Name}");
                    var res = await Command.RunGitDirectAsync(
                        repoPath,
                        TimeSpan.FromSeconds(15),
                        cancellationToken,
                        "stash",
                        "drop",
                        "-q",
                        live.Name).ConfigureAwait(false);
                    AppendCommandStreams(ctx.Log, res);

                    return new OperationActionOutcome<bool>
                    {
                        Stage = res.IsSuccess ? Models.GitOperationStage.Succeeded : Models.GitOperationStage.Failed,
                        TargetTipSha = live.SHA,
                        Summary = res.IsSuccess ? $"已删除暂存 {live.Name}（{ShortSha(live.SHA)}）。" : CombineError(res, "删除暂存失败。"),
                        Payload = res.IsSuccess,
                    };
                },
                journalStore: journalStore,
                outerLog: outerLog,
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        public static async Task<Models.OperationExecutionResult<bool>> CheckoutBranchFromStashAsync(
            string repoPath,
            string stashName,
            string branchName,
            string expectedStashSha = null,
            Models.OperationJournalStore journalStore = null,
            Models.ICommandLog outerLog = null,
            CancellationToken cancellationToken = default)
        {
            string safeBranch;
            try
            {
                safeBranch = CommandArgumentHelper.ValidateNonOptionToken(branchName, nameof(branchName));
            }
            catch (ArgumentException ex)
            {
                return new Models.OperationExecutionResult<bool>
                {
                    Succeeded = false,
                    Summary = ex.Message,
                    Payload = false,
                };
            }

            return await OperationJournalService.ExecuteGuardedWriteAsync(
                repoPath: repoPath,
                kind: Models.GitOperationKind.Stash,
                title: $"从暂存创建并切换分支 {safeBranch}",
                branchName: safeBranch,
                action: async ctx =>
                {
                    var live = await ResolveLiveStashAsync(repoPath, stashName, expectedStashSha, cancellationToken).ConfigureAwait(false);
                    if (live == null)
                    {
                        return new OperationActionOutcome<bool>
                        {
                            Stage = Models.GitOperationStage.Failed,
                            Summary = "未找到对应的暂存条目。",
                            Payload = false,
                        };
                    }

                    ctx.Log.AppendLine($"$ git stash branch {CommandArgumentHelper.Quote(safeBranch)} {live.Name}");
                    var res = await Command.RunGitDirectAsync(
                        repoPath,
                        TimeSpan.FromSeconds(30),
                        cancellationToken,
                        "stash",
                        "branch",
                        safeBranch,
                        live.Name).ConfigureAwait(false);
                    AppendCommandStreams(ctx.Log, res);

                    return new OperationActionOutcome<bool>
                    {
                        Stage = res.IsSuccess ? Models.GitOperationStage.Succeeded : Models.GitOperationStage.Failed,
                        TargetTipSha = live.SHA,
                        Summary = res.IsSuccess
                            ? $"已从暂存 {live.Name} 检出分支 {safeBranch}。"
                            : CombineError(res, "从暂存检出分支失败，原暂存项未移除。"),
                        Payload = res.IsSuccess,
                    };
                },
                journalStore: journalStore,
                outerLog: outerLog,
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// Executes a branch/revision switch cleanly or with transactional stash-and-reapply.
        /// Branch switching never performs an automatic force-discard of local changes.
        /// </summary>
        public static async Task<Models.BranchSwitchResult> SwitchSafelyAsync(
            string repoPath,
            string[] checkoutGitArgs,
            Models.DealWithLocalChanges localChangePolicy,
            string targetDescription,
            Models.OperationJournalStore journalStore = null,
            Models.ICommandLog outerLog = null,
            CancellationToken cancellationToken = default)
        {
            if (checkoutGitArgs == null || checkoutGitArgs.Length == 0)
            {
                return new Models.BranchSwitchResult
                {
                    Success = false,
                    Summary = "未提供有效的目标分支或提交参数。",
                };
            }

            if (localChangePolicy == Models.DealWithLocalChanges.Discard)
            {
                return new Models.BranchSwitchResult
                {
                    Success = false,
                    Summary = "按安全设计，切换分支不支持自动强行丢弃本地改动。请选择“暂存并重新应用”，或先手工处理工作区修改。",
                };
            }

            var status = await InspectStatusWithIgnoredAsync(repoPath, cancellationToken).ConfigureAwait(false);
            var hasChanges = status.StagedPaths.Count > 0 || status.WorktreeDirtyPaths.Count > 0 || status.UntrackedPaths.Count > 0;
            string createdStashSha = string.Empty;

            if (localChangePolicy == Models.DealWithLocalChanges.StashAndReapply && hasChanges)
            {
                var pushResult = await ExecutePushAsync(
                    repoPath,
                    $"AUTO_STASH_SWITCH ({targetDescription})",
                    selectedChanges: null,
                    includeUntracked: true,
                    keepIndex: false,
                    keepAll: false,
                    stagedOnly: false,
                    journalStore: journalStore,
                    outerLog: outerLog,
                    cancellationToken: cancellationToken).ConfigureAwait(false);

                if (!pushResult.Success)
                {
                    return new Models.BranchSwitchResult
                    {
                        Success = false,
                        Summary = $"切换前自动暂存本地修改失败，已取消分支切换：{pushResult.Message}",
                    };
                }

                if (!pushResult.WasNoOp)
                    createdStashSha = pushResult.CreatedStashSha;
            }

            var checkoutExec = await OperationJournalService.ExecuteGuardedWriteAsync(
                repoPath: repoPath,
                kind: Models.GitOperationKind.SwitchBranch,
                title: $"切换至 {targetDescription}",
                action: async ctx =>
                {
                    ctx.Log.AppendLine($"$ git {string.Join(" ", checkoutGitArgs.Select(CommandArgumentHelper.Quote))}");
                    var res = await Command.RunGitDirectAsync(
                        repoPath,
                        TimeSpan.FromSeconds(30),
                        cancellationToken,
                        checkoutGitArgs).ConfigureAwait(false);
                    AppendCommandStreams(ctx.Log, res);

                    var msg = res.IsSuccess
                        ? $"已切换至 {targetDescription}。"
                        : CombineError(res, $"切换至 {targetDescription} 失败。");

                    return new OperationActionOutcome<bool>
                    {
                        Stage = res.IsSuccess ? Models.GitOperationStage.Succeeded : Models.GitOperationStage.Failed,
                        Summary = msg,
                        DiagnosticDetail = res.IsSuccess ? string.Empty : msg,
                        Payload = res.IsSuccess,
                    };
                },
                journalStore: journalStore,
                outerLog: outerLog,
                cancellationToken: cancellationToken).ConfigureAwait(false);

            if (!checkoutExec.Succeeded)
            {
                var stashHint = string.IsNullOrEmpty(createdStashSha)
                    ? checkoutExec.Summary
                    : $"{checkoutExec.Summary} 本地未提交修改仍安全保存在暂存 {ShortSha(createdStashSha)} 中，可直接从暂存列表恢复。";
                return new Models.BranchSwitchResult
                {
                    Success = false,
                    CheckoutSucceeded = false,
                    StashCreated = !string.IsNullOrEmpty(createdStashSha),
                    StashSha = createdStashSha,
                    StashRetained = !string.IsNullOrEmpty(createdStashSha),
                    Summary = stashHint,
                };
            }

            if (string.IsNullOrEmpty(createdStashSha))
            {
                return new Models.BranchSwitchResult
                {
                    Success = true,
                    CheckoutSucceeded = true,
                    StashCreated = false,
                    Summary = $"已切换至 {targetDescription}。",
                };
            }

            var liveStash = await ResolveLiveStashAsync(repoPath, string.Empty, createdStashSha, cancellationToken).ConfigureAwait(false);
            var reapply = await ExecuteApplyAsync(
                repoPath,
                liveStash?.Name ?? "stash@{0}",
                createdStashSha,
                restoreIndex: true,
                dropAfterApply: true,
                allowDirtyWorkingTree: false,
                expectedSnapshot: null,
                journalStore: journalStore,
                outerLog: outerLog,
                cancellationToken: cancellationToken).ConfigureAwait(false);

            return new Models.BranchSwitchResult
            {
                Success = reapply.Success,
                CheckoutSucceeded = true,
                StashCreated = true,
                StashSha = createdStashSha,
                ReapplyAttempted = true,
                ReapplySucceeded = reapply.Success,
                StashRetained = reapply.StashRetained,
                HasConflicts = reapply.Status == Models.StashApplyStatus.AppliedWithConflictsRetained,
                ConflictedPaths = reapply.ConflictedPaths,
                Summary = reapply.Success
                    ? $"已切换至 {targetDescription}，并完整重放及清理了临时暂存。"
                    : $"已切换至 {targetDescription}，但恢复暂存 {ShortSha(createdStashSha)} 未完成（保存项已保留）：{reapply.Summary}",
            };
        }

        internal static async Task<Models.Stash> ResolveLiveStashAsync(
            string repoPath,
            string stashName,
            string expectedSha,
            CancellationToken cancellationToken)
        {
            var stashes = await new QueryStashes(repoPath).GetResultAsync().ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(expectedSha))
            {
                var bySha = stashes.FirstOrDefault(s => string.Equals(s.SHA, expectedSha.Trim(), StringComparison.OrdinalIgnoreCase));
                if (bySha != null)
                    return bySha;
            }

            if (!string.IsNullOrWhiteSpace(stashName))
            {
                var safeName = CommandArgumentHelper.ValidateNonOptionToken(stashName, nameof(stashName));
                var byName = stashes.FirstOrDefault(s => string.Equals(s.Name, safeName, StringComparison.Ordinal));
                if (byName != null)
                    return byName;
            }

            return null;
        }

        internal static async Task<List<string>> ReadStashAffectedPathsAsync(
            string repoPath,
            Models.Stash stash,
            CancellationToken cancellationToken)
        {
            var result = new List<string>();
            var seen = new HashSet<string>(PathComparer);

            if (stash.Parents.Count >= 1 && HexOidRegex().IsMatch(stash.Parents[0]))
            {
                var baseSha = stash.Parents[0];
                var pairs = new List<(string Base, string Target)> { (baseSha, stash.SHA) };
                if (stash.Parents.Count >= 2 && HexOidRegex().IsMatch(stash.Parents[1]))
                    pairs.Add((baseSha, stash.Parents[1]));

                foreach (var (fromSha, toSha) in pairs)
                {
                    var diffRes = await Command.RunGitDirectAsync(
                        repoPath,
                        cancellationToken,
                        "--no-pager",
                        "-c",
                        "core.quotepath=off",
                        "diff-tree",
                        "--no-commit-id",
                        "--name-only",
                        "-r",
                        "-z",
                        fromSha,
                        toSha).ConfigureAwait(false);

                    if (diffRes.IsSuccess && !string.IsNullOrEmpty(diffRes.StdOut))
                    {
                        foreach (var item in diffRes.StdOut.Split('\0', StringSplitOptions.RemoveEmptyEntries))
                        {
                            var norm = NormalizeRelPath(item);
                            if (seen.Add(norm))
                                result.Add(norm);
                        }
                    }
                }
            }
            else
            {
                var fallbackRes = await Command.RunGitDirectAsync(
                    repoPath,
                    cancellationToken,
                    "--no-pager",
                    "-c",
                    "core.quotepath=off",
                    "ls-tree",
                    "-r",
                    "--name-only",
                    "-z",
                    stash.SHA).ConfigureAwait(false);

                if (fallbackRes.IsSuccess && !string.IsNullOrEmpty(fallbackRes.StdOut))
                {
                    foreach (var item in fallbackRes.StdOut.Split('\0', StringSplitOptions.RemoveEmptyEntries))
                    {
                        var norm = NormalizeRelPath(item);
                        if (seen.Add(norm))
                            result.Add(norm);
                    }
                }
            }

            // A stash with >= 3 parents includes an untracked files commit as parent #3 (^3).
            if (stash.Parents.Count >= 3)
            {
                var untrackedParent = stash.Parents[2];
                if (!string.IsNullOrWhiteSpace(untrackedParent) && HexOidRegex().IsMatch(untrackedParent))
                {
                    var treeRes = await Command.RunGitDirectAsync(
                        repoPath,
                        cancellationToken,
                        "--no-pager",
                        "-c",
                        "core.quotepath=off",
                        "ls-tree",
                        "-r",
                        "--name-only",
                        "-z",
                        untrackedParent).ConfigureAwait(false);

                    if (treeRes.IsSuccess && !string.IsNullOrEmpty(treeRes.StdOut))
                    {
                        foreach (var item in treeRes.StdOut.Split('\0', StringSplitOptions.RemoveEmptyEntries))
                        {
                            var norm = NormalizeRelPath(item);
                            if (seen.Add(norm))
                                result.Add(norm);
                        }
                    }
                }
            }

            return result;
        }

        internal static async Task<bool> HasValidHeadAsync(string repoPath, CancellationToken cancellationToken)
        {
            var res = await Command.RunGitDirectAsync(repoPath, cancellationToken, "rev-parse", "--verify", "-q", "HEAD")
                .ConfigureAwait(false);
            return res.IsSuccess && !string.IsNullOrWhiteSpace(res.StdOut);
        }

        internal static async Task<StatusBreakdown> InspectStatusWithIgnoredAsync(string repoPath, CancellationToken cancellationToken)
        {
            var breakdown = new StatusBreakdown();
            var res = await Command.RunGitDirectAsync(
                repoPath,
                TimeSpan.FromSeconds(20),
                cancellationToken,
                "--no-optional-locks",
                "-c",
                "core.quotepath=off",
                "status",
                "--porcelain=v1",
                "-z",
                "-uall",
                "--ignored=matching").ConfigureAwait(false);

            if (!res.IsSuccess || string.IsNullOrEmpty(res.StdOut))
                return breakdown;

            var tokens = res.StdOut.Split('\0');
            for (var i = 0; i < tokens.Length; i++)
            {
                var token = tokens[i];
                if (token.Length < 4 || token[2] != ' ')
                    continue;

                var x = token[0];
                var y = token[1];
                var path = NormalizeRelPath(token[3..]);

                // Porcelain v1 -z emits a second NUL-terminated old path when X is R (renamed) or C (copied).
                if (x is 'R' or 'C' && i + 1 < tokens.Length)
                    i++;

                if (x == '?' && y == '?')
                {
                    breakdown.UntrackedPaths.Add(path);
                }
                else if (x == '!' && y == '!')
                {
                    breakdown.IgnoredPaths.Add(path);
                }
                else if (x == 'U' || y == 'U' || (x == 'D' && y == 'D') || (x == 'A' && y == 'A'))
                {
                    breakdown.ConflictedPaths.Add(path);
                }
                else
                {
                    if (x != ' ')
                        breakdown.StagedPaths.Add(path);
                    if (y != ' ')
                        breakdown.WorktreeDirtyPaths.Add(path);
                }
            }

            return breakdown;
        }

        private static async Task<List<string>> ReadUnmergedPathsAsync(string repoPath, CancellationToken cancellationToken)
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

            if (!res.IsSuccess || string.IsNullOrEmpty(res.StdOut))
                return [];

            var paths = new HashSet<string>(PathComparer);
            foreach (var rec in res.StdOut.Split('\0', StringSplitOptions.RemoveEmptyEntries))
            {
                var tab = rec.IndexOf('\t');
                if (tab > 0 && tab + 1 < rec.Length)
                    paths.Add(NormalizeRelPath(rec[(tab + 1)..]));
            }

            return paths.OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList();
        }

        private static async Task<bool> HasInProgressMarkerAsync(string repoPath, CancellationToken cancellationToken)
        {
            var (gitDir, _) = await Models.RepositoryWriteGuard.ResolveGitDirectoriesAsync(repoPath, cancellationToken).ConfigureAwait(false);
            return CaptureOperationSnapshot.DetectInProgressKind(gitDir) != Models.GitInProgressKind.None;
        }

        private static bool PathsCollide(string existingPath, string targetPath)
        {
            var a = NormalizeRelPath(existingPath);
            var b = NormalizeRelPath(targetPath);
            if (string.Equals(a, b, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                return true;

            var comp = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if (a.StartsWith(b + "/", comp) || b.StartsWith(a + "/", comp))
                return true;

            return false;
        }

        private static string SanitizeStashMessage(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
                return string.Empty;

            return raw.Replace("\0", string.Empty).Replace('\r', ' ').Replace('\n', ' ').Trim();
        }

        private static string NormalizeRelPath(string path) => path.Trim().Replace('\\', '/').TrimEnd('/');

        private static string ShortSha(string sha)
        {
            if (string.IsNullOrWhiteSpace(sha))
                return "(无)";
            return sha.Length > 8 ? sha[..8] : sha;
        }

        private static string CombineError(Command.Result res, string fallback)
        {
            var detail = string.IsNullOrWhiteSpace(res.StdErr) ? res.StdOut : res.StdErr;
            detail = Models.GitConfigSnapshot.RedactText(detail?.Trim() ?? string.Empty);
            return string.IsNullOrWhiteSpace(detail) ? fallback : detail;
        }

        private static void AppendCommandStreams(Models.ICommandLog log, Command.Result res)
        {
            if (log == null)
                return;
            if (!string.IsNullOrWhiteSpace(res.StdOut))
                log.AppendLine(res.StdOut.TrimEnd());
            if (!string.IsNullOrWhiteSpace(res.StdErr))
                log.AppendLine(res.StdErr.TrimEnd());
        }

        internal sealed class StatusBreakdown
        {
            public List<string> StagedPaths { get; } = [];
            public List<string> WorktreeDirtyPaths { get; } = [];
            public List<string> UntrackedPaths { get; } = [];
            public List<string> IgnoredPaths { get; } = [];
            public List<string> ConflictedPaths { get; } = [];
        }
    }
}
