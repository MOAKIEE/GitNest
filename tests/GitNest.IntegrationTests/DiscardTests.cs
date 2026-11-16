using SourceGit.Commands;
using SourceGit.Models;

internal static partial class Program
{
    private static async Task RunDiscardTestsAsync(string realGit, string suiteRoot)
    {
        var discardRoot = Path.Combine(suiteRoot, "discard-suite");
        Directory.CreateDirectory(discardRoot);

        var journal = new OperationJournalStore(Path.Combine(discardRoot, "journal.json"));
        var backupStore = new DiscardBackupStore(Path.Combine(discardRoot, "discard-backups"));

        (bool, string) MoveAsideRecycle(string path)
        {
            File.Move(path, Path.Combine(discardRoot, Guid.NewGuid().ToString("N")));
            return (true, string.Empty);
        }

        // 1. Unborn HEAD blocks RestoreToHead baseline
        var unbornRepo = Path.Combine(discardRoot, "unborn-repo");
        await InitRepoAsync(realGit, unbornRepo, "main", "Alice", "alice@example.invalid");
        await File.WriteAllTextAsync(Path.Combine(unbornRepo, "draft.txt"), "unborn draft\n", Utf8NoBom);
        await GitInAsync(realGit, unbornRepo, "add", "draft.txt");
        var unbornChanges = await new QueryLocalChanges(unbornRepo).GetResultAsync();
        var unbornHeadDiscardPre = await DiscardBackupService.InspectDiscardAsync(unbornRepo, unbornChanges, DiscardBaselineKind.RestoreToHead);
        Check(!unbornHeadDiscardPre.CanProceed && unbornHeadDiscardPre.HasUnbornHead,
              "Unborn HEAD repo blocks RestoreToHead discard baseline");

        // 2. Main repo setup
        var repo = Path.Combine(discardRoot, "main-repo");
        await InitRepoAsync(realGit, repo, "main", "Alice", "alice@example.invalid");
        await File.WriteAllTextAsync(Path.Combine(repo, ".gitignore"), "*.ignored\n", Utf8NoBom);
        var baseRel = "app.txt";
        var renameOldRel = "old-name.txt";
        await File.WriteAllTextAsync(Path.Combine(repo, baseRel), "line1\nline2\nline3\nline4\n", Utf8NoBom);
        await File.WriteAllTextAsync(Path.Combine(repo, renameOldRel), "rename-source-v1\n", Utf8NoBom);
        await GitInAsync(realGit, repo, "add", ".gitignore", baseRel, renameOldRel);
        await GitInAsync(realGit, repo, "commit", "-m", "initial commit");

        async Task<DiscardExecutionResult> DiscardAsync(DiscardBaselineKind baseline, IReadOnlyList<Change> targets = null, DiscardPreflight preflight = null, DiscardBackupStore store = null, DateTime? utcNow = null)
            => await DiscardBackupService.ExecuteDiscardAsync(
                repo,
                targets ?? await new QueryLocalChanges(repo).GetResultAsync(),
                baseline,
                backupStore: store ?? backupStore,
                recycleHandler: MoveAsideRecycle,
                journalStore: journal,
                utcNowOverride: utcNow,
                expectedPreflight: preflight);

        async Task<DiscardBackupRestoreResult> RestoreAsync(string id, bool allowOverwrite = false, DateTime? utcNow = null)
            => await DiscardBackupService.RestoreBackupAsync(
                repo,
                id,
                allowOverwriteSubsequentEdits: allowOverwrite,
                backupStore: backupStore,
                recycleHandler: MoveAsideRecycle,
                journalStore: journal,
                utcNowOverride: utcNow);

        // 3. Backup store write failure aborts discard + RestoreWorktreeToIndex preserves staged changes + missing tracked file
        var basePath = Path.Combine(repo, baseRel);
        await File.WriteAllTextAsync(basePath, "line1\nstaged-change\nline3\nline4\n", Utf8NoBom);
        await GitInAsync(realGit, repo, "add", baseRel);
        var expectedIndexContent = await File.ReadAllTextAsync(basePath, Utf8NoBom);
        await File.WriteAllTextAsync(basePath, "line1\nstaged-change\nline3\nunstaged-change\n", Utf8NoBom);
        var beforeDiscardWtContent = await File.ReadAllTextAsync(basePath, Utf8NoBom);

        var t0 = new DateTime(2026, 6, 1, 10, 0, 0, DateTimeKind.Utc);
        var brokenStoreRoot = Path.Combine(discardRoot, "blocked-backup-file");
        await File.WriteAllTextAsync(brokenStoreRoot, "occupy file path", Utf8NoBom);
        var failingStore = new DiscardBackupStore(brokenStoreRoot);
        var chBase = (await new QueryLocalChanges(repo).GetResultAsync()).Single(c => c.Path == baseRel);

        var abortedRes = await DiscardAsync(DiscardBaselineKind.RestoreWorktreeToIndex, [chBase], store: failingStore, utcNow: t0);
        Check(!abortedRes.Success && await File.ReadAllTextAsync(basePath, Utf8NoBom) == beforeDiscardWtContent,
              "Backup store write failure aborts discard cleanly before modifying working tree or index");

        var idxDiscardRes = await DiscardAsync(DiscardBaselineKind.RestoreWorktreeToIndex, [chBase], utcNow: t0);
        Check(idxDiscardRes.Success && !string.IsNullOrEmpty(idxDiscardRes.BackupId) &&
              await File.ReadAllTextAsync(basePath, Utf8NoBom) == expectedIndexContent &&
              await GitInAsync(realGit, repo, "show", $":{baseRel}") == expectedIndexContent,
              "Baseline 1 restores worktree to index while preserving staged changes and writing 30-day recovery backup");

        var idxCheckClean = await DiscardBackupService.InspectBackupRestoreAsync(repo, idxDiscardRes.BackupId, backupStore, t0.AddDays(5));
        Check(idxCheckClean.CanRestore && !idxCheckClean.HasSubsequentModifications,
              "30-day recovery check passes when no subsequent edits happened");

        await File.WriteAllTextAsync(basePath, "line1\nstaged-change\nnewer-edit-after-discard\n", Utf8NoBom);
        var idxCheckBlocked = await DiscardBackupService.InspectBackupRestoreAsync(repo, idxDiscardRes.BackupId, backupStore, t0.AddDays(5));
        var idxRestoreBlocked = await RestoreAsync(idxDiscardRes.BackupId, allowOverwrite: false, utcNow: t0.AddDays(5));
        Check(!idxCheckBlocked.CanRestore && idxCheckBlocked.HasSubsequentModifications &&
              idxCheckBlocked.ConflictingPaths.Contains(baseRel) && !idxRestoreBlocked.Success &&
              (await File.ReadAllTextAsync(basePath, Utf8NoBom)).Contains("newer-edit-after-discard", StringComparison.Ordinal),
              "30-day recovery store refuses silent overwrite when file was edited after discard");

        await GitInAsync(realGit, repo, "restore", "--worktree", "--", baseRel);
        var idxRestoreOk = await RestoreAsync(idxDiscardRes.BackupId, allowOverwrite: false, utcNow: t0.AddDays(5));
        Check(idxRestoreOk.Success && await File.ReadAllTextAsync(basePath, Utf8NoBom) == beforeDiscardWtContent,
              "30-day recovery store restores exact pre-discard worktree bytes");

        await GitInAsync(realGit, repo, "reset", "--hard", "HEAD");
        File.Delete(basePath);
        var deletedWorktreeRes = await DiscardAsync(DiscardBaselineKind.RestoreWorktreeToIndex, utcNow: t0);
        Check(deletedWorktreeRes.Success && await File.ReadAllTextAsync(basePath, Utf8NoBom) == "line1\nline2\nline3\nline4\n",
              "Discard restores a missing tracked file from the index without treating absence as backup failure");
        var deletedWorktreeRestore = await RestoreAsync(deletedWorktreeRes.BackupId, utcNow: t0);
        Check(deletedWorktreeRestore.Success && !File.Exists(basePath) &&
              (await GitInAsync(realGit, repo, "ls-files", baseRel)).Trim() == baseRel,
              "Discard backup restores a worktree deletion while retaining the tracked index entry");
        await GitInAsync(realGit, repo, "restore", baseRel);

        // 4. Baseline 2 (RestoreToHead): staged deletion, rm --cached, index-added file, rename collision & queued snapshot guard
        await GitInAsync(realGit, repo, "rm", baseRel);
        var stagedDelete = await DiscardAsync(DiscardBaselineKind.RestoreToHead, utcNow: t0);
        var stagedRestore = await RestoreAsync(stagedDelete.BackupId, utcNow: t0);
        Check(stagedDelete.Success && stagedRestore.Success && !File.Exists(basePath) &&
              string.IsNullOrWhiteSpace(await GitInAsync(realGit, repo, "ls-files", baseRel)) &&
              (await GitInAsync(realGit, repo, "status", "--porcelain")).Trim() == $"D  {baseRel}",
              "HEAD discard and recovery preserve exact staged deletion rather than reintroducing HEAD index entry");
        await GitInAsync(realGit, repo, "restore", "--source=HEAD", "--staged", "--worktree", baseRel);

        await GitInAsync(realGit, repo, "rm", "--cached", baseRel);
        var cachedBefore = await GitInAsync(realGit, repo, "status", "--porcelain");
        var cachedTargets = (await new QueryLocalChanges(repo).GetResultAsync()).Where(c => c.Index == ChangeState.Deleted).ToList();
        var cachedDiscard = await DiscardAsync(DiscardBaselineKind.RestoreToHead, cachedTargets, utcNow: t0);
        var cachedRestore = await RestoreAsync(cachedDiscard.BackupId, utcNow: t0);
        Check(cachedDiscard.Success && cachedRestore.Success && File.Exists(basePath) &&
              await GitInAsync(realGit, repo, "status", "--porcelain") == cachedBefore,
              "Recovery preserves both cached deletion and untracked working file");
        await GitInAsync(realGit, repo, "add", baseRel);

        await GitInAsync(realGit, repo, "mv", renameOldRel, "renamed-new.txt");
        await File.WriteAllTextAsync(Path.Combine(repo, "staged-new.txt"), "brand new staged content\n", Utf8NoBom);
        await GitInAsync(realGit, repo, "add", "staged-new.txt");

        var renameChange = (await new QueryLocalChanges(repo).GetResultAsync()).Single(c => c.Index == ChangeState.Renamed);
        var renameConfirm = await DiscardBackupService.InspectDiscardAsync(repo, [renameChange], DiscardBaselineKind.RestoreToHead);
        Check(renameConfirm.ConfirmationSnapshot.TargetFiles.Select(t => t.RelativePath).Order().SequenceEqual([renameOldRel, "renamed-new.txt"]),
              "Rename confirmation captures both original and destination paths");

        var oldRenameAbs = Path.Combine(repo, renameOldRel);
        await File.WriteAllTextAsync(oldRenameAbs, "new untracked work\n", Utf8NoBom);
        var collided = await DiscardAsync(DiscardBaselineKind.RestoreToHead, [renameChange], utcNow: t0);
        Check(!collided.Success && await File.ReadAllTextAsync(oldRenameAbs, Utf8NoBom) == "new untracked work\n" &&
              File.Exists(Path.Combine(repo, "renamed-new.txt")),
              "Discarding a rename refuses to overwrite a new untracked file at its original path");
        MoveAsideRecycle(oldRenameAbs);

        var renamedAndAdded = (await new QueryLocalChanges(repo).GetResultAsync())
            .Where(c => c.Path is "renamed-new.txt" or "staged-new.txt").ToList();
        var headDiscardRes = await DiscardAsync(DiscardBaselineKind.RestoreToHead, renamedAndAdded, utcNow: t0);
        Check(headDiscardRes.Success &&
              File.Exists(oldRenameAbs) &&
              !File.Exists(Path.Combine(repo, "renamed-new.txt")) &&
              !File.Exists(Path.Combine(repo, "staged-new.txt")) &&
              string.IsNullOrWhiteSpace(await GitInAsync(realGit, repo, "status", "--porcelain")),
              "Baseline 2 reverses staged renames and unstages+recycles index-added files cleanly");

        var headRestoreRes = await RestoreAsync(headDiscardRes.BackupId, allowOverwrite: true, utcNow: t0.AddDays(1));
        Check(headRestoreRes.Success && headRestoreRes.IndexRestoredCount >= 2 &&
              await File.ReadAllTextAsync(Path.Combine(repo, "staged-new.txt"), Utf8NoBom) == "brand new staged content\n" &&
              await GitInAsync(realGit, repo, "show", ":staged-new.txt") == "brand new staged content\n",
              "Restoring HEAD-baseline backup recovers both working tree bytes and exact Git index blob");

        await GitInAsync(realGit, repo, "reset", "--hard", "HEAD");
        await GitInAsync(realGit, repo, "clean", "-fd");

        // 5. Confirmation snapshot revalidation while queued behind RepositoryWriteGuard + cross-repo rejection + 30d expiry
        await File.WriteAllTextAsync(basePath, "queued content\n", Utf8NoBom);
        var preflightQueued = await DiscardBackupService.InspectDiscardAsync(repo, await new QueryLocalChanges(repo).GetResultAsync(), DiscardBaselineKind.RestoreToHead);
        var queueJournal = new OperationJournalStore(Path.Combine(discardRoot, "queued-journal.json"));
        var (_, commonDir) = await RepositoryWriteGuard.ResolveGitDirectoriesAsync(repo);
        var guard = await RepositoryWriteGuard.TryEnterAsync(repo, "Concurrent write fixture");
        Task<DiscardExecutionResult> pending;
        try
        {
            pending = DiscardBackupService.ExecuteDiscardAsync(
                repo, await new QueryLocalChanges(repo).GetResultAsync(), DiscardBaselineKind.RestoreToHead,
                backupStore: backupStore, journalStore: queueJournal, utcNowOverride: t0, expectedPreflight: preflightQueued);
            var limit = DateTime.UtcNow.AddSeconds(20);
            while (!queueJournal.GetByRepository(commonDir, 10).Any(r => r.Stage == GitOperationStage.Preparing))
            {
                if (DateTime.UtcNow >= limit) throw new TimeoutException("Discard did not reach the write guard.");
                await Task.Delay(20);
            }
            await File.WriteAllTextAsync(basePath, "new work while queued\n", Utf8NoBom);
        }
        finally
        {
            await guard.DisposeAsync();
        }
        var queuedRes = await pending;
        Check(!queuedRes.Success && queuedRes.BlockedBySnapshot && await File.ReadAllTextAsync(basePath, Utf8NoBom) == "new work while queued\n",
              "Discard revalidates confirmation after acquiring the write guard and preserves changes made while queued");
        await GitInAsync(realGit, repo, "restore", baseRel);

        var wrongRepo = Path.Combine(discardRoot, "other-repo");
        await GitAsync(realGit, "init", wrongRepo);
        var wrongRestore = await DiscardBackupService.RestoreBackupAsync(
            wrongRepo, headDiscardRes.BackupId, allowOverwriteSubsequentEdits: true, backupStore: backupStore, journalStore: journal);
        Check(!wrongRestore.Success && !File.Exists(Path.Combine(wrongRepo, "staged-new.txt")),
              "Recovery rejects backups belonging to another repository even with explicit overwrite enabled");

        var expiredCheck = await DiscardBackupService.InspectBackupRestoreAsync(repo, idxDiscardRes.BackupId, backupStore, t0.AddDays(31));
        Check(!expiredCheck.CanRestore && expiredCheck.IsExpired,
              "Recovery store blocks restore after 30-day retention expiration");
        var pruneRes = backupStore.PruneExpired(t0.AddDays(31));
        Check(pruneRes.RemovedCount >= 5 && backupStore.ListForRepository(repo).Count == 0,
              "PruneExpired cleans expired backup records and payload artifacts cleanly");

        // 6. Windows Recycle Bin + locked file refusal + legacy Discard.AllAsync does not touch ignored files
        var ignoredKeep = Path.Combine(repo, "keep-me.ignored");
        await File.WriteAllTextAsync(ignoredKeep, "ignored data", Utf8NoBom);
        await Discard.AllAsync(repo, includeModified: false, includeUntracked: false, includeIgnored: true, log: null);
        Check(File.Exists(ignoredKeep), "Legacy Discard.AllAsync wrapper does not clean ignored files");
        File.Delete(ignoredKeep);

        var recycleFileName = $"gitnest-test-recycle-{Guid.NewGuid():N}.txt";
        var recycleFilePath = Path.Combine(repo, recycleFileName);
        await File.WriteAllTextAsync(recycleFilePath, "tiny recycle test", Utf8NoBom);
        var recycleOkRes = await DiscardBackupService.ExecuteDiscardAsync(
            repo,
            [new Change { Path = recycleFileName, Index = ChangeState.Untracked, WorkTree = ChangeState.Untracked }],
            DiscardBaselineKind.MoveUntrackedToRecycleBin,
            journalStore: journal);
        Check(recycleOkRes.Success && !File.Exists(recycleFilePath) && recycleOkRes.RecycledPaths.Contains(recycleFileName),
              "Windows Recycle Bin safely removes explicit untracked file without File.Delete");

        var lockedName = "locked-untracked.txt";
        var lockedPath = Path.Combine(repo, lockedName);
        await File.WriteAllTextAsync(lockedPath, "held open", Utf8NoBom);
        using (var lockStream = new FileStream(lockedPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var lockedRes = await DiscardBackupService.ExecuteDiscardAsync(
                repo,
                [new Change { Path = lockedName, Index = ChangeState.Untracked, WorkTree = ChangeState.Untracked }],
                DiscardBaselineKind.MoveUntrackedToRecycleBin,
                journalStore: journal);
            Check(!lockedRes.Success && lockedRes.FailedPaths.Contains(lockedName) && File.Exists(lockedPath),
                  "Recycle failure on locked file refuses permanent delete fallback and preserves file on disk");
        }
        File.Delete(lockedPath);
    }
}
