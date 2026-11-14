using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace SourceGit.Models
{
    /// <summary>
    /// Thread-safe, secret-redacted atomic JSON journal for high-risk and synced Git operations.
    /// Records survive crashes/restarts so incomplete states can be inspected honestly on startup.
    /// </summary>
    public sealed class OperationJournalStore
    {
        private const int MaxTerminalRecordsPerRepo = 120;
        private const int TerminalRetentionDays = 30;

        public static OperationJournalStore Instance =>
            _instance ??= new OperationJournalStore(Path.Combine(Native.OS.BasicDirectories.ConfigDir, "operation-journal.json"));

        public OperationJournalStore(string file)
        {
            _file = Path.GetFullPath(file);
            try
            {
                if (File.Exists(_file))
                {
                    var text = File.ReadAllText(_file);
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        var loaded = JsonSerializer.Deserialize(text, JsonCodeGen.Default.ListGitOperationRecord) ?? [];
                        foreach (var item in loaded)
                        {
                            if (item != null && !string.IsNullOrWhiteSpace(item.OperationId))
                                _records.Add(RedactAndNormalizeRecord(item));
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _readError = ex.Message;
                _records = [];
            }
        }

        public string ReadError => _readError;

        public static string GenerateOperationId()
        {
            var stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
            var suffix = Guid.NewGuid().ToString("N")[..8];
            return $"{stamp}-{suffix}";
        }

        public static bool RequiresAttentionStage(GitOperationStage stage) =>
            stage is GitOperationStage.Preparing
                or GitOperationStage.AwaitingConfirmation
                or GitOperationStage.Executing
                or GitOperationStage.PendingVerification
                or GitOperationStage.CanceledUncertain
                or GitOperationStage.PartialSuccess;

        public void AppendOrUpdate(GitOperationRecord record)
        {
            ArgumentNullException.ThrowIfNull(record);

            lock (_gate)
            {
                if (_readError != null)
                    throw new InvalidDataException(_readError);

                var normalized = RedactAndNormalizeRecord(record);
                normalized.UpdatedAtUtc = DateTime.UtcNow;
                if (string.IsNullOrWhiteSpace(normalized.OperationId))
                    normalized.OperationId = GenerateOperationId();

                var idx = _records.FindIndex(r => string.Equals(r.OperationId, normalized.OperationId, StringComparison.Ordinal));
                if (idx >= 0)
                    _records[idx] = normalized;
                else
                    _records.Add(normalized);

                PruneOldRecordsLocked(normalized.RepositoryCommonDir);
                PersistLocked();
            }
        }

        public GitOperationRecord TryGet(string operationId)
        {
            if (string.IsNullOrWhiteSpace(operationId))
                return null;

            lock (_gate)
            {
                var found = _records.Find(r => string.Equals(r.OperationId, operationId, StringComparison.Ordinal));
                return found == null ? null : CloneRecord(found);
            }
        }

        public List<GitOperationRecord> GetByRepository(string commonDir, int maxCount = 100)
        {
            var normalizedDir = NormalizeRepoKey(commonDir);
            if (string.IsNullOrEmpty(normalizedDir))
                return [];

            lock (_gate)
            {
                return _records
                    .AsEnumerable()
                    .Reverse()
                    .Where(r => PathsEqual(r.RepositoryCommonDir, normalizedDir))
                    .OrderByDescending(r => r.CreatedAtUtc)
                    .Take(Math.Max(1, maxCount))
                    .Select(CloneRecord)
                    .ToList();
            }
        }

        public List<GitOperationRecord> GetAttentionRecordsByRepository(string commonDir)
        {
            var normalizedDir = NormalizeRepoKey(commonDir);
            if (string.IsNullOrEmpty(normalizedDir))
                return [];

            lock (_gate)
            {
                return _records
                    .AsEnumerable()
                    .Reverse()
                    .Where(r => PathsEqual(r.RepositoryCommonDir, normalizedDir) &&
                                !r.IsAcknowledged &&
                                (r.IsUnfinishedOnStartup || RequiresAttentionStage(r.Stage)))
                    .OrderByDescending(r => r.UpdatedAtUtc)
                    .Select(CloneRecord)
                    .ToList();
            }
        }

        /// <summary>
        /// Identifies records left in an active/uncertain stage from an earlier process run and persists
        /// honest startup markers without mutating the Git repository.
        /// </summary>
        public List<GitOperationRecord> MarkUnfinishedFromPreviousProcess(string commonDir)
        {
            var normalizedDir = NormalizeRepoKey(commonDir);
            if (string.IsNullOrEmpty(normalizedDir))
                return [];

            lock (_gate)
            {
                if (_readError != null)
                    return [];

                var dirty = false;
                var flagged = new List<GitOperationRecord>();
                foreach (var record in _records)
                {
                    if (!PathsEqual(record.RepositoryCommonDir, normalizedDir))
                        continue;

                    if (!record.IsAcknowledged &&
                        record.Stage is GitOperationStage.Preparing
                        or GitOperationStage.AwaitingConfirmation
                        or GitOperationStage.Executing
                        or GitOperationStage.PendingVerification
                        or GitOperationStage.CanceledUncertain)
                    {
                        if (!record.IsUnfinishedOnStartup || record.Stage is GitOperationStage.Preparing or GitOperationStage.Executing)
                        {
                            record.IsUnfinishedOnStartup = true;
                            if (record.Stage is GitOperationStage.Preparing or GitOperationStage.Executing)
                                record.Stage = GitOperationStage.PendingVerification;
                            record.UpdatedAtUtc = DateTime.UtcNow;
                            dirty = true;
                        }
                    }

                    if (!record.IsAcknowledged && (record.IsUnfinishedOnStartup || RequiresAttentionStage(record.Stage)))
                        flagged.Add(CloneRecord(record));
                }

                if (dirty)
                    PersistLocked();

                return flagged;
            }
        }

        private void PruneOldRecordsLocked(string commonDir)
        {
            if (string.IsNullOrWhiteSpace(commonDir))
                return;

            var cutoff = DateTime.UtcNow.AddDays(-TerminalRetentionDays);
            var repoTerminal = _records
                .Where(r => PathsEqual(r.RepositoryCommonDir, commonDir) &&
                            !r.IsUnfinishedOnStartup &&
                            !RequiresAttentionStage(r.Stage) &&
                            string.IsNullOrEmpty(r.BackupRefName))
                .OrderByDescending(r => r.CreatedAtUtc)
                .ToList();

            var toRemove = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 0; i < repoTerminal.Count; i++)
            {
                var item = repoTerminal[i];
                if (i >= MaxTerminalRecordsPerRepo || item.CreatedAtUtc < cutoff)
                    toRemove.Add(item.OperationId);
            }

            if (toRemove.Count > 0)
                _records.RemoveAll(r => toRemove.Contains(r.OperationId));
        }

        private void PersistLocked()
        {
            var dir = Path.GetDirectoryName(_file);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            var tempFile = _file + $".{Guid.NewGuid():N}.tmp";
            try
            {
                var content = JsonSerializer.Serialize(_records, JsonCodeGen.Default.ListGitOperationRecord);
                File.WriteAllText(tempFile, content);
                File.Move(tempFile, _file, true);
            }
            catch
            {
                try
                {
                    if (File.Exists(tempFile))
                        File.Delete(tempFile);
                }
                catch
                {
                    // Ignore best-effort temp cleanup failure.
                }

                throw;
            }
        }

        private static GitOperationRecord RedactAndNormalizeRecord(GitOperationRecord src)
        {
            return new GitOperationRecord
            {
                OperationId = (src.OperationId ?? string.Empty).Trim(),
                RepositoryCommonDir = NormalizeRepoKey(src.RepositoryCommonDir),
                WorktreePath = NormalizeRepoKey(GitConfigSnapshot.RedactText(src.WorktreePath)),
                Kind = src.Kind,
                Title = GitConfigSnapshot.RedactText(src.Title),
                BranchName = (src.BranchName ?? string.Empty).Trim(),
                RemoteName = (src.RemoteName ?? string.Empty).Trim(),
                RemoteBranchName = (src.RemoteBranchName ?? string.Empty).Trim(),
                OriginalTipSha = (src.OriginalTipSha ?? string.Empty).Trim(),
                TargetTipSha = (src.TargetTipSha ?? string.Empty).Trim(),
                ExpectedRemoteTipSha = (src.ExpectedRemoteTipSha ?? string.Empty).Trim(),
                BackupRefName = (src.BackupRefName ?? string.Empty).Trim(),
                Stage = src.Stage,
                StepSummary = GitConfigSnapshot.RedactText(src.StepSummary),
                CommandSummary = GitConfigSnapshot.RedactText(src.CommandSummary),
                DiagnosticDetail = GitConfigSnapshot.RedactText(src.DiagnosticDetail),
                CreatedAtUtc = EnsureUtc(src.CreatedAtUtc, DateTime.UtcNow),
                UpdatedAtUtc = EnsureUtc(src.UpdatedAtUtc, DateTime.UtcNow),
                CompletedAtUtc = src.CompletedAtUtc.HasValue ? EnsureUtc(src.CompletedAtUtc.Value, DateTime.UtcNow) : null,
                IsUnfinishedOnStartup = src.IsUnfinishedOnStartup,
                IsAcknowledged = src.IsAcknowledged,
                ReconciliationNote = GitConfigSnapshot.RedactText(src.ReconciliationNote),
            };
        }

        private static DateTime EnsureUtc(DateTime value, DateTime fallback)
        {
            if (value == default)
                return fallback;

            return value.Kind switch
            {
                DateTimeKind.Utc => value,
                DateTimeKind.Local => value.ToUniversalTime(),
                _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
            };
        }

        private static GitOperationRecord CloneRecord(GitOperationRecord src) => RedactAndNormalizeRecord(src);

        private static string NormalizeRepoKey(string path)
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
            string.Equals(
                NormalizeRepoKey(left),
                NormalizeRepoKey(right),
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

        private static OperationJournalStore _instance;
        private readonly string _file;
        private readonly string _readError;
        private readonly object _gate = new();
        private readonly List<GitOperationRecord> _records = [];
    }
}
