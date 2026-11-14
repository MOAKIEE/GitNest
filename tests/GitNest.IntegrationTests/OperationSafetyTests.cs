using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using SourceGit.Commands;
using SourceGit.Models;

internal static partial class Program
{
    private static async Task RunOperationSafetyTestsAsync(string realGit, string root)
    {
        var suiteRoot = Path.Combine(root, "operation-safety");
        Directory.CreateDirectory(suiteRoot);

        var bareOrigin = Path.Combine(suiteRoot, "origin.git");
        var repoDir = Path.Combine(suiteRoot, "main-repo");
        var peerDir = Path.Combine(suiteRoot, "peer-repo");
        var wtDir = Path.Combine(suiteRoot, "worktree 任务目录");
        var journalFile = Path.Combine(suiteRoot, "journal.json");
        var journal = new OperationJournalStore(journalFile);
        var utf8NoBom = new UTF8Encoding(false);

        await GitAsync(realGit, "init", "--bare", "-b", "main", bareOrigin);
        await GitAsync(realGit, "init", "-b", "main", repoDir);
        await GitAsync(realGit, "-C", repoDir, "config", "--local", "user.name", "Safety Tester");
        await GitAsync(realGit, "-C", repoDir, "config", "--local", "user.email", "safety@example.invalid");

        var alphaPath = Path.Combine(repoDir, "中文 文件 [v1].txt");
        File.WriteAllText(alphaPath, "alpha-v1\n", utf8NoBom);
        await GitAsync(realGit, "-C", repoDir, "add", "--", "中文 文件 [v1].txt");
        await GitAsync(realGit, "-C", repoDir, "commit", "-m", "initial commit");
        var headC1 = (await GitAsync(realGit, "-C", repoDir, "rev-parse", "HEAD")).Trim();

        await GitAsync(realGit, "-C", repoDir, "remote", "add", "origin", bareOrigin);
        await GitAsync(realGit, "-C", repoDir, "push", "-u", "origin", "main");

        // 1. Confirmation snapshot capture + external mutation invalidation checks
        var snap1 = await CaptureOperationSnapshot.CaptureAsync(
            repoDir,
            new OperationSnapshotCaptureOptions
            {
                RefsToWatch = ["refs/heads/main", "refs/remotes/origin/main"],
                TargetPaths = ["中文 文件 [v1].txt"],
                IncludeTargetFileDetails = true,
            });

        Check(snap1.HeadSha == headC1 &&
              snap1.HeadRefName == "refs/heads/main" &&
              !snap1.IsDetachedHead &&
              snap1.InProgressKind == GitInProgressKind.None &&
              snap1.IsWorkingTreeClean &&
              snap1.TargetFiles.Count == 1 &&
              snap1.TargetFiles[0].ExistsOnDisk &&
              snap1.TargetFiles[0].ExistsInIndex &&
              snap1.TargetFiles[0].ExistsInHead &&
              snap1.WatchedRefs["refs/heads/main"] == headC1 &&
              snap1.WatchedRefs["refs/remotes/origin/main"] == headC1,
            "Confirmation snapshot captures repository identity, HEAD, watched refs, and target file hashes");

        // 1a. External modification to target file on disk invalidates RequireSameTargetFiles
        File.WriteAllText(alphaPath, "alpha-modified\n", utf8NoBom);
        var callbackExecuted = false;
        var blockedByFileEdit = await OperationJournalService.ExecuteGuardedWriteAsync<bool>(
            repoDir,
            GitOperationKind.Discard,
            "预核对测试-目标文件变化",
            _ =>
            {
                callbackExecuted = true;
                return Task.FromResult(new OperationActionOutcome<bool> { Payload = true });
            },
            expectedSnapshot: snap1,
            verifyOptions: new OperationSnapshotVerifyOptions
            {
                RequireCleanWorkingTree = false,
                RequireSameTargetFiles = true,
            },
            journalStore: journal);

        Check(!blockedByFileEdit.Succeeded &&
              blockedByFileEdit.BlockedBySnapshot &&
              !callbackExecuted &&
              blockedByFileEdit.VerificationFailure.Reasons.Contains(OperationSnapshotMismatchReason.TargetFileChanged) &&
              blockedByFileEdit.Summary.Contains("仓库状态已变化", StringComparison.Ordinal),
            "External edit to watched target file invalidates confirmation snapshot and blocks mutation");

        // Also test RequireCleanWorkingTree fails cleanly when tree is dirty
        var blockedDirty = await CaptureOperationSnapshot.VerifyAsync(
            snap1,
            new OperationSnapshotVerifyOptions
            {
                RequireCleanWorkingTree = true,
            });
        Check(!blockedDirty.IsValid &&
              blockedDirty.Reasons.Contains(OperationSnapshotMismatchReason.WorkingTreeNoLongerClean),
            "Working tree dirty state invalidates confirmation snapshot requiring clean working tree");

        // Restore clean working tree and test watched remote ref advancement + HEAD advancement
        await GitAsync(realGit, "-C", repoDir, "checkout", "--", "中文 文件 [v1].txt");
        await GitAsync(realGit, "clone", bareOrigin, peerDir);
        await GitAsync(realGit, "-C", peerDir, "config", "--local", "user.name", "Peer");
        await GitAsync(realGit, "-C", peerDir, "config", "--local", "user.email", "peer@example.invalid");
        File.WriteAllText(Path.Combine(peerDir, "peer.txt"), "peer\n", utf8NoBom);
        await GitAsync(realGit, "-C", peerDir, "add", "peer.txt");
        await GitAsync(realGit, "-C", peerDir, "commit", "-m", "peer commit");
        await GitAsync(realGit, "-C", peerDir, "push", "origin", "main");
        await GitAsync(realGit, "-C", repoDir, "fetch", "origin");

        var blockedRefMoved = await CaptureOperationSnapshot.VerifyAsync(
            snap1,
            new OperationSnapshotVerifyOptions { RequireSameWatchedRefs = true });
        Check(!blockedRefMoved.IsValid &&
              blockedRefMoved.Reasons.Contains(OperationSnapshotMismatchReason.WatchedRefChanged),
            "Background fetch advancing watched remote tracking ref invalidates prior confirmation");

        // Re-capture and advance HEAD locally
        var snap2 = await CaptureOperationSnapshot.CaptureAsync(repoDir);
        File.WriteAllText(Path.Combine(repoDir, "c2.txt"), "c2\n", utf8NoBom);
        await GitAsync(realGit, "-C", repoDir, "add", "c2.txt");
        await GitAsync(realGit, "-C", repoDir, "commit", "-m", "local c2");
        var headC2 = (await GitAsync(realGit, "-C", repoDir, "rev-parse", "HEAD")).Trim();

        var blockedHeadMoved = await CaptureOperationSnapshot.VerifyAsync(snap2);
        Check(!blockedHeadMoved.IsValid &&
              blockedHeadMoved.Reasons.Contains(OperationSnapshotMismatchReason.HeadChanged),
            "External local commit advancing HEAD invalidates confirmation snapshot");

        // Respect native Git lock without removing it
        var snap3 = await CaptureOperationSnapshot.CaptureAsync(repoDir);
        var gitLock = Path.Combine(snap3.CanonicalGitDir, "index.lock");
        File.WriteAllText(gitLock, "external-lock", utf8NoBom);
        var lockBlockedResult = await OperationJournalService.ExecuteGuardedWriteAsync<bool>(
            repoDir,
            GitOperationKind.Commit,
            "锁检查测试",
            _ => Task.FromResult(new OperationActionOutcome<bool> { Payload = true }),
            expectedSnapshot: snap3,
            journalStore: journal);
        Check(!lockBlockedResult.Succeeded &&
              lockBlockedResult.BlockedByLock &&
              File.Exists(gitLock),
            "Guarded write stops cleanly when native index.lock exists and preserves lock file");
        File.Delete(gitLock);

        // 2. Concurrent write serialization across main repo and linked worktree + Watcher markers
        await GitAsync(realGit, "-C", repoDir, "worktree", "add", "-b", "wt/concurrent", wtDir, headC1);
        int activeCount = 0;
        int maxConcurrent = 0;

        async Task RunOverlapWrite(string path, string title)
        {
            await OperationJournalService.ExecuteGuardedWriteAsync<bool>(
                path,
                GitOperationKind.Commit,
                title,
                async _ =>
                {
                    var cur = Interlocked.Increment(ref activeCount);
                    if (cur > maxConcurrent)
                        Interlocked.Exchange(ref maxConcurrent, cur);
                    await Task.Delay(80);
                    Interlocked.Decrement(ref activeCount);
                    return new OperationActionOutcome<bool> { Payload = true, Summary = title + " done" };
                },
                journalStore: journal);
        }

        await Task.WhenAll(
            RunOverlapWrite(repoDir, "主仓写操作"),
            RunOverlapWrite(wtDir, "关联工作树写操作"));

        Check(maxConcurrent == 1,
            "Write operations on main repo and linked Unicode worktree serialize strictly via canonical common-dir");

        Check(Watcher.IsOperationStateFile("MERGE_HEAD") &&
              Watcher.IsOperationStateFile("REVERT_HEAD") &&
              Watcher.IsOperationStateFile("CHERRY_PICK_HEAD") &&
              Watcher.IsOperationStateFile("REBASE_HEAD") &&
              Watcher.IsOperationStateFile("rebase-merge/git-rebase-todo") &&
              Watcher.IsOperationStateFile("rebase-apply/applying") &&
              !Watcher.IsOperationStateFile("objects/aa/bb"),
            "Watcher classifies merge, revert, cherry-pick, rebase, and am state markers accurately");

        // 3. Backup references (refs/gitnest/backups/<op-id>), detached HEAD, branch restore & 30-day retention
        await GitAsync(realGit, "-C", repoDir, "branch", "feature/backup-target", headC2);
        var backupOpId = "20261114-test-backup-1";
        var createdBackup = await BackupRefs.CreateAsync(repoDir, backupOpId, "refs/heads/feature/backup-target");
        Check(createdBackup.Succeeded &&
              createdBackup.BackupRefName == BackupRefs.NamespacePrefix + backupOpId &&
              createdBackup.CommitSha == headC2,
            "BackupRefs creates local refs/gitnest/backups/<op-id> pointing cleanly to target commit");

        // Verify QueryBranches and git branch --list never expose refs/gitnest/backups/*
        var uiBranches = await new QueryBranches(repoDir).GetResultAsync();
        var cliBranches = await GitAsync(realGit, "-C", repoDir, "branch", "-a");
        Check(uiBranches.All(b => !b.FullName.Contains("gitnest/backups", StringComparison.Ordinal)) &&
              !cliBranches.Contains("gitnest/backups", StringComparison.Ordinal),
            "Local backup ref namespace stays completely isolated from normal branches and remotes");

        // Delete target branch; then test blocked restore when re-created, vs clean restore when unoccupied
        await GitAsync(realGit, "-C", repoDir, "branch", "-D", "feature/backup-target");
        await GitAsync(realGit, "-C", repoDir, "branch", "feature/backup-target", headC1);
        var blockedRestore = await BackupRefs.RestoreBranchFromBackupAsync(repoDir, "feature/backup-target", createdBackup.BackupRefName);
        var currentTargetAfterBlock = (await GitAsync(realGit, "-C", repoDir, "rev-parse", "refs/heads/feature/backup-target")).Trim();
        Check(!blockedRestore.Succeeded &&
              blockedRestore.BlockedByExistingBranch &&
              currentTargetAfterBlock == headC1,
            "Restoring backup branch refuses to overwrite an already re-created branch of the same name");

        await GitAsync(realGit, "-C", repoDir, "branch", "-D", "feature/backup-target");
        var cleanRestore = await BackupRefs.RestoreBranchFromBackupAsync(repoDir, "feature/backup-target", createdBackup.BackupRefName);
        var restoredSha = (await GitAsync(realGit, "-C", repoDir, "rev-parse", "refs/heads/feature/backup-target")).Trim();
        Check(cleanRestore.Succeeded && restoredSha == headC2,
            "Restoring backup branch recreates missing branch accurately at backed-up commit SHA");

        // Detached HEAD backup (using an old commit date via GIT_COMMITTER_DATE to ensure 30-day retention checks creation age, not commit age)
        var oldCommitEnvDir = Path.Combine(suiteRoot, "old-commit-repo");
        await GitAsync(realGit, "init", "-b", "main", oldCommitEnvDir);
        await GitAsync(realGit, "-C", oldCommitEnvDir, "config", "--local", "user.name", "Old Author");
        await GitAsync(realGit, "-C", oldCommitEnvDir, "config", "--local", "user.email", "old@example.invalid");
        File.WriteAllText(Path.Combine(oldCommitEnvDir, "old.txt"), "old\n", utf8NoBom);
        await GitAsync(realGit, "-C", oldCommitEnvDir, "add", "old.txt");
        var oldCommitStartInfo = new System.Diagnostics.ProcessStartInfo(realGit)
        {
            WorkingDirectory = oldCommitEnvDir,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        oldCommitStartInfo.Environment["GIT_AUTHOR_DATE"] = "2025-01-01T00:00:00Z";
        oldCommitStartInfo.Environment["GIT_COMMITTER_DATE"] = "2025-01-01T00:00:00Z";
        oldCommitStartInfo.ArgumentList.Add("commit");
        oldCommitStartInfo.ArgumentList.Add("-m");
        oldCommitStartInfo.ArgumentList.Add("old commit date");
        using (var proc = System.Diagnostics.Process.Start(oldCommitStartInfo)!)
        {
            var err = await proc.StandardError.ReadToEndAsync();
            await proc.WaitForExitAsync();
            if (proc.ExitCode != 0)
                throw new InvalidOperationException("Old commit fixture failed: " + err);
        }
        var oldCommitSha = (await GitAsync(realGit, "-C", oldCommitEnvDir, "rev-parse", "HEAD")).Trim();
        await GitAsync(realGit, "-C", oldCommitEnvDir, "checkout", "--detach", oldCommitSha);
        var detachedBackup = await BackupRefs.CreateAsync(oldCommitEnvDir, "20261114-detached-1", "HEAD");

        var emptyJournal = new OperationJournalStore(Path.Combine(suiteRoot, "empty-journal.json"));
        var freshPreview = await BackupRefs.PreviewExpiredAsync(oldCommitEnvDir, 30, emptyJournal);
        Check(detachedBackup.Succeeded &&
              freshPreview.ExpiredBackups.Count == 0 &&
              freshPreview.RetainedBackups.Count == 1 &&
              freshPreview.RetainedBackups[0].CommitSha == oldCommitSha,
            "Freshly created backup ref for an old detached commit is retained (age uses backup creation time, not commit date)");

        var expiredPreview = await BackupRefs.PreviewExpiredAsync(
            oldCommitEnvDir,
            30,
            journal,
            utcNowOverride: DateTime.UtcNow.AddDays(31));
        Check(expiredPreview.ExpiredBackups.Count == 1 &&
              expiredPreview.ExpiredBackups[0].FullRefName == detachedBackup.BackupRefName,
            "Backup prune preview detects refs older than 30 days accurately");

        var pruneRes = await BackupRefs.PruneExpiredAsync(oldCommitEnvDir, expiredPreview.ExpiredBackups);
        var existsAfterPrune = await BackupRefs.ExistsAsync(oldCommitEnvDir, detachedBackup.BackupRefName);
        Check(pruneRes.Succeeded && pruneRes.DeletedRefs.Count == 1 && !existsAfterPrune,
            "PruneExpiredAsync deletes confirmed expired local backup refs cleanly");

        // 4. Secret redaction in OperationJournalStore & Crash/Restart Reconciliation
        const string secretToken = "ghp_SecretTokenValue987654";
        var (_, repoCommonDir) = await RepositoryWriteGuard.ResolveGitDirectoriesAsync(repoDir);
        OperationJournalService.RecordCompletedOperation(
            repoDir,
            repoCommonDir,
            GitOperationKind.Push,
            "推送 https://alice:" + secretToken + "@git.example.com/org/repo.git",
            GitOperationStage.Failed,
            "推送失败：https://alice:" + secretToken + "@git.example.com/org/repo.git 被拒绝",
            commandLogText: "$ git push https://alice:" + secretToken + "@git.example.com/org/repo.git main",
            branchName: "main",
            remoteName: "origin",
            remoteBranchName: "main",
            originalTipSha: headC2,
            journalStore: journal);

        var reloadedStore = new OperationJournalStore(journalFile);
        var diskRaw = File.ReadAllText(journalFile, Encoding.UTF8);
        var loadedRecords = reloadedStore.GetByRepository(repoCommonDir);
        Check(!diskRaw.Contains(secretToken, StringComparison.Ordinal) &&
              loadedRecords.Any(r => r.CommandSummary.Contains("https://***@git.example.com/org/repo.git", StringComparison.Ordinal)),
            "OperationJournalStore redacts embedded URL secrets on both memory and disk persistence");

        // Simulate crash leaving Executing record while branch was subsequently moved externally + conflict active
        File.WriteAllText(Path.Combine(repoDir, "c3.txt"), "c3\n", utf8NoBom);
        await GitAsync(realGit, "-C", repoDir, "add", "c3.txt");
        await GitAsync(realGit, "-C", repoDir, "commit", "-m", "external c3");
        var headC3 = (await GitAsync(realGit, "-C", repoDir, "rev-parse", "HEAD")).Trim();
        File.WriteAllText(Path.Combine(snap3.CanonicalGitDir, "MERGE_HEAD"), headC1 + "\n", utf8NoBom);

        var crashedRecord = new GitOperationRecord
        {
            OperationId = "20261114-crash-sim-1",
            RepositoryCommonDir = repoCommonDir,
            WorktreePath = repoDir,
            Kind = GitOperationKind.Reset,
            Title = "模拟崩前正在进行的撤回提交",
            BranchName = "main",
            OriginalTipSha = headC2,
            TargetTipSha = headC1,
            BackupRefName = createdBackup.BackupRefName,
            Stage = GitOperationStage.Executing,
            StepSummary = "正在执行本地 reset",
        };
        reloadedStore.AppendOrUpdate(crashedRecord);

        var reconcile = await OperationJournalService.ReconcileOnOpenAsync(repoDir, reloadedStore);
        var headAfterReconcile = (await GitAsync(realGit, "-C", repoDir, "rev-parse", "HEAD")).Trim();
        var backupStillThere = await BackupRefs.ExistsAsync(repoDir, createdBackup.BackupRefName);
        Check(reconcile.HasPendingAttention &&
              reconcile.CurrentInProgressKind == GitInProgressKind.Merge &&
              reconcile.UnfinishedRecords.Any(r =>
                  r.OperationId == "20261114-crash-sim-1" &&
                  r.IsUnfinishedOnStartup &&
                  r.Stage == GitOperationStage.PendingVerification &&
                  r.ReconciliationNote.Contains("后续外部新工作", StringComparison.Ordinal)) &&
              headAfterReconcile == headC3 &&
              backupStillThere,
            "Startup reconciliation inspects unfinished Executing records, detects external follow-up work and active conflict without auto-rolling back");

        File.Delete(Path.Combine(snap3.CanonicalGitDir, "MERGE_HEAD"));

        // 5. Verify Repository initial LastFetchDisplayTime does not fabricate current time for unfetched repo
        var vmRepo = new SourceGit.ViewModels.Repository(false, oldCommitEnvDir, Path.Combine(oldCommitEnvDir, ".git"));
        Check(string.IsNullOrEmpty(vmRepo.LastFetchDisplayTime),
            "Repository reports empty LastFetchDisplayTime when never fetched rather than fabricating open time");

        // 6. Verify GitSyncService records journal entries only when explicit journalStore is provided
        var countBeforeNullCommit = reloadedStore.GetByRepository(repoCommonDir).Count;
        File.WriteAllText(Path.Combine(repoDir, "sync-silent.txt"), "silent\n", utf8NoBom);
        await GitAsync(realGit, "-C", repoDir, "add", "sync-silent.txt");
        var snapBeforeSilent = await QueryStagedSnapshot.CaptureAsync(repoDir);
        var silentCommitRes = await GitSyncService.CommitAsync(
            repoDir,
            new CommitExecutionOptions
            {
                Message = "commit without explicit journal store",
                ExpectedSnapshot = snapBeforeSilent,
            });
        var countAfterNullCommit = reloadedStore.GetByRepository(repoCommonDir).Count;
        Check(silentCommitRes.Succeeded && countAfterNullCommit == countBeforeNullCommit,
            "GitSyncService leaves global/custom OperationJournalStore untouched when journalStore parameter is omitted");

        File.WriteAllText(Path.Combine(repoDir, "sync-recorded.txt"), "recorded\n", utf8NoBom);
        await GitAsync(realGit, "-C", repoDir, "add", "sync-recorded.txt");
        var snapBeforeRecorded = await QueryStagedSnapshot.CaptureAsync(repoDir);
        var recordedCommitRes = await GitSyncService.CommitAsync(
            repoDir,
            new CommitExecutionOptions
            {
                Message = "commit with explicit journal store",
                ExpectedSnapshot = snapBeforeRecorded,
            },
            journalStore: reloadedStore);
        var entriesAfterRecorded = reloadedStore.GetByRepository(repoCommonDir);
        Check(recordedCommitRes.Succeeded &&
              entriesAfterRecorded.Count == countBeforeNullCommit + 1 &&
              entriesAfterRecorded[0].Kind == GitOperationKind.Commit &&
              entriesAfterRecorded[0].Stage == GitOperationStage.Succeeded &&
              entriesAfterRecorded[0].OriginalTipSha == snapBeforeRecorded.HeadOid &&
              entriesAfterRecorded[0].TargetTipSha == recordedCommitRes.CommitSha,
            "GitSyncService records Commit operation tips accurately into injected OperationJournalStore");
    }
}
