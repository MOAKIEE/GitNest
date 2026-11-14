using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;

namespace SourceGit.Models
{
    public sealed class DiscardBackupCaptureInput
    {
        public string RelativePath { get; init; } = string.Empty;
        public string OriginalPath { get; init; } = string.Empty;
        public DiscardBaselineKind Baseline { get; init; } = DiscardBaselineKind.RestoreWorktreeToIndex;
        public bool ExistsInWorktree { get; init; }
        public bool IsSymlink { get; init; }
        public string SymlinkTarget { get; init; } = string.Empty;
        public byte[] WorktreeBytes { get; init; }
        public bool ExistsInIndex { get; init; }
        public string IndexMode { get; init; } = string.Empty;
        public string IndexBlobOid { get; set; } = string.Empty;
        public byte[] IndexBytes { get; init; }
    }

    public sealed class DiscardBackupCaptureResult
    {
        public bool Success { get; init; }
        public string BackupId { get; init; } = string.Empty;
        public string Error { get; init; } = string.Empty;
        public DiscardBackupEntry Entry { get; init; }
    }

    /// <summary>
    /// 30-day local content recovery store for discarded tracked changes and unstaged/index snapshots.
    /// Kept isolated from Git refs/branches so recovery never leaks commits or refs, and never overwrites
    /// subsequent user edits silently.
    /// </summary>
    public sealed class DiscardBackupStore
    {
        public const int RetentionDays = 30;
        public const string AbsentMarker = "<ABSENT>";

        public static DiscardBackupStore Instance => _instance ??= new DiscardBackupStore(ResolveDefaultStoreRoot());

        public DiscardBackupStore(string storeRoot)
        {
            if (string.IsNullOrWhiteSpace(storeRoot))
                throw new ArgumentException("Backup store root cannot be empty.", nameof(storeRoot));

            _storeRoot = Path.GetFullPath(storeRoot);
            _indexFile = Path.Combine(_storeRoot, "index.json");
            _payloadRoot = Path.Combine(_storeRoot, "payloads");
        }

        public string StoreRoot => _storeRoot;

        public static string ResolveDefaultStoreRoot()
        {
            if (!string.IsNullOrWhiteSpace(Native.OS.BasicDirectories?.CacheDir))
                return Path.Combine(Native.OS.BasicDirectories.CacheDir, "discard-backups");

            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "GitNest",
                "discard-backups");
        }

        public static string ComputeSha256Hex(byte[] data)
        {
            return Convert.ToHexString(SHA256.HashData(data ?? []));
        }

        public static string ComputeFileSha256Hex(string filePath)
        {
            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            return Convert.ToHexString(SHA256.HashData(stream));
        }

