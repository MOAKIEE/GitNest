using System.Diagnostics;

using SourceGit.Commands;
using SourceGit.Models;

internal static partial class Program
{
    private static async Task RunMergeTestsAsync(string realGit, string root)
    {
        var mrRoot = Path.Combine(root, "merge-suite");
        Directory.CreateDirectory(mrRoot);

        var originBare = Path.Combine(mrRoot, "origin.git");
        var repo = Path.Combine(mrRoot, "work-repo");
        var wtPath = Path.Combine(mrRoot, "wt-occupied");
        var journal = new OperationJournalStore(Path.Combine(mrRoot, "merge-journal.json"));

        await InitBareRepoAsync(realGit, originBare);
        await GitAsync(realGit, "clone", originBare, repo);
        await GitInAsync(realGit, repo, "config", "--local", "user.name", "Alice");
        await GitInAsync(realGit, repo, "config", "--local", "user.email", "alice@example.invalid");

        var baseRel = "中文目录/说明 文档.txt";
        var initSha = await CommitFileAsync(realGit, repo, baseRel, "line-1\nline-2\nline-3\n", "initial commit");
        await GitInAsync(realGit, repo, "branch", "-M", "main");
        await GitInAsync(realGit, repo, "push", "-u", "origin", "main");

        // 0. Guard check on legacy Merge command wrapper
        var blockedOptionArg = false;
        try { _ = new Merge(repo, "--evil-option", "", false); }
        catch (ArgumentException) { blockedOptionArg = true; }
        Check(blockedOptionArg, "Commands.Merge rejects option-like ref names");

        // 1. Fast-forward directed merge (feat/ff -> main) stays on main and does not push
        await GitInAsync(realGit, repo, "checkout", "-b", "feat/ff");
        var ffTipSha = await CommitFileAsync(realGit, repo, "ff-added.txt", "fast forward content\n", "ff feature change");
        await GitInAsync(realGit, repo, "checkout", "main");

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
        Check(ffResult.Succeeded && ffResult.WasFastForward && ffResult.StayedOnTargetBranch &&
              ffResult.ResultHeadSha == ffTipSha && originMainAfterFf == initSha,
              "Directed FF merge advances main, stays on target branch, and does not push automatically");

        // 2. Diverged cross-branch directed merge (on feat/div, merging feat/div -> main)
        await GitInAsync(realGit, repo, "checkout", "-b", "feat/div");
        var featDivSha = await CommitFileAsync(realGit, repo, "div-feature.txt", "feature side\n", "diverged feature");

        await GitInAsync(realGit, repo, "checkout", "main");
        var mainBeforeDivSha = await CommitFileAsync(realGit, repo, "div-main.txt", "main side\n", "diverged main");

        await GitInAsync(realGit, repo, "checkout", "feat/div");
        var divPreflight = await MergeRevertService.InspectMergeAsync(repo, "feat/div", "main", MergeSourceKind.LocalBranch);
        Check(divPreflight.IsReady && divPreflight.NeedsCheckoutTargetBranch &&
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
        var currentAfterDiv = (await GitInAsync(realGit, repo, "symbolic-ref", "--short", "HEAD")).Trim();
        var featDivAfter = (await GitInAsync(realGit, repo, "rev-parse", "refs/heads/feat/div")).Trim();
        var mergeParents = (await GitInAsync(realGit, repo, "rev-list", "--parents", "-n", "1", "HEAD")).Trim().Split(' ');
        Check(divResult.Succeeded && !divResult.WasFastForward && divResult.StayedOnTargetBranch &&
              currentAfterDiv == "main" && featDivAfter == featDivSha &&
              mergeParents.Length == 3 && mergeParents[1] == mainBeforeDivSha && mergeParents[2] == featDivSha,
              "Cross-branch diverged merge switches to main, creates merge commit leaving source untouched, and stays on main");

        // 3. External git merge --no-commit vs GitNest MergeMode.DontCommit
        await GitInAsync(realGit, repo, "checkout", "-b", "feat/dc");
        await CommitFileAsync(realGit, repo, "dc-feat.txt", "dc feat\n", "dc feat commit");
        await GitInAsync(realGit, repo, "checkout", "main");
        await CommitFileAsync(realGit, repo, "dc-main.txt", "dc main\n", "dc main commit");

        await GitInAsync(realGit, repo, "merge", "--no-ff", "--no-commit", "feat/dc");
        var extMergeInspect = await MergeRevertService.InspectRepositoryInProgressAsync(repo, journal);
        Check(extMergeInspect.InProgressKind == GitInProgressKind.Merge &&
              extMergeInspect.Origin == ConflictOwnershipOrigin.ExternalOrUnknown &&
              !extMergeInspect.IsGitNestDontCommitMerge &&
              !extMergeInspect.CanOfferSafeAbort,
              "External git merge --no-commit stays ExternalOrUnknown and not GitNest DontCommit");
        await GitInAsync(realGit, repo, "merge", "--abort");

        var dcPreflight = await MergeRevertService.InspectMergeAsync(repo, "feat/dc", "main", MergeSourceKind.LocalBranch);
        var dcExec = await MergeRevertService.ExecuteMergeAsync(
            repo,
            new MergeExecutionOptions
            {
                SourceKind = MergeSourceKind.LocalBranch,
                SourceRefOrSha = "feat/dc",
                TargetBranchName = "main",
                Mode = MergeMode.DontCommit,
                Preflight = dcPreflight,
            },
            journal);
        var dcInspect = await MergeRevertService.InspectRepositoryInProgressAsync(repo, journal);
        Check(dcExec.Succeeded &&
              dcInspect.InProgressKind == GitInProgressKind.Merge &&
              dcInspect.Origin == ConflictOwnershipOrigin.GitNestOwnedClean &&
              dcInspect.IsGitNestDontCommitMerge,
              "GitNest MergeMode.DontCommit pauses cleanly and marks IsGitNestDontCommitMerge");
        await GitInAsync(realGit, repo, "merge", "--abort");

        // 4. Explicit remote snapshot requirement
        var missingRemote = await MergeRevertService.InspectMergeAsync(repo, "origin/not-fetched", "main", MergeSourceKind.RemoteBranch);
        Check(!missingRemote.IsReady && missingRemote.BlockingReason.Contains("Fetch", StringComparison.OrdinalIgnoreCase),
              "Remote branch source does not guess un-fetched remote names");

        await GitInAsync(realGit, repo, "checkout", "-b", "temp/remote-src");
        var remoteTipSha = await CommitFileAsync(realGit, repo, "remote-snap.txt", "from remote snapshot\n", "remote snapshot commit");
        await GitInAsync(realGit, repo, "push", "origin", "temp/remote-src:refs/heads/server/topic-snap");
        await GitInAsync(realGit, repo, "fetch", "origin");
        await GitInAsync(realGit, repo, "checkout", "main");
        await GitInAsync(realGit, repo, "branch", "-D", "temp/remote-src");

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
        Check(remotePreflight.IsReady && remoteMerge.Succeeded && remoteMerge.ResultHeadSha == remoteTipSha,
              "Merge from explicit fetched remote branch snapshot uses exact fetched OID");

        // 5. Block merge when target branch is checked out in another worktree / dirty / stale snapshot
        await GitInAsync(realGit, repo, "worktree", "add", "-b", "wt/branch", wtPath, "main");
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
        Check(!wtBlockedPre.IsReady && !string.IsNullOrEmpty(wtBlockedPre.TargetCheckedOutWorktreePath) &&
              wtBlockedExec.FailureKind == MergeRevertFailureKind.TargetBranchCheckedOutElsewhere,
              "Merge blocks modifying a target branch checked out in another worktree");

        var dirtyTracked = Path.Combine(repo, "ff-added.txt");
        File.WriteAllText(dirtyTracked, "dirty modification\n", Utf8NoBom);
        var dirtyPre = await MergeRevertService.InspectMergeAsync(repo, "feat/ff", "main", MergeSourceKind.LocalBranch);
        Check(!dirtyPre.IsReady && dirtyPre.HasDirtyWorkingTree, "Dirty working tree blocks merge in preflight");
        File.WriteAllText(dirtyTracked, "fast forward content\n", Utf8NoBom);

        await GitInAsync(realGit, repo, "checkout", "-b", "feat/stale-src", "main");
        await CommitFileAsync(realGit, repo, "stale.txt", "v1\n", "stale v1");
        await GitInAsync(realGit, repo, "checkout", "main");

        var stalePre = await MergeRevertService.InspectMergeAsync(repo, "feat/stale-src", "main", MergeSourceKind.LocalBranch);
        await GitInAsync(realGit, repo, "checkout", "feat/stale-src");
        await CommitFileAsync(realGit, repo, "stale.txt", "v2 externally updated\n", "stale v2");
        await GitInAsync(realGit, repo, "checkout", "main");

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
        Check(stalePre.IsReady && staleRes.BlockedBySnapshot && staleRes.FailureKind == MergeRevertFailureKind.SnapshotStale,
              "Merge rejects execution when source or target moved after confirmation snapshot");

        // 6. Conflict auto-abort + ownership guard + external CLI conflict read-only protection
        await GitInAsync(realGit, repo, "checkout", "-b", "feat/conflict-src", "main");
        await CommitFileAsync(realGit, repo, baseRel, "line-1\nfeature conflict edit\nline-3\n", "feature conflict line");
        await GitInAsync(realGit, repo, "checkout", "main");
        var mainPreConflictSha = await CommitFileAsync(realGit, repo, baseRel, "line-1\nmain conflict edit\nline-3\n", "main conflict line");

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
        var gitDir = Path.Combine(repo, ".git");
        Check(!cleanAbortRes.Succeeded &&
              cleanAbortRes.ConflictAbortedCleanly &&
              cleanAbortRes.FailureKind == MergeRevertFailureKind.ConflictAbortedCleanly &&
              cleanAbortRes.Summary.Contains("合并存在冲突，已取消，目标分支未合并", StringComparison.Ordinal) &&
              (await GitInAsync(realGit, repo, "rev-parse", "HEAD")).Trim() == mainPreConflictSha &&
              (await GitInAsync(realGit, repo, "status", "--porcelain")).Trim().Length == 0 &&
              !File.Exists(Path.Combine(gitDir, "MERGE_HEAD")),
              "Clean GitNest merge conflict auto-aborts safely and re-verifies clean target branch state");

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
                    File.WriteAllText(Path.Combine(repo, baseRel), aiEditedContent, Utf8NoBom);
                    File.WriteAllText(aiHelperPath, "kept by AI", Utf8NoBom);
                    return Task.CompletedTask;
                },
            },
            journal);
        var retainedInspect = await MergeRevertService.InspectRepositoryInProgressAsync(repo, journal);
        Check(!stopAbortRes.Succeeded &&
              stopAbortRes.AutoAbortStoppedForExternalIntervention &&
              stopAbortRes.FailureKind == MergeRevertFailureKind.ConflictOwnershipExternalIntervention &&
              File.Exists(Path.Combine(gitDir, "MERGE_HEAD")) &&
              File.ReadAllText(Path.Combine(repo, baseRel), Utf8NoBom) == aiEditedContent &&
              File.Exists(aiHelperPath) &&
              retainedInspect.Origin == ConflictOwnershipOrigin.GitNestOwnedWithExternalIntervention &&
              !retainedInspect.CanOfferSafeAbort,
              "Conflict ownership guard stops auto-abort immediately and preserves external/AI modifications");
        File.Delete(aiHelperPath);
        await GitInAsync(realGit, repo, "merge", "--abort");

        var (cliMergeExit, _, _) = await GitAllowFailureAsync(realGit, "-C", repo, "merge", "feat/conflict-src");
        Check(cliMergeExit != 0 && File.Exists(Path.Combine(gitDir, "MERGE_HEAD")),
              "Fixture sets up external CLI merge conflict");

        var externalPre = await MergeRevertService.InspectMergeAsync(repo, "feat/ff", "main", MergeSourceKind.LocalBranch);
        var externalInspect = await MergeRevertService.InspectRepositoryInProgressAsync(repo, journal);
        Check(!externalPre.IsReady &&
              externalInspect.InProgressKind == GitInProgressKind.Merge &&
              externalInspect.Origin == ConflictOwnershipOrigin.ExternalOrUnknown &&
              !externalInspect.CanOfferSafeAbort &&
              externalInspect.ConflictedPaths.Contains(baseRel) &&
              File.Exists(Path.Combine(gitDir, "MERGE_HEAD")),
              "External CLI merge conflict is identified as ExternalOrUnknown and never auto-aborted");

        var forgedVerify = await MergeRevertService.VerifyConflictOwnershipForAbortAsync(
            new ConflictOwnershipContext
            {
                OperationId = "nonexistent-op",
                OperationKind = GitOperationKind.Merge,
                RepoPath = repo,
                CanonicalGitDir = gitDir,
                ExpectedHeadSha = mainPreConflictSha,
                ExpectedPartnerSha = (await GitInAsync(realGit, repo, "rev-parse", "feat/conflict-src")).Trim(),
            },
            journal);
        Check(!forgedVerify.CanSafelyAutoAbort && forgedVerify.Origin == ConflictOwnershipOrigin.ExternalOrUnknown,
              "Ownership verifier rejects unrecorded conflict state before any abort");
        await GitInAsync(realGit, repo, "merge", "--abort");

        // 7. Distinguish pre-merge-commit hook vs GPG signing failure
        var hooksDir = Path.Combine(mrRoot, "custom-hooks");
        Directory.CreateDirectory(hooksDir);
        await GitInAsync(realGit, repo, "checkout", "-b", "feat/hook-merge", "main");
        await CommitFileAsync(realGit, repo, "hook-merge-file.txt", "hook merge test\n", "branch for merge hook and signing tests");
        await GitInAsync(realGit, repo, "checkout", "main");
        await CommitFileAsync(realGit, repo, "main-only-file.txt", "main diverge file\n", "main diverge for hook test");

        File.WriteAllText(Path.Combine(hooksDir, "pre-merge-commit"), "#!/bin/sh\necho 'custom policy blocked pre-merge-commit' >&2\nexit 1\n", Utf8NoBom);
        await GitInAsync(realGit, repo, "config", "--local", "core.hooksPath", hooksDir);
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
        await GitInAsync(realGit, repo, "merge", "--abort");
        await GitInAsync(realGit, repo, "config", "--local", "--unset", "core.hooksPath");

        var prevTestMode = Environment.GetEnvironmentVariable(FakeModeVariable);
        Environment.SetEnvironmentVariable(FakeModeVariable, "gpg-fail");
        try
        {
            await GitInAsync(realGit, repo, "config", "--local", "commit.gpgsign", "true");
            await GitInAsync(realGit, repo, "config", "--local", "gpg.format", "openpgp");
            await GitInAsync(realGit, repo, "config", "--local", "gpg.program", Environment.ProcessPath!);

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
            await GitInAsync(realGit, repo, "merge", "--abort");
        }
        finally
        {
            Environment.SetEnvironmentVariable(FakeModeVariable, prevTestMode);
        }
    }
}
