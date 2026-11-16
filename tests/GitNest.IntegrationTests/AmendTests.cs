using SourceGit.Commands;
using SourceGit.Models;
using VM = SourceGit.ViewModels;

internal static partial class Program
{
    private static async Task RunAmendTestsAsync(string realGit, string root)
    {
        var wcType = typeof(VM.WorkingCopy);
        Check(wcType.GetProperty("UseAmend") == null &&
              wcType.GetProperty("ResetAuthor") == null &&
              wcType.GetMethod("OpenAmendLastCommit") != null &&
              wcType.GetProperty("CanOpenAmendLastCommit") != null,
              "WorkingCopy exposes dedicated OpenAmendLastCommit entry point without inline Amend properties");

        var amendRoot = Path.Combine(root, "amend-suite");
        var originBare = Path.Combine(amendRoot, "origin.git");
        var originMoved = Path.Combine(amendRoot, "origin-offline.git");
        var cloneA = Path.Combine(amendRoot, "clone-a");
        var cloneB = Path.Combine(amendRoot, "clone-b");
        Directory.CreateDirectory(amendRoot);

        await InitBareRepoAsync(realGit, originBare);
        await GitAsync(realGit, "clone", originBare, cloneA);
        await GitInAsync(realGit, cloneA, "config", "user.name", "Alice");
        await GitInAsync(realGit, cloneA, "config", "user.email", "alice@example.invalid");
        await CommitFileAsync(realGit, cloneA, "README.md", "base\n", "chore: base");
        await GitInAsync(realGit, cloneA, "push", "-u", "origin", "main");
        await GitAsync(realGit, "clone", originBare, cloneB);
        await GitInAsync(realGit, cloneB, "config", "user.name", "Bob Collaborator");
        await GitInAsync(realGit, cloneB, "config", "user.email", "bob@example.invalid");

        var journalStore = new OperationJournalStore(Path.Combine(amendRoot, "amend-journal.json"));

        // 1. Unpushed local branch Amend succeeds, preserves history depth, creates backup ref, supports signoff/reset-author
        await GitInAsync(realGit, cloneA, "checkout", "-b", "feat/amend-local");
        var localBeforeSha = await CommitFileAsync(realGit, cloneA, "local.txt", "v1\n", "feat: local amend target");
        var countBefore = int.Parse((await GitInAsync(realGit, cloneA, "rev-list", "--count", "HEAD")).Trim());

        await File.WriteAllTextAsync(Path.Combine(cloneA, "local.txt"), "v2\n", Utf8NoBom);
        await GitInAsync(realGit, cloneA, "add", "local.txt");

        var unpushedPre = await GitSyncService.InspectAmendLastCommitAsync(cloneA);
        Check(!unpushedPre.HasHardBlocker &&
              unpushedPre.RemotePublishState == AmendRemotePublishState.UnpushedLocalOnly &&
              unpushedPre.CanAmendDirectly &&
              unpushedPre.NewlyStagedChanges.Any(c => c.Path == "local.txt"),
              "Unpushed feature branch preflight confirms UnpushedLocalOnly and lists newly staged file");

        var localAmendOut = await GitSyncService.ExecuteAmendLastCommitAsync(
            cloneA,
            new AmendLastCommitExecutionOptions
            {
                Preflight = unpushedPre,
                Message = "feat: local amend updated",
                SignOff = true,
                ResetAuthor = true,
                RewritePushedRemoteWithLease = false,
            },
            journalStore: journalStore);
        var localAfterSha = (await GitInAsync(realGit, cloneA, "rev-parse", "HEAD")).Trim();
        var countAfter = int.Parse((await GitInAsync(realGit, cloneA, "rev-list", "--count", "HEAD")).Trim());
        var localMsg = (await GitInAsync(realGit, cloneA, "log", "-1", "--format=%B")).Trim();
        var backupSha = (await GitInAsync(realGit, cloneA, "rev-parse", "--verify", localAmendOut.BackupRefName)).Trim();

        Check(localAmendOut.Succeeded &&
              localAmendOut.AmendSucceeded &&
              !localAmendOut.PushAttempted &&
              countAfter == countBefore &&
              localAfterSha != localBeforeSha &&
              backupSha == localBeforeSha &&
              localMsg.Contains("Signed-off-by: Alice <alice@example.invalid>", StringComparison.Ordinal),
              "Local unpushed Amend updates HEAD in place with SignOff and retains original SHA in backup ref");

        // 2. UnknownRemoteState (unreachable remote) refuses silent Amend
        await GitInAsync(realGit, cloneA, "checkout", "-b", "feat/amend-unknown");
        await GitInAsync(realGit, cloneA, "remote", "add", "broken", Path.Combine(amendRoot, "does-not-exist.git"));
        await GitInAsync(realGit, cloneA, "config", "branch.feat/amend-unknown.remote", "broken");
        await GitInAsync(realGit, cloneA, "config", "branch.feat/amend-unknown.merge", "refs/heads/feat/amend-unknown");
        await File.WriteAllTextAsync(Path.Combine(cloneA, "unk.txt"), "1\n", Utf8NoBom);
        await GitInAsync(realGit, cloneA, "add", "unk.txt");
        var headBeforeUnknown = (await GitInAsync(realGit, cloneA, "rev-parse", "HEAD")).Trim();
        var unknownPre = await GitSyncService.InspectAmendLastCommitAsync(cloneA);
        var unknownExec = await GitSyncService.ExecuteAmendLastCommitAsync(
            cloneA,
            new AmendLastCommitExecutionOptions { Preflight = unknownPre, Message = "should fail" },
            journalStore: journalStore);
        Check(unknownPre.RemotePublishState == AmendRemotePublishState.UnknownRemoteState &&
              !unknownPre.CanAmendDirectly &&
              !unknownExec.Succeeded &&
              unknownExec.BlockedByRemoteState &&
              (await GitInAsync(realGit, cloneA, "rev-parse", "HEAD")).Trim() == headBeforeUnknown,
              "Unknown/offline remote state refuses Amend and leaves HEAD untouched");
        await GitInAsync(realGit, cloneA, "remote", "remove", "broken");

        // 3. UnfetchedOrStaleRemote refuses direct Amend until fetched
        await GitInAsync(realGit, cloneA, "checkout", "feat/amend-local");
        await GitInAsync(realGit, cloneA, "push", "-u", "origin", "feat/amend-local");
        await GitInAsync(realGit, cloneB, "fetch", "origin");
        await GitInAsync(realGit, cloneB, "checkout", "-B", "feat/amend-local", "origin/feat/amend-local");
        await CommitFileAsync(realGit, cloneB, "bob-unfetched.txt", "bob\n", "bob commit ahead on server");
        await GitInAsync(realGit, cloneB, "push", "origin", "feat/amend-local");

        await File.WriteAllTextAsync(Path.Combine(cloneA, "stale-stage.txt"), "s\n", Utf8NoBom);
        await GitInAsync(realGit, cloneA, "add", "stale-stage.txt");
        var unfetchedPre = await GitSyncService.InspectAmendLastCommitAsync(cloneA);
        var unfetchedExec = await GitSyncService.ExecuteAmendLastCommitAsync(
            cloneA,
            new AmendLastCommitExecutionOptions { Preflight = unfetchedPre, Message = "blocked stale" },
            journalStore: journalStore);
        Check(unfetchedPre.RemotePublishState == AmendRemotePublishState.UnfetchedOrStaleRemote &&
              !unfetchedExec.Succeeded &&
              unfetchedExec.BlockedByRemoteState &&
              (await GitInAsync(realGit, cloneA, "rev-parse", "HEAD")).Trim() == localAfterSha,
              "Unfetched remote commits block silent Amend with UnfetchedOrStaleRemote");

        // 4. Protected branch 'main' AlreadyPushed blocks rewrite even with RewritePushedRemoteWithLease=true
        await GitInAsync(realGit, cloneA, "reset", "--hard");
        await GitInAsync(realGit, cloneA, "checkout", "main");
        await GitInAsync(realGit, cloneA, "fetch", "origin");
        await File.WriteAllTextAsync(Path.Combine(cloneA, "main-edit.txt"), "blocked\n", Utf8NoBom);
        await GitInAsync(realGit, cloneA, "add", "main-edit.txt");
        var mainProtectedPre = await GitSyncService.InspectAmendLastCommitAsync(cloneA);
        var mainProtectedExec = await GitSyncService.ExecuteAmendLastCommitAsync(
            cloneA,
            new AmendLastCommitExecutionOptions
            {
                Preflight = mainProtectedPre,
                Message = "try rewrite main",
                RewritePushedRemoteWithLease = true,
            },
            journalStore: journalStore);
        Check(mainProtectedPre.RemotePublishState == AmendRemotePublishState.AlreadyPushed &&
              mainProtectedPre.BlockedByBranchProtection &&
              !mainProtectedPre.CanOptInPushedRewrite &&
              !mainProtectedExec.Succeeded &&
              mainProtectedExec.BlockedByProtection,
              "Protected branch 'main' blocks pushed Amend rewrite under BranchProtectionPolicy");
        await GitInAsync(realGit, cloneA, "reset", "--hard");

        // 5. Unlocked pushed feature branch: default block -> opt-in lease push -> collaborator race -> outage retry
        await GitInAsync(realGit, cloneA, "checkout", "-b", "feat/amend-lease");
        await CommitFileAsync(realGit, cloneA, "lease.txt", "v1\n", "feat: lease base");
        await GitInAsync(realGit, cloneA, "push", "-u", "origin", "feat/amend-lease");
        await GitInAsync(realGit, cloneA, "fetch", "origin");

        await File.WriteAllTextAsync(Path.Combine(cloneA, "lease.txt"), "v2\n", Utf8NoBom);
        await GitInAsync(realGit, cloneA, "add", "lease.txt");
        var leasePre1 = await GitSyncService.InspectAmendLastCommitAsync(cloneA);
        var defaultBlockedExec = await GitSyncService.ExecuteAmendLastCommitAsync(
            cloneA,
            new AmendLastCommitExecutionOptions
            {
                Preflight = leasePre1,
                Message = "default should block",
                RewritePushedRemoteWithLease = false,
            },
            journalStore: journalStore);
        Check(leasePre1.RemotePublishState == AmendRemotePublishState.AlreadyPushed &&
              leasePre1.CanOptInPushedRewrite &&
              !defaultBlockedExec.Succeeded &&
              defaultBlockedExec.BlockedByRemoteState,
              "AlreadyPushed branch blocks Amend unless explicit lease rewrite opt-in is passed");

        var optInExec = await GitSyncService.ExecuteAmendLastCommitAsync(
            cloneA,
            new AmendLastCommitExecutionOptions
            {
                Preflight = leasePre1,
                Message = "feat: lease rewritten",
                RewritePushedRemoteWithLease = true,
            },
            journalStore: journalStore);
        var leaseTipRemote1 = (await GitInAsync(realGit, originBare, "rev-parse", "refs/heads/feat/amend-lease")).Trim();
        var leaseTipLocal1 = (await GitInAsync(realGit, cloneA, "rev-parse", "HEAD")).Trim();
        Check(optInExec.Succeeded &&
              optInExec.PushSucceeded &&
              leaseTipRemote1 == leaseTipLocal1,
              "Explicit opt-in Amend rewrites local HEAD and updates pushed remote via strict --force-with-lease");

        // Race: preflight on cloneA, then cloneB pushes collaborator commit before cloneA executes lease push
        await GitInAsync(realGit, cloneA, "fetch", "origin");
        await File.WriteAllTextAsync(Path.Combine(cloneA, "lease.txt"), "v3-local\n", Utf8NoBom);
        await GitInAsync(realGit, cloneA, "add", "lease.txt");
        var racePre = await GitSyncService.InspectAmendLastCommitAsync(cloneA);

        await GitInAsync(realGit, cloneB, "fetch", "origin");
        await GitInAsync(realGit, cloneB, "checkout", "-B", "feat/amend-lease", "origin/feat/amend-lease");
        await CommitFileAsync(realGit, cloneB, "bob-race.txt", "collaborator wins\n", "bob concurrent commit");
        await GitInAsync(realGit, cloneB, "push", "origin", "feat/amend-lease");
        var collaboratorSha = (await GitInAsync(realGit, originBare, "rev-parse", "refs/heads/feat/amend-lease")).Trim();

        var raceExec = await GitSyncService.ExecuteAmendLastCommitAsync(
            cloneA,
            new AmendLastCommitExecutionOptions
            {
                Preflight = racePre,
                Message = "feat: race amend",
                RewritePushedRemoteWithLease = true,
            },
            journalStore: journalStore);
        var serverTipAfterRace = (await GitInAsync(realGit, originBare, "rev-parse", "refs/heads/feat/amend-lease")).Trim();
        Check(!raceExec.Succeeded &&
              raceExec.IsPartialSuccess &&
              raceExec.RejectedByLease &&
              serverTipAfterRace == collaboratorSha &&
              raceExec.CanRetryPushOnly,
              "Concurrent collaborator push rejects pinned --force-with-lease, preserves server commit, and enters Push-Retry-Only state");

        // Partial outage + Retry Push Only recovery
        await GitInAsync(realGit, cloneA, "checkout", "-b", "feat/amend-retry");
        await CommitFileAsync(realGit, cloneA, "retry.txt", "r1\n", "feat: retry base");
        await GitInAsync(realGit, cloneA, "push", "-u", "origin", "feat/amend-retry");
        await GitInAsync(realGit, cloneA, "fetch", "origin");

        await File.WriteAllTextAsync(Path.Combine(cloneA, "retry.txt"), "r2\n", Utf8NoBom);
        await GitInAsync(realGit, cloneA, "add", "retry.txt");
        var partialPre = await GitSyncService.InspectAmendLastCommitAsync(cloneA);
        var shaBeforePartialAmend = partialPre.HeadSha;

        Directory.Move(originBare, originMoved);
        AmendLastCommitOutcome partialExec;
        try
        {
            partialExec = await GitSyncService.ExecuteAmendLastCommitAsync(
                cloneA,
                new AmendLastCommitExecutionOptions
                {
                    Preflight = partialPre,
                    Message = "feat: amended before network outage",
                    RewritePushedRemoteWithLease = true,
                },
                journalStore: journalStore);
        }
        finally
        {
            if (Directory.Exists(originMoved) && !Directory.Exists(originBare))
                Directory.Move(originMoved, originBare);
        }

        var amendedHeadSha = (await GitInAsync(realGit, cloneA, "rev-parse", "HEAD")).Trim();
        Check(!partialExec.Succeeded &&
              partialExec.AmendSucceeded &&
              !partialExec.PushSucceeded &&
              partialExec.IsPartialSuccess &&
              partialExec.NewHeadSha == amendedHeadSha &&
              amendedHeadSha != shaBeforePartialAmend &&
              partialExec.CanRetryPushOnly &&
              partialExec.RetrySession?.PendingPushOptions?.ForceWithLeaseExpectSha == partialPre.LiveRemoteTipSha,
              "Remote push outage leaves local Amend intact with backup ref and lease-pinned RetrySession");

        var retrySessionRes = await GitSyncService.RetryPushSessionAsync(
            partialExec.RetrySession!,
            journalStore: journalStore);
        var remoteRetryTip = (await GitInAsync(realGit, originBare, "rev-parse", "refs/heads/feat/amend-retry")).Trim();
        var headAfterRetry = (await GitInAsync(realGit, cloneA, "rev-parse", "HEAD")).Trim();
        Check(retrySessionRes.IsFullySucceeded &&
              headAfterRetry == amendedHeadSha &&
              remoteRetryTip == amendedHeadSha,
              "RetryPushSessionAsync pushes already-amended HEAD with lease without re-amending");
    }
}