        public DiscardBackupCaptureResult Capture(
            string repoPath,
            string gitCommonDir,
            string headSha,
            DiscardBaselineKind baseline,
            string summary,
            IReadOnlyList<DiscardBackupCaptureInput> inputs,
            DateTime? utcNowOverride = null)
        {
            if (inputs == null || inputs.Count == 0)
                return new DiscardBackupCaptureResult { Success = false, Error = "没有需要写入恢复库的文件。" };

            var nowUtc = EnsureUtc(utcNowOverride ?? DateTime.UtcNow);
            var backupId = $"{nowUtc:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..8]}";
            var backupPayloadDir = Path.Combine(_payloadRoot, backupId);

            lock (_gate)
            {
                try
                {
                    if (!Directory.Exists(_storeRoot))
                        Directory.CreateDirectory(_storeRoot);
                    if (!Directory.Exists(_payloadRoot))
                        Directory.CreateDirectory(_payloadRoot);
                    Directory.CreateDirectory(backupPayloadDir);

                    var fileEntries = new List<DiscardBackupFileEntry>(inputs.Count);
                    for (var i = 0; i < inputs.Count; i++)
                    {
                        var item = inputs[i];
                        if (string.IsNullOrWhiteSpace(item.RelativePath))
                            throw new InvalidOperationException("恢复库条目路径不能为空。");

                        var entry = new DiscardBackupFileEntry
                        {
                            RelativePath = NormalizeRepoRelativePath(item.RelativePath),
                            OriginalPath = string.IsNullOrWhiteSpace(item.OriginalPath) ? string.Empty : NormalizeRepoRelativePath(item.OriginalPath),
                            Baseline = item.Baseline,
                            ExistsInWorktree = item.ExistsInWorktree,
                            IsSymlink = item.IsSymlink,
                            SymlinkTarget = item.SymlinkTarget ?? string.Empty,
                            WorktreeSha256 = string.Empty,
                            WorktreeByteLength = 0,
                            BackupPayloadRelativePath = string.Empty,
                            ExistsInIndex = item.ExistsInIndex,
                            IndexMode = item.IndexMode ?? string.Empty,
                            IndexBlobOid = item.IndexBlobOid ?? string.Empty,
                            IndexPayloadRelativePath = string.Empty,
                        };

                        if (item.ExistsInWorktree && !item.IsSymlink)
                        {
                            if (item.WorktreeBytes == null)
                                throw new InvalidOperationException($"未能读取工作区文件内容：{item.RelativePath}");

                            var payloadRel = $"{i:D4}-wt.bin";
                            var payloadAbs = Path.Combine(backupPayloadDir, payloadRel);
                            File.WriteAllBytes(payloadAbs, item.WorktreeBytes);

                            var verifiedLength = new FileInfo(payloadAbs).Length;
                            if (verifiedLength != item.WorktreeBytes.LongLength)
                                throw new IOException($"备份载荷写入校验长度不一致：{item.RelativePath}");

                            entry.WorktreeSha256 = ComputeSha256Hex(item.WorktreeBytes);
                            entry.WorktreeByteLength = item.WorktreeBytes.LongLength;
                            entry.BackupPayloadRelativePath = Path.Combine("payloads", backupId, payloadRel).Replace('\\', '/');
                        }
                        else if (item.ExistsInWorktree && item.IsSymlink)
                        {
                            entry.WorktreeSha256 = ComputeSha256Hex(System.Text.Encoding.UTF8.GetBytes(entry.SymlinkTarget));
                        }

                        if (item.ExistsInIndex && item.IndexBytes != null)
                        {
                            var idxRel = $"{i:D4}-idx.bin";
                            var idxAbs = Path.Combine(backupPayloadDir, idxRel);
                            File.WriteAllBytes(idxAbs, item.IndexBytes);
                            entry.IndexPayloadRelativePath = Path.Combine("payloads", backupId, idxRel).Replace('\\', '/');
                        }

                        fileEntries.Add(entry);
                    }

                    var index = LoadIndexLocked();
                    var record = new DiscardBackupEntry
                    {
                        BackupId = backupId,
                        CreatedAtUtc = nowUtc,
                        ExpiresAtUtc = nowUtc.AddDays(RetentionDays),
                        RepoKey = NormalizePathKey(string.IsNullOrWhiteSpace(gitCommonDir) ? repoPath : gitCommonDir),
                        WorktreePath = NormalizePathKey(repoPath),
                        GitCommonDir = NormalizePathKey(gitCommonDir),
                        HeadSha = (headSha ?? string.Empty).Trim(),
                        Baseline = baseline,
                        Summary = GitConfigSnapshot.RedactText(summary ?? string.Empty),
                        Files = fileEntries,
                    };

                    index.Entries.Insert(0, record);
                    SaveIndexLocked(index);

                    return new DiscardBackupCaptureResult
                    {
                        Success = true,
                        BackupId = backupId,
                        Entry = CloneEntry(record),
                    };
                }
                catch (Exception ex)
                {
                    TryDeleteDirectoryBestEffort(backupPayloadDir);
                    return new DiscardBackupCaptureResult
                    {
                        Success = false,
                        Error = GitConfigSnapshot.RedactText(ex.Message),
                    };
                }
            }
        }

