using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace SourceGit.Commands
{
    /// <summary>
    /// Manages local-only recovery references under refs/gitnest/backups/<operation-id> with
    /// explicit branch occupancy checks on restore and previewed 30-day retention cleanup.
    /// </summary>
    public static class BackupRefs
    {
        public const string NamespacePrefix = "refs/gitnest/backups/";
        public const int DefaultRetentionDays = 30;

        public static string BuildBackupRefName(string operationId)
        {
            if (string.IsNullOrWhiteSpace(operationId))
                throw new ArgumentException("Operation id cannot be empty.", nameof(operationId));

            var sanitized = new string(operationId.Trim()
                .Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '-')
                .ToArray());

            if (string.IsNullOrWhiteSpace(sanitized) || sanitized.Contains("..", StringComparison.Ordinal))
                throw new ArgumentException($"Invalid operation id for backup ref: {operationId}", nameof(operationId));

            return NamespacePrefix + sanitized;
        }

        public static async Task<Models.GitBackupCreateResult> CreateAsync(
            string repoPath,
            string operationId,
            string targetCommitOrRef,
            Models.ICommandLog log = null,
            CancellationToken cancellationToken = default)
        {
            var resolved = await Command.RunGitDirectAsync(
                repoPath,
                cancellationToken,
                "rev-parse",
                "--verify",
                $"{targetCommitOrRef}^{{commit}}").ConfigureAwait(false);
            if (!resolved.IsSuccess || string.IsNullOrWhiteSpace(resolved.StdOut))
            {
                return new Models.GitBackupCreateResult
                {
                    Succeeded = false,
                    Summary = $"无法解析目标提交 '{targetCommitOrRef}' 以建立恢复引用：{Models.GitConfigSnapshot.RedactText(resolved.StdErr.Trim())}",
                };
            }

            var sha = resolved.StdOut.Trim();
            var backupRef = BuildBackupRefName(operationId);

            var update = await Command.RunGitDirectAsync(
                repoPath,
                cancellationToken,
                "-c",
                "core.logAllRefUpdates=always",
                "update-ref",
                "--no-deref",
                "-m",
                $"GitNest backup ({operationId})",
                backupRef,
                sha).ConfigureAwait(false);

            log?.AppendLine($"$ git update-ref --no-deref {backupRef} {sha}");
            if (!update.IsSuccess)
            {
                var err = Models.GitConfigSnapshot.RedactText(update.StdErr.Trim());
                log?.AppendLine(err);
                return new Models.GitBackupCreateResult
                {
                    Succeeded = false,
                    BackupRefName = backupRef,
                    CommitSha = sha,
                    Summary = string.IsNullOrWhiteSpace(err) ? $"创建恢复引用 {backupRef} 失败。" : err,
                };
            }

            return new Models.GitBackupCreateResult
            {
                Succeeded = true,
                BackupRefName = backupRef,
                CommitSha = sha,
                Summary = $"已建立本地恢复引用 {backupRef}（指向 {(sha.Length > 8 ? sha[..8] : sha)}）。",
            };
        }

        public static async Task<bool> ExistsAsync(
            string repoPath,
            string backupRef,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(backupRef) || !backupRef.StartsWith(NamespacePrefix, StringComparison.Ordinal))
                return false;

            var res = await Command.RunGitDirectAsync(repoPath, cancellationToken, "show-ref", "--verify", "--quiet", backupRef)
                .ConfigureAwait(false);
            return res.IsSuccess;
        }

        public static async Task<List<Models.GitBackupRefInfo>> ListAsync(
            string repoPath,
            int retentionDays = DefaultRetentionDays,
            Models.OperationJournalStore journalStore = null,
            DateTime? utcNowOverride = null,
            CancellationToken cancellationToken = default)
        {
            var res = await Command.RunGitDirectAsync(
                repoPath,
                cancellationToken,
                "for-each-ref",
                "--format=%(refname)%00%(objectname)%00%(creatordate:unix)%00%(subject)",
                NamespacePrefix).ConfigureAwait(false);

            if (!res.IsSuccess || string.IsNullOrWhiteSpace(res.StdOut))
                return [];

            var nowUtc = utcNowOverride ?? DateTime.UtcNow;
            var store = journalStore ?? Models.OperationJournalStore.Instance;
            var list = new List<Models.GitBackupRefInfo>();

            var lines = res.StdOut.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
            foreach (var line in lines)
            {
                var parts = line.Split('\0');
                if (parts.Length < 4)
                    continue;

                var fullRef = parts[0].Trim();
                if (!fullRef.StartsWith(NamespacePrefix, StringComparison.Ordinal))
                    continue;

                var opId = fullRef.Substring(NamespacePrefix.Length);
                var sha = parts[1].Trim();
                var subject = parts[3].Trim();

                // Prefer the persisted backup creation timestamp or the reflog creation timestamp
                // rather than the underlying commit's author/committer timestamp (which may be months old).
                var createdAtUtc = store.TryGet(opId)?.CreatedAtUtc ?? DateTime.MinValue;
                if (createdAtUtc == DateTime.MinValue)
                    createdAtUtc = await TryReadReflogTimestampAsync(repoPath, fullRef, cancellationToken).ConfigureAwait(false);

                if (createdAtUtc == DateTime.MinValue &&
                    long.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var commitUnix) &&
                    commitUnix > 0)
                {
                    createdAtUtc = DateTimeOffset.FromUnixTimeSeconds(commitUnix).UtcDateTime;
                }

                if (createdAtUtc == DateTime.MinValue)
                    createdAtUtc = nowUtc;

                var ageDays = Math.Max(0, (nowUtc - createdAtUtc).TotalDays);
                list.Add(new Models.GitBackupRefInfo
                {
                    FullRefName = fullRef,
                    OperationId = opId,
                    CommitSha = sha,
                    CreatedAtUtc = createdAtUtc,
                    Subject = subject,
                    AgeDays = ageDays,
                    IsExpired = ageDays >= retentionDays,
                });
            }

            return list.OrderByDescending(i => i.CreatedAtUtc).ToList();
        }

        public static async Task<Models.GitBackupRestoreResult> RestoreBranchFromBackupAsync(
            string repoPath,
            string branchName,
            string backupRef,
            Models.ICommandLog log = null,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(backupRef) || !backupRef.StartsWith(NamespacePrefix, StringComparison.Ordinal))
            {
                return new Models.GitBackupRestoreResult
                {
                    Succeeded = false,
                    BranchName = branchName ?? string.Empty,
                    BackupRefName = backupRef ?? string.Empty,
                    Summary = "无效的本地恢复引用名称。",
                };
            }

            if (string.IsNullOrWhiteSpace(branchName))
            {
                return new Models.GitBackupRestoreResult
                {
                    Succeeded = false,
                    BackupRefName = backupRef,
                    Summary = "恢复分支名称不能为空。",
                };
            }

            var checkName = await Command.RunGitDirectAsync(repoPath, cancellationToken, "check-ref-format", "--branch", branchName)
                .ConfigureAwait(false);
            if (!checkName.IsSuccess)
            {
                return new Models.GitBackupRestoreResult
                {
                    Succeeded = false,
                    BranchName = branchName,
                    BackupRefName = backupRef,
                    Summary = $"分支名称 '{branchName}' 不符合 Git 引用命名规范。",
                };
            }

            await using var guard = await Models.RepositoryWriteGuard.TryEnterAsync(
                repoPath,
                $"恢复分支 {branchName}",
                checkIndexLock: false,
                branchNameToUpdate: branchName,
                cancellationToken: cancellationToken).ConfigureAwait(false);

            if (!guard.Acquired)
            {
                return new Models.GitBackupRestoreResult
                {
                    Succeeded = false,
                    BlockedByLock = true,
                    BranchName = branchName,
                    BackupRefName = backupRef,
                    Summary = guard.LockDetection.Summary,
                };
            }

            var fullBranchRef = "refs/heads/" + branchName;
            var branchExists = await Command.RunGitDirectAsync(repoPath, cancellationToken, "show-ref", "--verify", "--quiet", fullBranchRef)
                .ConfigureAwait(false);
            if (branchExists.IsSuccess)
            {
                return new Models.GitBackupRestoreResult
                {
                    Succeeded = false,
                    BlockedByExistingBranch = true,
                    BranchName = branchName,
                    BackupRefName = backupRef,
                    Summary = $"本地分支 '{branchName}' 已被重新创建或占用，已拒绝自动覆盖。请改用其他名称或手动处理。",
                };
            }

            var backupResolved = await Command.RunGitDirectAsync(
                repoPath,
                cancellationToken,
                "rev-parse",
                "--verify",
                $"{backupRef}^{{commit}}").ConfigureAwait(false);
            if (!backupResolved.IsSuccess || string.IsNullOrWhiteSpace(backupResolved.StdOut))
            {
                return new Models.GitBackupRestoreResult
                {
                    Succeeded = false,
                    BranchName = branchName,
                    BackupRefName = backupRef,
                    Summary = $"恢复引用 {backupRef} 已失效或不再指向有效提交。",
                };
            }

            var targetSha = backupResolved.StdOut.Trim();
            var restore = await Command.RunGitDirectAsync(
                repoPath,
                cancellationToken,
                "update-ref",
                "--no-deref",
                "-m",
                $"GitNest restore branch {branchName} from {backupRef}",
                fullBranchRef,
                targetSha,
                string.Empty).ConfigureAwait(false);

            log?.AppendLine($"$ git update-ref --no-deref {fullBranchRef} {targetSha} \"\"");
            if (!restore.IsSuccess)
            {
                var err = Models.GitConfigSnapshot.RedactText(restore.StdErr.Trim());
                log?.AppendLine(err);
                return new Models.GitBackupRestoreResult
                {
                    Succeeded = false,
                    BranchName = branchName,
                    BackupRefName = backupRef,
                    Summary = string.IsNullOrWhiteSpace(err) ? $"恢复分支 '{branchName}' 失败。" : err,
                };
            }

            return new Models.GitBackupRestoreResult
            {
                Succeeded = true,
                BranchName = branchName,
                BackupRefName = backupRef,
                RestoredCommitSha = targetSha,
                Summary = $"已从恢复引用重建本地分支 '{branchName}'（指向 {(targetSha.Length > 8 ? targetSha[..8] : targetSha)}）。",
            };
        }

        public static async Task<Models.GitBackupPrunePreview> PreviewExpiredAsync(
            string repoPath,
            int retentionDays = DefaultRetentionDays,
            Models.OperationJournalStore journalStore = null,
            DateTime? utcNowOverride = null,
            CancellationToken cancellationToken = default)
        {
            var all = await ListAsync(repoPath, retentionDays, journalStore, utcNowOverride, cancellationToken)
                .ConfigureAwait(false);
            var expired = all.Where(x => x.IsExpired).ToList();
            var retained = all.Where(x => !x.IsExpired).ToList();

            var summary = expired.Count == 0
                ? $"当前没有超过 {retentionDays} 天的过期恢复引用（保留中：{retained.Count} 项）。"
                : $"共发现 {expired.Count} 项超过 {retentionDays} 天的过期恢复引用（保留中：{retained.Count} 项）。";

            return new Models.GitBackupPrunePreview
            {
                RetentionDays = retentionDays,
                ExpiredBackups = expired,
                RetainedBackups = retained,
                Summary = summary,
            };
        }

        public static async Task<Models.GitBackupPruneResult> PruneExpiredAsync(
            string repoPath,
            IReadOnlyList<Models.GitBackupRefInfo> confirmedExpired,
            Models.ICommandLog log = null,
            CancellationToken cancellationToken = default)
        {
            if (confirmedExpired == null || confirmedExpired.Count == 0)
            {
                return new Models.GitBackupPruneResult
                {
                    Succeeded = true,
                    Summary = "没有需要清理的过期恢复引用。",
                };
            }

            await using var guard = await Models.RepositoryWriteGuard.TryEnterAsync(
                repoPath,
                "清理过期恢复引用",
                checkIndexLock: false,
                cancellationToken: cancellationToken).ConfigureAwait(false);

            if (!guard.Acquired)
            {
                return new Models.GitBackupPruneResult
                {
                    Succeeded = false,
                    Summary = guard.LockDetection.Summary,
                };
            }

            var deleted = new List<string>();
            var failed = new List<string>();

            foreach (var item in confirmedExpired)
            {
                if (string.IsNullOrWhiteSpace(item.FullRefName) ||
                    !item.FullRefName.StartsWith(NamespacePrefix, StringComparison.Ordinal))
                {
                    continue;
                }

                var delErr = await DeleteBackupRefInternalAsync(repoPath, item.FullRefName, item.CommitSha, log, cancellationToken)
                    .ConfigureAwait(false);
                if (string.IsNullOrEmpty(delErr))
                    deleted.Add(item.FullRefName);
                else
                    failed.Add($"{item.FullRefName}: {delErr}");
            }

            var summary = failed.Count == 0
                ? $"已安全清理 {deleted.Count} 项过期恢复引用。"
                : $"已清理 {deleted.Count} 项过期恢复引用，{failed.Count} 项清理失败。";

            return new Models.GitBackupPruneResult
            {
                Succeeded = failed.Count == 0,
                DeletedRefs = deleted,
                FailedRefs = failed,
                Summary = summary,
            };
        }

        /// <summary>
        /// Deletes a single local backup ref by exact expected SHA. Intended for callers already holding RepositoryWriteGuard.
        /// </summary>
        public static async Task<bool> DeleteBackupRefAsync(
            string repoPath,
            string backupRefName,
            string expectedSha,
            Models.ICommandLog log = null,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(backupRefName) ||
                !backupRefName.StartsWith(NamespacePrefix, StringComparison.Ordinal) ||
                string.IsNullOrWhiteSpace(expectedSha))
            {
                return false;
            }

            var err = await DeleteBackupRefInternalAsync(repoPath, backupRefName, expectedSha, log, cancellationToken)
                .ConfigureAwait(false);
            return string.IsNullOrEmpty(err);
        }

        private static async Task<string> DeleteBackupRefInternalAsync(
            string repoPath,
            string backupRefName,
            string expectedSha,
            Models.ICommandLog log,
            CancellationToken cancellationToken)
        {
            var res = await Command.RunGitDirectAsync(
                repoPath,
                cancellationToken,
                "update-ref",
                "-d",
                backupRefName,
                expectedSha).ConfigureAwait(false);

            log?.AppendLine($"$ git update-ref -d {backupRefName} {expectedSha}");
            if (res.IsSuccess)
                return string.Empty;

            return Models.GitConfigSnapshot.RedactText(res.StdErr.Trim());
        }

        private static async Task<DateTime> TryReadReflogTimestampAsync(
            string repoPath,
            string fullRef,
            CancellationToken cancellationToken)
        {
            var res = await Command.RunGitDirectAsync(
                repoPath,
                cancellationToken,
                "log",
                "-g",
                "--date=unix",
                "--format=%gd",
                fullRef).ConfigureAwait(false);

            if (!res.IsSuccess || string.IsNullOrWhiteSpace(res.StdOut))
                return DateTime.MinValue;

            // Reflog walks newest-first, so the last entry records initial creation time.
            var oldestLine = res.StdOut.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
            if (string.IsNullOrEmpty(oldestLine))
                return DateTime.MinValue;

            var open = oldestLine.LastIndexOf("@{", StringComparison.Ordinal);
            var close = open >= 0 ? oldestLine.IndexOf('}', open + 2) : -1;
            if (open >= 0 && close > open + 2 &&
                long.TryParse(oldestLine.AsSpan(open + 2, close - (open + 2)), NumberStyles.Integer, CultureInfo.InvariantCulture, out var unix) &&
                unix > 0)
            {
                return DateTimeOffset.FromUnixTimeSeconds(unix).UtcDateTime;
            }

            return DateTime.MinValue;
        }
    }
}
