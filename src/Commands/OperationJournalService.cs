using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace SourceGit.Commands
{
    public sealed class OperationExecutionContext
    {
        public string OperationId { get; init; } = string.Empty;
        public string RepoPath { get; init; } = string.Empty;
        public string CanonicalGitDir { get; init; } = string.Empty;
        public string CanonicalCommonDir { get; init; } = string.Empty;
        public Models.ICommandLog Log { get; init; }
        public Models.GitOperationRecord Record { get; init; }
    }

    public sealed class OperationActionOutcome<T>
    {
        public Models.GitOperationStage Stage { get; init; } = Models.GitOperationStage.Succeeded;
        public string Summary { get; init; } = string.Empty;
        public string TargetTipSha { get; init; } = string.Empty;
        public string BackupRefName { get; init; } = string.Empty;
        public string DiagnosticDetail { get; init; } = string.Empty;
        public T Payload { get; init; }
    }

    /// <summary>
    /// Coordinates common-dir write serialization, confirmation snapshot verification, secret-redacted
    /// persistence in OperationJournalStore, and startup reconciliation against real Git state.
    /// </summary>
    public static class OperationJournalService
    {
        public static async Task<Models.OperationExecutionResult<T>> ExecuteGuardedWriteAsync<T>(
            string repoPath,
            Models.GitOperationKind kind,
            string title,
            Func<OperationExecutionContext, Task<OperationActionOutcome<T>>> action,
            Models.OperationConfirmationSnapshot expectedSnapshot = null,
            Models.OperationSnapshotVerifyOptions verifyOptions = null,
            string branchName = null,
            string remoteName = null,
            string remoteBranchName = null,
            string originalTipSha = null,
            string expectedTargetTipSha = null,
            string expectedRemoteTipSha = null,
            bool checkIndexLock = true,
            string branchLockToCheck = null,
            Models.OperationJournalStore journalStore = null,
            Models.ICommandLog outerLog = null,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(action);

            var store = journalStore ?? Models.OperationJournalStore.Instance;
            var (_, commonDir) = await Models.RepositoryWriteGuard.ResolveGitDirectoriesAsync(repoPath, cancellationToken).ConfigureAwait(false);
            var operationId = Models.OperationJournalStore.GenerateOperationId();
            var capturingLog = new CapturingCommandLog(outerLog);

            var record = new Models.GitOperationRecord
            {
                OperationId = operationId,
                RepositoryCommonDir = commonDir,
                WorktreePath = repoPath,
                Kind = kind,
                Title = title ?? kind.ToString(),
                BranchName = branchName ?? string.Empty,
                RemoteName = remoteName ?? string.Empty,
                RemoteBranchName = remoteBranchName ?? string.Empty,
                OriginalTipSha = originalTipSha ?? expectedSnapshot?.HeadSha ?? string.Empty,
                TargetTipSha = expectedTargetTipSha ?? string.Empty,
                ExpectedRemoteTipSha = expectedRemoteTipSha ?? string.Empty,
                Stage = Models.GitOperationStage.Preparing,
                StepSummary = "正在获取仓库写锁并核对当前状态...",
            };
            TrySaveRecord(store, record);

            await using var guard = await Models.RepositoryWriteGuard.TryEnterAsync(
                repoPath,
                title ?? kind.ToString(),
                checkIndexLock,
                branchLockToCheck,
                cancellationToken).ConfigureAwait(false);

            if (!guard.Acquired)
            {
                record.Stage = Models.GitOperationStage.Failed;
                record.StepSummary = guard.LockDetection.Summary;
                record.DiagnosticDetail = string.Join(Environment.NewLine, guard.LockDetection.BlockingLockFiles);
                record.CompletedAtUtc = DateTime.UtcNow;
                TrySaveRecord(store, record);

                return new Models.OperationExecutionResult<T>
                {
                    Succeeded = false,
                    BlockedByLock = true,
                    Record = record,
                    LockDetection = guard.LockDetection,
                    Summary = guard.LockDetection.Summary,
                };
            }

            if (expectedSnapshot != null)
            {
                var verify = await CaptureOperationSnapshot.VerifyAsync(
                    expectedSnapshot,
                    verifyOptions,
                    cancellationToken).ConfigureAwait(false);
                if (!verify.IsValid)
                {
                    record.Stage = Models.GitOperationStage.Failed;
                    record.StepSummary = verify.Summary;
                    record.DiagnosticDetail = string.Join(Environment.NewLine, verify.Details);
                    record.CompletedAtUtc = DateTime.UtcNow;
                    TrySaveRecord(store, record);

                    return new Models.OperationExecutionResult<T>
                    {
                        Succeeded = false,
                        BlockedBySnapshot = true,
                        BlockedByLock = verify.LockDetection.IsLocked,
                        Record = record,
                        VerificationFailure = verify,
                        LockDetection = verify.LockDetection,
                        Summary = verify.Summary,
                    };
                }
            }

            record.Stage = Models.GitOperationStage.Executing;
            record.StepSummary = $"正在执行：{record.Title}";
            TrySaveRecord(store, record);

            try
            {
                var ctx = new OperationExecutionContext
                {
                    OperationId = operationId,
                    RepoPath = repoPath,
                    CanonicalGitDir = expectedSnapshot?.CanonicalGitDir ?? string.Empty,
                    CanonicalCommonDir = guard.Lease.CanonicalCommonDir,
                    Log = capturingLog,
                    Record = record,
                };

                var outcome = await action(ctx).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(outcome.TargetTipSha))
                    record.TargetTipSha = outcome.TargetTipSha;
                if (!string.IsNullOrWhiteSpace(outcome.BackupRefName))
                    record.BackupRefName = outcome.BackupRefName;

                record.Stage = outcome.Stage;
                record.StepSummary = outcome.Summary ?? string.Empty;
                record.CommandSummary = capturingLog.GetCombinedText();
                record.DiagnosticDetail = outcome.DiagnosticDetail ?? string.Empty;

                if (!Models.OperationJournalStore.RequiresAttentionStage(outcome.Stage))
                {
                    record.CompletedAtUtc = DateTime.UtcNow;
                    record.IsUnfinishedOnStartup = false;
                }

                TrySaveRecord(store, record);

                return new Models.OperationExecutionResult<T>
                {
                    Succeeded = outcome.Stage == Models.GitOperationStage.Succeeded,
                    Record = record,
                    Payload = outcome.Payload,
                    Summary = record.StepSummary,
                };
            }
            catch (OperationCanceledException)
            {
                record.Stage = Models.GitOperationStage.CanceledUncertain;
                record.StepSummary = "操作已取消或中断，实际结果待结合本地与远端 Git 状态核实。";
                record.CommandSummary = capturingLog.GetCombinedText();
                TrySaveRecord(store, record);
                throw;
            }
            catch (Exception ex)
            {
                record.Stage = Models.GitOperationStage.Failed;
                record.StepSummary = Models.GitConfigSnapshot.RedactText(ex.Message);
                record.CommandSummary = capturingLog.GetCombinedText();
                record.DiagnosticDetail = Models.GitConfigSnapshot.RedactText(ex.ToString());
                record.CompletedAtUtc = DateTime.UtcNow;
                TrySaveRecord(store, record);

                return new Models.OperationExecutionResult<T>
                {
                    Succeeded = false,
                    Record = record,
                    Summary = record.StepSummary,
                };
            }
        }

        public static void RecordCompletedOperation(
            string repoPath,
            string commonDir,
            Models.GitOperationKind kind,
            string title,
            Models.GitOperationStage stage,
            string summary,
            string commandLogText = null,
            string branchName = null,
            string remoteName = null,
            string remoteBranchName = null,
            string originalTipSha = null,
            string targetTipSha = null,
            string expectedRemoteTipSha = null,
            string backupRefName = null,
            Models.OperationJournalStore journalStore = null,
            string diagnosticDetail = null)
        {
            var store = journalStore ?? Models.OperationJournalStore.Instance;
            var record = new Models.GitOperationRecord
            {
                OperationId = Models.OperationJournalStore.GenerateOperationId(),
                RepositoryCommonDir = commonDir ?? string.Empty,
                WorktreePath = repoPath ?? string.Empty,
                Kind = kind,
                Title = title ?? kind.ToString(),
                BranchName = branchName ?? string.Empty,
                RemoteName = remoteName ?? string.Empty,
                RemoteBranchName = remoteBranchName ?? string.Empty,
                OriginalTipSha = originalTipSha ?? string.Empty,
                TargetTipSha = targetTipSha ?? string.Empty,
                ExpectedRemoteTipSha = expectedRemoteTipSha ?? string.Empty,
                BackupRefName = backupRefName ?? string.Empty,
                Stage = stage,
                StepSummary = summary ?? string.Empty,
                CommandSummary = commandLogText ?? string.Empty,
                DiagnosticDetail = diagnosticDetail ?? string.Empty,
                CompletedAtUtc = Models.OperationJournalStore.RequiresAttentionStage(stage) ? null : DateTime.UtcNow,
            };

            TrySaveRecord(store, record);
        }

        /// <summary>
        /// Inspects any unfinished or uncertain journal records on repository open without mutating
        /// branches, index, or working tree, annotating each record with real Git disk truth.
        /// </summary>
        public static async Task<Models.OperationReconciliationReport> ReconcileOnOpenAsync(
            string repoPath,
            Models.OperationJournalStore journalStore = null,
            CancellationToken cancellationToken = default)
        {
            var store = journalStore ?? Models.OperationJournalStore.Instance;
            var (gitDir, commonDir) = await Models.RepositoryWriteGuard.ResolveGitDirectoriesAsync(repoPath, cancellationToken)
                .ConfigureAwait(false);

            var flagged = store.MarkUnfinishedFromPreviousProcess(commonDir);
            var headSha = await ReadHeadShaAsync(repoPath, cancellationToken).ConfigureAwait(false);
            var branchName = await ReadCurrentBranchNameAsync(repoPath, cancellationToken).ConfigureAwait(false);
            var inProgress = CaptureOperationSnapshot.DetectInProgressKind(gitDir);
            var backups = await BackupRefs.ListAsync(repoPath, BackupRefs.DefaultRetentionDays, store, null, cancellationToken)
                .ConfigureAwait(false);

            var reconciledList = new List<Models.GitOperationRecord>();
            foreach (var rec in flagged)
            {
                var updated = await ReconcileSingleRecordInternalAsync(
                    repoPath,
                    rec,
                    headSha,
                    inProgress,
                    store,
                    markResolved: false,
                    cancellationToken: cancellationToken).ConfigureAwait(false);
                reconciledList.Add(updated);
            }

            var activeBackupRefs = backups.Select(b => b.FullRefName).ToList();
            var summary = reconciledList.Count == 0
                ? string.Empty
                : $"检测到 {reconciledList.Count} 条上次未完成或待核实的操作记录（当前处于 {branchName} @ {ShortSha(headSha)}）。请先查看实际仓库状态并决定恢复或确认完成。";

            return new Models.OperationReconciliationReport
            {
                RepositoryCommonDir = commonDir,
                CurrentHeadSha = headSha,
                CurrentBranchName = branchName,
                CurrentInProgressKind = inProgress,
                UnfinishedRecords = reconciledList,
                ActiveBackupRefs = activeBackupRefs,
                Summary = summary,
            };
        }

        public static async Task<Models.GitOperationRecord> VerifyRecordStateAsync(
            string repoPath,
            string operationId,
            Models.OperationJournalStore journalStore = null,
            bool acknowledgeResolved = false,
            CancellationToken cancellationToken = default)
        {
            var store = journalStore ?? Models.OperationJournalStore.Instance;
            var rec = store.TryGet(operationId);
            if (rec == null)
                return null;

            var (gitDir, _) = await Models.RepositoryWriteGuard.ResolveGitDirectoriesAsync(repoPath, cancellationToken).ConfigureAwait(false);
            var headSha = await ReadHeadShaAsync(repoPath, cancellationToken).ConfigureAwait(false);
            var inProgress = CaptureOperationSnapshot.DetectInProgressKind(gitDir);

            return await ReconcileSingleRecordInternalAsync(
                repoPath,
                rec,
                headSha,
                inProgress,
                store,
                acknowledgeResolved,
                cancellationToken).ConfigureAwait(false);
        }

        private static async Task<Models.GitOperationRecord> ReconcileSingleRecordInternalAsync(
            string repoPath,
            Models.GitOperationRecord record,
            string headSha,
            Models.GitInProgressKind inProgress,
            Models.OperationJournalStore store,
            bool markResolved,
            CancellationToken cancellationToken)
        {
            var branchTip = string.Empty;
            if (!string.IsNullOrWhiteSpace(record.BranchName))
                branchTip = await ResolveRefAsync(repoPath, "refs/heads/" + record.BranchName, cancellationToken).ConfigureAwait(false);

            var remoteTip = string.Empty;
            if (!string.IsNullOrWhiteSpace(record.RemoteName) && !string.IsNullOrWhiteSpace(record.RemoteBranchName))
                remoteTip = await ResolveRefAsync(repoPath, $"refs/remotes/{record.RemoteName}/{record.RemoteBranchName}", cancellationToken)
                    .ConfigureAwait(false);

            var hasBackup = !string.IsNullOrWhiteSpace(record.BackupRefName) &&
                            await BackupRefs.ExistsAsync(repoPath, record.BackupRefName, cancellationToken).ConfigureAwait(false);

            var notes = new List<string>
            {
                $"实测 HEAD：{ShortSha(headSha)}；仓库进行中状态：{(inProgress == Models.GitInProgressKind.None ? "无" : inProgress.ToString())}",
            };

            if (!string.IsNullOrWhiteSpace(record.BranchName))
            {
                var branchDesc = string.IsNullOrEmpty(branchTip) ? "当前不存在" : ShortSha(branchTip);
                notes.Add($"目标本地分支 {record.BranchName}：{branchDesc}");
                if (!string.IsNullOrEmpty(branchTip) &&
                    !string.IsNullOrEmpty(record.TargetTipSha) &&
                    !string.Equals(branchTip, record.TargetTipSha, StringComparison.Ordinal))
                {
                    if (string.Equals(branchTip, record.OriginalTipSha, StringComparison.Ordinal))
                        notes.Add("提示：分支仍停留在原提交，操作可能尚未改写分支。");
                    else
                        notes.Add("注意：分支现已指向其他提交，可能已有后续外部新工作，切勿盲目自动回滚。");
                }
            }

            if (!string.IsNullOrWhiteSpace(record.RemoteName) && !string.IsNullOrWhiteSpace(record.RemoteBranchName))
            {
                var remoteDesc = string.IsNullOrEmpty(remoteTip) ? "跟踪引用不存在" : ShortSha(remoteTip);
                notes.Add($"远端跟踪 {record.RemoteName}/{record.RemoteBranchName}：{remoteDesc}");
                if (record.Kind == Models.GitOperationKind.RewriteRemoteHistory)
                {
                    if (!string.IsNullOrEmpty(record.TargetTipSha) &&
                        string.Equals(remoteTip, record.TargetTipSha, StringComparison.Ordinal))
                    {
                        notes.Add("本地远端跟踪缓存已指向目标提交；如需确认服务器最终状态可联网核对。");
                    }
                    else if (!string.IsNullOrEmpty(record.ExpectedRemoteTipSha) &&
                             string.Equals(remoteTip, record.ExpectedRemoteTipSha, StringComparison.Ordinal))
                    {
                        notes.Add("本地远端跟踪缓存仍停留在改写前提交（请联网核对服务器是否已更新）。");
                    }
                    else
                    {
                        notes.Add("请勿盲目重试改写，请先联网核对远端分支当前真实提交。");
                    }
                }
            }

            if (record.Kind is Models.GitOperationKind.Reset or Models.GitOperationKind.RewriteRemoteHistory &&
                !string.IsNullOrEmpty(branchTip) &&
                !string.IsNullOrEmpty(record.TargetTipSha) &&
                string.Equals(branchTip, record.TargetTipSha, StringComparison.Ordinal))
            {
                notes.Add($"本地分支已停留在撤回目标提交 {ShortSha(record.TargetTipSha)}。");
            }

            if (!string.IsNullOrWhiteSpace(record.BackupRefName))
            {
                notes.Add(hasBackup
                    ? $"本地恢复引用可用：{record.BackupRefName}"
                    : $"原记录的恢复引用 {record.BackupRefName} 已不存在。");
            }

            var cleanedNotes = notes
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .Select(n => n.Trim().TrimEnd('。', '；'))
                .Where(n => !string.IsNullOrEmpty(n))
                .ToList();
            record.ReconciliationNote = cleanedNotes.Count == 0
                ? string.Empty
                : string.Join("；", cleanedNotes) + "。";
            if (markResolved)
            {
                record.IsUnfinishedOnStartup = false;
                record.IsAcknowledged = true;
                if (record.Stage is Models.GitOperationStage.PendingVerification or Models.GitOperationStage.CanceledUncertain)
                {
                    record.Stage = !string.IsNullOrEmpty(record.TargetTipSha) && string.Equals(branchTip, record.TargetTipSha, StringComparison.Ordinal)
                        ? Models.GitOperationStage.Succeeded
                        : Models.GitOperationStage.PartialSuccess;
                }
                record.CompletedAtUtc ??= DateTime.UtcNow;
            }

            TrySaveRecord(store, record);
            return record;
        }

        private static async Task<string> ReadHeadShaAsync(string repoPath, CancellationToken cancellationToken)
        {
            var res = await Command.RunGitDirectAsync(repoPath, cancellationToken, "rev-parse", "--verify", "-q", "HEAD")
                .ConfigureAwait(false);
            return res.IsSuccess ? res.StdOut.Trim() : string.Empty;
        }

        private static async Task<string> ReadCurrentBranchNameAsync(string repoPath, CancellationToken cancellationToken)
        {
            var sym = await Command.RunGitDirectAsync(repoPath, cancellationToken, "symbolic-ref", "--short", "-q", "HEAD")
                .ConfigureAwait(false);
            return sym.IsSuccess ? sym.StdOut.Trim() : "detached HEAD";
        }

        private static async Task<string> ResolveRefAsync(string repoPath, string fullRef, CancellationToken cancellationToken)
        {
            var res = await Command.RunGitDirectAsync(repoPath, cancellationToken, "show-ref", "--verify", "--", fullRef)
                .ConfigureAwait(false);
            if (!res.IsSuccess || string.IsNullOrWhiteSpace(res.StdOut))
                return string.Empty;

            var idx = res.StdOut.Trim().IndexOf(' ');
            return idx > 0 ? res.StdOut.Trim().Substring(0, idx) : string.Empty;
        }

        private static string ShortSha(string sha)
        {
            if (string.IsNullOrWhiteSpace(sha))
                return "(无)";

            return sha.Length > 8 ? sha[..8] : sha;
        }

        internal static void TrySaveRecord(Models.OperationJournalStore store, Models.GitOperationRecord record)
        {
            if (store == null || record == null)
                return;

            try
            {
                store.AppendOrUpdate(record);
            }
            catch
            {
                // Preserve primary Git operation resilience if journal storage is unreadable.
            }
        }
    }
}
