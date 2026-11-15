using SourceGit.Commands;
using SourceGit.Models;

internal static partial class Program
{
    private static async Task RunReviewDiscardSafetyTestsAsync(string git, string suiteRoot)
    {
        var root = Path.Combine(suiteRoot, "review-discard");
        Directory.CreateDirectory(root);
        var repo = Path.Combine(root, "repo");
        await GitAsync(git, "init", "-b", "task", repo);
        await GitAsync(git, "-C", repo, "config", "user.name", "Discard Tester");
        await GitAsync(git, "-C", repo, "config", "user.email", "discard@example.invalid");
        var file = Path.Combine(repo, "a.txt");
        File.WriteAllText(file, "original\n");
        await GitAsync(git, "-C", repo, "add", ".");
        await GitAsync(git, "-C", repo, "commit", "-m", "initial");
        var store = new DiscardBackupStore(Path.Combine(root, "backups"));
        var journal = new OperationJournalStore(Path.Combine(root, "journal.json"));
        (bool, string) Recycle(string path)
        {
            File.Move(path, Path.Combine(root, Guid.NewGuid().ToString("N")));
            return (true, string.Empty);
        }
        async Task<DiscardExecutionResult> Discard(DiscardBaselineKind baseline, IReadOnlyList<Change> targets = null)
            => await DiscardBackupService.ExecuteDiscardAsync(repo,
                targets ?? await new QueryLocalChanges(repo).GetResultAsync(), baseline,
                backupStore: store, recycleHandler: Recycle, journalStore: journal);
        async Task<DiscardBackupRestoreResult> Restore(string id)
            => await DiscardBackupService.RestoreBackupAsync(repo, id, backupStore: store,
                recycleHandler: Recycle, journalStore: journal);

        File.Delete(file);
        var deleted = await Discard(DiscardBaselineKind.RestoreWorktreeToIndex);
        Check(deleted.Success && File.ReadAllText(file) == "original\n",
            "Discard restores a missing tracked file from the index without treating absence as backup failure");
        var deletedRestore = await Restore(deleted.BackupId);
        Check(deletedRestore.Success && !File.Exists(file) &&
              (await GitAsync(git, "-C", repo, "ls-files", "a.txt")).Trim() == "a.txt",
            "Discard backup restores a worktree deletion while retaining the tracked index entry");
        await GitAsync(git, "-C", repo, "restore", "a.txt");

        await GitAsync(git, "-C", repo, "rm", "a.txt");
        var stagedDelete = await Discard(DiscardBaselineKind.RestoreToHead);
        Check(stagedDelete.Success && File.Exists(file), "HEAD discard restores a staged deletion");
        var stagedRestore = await Restore(stagedDelete.BackupId);
        Check(stagedRestore.Success && !File.Exists(file) &&
              string.IsNullOrWhiteSpace(await GitAsync(git, "-C", repo, "ls-files", "a.txt")) &&
              (await GitAsync(git, "-C", repo, "status", "--porcelain")).Trim() == "D  a.txt",
            "Recovery restores the exact staged deletion rather than reintroducing the HEAD index entry");
        await GitAsync(git, "-C", repo, "restore", "--source=HEAD", "--staged", "--worktree", "a.txt");

        await GitAsync(git, "-C", repo, "rm", "--cached", "a.txt");
        var cachedBefore = await GitAsync(git, "-C", repo, "status", "--porcelain");
        var cachedTargets = (await new QueryLocalChanges(repo).GetResultAsync()).Where(c => c.Index == ChangeState.Deleted).ToList();
        var cachedDiscard = await Discard(DiscardBaselineKind.RestoreToHead, cachedTargets);
        var cachedRestore = await Restore(cachedDiscard.BackupId);
        Check(cachedDiscard.Success && cachedRestore.Success && File.ReadAllText(file) == "original\n" &&
              await GitAsync(git, "-C", repo, "status", "--porcelain") == cachedBefore,
            "Recovery preserves both the cached deletion and the untracked working file");
        await GitAsync(git, "-C", repo, "add", "a.txt");

        await GitAsync(git, "-C", repo, "mv", "a.txt", "b.txt");
        var rename = (await new QueryLocalChanges(repo).GetResultAsync()).Single(c => c.Index == ChangeState.Renamed);
        var confirmation = await DiscardBackupService.InspectDiscardAsync(repo, [rename], DiscardBaselineKind.RestoreToHead);
        Check(confirmation.ConfirmationSnapshot.TargetFiles.Select(t => t.RelativePath).Order().SequenceEqual(new[] { "a.txt", "b.txt" }),
            "Rename confirmation captures both the original and destination paths");
        File.WriteAllText(file, "new untracked work\n");
        var collided = await Discard(DiscardBaselineKind.RestoreToHead, [rename]);
        Check(!collided.Success && File.ReadAllText(file) == "new untracked work\n" && File.Exists(Path.Combine(repo, "b.txt")),
            "Discarding a rename refuses to overwrite a new untracked file at its original path");
        Recycle(file);
        var renamedBefore = await GitAsync(git, "-C", repo, "status", "--porcelain");
        var renameDiscard = await Discard(DiscardBaselineKind.RestoreToHead, [rename]);
        Check(renameDiscard.Success && store.TryGet(renameDiscard.BackupId).Files.Count == 2,
            "Rename discard backs up both paths including the absent original path");
        var renameRestore = await Restore(renameDiscard.BackupId);
        Check(renameRestore.Success && !File.Exists(file) && File.ReadAllText(Path.Combine(repo, "b.txt")) == "original\n" &&
              await GitAsync(git, "-C", repo, "status", "--porcelain") == renamedBefore,
            "Rename recovery restores the exact original worktree and staged rename");
        await GitAsync(git, "-C", repo, "reset", "--hard", "HEAD");

        File.WriteAllText(file, "queued content\n");
        var preflight = await DiscardBackupService.InspectDiscardAsync(repo,
            await new QueryLocalChanges(repo).GetResultAsync(), DiscardBaselineKind.RestoreToHead);
        var queueJournal = new OperationJournalStore(Path.Combine(root, "queued-journal.json"));
        var (_, commonDir) = await RepositoryWriteGuard.ResolveGitDirectoriesAsync(repo);
        var guard = await RepositoryWriteGuard.TryEnterAsync(repo, "Concurrent write fixture");
        Task<DiscardExecutionResult> pending;
        try
        {
            pending = DiscardBackupService.ExecuteDiscardAsync(repo, await new QueryLocalChanges(repo).GetResultAsync(),
                DiscardBaselineKind.RestoreToHead, backupStore: store, journalStore: queueJournal, expectedPreflight: preflight);
            var limit = DateTime.UtcNow.AddSeconds(20);
            while (!queueJournal.GetByRepository(commonDir, 10).Any(r => r.Stage == GitOperationStage.Preparing))
            {
                if (DateTime.UtcNow >= limit) throw new TimeoutException("Discard did not reach the write guard.");
                await Task.Delay(20);
            }
            File.WriteAllText(file, "new work while queued\n");
        }
        finally
        {
            await guard.DisposeAsync();
        }
        var queued = await pending;
        Check(!queued.Success && queued.BlockedBySnapshot && File.ReadAllText(file) == "new work while queued\n",
            "Discard revalidates confirmation after acquiring the write guard and preserves changes made while queued");
        await GitAsync(git, "-C", repo, "restore", "a.txt");

        var wrongRepo = Path.Combine(root, "other-repo");
        await GitAsync(git, "init", wrongRepo);
        var wrongRestore = await DiscardBackupService.RestoreBackupAsync(wrongRepo, renameDiscard.BackupId,
            allowOverwriteSubsequentEdits: true, backupStore: store, journalStore: journal);
        Check(!wrongRestore.Success && !File.Exists(Path.Combine(wrongRepo, "b.txt")),
            "Recovery rejects backups belonging to another repository even with explicit overwrite enabled");
    }
}