        public bool UpdatePostDiscardState(
            string backupId,
            IReadOnlyDictionary<string, (string WorktreeSha256, string IndexBlobOid)> postStates)
        {
            if (string.IsNullOrWhiteSpace(backupId) || postStates == null)
                return false;

            lock (_gate)
            {
                try
                {
                    var index = LoadIndexLocked();
                    var entry = index.Entries.Find(e => string.Equals(e.BackupId, backupId, StringComparison.Ordinal));
                    if (entry == null)
                        return false;

                    foreach (var file in entry.Files)
                    {
                        if (postStates.TryGetValue(file.RelativePath, out var state))
                        {
                            file.PostDiscardWorktreeSha256 = state.WorktreeSha256 ?? AbsentMarker;
                            file.PostDiscardIndexBlobOid = state.IndexBlobOid ?? AbsentMarker;
                        }
                    }

                    SaveIndexLocked(index);
                    return true;
                }
                catch
                {
                    return false;
                }
            }
        }

        public bool MarkRestored(string backupId, DateTime? utcNowOverride = null)
        {
            if (string.IsNullOrWhiteSpace(backupId))
                return false;

            lock (_gate)
            {
                try
                {
                    var index = LoadIndexLocked();
                    var entry = index.Entries.Find(e => string.Equals(e.BackupId, backupId, StringComparison.Ordinal));
                    if (entry == null)
                        return false;

                    entry.RestoredAtUtc = EnsureUtc(utcNowOverride ?? DateTime.UtcNow);
                    SaveIndexLocked(index);
                    return true;
                }
                catch
                {
                    return false;
                }
            }
        }

        public DiscardBackupEntry TryGet(string backupId)
        {
            if (string.IsNullOrWhiteSpace(backupId))
                return null;

            lock (_gate)
            {
                var index = LoadIndexLocked();
                var found = index.Entries.Find(e => string.Equals(e.BackupId, backupId, StringComparison.Ordinal));
                return found == null ? null : CloneEntry(found);
            }
        }

        public List<DiscardBackupEntry> ListForRepository(string repoPath, string gitCommonDir = null, DateTime? utcNowOverride = null, bool includeExpired = true)
        {
            var repoKey = NormalizePathKey(repoPath);
            var commonKey = NormalizePathKey(gitCommonDir);
            var nowUtc = EnsureUtc(utcNowOverride ?? DateTime.UtcNow);

            lock (_gate)
            {
                var index = LoadIndexLocked();
                return index.Entries
                    .Where(e =>
                    {
                        var matchesRepo =
                            (!string.IsNullOrEmpty(commonKey) && PathsEqual(e.GitCommonDir, commonKey)) ||
                            PathsEqual(e.WorktreePath, repoKey) ||
                            PathsEqual(e.RepoKey, commonKey) ||
                            PathsEqual(e.RepoKey, repoKey);
                        if (!matchesRepo)
                            return false;

                        return includeExpired || EnsureUtc(e.ExpiresAtUtc) > nowUtc;
                    })
                    .OrderByDescending(e => e.CreatedAtUtc)
                    .Select(CloneEntry)
                    .ToList();
            }
        }

        public byte[] ReadWorktreePayloadBytes(DiscardBackupFileEntry file, out string error)
        {
            error = string.Empty;
            if (file == null || !file.ExistsInWorktree)
                return null;

            if (file.IsSymlink)
                return null;

            if (string.IsNullOrWhiteSpace(file.BackupPayloadRelativePath))
            {
                error = $"恢复载荷路径缺失：{file.RelativePath}";
                return null;
            }

            var absPath = Path.GetFullPath(Path.Combine(_storeRoot, file.BackupPayloadRelativePath));
            if (!IsPathInsideRoot(absPath, _payloadRoot) || !File.Exists(absPath))
            {
                error = $"恢复载荷文件丢失：{file.RelativePath}";
                return null;
            }

            try
            {
                var bytes = File.ReadAllBytes(absPath);
                var actualSha = ComputeSha256Hex(bytes);
                if (!string.Equals(actualSha, file.WorktreeSha256, StringComparison.OrdinalIgnoreCase))
                {
                    error = $"恢复载荷校验失败（文件可能已损坏）：{file.RelativePath}";
                    return null;
                }

                return bytes;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return null;
            }
        }

