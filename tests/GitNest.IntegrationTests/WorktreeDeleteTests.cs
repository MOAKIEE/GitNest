using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using SourceGit.Commands;
using SourceGit.Models;

partial class Program
{
    private static async Task RunWorktreeDeleteTestsAsync(string realGit, string suiteRoot)
    {
        var utf8NoBom = new UTF8Encoding(false);
        var root = Path.Combine(suiteRoot, "worktree-delete-suite");
        Directory.CreateDirectory(root);

        var bare = Path.Combine(root, "origin.git");
        var subBare = Path.Combine(root, "submodule.git");
        var clone1 = Path.Combine(root, "clone1");
        var clone2 = Path.Combine(root, "clone2");
        var journal = new OperationJournalStore(Path.Combine(root, "wt-journal.json"));

        await GitAsync(realGit, "init", "--bare", "-b", "main", bare);
        await GitAsync(realGit, "init", "--bare", "-b", "main", subBare);

        // Seed submodule repo with 1 commit
        var subSeed = Path.Combine(root, "sub-seed");
        await GitAsync(realGit, "clone", subBare, subSeed);
        await GitAsync(realGit, "-C", subSeed, "config", "--local", "user.name", "Alice");
        await GitAsync(realGit, "-C", subSeed, "config", "--local", "user.email", "alice@example.invalid");
        File.WriteAllText(Path.Combine(subSeed, "lib.txt"), "sub\n", utf8NoBom);
        await GitAsync(realGit, "-C", subSeed, "add", ".");
        await GitAsync(realGit, "-C", subSeed, "commit", "-m", "sub initial");
        await GitAsync(realGit, "-C", subSeed, "push", "origin", "main");

        // Seed main repo
        await GitAsync(realGit, "clone", bare, clone1);
        await GitAsync(realGit, "-C", clone1, "config", "--local", "user.name", "Alice");
        await GitAsync(realGit, "-C", clone1, "config", "--local", "user.email", "alice@example.invalid");
        File.WriteAllText(Path.Combine(clone1, "README.md"), "# Worktree Delete Suite\n", utf8NoBom);
        File.WriteAllText(Path.Combine(clone1, ".gitignore"), "*.ignored\nignored-dir/\n", utf8NoBom);
        await GitAsync(realGit, "-C", clone1, "add", ".");
        await GitAsync(realGit, "-C", clone1, "commit", "-m", "initial commit");
        await GitAsync(realGit, "-C", clone1, "push", "-u", "origin", "main");
        var mainSha = (await GitAsync(realGit, "-C", clone1, "rev-parse", "HEAD")).Trim();

        // 1. Main worktree cannot be deleted
        var mainPreflight = await WorktreeDeletionService.InspectWorktreeAsync(clone1, clone1);
        var mainDelete = await WorktreeDeletionService.DeleteWorktreeAsync(
            clone1,
            clone1,
            new WorktreeDeleteOptions { Preflight = mainPreflight, ForceRemoveWorktree = true },
            journal);
        Check(mainPreflight.IsMainWorktree &&
              !mainPreflight.CanDeleteWithoutForce &&
              !mainPreflight.CanForceDeleteWorktree &&
              mainDelete.BlockedByProtection &&
              Directory.Exists(clone1),
            "Main worktree refuses inspection/deletion strictly even with force");

        // 1b. Linked worktree attached to protected branch refuses associated branch cleanup before removing directory
        var wtProtected = Path.Combine(root, "wt-protected-branch");
        await GitInAsync(realGit, clone1, "branch", "develop", mainSha);
        await GitInAsync(realGit, clone1, "worktree", "add", wtProtected, "develop");
        var protectedWtOutcome = await WorktreeDeletionService.DeleteWorktreeAsync(
            clone1,
            wtProtected,
            new WorktreeDeleteOptions { DeleteLocalBranch = true },
            journal);
        Check(!protectedWtOutcome.Succeeded && protectedWtOutcome.BlockedByProtection && Directory.Exists(wtProtected),
              "Task cleanup checks branch protection before removing the associated worktree directory");
        await GitInAsync(realGit, clone1, "worktree", "remove", wtProtected);

        // 2. Clean linked worktree (invoked from its own open tab path!) removes only directory and switches active context to main
        var wtClean = Path.Combine(root, "wt 空格 中文 clean");
        await GitAsync(realGit, "-C", clone1, "checkout", "-b", "feat/keep-branches", mainSha);
        await GitAsync(realGit, "-C", clone1, "push", "-u", "origin", "feat/keep-branches");
        await GitAsync(realGit, "-C", clone1, "checkout", "main");
        await GitAsync(realGit, "-C", clone1, "worktree", "add", wtClean, "feat/keep-branches");

        var closedTargetPath = string.Empty;
        var switchedToMainPath = string.Empty;
        var cleanPreflight = await WorktreeDeletionService.InspectWorktreeAsync(wtClean, wtClean);
        var cleanOutcome = await WorktreeDeletionService.DeleteWorktreeAsync(
            wtClean,
            wtClean,
            new WorktreeDeleteOptions
            {
                Preflight = cleanPreflight,
                DeleteLocalBranch = false,
                BeforeWorktreeRemoveAsync = (mainRoot, target, _) =>
                {
                    switchedToMainPath = mainRoot;
                    closedTargetPath = target;
                    return Task.CompletedTask;
                },
            },
            journal);
        var localKeepExists = (await GitAsync(realGit, "-C", clone1, "branch", "--list", "feat/keep-branches")).Trim();
        var remoteKeepTip = (await GitAsync(realGit, "--git-dir", bare, "rev-parse", "--verify", "refs/heads/feat/keep-branches")).Trim();
        Check(cleanOutcome.Succeeded &&
              cleanOutcome.WorktreeRemoved &&
              !cleanOutcome.LocalBranchDeleted &&
              !cleanOutcome.RemoteBranchDeleted &&
              !Directory.Exists(wtClean) &&
              WorktreeTaskStore.PathsEqual(switchedToMainPath, clone1) &&
              WorktreeTaskStore.PathsEqual(closedTargetPath, wtClean) &&
              !string.IsNullOrEmpty(localKeepExists) &&
              remoteKeepTip == mainSha,
            "Clean linked worktree deletion switches from open worktree tab to main repo and preserves local & remote branches");

        // 3. Ignored-only files worktree MUST stop regular deletion (preventing silent .gitignore loss) until explicit force
        var wtIgnored = Path.Combine(root, "wt-ignored");
        await GitAsync(realGit, "-C", clone1, "worktree", "add", "-b", "feat/ignored-only", wtIgnored, mainSha);
        File.WriteAllText(Path.Combine(wtIgnored, "local-notes.ignored"), "keep me\n", utf8NoBom);
        Directory.CreateDirectory(Path.Combine(wtIgnored, "ignored-dir"));
        File.WriteAllText(Path.Combine(wtIgnored, "ignored-dir", "artifact.txt"), "artifact\n", utf8NoBom);

        var ignoredPreflight = await WorktreeDeletionService.InspectWorktreeAsync(clone1, wtIgnored);
        var ignoredRegularAttempt = await WorktreeDeletionService.DeleteWorktreeAsync(
            clone1,
            wtIgnored,
            new WorktreeDeleteOptions { Preflight = ignoredPreflight, ForceRemoveWorktree = false },
            journal);
        Check(!ignoredPreflight.HasDirtyOrUntracked &&
              ignoredPreflight.HasIgnoredFiles &&
              ignoredPreflight.IgnoredFiles.Count >= 2 &&
              ignoredRegularAttempt.RequiresForceWorktreeConfirmation &&
              Directory.Exists(wtIgnored),
            "Worktree containing only gitignored files blocks default deletion until explicit force confirmation");

        // Snapshot digest invalidates if another ignored/untracked file appears after preflight!
        File.WriteAllText(Path.Combine(wtIgnored, "new-after-preflight.ignored"), "surprise\n", utf8NoBom);
        var ignoredStaleAttempt = await WorktreeDeletionService.DeleteWorktreeAsync(
            clone1,
            wtIgnored,
            new WorktreeDeleteOptions { Preflight = ignoredPreflight, ForceRemoveWorktree = true },
            journal);
        Check(ignoredStaleAttempt.BlockedBySnapshot &&
              Directory.Exists(wtIgnored),
            "Adding new ignored file after preflight invalidates confirmation state and blocks force worktree delete");

        // Verify BranchDeletionService shares the SAME WorktreeDeletionService protection (blocks ignored without force)
        var linkedIgnoredBranchPreflight = await BranchDeletionService.InspectLocalBranchAsync(clone1, "feat/ignored-only");
        var linkedIgnoredBranchBlock = await BranchDeletionService.DeleteLocalBranchAsync(
            clone1,
            "feat/ignored-only",
            new BranchDeleteOptions
            {
                Preflight = linkedIgnoredBranchPreflight,
                DeleteAttachedWorktree = true,
                ForceRemoveWorktree = false,
            },
            journal);
        Check(linkedIgnoredBranchBlock.RequiresForceWorktreeConfirmation &&
              !linkedIgnoredBranchBlock.WorktreeRemoved &&
              Directory.Exists(wtIgnored),
            "Branch deletion attached-worktree cleanup reuses WorktreeDeletionService ignored-file protection");

        var ignoredRefreshed = await WorktreeDeletionService.InspectWorktreeAsync(clone1, wtIgnored);
        var ignoredForced = await WorktreeDeletionService.DeleteWorktreeAsync(
            clone1,
            wtIgnored,
            new WorktreeDeleteOptions { Preflight = ignoredRefreshed, ForceRemoveWorktree = true },
            journal);
        Check(ignoredForced.Succeeded &&
              ignoredForced.WorktreeRemoved &&
              !Directory.Exists(wtIgnored),
            "Confirmed force deletion removes gitignored files worktree after fresh preflight");

        // 4. Locked worktree blocks even force delete (never auto -f -f); explicit Unlock + re-inspect enables delete
        var wtLocked = Path.Combine(root, "wt-locked");
        await GitAsync(realGit, "-C", clone1, "worktree", "add", "-b", "feat/locked-wt", wtLocked, mainSha);
        await GitAsync(realGit, "-C", clone1, "worktree", "lock", "--reason", "AI agent running on laptop", wtLocked);

        var lockedPreflight = await WorktreeDeletionService.InspectWorktreeAsync(clone1, wtLocked);
        var lockedForceAttempt = await WorktreeDeletionService.DeleteWorktreeAsync(
            clone1,
            wtLocked,
            new WorktreeDeleteOptions { Preflight = lockedPreflight, ForceRemoveWorktree = true },
            journal);
        Check(lockedPreflight.IsLocked &&
              lockedPreflight.LockReason.Contains("AI agent running on laptop", StringComparison.Ordinal) &&
              lockedForceAttempt.BlockedByLock &&
              !lockedForceAttempt.WorktreeRemoved &&
              Directory.Exists(wtLocked),
            "Locked worktree blocks deletion even when ForceRemoveWorktree is true without auto-bypassing lock");

        var unlockRes = await WorktreeDeletionService.UnlockWorktreeAsync(clone1, wtLocked);
        var afterUnlockDelete = await WorktreeDeletionService.DeleteWorktreeAsync(
            clone1,
            wtLocked,
            new WorktreeDeleteOptions { Preflight = unlockRes.Preflight },
            journal);
        Check(unlockRes.Succeeded &&
              !unlockRes.Preflight.IsLocked &&
              afterUnlockDelete.Succeeded &&
              !Directory.Exists(wtLocked),
            "Explicit unlock + re-inspection removes previously locked worktree cleanly");

        // 5. Nested Git repo AND Submodule block BOTH regular and force worktree deletion (and shared BranchDeletion)
        var wtNested = Path.Combine(root, "wt-nested");
        await GitAsync(realGit, "-C", clone1, "worktree", "add", "-b", "feat/nested-wt", wtNested, mainSha);
        var nestedRepoDir = Path.Combine(wtNested, "sub-agent-repo");
        Directory.CreateDirectory(nestedRepoDir);
        await GitAsync(realGit, "init", "-b", "main", nestedRepoDir);

        var nestedPreflight = await WorktreeDeletionService.InspectWorktreeAsync(clone1, wtNested);
        var nestedForceAttempt = await WorktreeDeletionService.DeleteWorktreeAsync(
            clone1,
            wtNested,
            new WorktreeDeleteOptions { Preflight = nestedPreflight, ForceRemoveWorktree = true },
            journal);
        var nestedBranchPreflight = await BranchDeletionService.InspectLocalBranchAsync(clone1, "feat/nested-wt");
        var nestedBranchAttempt = await BranchDeletionService.DeleteLocalBranchAsync(
            clone1,
            "feat/nested-wt",
            new BranchDeleteOptions { Preflight = nestedBranchPreflight, DeleteAttachedWorktree = true, ForceRemoveWorktree = true },
            journal);
        Check(nestedPreflight.HasNestedRepoOrSubmodule &&
              !nestedPreflight.CanForceDeleteWorktree &&
              nestedForceAttempt.BlockedByNestedRepoOrSubmodule &&
              nestedBranchAttempt.BlockedByNestedRepoOrSubmodule &&
              Directory.Exists(wtNested),
            "Nested Git repository blocks force worktree deletion across both worktree and branch entry points");

        var wtSubmod = Path.Combine(root, "wt-submod");
        await GitAsync(realGit, "-C", clone1, "worktree", "add", "-b", "feat/submod-wt", wtSubmod, mainSha);
        await GitAsync(realGit, "-C", wtSubmod, "-c", "protocol.file.allow=always", "submodule", "add", subBare, "vendor-lib");
        await GitAsync(realGit, "-C", wtSubmod, "commit", "-m", "add submodule");
        var submodPreflight = await WorktreeDeletionService.InspectWorktreeAsync(clone1, wtSubmod);
        var submodForceAttempt = await WorktreeDeletionService.DeleteWorktreeAsync(
            clone1,
            wtSubmod,
            new WorktreeDeleteOptions { Preflight = submodPreflight, ForceRemoveWorktree = true },
            journal);
        Check(submodPreflight.HasNestedRepoOrSubmodule &&
              submodPreflight.NestedRepoOrSubmodulePaths.Any(p => p.Contains("vendor-lib", StringComparison.Ordinal)) &&
              submodForceAttempt.BlockedByNestedRepoOrSubmodule &&
              Directory.Exists(wtSubmod),
            "Registered submodule inside worktree blocks force deletion as required in v1");

        // 6. Detached HEAD worktree deletion creates local backup ref recoverable into a branch
        var wtDetached = Path.Combine(root, "wt-detached");
        await GitAsync(realGit, "-C", clone1, "worktree", "add", "--detach", wtDetached, mainSha);
        File.WriteAllText(Path.Combine(wtDetached, "detached-work.txt"), "unique detached commit\n", utf8NoBom);
        await GitAsync(realGit, "-C", wtDetached, "add", "detached-work.txt");
        await GitAsync(realGit, "-C", wtDetached, "commit", "-m", "detached solo commit");
        var detachedSha = (await GitAsync(realGit, "-C", wtDetached, "rev-parse", "HEAD")).Trim();

        var detachedPreflight = await WorktreeDeletionService.InspectWorktreeAsync(clone1, wtDetached);
        var detachedOutcome = await WorktreeDeletionService.DeleteWorktreeAsync(
            clone1,
            wtDetached,
            new WorktreeDeleteOptions { Preflight = detachedPreflight },
            journal);
        var restoredDetached = await BranchDeletionService.RestoreDeletedLocalBranchAsync(
            clone1,
            "recovered/from-detached",
            detachedOutcome.DetachedBackupRefName,
            detachedOutcome.OperationId,
            journal);
        var recoveredDetachedSha = (await GitAsync(realGit, "-C", clone1, "rev-parse", "--verify", "refs/heads/recovered/from-detached")).Trim();
        Check(detachedPreflight.IsDetached &&
              detachedPreflight.RequiresDetachedBackup &&
              detachedOutcome.Succeeded &&
              detachedOutcome.WorktreeRemoved &&
              !string.IsNullOrEmpty(detachedOutcome.DetachedBackupRefName) &&
              detachedOutcome.DetachedBackupSha == detachedSha &&
              restoredDetached.Succeeded &&
              recoveredDetachedSha == detachedSha,
            "Detached HEAD worktree creates recoverable backup ref before removal and restores solo commit cleanly");

        // 7. Ordered cleanup (worktree -> local branch -> remote branch) with partial stop on unmerged -d and collaborator lease reject
        var wtUnmerged = Path.Combine(root, "wt-unmerged");
        await GitAsync(realGit, "-C", clone1, "worktree", "add", "-b", "feat/wt-unmerged", wtUnmerged, mainSha);
        File.WriteAllText(Path.Combine(wtUnmerged, "feature.txt"), "v1\n", utf8NoBom);
        await GitAsync(realGit, "-C", wtUnmerged, "add", "feature.txt");
        await GitAsync(realGit, "-C", wtUnmerged, "commit", "-m", "feat v1 pushed");
        await GitAsync(realGit, "-C", wtUnmerged, "push", "-u", "origin", "feat/wt-unmerged");
        var wtUnmergedSha1 = (await GitAsync(realGit, "-C", wtUnmerged, "rev-parse", "HEAD")).Trim();
        File.WriteAllText(Path.Combine(wtUnmerged, "feature.txt"), "v2 unpushed local\n", utf8NoBom);
        await GitAsync(realGit, "-C", wtUnmerged, "add", "feature.txt");
        await GitAsync(realGit, "-C", wtUnmerged, "commit", "-m", "feat unmerged local-only commit");

        // Probe upstream while worktree exists, then run ordered cleanup without ForceDeleteLocalBranch:
        // Step 1 removes worktree; Step 2 (-d) refuses unmerged branch -> partial success, directory stays removed, branch + remote preserved
        var orchPreflight = await WorktreeDeletionService.InspectWorktreeAsync(clone1, wtUnmerged, probeUpstreamRemote: true);
        var step2BlockedOutcome = await WorktreeDeletionService.DeleteWorktreeAsync(
            clone1,
            wtUnmerged,
            new WorktreeDeleteOptions
            {
                Preflight = orchPreflight,
                DeleteLocalBranch = true,
                ForceDeleteLocalBranch = false,
                DeleteLinkedUpstreamRemote = true,
            },
            journal);
        var branchStillPresentAfterStep2Block = (await GitAsync(realGit, "-C", clone1, "branch", "--list", "feat/wt-unmerged")).Trim();
        var serverStillPresentAfterStep2Block = (await GitAsync(realGit, "--git-dir", bare, "rev-parse", "--verify", "refs/heads/feat/wt-unmerged")).Trim();
        Check(!step2BlockedOutcome.Succeeded &&
              step2BlockedOutcome.IsPartialSuccess &&
              step2BlockedOutcome.WorktreeRemoved &&
              !step2BlockedOutcome.LocalBranchDeleted &&
              !step2BlockedOutcome.RemoteBranchDeleted &&
              step2BlockedOutcome.RequiresForceLocalBranchConfirmation &&
              step2BlockedOutcome.UnmergedCommitCount == 2 &&
              !Directory.Exists(wtUnmerged) &&
              !string.IsNullOrEmpty(branchStillPresentAfterStep2Block) &&
              serverStillPresentAfterStep2Block == wtUnmergedSha1,
            "Orchestrated cleanup removes worktree then stops on unmerged -d keeping local & remote branch intact");

        // Now collaborator advances origin/feat/wt-unmerged before Step 3 executes via remaining branch+remote deletion
        await GitAsync(realGit, "clone", bare, clone2);
        await GitAsync(realGit, "-C", clone2, "config", "--local", "user.name", "Bob");
        await GitAsync(realGit, "-C", clone2, "config", "--local", "user.email", "bob@example.invalid");
        await GitAsync(realGit, "-C", clone2, "checkout", "-b", "feat/wt-unmerged", "origin/feat/wt-unmerged");
        File.WriteAllText(Path.Combine(clone2, "feature.txt"), "v2 collaborator\n", utf8NoBom);
        await GitAsync(realGit, "-C", clone2, "add", "feature.txt");
        await GitAsync(realGit, "-C", clone2, "commit", "-m", "collaborator push");
        await GitAsync(realGit, "-C", clone2, "push", "origin", "feat/wt-unmerged");
        var collabSha = (await GitAsync(realGit, "-C", clone2, "rev-parse", "HEAD")).Trim();

        // Confirm local branch deletion after previous partial stop with frozen preflight SHA (wtUnmergedSha1) -> local deleted, lease rejects remote!
        var followUpBranchPreflight = await BranchDeletionService.InspectLocalBranchAsync(clone1, "feat/wt-unmerged", probeLinkedRemote: false);
        var leaseRejectOutcome = await BranchDeletionService.DeleteLocalBranchAsync(
            clone1,
            "feat/wt-unmerged",
            new BranchDeleteOptions
            {
                Preflight = followUpBranchPreflight,
                ForceDeleteLocal = true,
                DeleteLinkedUpstreamRemote = true,
                ConfirmedRemoteExistsOnServer = true,
                ConfirmedExpectedRemoteTipSha = wtUnmergedSha1,
                ConfirmedPushUrl = bare,
            },
            journal);
        var serverAfterLease = (await GitAsync(realGit, "--git-dir", bare, "rev-parse", "--verify", "refs/heads/feat/wt-unmerged")).Trim();
        Check(!leaseRejectOutcome.Succeeded &&
              leaseRejectOutcome.IsPartialSuccess &&
              leaseRejectOutcome.LocalBranchDeleted &&
              !leaseRejectOutcome.RemoteBranchDeleted &&
              leaseRejectOutcome.RejectedByRemoteLease &&
              leaseRejectOutcome.CollaboratorCommitsPreserved &&
              serverAfterLease == collabSha &&
              await BackupRefs.ExistsAsync(clone1, leaseRejectOutcome.BackupRefName),
            "Follow-up deletion enforces frozen remote lease after worktree removal, preserving collaborator commits and backup ref");

        // 8. Dry-run prune preview vs actual prune: missing unlocked pruned; missing LOCKED retained!
        var wtMissingUnlocked = Path.Combine(root, "wt-missing-unlocked");
        var wtMissingLocked = Path.Combine(root, "wt-missing-locked");
        await GitAsync(realGit, "-C", clone1, "worktree", "add", "-b", "feat/missing-unlocked", wtMissingUnlocked, mainSha);
        await GitAsync(realGit, "-C", clone1, "worktree", "add", "-b", "feat/missing-locked", wtMissingLocked, mainSha);
        await GitAsync(realGit, "-C", clone1, "worktree", "lock", "--reason", "USB drive offline", wtMissingLocked);

        Directory.Delete(wtMissingUnlocked, true);
        Directory.Delete(wtMissingLocked, true);

        var prunePreview = await WorktreeDeletionService.PreviewPruneWorktreesAsync(clone1);
        Check(prunePreview.HasCandidates &&
              prunePreview.Candidates.Any(c => WorktreeTaskStore.PathsEqual(c.WorktreePath, wtMissingUnlocked)) &&
              !prunePreview.Candidates.Any(c => WorktreeTaskStore.PathsEqual(c.WorktreePath, wtMissingLocked)) &&
              prunePreview.LockedRetainedItems.Any(r => WorktreeTaskStore.PathsEqual(r.WorktreePath, wtMissingLocked) && r.LockReason.Contains("USB drive offline", StringComparison.Ordinal)),
            "Dry-run prune preview identifies missing unlocked worktrees while retaining locked/offline worktrees");

        var pruneResult = await WorktreeDeletionService.PruneWorktreesAsync(clone1, journal);
        var remainingWorktrees = await new SourceGit.Commands.Worktree(clone1).ReadAllAsync();
        Check(pruneResult.Succeeded &&
              pruneResult.RemovedCount >= 1 &&
              remainingWorktrees.All(w => !WorktreeTaskStore.PathsEqual(w.FullPath, wtMissingUnlocked)) &&
              remainingWorktrees.Any(w => WorktreeTaskStore.PathsEqual(w.FullPath, wtMissingLocked) && w.IsLocked),
            "Confirmed prune removes stale unlocked records and leaves locked missing worktree record untouched");
    }
}
