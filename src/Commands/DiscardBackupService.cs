using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SourceGit.Commands
{
    /// <summary>
    /// Enforces baseline separation for discarding changes:
    /// 1. RestoreWorktreeToIndex (`git restore --worktree` only; preserves index).
    /// 2. RestoreToHead (`git restore --staged --worktree --source=HEAD`, plus index-added cleanup via reset + backup + recycle).
    /// 3. MoveUntrackedToRecycleBin (system Recycle Bin only, never silent delete or implicit git clean).
    /// Always persists recoverable disk + index content into the 30-day DiscardBackupStore BEFORE touching repo files.
    /// </summary>
    public static class DiscardBackupService
    {
        private static readonly StringComparer PathComparer = OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;

        public static async Task<Models.DiscardPreflight> InspectDiscardAsync(
            string repoPath,
            IReadOnlyList<Models.Change> targets,
            Models.DiscardBaselineKind baseline,
            CancellationToken cancellationToken = default)
        {
            if (targets == null || targets.Count == 0)
            {
                return new Models.DiscardPreflight
                {
                    Baseline = baseline,
                    CanProceed = false,
                    BlockingReason = "未选择任何要处理的文件或目录。",
                };
            }

            var hasHead = await StashWorkflowService.HasValidHeadAsync(repoPath, cancellationToken).ConfigureAwait(false);
            if (baseline == Models.DiscardBaselineKind.RestoreToHead && !hasHead)
            {
                return new Models.DiscardPreflight
                {
                    Baseline = baseline,
                    CanProceed = false,
                    HasUnbornHead = true,
                    BlockingReason = "当前仓库尚未创建首次提交（unborn HEAD），无法恢复到上次提交（HEAD）。请先创建首次提交，或改用移至回收站。",
                };
            }

            var headPaths = baseline == Models.DiscardBaselineKind.RestoreToHead
                ? await ReadHeadTreePathsAsync(repoPath, cancellationToken).ConfigureAwait(false)
                : new HashSet<string>(PathComparer);

            var indexInfo = await ReadIndexEntriesAsync(repoPath, cancellationToken).ConfigureAwait(false);

            var tracked = new List<Models.DiscardTargetItem>();
            var untracked = new List<Models.DiscardTargetItem>();
            var skippedSubmodules = new List<string>();
            var indexAddedOnly = new List<string>();

            var repoRootFull = Path.TrimEndingDirectorySeparator(Path.GetFullPath(repoPath));

            foreach (var c in targets)
            {
                if (c == null || string.IsNullOrWhiteSpace(c.Path))
                    continue;

                var relPath = NormalizeRelPath(c.Path);
                var origPath = string.IsNullOrWhiteSpace(c.OriginalPath) ? string.Empty : NormalizeRelPath(c.OriginalPath);

                var absPath = Path.Combine(repoRootFull, relPath.Replace('/', Path.DirectorySeparatorChar));
                var isGitlinkMode = indexInfo.TryGetValue(relPath, out var idxEntry) &&
                                    string.Equals(idxEntry.Mode, "160000", StringComparison.Ordinal);
                var isNestedRepoDir = Directory.Exists(absPath) &&
                                      (File.Exists(Path.Combine(absPath, ".git")) || Directory.Exists(Path.Combine(absPath, ".git")));

                if (isGitlinkMode || isNestedRepoDir)
                {
                    skippedSubmodules.Add(relPath);
                    continue;
                }

                var isUntracked = c.WorkTree == Models.ChangeState.Untracked || c.Index == Models.ChangeState.Untracked;
                var item = new Models.DiscardTargetItem
                {
                    Path = relPath,
                    OriginalPath = origPath,
                    Index = c.Index,
                    WorkTree = c.WorkTree,
                    IsSubmodule = false,
                    IsFolder = Directory.Exists(absPath),
                };

                if (isUntracked)
                {
                    untracked.Add(item);
                }
                else
                {
                    if (baseline == Models.DiscardBaselineKind.RestoreToHead && !headPaths.Contains(relPath))
                    {
                        item.IsIndexAddedOnly = true;
                        indexAddedOnly.Add(relPath);
                    }

                    tracked.Add(item);
                }
            }

            var confirmationPaths = tracked
                .Select(t => t.Path)
                .Concat(untracked.Select(u => u.Path))
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .Distinct(PathComparer)
                .ToList();
            var confirmationSnapshot = (tracked.Count > 0 || untracked.Count > 0)
                ? await CaptureOperationSnapshot.CaptureAsync(
                    repoPath,
                    new Models.OperationSnapshotCaptureOptions
                    {
                        TargetPaths = confirmationPaths,
                        IncludeTargetFileDetails = true,
                    },
                    cancellationToken).ConfigureAwait(false)
                : null;

            if (baseline == Models.DiscardBaselineKind.MoveUntrackedToRecycleBin)
            {
                return new Models.DiscardPreflight
                {
                    Baseline = baseline,
                    CanProceed = untracked.Count > 0,
                    HasUnbornHead = !hasHead,
                    BlockingReason = untracked.Count > 0
                        ? string.Empty
                        : skippedSubmodules.Count > 0
                            ? "子模块条目不能直接移至系统回收站，已跳过。"
                            : "所选条目中没有可移至回收站的未跟踪文件或目录。",
                    TrackedTargets = tracked,
                    UntrackedTargets = untracked,
                    SkippedSubmodulePaths = skippedSubmodules,
                    ConfirmationSnapshot = confirmationSnapshot,
                };
            }

            return new Models.DiscardPreflight
            {
                Baseline = baseline,
                CanProceed = tracked.Count > 0,
                HasUnbornHead = !hasHead,
                BlockingReason = tracked.Count > 0
                    ? string.Empty
                    : skippedSubmodules.Count > 0
                        ? "暂不支持按文件恢复库直接覆盖子模块变更。"
                        : "所选条目中没有符合当前基线的已跟踪文件修改。",
                TrackedTargets = tracked,
                UntrackedTargets = untracked,
                SkippedSubmodulePaths = skippedSubmodules,
                IndexAddedOnlyPaths = indexAddedOnly,
                ConfirmationSnapshot = confirmationSnapshot,
            };
        }

        public static async Task<Models.DiscardExecutionResult> ExecuteDiscardAsync(
            string repoPath,
            IReadOnlyList<Models.Change> targets,
            Models.DiscardBaselineKind baseline,
            Models.DiscardBackupStore backupStore = null,
            Func<string, (bool Success, string Error)> recycleHandler = null,
            Models.OperationJournalStore journalStore = null,
            Models.ICommandLog outerLog = null,
            DateTime? utcNowOverride = null,
            CancellationToken cancellationToken = default,
            Models.DiscardPreflight expectedPreflight = null)
        {
            if (expectedPreflight?.ConfirmationSnapshot != null)
            {
                var verify = await CaptureOperationSnapshot.VerifyAsync(
                    expectedPreflight.ConfirmationSnapshot,
                    new Models.OperationSnapshotVerifyOptions
                    {
                        RequireSameRepoIdentity = true,
                        RequireSameHead = true,
                        RequireSameInProgressState = true,
                        RequireSameStagedFingerprint = true,
                        RequireSameWatchedRefs = false,
                        RequireSameTargetFiles = true,
                        CheckGitLocks = false,
                        CheckIndexLock = false,
                    },
                    cancellationToken).ConfigureAwait(false);
                if (!verify.IsValid)
                {
                    return new Models.DiscardExecutionResult
                    {
                        Success = false,
                        BlockedBySnapshot = true,
                        Baseline = baseline,
                        Summary = string.IsNullOrWhiteSpace(verify.Summary)
                            ? "确认弹窗打开期间工作区或仓库状态已发生外部变更，已中止丢弃并刷新列表。"
                            : $"确认弹窗打开期间仓库或目标文件已发生外部变更（{verify.Summary}），已取消本次丢弃并为您重新检查。",
                    };
                }
            }

            var preflight = await InspectDiscardAsync(repoPath, targets, baseline, cancellationToken).ConfigureAwait(false);
            if (!preflight.CanProceed)
            {
                return new Models.DiscardExecutionResult
                {
                    Success = false,
                    Baseline = baseline,
                    Summary = preflight.BlockingReason,
                };
            }

            if (baseline == Models.DiscardBaselineKind.MoveUntrackedToRecycleBin)
            {
                return await ExecuteMoveUntrackedToRecycleBinAsync(
                    repoPath,
                    preflight.UntrackedTargets,
                    recycleHandler,
                    journalStore,
                    outerLog,
                    cancellationToken).ConfigureAwait(false);
            }

            var effectiveStore = backupStore ?? Models.DiscardBackupStore.Instance;
            var effectiveRecycle = recycleHandler ?? DefaultRecycleHandler;

            var title = baseline == Models.DiscardBaselineKind.RestoreWorktreeToIndex
                ? $"恢复未暂存修改到暂存区（{preflight.TrackedTargets.Count} 个文件）"
                : $"恢复文件到上次提交 HEAD（{preflight.TrackedTargets.Count} 个文件）";

            var exec = await OperationJournalService.ExecuteGuardedWriteAsync(
                repoPath: repoPath,
                kind: Models.GitOperationKind.Discard,
                title: title,
                action: async ctx =>
                {
                    // 1. Capture live disk content & index entries BEFORE altering working tree or index.
                    var indexInfo = await ReadIndexEntriesAsync(repoPath, cancellationToken).ConfigureAwait(false);
                    var captureInputs = new List<Models.DiscardBackupCaptureInput>(preflight.TrackedTargets.Count);

                    foreach (var target in preflight.TrackedTargets)
                    {
                        var absPath = ResolveSafeRepoPathAny(repoPath, target.Path);
                        if (absPath == null)
                        {
                            return FailBeforeMutation(
                                baseline,
                                $"拒绝处理仓库目录之外的异常路径：{target.Path}",
                                ctx.OperationId);
                        }

                        var input = await BuildCaptureInputAsync(
                            repoPath,
                            absPath,
                            target,
                            baseline,
                            indexInfo,
                            cancellationToken).ConfigureAwait(false);
                        if (input == null)
                        {
                            return FailBeforeMutation(
                                baseline,
                                $"读取待备份文件内容失败，已中止丢弃以防数据丢失：{target.Path}",
                                ctx.OperationId);
                        }

                        captureInputs.Add(input);
                    }

                    var headSha = await ReadHeadShaAsync(repoPath, cancellationToken).ConfigureAwait(false);
                    var capture = effectiveStore.Capture(
                        repoPath,
                        ctx.CanonicalCommonDir,
                        headSha,
                        baseline,
                        title,
                        captureInputs,
                        utcNowOverride);

                    if (!capture.Success)
                    {
                        return FailBeforeMutation(
                            baseline,
                            $"写入 30 天本地恢复库失败，已中止丢弃操作（工作区未作任何修改）：{capture.Error}",
                            ctx.OperationId);
                    }

                    ctx.Log.AppendLine($"[DiscardBackup] 已写入 30 天本地恢复库 ID={capture.BackupId}");

                    var processed = new List<string>();
                    var recycled = new List<string>();
                    var failed = new List<string>();

                    if (baseline == Models.DiscardBaselineKind.RestoreWorktreeToIndex)
                    {
                        var restorePaths = preflight.TrackedTargets.Select(t => t.Path).ToList();
                        var stdin = CommandArgumentHelper.BuildNulPathspecBytes(restorePaths);

                        ctx.Log.AppendLine($"$ git --literal-pathspecs restore --worktree --pathspec-from-file=- --pathspec-file-nul ({restorePaths.Count} paths)");
                        var res = await Command.RunGitDirectWithInputAsync(
                            repoPath,
                            TimeSpan.FromSeconds(30),
                            stdin,
                            cancellationToken,
                            "--literal-pathspecs",
                            "restore",
                            "--worktree",
                            "--pathspec-from-file=-",
                            "--pathspec-file-nul").ConfigureAwait(false);

                        AppendStreams(ctx.Log, res);
                        if (!res.IsSuccess)
                        {
                            var errMsg = CombineError(res, "执行 git restore --worktree 失败。");
                            return new OperationActionOutcome<Models.DiscardExecutionResult>
                            {
                                Stage = Models.GitOperationStage.Failed,
                                Summary = $"{errMsg}（原内容已安全备份在恢复库 {capture.BackupId}）",
                                DiagnosticDetail = errMsg,
                                Payload = new Models.DiscardExecutionResult
                                {
                                    Success = false,
                                    Baseline = baseline,
                                    BackupId = capture.BackupId,
                                    FailedPaths = restorePaths,
                                    Summary = $"{errMsg}（原内容已备份 ID={capture.BackupId}）",
                                    OperationRecordId = ctx.OperationId,
                                },
                            };
                        }

                        processed.AddRange(restorePaths);
                    }
                    else
                    {
                        var headRestoreSet = new HashSet<string>(PathComparer);
                        foreach (var t in preflight.TrackedTargets)
                        {
                            if (!t.IsIndexAddedOnly)
                                headRestoreSet.Add(t.Path);
                            else if (!string.IsNullOrEmpty(t.OriginalPath))
                                headRestoreSet.Add(t.OriginalPath);
                        }

                        var headRestorePaths = headRestoreSet.ToList();
                        var indexAddedTargets = preflight.TrackedTargets.Where(t => t.IsIndexAddedOnly).ToList();

                        if (headRestorePaths.Count > 0)
                        {
                            var stdin = CommandArgumentHelper.BuildNulPathspecBytes(headRestorePaths);
                            ctx.Log.AppendLine($"$ git --literal-pathspecs restore --staged --worktree --source=HEAD --pathspec-from-file=- --pathspec-file-nul ({headRestorePaths.Count} paths)");
                            var res = await Command.RunGitDirectWithInputAsync(
                                repoPath,
                                TimeSpan.FromSeconds(30),
                                stdin,
                                cancellationToken,
                                "--literal-pathspecs",
                                "restore",
                                "--staged",
                                "--worktree",
                                "--source=HEAD",
                                "--pathspec-from-file=-",
                                "--pathspec-file-nul").ConfigureAwait(false);

                            AppendStreams(ctx.Log, res);
                            if (!res.IsSuccess)
                            {
                                var errMsg = CombineError(res, "执行 git restore --staged --worktree --source=HEAD 失败。");
                                return new OperationActionOutcome<Models.DiscardExecutionResult>
                                {
                                    Stage = Models.GitOperationStage.Failed,
                                    Summary = $"{errMsg}（备份 ID={capture.BackupId}）",
                                    DiagnosticDetail = errMsg,
                                    Payload = new Models.DiscardExecutionResult
                                    {
                                        Success = false,
                                        Baseline = baseline,
                                        BackupId = capture.BackupId,
                                        FailedPaths = headRestorePaths,
                                        Summary = $"{errMsg}（备份 ID={capture.BackupId}）",
                                        OperationRecordId = ctx.OperationId,
                                    },
                                };
                            }

                            processed.AddRange(headRestorePaths);
                        }

                        if (indexAddedTargets.Count > 0)
                        {
                            // Unstage files added to index (or new rename targets) that are not in HEAD, then recycle safely.
                            foreach (var t in indexAddedTargets)
                            {
                                var resetArgs = new[] { "--literal-pathspecs", "reset", "-q", "HEAD", "--", t.Path };
                                ctx.Log.AppendLine($"$ git --literal-pathspecs reset -q HEAD -- {CommandArgumentHelper.Quote(t.Path)}");
                                var resetRes = await Command.RunGitDirectAsync(
                                    repoPath,
                                    TimeSpan.FromSeconds(15),
                                    cancellationToken,
                                    resetArgs).ConfigureAwait(false);
                                AppendStreams(ctx.Log, resetRes);

                                if (!resetRes.IsSuccess)
                                {
                                    failed.Add(t.Path);
                                    continue;
                                }

                                var absPath = ResolveSafeRepoPathAny(repoPath, t.Path);
                                if (absPath != null && (File.Exists(absPath) || Directory.Exists(absPath)))
                                {
                                    var (ok, recycleErr) = effectiveRecycle(absPath);
                                    if (!ok)
                                    {
                                        ctx.Log.AppendLine($"[RecycleFailed] {t.Path}: {recycleErr}");
                                        failed.Add(t.Path);
                                        continue;
                                    }

                                    recycled.Add(t.Path);
                                }

                                if (!processed.Contains(t.Path, PathComparer))
                                    processed.Add(t.Path);
                            }
                        }
                    }

                    // Record exact post-discard disk + index hashes so subsequent-modification detection works accurately.
                    var postIndex = await ReadIndexEntriesAsync(repoPath, cancellationToken).ConfigureAwait(false);
                    var postStates = new Dictionary<string, (string WorktreeSha256, string IndexBlobOid)>(PathComparer);
                    foreach (var t in preflight.TrackedTargets)
                    {
                        var abs = ResolveSafeRepoPathAny(repoPath, t.Path);
                        var wtSha = abs != null && File.Exists(abs)
                            ? Models.DiscardBackupStore.ComputeFileSha256Hex(abs)
                            : Models.DiscardBackupStore.AbsentMarker;
                        var idxOid = postIndex.TryGetValue(t.Path, out var idxEntry)
                            ? idxEntry.BlobOid
                            : Models.DiscardBackupStore.AbsentMarker;
                        postStates[t.Path] = (wtSha, idxOid);
                    }
                    effectiveStore.UpdatePostDiscardState(capture.BackupId, postStates);

                    var allOk = failed.Count == 0;
                    var finalSummary = allOk
                        ? $"已完成 {title}，原内容已存入 30 天本地恢复库（ID：{capture.BackupId}）。"
                        : $"部分路径未能完成移至回收站（{string.Join(", ", failed)}），原文件保留在磁盘，已写入恢复库（ID：{capture.BackupId}）。";

                    return new OperationActionOutcome<Models.DiscardExecutionResult>
                    {
                        Stage = allOk ? Models.GitOperationStage.Succeeded : Models.GitOperationStage.PartialSuccess,
                        Summary = finalSummary,
                        Payload = new Models.DiscardExecutionResult
                        {
                            Success = allOk,
                            Baseline = baseline,
                            BackupId = capture.BackupId,
                            ProcessedPaths = processed,
                            RecycledPaths = recycled,
                            FailedPaths = failed,
                            Summary = finalSummary,
                            OperationRecordId = ctx.OperationId,
                        },
                    };
                },
                journalStore: journalStore,
                outerLog: outerLog,
                cancellationToken: cancellationToken).ConfigureAwait(false);

            return exec.Payload ?? new Models.DiscardExecutionResult
            {
                Success = exec.Succeeded,
                Baseline = baseline,
                Summary = exec.Summary,
                OperationRecordId = exec.Record?.OperationId ?? string.Empty,
            };
        }

        private static async Task<Models.DiscardExecutionResult> ExecuteMoveUntrackedToRecycleBinAsync(
            string repoPath,
            IReadOnlyList<Models.DiscardTargetItem> untrackedTargets,
            Func<string, (bool Success, string Error)> recycleHandler,
            Models.OperationJournalStore journalStore,
            Models.ICommandLog outerLog,
            CancellationToken cancellationToken)
        {
            var effectiveRecycle = recycleHandler ?? DefaultRecycleHandler;
            var title = $"将 {untrackedTargets.Count} 个未跟踪项目移至回收站";

            var exec = await OperationJournalService.ExecuteGuardedWriteAsync(
                repoPath: repoPath,
                kind: Models.GitOperationKind.Discard,
                title: title,
                action: async ctx =>
                {
                    var trackedSet = await ReadIndexEntriesAsync(repoPath, cancellationToken).ConfigureAwait(false);
                    var recycled = new List<string>();
                    var failed = new List<string>();
                    var failureReasons = new List<string>();

                    foreach (var target in untrackedTargets)
                    {
                        if (trackedSet.ContainsKey(target.Path))
                        {
                            failed.Add(target.Path);
                            failureReasons.Add($"{target.Path}: 已受版本库索引跟踪，拒绝直接回收。");
                            continue;
                        }

                        var absPath = ResolveSafeRepoPathAny(repoPath, target.Path);
                        if (absPath == null)
                        {
                            failed.Add(target.Path);
                            failureReasons.Add($"{target.Path}: 路径越界或非法。");
                            continue;
                        }

                        if (!File.Exists(absPath) && !Directory.Exists(absPath))
                        {
                            recycled.Add(target.Path);
                            continue;
                        }

                        var (ok, err) = effectiveRecycle(absPath);
                        if (!ok || File.Exists(absPath) || Directory.Exists(absPath))
                        {
                            var reason = string.IsNullOrWhiteSpace(err)
                                ? "系统回收站拒绝或未能移走目标（原文件仍保留在磁盘）。"
                                : err;
                            ctx.Log.AppendLine($"[RecycleFailed] {target.Path}: {reason}");
                            failed.Add(target.Path);
                            failureReasons.Add($"{target.Path}: {reason}");
                            continue;
                        }

                        ctx.Log.AppendLine($"[Recycled] {target.Path}");
                        recycled.Add(target.Path);
                    }

                    var allOk = failed.Count == 0;
                    var summary = allOk
                        ? $"已将 {recycled.Count} 个未跟踪项目安全移至系统回收站。"
                        : $"移至回收站未全部完成（成功 {recycled.Count}，失败 {failed.Count}，失败文件保留原位）：{string.Join("; ", failureReasons)}";

                    return new OperationActionOutcome<Models.DiscardExecutionResult>
                    {
                        Stage = allOk
                            ? Models.GitOperationStage.Succeeded
                            : recycled.Count > 0
                                ? Models.GitOperationStage.PartialSuccess
                                : Models.GitOperationStage.Failed,
                        Summary = summary,
                        DiagnosticDetail = string.Join(Environment.NewLine, failureReasons),
                        Payload = new Models.DiscardExecutionResult
                        {
                            Success = allOk,
                            Baseline = Models.DiscardBaselineKind.MoveUntrackedToRecycleBin,
                            ProcessedPaths = recycled,
                            RecycledPaths = recycled,
                            FailedPaths = failed,
                            Summary = summary,
                            OperationRecordId = ctx.OperationId,
                        },
                    };
                },
                journalStore: journalStore,
                outerLog: outerLog,
                cancellationToken: cancellationToken).ConfigureAwait(false);

            return exec.Payload ?? new Models.DiscardExecutionResult
            {
                Success = exec.Succeeded,
                Baseline = Models.DiscardBaselineKind.MoveUntrackedToRecycleBin,
                Summary = exec.Summary,
                OperationRecordId = exec.Record?.OperationId ?? string.Empty,
            };
        }

        public static async Task<Models.DiscardBackupRestoreCheck> InspectBackupRestoreAsync(
            string repoPath,
            string backupId,
            Models.DiscardBackupStore backupStore = null,
            DateTime? utcNowOverride = null,
            CancellationToken cancellationToken = default)
        {
            var store = backupStore ?? Models.DiscardBackupStore.Instance;
            var entry = store.TryGet(backupId);
            if (entry == null)
            {
                return new Models.DiscardBackupRestoreCheck
                {
                    Found = false,
                    CanRestore = false,
                    BlockingReason = $"未找到恢复记录 {backupId}。",
                };
            }

            var nowUtc = utcNowOverride ?? DateTime.UtcNow;
            var isExpired = entry.ExpiresAtUtc <= nowUtc;

            var integrityOk = store.VerifyPayloadIntegrity(entry, out var missingPayloads);
            var liveIndex = await ReadIndexEntriesAsync(repoPath, cancellationToken).ConfigureAwait(false);

            var conflictingPaths = new List<string>();
            var conflictDetails = new List<string>();

            foreach (var file in entry.Files)
            {
                var abs = ResolveSafeRepoPathAny(repoPath, file.RelativePath);
                var currentWorktreeSha = abs != null && File.Exists(abs)
                    ? Models.DiscardBackupStore.ComputeFileSha256Hex(abs)
                    : Models.DiscardBackupStore.AbsentMarker;
                var currentIndexOid = liveIndex.TryGetValue(file.RelativePath, out var idx)
                    ? idx.BlobOid
                    : Models.DiscardBackupStore.AbsentMarker;

                var hasWorktreeDrift =
                    !string.IsNullOrEmpty(file.PostDiscardWorktreeSha256) &&
                    !string.Equals(currentWorktreeSha, file.PostDiscardWorktreeSha256, StringComparison.OrdinalIgnoreCase);

                var hasIndexDrift =
                    !string.IsNullOrEmpty(file.PostDiscardIndexBlobOid) &&
                    !string.Equals(currentIndexOid, file.PostDiscardIndexBlobOid, StringComparison.OrdinalIgnoreCase);

                if (hasWorktreeDrift || hasIndexDrift)
                {
                    conflictingPaths.Add(file.RelativePath);
                    var desc = hasWorktreeDrift && hasIndexDrift
                        ? "工作区和暂存区在丢弃后均产生了新的变更"
                        : hasWorktreeDrift
                            ? "工作区文件在丢弃后已被修改"
                            : "暂存区条目在丢弃后已被修改";
                    conflictDetails.Add($"{file.RelativePath}: {desc}");
                }
            }

            var hasSubsequent = conflictingPaths.Count > 0;
            var blocking = new List<string>();
            if (isExpired)
                blocking.Add("该恢复记录已超过 30 天保留期限。");
            if (!integrityOk)
                blocking.Add($"恢复载荷缺失或校验失败：{string.Join(", ", missingPayloads)}");
            if (hasSubsequent)
                blocking.Add($"检测到工作区或暂存区后续已有修改（{string.Join("; ", conflictDetails)}），默认阻止静默覆盖。");

            return new Models.DiscardBackupRestoreCheck
            {
                Found = true,
                CanRestore = !isExpired && integrityOk && !hasSubsequent,
                IsExpired = isExpired,
                HasPayloadIntegrityError = !integrityOk,
                HasSubsequentModifications = hasSubsequent,
                Entry = entry,
                ConflictingPaths = conflictingPaths,
                ConflictDetails = conflictDetails,
                MissingPayloadPaths = missingPayloads,
                BlockingReason = string.Join(" ", blocking),
            };
        }

        public static async Task<Models.DiscardBackupRestoreResult> RestoreBackupAsync(
            string repoPath,
            string backupId,
            bool allowOverwriteSubsequentEdits = false,
            Models.DiscardBackupStore backupStore = null,
            Func<string, (bool Success, string Error)> recycleHandler = null,
            Models.OperationJournalStore journalStore = null,
            Models.ICommandLog outerLog = null,
            DateTime? utcNowOverride = null,
            CancellationToken cancellationToken = default)
        {
            var store = backupStore ?? Models.DiscardBackupStore.Instance;
            var check = await InspectBackupRestoreAsync(repoPath, backupId, store, utcNowOverride, cancellationToken)
                .ConfigureAwait(false);

            if (!check.Found)
            {
                return new Models.DiscardBackupRestoreResult
                {
                    Success = false,
                    BackupId = backupId ?? string.Empty,
                    Summary = check.BlockingReason,
                };
            }

            if (check.IsExpired || check.HasPayloadIntegrityError || (check.HasSubsequentModifications && !allowOverwriteSubsequentEdits))
            {
                return new Models.DiscardBackupRestoreResult
                {
                    Success = false,
                    BackupId = backupId,
                    Summary = check.BlockingReason,
                };
            }

            var effectiveRecycle = recycleHandler ?? DefaultRecycleHandler;
            var entry = check.Entry;

            var exec = await OperationJournalService.ExecuteGuardedWriteAsync(
                repoPath: repoPath,
                kind: Models.GitOperationKind.RestoreBackup,
                title: $"从 30 天恢复库还原（{entry.Files.Count} 个文件，ID={backupId}）",
                action: async ctx =>
                {
                    var restoredPaths = new List<string>();
                    var indexRestoredCount = 0;

                    foreach (var file in entry.Files)
                    {
                        var absPath = ResolveSafeRepoPathAny(repoPath, file.RelativePath);
                        if (absPath == null)
                        {
                            return new OperationActionOutcome<Models.DiscardBackupRestoreResult>
                            {
                                Stage = Models.GitOperationStage.Failed,
                                Summary = $"拒绝恢复越界路径：{file.RelativePath}",
                                Payload = new Models.DiscardBackupRestoreResult { Success = false, BackupId = backupId, Summary = $"拒绝恢复越界路径：{file.RelativePath}" },
                            };
                        }

                        if (file.ExistsInWorktree && !file.IsSymlink)
                        {
                            var bytes = store.ReadWorktreePayloadBytes(file, out var readErr);
                            if (!string.IsNullOrEmpty(readErr))
                            {
                                return new OperationActionOutcome<Models.DiscardBackupRestoreResult>
                                {
                                    Stage = Models.GitOperationStage.Failed,
                                    Summary = readErr,
                                    Payload = new Models.DiscardBackupRestoreResult { Success = false, BackupId = backupId, Summary = readErr },
                                };
                            }

                            var parent = Path.GetDirectoryName(absPath);
                            if (!string.IsNullOrEmpty(parent) && !Directory.Exists(parent))
                                Directory.CreateDirectory(parent);

                            await File.WriteAllBytesAsync(absPath, bytes, cancellationToken).ConfigureAwait(false);
                        }
                        else if (file.ExistsInWorktree && file.IsSymlink)
                        {
                            var parent = Path.GetDirectoryName(absPath);
                            if (!string.IsNullOrEmpty(parent) && !Directory.Exists(parent))
                                Directory.CreateDirectory(parent);

                            if (File.Exists(absPath) || Directory.Exists(absPath))
                                effectiveRecycle(absPath);

                            File.CreateSymbolicLink(absPath, file.SymlinkTarget);
                        }
                        else if (!file.ExistsInWorktree && (File.Exists(absPath) || Directory.Exists(absPath)))
                        {
                            var (ok, err) = effectiveRecycle(absPath);
                            if (!ok)
                            {
                                return new OperationActionOutcome<Models.DiscardBackupRestoreResult>
                                {
                                    Stage = Models.GitOperationStage.Failed,
                                    Summary = $"还原丢弃前“工作区已删除”状态时移至回收站失败：{err}",
                                    Payload = new Models.DiscardBackupRestoreResult { Success = false, BackupId = backupId, Summary = err },
                                };
                            }
                        }

                        // For HEAD-baseline discards or index-altered states, restore exact index entry as well.
                        if (file.Baseline == Models.DiscardBaselineKind.RestoreToHead)
                        {
                            if (file.ExistsInIndex)
                            {
                                var idxBytes = store.ReadIndexPayloadBytes(file, out var idxErr);
                                string targetBlobOid = file.IndexBlobOid;
                                if (idxBytes != null && string.IsNullOrEmpty(idxErr))
                                {
                                    var hashRes = await Command.RunGitDirectWithInputAsync(
                                        repoPath,
                                        TimeSpan.FromSeconds(15),
                                        idxBytes,
                                        cancellationToken,
                                        "hash-object",
                                        "-w",
                                        "--stdin").ConfigureAwait(false);
                                    if (hashRes.IsSuccess && !string.IsNullOrWhiteSpace(hashRes.StdOut))
                                        targetBlobOid = hashRes.StdOut.Trim();
                                }

                                if (!string.IsNullOrEmpty(targetBlobOid))
                                {
                                    var mode = string.IsNullOrEmpty(file.IndexMode) ? "100644" : file.IndexMode;
                                    var upd = await Command.RunGitDirectAsync(
                                        repoPath,
                                        TimeSpan.FromSeconds(15),
                                        cancellationToken,
                                        "--literal-pathspecs",
                                        "update-index",
                                        "--add",
                                        "--cacheinfo",
                                        $"{mode},{targetBlobOid},{file.RelativePath}").ConfigureAwait(false);
                                    if (upd.IsSuccess)
                                        indexRestoredCount++;
                                }
                            }
                            else
                            {
                                await Command.RunGitDirectAsync(
                                    repoPath,
                                    TimeSpan.FromSeconds(10),
                                    cancellationToken,
                                    "--literal-pathspecs",
                                    "reset",
                                    "-q",
                                    "--",
                                    file.RelativePath).ConfigureAwait(false);
                            }
                        }

                        restoredPaths.Add(file.RelativePath);
                    }

                    store.MarkRestored(backupId, utcNowOverride);

                    var msg = $"已从恢复库还原 {restoredPaths.Count} 个文件（含暂存区条目还原 {indexRestoredCount} 项）。";
                    return new OperationActionOutcome<Models.DiscardBackupRestoreResult>
                    {
                        Stage = Models.GitOperationStage.Succeeded,
                        Summary = msg,
                        Payload = new Models.DiscardBackupRestoreResult
                        {
                            Success = true,
                            BackupId = backupId,
                            RestoredPaths = restoredPaths,
                            IndexRestoredCount = indexRestoredCount,
                            Summary = msg,
                        },
                    };
                },
                journalStore: journalStore,
                outerLog: outerLog,
                cancellationToken: cancellationToken).ConfigureAwait(false);

            return exec.Payload ?? new Models.DiscardBackupRestoreResult
            {
                Success = exec.Succeeded,
                BackupId = backupId,
                Summary = exec.Summary,
            };
        }

        private static async Task<Models.DiscardBackupCaptureInput> BuildCaptureInputAsync(
            string repoPath,
            string absPath,
            Models.DiscardTargetItem target,
            Models.DiscardBaselineKind baseline,
            Dictionary<string, IndexEntryRecord> indexInfo,
            CancellationToken cancellationToken)
        {
            try
            {
                var existsOnDisk = File.Exists(absPath);
                var isSymlink = false;
                var symlinkTarget = string.Empty;
                byte[] wtBytes = null;

                if (existsOnDisk)
                {
                    var fi = new FileInfo(absPath);
                    if (!string.IsNullOrEmpty(fi.LinkTarget))
                    {
                        isSymlink = true;
                        symlinkTarget = fi.LinkTarget;
                    }
                    else
                    {
                        wtBytes = await File.ReadAllBytesAsync(absPath, cancellationToken).ConfigureAwait(false);
                    }
                }
                else
                {
                    var symCheck = File.ResolveLinkTarget(absPath, false);
                    if (symCheck != null)
                    {
                        existsOnDisk = true;
                        isSymlink = true;
                        symlinkTarget = symCheck.FullName;
                    }
                }

                var hasIndex = indexInfo.TryGetValue(target.Path, out var idx);
                byte[] idxBytes = null;
                if (hasIndex && !string.IsNullOrEmpty(idx.BlobOid))
                {
                    idxBytes = await ReadGitBlobBytesAsync(repoPath, idx.BlobOid, cancellationToken).ConfigureAwait(false);
                }

                return new Models.DiscardBackupCaptureInput
                {
                    RelativePath = target.Path,
                    OriginalPath = target.OriginalPath,
                    Baseline = baseline,
                    ExistsInWorktree = existsOnDisk,
                    IsSymlink = isSymlink,
                    SymlinkTarget = symlinkTarget,
                    WorktreeBytes = wtBytes,
                    ExistsInIndex = hasIndex,
                    IndexMode = idx?.Mode ?? string.Empty,
                    IndexBlobOid = idx?.BlobOid ?? string.Empty,
                    IndexBytes = idxBytes,
                };
            }
            catch
            {
                return null;
            }
        }

        private static async Task<byte[]> ReadGitBlobBytesAsync(string repoPath, string blobOid, CancellationToken cancellationToken)
        {
            var gitExec = Native.OS.GitExecutable;
            if (string.IsNullOrEmpty(gitExec))
                return null;

            using var proc = new System.Diagnostics.Process();
            proc.StartInfo = new System.Diagnostics.ProcessStartInfo(gitExec)
            {
                WorkingDirectory = repoPath,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            proc.StartInfo.ArgumentList.Add("cat-file");
            proc.StartInfo.ArgumentList.Add("blob");
            proc.StartInfo.ArgumentList.Add(blobOid);

            proc.Start();
            using var ms = new MemoryStream();
            var copyTask = proc.StandardOutput.BaseStream.CopyToAsync(ms, cancellationToken);
            var errTask = proc.StandardError.ReadToEndAsync(cancellationToken);
            await Task.WhenAll(copyTask, errTask, proc.WaitForExitAsync(cancellationToken)).ConfigureAwait(false);
            return proc.ExitCode == 0 ? ms.ToArray() : null;
        }

        private static async Task<Dictionary<string, IndexEntryRecord>> ReadIndexEntriesAsync(
            string repoPath,
            CancellationToken cancellationToken)
        {
            var map = new Dictionary<string, IndexEntryRecord>(PathComparer);
            var res = await Command.RunGitDirectAsync(
                repoPath,
                cancellationToken,
                "--no-pager",
                "-c",
                "core.quotepath=off",
                "ls-files",
                "-s",
                "-z").ConfigureAwait(false);

            if (!res.IsSuccess || string.IsNullOrEmpty(res.StdOut))
                return map;

            foreach (var rec in res.StdOut.Split('\0', StringSplitOptions.RemoveEmptyEntries))
            {
                var tab = rec.IndexOf('\t');
                if (tab <= 0 || tab + 1 >= rec.Length)
                    continue;

                var meta = rec[..tab].Split(' ', StringSplitOptions.RemoveEmptyEntries);
                var path = NormalizeRelPath(rec[(tab + 1)..]);
                if (meta.Length >= 3)
                {
                    map[path] = new IndexEntryRecord(meta[0], meta[1], meta[2]);
                }
            }

            return map;
        }

        private static async Task<HashSet<string>> ReadHeadTreePathsAsync(string repoPath, CancellationToken cancellationToken)
        {
            var set = new HashSet<string>(PathComparer);
            var res = await Command.RunGitDirectAsync(
                repoPath,
                cancellationToken,
                "--no-pager",
                "-c",
                "core.quotepath=off",
                "ls-tree",
                "-r",
                "--name-only",
                "-z",
                "HEAD").ConfigureAwait(false);

            if (!res.IsSuccess || string.IsNullOrEmpty(res.StdOut))
                return set;

            foreach (var p in res.StdOut.Split('\0', StringSplitOptions.RemoveEmptyEntries))
                set.Add(NormalizeRelPath(p));

            return set;
        }

        private static async Task<string> ReadHeadShaAsync(string repoPath, CancellationToken cancellationToken)
        {
            var res = await Command.RunGitDirectAsync(repoPath, cancellationToken, "rev-parse", "--verify", "-q", "HEAD")
                .ConfigureAwait(false);
            return res.IsSuccess ? res.StdOut.Trim() : string.Empty;
        }

        private static OperationActionOutcome<Models.DiscardExecutionResult> FailBeforeMutation(
            Models.DiscardBaselineKind baseline,
            string reason,
            string opId)
        {
            return new OperationActionOutcome<Models.DiscardExecutionResult>
            {
                Stage = Models.GitOperationStage.Failed,
                Summary = reason,
                DiagnosticDetail = reason,
                Payload = new Models.DiscardExecutionResult
                {
                    Success = false,
                    Baseline = baseline,
                    Summary = reason,
                    OperationRecordId = opId,
                },
            };
        }

        private static (bool Success, string Error) DefaultRecycleHandler(string path)
        {
            var ok = Native.OS.MoveToRecycleBin(path, out var err);
            return (ok, err);
        }

        private static string ResolveSafeRepoPathAny(string repoPath, string relPath)
        {
            if (string.IsNullOrWhiteSpace(repoPath) || string.IsNullOrWhiteSpace(relPath))
                return null;

            try
            {
                var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(repoPath));
                var prefix = root + Path.DirectorySeparatorChar;
                var full = Path.GetFullPath(Path.Combine(root, relPath.Replace('/', Path.DirectorySeparatorChar)));
                var comp = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
                if (!full.StartsWith(prefix, comp))
                    return null;

                return full;
            }
            catch
            {
                return null;
            }
        }

        private static string NormalizeRelPath(string path) => path.Trim().Replace('\\', '/').TrimEnd('/');

        private static string CombineError(Command.Result res, string fallback)
        {
            var detail = string.IsNullOrWhiteSpace(res.StdErr) ? res.StdOut : res.StdErr;
            detail = Models.GitConfigSnapshot.RedactText(detail?.Trim() ?? string.Empty);
            return string.IsNullOrWhiteSpace(detail) ? fallback : detail;
        }

        private static void AppendStreams(Models.ICommandLog log, Command.Result res)
        {
            if (log == null)
                return;
            if (!string.IsNullOrWhiteSpace(res.StdOut))
                log.AppendLine(res.StdOut.TrimEnd());
            if (!string.IsNullOrWhiteSpace(res.StdErr))
                log.AppendLine(res.StdErr.TrimEnd());
        }

        private sealed record IndexEntryRecord(string Mode, string BlobOid, string Stage);
    }
}
