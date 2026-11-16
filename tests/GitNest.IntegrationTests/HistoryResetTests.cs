using SourceGit.Commands;
using SourceGit.Models;

internal static partial class Program
{
    private static async Task RunHistoryResetTestsAsync(string realGit, string root)
    {
        var testDir = Path.Combine(root, "history-reset-suite");
        var originBare = Path.Combine(testDir, "origin.git");
        var mirrorBare = Path.Combine(testDir, "mirror.git");
        var repo = Path.Combine(testDir, "repo");
        var wtPath = Path.Combine(testDir, "wt-busy");
        Directory.CreateDirectory(testDir);

        var journal = new OperationJournalStore(Path.Combine(testDir, "journal.json"));

        await InitBareRepoAsync(realGit, originBare);
        await InitBareRepoAsync(realGit, mirrorBare);
        await GitAsync(realGit, "clone", originBare, repo);
        await GitInAsync(realGit, repo, "config", "--local", "user.name", "Alice");
        await GitInAsync(realGit, repo, "config", "--local", "user.email", "alice@example.invalid");

        // 1. Unborn HEAD & Root commit guards
        var unbornPre = await HistoryResetService.InspectCommitResetAsync(repo, "HEAD");
        Check(unbornPre.IsUnbornHead && !unbornPre.CanResetLocally,
              "Unborn HEAD blocks commit reset cleanly");

        await File.WriteAllTextAsync(Path.Combine(repo, ".gitignore"), "*.ignored.log\nignored-dir/\n", Utf8NoBom);
        await File.WriteAllTextAsync(Path.Combine(repo, "app.txt"), "v1\n", Utf8NoBom);
        await File.WriteAllTextAsync(Path.Combine(repo, "collide.txt"), "from c1\n", Utf8NoBom);
        await File.WriteAllTextAsync(Path.Combine(repo, "old.ignored.log"), "committed in c1\n", Utf8NoBom);
        await GitInAsync(realGit, repo, "add", "-f", ".gitignore", "app.txt", "collide.txt", "old.ignored.log");
        await GitInAsync(realGit, repo, "commit", "-m", "C1 root");
        var c1 = (await GitInAsync(realGit, repo, "rev-parse", "HEAD")).Trim();

        var rootHeadPre = await HistoryResetService.InspectCommitResetAsync(repo, c1, probeRemote: false);
        var rootParentPre = await HistoryResetService.InspectCommitResetAsync(repo, $"{c1}^", probeRemote: false);
        var emptyTreePre = await HistoryResetService.InspectCommitResetAsync(repo, "4b825dc642cb6eb9a060e54bf8d69288fbee4904", probeRemote: false);
        Check(rootHeadPre.HeadIsRootCommit && !rootHeadPre.CanResetLocally &&
              !rootParentPre.CanResetLocally && !emptyTreePre.CanResetLocally,
              "Root commit cannot be undone into an empty repository");

        await GitInAsync(realGit, repo, "push", "-u", "origin", "main");

        // Build C2, side branch B1 (by Bob), merge M, and C3
        await File.WriteAllTextAsync(Path.Combine(repo, "app.txt"), "v2\n", Utf8NoBom);
        await GitInAsync(realGit, repo, "rm", "collide.txt", "old.ignored.log");
        await GitInAsync(realGit, repo, "commit", "-am", "C2 remove collide files");
        var c2 = (await GitInAsync(realGit, repo, "rev-parse", "HEAD")).Trim();

        await GitInAsync(realGit, repo, "checkout", "-b", "side-feat", c1);
        await GitInAsync(realGit, repo, "config", "--local", "user.name", "Bob");
        await GitInAsync(realGit, repo, "config", "--local", "user.email", "bob@example.invalid");
        var b1 = await CommitFileAsync(realGit, repo, "side.txt", "bob feature\n", "B1 Bob side commit");

        await GitInAsync(realGit, repo, "checkout", "main");
        await GitInAsync(realGit, repo, "config", "--local", "user.name", "Alice");
        await GitInAsync(realGit, repo, "config", "--local", "user.email", "alice@example.invalid");
        await GitInAsync(realGit, repo, "merge", "--no-ff", "side-feat", "-m", "M merge Bob feature");
        var c3 = await CommitFileAsync(realGit, repo, "app.txt", "v3\n", "C3 after merge");
        await GitInAsync(realGit, repo, "branch", "-D", "side-feat");
        await GitInAsync(realGit, repo, "push", "origin", "main");

        // 2. Detached HEAD & non-first-parent blocks + multi-author first-parent preview
        await GitInAsync(realGit, repo, "checkout", "--detach", c3);
        var detachedPre = await HistoryResetService.InspectCommitResetAsync(repo, c2, probeRemote: false);
        Check(detachedPre.IsDetachedHead && !detachedPre.CanResetLocally, "Detached HEAD blocks commit reset");
        await GitInAsync(realGit, repo, "checkout", "main");

        var sidePre = await HistoryResetService.InspectCommitResetAsync(repo, b1, probeRemote: false);
        var sideOutcome = await HistoryResetService.ExecuteCommitResetAsync(repo, new HistoryResetOptions { Preflight = sidePre }, journal);
        Check(!sidePre.IsOnFirstParentChain && !sidePre.CanResetLocally && sideOutcome.BlockedByNonFirstParent,
              "Merged side-branch commit outside --first-parent chain is strictly rejected");

        var fpPre = await HistoryResetService.InspectCommitResetAsync(repo, c2, probeRemote: false);
        Check(fpPre.IsOnFirstParentChain && fpPre.CanResetLocally &&
              fpPre.LocalCommitsToRemove.Count == 3 &&
              fpPre.LocalCommitsToRemove.Any(c => c.Sha == b1 && !c.IsFirstParent) &&
              fpPre.MergedSideCommitsToRemoveCount == 1 &&
              fpPre.HasOtherAuthorsInLocal &&
              fpPre.LocalAuthors.Any(a => a.Name == "Alice") &&
              fpPre.LocalAuthors.Any(a => a.Name == "Bob"),
              "First-parent reset preview computes full target..HEAD range across merge nodes with multi-author attribution");

        // 3. Clean worktree & untracked/.gitignore-ignored collision guards
        var ignoredKeepDir = Path.Combine(repo, "ignored-dir");
        Directory.CreateDirectory(ignoredKeepDir);
        var ignoredSafeFile = Path.Combine(ignoredKeepDir, "keep-me.bin");
        await File.WriteAllTextAsync(ignoredSafeFile, "must survive reset", Utf8NoBom);

        await File.WriteAllTextAsync(Path.Combine(repo, "app.txt"), "dirty unstaged\n", Utf8NoBom);
        var dirtyPre = await HistoryResetService.InspectCommitResetAsync(repo, c2, probeRemote: false);
        Check(!dirtyPre.CanResetLocally && dirtyPre.HasUnstagedChanges, "Dirty working tree blocks commit reset");
        await GitInAsync(realGit, repo, "checkout", "--", "app.txt");

        await File.WriteAllTextAsync(Path.Combine(repo, "collide.txt"), "untracked user work", Utf8NoBom);
        var untrackedCollidePre = await HistoryResetService.InspectCommitResetAsync(repo, c1, probeRemote: false);
        var untrackedCollideOut = await HistoryResetService.ExecuteCommitResetAsync(
            repo, new HistoryResetOptions { Mode = ResetCommitMode.DiscardCodeHard, Preflight = untrackedCollidePre }, journal);
        Check(!untrackedCollidePre.CanResetLocally &&
              untrackedCollidePre.CollisionPaths.Any(p => p.Contains("collide.txt", StringComparison.OrdinalIgnoreCase)) &&
              untrackedCollideOut.BlockedByUntrackedOrIgnoredCollision &&
              await File.ReadAllTextAsync(Path.Combine(repo, "collide.txt"), Utf8NoBom) == "untracked user work",
              "Untracked file colliding with reset target is detected and never overwritten");
        File.Delete(Path.Combine(repo, "collide.txt"));

        await File.WriteAllTextAsync(Path.Combine(repo, "old.ignored.log"), "important local ignored data", Utf8NoBom);
        var ignoredCollidePre = await HistoryResetService.InspectCommitResetAsync(repo, c1, probeRemote: false);
        Check(!ignoredCollidePre.CanResetLocally &&
              ignoredCollidePre.CollisionPaths.Any(p => p.Contains("old.ignored.log", StringComparison.OrdinalIgnoreCase)) &&
              await File.ReadAllTextAsync(Path.Combine(repo, "old.ignored.log"), Utf8NoBom) == "important local ignored data",
              "Gitignored file colliding with reset target tree blocks reset cleanly");
        File.Delete(Path.Combine(repo, "old.ignored.log"));

        // 4. Multi-remote unpushed disambiguation, --mixed, --hard & checked backup restore
        await GitInAsync(realGit, repo, "checkout", "-b", "feat/reset-flow", c3);
        await GitInAsync(realGit, repo, "push", "-u", "origin", "feat/reset-flow");
        await GitInAsync(realGit, repo, "remote", "add", "mirror", mirrorBare);
        await GitInAsync(realGit, repo, "push", "mirror", "feat/reset-flow");

        var c4 = await CommitFileAsync(realGit, repo, "app.txt", "v4 local\n", "C4 pushed to mirror only");
        await GitInAsync(realGit, repo, "push", "mirror", "feat/reset-flow");

        var multiRemotePre = await HistoryResetService.InspectCommitResetAsync(repo, c2, probeRemote: true);
        var multiRemoteRemotePre = await HistoryResetService.InspectRemoteRewriteForTargetAsync(repo, "feat/reset-flow", c2);
        Check(multiRemotePre.ConfiguredUpstream?.RemoteName == "origin" &&
              multiRemotePre.ConfiguredUpstream.UnpushedCommitCount == 1 &&
              multiRemotePre.LocalCommitsToRemove.Count == 4 &&
              multiRemoteRemotePre.CommitsToRemoveOnRemote.Count == 3 &&
              multiRemotePre.AdditionalTrackedRemotes.Any(r => r.RemoteName == "mirror" && r.RemoteMatchesLocalHead),
              "Multi-remote status isolates configured upstream unpushed count and separates local vs remote ranges");

        var mixedRes = await HistoryResetService.ExecuteCommitResetAsync(
            repo, new HistoryResetOptions { Mode = ResetCommitMode.KeepCodeMixed, Preflight = multiRemotePre }, journal);
        Check(mixedRes.Succeeded &&
              (await GitInAsync(realGit, repo, "rev-parse", "HEAD")).Trim() == c2 &&
              await File.ReadAllTextAsync(Path.Combine(repo, "app.txt"), Utf8NoBom) == "v4 local\n" &&
              File.Exists(ignoredSafeFile) &&
              await BackupRefs.ExistsAsync(repo, mixedRes.BackupRefName),
              "KeepCodeMixed (--mixed) rewinds HEAD while preserving worktree modifications, ignored files, and local backup ref");

        var dirtyRecoveryPre = await HistoryResetService.InspectBackupRestoreForResetAsync(repo, mixedRes.BackupRefName, "feat/reset-flow", mixedRes.OperationId, journal);
        Check(!dirtyRecoveryPre.CanUpdateExistingBranchSafely && dirtyRecoveryPre.CanCreateNewRecoveryBranch,
              "Dirty working tree blocks direct backup overwrite onto existing branch while offering new branch");

        await GitInAsync(realGit, repo, "checkout", "--", ".");
        File.Delete(Path.Combine(repo, "side.txt"));
        var cleanRecoveryPre = await HistoryResetService.InspectBackupRestoreForResetAsync(repo, mixedRes.BackupRefName, "feat/reset-flow", mixedRes.OperationId, journal);
        var restoredExistingRes = await HistoryResetService.RestoreFromBackupAsync(
            repo,
            new BackupResetRestoreOptions { Preflight = cleanRecoveryPre, RestoreOntoExistingBranch = true, SourceOperationId = mixedRes.OperationId },
            journal);
        Check(cleanRecoveryPre.CanUpdateExistingBranchSafely &&
              restoredExistingRes.Succeeded && restoredExistingRes.UpdatedExistingBranch &&
              (await GitInAsync(realGit, repo, "rev-parse", "HEAD")).Trim() == c4 &&
              File.Exists(ignoredSafeFile),
              "Checked backup restore updates existing reset branch when clean and unchanged");

        var preForHard = await HistoryResetService.InspectCommitResetAsync(repo, c2, probeRemote: false);
        var hardRes = await HistoryResetService.ExecuteCommitResetAsync(
            repo, new HistoryResetOptions { Mode = ResetCommitMode.DiscardCodeHard, Preflight = preForHard }, journal);
        Check(hardRes.Succeeded &&
              (await GitInAsync(realGit, repo, "rev-parse", "HEAD")).Trim() == c2 &&
              !File.Exists(Path.Combine(repo, "side.txt")) &&
              await File.ReadAllTextAsync(ignoredSafeFile, Utf8NoBom) == "must survive reset",
              "DiscardCodeHard (--hard) resets tracked files without running git clean on ignored artifacts");

        var d1 = await CommitFileAsync(realGit, repo, "post-reset.txt", "new work d1\n", "D1 new post-reset commit");
        var blockedNewerPre = await HistoryResetService.InspectBackupRestoreForResetAsync(repo, hardRes.BackupRefName, "feat/reset-flow", hardRes.OperationId, journal);
        var blockedOverwriteAttempt = await HistoryResetService.RestoreFromBackupAsync(
            repo,
            new BackupResetRestoreOptions { Preflight = blockedNewerPre, RestoreOntoExistingBranch = true, SourceOperationId = hardRes.OperationId },
            journal);
        var newBranchRestore = await HistoryResetService.RestoreFromBackupAsync(
            repo,
            new BackupResetRestoreOptions { Preflight = blockedNewerPre, RestoreOntoExistingBranch = false, SourceOperationId = hardRes.OperationId },
            journal);
        Check(blockedNewerPre.HasNewerCommitsAfterReset && !blockedNewerPre.CanUpdateExistingBranchSafely &&
              blockedOverwriteAttempt.BlockedByNewerCommits &&
              (await GitInAsync(realGit, repo, "rev-parse", "HEAD")).Trim() == d1 &&
              newBranchRestore.Succeeded &&
              (await GitInAsync(realGit, repo, "rev-parse", $"refs/heads/{newBranchRestore.BranchName}")).Trim() == c4,
              "Subsequent commits block direct branch overwrite and safely recover backup onto new branch");

        // 5. ResetWithoutCheckout guards (current branch, protected branch, busy worktree, idle branch)
        var currentNonCheckoutPre = await HistoryResetService.InspectWithoutCheckoutResetAsync(repo, "feat/reset-flow", c1);
        Check(!currentNonCheckoutPre.CanExecute && currentNonCheckoutPre.IsCurrentBranch,
              "ResetWithoutCheckout blocks direct ref rewrite of currently checked-out branch");

        await GitInAsync(realGit, repo, "checkout", "main");
        await GitInAsync(realGit, repo, "branch", "release", c4);
        await GitInAsync(realGit, repo, "branch", "master", c4);
        var protNonCheckoutPre = await HistoryResetService.InspectWithoutCheckoutResetAsync(repo, "master", c1);
        var protNonCheckoutOut = await HistoryResetService.ExecuteWithoutCheckoutResetAsync(
            repo, new ResetWithoutCheckoutOptions { Preflight = protNonCheckoutPre }, journal);
        Check(!protNonCheckoutPre.CanExecute && protNonCheckoutPre.BlockedByProtection && protNonCheckoutOut.BlockedByProtection &&
              (await GitInAsync(realGit, repo, "rev-parse", "refs/heads/master")).Trim() == c4,
              "ResetWithoutCheckout blocks non-fast-forward rewind of protected branch 'master'");

        await GitInAsync(realGit, repo, "worktree", "add", wtPath, "feat/reset-flow");
        var busyWtPre = await HistoryResetService.InspectWithoutCheckoutResetAsync(repo, "feat/reset-flow", c2);
        var busyWtOut = await HistoryResetService.ExecuteWithoutCheckoutResetAsync(
            repo, new ResetWithoutCheckoutOptions { Preflight = busyWtPre }, journal);
        Check(!busyWtPre.CanExecute && busyWtPre.IsCheckedOutInOtherWorktree && busyWtOut.BlockedByProtection,
              "ResetWithoutCheckout strictly blocks updating a branch checked out in another linked worktree");

        var idlePre = await HistoryResetService.InspectWithoutCheckoutResetAsync(repo, "release", c1);
        var idleOut = await HistoryResetService.ExecuteWithoutCheckoutResetAsync(
            repo, new ResetWithoutCheckoutOptions { Preflight = idlePre }, journal);
        Check(idlePre.CanExecute && idleOut.Succeeded &&
              (await GitInAsync(realGit, repo, "rev-parse", "refs/heads/release")).Trim() == c1 &&
              await BackupRefs.ExistsAsync(repo, idleOut.BackupRefName),
              "ResetWithoutCheckout updates idle branch with atomic ref update and backup creation");

        await GitInAsync(realGit, repo, "worktree", "remove", "--force", wtPath);
    }
}