        public byte[] ReadIndexPayloadBytes(DiscardBackupFileEntry file, out string error)
        {
            error = string.Empty;
            if (file == null || !file.ExistsInIndex || string.IsNullOrWhiteSpace(file.IndexPayloadRelativePath))
                return null;

            var absPath = Path.GetFullPath(Path.Combine(_storeRoot, file.IndexPayloadRelativePath));
            if (!IsPathInsideRoot(absPath, _payloadRoot) || !File.Exists(absPath))
            {
                error = $"暂存区恢复载荷文件丢失：{file.RelativePath}";
                return null;
            }

            try
            {
                return File.ReadAllBytes(absPath);
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return null;
            }
        }

        public bool VerifyPayloadIntegrity(DiscardBackupEntry entry, out List<string> missingOrCorruptPaths)
        {
            missingOrCorruptPaths = [];
            if (entry == null)
                return false;

            foreach (var file in entry.Files)
            {
                if (file.ExistsInWorktree && !file.IsSymlink)
                {
                    _ = ReadWorktreePayloadBytes(file, out var err);
                    if (!string.IsNullOrEmpty(err))
                        missingOrCorruptPaths.Add(file.RelativePath);
                }
                else if (file.ExistsInIndex && !string.IsNullOrEmpty(file.IndexPayloadRelativePath))
                {
                    _ = ReadIndexPayloadBytes(file, out var idxErr);
                    if (!string.IsNullOrEmpty(idxErr))
                        missingOrCorruptPaths.Add(file.RelativePath);
                }
            }

            return missingOrCorruptPaths.Count == 0;
        }

        public DiscardBackupPruneResult PruneExpired(DateTime? utcNowOverride = null)
        {
            var nowUtc = EnsureUtc(utcNowOverride ?? DateTime.UtcNow);
            var removedIds = new List<string>();

            lock (_gate)
            {
                var index = LoadIndexLocked();
                var keep = new List<DiscardBackupEntry>(index.Entries.Count);
                foreach (var entry in index.Entries)
                {
                    if (EnsureUtc(entry.ExpiresAtUtc) <= nowUtc)
                        removedIds.Add(entry.BackupId);
                    else
                        keep.Add(entry);
                }

                if (removedIds.Count > 0)
                {
                    index.Entries = keep;
                    SaveIndexLocked(index);

                    foreach (var id in removedIds)
                        TryDeleteDirectoryBestEffort(Path.Combine(_payloadRoot, id));
                }
            }

            return new DiscardBackupPruneResult
            {
                ExpiredCount = removedIds.Count,
                RemovedCount = removedIds.Count,
                RemovedBackupIds = removedIds,
                Summary = removedIds.Count == 0
                    ? "没有已超过 30 天保留期的恢复记录。"
                    : $"已清理 {removedIds.Count} 条超过 30 天保留期的本地恢复记录。",
            };
        }

        public bool DeleteBackup(string backupId)
        {
            if (string.IsNullOrWhiteSpace(backupId))
                return false;

            lock (_gate)
            {
                var index = LoadIndexLocked();
                var removed = index.Entries.RemoveAll(e => string.Equals(e.BackupId, backupId, StringComparison.Ordinal));
                if (removed == 0)
                    return false;

                SaveIndexLocked(index);
                TryDeleteDirectoryBestEffort(Path.Combine(_payloadRoot, backupId));
                return true;
            }
        }

        private DiscardBackupIndex LoadIndexLocked()
        {
            if (!File.Exists(_indexFile))
                return new DiscardBackupIndex();

            var text = File.ReadAllText(_indexFile);
            if (string.IsNullOrWhiteSpace(text))
                return new DiscardBackupIndex();

            return JsonSerializer.Deserialize(text, JsonCodeGen.Default.DiscardBackupIndex) ?? new DiscardBackupIndex();
        }

