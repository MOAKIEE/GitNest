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
    /// 2. RestoreToHead (`git restore --staged --worktree --source=HEAD`, plus index-added cleanup via reset + backup + configured removal mode).
    /// 3. MoveUntrackedToRecycleBin (cleans untracked files/symlinks/empty dirs using configured RecycleBin or Direct delete after backup).
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
            Models.FileRemovalMode removalMode = Models.FileRemovalMode.RecycleBin,
            IReadOnlyList<string> selectedFolderPaths = null,
            CancellationToken cancellationToken = default)
        {
            return await InspectDiscardCoreAsync(
                repoPath,
                targets,
                baseline,
                Models.FileRemovalModeExtensions.Normalize(removalMode),
                selectedFolderPaths,
                includeSnapshot: true,
                cancellationToken).ConfigureAwait(false);
        }

        private static async Task<Models.DiscardPreflight> InspectDiscardCoreAsync(
            string repoPath,
            IReadOnlyList<Models.Change> targets,
            Models.DiscardBaselineKind baseline,
            Models.FileRemovalMode removalMode,
            IReadOnlyList<string> selectedFolderPaths,
            bool includeSnapshot,
            CancellationToken cancellationToken)
        {
            var normalizedMode = Models.FileRemovalModeExtensions.Normalize(removalMode);
            var normalizedFolders = (selectedFolderPaths ?? [])
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .Select(NormalizeRelPath)
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .Distinct(PathComparer)
                .ToList();

            if ((targets == null || targets.Count == 0) && normalizedFolders.Count == 0)
            {
                return new Models.DiscardPreflight
                {
                    Baseline = baseline,
                    RemovalMode = normalizedMode,
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
                    RemovalMode = normalizedMode,
                    CanProceed = false,
                    HasUnbornHead = true,
                    BlockingReason = "当前仓库尚未创建首次提交（unborn HEAD），无法恢复到上次提交（HEAD）。请先创建首次提交，或改用清理未跟踪文件。",
                };
            }

            var (repoGitDir, repoCommonDir) = await Models.RepositoryWriteGuard.ResolveGitDirectoriesAsync(repoPath, cancellationToken).ConfigureAwait(false);
            var headPaths = baseline == Models.DiscardBaselineKind.RestoreToHead
                ? await ReadHeadTreePathsAsync(repoPath, cancellationToken).ConfigureAwait(false)
                : new HashSet<string>(PathComparer);
            var indexInfo = await ReadIndexEntriesAsync(repoPath, cancellationToken).ConfigureAwait(false);

            var tracked = new List<Models.DiscardTargetItem>();
            var untracked = new List<Models.DiscardTargetItem>();
            var cleanableDirs = new List<string>();
            var skippedSubmodules = new List<string>();
            var skippedNestedRepos = new List<string>();
            var skippedIgnored = new List<string>();
            var invalidPaths = new List<string>();
            var indexAddedOnly = new List<string>();
            var renameCollisions = new List<string>();

            var targetByPath = new Dictionary<string, Models.Change>(PathComparer);
            var directUntrackedLeaves = new List<(string RelPath, DiskEntryInfo Info, Models.Change Source)>();
            var dirQueue = new Queue<string>();
            var visitedDirs = new HashSet<string>(PathComparer);
            var explicitExpandRoots = new HashSet<string>(normalizedFolders, PathComparer);

            if (targets != null)
            {
                foreach (var c in targets)
                {
                    if (c == null || string.IsNullOrWhiteSpace(c.Path))
                        continue;

                    var relPath = NormalizeRelPath(c.Path);
                    var isUntracked = c.WorkTree == Models.ChangeState.Untracked || c.Index == Models.ChangeState.Untracked;
                    if (isUntracked &&
                        TryResolveSafeRepoTarget(repoPath, relPath, repoGitDir, repoCommonDir, out var dirAbs, out _, out _) &&
                        TryGetDiskEntryInfo(dirAbs, out var dirInfo) &&
                        !dirInfo.IsSymlink &&
                        dirInfo.IsDirectory &&
                        !HasDotGitEntry(dirAbs))
                    {
                        explicitExpandRoots.Add(relPath);
                    }
                }

                foreach (var c in targets)
                {
                    if (c == null || string.IsNullOrWhiteSpace(c.Path))
                        continue;

                    var relPath = NormalizeRelPath(c.Path);
                    var origPath = string.IsNullOrWhiteSpace(c.OriginalPath) ? string.Empty : NormalizeRelPath(c.OriginalPath);
                    targetByPath.TryAdd(relPath, c);

                    if (!TryResolveSafeRepoTarget(
                            repoPath,
                            relPath,
                            repoGitDir,
                            repoCommonDir,
                            out var absPath,
                            out _,
                            out var symlinkAncestorRel))
                    {
                        if (!string.IsNullOrEmpty(symlinkAncestorRel) && IsCoveredByExpandRoots(symlinkAncestorRel, explicitExpandRoots))
                            continue;

                        AddUniquePath(invalidPaths, relPath);
                        continue;
                    }

                    var isGitlinkMode = indexInfo.TryGetValue(relPath, out var idxEntry) &&
                                        string.Equals(idxEntry.Mode, "160000", StringComparison.Ordinal);
                    if (isGitlinkMode)
                    {
                        AddUniquePath(skippedSubmodules, relPath);
                        continue;
                    }

                    TryGetDiskEntryInfo(absPath, out var diskInfo);
                    var isNestedRepoDir = diskInfo.Exists && !diskInfo.IsSymlink && diskInfo.IsDirectory && HasDotGitEntry(absPath);

                    var isUntracked = c.WorkTree == Models.ChangeState.Untracked || c.Index == Models.ChangeState.Untracked;
                    if (isUntracked)
                    {
                        if (isNestedRepoDir)
                        {
                            AddUniquePath(skippedNestedRepos, relPath);
                            continue;
                        }

                        if (!diskInfo.Exists)
                            continue;

                        if (diskInfo.IsSymlink)
                        {
                            directUntrackedLeaves.Add((relPath, diskInfo, c));
                        }
                        else if (diskInfo.IsDirectory)
                        {
                            if (visitedDirs.Add(relPath))
                                dirQueue.Enqueue(relPath);
                        }
                        else
                        {
                            directUntrackedLeaves.Add((relPath, diskInfo, c));
                        }

                        continue;
                    }

                    if (isNestedRepoDir)
                    {
                        AddUniquePath(skippedSubmodules, relPath);
                        continue;
                    }

                    if (baseline == Models.DiscardBaselineKind.RestoreToHead &&
                        (c.Index == Models.ChangeState.Renamed || c.WorkTree == Models.ChangeState.Renamed) &&
                        !string.IsNullOrEmpty(origPath))
                    {
                        if (!TryResolveSafeRepoTarget(repoPath, origPath, repoGitDir, repoCommonDir, out var origAbs, out _, out _))
                        {
                            AddUniquePath(invalidPaths, origPath);
                            continue;
                        }

                        if (!indexInfo.ContainsKey(origPath) && EntryExistsOnDisk(origAbs))
                            AddUniquePath(renameCollisions, origPath);
                    }

                    var item = new Models.DiscardTargetItem
                    {
                        Path = relPath,
                        OriginalPath = origPath,
                        Index = c.Index,
                        WorkTree = c.WorkTree,
                        IsSubmodule = false,
                        IsFolder = diskInfo.Exists && !diskInfo.IsSymlink && diskInfo.IsDirectory,
                        IsSymlink = diskInfo.IsSymlink,
                        IsDirectorySymlink = diskInfo.IsDirectorySymlink,
                    };

                    if (baseline == Models.DiscardBaselineKind.RestoreToHead && !headPaths.Contains(relPath))
                    {
                        item.IsIndexAddedOnly = true;
                        AddUniquePath(indexAddedOnly, relPath);
                    }

                    tracked.Add(item);
                }
            }

            if (baseline == Models.DiscardBaselineKind.MoveUntrackedToRecycleBin)
            {
                foreach (var folderRel in normalizedFolders)
                {
                    if (!TryResolveSafeRepoTarget(repoPath, folderRel, repoGitDir, repoCommonDir, out var folderAbs, out _, out var symAnc))
                    {
                        if (string.IsNullOrEmpty(symAnc) || !IsCoveredByExpandRoots(symAnc, explicitExpandRoots))
                            AddUniquePath(invalidPaths, folderRel);
                        continue;
                    }

                    if (!TryGetDiskEntryInfo(folderAbs, out var folderInfo) || !folderInfo.Exists)
                        continue;

                    if (indexInfo.TryGetValue(folderRel, out var fIdx) && string.Equals(fIdx.Mode, "160000", StringComparison.Ordinal))
                    {
                        AddUniquePath(skippedSubmodules, folderRel);
                        continue;
                    }

                    if (folderInfo.IsSymlink)
                    {
                        if (!indexInfo.ContainsKey(folderRel))
                        {
                            targetByPath.TryGetValue(folderRel, out var matched);
                            directUntrackedLeaves.Add((folderRel, folderInfo, matched));
                        }
                        continue;
                    }

                    if (!folderInfo.IsDirectory)
                        continue;

                    if (HasDotGitEntry(folderAbs))
                    {
                        AddUniquePath(skippedNestedRepos, folderRel);
                        continue;
                    }

                    if (visitedDirs.Add(folderRel))
                        dirQueue.Enqueue(folderRel);
                }

                await ExpandUntrackedCandidatesAsync(
                    repoPath,
                    repoGitDir,
                    repoCommonDir,
                    indexInfo,
                    targetByPath,
                    directUntrackedLeaves,
                    dirQueue,
                    untracked,
                    cleanableDirs,
                    skippedSubmodules,
                    skippedNestedRepos,
                    skippedIgnored,
                    cancellationToken).ConfigureAwait(false);
            }
            else if (baseline == Models.DiscardBaselineKind.RestoreToHead && indexAddedOnly.Count > 0 && normalizedFolders.Count > 0)
            {
                foreach (var folderRel in normalizedFolders)
                {
                    if (TryResolveSafeRepoTarget(repoPath, folderRel, repoGitDir, repoCommonDir, out var folderAbs, out _, out _) &&
                        TryGetDiskEntryInfo(folderAbs, out var folderInfo) &&
                        folderInfo.Exists &&
                        !folderInfo.IsSymlink &&
                        folderInfo.IsDirectory &&
                        !HasDotGitEntry(folderAbs))
                    {
                        AddUniquePath(cleanableDirs, folderRel);
                    }
                }
            }

            var hasActionableEmptyDir = baseline == Models.DiscardBaselineKind.MoveUntrackedToRecycleBin &&
                                        untracked.Count == 0 &&
                                        cleanableDirs.Any(d =>
                                            TryResolveSafeRepoTarget(repoPath, d, repoGitDir, repoCommonDir, out var abs, out _, out _) &&
                                            Directory.Exists(abs) &&
                                            !HasAnyDirectoryEntries(abs));

            var confirmationPaths = baseline == Models.DiscardBaselineKind.MoveUntrackedToRecycleBin
                ? untracked.Select(u => u.Path).Distinct(PathComparer).ToList()
                : tracked
                    .SelectMany(t => baseline == Models.DiscardBaselineKind.RestoreToHead && HasRenameSource(t)
                        ? new[] { t.Path, t.OriginalPath }
                        : new[] { t.Path })
                    .Where(p => !string.IsNullOrWhiteSpace(p))
                    .Distinct(PathComparer)
                    .ToList();

            Models.OperationConfirmationSnapshot confirmationSnapshot = null;
            if (includeSnapshot && confirmationPaths.Count > 0)
            {
                confirmationSnapshot = await CaptureOperationSnapshot.CaptureAsync(
                    repoPath,
                    new Models.OperationSnapshotCaptureOptions
                    {
                        TargetPaths = confirmationPaths,
                        IncludeTargetFileDetails = true,
                    },
                    cancellationToken).ConfigureAwait(false);
            }

            if (invalidPaths.Count > 0)
            {
                return new Models.DiscardPreflight
                {
                    Baseline = baseline,
                    RemovalMode = normalizedMode,
                    CanProceed = false,
                    HasUnbornHead = !hasHead,
                    BlockingReason = $"拒绝处理越界、.git 元数据或符号链接穿透路径：{string.Join("、", invalidPaths)}",
                    TrackedTargets = tracked,
                    UntrackedTargets = untracked,
                    CleanableDirectories = cleanableDirs,
                    SkippedSubmodulePaths = skippedSubmodules,
                    SkippedNestedRepoPaths = skippedNestedRepos,
                    SkippedIgnoredPaths = skippedIgnored,
                    InvalidPaths = invalidPaths,
                    IndexAddedOnlyPaths = indexAddedOnly,
                    ConfirmationSnapshot = confirmationSnapshot,
                };
            }

            if (baseline == Models.DiscardBaselineKind.MoveUntrackedToRecycleBin)
            {
                var canProceed = untracked.Count > 0 || hasActionableEmptyDir;
                var blockingReason = string.Empty;
                if (!canProceed)
                {
                    if (skippedSubmodules.Count > 0)
                        blockingReason = "子模块条目不能通过未跟踪文件清理流程删除，已跳过。";
                    else if (skippedNestedRepos.Count > 0)
                        blockingReason = "嵌套 Git 仓库不会被自动展开或删除，已跳过。";
                    else if (skippedIgnored.Count > 0)
                        blockingReason = "所选条目仅包含被 Git 忽略的文件或目录，已按安全策略跳过。";
                    else
                        blockingReason = normalizedMode == Models.FileRemovalMode.Direct
                            ? "所选条目中没有可直接删除的未跟踪文件或目录。"
                            : "所选条目中没有可移至回收站的未跟踪文件或目录。";
                }

                return new Models.DiscardPreflight
                {
                    Baseline = baseline,
                    RemovalMode = normalizedMode,
                    CanProceed = canProceed,
                    HasUnbornHead = !hasHead,
                    BlockingReason = blockingReason,
                    TrackedTargets = tracked,
                    UntrackedTargets = untracked,
                    CleanableDirectories = cleanableDirs,
                    SkippedSubmodulePaths = skippedSubmodules,
                    SkippedNestedRepoPaths = skippedNestedRepos,
                    SkippedIgnoredPaths = skippedIgnored,
                    InvalidPaths = invalidPaths,
                    ConfirmationSnapshot = confirmationSnapshot,
                };
            }

            return new Models.DiscardPreflight
            {
                Baseline = baseline,
                RemovalMode = normalizedMode,
                CanProceed = tracked.Count > 0 && renameCollisions.Count == 0,
                HasUnbornHead = !hasHead,
                BlockingReason = renameCollisions.Count > 0
                    ? $"重命名的原路径包含新文件或目录，请先移走或保存后再恢复：{string.Join("、", renameCollisions)}"
                    : tracked.Count > 0
                        ? string.Empty
                        : skippedSubmodules.Count > 0
                            ? "暂不支持按文件恢复库直接覆盖子模块变更。"
                            : "所选条目中没有符合当前基线的已跟踪文件修改。",
                TrackedTargets = tracked,
                UntrackedTargets = untracked,
                CleanableDirectories = cleanableDirs,
                SkippedSubmodulePaths = skippedSubmodules,
                SkippedNestedRepoPaths = skippedNestedRepos,
                SkippedIgnoredPaths = skippedIgnored,
                InvalidPaths = invalidPaths,
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
            Models.DiscardPreflight expectedPreflight = null,
            Models.FileRemovalMode removalMode = Models.FileRemovalMode.RecycleBin,
            Func<string, (bool Success, string Error)> directDeleteHandler = null,
            IReadOnlyList<string> selectedFolderPaths = null)
        {
            var normalizedMode = Models.FileRemovalModeExtensions.Normalize(removalMode);
            var effectiveFolderPaths = selectedFolderPaths ?? expectedPreflight?.CleanableDirectories;
            var preflight = await InspectDiscardAsync(
                repoPath,
                targets,
                baseline,
                normalizedMode,
                effectiveFolderPaths,
                cancellationToken).ConfigureAwait(false);

            if (!preflight.CanProceed)
            {
                var preReasons = new Dictionary<string, string>(PathComparer);
                foreach (var p in preflight.InvalidPaths)
                    preReasons[p] = preflight.BlockingReason;
                return new Models.DiscardExecutionResult
                {
                    Success = false,
                    Baseline = baseline,
                    RemovalMode = normalizedMode,
                    FailedPaths = preflight.InvalidPaths.ToList(),
                    FailureReasons = preReasons,
                    Summary = preflight.BlockingReason,
                };
            }

            if (expectedPreflight != null && !PreflightTargetsMatch(expectedPreflight, preflight))
            {
                const string changedMsg = "确认期间目标列表或目录内容已发生变化，已中止操作。请刷新更改列表后重新确认。";
                return new Models.DiscardExecutionResult
                {
                    Success = false,
                    BlockedBySnapshot = true,
                    Baseline = baseline,
                    RemovalMode = normalizedMode,
                    Summary = changedMsg,
                };
            }

            if (baseline == Models.DiscardBaselineKind.MoveUntrackedToRecycleBin)
            {
                return await ExecuteCleanUntrackedAsync(
                    repoPath,
                    targets,
                    preflight,
                    normalizedMode,
                    backupStore,
                    recycleHandler,
                    directDeleteHandler,
                    journalStore,
                    outerLog,
                    utcNowOverride,
                    cancellationToken,
                    expectedPreflight?.ConfirmationSnapshot ?? preflight.ConfirmationSnapshot,
                    effectiveFolderPaths).ConfigureAwait(false);
            }

            var effectiveStore = backupStore ?? Models.DiscardBackupStore.Instance;
            var effectiveRecycle = recycleHandler ?? DefaultRecycleHandler;
            var effectiveDirectDelete = directDeleteHandler ?? DefaultDirectDeleteHandler;
            var (repoGitDir, repoCommonDir) = await Models.RepositoryWriteGuard.ResolveGitDirectoriesAsync(repoPath, cancellationToken).ConfigureAwait(false);

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
                    var backupTargets = preflight.TrackedTargets.ToList();
                    if (baseline == Models.DiscardBaselineKind.RestoreToHead)
                    {
                        foreach (var original in preflight.TrackedTargets.Where(HasRenameSource).Select(t => t.OriginalPath))
                        {
                            if (!backupTargets.Any(t => PathComparer.Equals(t.Path, original)))
                                backupTargets.Add(new Models.DiscardTargetItem { Path = original });
                        }
                    }

                    var captureInputs = new List<Models.DiscardBackupCaptureInput>(backupTargets.Count);
                    var invalidOrFailed = new List<string>();
                    var invalidReasons = new Dictionary<string, string>(PathComparer);

                    foreach (var target in backupTargets)
                    {
                        if (!TryResolveSafeRepoTarget(repoPath, target.Path, repoGitDir, repoCommonDir, out var absPath, out var pathReason, out _))
                        {
                            invalidOrFailed.Add(target.Path);
                            invalidReasons[target.Path] = pathReason;
                            continue;
                        }

                        var targetRemovalMode = baseline == Models.DiscardBaselineKind.RestoreToHead && target.IsIndexAddedOnly
                            ? normalizedMode
                            : Models.FileRemovalMode.RecycleBin;
                        var input = await BuildCaptureInputAsync(
                            repoPath,
                            absPath,
                            target,
                            baseline,
                            targetRemovalMode,
                            indexInfo,
                            cancellationToken).ConfigureAwait(false);
                        if (input == null)
                        {
                            invalidOrFailed.Add(target.Path);
                            invalidReasons[target.Path] = "读取待备份文件内容失败，已中止丢弃以防数据丢失。";
                            continue;
                        }

                        captureInputs.Add(input);
                    }

                    if (invalidOrFailed.Count > 0)
                    {
                        var firstReason = $"{invalidOrFailed[0]}: {invalidReasons[invalidOrFailed[0]]}";
                        return FailBeforeMutation(
                            baseline,
                            normalizedMode,
                            firstReason,
                            ctx.OperationId,
                            invalidOrFailed,
                            invalidReasons);
                    }

                    var headSha = await ReadHeadShaAsync(repoPath, cancellationToken).ConfigureAwait(false);
                    var entryRemovalMode = preflight.IncludesFileRemoval
                        ? normalizedMode
                        : Models.FileRemovalMode.RecycleBin;
                    var capture = effectiveStore.Capture(
                        repoPath,
                        ctx.CanonicalCommonDir,
                        headSha,
                        baseline,
                        title,
                        captureInputs,
                        utcNowOverride,
                        entryRemovalMode);

                    List<string> corruptPaths = null;
                    if (!capture.Success || !effectiveStore.VerifyPayloadIntegrity(capture.Entry, out corruptPaths))
                    {
                        var err = capture.Success
                            ? $"恢复库载荷校验失败：{string.Join("、", corruptPaths ?? [])}"
                            : capture.Error;
                        var failTargets = backupTargets.Select(t => t.Path).ToList();
                        var capReasons = new Dictionary<string, string>(PathComparer);
                        foreach (var ft in failTargets)
                            capReasons[ft] = err;
                        return FailBeforeMutation(
                            baseline,
                            normalizedMode,
                            $"写入 30 天本地恢复库失败，已中止丢弃操作（工作区未作任何修改）：{err}",
                            ctx.OperationId,
                            failTargets,
                            capReasons);
                    }

                    ctx.Log.AppendLine($"[DiscardBackup] 已写入 30 天本地恢复库 ID={capture.BackupId}");

                    var restored = new List<string>();
                    var recycled = new List<string>();
                    var directDeleted = new List<string>();
                    var skipped = new List<string>();
                    var failed = new List<string>();
                    var failureReasons = new Dictionary<string, string>(PathComparer);

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
                            var cmdReasons = new Dictionary<string, string>(PathComparer);
                            foreach (var rp in restorePaths)
                                cmdReasons[rp] = errMsg;
                            return new OperationActionOutcome<Models.DiscardExecutionResult>
                            {
                                Stage = Models.GitOperationStage.Failed,
                                Summary = $"{errMsg}（原内容已安全备份在恢复库 {capture.BackupId}）",
                                DiagnosticDetail = errMsg,
                                Payload = new Models.DiscardExecutionResult
                                {
                                    Success = false,
                                    Baseline = baseline,
                                    RemovalMode = normalizedMode,
                                    BackupId = capture.BackupId,
                                    FailedPaths = restorePaths,
                                    FailureReasons = cmdReasons,
                                    Summary = $"{errMsg}（原内容已备份 ID={capture.BackupId}）",
                                    OperationRecordId = ctx.OperationId,
                                },
                            };
                        }

                        restored.AddRange(restorePaths);
                    }
                    else
                    {
                        var headRestoreSet = new HashSet<string>(PathComparer);
                        foreach (var t in preflight.TrackedTargets)
                        {
                            if (!t.IsIndexAddedOnly)
                                headRestoreSet.Add(t.Path);
                            if (HasRenameSource(t))
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
                                var headReasons = new Dictionary<string, string>(PathComparer);
                                foreach (var hp in headRestorePaths)
                                    headReasons[hp] = errMsg;
                                return new OperationActionOutcome<Models.DiscardExecutionResult>
                                {
                                    Stage = Models.GitOperationStage.Failed,
                                    Summary = $"{errMsg}（备份 ID={capture.BackupId}）",
                                    DiagnosticDetail = errMsg,
                                    Payload = new Models.DiscardExecutionResult
                                    {
                                        Success = false,
                                        Baseline = baseline,
                                        RemovalMode = normalizedMode,
                                        BackupId = capture.BackupId,
                                        FailedPaths = headRestorePaths,
                                        FailureReasons = headReasons,
                                        Summary = $"{errMsg}（备份 ID={capture.BackupId}）",
                                        OperationRecordId = ctx.OperationId,
                                    },
                                };
                            }

                            restored.AddRange(headRestorePaths);
                        }

                        if (indexAddedTargets.Count > 0)
                        {
                            // Unstage files added to index (or new rename targets) that are not in HEAD, then remove via chosen removal mode.
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
                                    var errMsg = CombineError(resetRes, "取消暂存新增文件失败。");
                                    failed.Add(t.Path);
                                    failureReasons[t.Path] = errMsg;
                                    continue;
                                }

                                if (!TryResolveSafeRepoTarget(repoPath, t.Path, repoGitDir, repoCommonDir, out var absPath, out var pathReason, out _))
                                {
                                    failed.Add(t.Path);
                                    failureReasons[t.Path] = pathReason;
                                    continue;
                                }

                                if (EntryExistsOnDisk(absPath))
                                {
                                    var (ok, removeErr) = RemoveFileOrSymlink(absPath, normalizedMode, effectiveRecycle, effectiveDirectDelete);
                                    if (!ok || EntryExistsOnDisk(absPath))
                                    {
                                        var reason = string.IsNullOrWhiteSpace(removeErr)
                                            ? (normalizedMode == Models.FileRemovalMode.Direct ? "直接删除失败，原文件保留在磁盘。" : "系统回收站拒绝或未能移走目标（原文件仍保留在磁盘）。")
                                            : removeErr;
                                        ctx.Log.AppendLine($"[RemoveFailed:{normalizedMode}] {t.Path}: {reason}");
                                        failed.Add(t.Path);
                                        failureReasons[t.Path] = reason;
                                        continue;
                                    }

                                    if (normalizedMode == Models.FileRemovalMode.Direct)
                                        directDeleted.Add(t.Path);
                                    else
                                        recycled.Add(t.Path);
                                }
                                else
                                {
                                    restored.Add(t.Path);
                                }
                            }

                            PruneCleanableEmptyDirectories(repoPath, repoGitDir, repoCommonDir, preflight.CleanableDirectories, ctx.Log);
                        }
                    }

                    // Record exact post-discard disk + index hashes so subsequent-modification detection works accurately.
                    var postIndex = await ReadIndexEntriesAsync(repoPath, cancellationToken).ConfigureAwait(false);
                    var postStates = new Dictionary<string, (string WorktreeSha256, string IndexBlobOid)>(PathComparer);
                    foreach (var t in backupTargets)
                    {
                        var wtSha = TryResolveSafeRepoTarget(repoPath, t.Path, repoGitDir, repoCommonDir, out var abs, out _, out _)
                            ? ComputeLiveWorktreeStateSha(abs)
                            : Models.DiscardBackupStore.AbsentMarker;
                        var idxOid = postIndex.TryGetValue(t.Path, out var idxEntry)
                            ? idxEntry.BlobOid
                            : Models.DiscardBackupStore.AbsentMarker;
                        postStates[t.Path] = (wtSha, idxOid);
                    }
                    effectiveStore.UpdatePostDiscardState(capture.BackupId, postStates);

                    var processed = restored.Concat(recycled).Concat(directDeleted).Distinct(PathComparer).ToList();
                    var allOk = failed.Count == 0;
                    var finalSummary = allOk
                        ? $"已完成 {title}，原内容已存入 30 天本地恢复库（ID：{capture.BackupId}）。"
                        : $"部分路径未能完成{(normalizedMode == Models.FileRemovalMode.Direct ? "直接删除" : "移至回收站")}（{string.Join(", ", failed)}），原文件保留在磁盘，已写入恢复库（ID：{capture.BackupId}）。";

                    return new OperationActionOutcome<Models.DiscardExecutionResult>
                    {
                        Stage = allOk
                            ? Models.GitOperationStage.Succeeded
                            : processed.Count > 0
                                ? Models.GitOperationStage.PartialSuccess
                                : Models.GitOperationStage.Failed,
                        Summary = finalSummary,
                        DiagnosticDetail = string.Join(Environment.NewLine, failureReasons.Select(kv => $"{kv.Key}: {kv.Value}")),
                        Payload = new Models.DiscardExecutionResult
                        {
                            Success = allOk,
                            Baseline = baseline,
                            RemovalMode = normalizedMode,
                            BackupId = capture.BackupId,
                            ProcessedPaths = processed,
                            RestoredPaths = restored,
                            RecycledPaths = recycled,
                            DirectDeletedPaths = directDeleted,
                            SkippedPaths = skipped,
                            FailedPaths = failed,
                            FailureReasons = failureReasons,
                            Summary = finalSummary,
                            OperationRecordId = ctx.OperationId,
                        },
                    };
                },
                expectedSnapshot: expectedPreflight?.ConfirmationSnapshot ?? preflight.ConfirmationSnapshot,
                verifyOptions: DiscardVerifyOptions(),
                journalStore: journalStore,
                outerLog: outerLog,
                cancellationToken: cancellationToken).ConfigureAwait(false);

            return exec.Payload ?? new Models.DiscardExecutionResult
            {
                Success = exec.Succeeded,
                BlockedBySnapshot = exec.BlockedBySnapshot,
                Baseline = baseline,
                RemovalMode = normalizedMode,
                Summary = exec.Summary,
                OperationRecordId = exec.Record?.OperationId ?? string.Empty,
            };
        }

        private static async Task<Models.DiscardExecutionResult> ExecuteCleanUntrackedAsync(
            string repoPath,
            IReadOnlyList<Models.Change> rawTargets,
            Models.DiscardPreflight preflight,
            Models.FileRemovalMode removalMode,
            Models.DiscardBackupStore backupStore,
            Func<string, (bool Success, string Error)> recycleHandler,
            Func<string, (bool Success, string Error)> directDeleteHandler,
            Models.OperationJournalStore journalStore,
            Models.ICommandLog outerLog,
            DateTime? utcNowOverride,
            CancellationToken cancellationToken,
            Models.OperationConfirmationSnapshot expectedSnapshot,
            IReadOnlyList<string> selectedFolderPaths)
        {
            var effectiveStore = backupStore ?? Models.DiscardBackupStore.Instance;
            var effectiveRecycle = recycleHandler ?? DefaultRecycleHandler;
            var effectiveDirectDelete = directDeleteHandler ?? DefaultDirectDeleteHandler;
            var normalizedMode = Models.FileRemovalModeExtensions.Normalize(removalMode);
            var (repoGitDir, repoCommonDir) = await Models.RepositoryWriteGuard.ResolveGitDirectoriesAsync(repoPath, cancellationToken).ConfigureAwait(false);

            var countLabel = preflight.UntrackedTargets.Count > 0
                ? $"{preflight.UntrackedTargets.Count} 个未跟踪项目"
                : $"{preflight.CleanableDirectories.Count} 个空目录";
            var title = normalizedMode == Models.FileRemovalMode.Direct
                ? $"直接删除 {countLabel}"
                : $"将 {countLabel} 移至回收站";

            var exec = await OperationJournalService.ExecuteGuardedWriteAsync(
                repoPath: repoPath,
                kind: Models.GitOperationKind.Discard,
                title: title,
                action: async ctx =>
                {
                    var liveInspect = await InspectDiscardCoreAsync(
                        repoPath,
                        rawTargets,
                        Models.DiscardBaselineKind.MoveUntrackedToRecycleBin,
                        normalizedMode,
                        selectedFolderPaths,
                        includeSnapshot: false,
                        cancellationToken).ConfigureAwait(false);

                    if (!liveInspect.CanProceed || !PreflightTargetsMatch(preflight, liveInspect))
                    {
                        const string driftMsg = "确认期间目标目录或文件状态已发生变化，已中止清理。请刷新更改列表后重新确认。";
                        return new OperationActionOutcome<Models.DiscardExecutionResult>
                        {
                            Stage = Models.GitOperationStage.Failed,
                            Summary = driftMsg,
                            DiagnosticDetail = driftMsg,
                            Payload = new Models.DiscardExecutionResult
                            {
                                Success = false,
                                BlockedBySnapshot = true,
                                Baseline = Models.DiscardBaselineKind.MoveUntrackedToRecycleBin,
                                RemovalMode = normalizedMode,
                                Summary = driftMsg,
                                OperationRecordId = ctx.OperationId,
                            },
                        };
                    }

                    var trackedSet = await ReadIndexEntriesAsync(repoPath, cancellationToken).ConfigureAwait(false);
                    var validationFailed = new List<string>();
                    var validationReasons = new Dictionary<string, string>(PathComparer);

                    foreach (var target in liveInspect.UntrackedTargets)
                    {
                        if (trackedSet.ContainsKey(target.Path))
                        {
                            validationFailed.Add(target.Path);
                            validationReasons[target.Path] = "已受版本库索引跟踪，拒绝按未跟踪文件删除。";
                            continue;
                        }

                        if (!TryResolveSafeRepoTarget(repoPath, target.Path, repoGitDir, repoCommonDir, out _, out var pathErr, out _))
                        {
                            validationFailed.Add(target.Path);
                            validationReasons[target.Path] = pathErr;
                        }
                    }

                    if (validationFailed.Count > 0)
                    {
                        var firstReason = $"{validationFailed[0]}: {validationReasons[validationFailed[0]]}";
                        return FailBeforeMutation(
                            Models.DiscardBaselineKind.MoveUntrackedToRecycleBin,
                            normalizedMode,
                            firstReason,
                            ctx.OperationId,
                            validationFailed,
                            validationReasons);
                    }

                    var backupId = string.Empty;
                    if (liveInspect.UntrackedTargets.Count > 0)
                    {
                        var captureInputs = new List<Models.DiscardBackupCaptureInput>(liveInspect.UntrackedTargets.Count);
                        var captureFailed = new List<string>();
                        var captureReasons = new Dictionary<string, string>(PathComparer);

                        foreach (var target in liveInspect.UntrackedTargets)
                        {
                            TryResolveSafeRepoTarget(repoPath, target.Path, repoGitDir, repoCommonDir, out var absPath, out _, out _);
                            var input = await BuildCaptureInputAsync(
                                repoPath,
                                absPath,
                                target,
                                Models.DiscardBaselineKind.MoveUntrackedToRecycleBin,
                                normalizedMode,
                                trackedSet,
                                cancellationToken).ConfigureAwait(false);
                            if (input == null)
                            {
                                captureFailed.Add(target.Path);
                                captureReasons[target.Path] = "写入本地恢复库前读取源内容失败，已保留原文件。";
                                continue;
                            }

                            captureInputs.Add(input);
                        }

                        if (captureFailed.Count > 0)
                        {
                            var reasonSummary = string.Join("; ", captureReasons.Select(kv => $"{kv.Key}: {kv.Value}"));
                            return FailBeforeMutation(
                                Models.DiscardBaselineKind.MoveUntrackedToRecycleBin,
                                normalizedMode,
                                $"读取待清理文件失败，已中止操作（原文件未作任何删除）：{reasonSummary}",
                                ctx.OperationId,
                                captureFailed,
                                captureReasons);
                        }

                        var headSha = await ReadHeadShaAsync(repoPath, cancellationToken).ConfigureAwait(false);
                        var capture = effectiveStore.Capture(
                            repoPath,
                            ctx.CanonicalCommonDir,
                            headSha,
                            Models.DiscardBaselineKind.MoveUntrackedToRecycleBin,
                            title,
                            captureInputs,
                            utcNowOverride,
                            normalizedMode);

                        List<string> corruptPaths = null;
                        if (!capture.Success || !effectiveStore.VerifyPayloadIntegrity(capture.Entry, out corruptPaths))
                        {
                            var err = capture.Success
                                ? $"恢复库载荷校验失败：{string.Join("、", corruptPaths ?? [])}"
                                : capture.Error;
                            var failPaths = liveInspect.UntrackedTargets.Select(t => t.Path).ToList();
                            var capReasons = new Dictionary<string, string>(PathComparer);
                            foreach (var fp in failPaths)
                                capReasons[fp] = err;
                            return FailBeforeMutation(
                                Models.DiscardBaselineKind.MoveUntrackedToRecycleBin,
                                normalizedMode,
                                $"写入 30 天本地恢复库失败，已中止删除（原文件未作任何删除）：{err}",
                                ctx.OperationId,
                                failPaths,
                                capReasons);
                        }

                        backupId = capture.BackupId;
                        ctx.Log.AppendLine($"[DiscardBackup] 已写入 30 天本地恢复库 ID={backupId}");
                    }

                    var recycled = new List<string>();
                    var directDeleted = new List<string>();
                    var skipped = new List<string>();
                    var failed = new List<string>();
                    var failureReasons = new Dictionary<string, string>(PathComparer);

                    foreach (var target in liveInspect.UntrackedTargets)
                    {
                        if (trackedSet.ContainsKey(target.Path))
                        {
                            failed.Add(target.Path);
                            failureReasons[target.Path] = "已受版本库索引跟踪，拒绝删除。";
                            continue;
                        }

                        if (!TryResolveSafeRepoTarget(repoPath, target.Path, repoGitDir, repoCommonDir, out var absPath, out var pathReason, out _))
                        {
                            failed.Add(target.Path);
                            failureReasons[target.Path] = pathReason;
                            continue;
                        }

                        if (!EntryExistsOnDisk(absPath))
                        {
                            skipped.Add(target.Path);
                            continue;
                        }

                        var (ok, err) = RemoveFileOrSymlink(absPath, normalizedMode, effectiveRecycle, effectiveDirectDelete);
                        if (!ok || EntryExistsOnDisk(absPath))
                        {
                            var reason = string.IsNullOrWhiteSpace(err)
                                ? (normalizedMode == Models.FileRemovalMode.Direct ? "直接删除失败（原文件仍保留在磁盘）。" : "系统回收站拒绝或未能移走目标（原文件仍保留在磁盘）。")
                                : err;
                            ctx.Log.AppendLine($"[RemoveFailed:{normalizedMode}] {target.Path}: {reason}");
                            failed.Add(target.Path);
                            failureReasons[target.Path] = reason;
                            continue;
                        }

                        ctx.Log.AppendLine(normalizedMode == Models.FileRemovalMode.Direct
                            ? $"[DirectDeleted] {target.Path}"
                            : $"[Recycled] {target.Path}");
                        if (normalizedMode == Models.FileRemovalMode.Direct)
                            directDeleted.Add(target.Path);
                        else
                            recycled.Add(target.Path);
                    }

                    var removedEmptyDirs = PruneCleanableEmptyDirectories(
                        repoPath,
                        repoGitDir,
                        repoCommonDir,
                        liveInspect.CleanableDirectories,
                        ctx.Log);

                    if (liveInspect.UntrackedTargets.Count == 0 && removedEmptyDirs.Count > 0)
                    {
                        if (normalizedMode == Models.FileRemovalMode.Direct)
                            directDeleted.AddRange(removedEmptyDirs);
                        else
                            recycled.AddRange(removedEmptyDirs);
                    }

                    if (!string.IsNullOrEmpty(backupId))
                    {
                        var postIndex = await ReadIndexEntriesAsync(repoPath, cancellationToken).ConfigureAwait(false);
                        var postStates = new Dictionary<string, (string WorktreeSha256, string IndexBlobOid)>(PathComparer);
                        foreach (var target in liveInspect.UntrackedTargets)
                        {
                            var wtSha = TryResolveSafeRepoTarget(repoPath, target.Path, repoGitDir, repoCommonDir, out var abs, out _, out _)
                                ? ComputeLiveWorktreeStateSha(abs)
                                : Models.DiscardBackupStore.AbsentMarker;
                            var idxOid = postIndex.TryGetValue(target.Path, out var idxEntry)
                                ? idxEntry.BlobOid
                                : Models.DiscardBackupStore.AbsentMarker;
                            postStates[target.Path] = (wtSha, idxOid);
                        }
                        effectiveStore.UpdatePostDiscardState(backupId, postStates);
                    }

                    var deletedCount = recycled.Count + directDeleted.Count;
                    var allOk = failed.Count == 0;
                    var actionLabel = normalizedMode == Models.FileRemovalMode.Direct ? "直接删除" : "移至系统回收站";
                    var formattedReasons = string.Join("; ", failureReasons.Select(kv => $"{kv.Key}: {kv.Value}"));
                    var summary = allOk
                        ? (string.IsNullOrEmpty(backupId)
                            ? $"已完成{actionLabel} {deletedCount} 个项目。"
                            : $"已{actionLabel} {deletedCount} 个未跟踪项目，原内容已存入 30 天本地恢复库（ID：{backupId}）。")
                        : $"{actionLabel}未全部完成（成功 {deletedCount}，失败 {failed.Count}，失败文件保留原位）：{formattedReasons}";

                    var processed = recycled.Concat(directDeleted).Distinct(PathComparer).ToList();
                    return new OperationActionOutcome<Models.DiscardExecutionResult>
                    {
                        Stage = allOk
                            ? Models.GitOperationStage.Succeeded
                            : deletedCount > 0
                                ? Models.GitOperationStage.PartialSuccess
                                : Models.GitOperationStage.Failed,
                        Summary = summary,
                        DiagnosticDetail = string.Join(Environment.NewLine, failureReasons.Select(kv => $"{kv.Key}: {kv.Value}")),
                        Payload = new Models.DiscardExecutionResult
                        {
                            Success = allOk,
                            Baseline = Models.DiscardBaselineKind.MoveUntrackedToRecycleBin,
                            RemovalMode = normalizedMode,
                            BackupId = backupId,
                            ProcessedPaths = processed,
                            RecycledPaths = recycled,
                            DirectDeletedPaths = directDeleted,
                            SkippedPaths = skipped,
                            FailedPaths = failed,
                            FailureReasons = failureReasons,
                            Summary = summary,
                            OperationRecordId = ctx.OperationId,
                        },
                    };
                },
                expectedSnapshot: expectedSnapshot,
                verifyOptions: DiscardVerifyOptions(),
                journalStore: journalStore,
                outerLog: outerLog,
                cancellationToken: cancellationToken).ConfigureAwait(false);

            return exec.Payload ?? new Models.DiscardExecutionResult
            {
                Success = exec.Succeeded,
                BlockedBySnapshot = exec.BlockedBySnapshot,
                Baseline = Models.DiscardBaselineKind.MoveUntrackedToRecycleBin,
                RemovalMode = normalizedMode,
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
            var (repoGitDir, commonDir) = await Models.RepositoryWriteGuard.ResolveGitDirectoriesAsync(repoPath, cancellationToken).ConfigureAwait(false);
            if (!Models.WorktreeTaskStore.PathsEqual(entry.GitCommonDir, commonDir) ||
                !Models.WorktreeTaskStore.PathsEqual(entry.WorktreePath, repoPath))
            {
                return new Models.DiscardBackupRestoreCheck
                {
                    Found = false,
                    BlockingReason = "恢复记录属于其他仓库或工作树，已停止恢复。",
                };
            }

            var integrityOk = store.VerifyPayloadIntegrity(entry, out var missingPayloads);
            var liveIndex = await ReadIndexEntriesAsync(repoPath, cancellationToken).ConfigureAwait(false);

            var conflictingPaths = new List<string>();
            var conflictDetails = new List<string>();

            foreach (var file in entry.Files)
            {
                var currentWorktreeSha = TryResolveSafeRepoTarget(repoPath, file.RelativePath, repoGitDir, commonDir, out var abs, out _, out _)
                    ? ComputeLiveWorktreeStateSha(abs)
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
            var (repoGitDir, repoCommonDir) = await Models.RepositoryWriteGuard.ResolveGitDirectoriesAsync(repoPath, cancellationToken).ConfigureAwait(false);

            var exec = await OperationJournalService.ExecuteGuardedWriteAsync(
                repoPath: repoPath,
                kind: Models.GitOperationKind.RestoreBackup,
                title: $"从 30 天恢复库还原（{entry.Files.Count} 个文件，ID={backupId}）",
                action: async ctx =>
                {
                    var restoredPaths = new List<string>();
                    var indexRestoredCount = 0;
                    var hasMutated = false;
                    OperationActionOutcome<Models.DiscardBackupRestoreResult> FailedRestore(string reason) => new()
                    {
                        Stage = hasMutated ? Models.GitOperationStage.PartialSuccess : Models.GitOperationStage.Failed,
                        Summary = reason,
                        Payload = new Models.DiscardBackupRestoreResult
                        {
                            Success = false,
                            BackupId = backupId,
                            RestoredPaths = restoredPaths,
                            IndexRestoredCount = indexRestoredCount,
                            Summary = reason,
                        },
                    };

                    var liveCheck = await InspectBackupRestoreAsync(repoPath, backupId, store, utcNowOverride, cancellationToken).ConfigureAwait(false);
                    if (!liveCheck.Found || liveCheck.IsExpired || liveCheck.HasPayloadIntegrityError ||
                        (liveCheck.HasSubsequentModifications && !allowOverwriteSubsequentEdits))
                        return FailedRestore(liveCheck.BlockingReason);

                    foreach (var file in entry.Files)
                    {
                        if (!TryResolveSafeRepoTarget(repoPath, file.RelativePath, repoGitDir, repoCommonDir, out var absPath, out _, out _))
                            return FailedRestore($"拒绝恢复越界或异常路径：{file.RelativePath}");

                        hasMutated = true;
                        try
                        {
                            if (file.ExistsInWorktree && !file.IsSymlink)
                            {
                                var bytes = store.ReadWorktreePayloadBytes(file, out var readErr);
                                if (!string.IsNullOrEmpty(readErr))
                                    return FailedRestore(readErr);

                                if (TryGetDiskEntryInfo(absPath, out var conflictInfo) &&
                                    conflictInfo.Exists &&
                                    (conflictInfo.IsSymlink || conflictInfo.IsDirectory))
                                {
                                    var (ok, err) = effectiveRecycle(absPath);
                                    if (!ok || EntryExistsOnDisk(absPath))
                                        return FailedRestore($"覆盖冲突条目时移至回收站失败：{err}");
                                }

                                var parent = Path.GetDirectoryName(absPath);
                                if (!string.IsNullOrEmpty(parent) && !Directory.Exists(parent))
                                    Directory.CreateDirectory(parent);

                                await File.WriteAllBytesAsync(absPath, bytes, cancellationToken).ConfigureAwait(false);
                                var actualSha = Models.DiscardBackupStore.ComputeFileSha256Hex(absPath);
                                if (!string.Equals(actualSha, file.WorktreeSha256, StringComparison.OrdinalIgnoreCase))
                                    return FailedRestore($"还原文件后内容校验失败：{file.RelativePath}");
                            }
                            else if (file.ExistsInWorktree && file.IsSymlink)
                            {
                                var parent = Path.GetDirectoryName(absPath);
                                if (!string.IsNullOrEmpty(parent) && !Directory.Exists(parent))
                                    Directory.CreateDirectory(parent);

                                if (EntryExistsOnDisk(absPath))
                                {
                                    var (ok, err) = effectiveRecycle(absPath);
                                    if (!ok || EntryExistsOnDisk(absPath))
                                        return FailedRestore($"恢复符号链接前移至回收站失败：{err}");
                                }

                                if (file.IsDirectorySymlink)
                                    Directory.CreateSymbolicLink(absPath, file.SymlinkTarget);
                                else
                                    File.CreateSymbolicLink(absPath, file.SymlinkTarget);
                            }
                            else if (!file.ExistsInWorktree && EntryExistsOnDisk(absPath))
                            {
                                var (ok, err) = effectiveRecycle(absPath);
                                if (!ok || EntryExistsOnDisk(absPath))
                                    return FailedRestore($"还原丢弃前“工作区已删除”状态时移至回收站失败：{err}");
                            }
                        }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
                        {
                            return FailedRestore($"还原工作区文件失败（{file.RelativePath}）：{Models.GitConfigSnapshot.RedactText(ex.Message)}");
                        }

                        // For HEAD-baseline discards or index-altered states, restore exact index entry as well.
                        if (file.Baseline == Models.DiscardBaselineKind.RestoreToHead)
                        {
                            if (file.ExistsInIndex)
                            {
                                var idxBytes = store.ReadIndexPayloadBytes(file, out var idxErr);
                                if (idxBytes == null || !string.IsNullOrEmpty(idxErr))
                                    return FailedRestore(string.IsNullOrEmpty(idxErr) ? $"缺少索引恢复载荷：{file.RelativePath}" : idxErr);

                                var hashRes = await Command.RunGitDirectWithInputAsync(
                                    repoPath,
                                    TimeSpan.FromSeconds(15),
                                    idxBytes,
                                    cancellationToken,
                                    "hash-object",
                                    "-w",
                                    "--stdin").ConfigureAwait(false);
                                if (!hashRes.IsSuccess || string.IsNullOrWhiteSpace(hashRes.StdOut))
                                    return FailedRestore(CombineError(hashRes, $"写入索引恢复对象失败：{file.RelativePath}"));
                                var targetBlobOid = hashRes.StdOut.Trim();
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
                                if (!upd.IsSuccess)
                                    return FailedRestore(CombineError(upd, $"恢复索引条目失败：{file.RelativePath}"));
                                indexRestoredCount++;
                            }
                            else
                            {
                                var remove = await Command.RunGitDirectAsync(
                                    repoPath,
                                    TimeSpan.FromSeconds(10),
                                    cancellationToken,
                                    "--literal-pathspecs",
                                    "update-index",
                                    "--force-remove",
                                    "--",
                                    file.RelativePath).ConfigureAwait(false);
                                if (!remove.IsSuccess)
                                    return FailedRestore(CombineError(remove, $"恢复索引删除状态失败：{file.RelativePath}"));
                                indexRestoredCount++;
                            }
                        }

                        restoredPaths.Add(file.RelativePath);
                    }

                    // Post-restore state verification before marking backup entry restored.
                    var verifiedIndex = await ReadIndexEntriesAsync(repoPath, cancellationToken).ConfigureAwait(false);
                    foreach (var file in entry.Files)
                    {
                        if (!TryResolveSafeRepoTarget(repoPath, file.RelativePath, repoGitDir, repoCommonDir, out var absPath, out _, out _))
                            return FailedRestore($"还原后路径校验失败：{file.RelativePath}");

                        if (file.ExistsInWorktree)
                        {
                            if (!TryGetDiskEntryInfo(absPath, out var diskInfo) || !diskInfo.Exists)
                                return FailedRestore($"还原后文件缺失：{file.RelativePath}");

                            if (file.IsSymlink)
                            {
                                if (!diskInfo.IsSymlink || !string.Equals(diskInfo.LinkTarget, file.SymlinkTarget, StringComparison.Ordinal))
                                    return FailedRestore($"还原后符号链接目标不一致：{file.RelativePath}");
                            }
                            else if (!string.Equals(ComputeLiveWorktreeStateSha(absPath), file.WorktreeSha256, StringComparison.OrdinalIgnoreCase))
                            {
                                return FailedRestore($"还原后文件内容哈希校验失败：{file.RelativePath}");
                            }
                        }
                        else if (EntryExistsOnDisk(absPath))
                        {
                            return FailedRestore($"还原后工作区仍存在冗余条目：{file.RelativePath}");
                        }

                        if (file.Baseline == Models.DiscardBaselineKind.RestoreToHead)
                        {
                            if (file.ExistsInIndex)
                            {
                                if (!verifiedIndex.TryGetValue(file.RelativePath, out var rec) ||
                                    (!string.IsNullOrEmpty(file.IndexBlobOid) && !string.Equals(rec.BlobOid, file.IndexBlobOid, StringComparison.OrdinalIgnoreCase)))
                                    return FailedRestore($"还原后暂存区条目校验失败：{file.RelativePath}");
                            }
                            else if (verifiedIndex.ContainsKey(file.RelativePath))
                            {
                                return FailedRestore($"还原后暂存区仍存在冗余条目：{file.RelativePath}");
                            }
                        }
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
            Models.FileRemovalMode removalMode,
            Dictionary<string, IndexEntryRecord> indexInfo,
            CancellationToken cancellationToken)
        {
            try
            {
                TryGetDiskEntryInfo(absPath, out var diskInfo);
                if (diskInfo.Exists && !diskInfo.IsSymlink && diskInfo.IsDirectory)
                    return null;

                byte[] wtBytes = null;
                if (diskInfo.Exists && !diskInfo.IsSymlink)
                {
                    await using var stream = new FileStream(
                        absPath,
                        FileMode.Open,
                        FileAccess.Read,
                        FileShare.ReadWrite | FileShare.Delete,
                        bufferSize: 4096,
                        useAsync: true);
                    using var ms = new MemoryStream();
                    await stream.CopyToAsync(ms, cancellationToken).ConfigureAwait(false);
                    wtBytes = ms.ToArray();
                }

                var hasIndex = indexInfo.TryGetValue(target.Path, out var idx);
                byte[] idxBytes = null;
                if (hasIndex && !string.IsNullOrEmpty(idx.BlobOid))
                    idxBytes = await ReadGitBlobBytesAsync(repoPath, idx.BlobOid, cancellationToken).ConfigureAwait(false);

                return new Models.DiscardBackupCaptureInput
                {
                    RelativePath = target.Path,
                    OriginalPath = target.OriginalPath,
                    Baseline = baseline,
                    RemovalMode = Models.FileRemovalModeExtensions.Normalize(removalMode),
                    ExistsInWorktree = diskInfo.Exists,
                    IsSymlink = diskInfo.IsSymlink,
                    IsDirectorySymlink = diskInfo.IsDirectorySymlink,
                    SymlinkTarget = diskInfo.LinkTarget,
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
            Models.FileRemovalMode removalMode,
            string reason,
            string opId,
            List<string> failedPaths = null,
            Dictionary<string, string> failureReasons = null)
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
                    RemovalMode = removalMode,
                    FailedPaths = failedPaths ?? [],
                    FailureReasons = failureReasons ?? new Dictionary<string, string>(PathComparer),
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

        private static (bool Success, string Error) DefaultDirectDeleteHandler(string path)
        {
            try
            {
                if (!TryGetDiskEntryInfo(path, out var info) || !info.Exists)
                    return (true, string.Empty);

                if (info.IsSymlink)
                {
                    if (info.IsDirectorySymlink)
                        Directory.Delete(path, false);
                    else
                        File.Delete(path);
                    return (true, string.Empty);
                }

                if (info.IsDirectory)
                    return (false, "拒绝直接删除未展开的目录路径。");

                var attrs = File.GetAttributes(path);
                if ((attrs & FileAttributes.ReadOnly) != 0)
                    File.SetAttributes(path, attrs & ~FileAttributes.ReadOnly);

                File.Delete(path);
                return (true, string.Empty);
            }
            catch (Exception ex)
            {
                return (false, Models.GitConfigSnapshot.RedactText(ex.Message));
            }
        }

        private static (bool Success, string Error) RemoveFileOrSymlink(
            string absPath,
            Models.FileRemovalMode removalMode,
            Func<string, (bool Success, string Error)> recycleHandler,
            Func<string, (bool Success, string Error)> directDeleteHandler)
        {
            if (!TryGetDiskEntryInfo(absPath, out var info) || !info.Exists)
                return (true, string.Empty);

            if (Models.FileRemovalModeExtensions.Normalize(removalMode) == Models.FileRemovalMode.Direct)
                return (directDeleteHandler ?? DefaultDirectDeleteHandler)(absPath);

            return (recycleHandler ?? DefaultRecycleHandler)(absPath);
        }

        private static List<string> PruneCleanableEmptyDirectories(
            string repoPath,
            string repoGitDir,
            string repoCommonDir,
            IReadOnlyList<string> cleanableDirectories,
            Models.ICommandLog log)
        {
            var removed = new List<string>();
            if (cleanableDirectories == null || cleanableDirectories.Count == 0)
                return removed;

            var ordered = cleanableDirectories
                .Where(d => !string.IsNullOrWhiteSpace(d))
                .Distinct(PathComparer)
                .OrderByDescending(d => d.Count(ch => ch == '/'))
                .ThenByDescending(d => d.Length)
                .ToList();

            foreach (var dirRel in ordered)
            {
                if (!TryResolveSafeRepoTarget(repoPath, dirRel, repoGitDir, repoCommonDir, out var dirAbs, out _, out _))
                    continue;

                if (!TryGetDiskEntryInfo(dirAbs, out var info) || !info.Exists || info.IsSymlink || !info.IsDirectory)
                    continue;

                if (HasDotGitEntry(dirAbs) || HasAnyDirectoryEntries(dirAbs))
                    continue;

                try
                {
                    Directory.Delete(dirAbs, false);
                    removed.Add(dirRel);
                    log?.AppendLine($"[RemovedEmptyDir] {dirRel}");
                }
                catch
                {
                    // Leave non-deletable directories intact without failing already-removed files.
                }
            }

            return removed;
        }

        private static async Task ExpandUntrackedCandidatesAsync(
            string repoPath,
            string repoGitDir,
            string repoCommonDir,
            Dictionary<string, IndexEntryRecord> indexInfo,
            Dictionary<string, Models.Change> targetByPath,
            List<(string RelPath, DiskEntryInfo Info, Models.Change Source)> directUntrackedLeaves,
            Queue<string> dirQueue,
            List<Models.DiscardTargetItem> untracked,
            List<string> cleanableDirs,
            List<string> skippedSubmodules,
            List<string> skippedNestedRepos,
            List<string> skippedIgnored,
            CancellationToken cancellationToken)
        {
            var seenLeaves = new HashSet<string>(PathComparer);
            var visitedDirs = new HashSet<string>(PathComparer);
            var initialDirs = new List<string>();
            while (dirQueue.Count > 0)
            {
                var d = dirQueue.Dequeue();
                if (visitedDirs.Add(d))
                    initialDirs.Add(d);
            }

            var initialCheckPaths = directUntrackedLeaves
                .Select(x => x.RelPath)
                .Concat(initialDirs)
                .Distinct(PathComparer)
                .ToList();
            var initialIgnored = await CheckIgnoredPathsBatchAsync(repoPath, initialCheckPaths, cancellationToken)
                .ConfigureAwait(false);

            foreach (var leaf in directUntrackedLeaves)
            {
                if (initialIgnored.Contains(leaf.RelPath))
                {
                    AddUniquePath(skippedIgnored, leaf.RelPath);
                    continue;
                }

                if (seenLeaves.Add(leaf.RelPath))
                {
                    untracked.Add(CreateUntrackedTargetItem(leaf.RelPath, leaf.Info, leaf.Source));
                }
            }

            foreach (var dirRel in initialDirs)
            {
                if (initialIgnored.Contains(dirRel))
                {
                    AddUniquePath(skippedIgnored, dirRel);
                    continue;
                }

                dirQueue.Enqueue(dirRel);
                AddUniquePath(cleanableDirs, dirRel);
            }

            while (dirQueue.Count > 0)
            {
                var levelCount = dirQueue.Count;
                var levelCandidates = new List<(string RelPath, DiskEntryInfo Info)>();

                for (var i = 0; i < levelCount; i++)
                {
                    var dirRel = dirQueue.Dequeue();
                    if (!TryResolveSafeRepoTarget(repoPath, dirRel, repoGitDir, repoCommonDir, out var dirAbs, out _, out _))
                        continue;

                    if (!TryGetDiskEntryInfo(dirAbs, out var dirInfo) || !dirInfo.Exists || dirInfo.IsSymlink || !dirInfo.IsDirectory)
                        continue;

                    if (HasDotGitEntry(dirAbs))
                    {
                        AddUniquePath(skippedNestedRepos, dirRel);
                        continue;
                    }

                    string[] children;
                    try
                    {
                        children = Directory.EnumerateFileSystemEntries(dirAbs)
                            .OrderBy(p => Path.GetFileName(p), StringComparer.OrdinalIgnoreCase)
                            .ToArray();
                    }
                    catch
                    {
                        continue;
                    }

                    foreach (var childAbs in children)
                    {
                        var name = Path.GetFileName(childAbs);
                        if (string.IsNullOrEmpty(name) ||
                            string.Equals(name, ".", StringComparison.Ordinal) ||
                            string.Equals(name, "..", StringComparison.Ordinal) ||
                            string.Equals(name, ".git", StringComparison.OrdinalIgnoreCase))
                            continue;

                        var childRel = NormalizeRelPath($"{dirRel}/{name}");
                        if (!TryResolveSafeRepoTarget(repoPath, childRel, repoGitDir, repoCommonDir, out _, out _, out _))
                            continue;

                        if (indexInfo.TryGetValue(childRel, out var idxRec))
                        {
                            if (string.Equals(idxRec.Mode, "160000", StringComparison.Ordinal))
                                AddUniquePath(skippedSubmodules, childRel);
                            continue;
                        }

                        if (!TryGetDiskEntryInfo(childAbs, out var childInfo) || !childInfo.Exists)
                            continue;

                        if (!childInfo.IsSymlink && childInfo.IsDirectory && HasDotGitEntry(childAbs))
                        {
                            AddUniquePath(skippedNestedRepos, childRel);
                            continue;
                        }

                        levelCandidates.Add((childRel, childInfo));
                    }
                }

                if (levelCandidates.Count == 0)
                    continue;

                var levelIgnored = await CheckIgnoredPathsBatchAsync(
                    repoPath,
                    levelCandidates.Select(c => c.RelPath).Distinct(PathComparer).ToList(),
                    cancellationToken).ConfigureAwait(false);

                foreach (var candidate in levelCandidates)
                {
                    if (levelIgnored.Contains(candidate.RelPath))
                    {
                        AddUniquePath(skippedIgnored, candidate.RelPath);
                        continue;
                    }

                    if (!candidate.Info.IsSymlink && candidate.Info.IsDirectory)
                    {
                        if (visitedDirs.Add(candidate.RelPath))
                        {
                            dirQueue.Enqueue(candidate.RelPath);
                            AddUniquePath(cleanableDirs, candidate.RelPath);
                        }
                    }
                    else if (seenLeaves.Add(candidate.RelPath))
                    {
                        targetByPath.TryGetValue(candidate.RelPath, out var source);
                        untracked.Add(CreateUntrackedTargetItem(candidate.RelPath, candidate.Info, source));
                    }
                }
            }
        }

        private static Models.DiscardTargetItem CreateUntrackedTargetItem(
            string relPath,
            DiskEntryInfo info,
            Models.Change source)
        {
            return new Models.DiscardTargetItem
            {
                Path = relPath,
                OriginalPath = string.IsNullOrWhiteSpace(source?.OriginalPath) ? string.Empty : NormalizeRelPath(source.OriginalPath),
                Index = source?.Index ?? Models.ChangeState.Untracked,
                WorkTree = source?.WorkTree ?? Models.ChangeState.Untracked,
                IsSubmodule = false,
                IsFolder = false,
                IsSymlink = info.IsSymlink,
                IsDirectorySymlink = info.IsDirectorySymlink,
            };
        }

        private static async Task<HashSet<string>> CheckIgnoredPathsBatchAsync(
            string repoPath,
            IReadOnlyList<string> relPaths,
            CancellationToken cancellationToken)
        {
            var ignored = new HashSet<string>(PathComparer);
            if (relPaths == null || relPaths.Count == 0)
                return ignored;

            var stdin = CommandArgumentHelper.BuildNulPathspecBytes(relPaths);
            if (stdin.Length == 0)
                return ignored;

            var res = await Command.RunGitDirectWithInputAsync(
                repoPath,
                TimeSpan.FromSeconds(15),
                stdin,
                cancellationToken,
                "--no-pager",
                "-c",
                "core.quotepath=off",
                "check-ignore",
                "--stdin",
                "-z").ConfigureAwait(false);

            if (string.IsNullOrEmpty(res.StdOut))
                return ignored;

            foreach (var raw in res.StdOut.Split('\0', StringSplitOptions.RemoveEmptyEntries))
            {
                var normalized = NormalizeRelPath(raw);
                if (!string.IsNullOrEmpty(normalized))
                    ignored.Add(normalized);
            }

            return ignored;
        }

        private static bool PreflightTargetsMatch(Models.DiscardPreflight expected, Models.DiscardPreflight current)
        {
            if (expected == null || current == null || expected.Baseline != current.Baseline)
                return false;

            if (expected.Baseline == Models.DiscardBaselineKind.MoveUntrackedToRecycleBin)
            {
                return PathSetsEqual(
                           expected.UntrackedTargets.Select(t => t.Path),
                           current.UntrackedTargets.Select(t => t.Path)) &&
                       PathSetsEqual(
                           expected.CleanableDirectories,
                           current.CleanableDirectories);
            }

            return PathSetsEqual(
                expected.TrackedTargets.Select(t => t.Path),
                current.TrackedTargets.Select(t => t.Path));
        }

        private static bool PathSetsEqual(IEnumerable<string> left, IEnumerable<string> right)
        {
            var leftSet = new HashSet<string>(left ?? [], PathComparer);
            return leftSet.SetEquals(right ?? []);
        }

        private static bool TryResolveSafeRepoTarget(
            string repoPath,
            string relPath,
            string repoGitDir,
            string repoCommonDir,
            out string absPath,
            out string failureReason,
            out string symlinkAncestorRel)
        {
            absPath = null;
            failureReason = string.Empty;
            symlinkAncestorRel = string.Empty;

            if (string.IsNullOrWhiteSpace(repoPath) || string.IsNullOrWhiteSpace(relPath) || relPath.IndexOf('\0') >= 0)
            {
                failureReason = "目标路径为空或包含非法字符。";
                return false;
            }

            var trimmed = relPath.Trim().Replace('\\', '/');
            if (trimmed.StartsWith("/", StringComparison.Ordinal) || Path.IsPathRooted(trimmed))
            {
                failureReason = "拒绝处理绝对路径或根目录路径。";
                return false;
            }

            var rawSegments = trimmed.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (rawSegments.Length == 0)
            {
                failureReason = "拒绝处理仓库根目录本身。";
                return false;
            }

            for (var i = 0; i < rawSegments.Length; i++)
            {
                var seg = rawSegments[i];
                if (string.Equals(seg, ".", StringComparison.Ordinal) ||
                    string.Equals(seg, "..", StringComparison.Ordinal))
                {
                    failureReason = "拒绝处理包含 . 或 .. 的越界路径。";
                    return false;
                }

                if (string.Equals(seg, ".git", StringComparison.OrdinalIgnoreCase))
                {
                    failureReason = "拒绝修改或删除 .git 元数据目录内容。";
                    return false;
                }

                if (OperatingSystem.IsWindows() && seg.Contains(':', StringComparison.Ordinal))
                {
                    failureReason = "拒绝处理包含盘符或保留流名的非法路径。";
                    return false;
                }
            }

            try
            {
                var rootFull = Path.TrimEndingDirectorySeparator(Path.GetFullPath(repoPath));
                var prefix = rootFull + Path.DirectorySeparatorChar;
                var candidateFull = Path.GetFullPath(Path.Combine(rootFull, Path.Combine(rawSegments)));
                var comp = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
                if (!candidateFull.StartsWith(prefix, comp))
                {
                    failureReason = "拒绝处理仓库根目录之外的越界路径。";
                    return false;
                }

                if (IsSameOrUnderDirectory(candidateFull, repoGitDir, comp) ||
                    IsSameOrUnderDirectory(candidateFull, repoCommonDir, comp))
                {
                    failureReason = "拒绝处理 Git 内部元数据目录路径。";
                    return false;
                }

                // Walk parent segments to forbid traversing through symlink/junction ancestor directories.
                var cursor = rootFull;
                for (var i = 0; i < rawSegments.Length - 1; i++)
                {
                    cursor = Path.Combine(cursor, rawSegments[i]);
                    if (TryGetDiskEntryInfo(cursor, out var segInfo) &&
                        segInfo.Exists &&
                        (segInfo.IsSymlink || segInfo.IsReparsePoint))
                    {
                        symlinkAncestorRel = string.Join('/', rawSegments, 0, i + 1);
                        failureReason = $"拒绝穿过符号链接或目录连接点路径：{symlinkAncestorRel}";
                        return false;
                    }
                }

                absPath = candidateFull;
                return true;
            }
            catch
            {
                failureReason = "解析目标路径失败，路径非法或受系统限制。";
                return false;
            }
        }

        private static bool IsSameOrUnderDirectory(string candidateFull, string protectedDir, StringComparison comp)
        {
            if (string.IsNullOrWhiteSpace(protectedDir))
                return false;

            try
            {
                var fullProtected = Path.TrimEndingDirectorySeparator(Path.GetFullPath(protectedDir));
                return string.Equals(candidateFull, fullProtected, comp) ||
                       candidateFull.StartsWith(fullProtected + Path.DirectorySeparatorChar, comp);
            }
            catch
            {
                return false;
            }
        }

        private static bool IsCoveredByExpandRoots(string symlinkAncestorRel, HashSet<string> expandRoots)
        {
            if (string.IsNullOrEmpty(symlinkAncestorRel) || expandRoots == null || expandRoots.Count == 0)
                return false;

            if (expandRoots.Contains(symlinkAncestorRel))
                return true;

            foreach (var root in expandRoots)
            {
                if (symlinkAncestorRel.StartsWith(root + "/", OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                    return true;
            }

            return false;
        }

        private static bool TryGetDiskEntryInfo(string absPath, out DiskEntryInfo info)
        {
            info = default;
            if (string.IsNullOrWhiteSpace(absPath))
                return false;

            try
            {
                var fi = new FileInfo(absPath);
                var di = new DirectoryInfo(absPath);
                var attrs = fi.Attributes;
                var fileLinkTarget = fi.LinkTarget;
                var dirLinkTarget = di.LinkTarget;
                var hasLinkTarget = fileLinkTarget != null || dirLinkTarget != null;
                var exists = (int)attrs != -1 || hasLinkTarget;
                if (!exists)
                    return false;

                var isDir = (int)attrs != -1 && (attrs & FileAttributes.Directory) != 0;
                var isReparse = hasLinkTarget || ((int)attrs != -1 && (attrs & FileAttributes.ReparsePoint) != 0);
                var linkTarget = fileLinkTarget ?? dirLinkTarget ?? string.Empty;
                var isSymlink = hasLinkTarget || (isReparse && isDir);
                var isDirSymlink = isSymlink && isDir;

                info = new DiskEntryInfo(
                    Exists: true,
                    IsDirectory: isDir,
                    IsSymlink: isSymlink,
                    IsDirectorySymlink: isDirSymlink,
                    IsReparsePoint: isReparse,
                    LinkTarget: linkTarget);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool EntryExistsOnDisk(string absPath) =>
            TryGetDiskEntryInfo(absPath, out var info) && info.Exists;

        private static bool HasDotGitEntry(string dirAbs)
        {
            var dotGit = Path.Combine(dirAbs, ".git");
            return EntryExistsOnDisk(dotGit);
        }

        private static bool HasAnyDirectoryEntries(string dirAbs)
        {
            try
            {
                using var enumerator = Directory.EnumerateFileSystemEntries(dirAbs).GetEnumerator();
                return enumerator.MoveNext();
            }
            catch
            {
                return true;
            }
        }

        private static string ComputeLiveWorktreeStateSha(string absPath)
        {
            if (string.IsNullOrEmpty(absPath) || !TryGetDiskEntryInfo(absPath, out var info) || !info.Exists)
                return Models.DiscardBackupStore.AbsentMarker;

            if (info.IsSymlink)
                return Models.DiscardBackupStore.ComputeSha256Hex(Encoding.UTF8.GetBytes(info.LinkTarget ?? string.Empty));

            if (info.IsDirectory)
                return "<DIR>";

            try
            {
                return Models.DiscardBackupStore.ComputeFileSha256Hex(absPath);
            }
            catch
            {
                return "<UNREADABLE>";
            }
        }

        private static void AddUniquePath(List<string> list, string path)
        {
            if (!string.IsNullOrWhiteSpace(path) && !list.Contains(path, PathComparer))
                list.Add(path);
        }

        private static bool HasRenameSource(Models.DiscardTargetItem target) =>
            !string.IsNullOrEmpty(target.OriginalPath) &&
            (target.Index == Models.ChangeState.Renamed || target.WorkTree == Models.ChangeState.Renamed);

        private static Models.OperationSnapshotVerifyOptions DiscardVerifyOptions() => new()
        {
            RequireSameRepoIdentity = true,
            RequireSameHead = true,
            RequireSameInProgressState = true,
            RequireSameStagedFingerprint = true,
            RequireSameTargetFiles = true,
            RequireSameWatchedRefs = false,
            CheckGitLocks = true,
            CheckIndexLock = true,
        };

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

        private readonly record struct DiskEntryInfo(
            bool Exists,
            bool IsDirectory,
            bool IsSymlink,
            bool IsDirectorySymlink,
            bool IsReparsePoint,
            string LinkTarget);

        private sealed record IndexEntryRecord(string Mode, string BlobOid, string Stage);
    }
}
