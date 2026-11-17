using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SourceGit.Commands
{
    /// <summary>
    /// Captures and re-verifies structured repository snapshots prior to mutating Git operations
    /// so external edits, branch moves, dirty trees, or locks fail safe with explicit diagnostics.
    /// </summary>
    public static class CaptureOperationSnapshot
    {
        public static Models.GitInProgressKind DetectInProgressKind(string gitDir)
        {
            if (string.IsNullOrWhiteSpace(gitDir) || !Directory.Exists(gitDir))
                return Models.GitInProgressKind.None;

            if (File.Exists(Path.Combine(gitDir, "MERGE_HEAD")))
                return Models.GitInProgressKind.Merge;

            if (Directory.Exists(Path.Combine(gitDir, "rebase-merge")))
                return Models.GitInProgressKind.Rebase;

            var rebaseApplyDir = Path.Combine(gitDir, "rebase-apply");
            if (Directory.Exists(rebaseApplyDir))
            {
                return File.Exists(Path.Combine(rebaseApplyDir, "applying"))
                    ? Models.GitInProgressKind.Am
                    : Models.GitInProgressKind.Rebase;
            }

            if (File.Exists(Path.Combine(gitDir, "CHERRY_PICK_HEAD")))
                return Models.GitInProgressKind.CherryPick;

            if (File.Exists(Path.Combine(gitDir, "REVERT_HEAD")))
                return Models.GitInProgressKind.Revert;

            if (File.Exists(Path.Combine(gitDir, "BISECT_LOG")))
                return Models.GitInProgressKind.Bisect;

            return Models.GitInProgressKind.None;
        }

        public static async Task<Models.OperationConfirmationSnapshot> CaptureAsync(
            string repoPath,
            Models.OperationSnapshotCaptureOptions options = null,
            CancellationToken cancellationToken = default)
        {
            var stagedSnap = await QueryStagedSnapshot.CaptureAsync(repoPath, cancellationToken).ConfigureAwait(false);
            var (gitDir, commonDir) = (stagedSnap.GitDir, stagedSnap.GitCommonDir);
            if (string.IsNullOrEmpty(gitDir) || string.IsNullOrEmpty(commonDir))
                (gitDir, commonDir) = await Models.RepositoryWriteGuard.ResolveGitDirectoriesAsync(repoPath, cancellationToken).ConfigureAwait(false);

            var headRefName = string.Empty;
            if (!stagedSnap.IsDetachedHead)
            {
                var symRes = await Command.RunGitDirectAsync(repoPath, cancellationToken, "symbolic-ref", "--quiet", "HEAD").ConfigureAwait(false);
                if (symRes.IsSuccess)
                    headRefName = symRes.StdOut.Trim();
                else if (!string.IsNullOrEmpty(stagedSnap.CurrentBranchName))
                    headRefName = "refs/heads/" + stagedSnap.CurrentBranchName;
            }

            var watchedRefs = new Dictionary<string, string>(StringComparer.Ordinal);
            if (options?.RefsToWatch != null)
            {
                foreach (var rawRef in options.RefsToWatch)
                {
                    if (string.IsNullOrWhiteSpace(rawRef))
                        continue;

                    var fullRef = rawRef.Trim();
                    watchedRefs[fullRef] = await ResolveRefOidAsync(repoPath, fullRef, cancellationToken).ConfigureAwait(false);
                }
            }

            var targetFiles = new List<Models.OperationTargetFileSnapshot>();
            if (options?.TargetPaths != null)
            {
                var normalizedPaths = options.TargetPaths
                    .Where(p => !string.IsNullOrWhiteSpace(p))
                    .Select(p => p.Replace('\\', '/').TrimStart('/'))
                    .Distinct(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal)
                    .ToList();

                if (normalizedPaths.Count > 0)
                {
                    var indexMap = await ReadIndexBlobOidsAsync(repoPath, normalizedPaths, cancellationToken).ConfigureAwait(false);
                    var headMap = stagedSnap.IsUnborn
                        ? new Dictionary<string, string>(StringComparer.Ordinal)
                        : await ReadHeadTreeBlobOidsAsync(repoPath, normalizedPaths, cancellationToken).ConfigureAwait(false);

                    foreach (var rel in normalizedPaths)
                    {
                        var diskInfo = await CaptureDiskFileAsync(repoPath, rel, options.IncludeTargetFileDetails, cancellationToken)
                            .ConfigureAwait(false);
                        indexMap.TryGetValue(rel, out var indexOid);
                        headMap.TryGetValue(rel, out var headOid);

                        targetFiles.Add(new Models.OperationTargetFileSnapshot
                        {
                            RelativePath = rel,
                            ExistsOnDisk = diskInfo.Exists,
                            DiskLength = diskInfo.Length,
                            DiskSha256 = diskInfo.Sha256,
                            ExistsInIndex = !string.IsNullOrEmpty(indexOid),
                            IndexSha1 = indexOid ?? string.Empty,
                            ExistsInHead = !string.IsNullOrEmpty(headOid),
                            HeadSha1 = headOid ?? string.Empty,
                        });
                    }
                }
            }

            var hasUnstaged = stagedSnap.AllChanges.Any(c => c.WorkTreeState != Models.ChangeState.None && c.WorkTreeState != Models.ChangeState.Untracked);
            var hasUntracked = stagedSnap.AllChanges.Any(c => c.WorkTreeState == Models.ChangeState.Untracked);

            return new Models.OperationConfirmationSnapshot
            {
                CapturedAtUtc = DateTime.UtcNow,
                RepoPath = stagedSnap.RepoPath,
                CanonicalGitDir = gitDir,
                CanonicalCommonDir = commonDir,
                HeadSha = stagedSnap.HeadOid,
                HeadRefName = headRefName,
                IsDetachedHead = stagedSnap.IsDetachedHead,
                InProgressKind = DetectInProgressKind(gitDir),
                StagedFingerprint = stagedSnap.IndexFingerprint,
                HasStagedChanges = stagedSnap.HasStagedChanges,
                HasUnstagedChanges = hasUnstaged,
                HasUntrackedFiles = hasUntracked,
                HasConflicts = stagedSnap.HasConflicts,
                WatchedRefs = watchedRefs,
                TargetFiles = targetFiles,
            };
        }

        public static async Task<Models.OperationSnapshotVerificationResult> VerifyAsync(
            Models.OperationConfirmationSnapshot expected,
            Models.OperationSnapshotVerifyOptions options = null,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(expected);
            options ??= new Models.OperationSnapshotVerifyOptions();

            if (string.IsNullOrWhiteSpace(expected.RepoPath) || !Directory.Exists(expected.RepoPath))
            {
                return new Models.OperationSnapshotVerificationResult
                {
                    IsValid = false,
                    Reasons = [Models.OperationSnapshotMismatchReason.RepoNotAccessible],
                    Details = [$"目标工作区目录已不存在或不可访问：{expected.RepoPath}"],
                    Summary = "仓库目录不可访问，原确认已失效。",
                };
            }

            Models.GitLockDetectionResult lockResult = new();
            if (options.CheckGitLocks)
            {
                lockResult = await Models.RepositoryWriteGuard.InspectLocksAsync(
                    expected.RepoPath,
                    options.CheckIndexLock,
                    options.BranchLockToCheck,
                    cancellationToken).ConfigureAwait(false);
                if (lockResult.IsLocked)
                {
                    return new Models.OperationSnapshotVerificationResult
                    {
                        IsValid = false,
                        Reasons = [Models.OperationSnapshotMismatchReason.GitLockPresent],
                        Details = lockResult.BlockingLockFiles,
                        LockDetection = lockResult,
                        Summary = lockResult.Summary,
                    };
                }
            }

            var current = await CaptureAsync(
                expected.RepoPath,
                new Models.OperationSnapshotCaptureOptions
                {
                    RefsToWatch = expected.WatchedRefs.Keys,
                    TargetPaths = expected.TargetFiles.Select(t => t.RelativePath),
                    IncludeTargetFileDetails = true,
                },
                cancellationToken).ConfigureAwait(false);

            var reasons = new List<Models.OperationSnapshotMismatchReason>();
            var details = new List<string>();
            var pathComparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            var pathComparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

            if (options.RequireSameRepoIdentity)
            {
                if (!string.Equals(expected.CanonicalCommonDir, current.CanonicalCommonDir, pathComparison) ||
                    !string.Equals(expected.CanonicalGitDir, current.CanonicalGitDir, pathComparison))
                {
                    reasons.Add(Models.OperationSnapshotMismatchReason.RepoIdentityChanged);
                    details.Add("仓库或工作树物理标识已发生变化。");
                }
            }

            if (options.RequireSameHead)
            {
                if (!string.Equals(expected.HeadSha, current.HeadSha, StringComparison.Ordinal) ||
                    expected.IsDetachedHead != current.IsDetachedHead ||
                    !string.Equals(expected.HeadRefName, current.HeadRefName, StringComparison.Ordinal))
                {
                    reasons.Add(Models.OperationSnapshotMismatchReason.HeadChanged);
                    details.Add($"当前 HEAD 已由 {ShortSha(expected.HeadSha)} 变为 {ShortSha(current.HeadSha)}。");
                }
            }

            if (options.RequireSameInProgressState && expected.InProgressKind != current.InProgressKind)
            {
                reasons.Add(Models.OperationSnapshotMismatchReason.InProgressStateChanged);
                details.Add($"仓库进行中状态已由 {expected.InProgressKind} 变为 {current.InProgressKind}。");
            }

            if (options.RequireCleanWorkingTree && !current.IsWorkingTreeClean)
            {
                reasons.Add(Models.OperationSnapshotMismatchReason.WorkingTreeNoLongerClean);
                details.Add("工作区或暂存区当前不再干净，存在未提交更改或冲突。");
            }

            if (options.RequireSameStagedFingerprint &&
                !string.Equals(expected.StagedFingerprint, current.StagedFingerprint, StringComparison.Ordinal))
            {
                reasons.Add(Models.OperationSnapshotMismatchReason.StagedSnapshotChanged);
                details.Add("暂存区内容与确认时快照不再一致。");
            }

            if (options.RequireSameWatchedRefs)
            {
                foreach (var kv in expected.WatchedRefs)
                {
                    current.WatchedRefs.TryGetValue(kv.Key, out var nowOid);
                    nowOid ??= string.Empty;
                    if (!string.Equals(kv.Value, nowOid, StringComparison.Ordinal))
                    {
                        if (!reasons.Contains(Models.OperationSnapshotMismatchReason.WatchedRefChanged))
                            reasons.Add(Models.OperationSnapshotMismatchReason.WatchedRefChanged);
                        details.Add($"引用 {kv.Key} 已由 {ShortSha(kv.Value)} 变为 {ShortSha(nowOid)}。");
                    }
                }
            }

            if (options.RequireSameTargetFiles && expected.TargetFiles.Count > 0)
            {
                var currentMap = current.TargetFiles.ToDictionary(
                    f => f.RelativePath,
                    pathComparer);
                foreach (var expFile in expected.TargetFiles)
                {
                    if (!currentMap.TryGetValue(expFile.RelativePath, out var curFile) ||
                        !TargetFileMatches(expFile, curFile))
                    {
                        if (!reasons.Contains(Models.OperationSnapshotMismatchReason.TargetFileChanged))
                            reasons.Add(Models.OperationSnapshotMismatchReason.TargetFileChanged);
                        details.Add($"目标文件 {expFile.RelativePath} 在工作区或索引中的内容已发生变化。");
                    }
                }
            }

            if (reasons.Count == 0)
            {
                return new Models.OperationSnapshotVerificationResult
                {
                    IsValid = true,
                    CurrentSnapshot = current,
                    LockDetection = lockResult,
                    Summary = string.Empty,
                };
            }

            var summary = "仓库状态已变化，请重新查看，原确认已失效。" + (details.Count > 0 ? " " + string.Join(" ", details) : string.Empty);
            return new Models.OperationSnapshotVerificationResult
            {
                IsValid = false,
                Reasons = reasons,
                Details = details,
                Summary = summary,
                CurrentSnapshot = current,
                LockDetection = lockResult,
            };
        }

        private static bool TargetFileMatches(Models.OperationTargetFileSnapshot expected, Models.OperationTargetFileSnapshot current)
        {
            return expected.ExistsOnDisk == current.ExistsOnDisk &&
                   expected.DiskLength == current.DiskLength &&
                   string.Equals(expected.DiskSha256, current.DiskSha256, StringComparison.Ordinal) &&
                   expected.ExistsInIndex == current.ExistsInIndex &&
                   string.Equals(expected.IndexSha1, current.IndexSha1, StringComparison.Ordinal) &&
                   expected.ExistsInHead == current.ExistsInHead &&
                   string.Equals(expected.HeadSha1, current.HeadSha1, StringComparison.Ordinal);
        }

        private static async Task<string> ResolveRefOidAsync(string repoPath, string fullRef, CancellationToken cancellationToken)
        {
            var res = await Command.RunGitDirectAsync(repoPath, cancellationToken, "show-ref", "--verify", "--", fullRef).ConfigureAwait(false);
            if (!res.IsSuccess || string.IsNullOrWhiteSpace(res.StdOut))
                return string.Empty;

            var firstSpace = res.StdOut.Trim().IndexOf(' ');
            return firstSpace > 0 ? res.StdOut.Trim().Substring(0, firstSpace) : string.Empty;
        }

        private static async Task<(bool Exists, long Length, string Sha256)> CaptureDiskFileAsync(
            string repoPath,
            string relPath,
            bool computeHash,
            CancellationToken cancellationToken)
        {
            var abs = Native.OS.GetAbsPath(repoPath, relPath);
            var info = new FileInfo(abs);
            var attrs = info.Attributes;
            var isReparse = info.LinkTarget != null ||
                            ((int)attrs != -1 && (attrs & FileAttributes.ReparsePoint) != 0);
            if (isReparse)
            {
                var target = !string.IsNullOrEmpty(info.LinkTarget)
                    ? info.LinkTarget
                    : new DirectoryInfo(abs).LinkTarget ?? string.Empty;
                return (true, 0, $"symlink:{target}");
            }

            if ((int)attrs != -1 && (attrs & FileAttributes.Directory) != 0)
                return (true, 0, "dir");

            if (!File.Exists(abs))
                return (false, 0, string.Empty);

            if (!computeHash)
                return (true, info.Length, string.Empty);

            try
            {
                await using var stream = new FileStream(
                    abs,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete,
                    bufferSize: 4096,
                    useAsync: true);
                var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
                return (true, info.Length, Convert.ToHexStringLower(hash));
            }
            catch (IOException)
            {
                return (true, info.Length, $"unreadable:{info.LastWriteTimeUtc.Ticks}");
            }
            catch (UnauthorizedAccessException)
            {
                return (true, info.Length, $"unreadable:{info.LastWriteTimeUtc.Ticks}");
            }
        }

        private static async Task<Dictionary<string, string>> ReadIndexBlobOidsAsync(
            string repoPath,
            List<string> relPaths,
            CancellationToken cancellationToken)
        {
            var cmp = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
            var map = new Dictionary<string, string>(cmp);
            foreach (var chunk in relPaths.Chunk(40))
            {
                var args = new List<string>(chunk.Length + 4) { "--literal-pathspecs", "ls-files", "--stage", "-z", "--" };
                args.AddRange(chunk);
                var res = await Command.RunGitDirectAsync(repoPath, cancellationToken, [.. args]).ConfigureAwait(false);
                if (!res.IsSuccess || string.IsNullOrEmpty(res.StdOut))
                    continue;

                var entries = res.StdOut.Split('\0', StringSplitOptions.RemoveEmptyEntries);
                foreach (var entry in entries)
                {
                    var tabIdx = entry.IndexOf('\t');
                    if (tabIdx <= 0 || tabIdx + 1 >= entry.Length)
                        continue;

                    var meta = entry.Substring(0, tabIdx).Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (meta.Length < 3)
                        continue;

                    var stage = meta[2];
                    if (stage != "0" && map.ContainsKey(entry.Substring(tabIdx + 1)))
                        continue;

                    map[entry.Substring(tabIdx + 1)] = meta[1];
                }
            }

            return map;
        }

        private static async Task<Dictionary<string, string>> ReadHeadTreeBlobOidsAsync(
            string repoPath,
            List<string> relPaths,
            CancellationToken cancellationToken)
        {
            var cmp = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
            var map = new Dictionary<string, string>(cmp);
            foreach (var chunk in relPaths.Chunk(40))
            {
                var args = new List<string>(chunk.Length + 4) { "--literal-pathspecs", "ls-tree", "-z", "HEAD", "--" };
                args.AddRange(chunk);
                var res = await Command.RunGitDirectAsync(repoPath, cancellationToken, [.. args]).ConfigureAwait(false);
                if (!res.IsSuccess || string.IsNullOrEmpty(res.StdOut))
                    continue;

                var entries = res.StdOut.Split('\0', StringSplitOptions.RemoveEmptyEntries);
                foreach (var entry in entries)
                {
                    var tabIdx = entry.IndexOf('\t');
                    if (tabIdx <= 0 || tabIdx + 1 >= entry.Length)
                        continue;

                    var meta = entry.Substring(0, tabIdx).Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (meta.Length < 3)
                        continue;

                    map[entry.Substring(tabIdx + 1)] = meta[2];
                }
            }

            return map;
        }

        private static string ShortSha(string sha)
        {
            if (string.IsNullOrWhiteSpace(sha))
                return "(不存在)";

            return sha.Length > 8 ? sha[..8] : sha;
        }
    }
}
