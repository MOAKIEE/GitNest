using System.Text;
using SourceGit.Commands;
using SourceGit.Models;

internal static partial class Program
{
    private static async Task RunBranchDeleteTestsAsync(string realGit, string suiteRoot)
    {
        var utf8NoBom = new UTF8Encoding(false);
        var bdRoot = Path.Combine(suiteRoot, "branch-delete-tests");
        Directory.CreateDirectory(bdRoot);

        var originBare = Path.Combine(bdRoot, "origin.git");
        var forkBare = Path.Combine(bdRoot, "fork.git");
        var clone1 = Path.Combine(bdRoot, "clone1");
        var clone2 = Path.Combine(bdRoot, "clone2");
        var wtPath = Path.Combine(bdRoot, "wt-task-1");
        var journalFile = Path.Combine(bdRoot, "branch-delete-journal.json");
        var journal = new OperationJournalStore(journalFile);

        await GitAsync(realGit, "init", "--bare", originBare);
        await GitAsync(realGit, "init", "--bare", forkBare);
        await GitAsync(realGit, "clone", originBare, clone1);
        await GitAsync(realGit, "-C", clone1, "config", "--local", "user.name", "Alice");
        await GitAsync(realGit, "-C", clone1, "config", "--local", "user.email", "alice@example.invalid");

        File.WriteAllText(Path.Combine(clone1, "readme.txt"), "base\n", utf8NoBom);
        await GitAsync(realGit, "-C", clone1, "add", "readme.txt");
        await GitAsync(realGit, "-C", clone1, "commit", "-m", "initial commit");
        await GitAsync(realGit, "-C", clone1, "branch", "-M", "main");
        await GitAsync(realGit, "-C", clone1, "push", "-u", "origin", "main");
        var mainSha = (await GitAsync(realGit, "-C", clone1, "rev-parse", "HEAD")).Trim();

        // Push server branches on fork as well (multi-remote scenario)
        await GitAsync(realGit, "-C", clone1, "remote", "add", "fork", forkBare);
        await GitAsync(realGit, "-C", clone1, "push", "fork", "main:refs/heads/main");

        // 1. Upstream resolution: no-upstream branch vs different-name upstream under second remote
        await GitAsync(realGit, "-C", clone1, "checkout", "-b", "local/no-upstream", mainSha);
        // Push same-name branch on origin & fork WITHOUT setting upstream config on local/no-upstream
        await GitAsync(realGit, "-C", clone1, "push", "origin", "local/no-upstream:refs/heads/local/no-upstream");
        await GitAsync(realGit, "-C", clone1, "push", "fork", "local/no-upstream:refs/heads/local/no-upstream");

        await GitAsync(realGit, "-C", clone1, "checkout", "-b", "local/mapped", mainSha);
        File.WriteAllText(Path.Combine(clone1, "fork.txt"), "fork change\n", utf8NoBom);
        await GitAsync(realGit, "-C", clone1, "add", "fork.txt");
        await GitAsync(realGit, "-C", clone1, "commit", "-m", "mapped upstream commit");
        await GitAsync(realGit, "-C", clone1, "push", "fork", "local/mapped:refs/heads/server/topic-9");
        await GitAsync(realGit, "-C", clone1, "fetch", "fork");
        await GitAsync(realGit, "-C", clone1, "branch", "--set-upstream-to=fork/server/topic-9", "local/mapped");
        await GitAsync(realGit, "-C", clone1, "checkout", "main");

        var noUpPreflight = await BranchDeletionService.InspectLocalBranchAsync(clone1, "local/no-upstream");
        var mappedPreflight = await BranchDeletionService.InspectLocalBranchAsync(clone1, "local/mapped", probeLinkedRemote: true);
        Check(!noUpPreflight.HasConfiguredUpstream &&
              string.IsNullOrEmpty(noUpPreflight.RemoteName),
            "Branch without configured upstream does not guess same-name remotes");
        Check(mappedPreflight.HasConfiguredUpstream &&
              mappedPreflight.HasMultipleRemotes &&
              mappedPreflight.RemoteName == "fork" &&
              mappedPreflight.RemoteBranchName == "server/topic-9" &&
              mappedPreflight.IsUpstreamNameDifferent &&
              mappedPreflight.RemoteProbe is { QuerySucceeded: true, ExistsOnServer: true },
            "Upstream resolution identifies different remote and branch name cleanly across multiple remotes");

        // Deleting mapped branch + linked remote deletes both on the exact mapped remote without touching origin
        var mappedDelRes = await BranchDeletionService.DeleteLocalBranchAsync(
            clone1,
            "local/mapped",
            new BranchDeleteOptions
            {
                Preflight = mappedPreflight,
                ForceDeleteLocal = true,
                DeleteLinkedUpstreamRemote = true,
            },
            journal);
        var forkRefsAfter = await GitAsync(realGit, "--git-dir", forkBare, "for-each-ref", "--format=%(refname)", "refs/heads/");
        Check(mappedDelRes.Succeeded &&
              mappedDelRes.LocalBranchDeleted &&
              mappedDelRes.RemoteBranchDeleted &&
              !forkRefsAfter.Contains("refs/heads/server/topic-9", StringComparison.Ordinal),
            "Linked deletion removes exact different-named upstream branch on configured multi-remote target");

        // 2. Protection of current branch & attached worktree (dirty worktree blocks removal & stops branch deletion)
        var currentPreflight = await BranchDeletionService.InspectLocalBranchAsync(clone1, "main");
        var currentBlock = await BranchDeletionService.DeleteLocalBranchAsync(clone1, "main", new BranchDeleteOptions { Preflight = currentPreflight }, journal);
        Check(currentBlock.BlockedByProtection && !currentBlock.LocalBranchDeleted,
            "Deleting current checked-out branch is strictly blocked");

        await GitAsync(realGit, "-C", clone1, "worktree", "add", "-b", "feat/in-wt", wtPath, "main");
        File.WriteAllText(Path.Combine(wtPath, "dirty.txt"), "unsaved\n", utf8NoBom);
        var wtPreflightDirect = await BranchDeletionService.InspectLocalBranchAsync(clone1, "feat/in-wt");
        var wtDirectBlock = await BranchDeletionService.DeleteLocalBranchAsync(clone1, "feat/in-wt", new BranchDeleteOptions { Preflight = wtPreflightDirect }, journal);
        Check(wtPreflightDirect.IsCheckedOutInOtherWorktree &&
              wtPreflightDirect.AttachedWorktreeHasChanges &&
              wtDirectBlock.BlockedByProtection &&
              !wtDirectBlock.LocalBranchDeleted,
            "Deleting branch checked out in another worktree directly is blocked");

        var wtDirtyStop = await BranchDeletionService.DeleteLocalBranchAsync(
            clone1,
            "feat/in-wt",
            new BranchDeleteOptions
            {
                Preflight = wtPreflightDirect,
                DeleteAttachedWorktree = true,
                ForceRemoveWorktree = false,
            },
            journal);
        var branchStillInRepo = await GitAsync(realGit, "-C", clone1, "show-ref", "refs/heads/feat/in-wt");
        Check(!wtDirtyStop.Succeeded &&
              !wtDirtyStop.WorktreeRemoved &&
              !wtDirtyStop.LocalBranchDeleted &&
              Directory.Exists(wtPath) &&
              branchStillInRepo.Contains("feat/in-wt", StringComparison.Ordinal),
            "Dirty linked worktree removal failure stops step sequence before deleting branch");

        // Clean the worktree then run worktree -> local ordered deletion
        File.Delete(Path.Combine(wtPath, "dirty.txt"));
        var wtCleanPreflight = await BranchDeletionService.InspectLocalBranchAsync(clone1, "feat/in-wt");
        var wtOrderedDel = await BranchDeletionService.DeleteLocalBranchAsync(
            clone1,
            "feat/in-wt",
            new BranchDeleteOptions
            {
                Preflight = wtCleanPreflight,
                DeleteAttachedWorktree = true,
            },
            journal);
        var branchExistsAfterWt = await GitAsync(realGit, "-C", clone1, "branch", "--list", "feat/in-wt");
        Check(wtOrderedDel.Succeeded &&
              wtOrderedDel.WorktreeRemoved &&
              wtOrderedDel.LocalBranchDeleted &&
              !Directory.Exists(wtPath) &&
              string.IsNullOrWhiteSpace(branchExistsAfterWt),
            "Explicit worktree cleanup followed by local branch deletion completes cleanly in order");

        // 3. Regular -d check rejects unmerged commits with commit/file details, then explicit force -D & backup restore
        await GitAsync(realGit, "-C", clone1, "checkout", "-b", "feat/unmerged-work", mainSha);
        File.WriteAllText(Path.Combine(clone1, "unmerged.txt"), "important work\n", utf8NoBom);
        await GitAsync(realGit, "-C", clone1, "add", "unmerged.txt");
        await GitAsync(realGit, "-C", clone1, "commit", "-m", "unmerged feature commit");
        var unmergedSha = (await GitAsync(realGit, "-C", clone1, "rev-parse", "HEAD")).Trim();
        await GitAsync(realGit, "-C", clone1, "checkout", "main");

        var unmergedPreflight = await BranchDeletionService.InspectLocalBranchAsync(clone1, "feat/unmerged-work");
        var regularAttempt = await BranchDeletionService.DeleteLocalBranchAsync(
            clone1,
            "feat/unmerged-work",
            new BranchDeleteOptions { Preflight = unmergedPreflight, ForceDeleteLocal = false },
            journal);
        Check(!regularAttempt.Succeeded &&
              regularAttempt.RequiresForceLocalConfirmation &&
              !regularAttempt.LocalBranchDeleted &&
              regularAttempt.UnmergedCommitCount == 1 &&
              regularAttempt.UnmergedChangedFilesCount == 1 &&
              regularAttempt.UnmergedCommits.Any(c => c.Subject.Contains("unmerged feature commit", StringComparison.Ordinal)),
            "Regular delete (-d) refuses unmerged branch and reports exact unmerged commit & file counts");

        // Snapshot invalidation test before force delete
        await GitAsync(realGit, "-C", clone1, "checkout", "feat/unmerged-work");
        File.AppendAllText(Path.Combine(clone1, "unmerged.txt"), "extra line\n", utf8NoBom);
        await GitAsync(realGit, "-C", clone1, "add", "unmerged.txt");
        await GitAsync(realGit, "-C", clone1, "commit", "-m", "external commit advancing branch");
        var unmergedSha2 = (await GitAsync(realGit, "-C", clone1, "rev-parse", "HEAD")).Trim();
        await GitAsync(realGit, "-C", clone1, "checkout", "main");

        var staleSnapshotRes = await BranchDeletionService.DeleteLocalBranchAsync(
            clone1,
            "feat/unmerged-work",
            new BranchDeleteOptions { Preflight = unmergedPreflight, ForceDeleteLocal = true },
            journal);
        Check(staleSnapshotRes.BlockedBySnapshot && !staleSnapshotRes.LocalBranchDeleted,
            "Branch advancement after preflight invalidates confirmation snapshot and blocks deletion");

        // Re-inspect & explicitly force delete (-D), then verify backup restore & non-overwrite guard
        var refreshedUnmergedPreflight = await BranchDeletionService.InspectLocalBranchAsync(clone1, "feat/unmerged-work");
        var forcedDel = await BranchDeletionService.DeleteLocalBranchAsync(
            clone1,
            "feat/unmerged-work",
            new BranchDeleteOptions { Preflight = refreshedUnmergedPreflight, ForceDeleteLocal = true },
            journal);
        Check(forcedDel.Succeeded &&
              forcedDel.LocalBranchDeleted &&
              !string.IsNullOrEmpty(forcedDel.BackupRefName) &&
              await BackupRefs.ExistsAsync(clone1, forcedDel.BackupRefName),
            "Explicit force delete removes unmerged branch cleanly and saves local backup ref");

        await GitAsync(realGit, "-C", clone1, "branch", "feat/unmerged-work", mainSha);
        var blockedRestore = await BranchDeletionService.RestoreDeletedLocalBranchAsync(
            clone1,
            "feat/unmerged-work",
            forcedDel.BackupRefName,
            forcedDel.OperationId,
            journal);
        var renamedRestore = await BranchDeletionService.RestoreDeletedLocalBranchAsync(
            clone1,
            "feat/recovered-unmerged",
            forcedDel.BackupRefName,
            forcedDel.OperationId,
            journal);
        var restoredTip = (await GitAsync(realGit, "-C", clone1, "rev-parse", "refs/heads/feat/recovered-unmerged")).Trim();
        Check(blockedRestore.BlockedByExistingBranch &&
              renamedRestore.Succeeded &&
              restoredTip == unmergedSha2,
            "Backup restore prevents overwriting existing branch and accurately reconstructs tip at new name");

        // 4. Collaborator push + background fetch + explicit lease rejection + partial failure + local recovery
        await GitAsync(realGit, "-C", clone1, "checkout", "-b", "feat/collab-lease", mainSha);
        File.WriteAllText(Path.Combine(clone1, "collab.txt"), "v1\n", utf8NoBom);
        await GitAsync(realGit, "-C", clone1, "add", "collab.txt");
        await GitAsync(realGit, "-C", clone1, "commit", "-m", "collab v1");
        await GitAsync(realGit, "-C", clone1, "push", "-u", "origin", "feat/collab-lease");
        var collabSha1 = (await GitAsync(realGit, "-C", clone1, "rev-parse", "HEAD")).Trim();
        await GitAsync(realGit, "-C", clone1, "checkout", "main");

        // Collaborator clones and pushes v2 AFTER clone1 confirms preflight
        var collabPreflight = await BranchDeletionService.InspectLocalBranchAsync(clone1, "feat/collab-lease", probeLinkedRemote: true);
        await GitAsync(realGit, "clone", originBare, clone2);
        await GitAsync(realGit, "-C", clone2, "config", "--local", "user.name", "Bob");
        await GitAsync(realGit, "-C", clone2, "config", "--local", "user.email", "bob@example.invalid");
        await GitAsync(realGit, "-C", clone2, "checkout", "-b", "feat/collab-lease", "origin/feat/collab-lease");
        File.WriteAllText(Path.Combine(clone2, "collab.txt"), "v2 by collaborator\n", utf8NoBom);
        await GitAsync(realGit, "-C", clone2, "add", "collab.txt");
        await GitAsync(realGit, "-C", clone2, "commit", "-m", "collaborator new commit");
        await GitAsync(realGit, "-C", clone2, "push", "origin", "feat/collab-lease");
        var collabSha2 = (await GitAsync(realGit, "-C", clone2, "rev-parse", "HEAD")).Trim();

        // Background fetch in clone1 updates refs/remotes/origin/feat/collab-lease to collabSha2,
        // yet confirmed ExpectedRemoteTipSha stays frozen at collabSha1!
        await GitAsync(realGit, "-C", clone1, "fetch", "origin");

        var partialOutcome = await BranchDeletionService.DeleteLocalBranchAsync(
            clone1,
            "feat/collab-lease",
            new BranchDeleteOptions
            {
                Preflight = collabPreflight,
                ForceDeleteLocal = true,
                DeleteLinkedUpstreamRemote = true,
            },
            journal);
        var originLeaseTipAfter = (await GitAsync(realGit, "--git-dir", originBare, "rev-parse", "refs/heads/feat/collab-lease")).Trim();
        var journalRecordAfter = journal.TryGet(partialOutcome.OperationId);
        Check(!partialOutcome.Succeeded &&
              partialOutcome.IsPartialSuccess &&
              partialOutcome.LocalBranchDeleted &&
              !partialOutcome.RemoteBranchDeleted &&
              partialOutcome.RejectedByRemoteLease &&
              partialOutcome.CollaboratorCommitsPreserved &&
              partialOutcome.ExpectedRemoteTipSha == collabSha1 &&
              originLeaseTipAfter == collabSha2 &&
              journalRecordAfter is { Stage: GitOperationStage.PartialSuccess } &&
              journalRecordAfter.StepSummary.Contains("本地已删除，远程未删除", StringComparison.Ordinal),
            "Background fetch never overrides confirmed lease; collaborator push rejects remote delete preserving server commit and marks PartialSuccess");

        var recoverFromPartial = await BranchDeletionService.RestoreDeletedLocalBranchAsync(
            clone1,
            "feat/collab-lease",
            partialOutcome.BackupRefName,
            partialOutcome.OperationId,
            journal);
        var restoredCollabTip = (await GitAsync(realGit, "-C", clone1, "rev-parse", "refs/heads/feat/collab-lease")).Trim();
        Check(recoverFromPartial.Succeeded &&
              restoredCollabTip == collabSha1 &&
              journal.TryGet(partialOutcome.OperationId)?.IsAcknowledged == true,
            "Restoring local branch after partial failure recovers original local commit and resolves journal alert");

        // 5. Standalone remote deletion keeps local branch + marks IsUpstreamGone, distinguishes absent cache vs unreachable secret URL
        await GitAsync(realGit, "-C", clone1, "checkout", "-b", "feat/solo-remote", mainSha);
        File.WriteAllText(Path.Combine(clone1, "solo.txt"), "solo\n", utf8NoBom);
        await GitAsync(realGit, "-C", clone1, "add", "solo.txt");
        await GitAsync(realGit, "-C", clone1, "commit", "-m", "solo commit");
        await GitAsync(realGit, "-C", clone1, "push", "-u", "origin", "feat/solo-remote");
        var soloSha = (await GitAsync(realGit, "-C", clone1, "rev-parse", "HEAD")).Trim();
        await GitAsync(realGit, "-C", clone1, "checkout", "main");

        var soloRemotePreflight = await BranchDeletionService.InspectRemoteBranchAsync(clone1, "origin", "feat/solo-remote");
        var soloRemoteDel = await BranchDeletionService.DeleteRemoteBranchAsync(
            clone1,
            "origin",
            "feat/solo-remote",
            new BranchDeleteOptions { Preflight = soloRemotePreflight },
            journal);
        var branchesAfterSoloRemoteDel = await new QueryBranches(clone1).GetResultAsync();
        var localSoloAfter = branchesAfterSoloRemoteDel.FirstOrDefault(b => b.IsLocal && b.Name == "feat/solo-remote");
        Check(soloRemoteDel.Succeeded &&
              soloRemoteDel.RemoteBranchDeleted &&
              localSoloAfter != null &&
              localSoloAfter.Head == soloSha &&
              localSoloAfter.IsUpstreamGone,
            "Standalone remote deletion preserves local branch of same name and marks upstream as gone");

        // Missing server branch should NOT silently masquerade remote tracking cache delete as remote delete
        await GitAsync(realGit, "-C", clone1, "update-ref", "refs/remotes/origin/stale-only", mainSha);
        var staleRemotePreflight = await BranchDeletionService.InspectRemoteBranchAsync(clone1, "origin", "stale-only");
        var staleNeedsConfirm = await BranchDeletionService.DeleteRemoteBranchAsync(
            clone1,
            "origin",
            "stale-only",
            new BranchDeleteOptions { Preflight = staleRemotePreflight, PruneStaleTrackingCacheWhenRemoteAbsent = false },
            journal);
        var staleStillPresent = (await GitAsync(realGit, "-C", clone1, "show-ref", "--verify", "refs/remotes/origin/stale-only")).Trim();
        var staleExplicitPrune = await BranchDeletionService.DeleteRemoteBranchAsync(
            clone1,
            "origin",
            "stale-only",
            new BranchDeleteOptions { Preflight = staleRemotePreflight, PruneStaleTrackingCacheWhenRemoteAbsent = true },
            journal);
        Check(staleNeedsConfirm.RequiresRemoteCachePruneConfirmation &&
              !string.IsNullOrEmpty(staleStillPresent) &&
              staleExplicitPrune.Succeeded &&
              !staleExplicitPrune.RemoteBranchDeleted &&
              staleExplicitPrune.LocalTrackingCachePruned,
            "Remote deletion distinguishes missing server branch from local cache and requires explicit cache-prune confirmation");

        // Unreachable remote with secret URL fails cleanly without deleting local tracking cache or leaking password
        const string remoteSecret = "ghp_RemoteDeleteSecret555XYZ";
        await GitAsync(realGit, "-C", clone1, "remote", "add", "broken", $"https://alice:{remoteSecret}@example.invalid/org/repo.git");
        await GitAsync(realGit, "-C", clone1, "update-ref", "refs/remotes/broken/main", mainSha);
        var brokenPreflight = await BranchDeletionService.InspectRemoteBranchAsync(clone1, "broken", "main");
        var brokenDel = await BranchDeletionService.DeleteRemoteBranchAsync(
            clone1,
            "broken",
            "main",
            new BranchDeleteOptions { Preflight = brokenPreflight },
            journal);
        var brokenCacheStillThere = (await GitAsync(realGit, "-C", clone1, "rev-parse", "--verify", "refs/remotes/broken/main")).Trim();
        var rawJournalAfterBroken = File.ReadAllText(journalFile, Encoding.UTF8);
        Check(!brokenDel.Succeeded &&
              brokenDel.BlockedByProtection &&
              !brokenPreflight.RedactedRemoteUrl.Contains(remoteSecret, StringComparison.Ordinal) &&
              !brokenDel.Summary.Contains(remoteSecret, StringComparison.Ordinal) &&
              !rawJournalAfterBroken.Contains(remoteSecret, StringComparison.Ordinal) &&
              brokenCacheStillThere == mainSha,
            "Unreachable remote URL fails cleanly with redacted credentials and preserves local tracking cache");
    }
}
