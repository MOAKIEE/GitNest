using System.Text;
using SourceGit.Commands;
using SourceGit.Models;

internal static partial class Program
{
    private static async Task RunMergeRevertTestsAsync(string realGit, string suiteRoot)
    {
        var utf8NoBom = new UTF8Encoding(false);
        var mrRoot = Path.Combine(suiteRoot, "merge-revert-tests");
        Directory.CreateDirectory(mrRoot);

        var originBare = Path.Combine(mrRoot, "origin.git");
        var repo = Path.Combine(mrRoot, "work-repo");
        var wtPath = Path.Combine(mrRoot, "wt-occupied");
        var journalFile = Path.Combine(mrRoot, "merge-revert-journal.json");
        var journal = new OperationJournalStore(journalFile);

        await GitAsync(realGit, "init", "--bare", originBare);
        await GitAsync(realGit, "clone", originBare, repo);
        await GitAsync(realGit, "-C", repo, "config", "--local", "user.name", "Alice");
        await GitAsync(realGit, "-C", repo, "config", "--local", "user.email", "alice@example.invalid");

        var baseRel = "中文目录/说明 文档.txt";
        Directory.CreateDirectory(Path.Combine(repo, "中文目录"));
        File.WriteAllText(Path.Combine(repo, baseRel), "line-1\nline-2\nline-3\n", utf8NoBom);
        await GitAsync(realGit, "-C", repo, "add", baseRel);
        await GitAsync(realGit, "-C", repo, "commit", "-m", "initial commit");
        await GitAsync(realGit, "-C", repo, "branch", "-M", "main");
        await GitAsync(realGit, "-C", repo, "push", "-u", "origin", "main");
        var initSha = (await GitAsync(realGit, "-C", repo, "rev-parse", "HEAD")).Trim();

        // 0. Guard checks on legacy Command wrappers (Revert no longer injects -m 1; Merge blocks option-like refs)
        var cmdRevert = new Revert(repo, initSha, autoCommit: true);
        Check(!cmdRevert.Args.Contains("-m 1", StringComparison.Ordinal) &&
              cmdRevert.Args.Contains("--no-edit", StringComparison.Ordinal),
            "Commands.Revert removes hardcoded -m 1 and safely targets single commits");

        var blockedOptionArg = false;
        try
        {
            _ = new Merge(repo, "--evil-option", "", false);
        }
        catch (ArgumentException)
        {
            blockedOptionArg = true;
        }
        Check(blockedOptionArg, "Commands.Merge rejects option-like ref names");

        // 1. Fast-forward directed merge (feat/ff -> main) stays on main and does not push
        await GitAsync(realGit, "-C", repo, "checkout", "-b", "feat/ff");
        File.WriteAllText(Path.Combine(repo, "ff-added.txt"), "fast forward content\n", utf8NoBom);
        await GitAsync(realGit, "-C", repo, "add", "ff-added.txt");
        await GitAsync(realGit, "-C", repo, "commit", "-m", "ff feature change");
        var ffTipSha = (await GitAsync(realGit, "-C", repo, "rev-parse", "HEAD")).Trim();
        await GitAsync(realGit, "-C", repo, "checkout", "main");

        var ffPreflight = await MergeRevertService.InspectMergeAsync(repo, "feat/ff", "main", MergeSourceKind.LocalBranch);
        Check(ffPreflight.IsReady &&
              ffPreflight.DirectionSummary == "将【feat/ff】的改动合并到【main】" &&
              ffPreflight.Relation == MergeRelationKind.FastForward &&
              ffPreflight.ConflictPrediction == MergeConflictPrediction.Clean &&
              ffPreflight.IncomingCommits.Count == 1 &&
              ffPreflight.IncomingChanges.Any(c => c.Path == "ff-added.txt"),
            "Directed merge preflight previews source, target, FF relation, clean tree, and incoming commits/files");

        var ffResult = await MergeRevertService.ExecuteMergeAsync(
            repo,
            new MergeExecutionOptions
            {
                SourceKind = MergeSourceKind.LocalBranch,
                SourceRefOrSha = "feat/ff",
                TargetBranchName = "main",
                Preflight = ffPreflight,
            },
            journal);
        var originMainAfterFf = (await GitAsync(realGit, "--git-dir", originBare, "rev-parse", "refs/heads/main")).Trim();
        Check(ffResult.Succeeded &&
              ffResult.WasFastForward &&
              ffResult.StayedOnTargetBranch &&
              ffResult.ResultHeadSha == ffTipSha &&
              originMainAfterFf == initSha,
            "Directed FF merge advances main, stays on target branch, and does not push automatically");

        // 2. Diverged cross-branch directed merge (currently on feat/div, merging feat/div -> main)
        await GitAsync(realGit, "-C", repo, "checkout", "-b", "feat/div");
        File.WriteAllText(Path.Combine(repo, "div-feature.txt"), "feature side\n", utf8NoBom);
        await GitAsync(realGit, "-C", repo, "add", "div-feature.txt");
        await GitAsync(realGit, "-C", repo, "commit", "-m", "diverged feature");
        var featDivSha = (await GitAsync(realGit, "-C", repo, "rev-parse", "HEAD")).Trim();

        await GitAsync(realGit, "-C", repo, "checkout", "main");
        File.WriteAllText(Path.Combine(repo, "div-main.txt"), "main side\n", utf8NoBom);
        await GitAsync(realGit, "-C", repo, "add", "div-main.txt");
        await GitAsync(realGit, "-C", repo, "commit", "-m", "diverged main");
        var mainBeforeDivSha = (await GitAsync(realGit, "-C", repo, "rev-parse", "HEAD")).Trim();

        // Switch back to feat/div so merging into main tests cross-branch target switch
        await GitAsync(realGit, "-C", repo, "checkout", "feat/div");
        var divPreflight = await MergeRevertService.InspectMergeAsync(repo, "feat/div", "main", MergeSourceKind.LocalBranch);
        Check(divPreflight.IsReady &&
              divPreflight.NeedsCheckoutTargetBranch &&
              divPreflight.Relation == MergeRelationKind.Diverged &&
              divPreflight.ConflictPrediction == MergeConflictPrediction.Clean,
            "Diverged directed merge preflight detects cross-branch checkout need and clean prediction");

        var divResult = await MergeRevertService.ExecuteMergeAsync(
            repo,
            new MergeExecutionOptions
            {
                SourceKind = MergeSourceKind.LocalBranch,
                SourceRefOrSha = "feat/div",
                TargetBranchName = "main",
                Preflight = divPreflight,
            },
            journal);
        var currentAfterDiv = (await GitAsync(realGit, "-C", repo, "symbolic-ref", "--short", "HEAD")).Trim();
        var featDivAfter = (await GitAsync(realGit, "-C", repo, "rev-parse", "refs/heads/feat/div")).Trim();
        var mergeParents = (await GitAsync(realGit, "-C", repo, "rev-list", "--parents", "-n", "1", "HEAD")).Trim().Split(' ');
        Check(divResult.Succeeded &&
              !divResult.WasFastForward &&
              divResult.StayedOnTargetBranch &&
              currentAfterDiv == "main" &&
              featDivAfter == featDivSha &&
              mergeParents.Length == 3 &&
              mergeParents[1] == mainBeforeDivSha &&
              mergeParents[2] == featDivSha,
            "Cross-branch diverged merge switches to main, creates merge commit leaving source untouched, and stays on main");

        // 3. Explicit remote snapshot requirement (blocks un-fetched remote ref, merges fetched remote snapshot)
        var missingRemote = await MergeRevertService.InspectMergeAsync(repo, "origin/not-fetched", "main", MergeSourceKind.RemoteBranch);
        Check(!missingRemote.IsReady && missingRemote.BlockingReason.Contains("Fetch", StringComparison.OrdinalIgnoreCase),
            "Remote branch source does not guess un-fetched remote names");

        await GitAsync(realGit, "-C", repo, "checkout", "-b", "temp/remote-src");
        File.WriteAllText(Path.Combine(repo, "remote-snap.txt"), "from remote snapshot\n", utf8NoBom);
        await GitAsync(realGit, "-C", repo, "add", "remote-snap.txt");
        await GitAsync(realGit, "-C", repo, "commit", "-m", "remote snapshot commit");
        var remoteTipSha = (await GitAsync(realGit, "-C", repo, "rev-parse", "HEAD")).Trim();
        await GitAsync(realGit, "-C", repo, "push", "origin", "temp/remote-src:refs/heads/server/topic-snap");
        await GitAsync(realGit, "-C", repo, "fetch", "origin");
        await GitAsync(realGit, "-C", repo, "checkout", "main");
        await GitAsync(realGit, "-C", repo, "branch", "-D", "temp/remote-src");

        var remotePreflight = await MergeRevertService.InspectMergeAsync(repo, "origin/server/topic-snap", "main", MergeSourceKind.RemoteBranch);
        var remoteMerge = await MergeRevertService.ExecuteMergeAsync(
            repo,
            new MergeExecutionOptions
            {
                SourceKind = MergeSourceKind.RemoteBranch,
                SourceRefOrSha = "origin/server/topic-snap",
                TargetBranchName = "main",
                Preflight = remotePreflight,
            },
            journal);
        Check(remotePreflight.IsReady &&
              remoteMerge.Succeeded &&
              remoteMerge.ResultHeadSha == remoteTipSha,
            "Merge from explicit fetched remote branch snapshot uses exact fetched OID");

        // 4. Block merge when target branch is checked out in another worktree
        await GitAsync(realGit, "-C", repo, "worktree", "add", "-b", "wt/branch", wtPath, "main");
        var wtBlockedPre = await MergeRevertService.InspectMergeAsync(repo, "feat/ff", "wt/branch", MergeSourceKind.LocalBranch);
        var wtBlockedExec = await MergeRevertService.ExecuteMergeAsync(
            repo,
            new MergeExecutionOptions
            {
                SourceKind = MergeSourceKind.LocalBranch,
                SourceRefOrSha = "feat/ff",
                TargetBranchName = "wt/branch",
                Preflight = wtBlockedPre,
            },
            journal);
        Check(!wtBlockedPre.IsReady &&
              !string.IsNullOrEmpty(wtBlockedPre.TargetCheckedOutWorktreePath) &&
              wtBlockedExec.FailureKind == MergeRevertFailureKind.TargetBranchCheckedOutElsewhere,
            "Merge blocks modifying a target branch checked out in another worktree");

        // 5. Dirty working tree & stale snapshot rejection
        var dirtyTracked = Path.Combine(repo, "ff-added.txt");
        File.WriteAllText(dirtyTracked, "dirty modification\n", utf8NoBom);
        var dirtyPre = await MergeRevertService.InspectMergeAsync(repo, "feat/ff", "main", MergeSourceKind.LocalBranch);
        Check(!dirtyPre.IsReady && dirtyPre.HasDirtyWorkingTree,
            "Dirty working tree blocks merge in preflight");
        File.WriteAllText(dirtyTracked, "fast forward content\n", utf8NoBom);

        await GitAsync(realGit, "-C", repo, "checkout", "-b", "feat/stale-src", "main");
        File.WriteAllText(Path.Combine(repo, "stale.txt"), "v1\n", utf8NoBom);
        await GitAsync(realGit, "-C", repo, "add", "stale.txt");
        await GitAsync(realGit, "-C", repo, "commit", "-m", "stale v1");
        await GitAsync(realGit, "-C", repo, "checkout", "main");

        var stalePre = await MergeRevertService.InspectMergeAsync(repo, "feat/stale-src", "main", MergeSourceKind.LocalBranch);
        Check(stalePre.IsReady, "Stale test snapshot starts ready");
        await GitAsync(realGit, "-C", repo, "checkout", "feat/stale-src");
        File.WriteAllText(Path.Combine(repo, "stale.txt"), "v2 externally updated\n", utf8NoBom);
        await GitAsync(realGit, "-C", repo, "add", "stale.txt");
        await GitAsync(realGit, "-C", repo, "commit", "-m", "stale v2");
        await GitAsync(realGit, "-C", repo, "checkout", "main");

        var staleRes = await MergeRevertService.ExecuteMergeAsync(
            repo,
            new MergeExecutionOptions
            {
                SourceKind = MergeSourceKind.LocalBranch,
                SourceRefOrSha = "feat/stale-src",
                TargetBranchName = "main",
                Preflight = stalePre,
            },
            journal);
        Check(staleRes.BlockedBySnapshot &&
              staleRes.FailureKind == MergeRevertFailureKind.SnapshotStale,
            "Merge rejects execution when source or target moved after confirmation snapshot");

        // 6. Conflict auto-abort (GitNest owned & untouched) + post-abort verification
        await GitAsync(realGit, "-C", repo, "checkout", "-b", "feat/conflict-src", "main");
        File.WriteAllText(Path.Combine(repo, baseRel), "line-1\nfeature conflict edit\nline-3\n", utf8NoBom);
        await GitAsync(realGit, "-C", repo, "add", baseRel);
        await GitAsync(realGit, "-C", repo, "commit", "-m", "feature conflict line");

        await GitAsync(realGit, "-C", repo, "checkout", "main");
        File.WriteAllText(Path.Combine(repo, baseRel), "line-1\nmain conflict edit\nline-3\n", utf8NoBom);
        await GitAsync(realGit, "-C", repo, "add", baseRel);
        await GitAsync(realGit, "-C", repo, "commit", "-m", "main conflict line");
        var mainPreConflictSha = (await GitAsync(realGit, "-C", repo, "rev-parse", "HEAD")).Trim();

        var conflictPre = await MergeRevertService.InspectMergeAsync(repo, "feat/conflict-src", "main", MergeSourceKind.LocalBranch);
        Check(conflictPre.IsReady &&
              conflictPre.ConflictPrediction == MergeConflictPrediction.HasConflicts &&
              conflictPre.PredictedConflictPaths.Contains(baseRel),
            "Non-mutating merge-tree preflight predicts exact conflicted Chinese/spaced path");

        var cleanAbortRes = await MergeRevertService.ExecuteMergeAsync(
            repo,
            new MergeExecutionOptions
            {
                SourceKind = MergeSourceKind.LocalBranch,
                SourceRefOrSha = "feat/conflict-src",
                TargetBranchName = "main",
                Preflight = conflictPre,
            },
            journal);
        var statusAfterCleanAbort = (await GitAsync(realGit, "-C", repo, "status", "--porcelain")).Trim();
        var headAfterCleanAbort = (await GitAsync(realGit, "-C", repo, "rev-parse", "HEAD")).Trim();
        var gitDir = Path.Combine(repo, ".git");
        Check(!cleanAbortRes.Succeeded &&
              cleanAbortRes.ConflictAbortedCleanly &&
              cleanAbortRes.FailureKind == MergeRevertFailureKind.ConflictAbortedCleanly &&
              cleanAbortRes.Summary.Contains("合并存在冲突，已取消，目标分支未合并", StringComparison.Ordinal) &&
              headAfterCleanAbort == mainPreConflictSha &&
              statusAfterCleanAbort.Length == 0 &&
              !File.Exists(Path.Combine(gitDir, "MERGE_HEAD")),
            "Clean GitNest merge conflict auto-aborts safely and re-verifies clean target branch state");

        // 7. Conflict ownership guard: external AI agent edits conflicted file + creates untracked file -> MUST STOP auto-abort
        var conflictPre2 = await MergeRevertService.InspectMergeAsync(repo, "feat/conflict-src", "main", MergeSourceKind.LocalBranch);
        var aiEditedContent = "line-1\nAI resolved both edits carefully\nline-3\n";
        var aiHelperPath = Path.Combine(repo, "ai-resolution-note.txt");
        var stopAbortRes = await MergeRevertService.ExecuteMergeAsync(
            repo,
            new MergeExecutionOptions
            {
                SourceKind = MergeSourceKind.LocalBranch,
                SourceRefOrSha = "feat/conflict-src",
                TargetBranchName = "main",
                Preflight = conflictPre2,
                BeforeAutoAbortAsync = _ =>
                {
                    File.WriteAllText(Path.Combine(repo, baseRel), aiEditedContent, utf8NoBom);
                    File.WriteAllText(aiHelperPath, "kept by AI", utf8NoBom);
                    return Task.CompletedTask;
                },
            },
            journal);
        var retainedInspect = await MergeRevertService.InspectRepositoryInProgressAsync(repo, journal);
        Check(!stopAbortRes.Succeeded &&
              stopAbortRes.AutoAbortStoppedForExternalIntervention &&
              stopAbortRes.FailureKind == MergeRevertFailureKind.ConflictOwnershipExternalIntervention &&
              File.Exists(Path.Combine(gitDir, "MERGE_HEAD")) &&
              File.ReadAllText(Path.Combine(repo, baseRel), utf8NoBom) == aiEditedContent &&
              File.Exists(aiHelperPath) &&
              retainedInspect.Origin == ConflictOwnershipOrigin.GitNestOwnedWithExternalIntervention &&
              !retainedInspect.CanOfferSafeAbort,
            "Conflict ownership guard stops auto-abort immediately and preserves external/AI modifications");

        // Clean up retained conflict manually before next tests
        File.Delete(aiHelperPath);
        await GitAsync(realGit, "-C", repo, "merge", "--abort");

        // 8. Pre-existing external CLI merge conflict: detected read-only, NEVER auto-aborted on open/refresh
        var cliMergeProc = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(realGit, $"-C \"{repo}\" merge feat/conflict-src")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        })!;
        await cliMergeProc.WaitForExitAsync();
        Check(cliMergeProc.ExitCode != 0 && File.Exists(Path.Combine(gitDir, "MERGE_HEAD")),
            "Fixture sets up external CLI merge conflict");

        var externalPre = await MergeRevertService.InspectMergeAsync(repo, "feat/ff", "main", MergeSourceKind.LocalBranch);
        var externalInspect1 = await MergeRevertService.InspectRepositoryInProgressAsync(repo, journal);
        var externalInspect2 = await MergeRevertService.InspectRepositoryInProgressAsync(repo, journal);
        Check(!externalPre.IsReady &&
              externalInspect1.InProgressKind == GitInProgressKind.Merge &&
              externalInspect1.Origin == ConflictOwnershipOrigin.ExternalOrUnknown &&
              !externalInspect1.CanOfferSafeAbort &&
              externalInspect1.ConflictedPaths.Contains(baseRel) &&
              externalInspect2.InProgressKind == GitInProgressKind.Merge &&
              File.Exists(Path.Combine(gitDir, "MERGE_HEAD")),
            "External CLI merge conflict is identified as ExternalOrUnknown and never auto-aborted on repeated inspections");

        // Also ensure fabricated ownership context with wrong operationId cannot bypass VerifyConflictOwnershipForAbortAsync
        var forgedVerify = await MergeRevertService.VerifyConflictOwnershipForAbortAsync(
            new ConflictOwnershipContext
            {
                OperationId = "nonexistent-op",
                OperationKind = GitOperationKind.Merge,
                RepoPath = repo,
                CanonicalGitDir = gitDir,
                ExpectedHeadSha = mainPreConflictSha,
                ExpectedPartnerSha = (await GitAsync(realGit, "-C", repo, "rev-parse", "feat/conflict-src")).Trim(),
            },
            journal);
        Check(!forgedVerify.CanSafelyAutoAbort &&
              forgedVerify.Origin == ConflictOwnershipOrigin.ExternalOrUnknown,
            "Ownership verifier rejects unrecorded conflict state before any abort");

        await GitAsync(realGit, "-C", repo, "merge", "--abort");

        // 9. Single normal commit revert vs merge commit block
        File.WriteAllText(Path.Combine(repo, "to-revert.txt"), "bad line\n", utf8NoBom);
        await GitAsync(realGit, "-C", repo, "add", "to-revert.txt");
        await GitAsync(realGit, "-C", repo, "commit", "-m", "commit to revert");
        var normalCommitToRevert = (await GitAsync(realGit, "-C", repo, "rev-parse", "HEAD")).Trim();

        // Find the earlier diverged merge commit on main
        var mergeCommitSha = (await GitAsync(realGit, "-C", repo, "rev-list", "--merges", "-n", "1", "main")).Trim();
        var mergeRevPre = await MergeRevertService.InspectRevertAsync(repo, mergeCommitSha);
        var mergeRevExec = await MergeRevertService.ExecuteRevertAsync(
            repo,
            new RevertExecutionOptions { CommitSha = mergeCommitSha, Preflight = mergeRevPre },
            journal);
        Check(mergeRevPre.IsMergeCommit &&
              !mergeRevPre.IsReady &&
              !mergeRevExec.Succeeded &&
              mergeRevExec.FailureKind == MergeRevertFailureKind.MergeCommitNotSupported,
            "Revert strictly blocks merge commits instead of guessing -m 1");

        var normalRevPre = await MergeRevertService.InspectRevertAsync(repo, normalCommitToRevert);
        Check(normalRevPre.IsReady &&
              !normalRevPre.IsMergeCommit &&
              normalRevPre.AffectedChanges.Any(c => c.Path == "to-revert.txt"),
            "Single normal commit revert preflight succeeds and lists affected files");

        var normalRevExec = await MergeRevertService.ExecuteRevertAsync(
            repo,
            new RevertExecutionOptions { CommitSha = normalCommitToRevert, AutoCommit = true, Preflight = normalRevPre },
            journal);
        Check(normalRevExec.Succeeded &&
              !File.Exists(Path.Combine(repo, "to-revert.txt")) &&
              (await GitAsync(realGit, "-C", repo, "rev-list", "--count", normalCommitToRevert)).Trim().Length > 0,
            "Single normal commit revert creates reverse commit while preserving history");

        // 10. Revert conflict: clean auto-abort vs external intervention stop
        File.WriteAllText(Path.Combine(repo, "revert-conflict.txt"), "v1\n", utf8NoBom);
        await GitAsync(realGit, "-C", repo, "add", "revert-conflict.txt");
        await GitAsync(realGit, "-C", repo, "commit", "-m", "introduce revert-conflict v1");
        File.WriteAllText(Path.Combine(repo, "revert-conflict.txt"), "v2 conflicting later line\n", utf8NoBom);
        await GitAsync(realGit, "-C", repo, "add", "revert-conflict.txt");
        await GitAsync(realGit, "-C", repo, "commit", "-m", "edit revert-conflict to v2");
        var v2Sha = (await GitAsync(realGit, "-C", repo, "rev-parse", "HEAD")).Trim();
        File.WriteAllText(Path.Combine(repo, "revert-conflict.txt"), "v3 overlapping line\n", utf8NoBom);
        await GitAsync(realGit, "-C", repo, "add", "revert-conflict.txt");
        await GitAsync(realGit, "-C", repo, "commit", "-m", "edit revert-conflict to v3");
        var v3Sha = (await GitAsync(realGit, "-C", repo, "rev-parse", "HEAD")).Trim();

        var revConflictPre = await MergeRevertService.InspectRevertAsync(repo, v2Sha);
        var revCleanAbort = await MergeRevertService.ExecuteRevertAsync(
            repo,
            new RevertExecutionOptions { CommitSha = v2Sha, Preflight = revConflictPre },
            journal);
        Check(!revCleanAbort.Succeeded &&
              revCleanAbort.ConflictAbortedCleanly &&
              revCleanAbort.FailureKind == MergeRevertFailureKind.ConflictAbortedCleanly &&
              (await GitAsync(realGit, "-C", repo, "rev-parse", "HEAD")).Trim() == v3Sha &&
              !File.Exists(Path.Combine(gitDir, "REVERT_HEAD")) &&
              (await GitAsync(realGit, "-C", repo, "status", "--porcelain")).Trim().Length == 0,
            "Revert conflict with exclusive GitNest ownership auto-aborts cleanly and restores HEAD");

        var revConflictPre2 = await MergeRevertService.InspectRevertAsync(repo, v2Sha);
        var aiRevertText = "v-ai-edited\n";
        var revStopAbort = await MergeRevertService.ExecuteRevertAsync(
            repo,
            new RevertExecutionOptions
            {
                CommitSha = v2Sha,
                Preflight = revConflictPre2,
                BeforeAutoAbortAsync = _ =>
                {
                    File.WriteAllText(Path.Combine(repo, "revert-conflict.txt"), aiRevertText, utf8NoBom);
                    return Task.CompletedTask;
                },
            },
            journal);
        Check(!revStopAbort.Succeeded &&
              revStopAbort.AutoAbortStoppedForExternalIntervention &&
              revStopAbort.FailureKind == MergeRevertFailureKind.ConflictOwnershipExternalIntervention &&
              File.Exists(Path.Combine(gitDir, "REVERT_HEAD")) &&
              File.ReadAllText(Path.Combine(repo, "revert-conflict.txt"), utf8NoBom) == aiRevertText,
            "Revert conflict ownership guard stops auto-abort when external edits occur after conflict");
        await GitAsync(realGit, "-C", repo, "revert", "--abort");

        // 11. Distinguish Hook failure vs GPG/SSH signing failure (preserving state, no false success) for both Revert & Merge
        var hooksDir = Path.Combine(mrRoot, "custom-hooks");
        Directory.CreateDirectory(hooksDir);
        var revertHook = Path.Combine(hooksDir, "prepare-commit-msg");
        File.WriteAllText(revertHook, "#!/bin/sh\necho 'custom policy blocked prepare-commit-msg' >&2\nexit 1\n", utf8NoBom);

        await GitAsync(realGit, "-C", repo, "config", "--local", "core.hooksPath", hooksDir);
        var hookRevPre = await MergeRevertService.InspectRevertAsync(repo, v3Sha);
        var hookRevRes = await MergeRevertService.ExecuteRevertAsync(
            repo,
            new RevertExecutionOptions { CommitSha = v3Sha, Preflight = hookRevPre },
            journal);
        Check(!hookRevRes.Succeeded &&
              hookRevRes.FailureKind == MergeRevertFailureKind.HookRejected &&
              !hookRevRes.ConflictAbortedCleanly &&
              hookRevRes.DiagnosticDetail.Contains("custom policy blocked prepare-commit-msg", StringComparison.Ordinal),
            "Revert distinguishes prepare-commit-msg hook rejection from conflicts and preserves diagnostic detail");
        if (File.Exists(Path.Combine(gitDir, "REVERT_HEAD")))
            await GitAsync(realGit, "-C", repo, "revert", "--abort");
        else
            await GitAsync(realGit, "-C", repo, "reset", "--hard", "HEAD");
        File.Delete(revertHook);

        // Diverged merge hook rejection (pre-merge-commit preserves MERGE_HEAD & staged merge cleanly)
        await GitAsync(realGit, "-C", repo, "checkout", "-b", "feat/hook-merge", "main");
        File.WriteAllText(Path.Combine(repo, "hook-merge-file.txt"), "hook merge test\n", utf8NoBom);
        await GitAsync(realGit, "-C", repo, "add", "hook-merge-file.txt");
        await GitAsync(realGit, "-C", repo, "commit", "-m", "branch for merge hook and signing tests");
        await GitAsync(realGit, "-C", repo, "checkout", "main");
        File.WriteAllText(Path.Combine(repo, "main-only-file.txt"), "main diverge file\n", utf8NoBom);
        await GitAsync(realGit, "-C", repo, "add", "main-only-file.txt");
        await GitAsync(realGit, "-C", repo, "commit", "-m", "main diverge for hook test");

        var preMergeHook = Path.Combine(hooksDir, "pre-merge-commit");
        File.WriteAllText(preMergeHook, "#!/bin/sh\necho 'custom policy blocked pre-merge-commit' >&2\nexit 1\n", utf8NoBom);
        var hookMergePre = await MergeRevertService.InspectMergeAsync(repo, "feat/hook-merge", "main", MergeSourceKind.LocalBranch);
        var hookMergeRes = await MergeRevertService.ExecuteMergeAsync(
            repo,
            new MergeExecutionOptions
            {
                SourceKind = MergeSourceKind.LocalBranch,
                SourceRefOrSha = "feat/hook-merge",
                TargetBranchName = "main",
                Preflight = hookMergePre,
            },
            journal);
        Check(!hookMergeRes.Succeeded &&
              hookMergeRes.FailureKind == MergeRevertFailureKind.HookRejected &&
              !hookMergeRes.ConflictAbortedCleanly &&
              File.Exists(Path.Combine(gitDir, "MERGE_HEAD")) &&
              hookMergeRes.DiagnosticDetail.Contains("custom policy blocked pre-merge-commit", StringComparison.Ordinal),
            "Diverged merge distinguishes pre-merge-commit hook rejection from conflicts and retains MERGE_HEAD");
        await GitAsync(realGit, "-C", repo, "merge", "--abort");
        await GitAsync(realGit, "-C", repo, "config", "--local", "--unset", "core.hooksPath");

        // GPG signing failure via test executable fixture (both Revert and Merge)
        var selfExe = Environment.ProcessPath!;
        var prevTestMode = Environment.GetEnvironmentVariable(FakeModeVariable);
        Environment.SetEnvironmentVariable(FakeModeVariable, "gpg-fail");
        try
        {
            await GitAsync(realGit, "-C", repo, "config", "--local", "commit.gpgsign", "true");
            await GitAsync(realGit, "-C", repo, "config", "--local", "gpg.format", "openpgp");
            await GitAsync(realGit, "-C", repo, "config", "--local", "gpg.program", selfExe);

            var signRevPre = await MergeRevertService.InspectRevertAsync(repo, v3Sha);
            var signRevRes = await MergeRevertService.ExecuteRevertAsync(
                repo,
                new RevertExecutionOptions { CommitSha = v3Sha, Preflight = signRevPre },
                journal);
            var signRevStatus = (await GitAsync(realGit, "-C", repo, "status", "--porcelain")).Trim();
            Check(!signRevRes.Succeeded &&
                  signRevRes.FailureKind == MergeRevertFailureKind.SigningFailed &&
                  !signRevRes.ConflictAbortedCleanly &&
                  signRevRes.DiagnosticDetail.Contains("gpg failed", StringComparison.Ordinal) &&
                  signRevStatus.Length > 0,
                "Revert distinguishes GPG signing failure from conflicts and preserves staged diagnostic state");
            if (File.Exists(Path.Combine(gitDir, "REVERT_HEAD")))
                await GitAsync(realGit, "-C", repo, "revert", "--abort");
            else
                await GitAsync(realGit, "-C", repo, "reset", "--hard", "HEAD");

            var signMergePre = await MergeRevertService.InspectMergeAsync(repo, "feat/hook-merge", "main", MergeSourceKind.LocalBranch);
            var signMergeRes = await MergeRevertService.ExecuteMergeAsync(
                repo,
                new MergeExecutionOptions
                {
                    SourceKind = MergeSourceKind.LocalBranch,
                    SourceRefOrSha = "feat/hook-merge",
                    TargetBranchName = "main",
                    Preflight = signMergePre,
                },
                journal);
            Check(!signMergeRes.Succeeded &&
                  signMergeRes.FailureKind == MergeRevertFailureKind.SigningFailed &&
                  !signMergeRes.ConflictAbortedCleanly &&
                  File.Exists(Path.Combine(gitDir, "MERGE_HEAD")),
                "Diverged merge distinguishes GPG signing failure from conflicts and retains pending MERGE_HEAD state");
            await GitAsync(realGit, "-C", repo, "merge", "--abort");
        }
        finally
        {
            Environment.SetEnvironmentVariable(FakeModeVariable, prevTestMode);
            await GitAsync(realGit, "-C", repo, "config", "--local", "--unset-all", "commit.gpgsign");
            await GitAsync(realGit, "-C", repo, "config", "--local", "--unset-all", "gpg.program");
            await GitAsync(realGit, "-C", repo, "config", "--local", "--unset-all", "gpg.format");
        }
    }
}