        private void SaveIndexLocked(DiscardBackupIndex index)
        {
            if (!Directory.Exists(_storeRoot))
                Directory.CreateDirectory(_storeRoot);

            var tmp = _indexFile + $".tmp-{Guid.NewGuid():N}";
            var json = JsonSerializer.Serialize(index, JsonCodeGen.Default.DiscardBackupIndex);
            File.WriteAllText(tmp, json);

            if (File.Exists(_indexFile))
                File.Replace(tmp, _indexFile, null, ignoreMetadataErrors: true);
            else
                File.Move(tmp, _indexFile);
        }

        private static DiscardBackupEntry CloneEntry(DiscardBackupEntry src)
        {
            return new DiscardBackupEntry
            {
                BackupId = src.BackupId,
                CreatedAtUtc = EnsureUtc(src.CreatedAtUtc),
                ExpiresAtUtc = EnsureUtc(src.ExpiresAtUtc),
                RepoKey = src.RepoKey,
                WorktreePath = src.WorktreePath,
                GitCommonDir = src.GitCommonDir,
                HeadSha = src.HeadSha,
                Baseline = src.Baseline,
                Summary = src.Summary,
                RestoredAtUtc = src.RestoredAtUtc.HasValue ? EnsureUtc(src.RestoredAtUtc.Value) : null,
                Files = src.Files.Select(f => new DiscardBackupFileEntry
                {
                    RelativePath = f.RelativePath,
                    OriginalPath = f.OriginalPath,
                    Baseline = f.Baseline,
                    ExistsInWorktree = f.ExistsInWorktree,
                    IsSymlink = f.IsSymlink,
                    SymlinkTarget = f.SymlinkTarget,
                    WorktreeSha256 = f.WorktreeSha256,
                    WorktreeByteLength = f.WorktreeByteLength,
                    BackupPayloadRelativePath = f.BackupPayloadRelativePath,
                    ExistsInIndex = f.ExistsInIndex,
                    IndexMode = f.IndexMode,
                    IndexBlobOid = f.IndexBlobOid,
                    IndexPayloadRelativePath = f.IndexPayloadRelativePath,
                    PostDiscardWorktreeSha256 = f.PostDiscardWorktreeSha256,
                    PostDiscardIndexBlobOid = f.PostDiscardIndexBlobOid,
                }).ToList(),
            };
        }

        private static string NormalizeRepoRelativePath(string path) => path.Trim().Replace('\\', '/').TrimStart('/');

        private static string NormalizePathKey(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return string.Empty;

            try
            {
                return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)).Replace('\\', '/');
            }
            catch
            {
                return path.Trim().Replace('\\', '/').TrimEnd('/');
            }
        }

        private static bool PathsEqual(string left, string right) =>
            !string.IsNullOrEmpty(left) &&
            !string.IsNullOrEmpty(right) &&
            string.Equals(
                NormalizePathKey(left),
                NormalizePathKey(right),
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

        private static bool IsPathInsideRoot(string candidatePath, string rootPath)
        {
            var fullCandidate = Path.GetFullPath(candidatePath);
            var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootPath)) + Path.DirectorySeparatorChar;
            return fullCandidate.StartsWith(
                fullRoot,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        }

        private static DateTime EnsureUtc(DateTime value)
        {
            if (value == default)
                return DateTime.UtcNow;

            return value.Kind switch
            {
                DateTimeKind.Utc => value,
                DateTimeKind.Local => value.ToUniversalTime(),
                _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
            };
        }

        private static void TryDeleteDirectoryBestEffort(string path)
        {
            try
            {
                if (!Directory.Exists(path))
                    return;

                foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
                    File.SetAttributes(file, FileAttributes.Normal);

                Directory.Delete(path, true);
            }
            catch
            {
                // Best-effort cleanup of temp/expired backup payloads.
            }
        }

        private static DiscardBackupStore _instance;
        private readonly string _storeRoot;
        private readonly string _indexFile;
        private readonly string _payloadRoot;
        private readonly object _gate = new();
    }
}
