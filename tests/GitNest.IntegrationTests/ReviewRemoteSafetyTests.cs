using SourceGit.Commands;
using SourceGit.Models;
using RemoteModel = SourceGit.Models.Remote;

internal static partial class Program
{
    private static async Task RunReviewRemoteSafetyTestsAsync(string git, string suiteRoot)
    {
        var root = Path.Combine(suiteRoot, "review-remote");
        Directory.CreateDirectory(root);
        var repo = Path.Combine(root, "repo");
        var fetch = Path.Combine(root, "fetch.git");
        var server = Path.Combine(root, "push.git");
        var other = Path.Combine(root, "other.git");
        foreach (var bare in new[] { fetch, server, other }) await GitAsync(git, "init", "--bare", bare);
        await GitAsync(git, "init", "-b", "task", repo);
        await GitAsync(git, "-C", repo, "config", "user.name", "Remote Tester");
        await GitAsync(git, "-C", repo, "config", "user.email", "remote@example.invalid");
        var file = Path.Combine(repo, "a.txt");
        File.WriteAllText(file, "base\n");
        await GitAsync(git, "-C", repo, "add", ".");
        await GitAsync(git, "-C", repo, "commit", "-m", "initial");
        var baseSha = (await GitAsync(git, "-C", repo, "rev-parse", "HEAD")).Trim();
        await GitAsync(git, "-C", repo, "remote", "add", "origin", fetch);
        await GitAsync(git, "-C", repo, "push", "origin", "task:task");
        File.AppendAllText(file, "second\n");
        await GitAsync(git, "-C", repo, "commit", "-am", "second");
        var tip = (await GitAsync(git, "-C", repo, "rev-parse", "HEAD")).Trim();
        await GitAsync(git, "-C", repo, "push", server, "task:task");
        await GitAsync(git, "-C", repo, "push", other, "task:task");
        await GitAsync(git, "-C", repo, "remote", "set-url", "--push", "origin", server);
        var journal = new OperationJournalStore(Path.Combine(root, "journal.json"));
        var remote = new RemoteModel { Name = "origin", URL = fetch, PushURL = server };
        async Task<string> Ref(string bare, string name) => (await GitAsync(git, "--git-dir", bare,
            "for-each-ref", "--format=%(objectname)", name)).Trim();

        var probe = await BranchDeletionService.ProbeRemoteBranchAsync(repo, "origin", "task");
        Check(probe.QuerySucceeded && probe.LiveTipSha == tip && probe.PushUrl == server && probe.RedactedRemoteUrl == server,
            "Remote deletion probes the effective push URL rather than the different fetch repository");
        var splitPreflight = await BranchDeletionService.InspectRemoteBranchAsync(repo, "origin", "task");
        var splitDelete = await BranchDeletionService.DeleteRemoteBranchAsync(repo, "origin", "task",
            new BranchDeleteOptions { Preflight = splitPreflight }, journal);
        Check(splitDelete.Succeeded && string.IsNullOrEmpty(await Ref(server, "refs/heads/task")) && await Ref(fetch, "refs/heads/task") == baseSha,
            "Remote deletion removes only the confirmed push repository branch and retains the fetch repository branch");
        Check(!splitDelete.LocalTrackingCachePruned &&
              (await GitAsync(git, "-C", repo, "rev-parse", "refs/remotes/origin/task")).Trim() == baseSha,
            "Deleting a separate push server branch preserves the fetch server tracking cache");
        var splitPrune = await BranchDeletionService.DeleteRemoteBranchAsync(repo, "origin", "task",
            new BranchDeleteOptions { PruneStaleTrackingCacheWhenRemoteAbsent = true }, journal);
        Check(!splitPrune.LocalTrackingCachePruned &&
              (await GitAsync(git, "-C", repo, "rev-parse", "refs/remotes/origin/task")).Trim() == baseSha,
            "An absent push server branch cannot justify pruning a different fetch server cache");
        var splitConfigHash = Hash(Path.Combine(repo, ".git", "config"));
        var splitPush = await GitSyncService.PushSingleBranchAsync(repo,
            new PushExecutionOptions { LocalBranch = "task", RemoteBranch = "task", Remote = remote }, journalStore: journal);
        Check(splitPush.Succeeded && await Ref(server, "refs/heads/task") == tip &&
              (await GitAsync(git, "-C", repo, "rev-parse", "refs/remotes/origin/task")).Trim() == baseSha &&
              Hash(Path.Combine(repo, ".git", "config")) == splitConfigHash,
            "Split-endpoint push preserves fetch tracking and leaves no temporary remote configuration");

        var stale = await BranchDeletionService.InspectRemoteBranchAsync(repo, "origin", "task");
        await GitAsync(git, "-C", repo, "remote", "set-url", "--push", "origin", other);
        var changedDelete = await BranchDeletionService.DeleteRemoteBranchAsync(repo, "origin", "task",
            new BranchDeleteOptions { Preflight = stale }, journal);
        Check(!changedDelete.Succeeded && await Ref(server, "refs/heads/task") == tip && await Ref(other, "refs/heads/task") == tip,
            "Changing a push URL after confirmation blocks deletion on both the original and replacement servers");
        await GitAsync(git, "-C", repo, "remote", "set-url", "--push", "origin", server);
        await new SourceGit.Commands.Branch(repo, "task").SetUpstreamTargetedAsync("origin", "task");
        var resetPreflight = await HistoryResetService.InspectCommitResetAsync(repo, baseSha);
        var rewritePreflight = await HistoryResetService.InspectRemoteRewriteForTargetAsync(repo, "task", baseSha);
        Check(rewritePreflight.CanRewriteRemote && rewritePreflight.LiveRemoteTipSha == tip && rewritePreflight.PushUrl == server,
            "Remote rewrite preflight pins the actual push endpoint and its live tip");
        await GitAsync(git, "-C", repo, "remote", "set-url", "--push", "origin", other);
        var resetBlocked = await HistoryResetService.ExecuteCommitResetAsync(repo,
            new HistoryResetOptions { Preflight = resetPreflight, AlsoRewriteRemote = true, RemoteRewritePreflight = rewritePreflight }, journal);
        Check(!resetBlocked.Succeeded && !resetBlocked.LocalBranchReset &&
              (await GitAsync(git, "-C", repo, "rev-parse", "HEAD")).Trim() == tip,
            "A changed remote endpoint blocks the linked reset before any local history mutation");
        var rewriteBlocked = await HistoryResetService.ExecuteRemoteRewriteOnlyAsync(repo,
            new RemoteRewriteOptions { Preflight = rewritePreflight }, journal);
        Check(!rewriteBlocked.Succeeded && await Ref(other, "refs/heads/task") == tip,
            "Standalone remote rewrite refuses a changed endpoint");
        await GitAsync(git, "-C", repo, "remote", "set-url", "--push", "origin", server);

        splitConfigHash = Hash(Path.Combine(repo, ".git", "config"));
        var splitRewrite = await HistoryResetService.ExecuteRemoteRewriteOnlyAsync(repo,
            new RemoteRewriteOptions { Preflight = await HistoryResetService.InspectRemoteRewriteForTargetAsync(repo, "task", baseSha) }, journal);
        Check(splitRewrite.Succeeded && await Ref(server, "refs/heads/task") == baseSha &&
              (await GitAsync(git, "-C", repo, "rev-parse", "refs/remotes/origin/task")).Trim() == baseSha &&
              Hash(Path.Combine(repo, ".git", "config")) == splitConfigHash,
            "Split-endpoint lease rewrite preserves fetch tracking without persisting its temporary remote");
        await GitAsync(git, "--git-dir", server, "update-ref", "refs/heads/task", tip, baseSha);

        await GitAsync(git, "-C", repo, "remote", "set-url", "--push", "--add", "origin", other);
        var multipleProbe = await BranchDeletionService.ProbeRemoteBranchAsync(repo, "origin", "task");
        var multiplePush = await GitSyncService.PushSingleBranchAsync(repo,
            new PushExecutionOptions { LocalBranch = "task", RemoteBranch = "task", Remote = remote }, journalStore: journal);
        var multipleRevision = await GitSyncService.PushSingleBranchAsync(repo,
            new PushExecutionOptions { LocalBranch = "task", SourceRevisionSha = baseSha, RemoteBranch = "revision-only", Remote = remote }, journalStore: journal);
        var multipleDelete = await BranchDeletionService.DeleteRemoteBranchAsync(repo, "origin", "task", journalStore: journal);
        var multipleRewrite = await HistoryResetService.InspectRemoteRewriteForTargetAsync(repo, "task", baseSha);
        Check(!multipleProbe.QuerySucceeded && !multiplePush.Succeeded && !multipleDelete.Succeeded && !multipleRewrite.CanRewriteRemote &&
              await Ref(server, "refs/heads/task") == tip && await Ref(other, "refs/heads/task") == tip,
            "Multiple push URLs block ordinary push, deletion, and rewrite while preserving every server branch");
        Check(!multipleRevision.Succeeded && string.IsNullOrEmpty(await Ref(server, "refs/heads/revision-only")) &&
              string.IsNullOrEmpty(await Ref(other, "refs/heads/revision-only")),
            "Selected-revision push also rejects multiple push endpoints");
        await GitAsync(git, "-C", repo, "config", "--unset-all", "remote.origin.pushurl");
        await GitAsync(git, "-C", repo, "remote", "set-url", "origin", server);
        await GitAsync(git, "-C", repo, "config", "push.followTags", "true");
        await GitAsync(git, "-C", repo, "tag", "-a", "unrequested-release", "-m", "private tag");
        var pushed = await GitSyncService.PushSingleBranchAsync(repo,
            new PushExecutionOptions { LocalBranch = "task", RemoteBranch = "task", Remote = remote, SetUpstream = true, ConfirmOverwriteExistingRemoteBranchWithoutUpstream = true }, journalStore: journal);
        Check(pushed.Succeeded && pushed.SetUpstreamApplied && string.IsNullOrEmpty(await Ref(server, "refs/tags")) &&
              (await GitAsync(git, "-C", repo, "rev-parse", "--abbrev-ref", "@{upstream}")).Trim() == "origin/task" &&
              (await GitAsync(git, "-C", repo, "config", "push.followTags")).Trim() == "true",
            "Single-branch push suppresses inherited followTags and retains named upstream tracking without changing user config");
        await GitAsync(git, "-C", repo, "tag", "-a", "unrequested-base", "-m", "private base tag", baseSha);
        var selectedRevision = await GitSyncService.PushSingleBranchAsync(repo,
            new PushExecutionOptions { LocalBranch = "task", SourceRevisionSha = baseSha, RemoteBranch = "revision-only", Remote = remote }, journalStore: journal);
        Check(selectedRevision.Succeeded && selectedRevision.HeadSha == baseSha &&
              await Ref(server, "refs/heads/revision-only") == baseSha && string.IsNullOrEmpty(await Ref(server, "refs/tags")),
            "Selected-revision push publishes exactly the chosen commit without newer branch commits or annotated tags");
        var cachePreflight = await BranchDeletionService.InspectRemoteBranchAsync(repo, "origin", "task");
        await GitAsync(git, "-C", repo, "update-ref", "refs/remotes/origin/task", baseSha);
        var cacheChangedDelete = await BranchDeletionService.DeleteRemoteBranchAsync(repo, "origin", "task",
            new BranchDeleteOptions { Preflight = cachePreflight }, journal);
        Check(cacheChangedDelete.Succeeded && !cacheChangedDelete.LocalTrackingCachePruned &&
              (await GitAsync(git, "-C", repo, "rev-parse", "refs/remotes/origin/task")).Trim() == baseSha,
            "Remote deletion preserves a tracking cache changed externally after confirmation");
        await GitAsync(git, "-C", repo, "push", "--no-follow-tags", "origin", "task:task");
        var rewrite = await HistoryResetService.ExecuteRemoteRewriteOnlyAsync(repo,
            new RemoteRewriteOptions { Preflight = await HistoryResetService.InspectRemoteRewriteForTargetAsync(repo, "task", baseSha) }, journal);
        Check(rewrite.Succeeded && await Ref(server, "refs/heads/task") == baseSha && string.IsNullOrEmpty(await Ref(server, "refs/tags")),
            "Lease rewrite sends only its pinned commit and suppresses automatic tag publication");
        var rewriteRecord = journal.TryGet(rewrite.OperationId);
        Check(!string.IsNullOrEmpty(rewriteRecord.RemotePushUrlFingerprint), "Rewrite journal persists an endpoint fingerprint without storing its raw credentialed URL");
        await GitAsync(git, "-C", repo, "remote", "set-url", "origin", other);
        var verifyChanged = await HistoryResetService.VerifyRemoteRewriteJournalRecordAsync(repo, rewriteRecord, journal);
        Check(!verifyChanged.QuerySucceeded, "Restart verification rejects a replacement remote endpoint instead of reconciling against another server");
        await GitAsync(git, "-C", repo, "remote", "set-url", "origin", server);

        foreach (var branch in new[] { "main", "master", "develop", "release-default" })
        {
            await GitAsync(git, "-C", repo, "branch", branch);
            await GitAsync(git, "-C", repo, "push", "--no-follow-tags", server, $"{branch}:{branch}");
        }
        await GitAsync(git, "-C", repo, "fetch", "origin");
        await GitAsync(git, "-C", repo, "symbolic-ref", "refs/remotes/origin/HEAD", "refs/remotes/origin/release-default");
        foreach (var branch in new[] { "main", "master", "develop", "release-default" })
        {
            var localDelete = await BranchDeletionService.DeleteLocalBranchAsync(repo, branch,
                new BranchDeleteOptions { ForceDeleteLocal = true }, journal);
            var remoteDelete = await BranchDeletionService.DeleteRemoteBranchAsync(repo, "origin", branch, journalStore: journal);
            Check(!localDelete.Succeeded && localDelete.BlockedByProtection && !remoteDelete.Succeeded && remoteDelete.BlockedByProtection &&
                  await Ref(server, $"refs/heads/{branch}") == tip,
                $"Default protection blocks both local force deletion and remote deletion for {branch}");
        }
        var wt = Path.Combine(root, "protected-worktree");
        await GitAsync(git, "-C", repo, "worktree", "add", wt, "main");
        var protectedWorktree = await WorktreeDeletionService.DeleteWorktreeAsync(repo, wt,
            new WorktreeDeleteOptions { DeleteLocalBranch = true }, journal);
        Check(!protectedWorktree.Succeeded && protectedWorktree.BlockedByProtection && Directory.Exists(wt),
            "Task cleanup checks branch protection before removing the associated worktree directory");
        await GitAsync(git, "-C", repo, "worktree", "remove", wt);
        await GitAsync(git, "-C", repo, "branch", "feature-linked");
        await new SourceGit.Commands.Branch(repo, "feature-linked").SetUpstreamTargetedAsync("origin", "main");
        var linkedDelete = await BranchDeletionService.DeleteLocalBranchAsync(repo, "feature-linked",
            new BranchDeleteOptions { DeleteLinkedUpstreamRemote = true }, journal);
        Check(!linkedDelete.Succeeded && !linkedDelete.LocalBranchDeleted && linkedDelete.BlockedByProtection,
            "Linked deletion preserves the local branch when its actual upstream is protected");
        var batch = await BranchDeletionService.DeleteMultipleBranchesAsync(repo,
            new[] { "main", "master" }.Select(n => new SourceGit.Models.Branch { Name = n, IsLocal = true }).ToList(), true, true, journal);
        Check(batch.SucceededCount == 0 && batch.SkippedCount == 2, "Batch deletion cannot bypass default branch protection");
        await BranchProtectionPolicy.SetLocalOverrideAsync(repo, "main", true);
        var unlockedPreflight = await BranchDeletionService.InspectLocalBranchAsync(repo, "main");
        await BranchProtectionPolicy.SetLocalOverrideAsync(repo, "main", false);
        var relocked = await BranchDeletionService.DeleteLocalBranchAsync(repo, "main", new BranchDeleteOptions { Preflight = unlockedPreflight }, journal);
        Check(!relocked.Succeeded && relocked.BlockedByProtection, "Deletion rechecks protection changes made after preflight");
        await BranchProtectionPolicy.SetLocalOverrideAsync(repo, "main", true);
        var allowed = await BranchDeletionService.DeleteLocalBranchAsync(repo, "main", journalStore: journal);
        Check(allowed.Succeeded && await BackupRefs.ExistsAsync(repo, allowed.BackupRefName),
            "Explicit repository-local protection opt-out allows deletion with a recovery ref");
    }
}
