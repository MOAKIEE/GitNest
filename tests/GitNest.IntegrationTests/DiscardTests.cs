using System.Diagnostics;
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

        // 7. FileRemovalMode.Direct: backup-first deletion, restore, unborn HEAD, directory expansion, junctions, IndexAddedOnly, and path safety
        var directRoot = Path.Combine(discardRoot, "direct-suite");
        var directStore = new DiscardBackupStore(Path.Combine(directRoot, "backups"));
        var directJournal = new OperationJournalStore(Path.Combine(directRoot, "journal.json"));

        var missingIndexCapture = directStore.Capture(
            repo, Path.Combine(repo, ".git"), string.Empty,
            DiscardBaselineKind.RestoreToHead, "Missing index payload",
            [new DiscardBackupCaptureInput { RelativePath = "missing-index.txt", ExistsInIndex = true }],
            utcNowOverride: t0);
        Check(!missingIndexCapture.Success && directStore.TryGet(missingIndexCapture.BackupId) == null,
              "Backup capture rejects missing index bytes before accepting a recoverable entry");
        Check(!directStore.VerifyPayloadIntegrity(
                  new DiscardBackupEntry
                  {
                      Files = [new DiscardBackupFileEntry { RelativePath = "missing-index.txt", ExistsInIndex = true }],
                  }, out var missingIndexPaths) && missingIndexPaths.SequenceEqual(["missing-index.txt"]),
              "Backup integrity validation rejects index entries with missing payload paths");

        // 7a. Untracked single & multi Direct Delete saves 30d backup, never calls recycleHandler, and restores identically
        var u1 = "direct-one.txt";
        var u2 = "新增 直接删除 文档.txt";
        await File.WriteAllTextAsync(Path.Combine(repo, u1), "direct content 1\n", Utf8NoBom);
        await File.WriteAllTextAsync(Path.Combine(repo, u2), "direct content 2 中文\n", Utf8NoBom);
        var recycleCalls = 0;
        var directRes = await DiscardBackupService.ExecuteDiscardAsync(
            repo,
            [
                new Change { Path = u1, Index = ChangeState.Untracked, WorkTree = ChangeState.Untracked },
                new Change { Path = u2, Index = ChangeState.Untracked, WorkTree = ChangeState.Untracked },
            ],
            DiscardBaselineKind.MoveUntrackedToRecycleBin,
            backupStore: directStore,
            recycleHandler: _ => { recycleCalls++; return (true, string.Empty); },
            journalStore: directJournal,
            utcNowOverride: t0,
            removalMode: FileRemovalMode.Direct);
        Check(directRes.Success &&
              recycleCalls == 0 &&
              directRes.RemovalMode == FileRemovalMode.Direct &&
              !File.Exists(Path.Combine(repo, u1)) &&
              !File.Exists(Path.Combine(repo, u2)) &&
              directRes.DirectDeletedPaths.Contains(u1) &&
              directRes.DirectDeletedPaths.Contains(u2) &&
              directRes.RecycledPaths.Count == 0 &&
              !string.IsNullOrEmpty(directRes.BackupId),
              "Direct delete removes untracked files after backup without invoking Recycle Bin or double counting");

        var directBackupEntry = directStore.TryGet(directRes.BackupId);
        Check(directBackupEntry is { RemovalMode: FileRemovalMode.Direct, Baseline: DiscardBaselineKind.MoveUntrackedToRecycleBin } &&
              directBackupEntry.Files.Count == 2,
              "Untracked direct delete writes 30-day backup entry recording RemovalMode.Direct");

        var directRestore = await DiscardBackupService.RestoreBackupAsync(
            repo,
            directRes.BackupId,
            backupStore: directStore,
            journalStore: directJournal,
            utcNowOverride: t0.AddDays(1));
        Check(directRestore.Success &&
              await File.ReadAllTextAsync(Path.Combine(repo, u1), Utf8NoBom) == "direct content 1\n" &&
              await File.ReadAllTextAsync(Path.Combine(repo, u2), Utf8NoBom) == "direct content 2 中文\n",
              "Untracked direct-deleted files restore cleanly from 30-day local backup");
        File.Delete(Path.Combine(repo, u1));
        File.Delete(Path.Combine(repo, u2));

        // 7b. RecycleBin failure never falls back to Direct Delete
        var failRecycleFile = "recycle-fail-keep.txt";
        await File.WriteAllTextAsync(Path.Combine(repo, failRecycleFile), "must stay on recycle error", Utf8NoBom);
        var recycleFailCalls = 0;
        var directFallbackCalls = 0;
        var recycleFailRes = await DiscardBackupService.ExecuteDiscardAsync(
            repo,
            [new Change { Path = failRecycleFile, Index = ChangeState.Untracked, WorkTree = ChangeState.Untracked }],
            DiscardBaselineKind.MoveUntrackedToRecycleBin,
            backupStore: directStore,
            recycleHandler: _ => { recycleFailCalls++; return (false, "Recycle Bin unavailable"); },
            journalStore: directJournal,
            utcNowOverride: t0,
            removalMode: FileRemovalMode.RecycleBin,
            directDeleteHandler: _ => { directFallbackCalls++; return (true, string.Empty); });
        Check(!recycleFailRes.Success &&
              recycleFailCalls == 1 &&
              directFallbackCalls == 0 &&
              recycleFailRes.FailedPaths.Contains(failRecycleFile) &&
              recycleFailRes.FailureReasons.ContainsKey(failRecycleFile) &&
              File.Exists(Path.Combine(repo, failRecycleFile)),
              "RecycleBin failure never falls back to Direct Delete and preserves file + failure reason");

        // Retrying the remaining failed file after switching to Direct succeeds cleanly
        var retryDirectRes = await DiscardBackupService.ExecuteDiscardAsync(
            repo,
            [new Change { Path = failRecycleFile, Index = ChangeState.Untracked, WorkTree = ChangeState.Untracked }],
            DiscardBaselineKind.MoveUntrackedToRecycleBin,
            backupStore: directStore,
            recycleHandler: _ => { recycleFailCalls++; return (true, string.Empty); },
            journalStore: directJournal,
            utcNowOverride: t0,
            removalMode: FileRemovalMode.Direct);
        Check(retryDirectRes.Success &&
              !File.Exists(Path.Combine(repo, failRecycleFile)) &&
              retryDirectRes.DirectDeletedPaths.Contains(failRecycleFile),
              "Switching failed item to Direct Delete deletes it cleanly on retry");

        // 7c. Backup write failure blocks Direct Delete completely before calling directDeleteHandler
        var keepOnBackupErr = "backup-fail-direct.txt";
        await File.WriteAllTextAsync(Path.Combine(repo, keepOnBackupErr), "do not delete", Utf8NoBom);
        var blockedDirectCalls = 0;
        var backupBlockedDirect = await DiscardBackupService.ExecuteDiscardAsync(
            repo,
            [new Change { Path = keepOnBackupErr, Index = ChangeState.Untracked, WorkTree = ChangeState.Untracked }],
            DiscardBaselineKind.MoveUntrackedToRecycleBin,
            backupStore: failingStore,
            journalStore: directJournal,
            utcNowOverride: t0,
            removalMode: FileRemovalMode.Direct,
            directDeleteHandler: _ => { blockedDirectCalls++; return (true, string.Empty); });
        Check(!backupBlockedDirect.Success &&
              blockedDirectCalls == 0 &&
              File.Exists(Path.Combine(repo, keepOnBackupErr)),
              "Backup failure aborts Direct Delete before calling delete handler");
        File.Delete(Path.Combine(repo, keepOnBackupErr));

        // 7d. Unborn HEAD supports Direct Delete + 30d backup + restore
        var unbornUntracked = "unborn-direct.txt";
        await File.WriteAllTextAsync(Path.Combine(unbornRepo, unbornUntracked), "unborn untracked payload\n", Utf8NoBom);
        var unbornDirectRes = await DiscardBackupService.ExecuteDiscardAsync(
            unbornRepo,
            [new Change { Path = unbornUntracked, Index = ChangeState.Untracked, WorkTree = ChangeState.Untracked }],
            DiscardBaselineKind.MoveUntrackedToRecycleBin,
            backupStore: directStore,
            journalStore: directJournal,
            utcNowOverride: t0,
            removalMode: FileRemovalMode.Direct);
        var unbornRestoreRes = await DiscardBackupService.RestoreBackupAsync(
            unbornRepo,
            unbornDirectRes.BackupId,
            backupStore: directStore,
            journalStore: directJournal,
            utcNowOverride: t0);
        Check(unbornDirectRes.Success && unbornRestoreRes.Success &&
              await File.ReadAllTextAsync(Path.Combine(unbornRepo, unbornUntracked), Utf8NoBom) == "unborn untracked payload\n",
              "Unborn HEAD repo backs up, directly deletes, and restores untracked files");

        // 7e. Directory expansion skips ignored files/local excludes/nested repos, handles empty dirs, and rejects mid-flight dir changes
        var excludePath = Path.Combine(repo, ".git", "info", "exclude");
        await File.WriteAllTextAsync(excludePath, "*.excluded\n", Utf8NoBom);
        var mixDir = Path.Combine(repo, "mixdir");
        var mixSub = Path.Combine(mixDir, "sub");
        var mixEmpty = Path.Combine(mixDir, "emptysub");
        var mixNested = Path.Combine(mixDir, "nestedrepo");
        Directory.CreateDirectory(mixSub);
        Directory.CreateDirectory(mixEmpty);
        await InitRepoAsync(realGit, mixNested, "main", "Bob", "bob@example.invalid");
        await File.WriteAllTextAsync(Path.Combine(mixNested, "inner.txt"), "nested commit file\n", Utf8NoBom);
        await File.WriteAllTextAsync(Path.Combine(mixDir, "del1.txt"), "delete me 1\n", Utf8NoBom);
        await File.WriteAllTextAsync(Path.Combine(mixSub, "del2.txt"), "delete me 2\n", Utf8NoBom);
        await File.WriteAllTextAsync(Path.Combine(mixDir, "keep.ignored"), "ignored data\n", Utf8NoBom);
        await File.WriteAllTextAsync(Path.Combine(mixDir, "keep.excluded"), "excluded data\n", Utf8NoBom);

        var mixInspect = await DiscardBackupService.InspectDiscardAsync(
            repo,
            [
                new Change { Path = "mixdir", Index = ChangeState.Untracked, WorkTree = ChangeState.Untracked },
                new Change { Path = "mixdir/del1.txt", Index = ChangeState.Untracked, WorkTree = ChangeState.Untracked },
            ],
            DiscardBaselineKind.MoveUntrackedToRecycleBin,
            FileRemovalMode.Direct,
            ["mixdir"]);
        Check(mixInspect.CanProceed &&
              mixInspect.UntrackedTargets.Select(t => t.Path).Order().SequenceEqual(["mixdir/del1.txt", "mixdir/sub/del2.txt"]) &&
              mixInspect.SkippedNestedRepoPaths.Contains("mixdir/nestedrepo") &&
              mixInspect.SkippedIgnoredPaths.Contains("mixdir/keep.ignored") &&
              mixInspect.SkippedIgnoredPaths.Contains("mixdir/keep.excluded"),
              "Directory BFS expansion deduplicates leaves and skips nested repos plus .gitignore and local exclude files");

        // Directory drift between inspect and execute aborts safely
        await File.WriteAllTextAsync(Path.Combine(mixDir, "arrived-late.txt"), "created after preflight\n", Utf8NoBom);
        var dirDriftRes = await DiscardBackupService.ExecuteDiscardAsync(
            repo,
            [new Change { Path = "mixdir", Index = ChangeState.Untracked, WorkTree = ChangeState.Untracked }],
            DiscardBaselineKind.MoveUntrackedToRecycleBin,
            backupStore: directStore,
            journalStore: directJournal,
            utcNowOverride: t0,
            expectedPreflight: mixInspect,
            removalMode: FileRemovalMode.Direct,
            selectedFolderPaths: ["mixdir"]);
        Check(!dirDriftRes.Success && dirDriftRes.BlockedBySnapshot &&
              File.Exists(Path.Combine(mixDir, "del1.txt")) &&
              File.Exists(Path.Combine(mixDir, "arrived-late.txt")),
              "Directory content drift between confirmation and execution aborts safely without deleting files");
        File.Delete(Path.Combine(mixDir, "arrived-late.txt"));

        var mixExec = await DiscardBackupService.ExecuteDiscardAsync(
            repo,
            [new Change { Path = "mixdir", Index = ChangeState.Untracked, WorkTree = ChangeState.Untracked }],
            DiscardBaselineKind.MoveUntrackedToRecycleBin,
            backupStore: directStore,
            journalStore: directJournal,
            utcNowOverride: t0,
            expectedPreflight: mixInspect,
            removalMode: FileRemovalMode.Direct,
            selectedFolderPaths: ["mixdir"]);
        Check(mixExec.Success &&
              !File.Exists(Path.Combine(mixDir, "del1.txt")) &&
              !File.Exists(Path.Combine(mixSub, "del2.txt")) &&
              !Directory.Exists(mixEmpty) &&
              !Directory.Exists(mixSub) &&
              Directory.Exists(mixDir) &&
              File.Exists(Path.Combine(mixDir, "keep.ignored")) &&
              File.Exists(Path.Combine(mixDir, "keep.excluded")) &&
              File.Exists(Path.Combine(mixNested, "inner.txt")),
              "Mixed folder cleanup removes only empty subdirs and non-ignored leaves while preserving ignored files and nested repo");

        var pureEmptyDir = Path.Combine(repo, "clean-all-dir");
        Directory.CreateDirectory(Path.Combine(pureEmptyDir, "empty"));
        await File.WriteAllTextAsync(Path.Combine(pureEmptyDir, "only.txt"), "clean all\n", Utf8NoBom);
        var cleanAllRes = await DiscardBackupService.ExecuteDiscardAsync(
            repo,
            [],
            DiscardBaselineKind.MoveUntrackedToRecycleBin,
            backupStore: directStore,
            journalStore: directJournal,
            utcNowOverride: t0,
            removalMode: FileRemovalMode.Direct,
            selectedFolderPaths: ["clean-all-dir"]);
        Check(cleanAllRes.Success && !Directory.Exists(pureEmptyDir),
              "Folder cleanup removes parent directory once empty");

        // 7f. RestoreToHead with IndexAddedOnly uses Direct removal mode and restores disk + index cleanly
        var headDirectFile = "staged-new-direct.txt";
        await File.WriteAllTextAsync(Path.Combine(repo, headDirectFile), "staged new direct content\n", Utf8NoBom);
        await GitInAsync(realGit, repo, "add", headDirectFile);
        var headAddedChange = (await new QueryLocalChanges(repo).GetResultAsync()).Single(c => c.Path == headDirectFile);
        var headAddedPreflight = await DiscardBackupService.InspectDiscardAsync(
            repo,
            [headAddedChange],
            DiscardBaselineKind.RestoreToHead,
            FileRemovalMode.Direct);
        var headAddedRecycleCalls = 0;
        var headAddedRes = await DiscardBackupService.ExecuteDiscardAsync(
            repo,
            [headAddedChange],
            DiscardBaselineKind.RestoreToHead,
            backupStore: directStore,
            recycleHandler: _ => { headAddedRecycleCalls++; return (true, string.Empty); },
            journalStore: directJournal,
            utcNowOverride: t0,
            expectedPreflight: headAddedPreflight,
            removalMode: FileRemovalMode.Direct);
        Check(headAddedPreflight.IncludesFileRemoval &&
              headAddedRes.Success &&
              headAddedRecycleCalls == 0 &&
              !File.Exists(Path.Combine(repo, headDirectFile)) &&
              headAddedRes.DirectDeletedPaths.Contains(headDirectFile) &&
              string.IsNullOrWhiteSpace(await GitInAsync(realGit, repo, "ls-files", headDirectFile)),
              "RestoreToHead applies Direct Delete to IndexAddedOnly files without invoking Recycle Bin");
        var headAddedRestore = await DiscardBackupService.RestoreBackupAsync(
            repo,
            headAddedRes.BackupId,
            backupStore: directStore,
            journalStore: directJournal,
            utcNowOverride: t0);
        Check(headAddedRestore.Success &&
              await File.ReadAllTextAsync(Path.Combine(repo, headDirectFile), Utf8NoBom) == "staged new direct content\n" &&
              await GitInAsync(realGit, repo, "show", $":{headDirectFile}") == "staged new direct content\n",
              "Restoring IndexAddedOnly Direct-delete backup recovers both worktree and index");
        await GitInAsync(realGit, repo, "reset", "--hard", "HEAD");
        await GitInAsync(realGit, repo, "clean", "-fd");

        // 7g. Security boundary + Junction / symlink protection
        var outsideDir = Path.Combine(directRoot, "outside-target");
        Directory.CreateDirectory(outsideDir);
        var outsideSecret = Path.Combine(outsideDir, "outside-secret.txt");
        await File.WriteAllTextAsync(outsideSecret, "must never be deleted", Utf8NoBom);

        foreach (var badPath in new[] { ".", ".git/config", "../outside-target/outside-secret.txt", Path.Combine(outsideDir, "outside-secret.txt") })
        {
            var secPre = await DiscardBackupService.InspectDiscardAsync(
                repo,
                [new Change { Path = badPath, Index = ChangeState.Untracked, WorkTree = ChangeState.Untracked }],
                DiscardBaselineKind.MoveUntrackedToRecycleBin,
                FileRemovalMode.Direct);
            Check(!secPre.CanProceed && secPre.InvalidPaths.Count > 0,
                  $"Path safety validation rejects dangerous path '{badPath}'");
        }

        if (OperatingSystem.IsWindows())
        {
            var juncRoot = Path.Combine(repo, "juncparent");
            var juncPath = Path.Combine(juncRoot, "junc");
            Directory.CreateDirectory(juncRoot);
            await File.WriteAllTextAsync(Path.Combine(juncRoot, "local.txt"), "local leaf\n", Utf8NoBom);
            var mk = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{juncPath}\" \"{outsideDir}\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            mk!.WaitForExit();
            if (mk.ExitCode == 0 && Directory.Exists(juncPath))
            {
                var traversePre = await DiscardBackupService.InspectDiscardAsync(
                    repo,
                    [new Change { Path = "juncparent/junc/outside-secret.txt", Index = ChangeState.Untracked, WorkTree = ChangeState.Untracked }],
                    DiscardBaselineKind.MoveUntrackedToRecycleBin,
                    FileRemovalMode.Direct);
                Check(!traversePre.CanProceed && File.Exists(outsideSecret),
                      "Explicit traversal into directory junction is blocked before touching outside target");

                var juncFolderRes = await DiscardBackupService.ExecuteDiscardAsync(
                    repo,
                    [new Change { Path = "juncparent/junc/outside-secret.txt", Index = ChangeState.Untracked, WorkTree = ChangeState.Untracked }],
                    DiscardBaselineKind.MoveUntrackedToRecycleBin,
                    backupStore: directStore,
                    journalStore: directJournal,
                    utcNowOverride: t0,
                    removalMode: FileRemovalMode.Direct,
                    selectedFolderPaths: ["juncparent"]);
                Check(juncFolderRes.Success &&
                      !Directory.Exists(juncPath) &&
                      !Directory.Exists(juncRoot) &&
                      File.Exists(outsideSecret) &&
                      await File.ReadAllTextAsync(outsideSecret, Utf8NoBom) == "must never be deleted",
                      "Cleaning folder with Windows junction deletes only the junction link without deleting external directory contents");
            }
        }
    }
}
