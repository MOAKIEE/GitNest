using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using SourceGit.Commands;
using SourceGit.Models;

internal static partial class Program
{
    private static async Task RunHistoryResetRewriteTestsAsync(string realGit, string root)
    {
        var utf8NoBom = new UTF8Encoding(false);
        var testDir = Path.Combine(root, "history-reset-rewrite-tests");
        var originBare = Path.Combine(testDir, "origin.git");
        var mirrorBare = Path.Combine(testDir, "mirror.git");
        var clone1 = Path.Combine(testDir, "clone1");
        var clone2 = Path.Combine(testDir, "clone2");
        var wtPath = Path.Combine(testDir, "wt-busy");
        var journalFile = Path.Combine(testDir, "journal.json");
        Directory.CreateDirectory(testDir);

        var journal = new OperationJournalStore(journalFile);

        // 0. Initialize bare origin with default branch 'release' to verify remote HEAD protection
        await GitAsync(realGit, "init", "--bare", "-b", "release", originBare);
        await GitAsync(realGit, "init", "--bare", "-b", "release", mirrorBare);
        await GitAsync(realGit, "clone", originBare, clone1);
        await GitAsync(realGit, "-C", clone1, "config", "--local", "user.name", "Alice");
        await GitAsync(realGit, "-C", clone1, "config", "--local", "user.email", "alice@example.invalid");

        // Unborn HEAD check
        var unbornPre = await HistoryResetService.InspectCommitResetAsync(clone1, "HEAD");
        Check(unbornPre.IsUnbornHead && !unbornPre.CanResetLocally,
            "Unborn HEAD blocks commit reset cleanly");

        // Create root commit C1
        File.WriteAllText(Path.Combine(clone1, ".gitignore"), "*.ignored.log\nignored-dir/\n", utf8NoBom);
        File.WriteAllText(Path.Combine(clone1, "app.txt"), "v1\n", utf8NoBom);
        File.WriteAllText(Path.Combine(clone1, "collide.txt"), "from c1\n", utf8NoBom);
        File.WriteAllText(Path.Combine(clone1, "old.ignored.log"), "committed in c1\n", utf8NoBom);
        await GitAsync(realGit, "-C", clone1, "add", "-f", ".gitignore", "app.txt", "collide.txt", "old.ignored.log");
        await GitAsync(realGit, "-C", clone1, "commit", "-m", "C1 root");
        var c1 = (await GitAsync(realGit, "-C", clone1, "rev-parse", "HEAD")).Trim();

        // Root HEAD cannot be undone into empty repository
        var rootHeadPre = await HistoryResetService.InspectCommitResetAsync(clone1, c1, probeRemote: false);
        var rootParentPre = await HistoryResetService.InspectCommitResetAsync(clone1, $"{c1}^", probeRemote: false);
        var emptyTreePre = await HistoryResetService.InspectCommitResetAsync(clone1, "4b825dc642cb6eb9a060e54bf8d69288fbee4904", probeRemote: false);
        Check(rootHeadPre.HeadIsRootCommit && !rootHeadPre.CanResetLocally &&
              !rootParentPre.CanResetLocally && !emptyTreePre.CanResetLocally,
            "Root commit cannot be undone into an empty repository");

        await GitAsync(realGit, "-C", clone1, "push", "-u", "origin", "release");

        // Build C2, side branch B1 (by Bob), merge M, and C3
        File.WriteAllText(Path.Combine(clone1, "app.txt"), "v2\n", utf8NoBom);
        await GitAsync(realGit, "-C", clone1, "rm", "collide.txt", "old.ignored.log");
        await GitAsync(realGit, "-C", clone1, "commit", "-am", "C2 remove collide files");
        var c2 = (await GitAsync(realGit, "-C", clone1, "rev-parse", "HEAD")).Trim();

        await GitAsync(realGit, "-C", clone1, "checkout", "-b", "side-feat", c1);
        await GitAsync(realGit, "-C", clone1, "config", "--local", "user.name", "Bob");
        await GitAsync(realGit, "-C", clone1, "config", "--local", "user.email", "bob@example.invalid");
        File.WriteAllText(Path.Combine(clone1, "side.txt"), "bob feature\n", utf8NoBom);
        await GitAsync(realGit, "-C", clone1, "add", "side.txt");
        await GitAsync(realGit, "-C", clone1, "commit", "-m", "B1 Bob side commit");
        var b1 = (await GitAsync(realGit, "-C", clone1, "rev-parse", "HEAD")).Trim();

        await GitAsync(realGit, "-C", clone1, "checkout", "release");
        await GitAsync(realGit, "-C", clone1, "config", "--local", "user.name", "Alice");
        await GitAsync(realGit, "-C", clone1, "config", "--local", "user.email", "alice@example.invalid");
        await GitAsync(realGit, "-C", clone1, "merge", "--no-ff", "side-feat", "-m", "M merge Bob feature");
        var mSha = (await GitAsync(realGit, "-C", clone1, "rev-parse", "HEAD")).Trim();

        File.WriteAllText(Path.Combine(clone1, "app.txt"), "v3\n", utf8NoBom);
        await GitAsync(realGit, "-C", clone1, "commit", "-am", "C3 after merge");
        var c3 = (await GitAsync(realGit, "-C", clone1, "rev-parse", "HEAD")).Trim();
        await GitAsync(realGit, "-C", clone1, "branch", "-D", "side-feat");
        await GitAsync(realGit, "-C", clone1, "push", "origin", "release");

        // Ensure refs/remotes/origin/HEAD points to origin/release in clone1
        await GitAsync(realGit, "-C", clone1, "remote", "set-head", "origin", "release");

        // 1. Detached HEAD blocked & in-progress blocked & first-parent enforcement
        await GitAsync(realGit, "-C", clone1, "checkout", "--detach", c3);
        var detachedPre = await HistoryResetService.InspectCommitResetAsync(clone1, c2, probeRemote: false);
        Check(detachedPre.IsDetachedHead && !detachedPre.CanResetLocally,
            "Detached HEAD blocks commit reset");
        await GitAsync(realGit, "-C", clone1, "checkout", "release");

        // Non-first-parent commit B1 (second parent of merge M) MUST be blocked even though it's an ancestor!
        var sidePre = await HistoryResetService.InspectCommitResetAsync(clone1, b1, probeRemote: false);
        var sideOutcome = await HistoryResetService.ExecuteCommitResetAsync(clone1, new HistoryResetOptions { Preflight = sidePre }, journal);
        Check(!sidePre.IsOnFirstParentChain && !sidePre.CanResetLocally && sideOutcome.BlockedByNonFirstParent,
            "Merged side-branch commit outside --first-parent chain is strictly rejected");

        // First-parent target C2 preview MUST include merged commit B1 + multi-author attribution!
        var fpPre = await HistoryResetService.InspectCommitResetAsync(clone1, c2, probeRemote: false);
        Check(fpPre.IsOnFirstParentChain &&
              fpPre.CanResetLocally &&
              fpPre.LocalCommitsToRemove.Count == 3 &&
              fpPre.LocalCommitsToRemove.Any(c => c.Sha == b1 && !c.IsFirstParent) &&
              fpPre.MergedSideCommitsToRemoveCount == 1 &&
              fpPre.HasOtherAuthorsInLocal &&
              fpPre.LocalAuthors.Any(a => a.Name == "Alice") &&
              fpPre.LocalAuthors.Any(a => a.Name == "Bob"),
            "First-parent reset preview computes full target..HEAD range across merge nodes with multi-author attribution");

        // 2. Clean worktree/index checks + untracked & .gitignore-ignored collision checks (never runs git clean)
        var ignoredKeepDir = Path.Combine(clone1, "ignored-dir");
        Directory.CreateDirectory(ignoredKeepDir);
        var ignoredSafeFile = Path.Combine(ignoredKeepDir, "keep-me.bin");
        File.WriteAllText(ignoredSafeFile, "must survive reset", utf8NoBom);

        // Staged & unstaged tracked changes block reset
        File.WriteAllText(Path.Combine(clone1, "app.txt"), "dirty unstaged\n", utf8NoBom);
        var dirtyPre = await HistoryResetService.InspectCommitResetAsync(clone1, c2, probeRemote: false);
        Check(!dirtyPre.CanResetLocally && dirtyPre.HasUnstagedChanges,
            "Dirty working tree blocks commit reset");
        await GitAsync(realGit, "-C", clone1, "checkout", "--", "app.txt");

        // Untracked collision with C1 (collide.txt existed in C1, deleted in C2, recreated untracked)
        File.WriteAllText(Path.Combine(clone1, "collide.txt"), "untracked user work", utf8NoBom);
        var untrackedCollidePre = await HistoryResetService.InspectCommitResetAsync(clone1, c1, probeRemote: false);
        var untrackedCollideOut = await HistoryResetService.ExecuteCommitResetAsync(clone1, new HistoryResetOptions { Mode = ResetCommitMode.DiscardCodeHard, Preflight = untrackedCollidePre }, journal);
        Check(!untrackedCollidePre.CanResetLocally &&
              untrackedCollidePre.CollisionPaths.Any(p => p.Contains("collide.txt", StringComparison.OrdinalIgnoreCase)) &&
              untrackedCollideOut.BlockedByUntrackedOrIgnoredCollision &&
              File.ReadAllText(Path.Combine(clone1, "collide.txt"), utf8NoBom) == "untracked user work",
            "Untracked file colliding with reset target is detected and never overwritten");
        File.Delete(Path.Combine(clone1, "collide.txt"));

        // .gitignore-ignored file colliding with C1 (old.ignored.log committed in C1, ignored now, recreated on disk)
        File.WriteAllText(Path.Combine(clone1, "old.ignored.log"), "important local ignored data", utf8NoBom);
        var ignoredCollidePre = await HistoryResetService.InspectCommitResetAsync(clone1, c1, probeRemote: false);
        Check(!ignoredCollidePre.CanResetLocally &&
              ignoredCollidePre.CollisionPaths.Any(p => p.Contains("old.ignored.log", StringComparison.OrdinalIgnoreCase)) &&
              File.ReadAllText(Path.Combine(clone1, "old.ignored.log"), utf8NoBom) == "important local ignored data",
            "Gitignored file colliding with reset target tree blocks reset cleanly");
        File.Delete(Path.Combine(clone1, "old.ignored.log"));

        // 3. Multi-remote unpushed disambiguation (origin unpushed U1 vs mirror locally matching HEAD)
        await GitAsync(realGit, "-C", clone1, "checkout", "-b", "feat/reset-flow", c3);
        await GitAsync(realGit, "-C", clone1, "push", "-u", "origin", "feat/reset-flow");
        await GitAsync(realGit, "-C", clone1, "remote", "add", "mirror", mirrorBare);
        await GitAsync(realGit, "-C", clone1, "push", "mirror", "feat/reset-flow");

        // Local-only unpushed commit C4 on feat/reset-flow; mirror still points at C3, wait — push mirror to C4 so mirror matches C4 while origin is behind at C3!
        File.WriteAllText(Path.Combine(clone1, "app.txt"), "v4 local\n", utf8NoBom);
        await GitAsync(realGit, "-C", clone1, "commit", "-am", "C4 pushed to mirror only");
        var c4 = (await GitAsync(realGit, "-C", clone1, "rev-parse", "HEAD")).Trim();
        await GitAsync(realGit, "-C", clone1, "push", "mirror", "feat/reset-flow");

        var multiRemotePre = await HistoryResetService.InspectCommitResetAsync(clone1, c2, probeRemote: true);
        var multiRemoteRemotePre = await HistoryResetService.InspectRemoteRewriteForTargetAsync(clone1, "feat/reset-flow", c2);
        Check(multiRemotePre.ConfiguredUpstream?.RemoteName == "origin" &&
              multiRemotePre.ConfiguredUpstream.UnpushedCommitCount == 1 &&
              multiRemotePre.LocalCommitsToRemove.Count == 4 &&
              multiRemoteRemotePre.CommitsToRemoveOnRemote.Count == 3 &&
              multiRemotePre.AdditionalTrackedRemotes.Any(r => r.RemoteName == "mirror" && r.RemoteMatchesLocalHead),
            "Multi-remote status strictly isolates configured target remote unpushed count and separates local vs remote commit ranges");

        // Reset --mixed keeps C3/B1/C4 changes in working tree + keeps uncolliding ignored file + keeps backup ref!
        var mixedRes = await HistoryResetService.ExecuteCommitResetAsync(
            clone1,
            new HistoryResetOptions { Mode = ResetCommitMode.KeepCodeMixed, Preflight = multiRemotePre },
            journal);
        var headAfterMixed = (await GitAsync(realGit, "-C", clone1, "rev-parse", "HEAD")).Trim();
        var mixedAppText = File.ReadAllText(Path.Combine(clone1, "app.txt"), utf8NoBom);
        Check(mixedRes.Succeeded &&
              headAfterMixed == c2 &&
              mixedAppText == "v4 local\n" &&
              File.Exists(ignoredSafeFile) &&
              await BackupRefs.ExistsAsync(clone1, mixedRes.BackupRefName),
            "KeepCodeMixed (--mixed) rewinds HEAD while preserving worktree modifications, ignored files, and local backup ref");

        // Test recovery onto existing reset branch blocked when worktree is dirty (mixed edits), allowed after cleanup!
        var dirtyRecoveryPre = await HistoryResetService.InspectBackupRestoreForResetAsync(clone1, mixedRes.BackupRefName, "feat/reset-flow", mixedRes.OperationId, journal);
        Check(!dirtyRecoveryPre.CanUpdateExistingBranchSafely && dirtyRecoveryPre.CanCreateNewRecoveryBranch,
            "Dirty working tree blocks direct backup overwrite onto existing branch while offering new branch");

        // Discard mixed changes cleanly so working tree is clean, then restore existing branch back to C4!
        await GitAsync(realGit, "-C", clone1, "checkout", "--", ".");
        File.Delete(Path.Combine(clone1, "side.txt"));
        var cleanRecoveryPre = await HistoryResetService.InspectBackupRestoreForResetAsync(clone1, mixedRes.BackupRefName, "feat/reset-flow", mixedRes.OperationId, journal);
        var restoredExistingRes = await HistoryResetService.RestoreFromBackupAsync(
            clone1,
            new BackupResetRestoreOptions { Preflight = cleanRecoveryPre, RestoreOntoExistingBranch = true, SourceOperationId = mixedRes.OperationId },
            journal);
        var headAfterExistingRestore = (await GitAsync(realGit, "-C", clone1, "rev-parse", "HEAD")).Trim();
        Check(cleanRecoveryPre.CanUpdateExistingBranchSafely &&
              restoredExistingRes.Succeeded &&
              restoredExistingRes.UpdatedExistingBranch &&
              headAfterExistingRestore == c4 &&
              File.Exists(ignoredSafeFile),
            "Checked backup restore updates existing reset branch when clean and unchanged");

        // Reset --hard removes tracked changes cleanly while preserving uncolliding ignored files
        var preForHard = await HistoryResetService.InspectCommitResetAsync(clone1, c2, probeRemote: false);
        var hardRes = await HistoryResetService.ExecuteCommitResetAsync(
            clone1,
            new HistoryResetOptions { Mode = ResetCommitMode.DiscardCodeHard, Preflight = preForHard },
            journal);
        var headAfterHard = (await GitAsync(realGit, "-C", clone1, "rev-parse", "HEAD")).Trim();
        Check(hardRes.Succeeded &&
              headAfterHard == c2 &&
              !File.Exists(Path.Combine(clone1, "side.txt")) &&
              File.ReadAllText(ignoredSafeFile, utf8NoBom) == "must survive reset",
            "DiscardCodeHard (--hard) resets tracked files without running git clean on ignored artifacts");

        // Commit D1 after reset -> restoring original backup MUST refuse overwriting D1 and force new branch!
        File.WriteAllText(Path.Combine(clone1, "post-reset.txt"), "new work d1\n", utf8NoBom);
        await GitAsync(realGit, "-C", clone1, "add", "post-reset.txt");
        await GitAsync(realGit, "-C", clone1, "commit", "-m", "D1 new post-reset commit");
        var d1 = (await GitAsync(realGit, "-C", clone1, "rev-parse", "HEAD")).Trim();

        var blockedNewerPre = await HistoryResetService.InspectBackupRestoreForResetAsync(clone1, hardRes.BackupRefName, "feat/reset-flow", hardRes.OperationId, journal);
        var blockedOverwriteAttempt = await HistoryResetService.RestoreFromBackupAsync(
            clone1,
            new BackupResetRestoreOptions { Preflight = blockedNewerPre, RestoreOntoExistingBranch = true, SourceOperationId = hardRes.OperationId },
            journal);
        var newBranchRestore = await HistoryResetService.RestoreFromBackupAsync(
            clone1,
            new BackupResetRestoreOptions { Preflight = blockedNewerPre, RestoreOntoExistingBranch = false, SourceOperationId = hardRes.OperationId },
            journal);
        Check(blockedNewerPre.HasNewerCommitsAfterReset &&
              !blockedNewerPre.CanUpdateExistingBranchSafely &&
              blockedOverwriteAttempt.BlockedByNewerCommits &&
              (await GitAsync(realGit, "-C", clone1, "rev-parse", "HEAD")).Trim() == d1 &&
              newBranchRestore.Succeeded &&
              (await GitAsync(realGit, "-C", clone1, "rev-parse", $"refs/heads/{newBranchRestore.BranchName}")).Trim() == c4,
            "Subsequent commits block direct branch overwrite and safely recover backup onto new branch");

        // 4. Branch protection default & explicit local override
        var mainProt = await BranchProtectionPolicy.InspectAsync(clone1, "main");
        var masterProt = await BranchProtectionPolicy.InspectAsync(clone1, "master");
        var developProt = await BranchProtectionPolicy.InspectAsync(clone1, "develop");
        var releaseProt = await BranchProtectionPolicy.InspectAsync(clone1, "release");
        Check(mainProt.IsEffectivelyProtected && masterProt.IsEffectivelyProtected &&
              developProt.IsEffectivelyProtected && releaseProt.IsEffectivelyProtected &&
              releaseProt.IsRemoteDefaultBranch,
            "Default branch protection guards main, master, develop, and remote HEAD default branch");

        await GitAsync(realGit, "-C", clone1, "checkout", "release");
        var releaseResetPre = await HistoryResetService.InspectCommitResetAsync(clone1, c2, probeRemote: true);
        var releaseRemotePreBlocked = await HistoryResetService.InspectRemoteRewriteForTargetAsync(clone1, "release", c2);
        var blockedProtExec = await HistoryResetService.ExecuteCommitResetAsync(
            clone1,
            new HistoryResetOptions
            {
                Mode = ResetCommitMode.DiscardCodeHard,
                Preflight = releaseResetPre,
                AlsoRewriteRemote = true,
                RemoteRewritePreflight = releaseRemotePreBlocked,
            },
            journal);
        Check(!releaseRemotePreBlocked.CanRewriteRemote &&
              releaseRemotePreBlocked.BlockedByProtection &&
              blockedProtExec.BlockedByProtection &&
              (await GitAsync(realGit, "-C", clone1, "rev-parse", "HEAD")).Trim() == c3,
            "Protected remote default branch refuses remote history rewrite by default");

        // Explicit local override in clone1 unlocks ONLY release in clone1 (clone2 stays protected!)
        await BranchProtectionPolicy.SetLocalOverrideAsync(clone1, "release", allowDestructiveHistoryRewrite: true);
        await GitAsync(realGit, "clone", originBare, clone2);
        await GitAsync(realGit, "-C", clone2, "config", "--local", "user.name", "Bob");
        await GitAsync(realGit, "-C", clone2, "config", "--local", "user.email", "bob@example.invalid");

        var clone1ReleaseUnlocked = await BranchProtectionPolicy.InspectAsync(clone1, "release");
        var clone1MainStillProt = await BranchProtectionPolicy.InspectAsync(clone1, "main");
        var clone2ReleaseStillProt = await BranchProtectionPolicy.InspectAsync(clone2, "release");
        Check(!clone1ReleaseUnlocked.IsEffectivelyProtected &&
              clone1MainStillProt.IsEffectivelyProtected &&
              clone2ReleaseStillProt.IsEffectivelyProtected,
            "Explicit repository branch-protection opt-out is strictly scoped to one branch in one repository");

        // 5. Offline unknown remote state + diverged remote blocking + credential redaction
        const string secretToken = "ghp_ResetSecret888XYZ";
        await GitAsync(realGit, "-C", clone1, "remote", "add", "broken", $"https://bob:{secretToken}@example.invalid/org/repo.git");
        var brokenPre = await HistoryResetService.InspectRemoteRewriteForTargetAsync(clone1, "release", c2, "broken", "release");
        Check(!brokenPre.CanRewriteRemote &&
              brokenPre.RemoteStateUnknown &&
              brokenPre.BlockReason.Contains("远端状态未知", StringComparison.Ordinal) &&
              !brokenPre.BlockReason.Contains(secretToken, StringComparison.Ordinal) &&
              !brokenPre.RedactedRemoteUrl.Contains(secretToken, StringComparison.Ordinal),
            "Offline or unreachable remote reports '远端状态未知', blocks rewrite, and redacts credentials");

        // Collaborator pushes C5 to clone2 -> origin/release BEFORE rewrite preflight -> diverged block!
        await GitAsync(realGit, "-C", clone2, "checkout", "release");
        File.WriteAllText(Path.Combine(clone2, "collab-diverge.txt"), "from bob\n", utf8NoBom);
        await GitAsync(realGit, "-C", clone2, "add", "collab-diverge.txt");
        await GitAsync(realGit, "-C", clone2, "commit", "-m", "C5 collaborator unintegrated commit");
        await GitAsync(realGit, "-C", clone2, "push", "origin", "release");
        var c5Collab = (await GitAsync(realGit, "-C", clone2, "rev-parse", "HEAD")).Trim();

        var divergedPre = await HistoryResetService.InspectRemoteRewriteForTargetAsync(clone1, "release", c2);
        Check(!divergedPre.CanRewriteRemote &&
              divergedPre.RemoteDivergedWithUnintegratedCommits &&
              divergedPre.LiveRemoteTipSha == c5Collab,
            "Diverged or unintegrated collaborator commits on remote stop remote history rewrite before mutation");

        // Integrate origin/release back, or test on dedicated feature branch feat/lease-rewrite for post-confirm race!
        await GitAsync(realGit, "-C", clone1, "checkout", "-b", "feat/lease-rewrite", c2);
        File.WriteAllText(Path.Combine(clone1, "lease.txt"), "commit A\n", utf8NoBom);
        await GitAsync(realGit, "-C", clone1, "add", "lease.txt");
        await GitAsync(realGit, "-C", clone1, "commit", "-m", "L1");
        var l1 = (await GitAsync(realGit, "-C", clone1, "rev-parse", "HEAD")).Trim();
        File.WriteAllText(Path.Combine(clone1, "lease.txt"), "commit B\n", utf8NoBom);
        await GitAsync(realGit, "-C", clone1, "commit", "-am", "L2");
        var l2 = (await GitAsync(realGit, "-C", clone1, "rev-parse", "HEAD")).Trim();
        await GitAsync(realGit, "-C", clone1, "push", "-u", "origin", "feat/lease-rewrite");

        // Confirm preflight pinned at L2
        var leaseLocalPre = await HistoryResetService.InspectCommitResetAsync(clone1, c2, probeRemote: true);
        var leaseRemotePre = await HistoryResetService.InspectRemoteRewriteForTargetAsync(clone1, "feat/lease-rewrite", c2);

        // Collaborator pushes L3 AFTER confirmation, AND background fetch updates clone1 refs/remotes/origin/feat/lease-rewrite -> L3!
        await GitAsync(realGit, "-C", clone2, "fetch", "origin");
        await GitAsync(realGit, "-C", clone2, "checkout", "-b", "feat/lease-rewrite", "origin/feat/lease-rewrite");
        File.WriteAllText(Path.Combine(clone2, "collab-race.txt"), "L3 collaborator\n", utf8NoBom);
        await GitAsync(realGit, "-C", clone2, "add", "collab-race.txt");
        await GitAsync(realGit, "-C", clone2, "commit", "-m", "L3 post-confirm push");
        await GitAsync(realGit, "-C", clone2, "push", "origin", "feat/lease-rewrite");
        var l3 = (await GitAsync(realGit, "-C", clone2, "rev-parse", "HEAD")).Trim();
        await GitAsync(realGit, "-C", clone1, "fetch", "origin");

        var leaseRaceOut = await HistoryResetService.ExecuteCommitResetAsync(
            clone1,
            new HistoryResetOptions
            {
                Mode = ResetCommitMode.DiscardCodeHard,
                Preflight = leaseLocalPre,
                AlsoRewriteRemote = true,
                RemoteRewritePreflight = leaseRemotePre,
            },
            journal);
        var originLeaseAfterRace = (await GitAsync(realGit, "--git-dir", originBare, "rev-parse", "refs/heads/feat/lease-rewrite")).Trim();
        var originRefsAll = await GitAsync(realGit, "--git-dir", originBare, "show-ref");
        Check(!leaseRaceOut.Succeeded &&
              leaseRaceOut.IsPartialSuccess &&
              leaseRaceOut.LocalBranchReset &&
              !leaseRaceOut.RemoteBranchRewritten &&
              leaseRaceOut.RejectedByRemoteLease &&
              leaseRaceOut.CollaboratorCommitsPreserved &&
              leaseRaceOut.ExpectedRemoteTipSha == l2 &&
              originLeaseAfterRace == l3 &&
              leaseRaceOut.Summary.Contains("本地已撤回，远端未更新", StringComparison.Ordinal) &&
              !originRefsAll.Contains("refs/gitnest/backups", StringComparison.Ordinal),
            "Pinned --force-with-lease survives background fetch, rejects collaborator post-confirm push, preserves L3, and marks PartialSuccess");

        // Recover feat/lease-rewrite locally back to L2, then perform clean happy-path standalone remote rewrite on feat/clean-rewrite
        var leaseRecoverPre = await HistoryResetService.InspectBackupRestoreForResetAsync(clone1, leaseRaceOut.BackupRefName, "feat/lease-rewrite", leaseRaceOut.OperationId, journal);
        await HistoryResetService.RestoreFromBackupAsync(
            clone1,
            new BackupResetRestoreOptions { Preflight = leaseRecoverPre, RestoreOntoExistingBranch = true, SourceOperationId = leaseRaceOut.OperationId },
            journal);
        Check((await GitAsync(realGit, "-C", clone1, "rev-parse", "HEAD")).Trim() == l2,
            "Recovery after partial remote failure restores feat/lease-rewrite back to original tip");

        await GitAsync(realGit, "-C", clone1, "checkout", "-b", "feat/clean-rewrite", c2);
        File.WriteAllText(Path.Combine(clone1, "clean.txt"), "to be rewritten\n", utf8NoBom);
        await GitAsync(realGit, "-C", clone1, "add", "clean.txt");
        await GitAsync(realGit, "-C", clone1, "commit", "-m", "CR1 rewrite target");
        var cr1 = (await GitAsync(realGit, "-C", clone1, "rev-parse", "HEAD")).Trim();
        await GitAsync(realGit, "-C", clone1, "push", "-u", "origin", "feat/clean-rewrite");

        var happyLocalPre = await HistoryResetService.InspectCommitResetAsync(clone1, c2, probeRemote: true);
        var happyRemotePre = await HistoryResetService.InspectRemoteRewriteForTargetAsync(clone1, "feat/clean-rewrite", c2);
        var happyOut = await HistoryResetService.ExecuteCommitResetAsync(
            clone1,
            new HistoryResetOptions
            {
                Mode = ResetCommitMode.DiscardCodeHard,
                Preflight = happyLocalPre,
                AlsoRewriteRemote = true,
                RemoteRewritePreflight = happyRemotePre,
            },
            journal);
        var originHappyTip = (await GitAsync(realGit, "--git-dir", originBare, "rev-parse", "refs/heads/feat/clean-rewrite")).Trim();
        var originAllRefsHappy = await GitAsync(realGit, "--git-dir", originBare, "show-ref");
        Check(happyOut.Succeeded &&
              happyOut.LocalBranchReset &&
              happyOut.RemoteBranchRewritten &&
              originHappyTip == c2 &&
              await BackupRefs.ExistsAsync(clone1, happyOut.BackupRefName) &&
              !originAllRefsHappy.Contains("refs/gitnest/backups", StringComparison.Ordinal),
            "Clean reset + remote history rewrite updates both local and remote via --force-with-lease without pushing backup refs");

        // 6. Ambiguous network timeout test: single push attempt only (never blind retry), verified immediately via ls-remote!
        await GitAsync(realGit, "-C", clone1, "checkout", "-b", "feat/timeout-probe", c2);
        File.WriteAllText(Path.Combine(clone1, "timeout.txt"), "timeout test\n", utf8NoBom);
        await GitAsync(realGit, "-C", clone1, "add", "timeout.txt");
        await GitAsync(realGit, "-C", clone1, "commit", "-m", "TP1 commit");
        var tp1 = (await GitAsync(realGit, "-C", clone1, "rev-parse", "HEAD")).Trim();
        await GitAsync(realGit, "-C", clone1, "push", "-u", "origin", "feat/timeout-probe");

        await GitAsync(realGit, "-C", clone1, "reset", "--hard", c2);
        var tpRemotePre = await HistoryResetService.InspectRemoteRewriteForTargetAsync(clone1, "feat/timeout-probe", c2);

        int pushInvocationCount = 0;
        HistoryResetService.RemotePushExecutorOverride = async (repo, sshKey, pushArgs, log, ct) =>
        {
            pushInvocationCount++;
            // Simulate server having actually committed c2 even though client connection timed out waiting for OK
            await GitAsync(realGit, "--git-dir", originBare, "update-ref", "refs/heads/feat/timeout-probe", c2);
            return new HistoryResetService.RemotePushRawResult
            {
                IsSuccess = false,
                TimedOutOrConnectionLost = true,
                StdErr = "fatal: early EOF / operation timed out",
            };
        };

        HistoryResetOutcome ambiguousVerifiedOut;
        try
        {
            ambiguousVerifiedOut = await HistoryResetService.ExecuteRemoteRewriteOnlyAsync(
                clone1,
                new RemoteRewriteOptions { Preflight = tpRemotePre },
                journal);
        }
        finally
        {
            HistoryResetService.RemotePushExecutorOverride = null;
        }

        Check(pushInvocationCount == 1 &&
              ambiguousVerifiedOut.Succeeded &&
              ambiguousVerifiedOut.RemoteBranchRewritten &&
              ambiguousVerifiedOut.VerifiedRemoteTipSha == c2 &&
              ambiguousVerifiedOut.StepResults.Any(s => s.Kind == HistoryResetStepKind.RemoteStateProbe && s.Succeeded),
            "Ambiguous timeout does NOT blindly retry push and verifies server outcome directly via ls-remote");

        // Simulated timeout against unreachable remote -> PendingVerification, then reconciled on restart + VerifyRemoteRewriteJournalRecordAsync
        await GitAsync(realGit, "-C", clone1, "checkout", "-b", "feat/pending-verify", cr1);
        await GitAsync(realGit, "-C", clone1, "push", "-u", "origin", "feat/pending-verify");
        var pvPre = await HistoryResetService.InspectCommitResetAsync(clone1, c2, probeRemote: true);
        var pvRemotePre = await HistoryResetService.InspectRemoteRewriteForTargetAsync(clone1, "feat/pending-verify", c2);
        // Point origin url temporarily to invalid after preflight so ls-remote probe fails too? Or directly inspect journal PendingVerification!
        // Instead use RemotePushExecutorOverride + invalid remote URL on temp branch? Better: simulate journal interrupted record directly AND test VerifyRemoteRewriteJournalRecordAsync!
        var uncertainRec = new GitOperationRecord
        {
            OperationId = "op_reset_uncertain_test",
            RepositoryCommonDir = pvPre.CanonicalCommonDir,
            WorktreePath = clone1,
            Kind = GitOperationKind.RewriteRemoteHistory,
            Title = "模拟中断的远程历史改写",
            BranchName = "feat/pending-verify",
            RemoteName = "origin",
            RemoteBranchName = "feat/pending-verify",
            OriginalTipSha = cr1,
            TargetTipSha = c2,
            ExpectedRemoteTipSha = cr1,
            BackupRefName = happyOut.BackupRefName,
            Stage = GitOperationStage.Executing,
        };
        journal.AppendOrUpdate(uncertainRec);
        await GitAsync(realGit, "-C", clone1, "reset", "--hard", c2);

        var openReport = await OperationJournalService.ReconcileOnOpenAsync(clone1, journal);
        var reconciledUncertain = journal.TryGet(uncertainRec.OperationId);
        var stageOnReopen = reconciledUncertain.Stage;
        var noteOnReopen = reconciledUncertain.ReconciliationNote;
        var liveVerify = await HistoryResetService.VerifyRemoteRewriteJournalRecordAsync(clone1, reconciledUncertain, journal);
        Check(openReport.HasPendingAttention &&
              stageOnReopen == GitOperationStage.PendingVerification &&
              (noteOnReopen.Contains('待', StringComparison.Ordinal) ||
               noteOnReopen.Contains("核对", StringComparison.Ordinal)) &&
              liveVerify.QuerySucceeded &&
              liveVerify.LiveRemoteTipSha == cr1 &&
              liveVerify.UpdatedRecord.Stage == GitOperationStage.PartialSuccess &&
              liveVerify.UpdatedRecord.StepSummary.Contains("本地已撤回，远端未更新", StringComparison.Ordinal),
            "Interrupted reset/rewrite record is flagged on reopen and cleanly reconciled via VerifyRemoteRewriteJournalRecordAsync");

        // 7. ResetWithoutCheckout guards (blocks current branch, busy worktree, protected branch back-move; allows idle safe branch)
        var currentNonCheckoutPre = await HistoryResetService.InspectWithoutCheckoutResetAsync(clone1, "feat/pending-verify", c1);
        Check(!currentNonCheckoutPre.CanExecute && currentNonCheckoutPre.IsCurrentBranch,
            "ResetWithoutCheckout blocks direct ref rewrite of currently checked-out branch");

        await GitAsync(realGit, "-C", clone1, "checkout", "release");

        await GitAsync(realGit, "-C", clone1, "branch", "main", cr1);
        var protNonCheckoutPre = await HistoryResetService.InspectWithoutCheckoutResetAsync(clone1, "main", c1);
        var protNonCheckoutOut = await HistoryResetService.ExecuteWithoutCheckoutResetAsync(clone1, new ResetWithoutCheckoutOptions { Preflight = protNonCheckoutPre }, journal);
        Check(!protNonCheckoutPre.CanExecute &&
              protNonCheckoutPre.BlockedByProtection &&
              protNonCheckoutOut.BlockedByProtection &&
              (await GitAsync(realGit, "-C", clone1, "rev-parse", "refs/heads/main")).Trim() == cr1,
            "ResetWithoutCheckout blocks non-fast-forward rewind of protected branch 'main'");
        await GitAsync(realGit, "-C", clone1, "worktree", "add", wtPath, "feat/lease-rewrite");
        var busyWtPre = await HistoryResetService.InspectWithoutCheckoutResetAsync(clone1, "feat/lease-rewrite", c2);
        var busyWtOut = await HistoryResetService.ExecuteWithoutCheckoutResetAsync(clone1, new ResetWithoutCheckoutOptions { Preflight = busyWtPre }, journal);
        Check(!busyWtPre.CanExecute && busyWtPre.IsCheckedOutInOtherWorktree && busyWtOut.BlockedByProtection,
            "ResetWithoutCheckout strictly blocks updating a branch checked out in another linked worktree");

        var idlePre = await HistoryResetService.InspectWithoutCheckoutResetAsync(clone1, "feat/pending-verify", c1);
        var idleOut = await HistoryResetService.ExecuteWithoutCheckoutResetAsync(clone1, new ResetWithoutCheckoutOptions { Preflight = idlePre }, journal);
        Check(idlePre.CanExecute &&
              idleOut.Succeeded &&
              (await GitAsync(realGit, "-C", clone1, "rev-parse", "refs/heads/feat/pending-verify")).Trim() == c1 &&
              await BackupRefs.ExistsAsync(clone1, idleOut.BackupRefName),
            "ResetWithoutCheckout updates idle branch with atomic ref update and backup creation");

        await GitAsync(realGit, "-C", clone1, "worktree", "remove", "--force", wtPath);
    }
}
