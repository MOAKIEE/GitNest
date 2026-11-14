using System.Text;
using SourceGit.Commands;
using SourceGit.Models;
using StashModel = SourceGit.Models.Stash;

internal static partial class Program
{
    private static async Task RunStashDiscardIgnoreTestsAsync(string realGit, string suiteRoot)
    {
        var utf8NoBom = new UTF8Encoding(false);
        var phaseRoot = Path.Combine(suiteRoot, "stash-discard-ignore-tests");
        Directory.CreateDirectory(phaseRoot);

        var journalFile = Path.Combine(phaseRoot, "journal.json");
        var journal = new OperationJournalStore(journalFile);
        var backupRoot = Path.Combine(phaseRoot, "discard-backups");
        var backupStore = new DiscardBackupStore(backupRoot);

        // ------------------------------------------------------------
        // 1. Unborn HEAD repo blocks stash push & RestoreToHead discard
        // ------------------------------------------------------------
        var unbornRepo = Path.Combine(phaseRoot, "unborn-repo");
        Directory.CreateDirectory(unbornRepo);
        await GitAsync(realGit, "init", unbornRepo);
        await GitAsync(realGit, "-C", unbornRepo, "config", "--local", "user.name", "Alice");
        await GitAsync(realGit, "-C", unbornRepo, "config", "--local", "user.email", "alice@example.invalid");
        File.WriteAllText(Path.Combine(unbornRepo, "draft.txt"), "unborn draft\n", utf8NoBom);
        await GitAsync(realGit, "-C", unbornRepo, "add", "draft.txt");

        var unbornStashPre = await StashWorkflowService.InspectPushAsync(unbornRepo);
        Check(!unbornStashPre.CanProceed && unbornStashPre.IsUnbornHead,
            "Unborn HEAD repo blocks stash push with explicit initial-commit guidance");

        var unbornStashExec = await StashWorkflowService.ExecutePushAsync(unbornRepo, "should block", journalStore: journal);
        Check(!unbornStashExec.Success && !string.IsNullOrEmpty(unbornStashExec.Message),
            "ExecutePushAsync rejects unborn HEAD without corrupting index");

        var unbornChanges = await new QueryLocalChanges(unbornRepo).GetResultAsync();
        var unbornHeadDiscardPre = await DiscardBackupService.InspectDiscardAsync(unbornRepo, unbornChanges, DiscardBaselineKind.RestoreToHead);
        Check(!unbornHeadDiscardPre.CanProceed && unbornHeadDiscardPre.HasUnbornHead,
            "Unborn HEAD repo blocks RestoreToHead discard baseline");

        // ------------------------------------------------------------
        // 2. Main repo setup (tracked, special filenames, .gitignore, external stash compatibility)
        // ------------------------------------------------------------
        var repo = Path.Combine(phaseRoot, "main-repo");
        Directory.CreateDirectory(repo);
        await GitAsync(realGit, "init", "-b", "main", repo);
        await GitAsync(realGit, "-C", repo, "config", "--local", "user.name", "Alice");
        await GitAsync(realGit, "-C", repo, "config", "--local", "user.email", "alice@example.invalid");

        var docRel = "docs [1]/中文 说明.txt";
        var baseRel = "app.txt";
        var mergeRel = "shared.txt";
        var renameOldRel = "old-name.txt";
        Directory.CreateDirectory(Path.Combine(repo, "docs [1]"));
        File.WriteAllText(Path.Combine(repo, ".gitignore"), "*.ignored\n", utf8NoBom);
        File.WriteAllText(Path.Combine(repo, docRel), "doc-v1\n", utf8NoBom);
        File.WriteAllText(Path.Combine(repo, baseRel), "line1\nline2\nline3\nline4\n", utf8NoBom);
        File.WriteAllText(Path.Combine(repo, mergeRel), "base-alpha\n", utf8NoBom);
        File.WriteAllText(Path.Combine(repo, renameOldRel), "rename-source-v1\n", utf8NoBom);
        await GitAsync(realGit, "-C", repo, "add", ".gitignore", docRel, baseRel, mergeRel, renameOldRel);
        await GitAsync(realGit, "-C", repo, "commit", "-m", "initial commit");
        var headMainSha = (await GitAsync(realGit, "-C", repo, "rev-parse", "HEAD")).Trim();

        // Stash model parsing: WIP on / On / external stash without standard prefix
        var sWip = new StashModel { Name = "stash@{0}", SHA = headMainSha, Message = "WIP on feat/ui: abcdef1 fix" };
        var sOn = new StashModel { Name = "stash@{1}", SHA = headMainSha, Message = "On main: custom msg" };
        var sExt = new StashModel { Name = "stash@{2}", SHA = headMainSha, Message = "external tool raw commit title" };
        Check(sWip.SourceBranch == "feat/ui" && sWip.HasStandardPrefix, "Stash model extracts source branch from WIP prefix");
        Check(sOn.SourceBranch == "main" && sOn.DisplaySubject == "custom msg", "Stash model extracts source branch and message from On prefix");
        Check(string.IsNullOrEmpty(sExt.SourceBranch) && !sExt.HasStandardPrefix && sExt.DisplaySubject == "external tool raw commit title",
            "Stash model degrades gracefully for external stashes without branch prefix");

        // ------------------------------------------------------------
        // 3. Stash Push preflight: special characters, untracked inclusion, ignored file count notice
        // ------------------------------------------------------------
        File.WriteAllText(Path.Combine(repo, docRel), "doc-v2-edited\n", utf8NoBom);
        File.WriteAllText(Path.Combine(repo, "untracked 中文.txt"), "new-untracked\n", utf8NoBom);
        File.WriteAllText(Path.Combine(repo, "secret.ignored"), "keep-ignored\n", utf8NoBom);

        var localChanges = await new QueryLocalChanges(repo).GetResultAsync();
        var docChange = localChanges.Single(c => c.Path == docRel);
        var untrackedChange = localChanges.Single(c => c.Path == "untracked 中文.txt");

        var pushPre = await StashWorkflowService.InspectPushAsync(
            repo,
            selectedChanges: [docChange, untrackedChange],
            includeUntracked: true,
            stagedOnly: false);
        Check(pushPre.CanProceed && pushPre.IncludedCount == 2 && pushPre.UntrackedIncludedCount == 1 && pushPre.IgnoredFileCount == 1,
            "Stash push preflight counts included tracked/untracked files and warns about excluded ignored files");

        var pushRes = await StashWorkflowService.ExecutePushAsync(
            repo,
            "partial special chars",
            selectedChanges: [docChange, untrackedChange],
            includeUntracked: true,
            journalStore: journal);
        Check(pushRes.Success && !string.IsNullOrEmpty(pushRes.CreatedStashSha) &&
              File.ReadAllText(Path.Combine(repo, docRel), utf8NoBom) == "doc-v1\n" &&
              !File.Exists(Path.Combine(repo, "untracked 中文.txt")) &&
              File.Exists(Path.Combine(repo, "secret.ignored")),
            "Stash push uses literal NUL pathspecs on special filenames and leaves ignored files untouched");

        var specialStashSha = pushRes.CreatedStashSha;

        // ------------------------------------------------------------
        // 4. Stash Apply collision checks (dirty worktree + untracked/ignored collision) + SHA stability
        // ------------------------------------------------------------
        // Case A: dirty working tree blocks default InspectApplyAsync / ExecuteApplyAsync
        File.WriteAllText(Path.Combine(repo, baseRel), "dirty-local\n", utf8NoBom);
        var dirtyApplyPre = await StashWorkflowService.InspectApplyAsync(repo, new StashModel { Name = "stash@{0}", SHA = specialStashSha });
        Check(!dirtyApplyPre.CanProceed && !dirtyApplyPre.IsWorkingTreeClean,
            "Stash apply preflight blocks when working tree has uncommitted edits");
        var dirtyApplyRes = await StashWorkflowService.ExecuteApplyAsync(repo, "stash@{0}", specialStashSha, journalStore: journal);
        Check(dirtyApplyRes.Status == StashApplyStatus.Blocked && dirtyApplyRes.StashRetained,
            "ExecuteApplyAsync blocks dirty working tree without applying or dropping stash");
        await GitAsync(realGit, "-C", repo, "checkout", "--", baseRel);

        // Case B: colliding untracked file blocks apply
        File.WriteAllText(Path.Combine(repo, "untracked 中文.txt"), "newer-untracked-collision\n", utf8NoBom);
        var untrackedColPre = await StashWorkflowService.InspectApplyAsync(repo, new StashModel { Name = "stash@{0}", SHA = specialStashSha });
        Check(!untrackedColPre.CanProceed && untrackedColPre.CollidingUntrackedPaths.Contains("untracked 中文.txt"),
            "Stash apply preflight catches untracked path collision against stash^3");
        File.Delete(Path.Combine(repo, "untracked 中文.txt"));

        // Case C: colliding ignored file blocks apply
        File.WriteAllText(Path.Combine(repo, ".gitignore"), "*.ignored\ndocs \\[1\\]/中文 说明.txt\n", utf8NoBom);
        await GitAsync(realGit, "-C", repo, "add", ".gitignore");
        await GitAsync(realGit, "-C", repo, "commit", "-m", "ignore doc");
        await GitAsync(realGit, "-C", repo, "rm", "--cached", docRel);
        await GitAsync(realGit, "-C", repo, "commit", "-m", "untrack doc");
        File.WriteAllText(Path.Combine(repo, docRel), "ignored-file-on-disk\n", utf8NoBom);
        var ignoredColPre = await StashWorkflowService.InspectApplyAsync(repo, new StashModel { Name = "stash@{0}", SHA = specialStashSha });
        Check(!ignoredColPre.CanProceed && ignoredColPre.CollidingIgnoredPaths.Contains(docRel),
            "Stash apply preflight catches ignored path collision so apply will not silently fail or overwrite");
        // Restore main back to clean initial commit for clean conflict/SHA tests
        await GitAsync(realGit, "-C", repo, "reset", "--hard", headMainSha);
        File.Delete(Path.Combine(repo, "secret.ignored"));

        // Case D: index shift after pushing another newer stash still applies/drops target by SHA
        File.WriteAllText(Path.Combine(repo, baseRel), "newer-unrelated-change\n", utf8NoBom);
        var newerPush = await StashWorkflowService.ExecutePushAsync(repo, "newer shift stash", journalStore: journal);
        Check(newerPush.Success && newerPush.CreatedStashSha != specialStashSha, "Pushed second stash shifting previous stash to stash@{1}");

        // Target specialStashSha was originally stash@{0}; passing old name "stash@{0}" + expected specialStashSha must target specialStashSha!
        var shiftedApply = await StashWorkflowService.ExecuteApplyAsync(
            repo,
            "stash@{0}",
            specialStashSha,
            restoreIndex: true,
            dropAfterApply: true,
            journalStore: journal);
        Check(shiftedApply.Success && shiftedApply.StashSha == specialStashSha && shiftedApply.DropSucceeded &&
              File.ReadAllText(Path.Combine(repo, docRel), utf8NoBom) == "doc-v2-edited\n" &&
              File.Exists(Path.Combine(repo, "untracked 中文.txt")),
            "ExecuteApplyAsync resolves shifted stash index by exact SHA, applies, and drops only the applied stash");

        var stashesAfterShiftDrop = await new QueryStashes(repo).GetResultAsync();
        Check(stashesAfterShiftDrop.Count == 1 && stashesAfterShiftDrop[0].SHA == newerPush.CreatedStashSha,
            "Unrelated newer stash stays intact after dropping the SHA-matched earlier stash");

        // Clean working tree before next tests
        await GitAsync(realGit, "-C", repo, "checkout", "--", docRel);
        File.Delete(Path.Combine(repo, "untracked 中文.txt"));

        // ------------------------------------------------------------
        // 5. Stash apply conflict: retains stash, marks PendingVerification, recognized by InProgressInspection
        // ------------------------------------------------------------
        File.WriteAllText(Path.Combine(repo, mergeRel), "stash-side-line\n", utf8NoBom);
        var conflictStashRes = await StashWorkflowService.ExecutePushAsync(repo, "conflicting stash", journalStore: journal);
        Check(conflictStashRes.Success, "Created conflict-producing stash");

        File.WriteAllText(Path.Combine(repo, mergeRel), "committed-branch-line\n", utf8NoBom);
        await GitAsync(realGit, "-C", repo, "add", mergeRel);
        await GitAsync(realGit, "-C", repo, "commit", "-m", "diverge shared.txt");
        var conflictHead = (await GitAsync(realGit, "-C", repo, "rev-parse", "HEAD")).Trim();

        var conflictApply = await StashWorkflowService.ExecuteApplyAsync(
            repo,
            conflictStashRes.CreatedStashName,
            conflictStashRes.CreatedStashSha,
            restoreIndex: true,
            dropAfterApply: true,
            journalStore: journal);
        Check(conflictApply.Status == StashApplyStatus.AppliedWithConflictsRetained &&
              !conflictApply.Success &&
              conflictApply.StashRetained &&
              !conflictApply.DropSucceeded &&
              conflictApply.ConflictedPaths.Contains(mergeRel),
            "Conflicting stash apply returns AppliedWithConflictsRetained, refuses to drop stash, and reports conflicted files");

        var stashesAfterConflict = await new QueryStashes(repo).GetResultAsync();
        Check(stashesAfterConflict.Any(s => s.SHA == conflictStashRes.CreatedStashSha),
            "Retained conflicting stash still exists in git stash list");

        var conflictInProgress = await MergeRevertService.InspectRepositoryInProgressAsync(repo, journal);
        Check(conflictInProgress.ConflictedPaths.Contains(mergeRel) &&
              conflictInProgress.Origin == ConflictOwnershipOrigin.GitNestOwnedWithExternalIntervention &&
              !conflictInProgress.CanOfferSafeAbort &&
              conflictInProgress.Headline.Contains("保存项仍保留", StringComparison.Ordinal) &&
              conflictInProgress.UnfinishedOperation?.ExpectedPartnerSha == conflictStashRes.CreatedStashSha,
            "Read-only conflict inspection identifies retained stash conflict without offering destructive auto-abort");

        // Resolve and reset cleanly to continue tests
        await GitAsync(realGit, "-C", repo, "checkout", "--ours", mergeRel);
        await GitAsync(realGit, "-C", repo, "add", mergeRel);
        await GitAsync(realGit, "-C", repo, "reset", "--hard", conflictHead);

        // ------------------------------------------------------------
        // 6. Branch Switch Safety: blocks Discard policy; StashAndReapply preserves untracked & retains on conflict
        // ------------------------------------------------------------
        await GitAsync(realGit, "-C", repo, "branch", "feat/switch-clean", headMainSha);
        File.WriteAllText(Path.Combine(repo, baseRel), "local-edit-before-switch\n", utf8NoBom);
        File.WriteAllText(Path.Combine(repo, "untracked-switch.txt"), "carry-me\n", utf8NoBom);

        var refusedSwitch = await StashWorkflowService.SwitchSafelyAsync(
            repo,
            ["checkout", "feat/switch-clean"],
            DealWithLocalChanges.Discard,
            "feat/switch-clean",
            journal);
        Check(!refusedSwitch.Success && (await GitAsync(realGit, "-C", repo, "branch", "--show-current")).Trim() == "main",
            "Branch switch refuses DealWithLocalChanges.Discard so local work is never auto-discarded");

        var cleanSwitch = await StashWorkflowService.SwitchSafelyAsync(
            repo,
            ["checkout", "feat/switch-clean"],
            DealWithLocalChanges.StashAndReapply,
            "feat/switch-clean",
            journal);
        Check(cleanSwitch.Success && cleanSwitch.CheckoutSucceeded && cleanSwitch.ReapplySucceeded && !cleanSwitch.StashRetained &&
              (await GitAsync(realGit, "-C", repo, "branch", "--show-current")).Trim() == "feat/switch-clean" &&
              File.ReadAllText(Path.Combine(repo, baseRel), utf8NoBom) == "local-edit-before-switch\n" &&
              File.Exists(Path.Combine(repo, "untracked-switch.txt")),
            "Branch switch StashAndReapply preserves untracked + tracked changes cleanly and drops temp stash");

        // Switch conflict retains temporary stash
        File.Delete(Path.Combine(repo, "untracked-switch.txt"));
        await GitAsync(realGit, "-C", repo, "checkout", "--", baseRel);
        File.WriteAllText(Path.Combine(repo, mergeRel), "switch-conflict-local\n", utf8NoBom);
        var conflictSwitch = await StashWorkflowService.SwitchSafelyAsync(
            repo,
            ["checkout", "main"],
            DealWithLocalChanges.StashAndReapply,
            "main",
            journal);
        Check(!conflictSwitch.Success && conflictSwitch.CheckoutSucceeded && conflictSwitch.HasConflicts &&
              conflictSwitch.StashRetained && !string.IsNullOrEmpty(conflictSwitch.StashSha),
            "Branch switch preserves temporary stash when reapply hits conflicts on target branch");

        await GitAsync(realGit, "-C", repo, "reset", "--hard", "HEAD");
        await GitAsync(realGit, "-C", repo, "stash", "clear");

        // ------------------------------------------------------------
        // 7. Discard dual baselines + 30-day recovery store + failure abort + subsequent-edit conflict + prune
        // ------------------------------------------------------------
        // Prepare partial staging on baseRel + rename + newly staged file
        File.WriteAllText(Path.Combine(repo, baseRel), "line1\nstaged-change\nline3\nline4\n", utf8NoBom);
        await GitAsync(realGit, "-C", repo, "add", baseRel);
        var expectedIndexContent = File.ReadAllText(Path.Combine(repo, baseRel), utf8NoBom);
        File.WriteAllText(Path.Combine(repo, baseRel), "line1\nstaged-change\nline3\nunstaged-change\n", utf8NoBom);
        var beforeDiscardWtContent = File.ReadAllText(Path.Combine(repo, baseRel), utf8NoBom);

        var t0 = new DateTime(2026, 6, 1, 10, 0, 0, DateTimeKind.Utc);

        // 7a. Backup write failure aborts discard and leaves disk/index untouched!
        var brokenStoreRoot = Path.Combine(phaseRoot, "blocked-backup-file");
        File.WriteAllText(brokenStoreRoot, "occupy file path so Directory.CreateDirectory fails", utf8NoBom);
        var failingStore = new DiscardBackupStore(brokenStoreRoot);
        var chBase = (await new QueryLocalChanges(repo).GetResultAsync()).Single(c => c.Path == baseRel);
        var abortedRes = await DiscardBackupService.ExecuteDiscardAsync(
            repo,
            [chBase],
            DiscardBaselineKind.RestoreWorktreeToIndex,
            backupStore: failingStore,
            journalStore: journal,
            utcNowOverride: t0);
        Check(!abortedRes.Success && File.ReadAllText(Path.Combine(repo, baseRel), utf8NoBom) == beforeDiscardWtContent,
            "Backup store write failure aborts discard cleanly before modifying working tree or index");

        // 7b. Baseline 1: RestoreWorktreeToIndex restores worktree only and preserves staged hunk + writes 30-day backup
        var idxDiscardRes = await DiscardBackupService.ExecuteDiscardAsync(
            repo,
            [chBase],
            DiscardBaselineKind.RestoreWorktreeToIndex,
            backupStore: backupStore,
            journalStore: journal,
            utcNowOverride: t0);
        Check(idxDiscardRes.Success && !string.IsNullOrEmpty(idxDiscardRes.BackupId) &&
              File.ReadAllText(Path.Combine(repo, baseRel), utf8NoBom) == expectedIndexContent,
            "Baseline 1 restores worktree to index while preserving staged changes and writing 30-day recovery backup");

        var afterIdxStaged = await GitAsync(realGit, "-C", repo, "show", $":{baseRel}");
        Check(afterIdxStaged == expectedIndexContent, "Index content remains strictly untouched by Baseline 1 restore");

        // Check recovery check & subsequent edit blocking
        var idxCheckClean = await DiscardBackupService.InspectBackupRestoreAsync(repo, idxDiscardRes.BackupId, backupStore, t0.AddDays(5));
        Check(idxCheckClean.CanRestore && !idxCheckClean.HasSubsequentModifications,
            "30-day recovery check passes when no subsequent edits happened");

        // Simulate subsequent user edit on disk -> recovery check must detect and block!
        File.WriteAllText(Path.Combine(repo, baseRel), "line1\nstaged-change\nnewer-edit-after-discard\n", utf8NoBom);
        var idxCheckBlocked = await DiscardBackupService.InspectBackupRestoreAsync(repo, idxDiscardRes.BackupId, backupStore, t0.AddDays(5));
        var idxRestoreBlocked = await DiscardBackupService.RestoreBackupAsync(
            repo, idxDiscardRes.BackupId, allowOverwriteSubsequentEdits: false, backupStore: backupStore, journalStore: journal, utcNowOverride: t0.AddDays(5));
        Check(!idxCheckBlocked.CanRestore && idxCheckBlocked.HasSubsequentModifications &&
              idxCheckBlocked.ConflictingPaths.Contains(baseRel) && !idxRestoreBlocked.Success &&
              File.ReadAllText(Path.Combine(repo, baseRel), utf8NoBom).Contains("newer-edit-after-discard"),
            "30-day recovery store refuses silent overwrite when file was edited after discard");

        // Restore cleanly when reset back to post-discard state
        await GitAsync(realGit, "-C", repo, "restore", "--worktree", "--", baseRel);
        var idxRestoreOk = await DiscardBackupService.RestoreBackupAsync(
            repo, idxDiscardRes.BackupId, allowOverwriteSubsequentEdits: false, backupStore: backupStore, journalStore: journal, utcNowOverride: t0.AddDays(5));
        Check(idxRestoreOk.Success && File.ReadAllText(Path.Combine(repo, baseRel), utf8NoBom) == beforeDiscardWtContent,
            "30-day recovery store restores exact pre-discard worktree bytes");

        // 7c. Baseline 2: RestoreToHead handles tracked edits + staged rename + index-added-only file with index blob recovery
        await GitAsync(realGit, "-C", repo, "mv", renameOldRel, "renamed-new.txt");
        File.WriteAllText(Path.Combine(repo, "staged-new.txt"), "brand new staged content\n", utf8NoBom);
        await GitAsync(realGit, "-C", repo, "add", "staged-new.txt");
        await GitAsync(realGit, "-C", repo, "add", baseRel); // stage baseRel so index != HEAD

        var fakeRecycled = new List<string>();
        var headDiscardChanges = (await new QueryLocalChanges(repo).GetResultAsync())
            .Where(c => c.Path is "app.txt" or "renamed-new.txt" or "staged-new.txt")
            .ToList();
        var headDiscardRes = await DiscardBackupService.ExecuteDiscardAsync(
            repo,
            headDiscardChanges,
            DiscardBaselineKind.RestoreToHead,
            backupStore: backupStore,
            recycleHandler: p =>
            {
                fakeRecycled.Add(Path.GetFileName(p));
                File.Delete(p);
                return (true, string.Empty);
            },
            journalStore: journal,
            utcNowOverride: t0);

        Check(headDiscardRes.Success &&
              File.Exists(Path.Combine(repo, renameOldRel)) &&
              !File.Exists(Path.Combine(repo, "renamed-new.txt")) &&
              !File.Exists(Path.Combine(repo, "staged-new.txt")) &&
              fakeRecycled.Contains("staged-new.txt") &&
              fakeRecycled.Contains("renamed-new.txt") &&
              File.ReadAllText(Path.Combine(repo, baseRel), utf8NoBom) == "line1\nline2\nline3\nline4\n" &&
              string.IsNullOrWhiteSpace(await GitAsync(realGit, "-C", repo, "status", "--porcelain")),
            "Baseline 2 restores modified HEAD files, reverses staged renames, and unstages+recycles index-added files cleanly");

        // Restore staged-new.txt & app.txt from HEAD-baseline 30-day backup and verify index blob was restored too!
        var headRestoreRes = await DiscardBackupService.RestoreBackupAsync(
            repo,
            headDiscardRes.BackupId,
            allowOverwriteSubsequentEdits: true,
            backupStore: backupStore,
            recycleHandler: p => { File.Delete(p); return (true, string.Empty); },
            journalStore: journal,
            utcNowOverride: t0.AddDays(1));
        var stagedNewBlobContent = await GitAsync(realGit, "-C", repo, "show", ":staged-new.txt");
        Check(headRestoreRes.Success && headRestoreRes.IndexRestoredCount >= 2 &&
              File.ReadAllText(Path.Combine(repo, "staged-new.txt"), utf8NoBom) == "brand new staged content\n" &&
              stagedNewBlobContent == "brand new staged content\n",
            "Restoring a HEAD-baseline backup recovers both working tree bytes and exact Git index blob");

        await GitAsync(realGit, "-C", repo, "reset", "--hard", "HEAD");
        await GitAsync(realGit, "-C", repo, "clean", "-fd");

        // 7d. 30-day expiration & prune check
        var expiredCheck = await DiscardBackupService.InspectBackupRestoreAsync(repo, idxDiscardRes.BackupId, backupStore, t0.AddDays(31));
        Check(!expiredCheck.CanRestore && expiredCheck.IsExpired,
            "Recovery store blocks restore after 30-day retention expiration");
        var pruneRes = backupStore.PruneExpired(t0.AddDays(31));
        Check(pruneRes.RemovedCount == 2 && backupStore.ListForRepository(repo).Count == 0,
            "PruneExpired cleans expired backup records and payload artifacts cleanly");

        // ------------------------------------------------------------
        // 8. Windows Recycle Bin (real SHFileOperationW success + locked file failure never falls back to delete)
        // ------------------------------------------------------------
        var untrackedKeepIgnored = Path.Combine(repo, "keep-me.ignored");
        File.WriteAllText(untrackedKeepIgnored, "ignored data", utf8NoBom);
        await Discard.AllAsync(repo, includeModified: false, includeUntracked: false, includeIgnored: true, log: null);
        Check(File.Exists(untrackedKeepIgnored),
            "Legacy Discard.AllAsync wrapper does not clean ignored files");
        File.Delete(untrackedKeepIgnored);

        var recycleFileName = $"gitnest-test-recycle-{Guid.NewGuid():N}.txt";
        var recycleFilePath = Path.Combine(repo, recycleFileName);
        File.WriteAllText(recycleFilePath, "tiny recycle test", utf8NoBom);
        var recycleChange = new Change { Path = recycleFileName, Index = ChangeState.Untracked, WorkTree = ChangeState.Untracked };
        var recycleOkRes = await DiscardBackupService.ExecuteDiscardAsync(
            repo,
            [recycleChange],
            DiscardBaselineKind.MoveUntrackedToRecycleBin,
            journalStore: journal);
        Check(recycleOkRes.Success && !File.Exists(recycleFilePath) && recycleOkRes.RecycledPaths.Contains(recycleFileName),
            "Windows Recycle Bin safely removes explicit untracked file without File.Delete");

        // Locked file failure must keep file intact on disk!
        var lockedName = "locked-untracked.txt";
        var lockedPath = Path.Combine(repo, lockedName);
        File.WriteAllText(lockedPath, "held open", utf8NoBom);
        using (var lockStream = new FileStream(lockedPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var lockedChange = new Change { Path = lockedName, Index = ChangeState.Untracked, WorkTree = ChangeState.Untracked };
            var lockedRes = await DiscardBackupService.ExecuteDiscardAsync(
                repo,
                [lockedChange],
                DiscardBaselineKind.MoveUntrackedToRecycleBin,
                journalStore: journal);
            Check(!lockedRes.Success && lockedRes.FailedPaths.Contains(lockedName) && File.Exists(lockedPath),
                "Recycle failure on locked file refuses permanent delete fallback and preserves file on disk");
        }
        File.Delete(lockedPath);

        // ------------------------------------------------------------
        // 9. Ignore rules (.gitignore + linked worktree info/exclude + escaping + dedupe + negation + tracked warn)
        // ------------------------------------------------------------
        var wtPath = Path.Combine(phaseRoot, "linked-wt");
        await GitAsync(realGit, "-C", repo, "worktree", "add", wtPath, "-b", "wt/ignore-test");
        var specialDir = Path.Combine(wtPath, "dir [1]");
        Directory.CreateDirectory(specialDir);
        var specialIgnRel = "dir [1]/#draft!.tmp";
        File.WriteAllText(Path.Combine(wtPath, specialIgnRel), "temp draft", utf8NoBom);
        File.WriteAllText(Path.Combine(wtPath, "already-tracked-check.tmp"), "untracked", utf8NoBom);

        var targets = await IgnoreRuleService.GetSupportedTargetsAsync(wtPath, specialIgnRel);
        var rootTarget = targets.Single(t => t.Kind == IgnoreRuleTargetKind.RootGitIgnore);
        var subTarget = targets.Single(t => t.Kind == IgnoreRuleTargetKind.ParentGitIgnore);
        var localTarget = targets.Single(t => t.Kind == IgnoreRuleTargetKind.LocalExclude);

        // Preset generation & special character escaping
        var presetsRoot = IgnoreRuleService.BuildPresetOptions(specialIgnRel, isDirectory: false, rootTarget.BaseRelativeDir);
        var exactPreset = presetsRoot.Single(p => p.Kind == IgnoreRulePresetKind.ExactFile);
        Check(exactPreset.Pattern.Contains(@"\[1\]", StringComparison.Ordinal),
            "Ignore rule preset escapes bracket glob metacharacters cleanly");

        // Write exact pattern to local info/exclude in linked worktree & verify real git status honors it
        var wtExactApply = await IgnoreRuleService.ApplyAsync(wtPath, exactPreset.Pattern, localTarget, journal);
        var wtStatus = await GitAsync(realGit, "-C", wtPath, "-c", "core.quotepath=off", "status", "-uall", "--porcelain", "--ignored");
        Check(wtExactApply.Success && wtExactApply.Inspection.IsLocalOnly &&
              wtStatus.Contains("dir [1]/#draft!.tmp", StringComparison.Ordinal) &&
              wtStatus.Contains("!! ", StringComparison.Ordinal),
            "Local info/exclude resolved in linked worktree ignores special filename under real Git");

        // Dedupe keeps existing content untouched & skips duplicate write
        File.WriteAllText(rootTarget.FullPath, "# keep comment\n*.ignored\n!important.txt\n", utf8NoBom);
        var dupApply = await IgnoreRuleService.ApplyAsync(wtPath, "*.ignored", rootTarget, journal);
        Check(dupApply.Success && dupApply.SkippedDuplicate &&
              File.ReadAllText(rootTarget.FullPath, utf8NoBom).Split('\n').Count(l => l.Trim() == "*.ignored") == 1,
            "Ignore rule service preserves comments and deduplicates existing rule");

        // Negation rule conflict detection
        var negInspect = await IgnoreRuleService.InspectAsync(wtPath, "*.txt", rootTarget);
        Check(negInspect.NegationConflicts.Contains("!important.txt") &&
              negInspect.MatchedTrackedPaths.Contains("app.txt"),
            "Ignore inspection warns on both conflicting '!' negation rule and already-tracked matching files without running git rm --cached");

        // Confirm tracked file remained tracked in git index!
        var appStillTracked = await GitAsync(realGit, "-C", wtPath, "ls-files", "--", "app.txt");
        Check(!string.IsNullOrWhiteSpace(appStillTracked),
            "Tracked files matched by new ignore rule remain tracked unless manually removed by user");

        // Subdirectory .gitignore write
        var subPresets = IgnoreRuleService.BuildPresetOptions(specialIgnRel, isDirectory: false, subTarget.BaseRelativeDir);
        var subExt = subPresets.Single(p => p.Kind == IgnoreRulePresetKind.ByExtension);
        var subApply = await IgnoreRuleService.ApplyAsync(wtPath, subExt.Pattern, subTarget, journal);
        Check(subApply.Success && File.ReadAllText(subTarget.FullPath, utf8NoBom).Contains("*.tmp", StringComparison.Ordinal),
            "Subdirectory .gitignore receives normalized relative rule cleanly");

        // Cleanup linked worktree before temp sweep
        await GitAsync(realGit, "-C", repo, "worktree", "remove", "--force", wtPath);
    }
}
