using SourceGit.Commands;
using SourceGit.Models;

internal static partial class Program
{
    private static async Task RunRevertTestsAsync(string realGit, string root)
    {
        var revRoot = Path.Combine(root, "revert-suite");
        Directory.CreateDirectory(revRoot);
        var repo = Path.Combine(revRoot, "repo");
        var journal = new OperationJournalStore(Path.Combine(revRoot, "revert-journal.json"));

        await InitRepoAsync(realGit, repo, "main", "Alice", "alice@example.invalid");
        var initSha = await CommitFileAsync(realGit, repo, "base.txt", "base\n", "initial commit");

        // 0. Guard check on Revert command wrapper
        var cmdRevert = new Revert(repo, initSha, autoCommit: true);
        Check(!cmdRevert.Args.Contains("-m 1", StringComparison.Ordinal) &&
              cmdRevert.Args.Contains("--no-edit", StringComparison.Ordinal),
              "Commands.Revert removes hardcoded -m 1 and safely targets single commits");

        // 1. Merge commit block vs single normal commit revert
        await GitInAsync(realGit, repo, "checkout", "-b", "feat/side");
        await CommitFileAsync(realGit, repo, "side.txt", "side\n", "side commit");
        await GitInAsync(realGit, repo, "checkout", "main");
        await CommitFileAsync(realGit, repo, "main.txt", "main\n", "main commit");
        await GitInAsync(realGit, repo, "merge", "--no-ff", "feat/side", "-m", "merge side");
        var mergeCommitSha = (await GitInAsync(realGit, repo, "rev-parse", "HEAD")).Trim();

        var mergeRevPre = await MergeRevertService.InspectRevertAsync(repo, mergeCommitSha);
        var mergeRevExec = await MergeRevertService.ExecuteRevertAsync(
            repo,
            new RevertExecutionOptions { CommitSha = mergeCommitSha, Preflight = mergeRevPre },
            journal);
        Check(mergeRevPre.IsMergeCommit && !mergeRevPre.IsReady && !mergeRevExec.Succeeded &&
              mergeRevExec.FailureKind == MergeRevertFailureKind.MergeCommitNotSupported,
              "Revert strictly blocks merge commits instead of guessing -m 1");

        var normalCommitToRevert = await CommitFileAsync(realGit, repo, "to-revert.txt", "bad line\n", "commit to revert");
        var normalRevPre = await MergeRevertService.InspectRevertAsync(repo, normalCommitToRevert);
        Check(normalRevPre.IsReady && !normalRevPre.IsMergeCommit &&
              normalRevPre.AffectedChanges.Any(c => c.Path == "to-revert.txt"),
              "Single normal commit revert preflight succeeds and lists affected files");

        var normalRevExec = await MergeRevertService.ExecuteRevertAsync(
            repo,
            new RevertExecutionOptions { CommitSha = normalCommitToRevert, AutoCommit = true, Preflight = normalRevPre },
            journal);
        Check(normalRevExec.Succeeded && !File.Exists(Path.Combine(repo, "to-revert.txt")) &&
              (await GitInAsync(realGit, repo, "rev-list", "--count", normalCommitToRevert)).Trim().Length > 0,
              "Single normal commit revert creates reverse commit while preserving history");

        // 2. Revert conflict: clean auto-abort vs external intervention stop
        await CommitFileAsync(realGit, repo, "revert-conflict.txt", "v1\n", "introduce revert-conflict v1");
        var v2Sha = await CommitFileAsync(realGit, repo, "revert-conflict.txt", "v2 conflicting later line\n", "edit revert-conflict to v2");
        var v3Sha = await CommitFileAsync(realGit, repo, "revert-conflict.txt", "v3 overlapping line\n", "edit revert-conflict to v3");
        var gitDir = Path.Combine(repo, ".git");

        var revConflictPre = await MergeRevertService.InspectRevertAsync(repo, v2Sha);
        var revCleanAbort = await MergeRevertService.ExecuteRevertAsync(
            repo,
            new RevertExecutionOptions { CommitSha = v2Sha, Preflight = revConflictPre },
            journal);
        Check(!revCleanAbort.Succeeded && revCleanAbort.ConflictAbortedCleanly &&
              revCleanAbort.FailureKind == MergeRevertFailureKind.ConflictAbortedCleanly &&
              (await GitInAsync(realGit, repo, "rev-parse", "HEAD")).Trim() == v3Sha &&
              !File.Exists(Path.Combine(gitDir, "REVERT_HEAD")) &&
              (await GitInAsync(realGit, repo, "status", "--porcelain")).Trim().Length == 0,
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
                    File.WriteAllText(Path.Combine(repo, "revert-conflict.txt"), aiRevertText, Utf8NoBom);
                    return Task.CompletedTask;
                },
            },
            journal);
        Check(!revStopAbort.Succeeded && revStopAbort.AutoAbortStoppedForExternalIntervention &&
              revStopAbort.FailureKind == MergeRevertFailureKind.ConflictOwnershipExternalIntervention &&
              File.Exists(Path.Combine(gitDir, "REVERT_HEAD")) &&
              File.ReadAllText(Path.Combine(repo, "revert-conflict.txt"), Utf8NoBom) == aiRevertText,
              "Revert conflict ownership guard stops auto-abort when external edits occur after conflict");
        await GitInAsync(realGit, repo, "revert", "--abort");

        // 3. Distinguish prepare-commit-msg hook failure vs GPG signing failure
        var hooksDir = Path.Combine(revRoot, "custom-hooks");
        Directory.CreateDirectory(hooksDir);
        File.WriteAllText(Path.Combine(hooksDir, "prepare-commit-msg"), "#!/bin/sh\necho 'custom policy blocked prepare-commit-msg' >&2\nexit 1\n", Utf8NoBom);
        await GitInAsync(realGit, repo, "config", "--local", "core.hooksPath", hooksDir);

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
            await GitInAsync(realGit, repo, "revert", "--abort");
        else
            await GitInAsync(realGit, repo, "reset", "--hard", "HEAD");
        await GitInAsync(realGit, repo, "config", "--local", "--unset", "core.hooksPath");

        var prevTestMode = Environment.GetEnvironmentVariable(FakeModeVariable);
        Environment.SetEnvironmentVariable(FakeModeVariable, "gpg-fail");
        try
        {
            await GitInAsync(realGit, repo, "config", "--local", "commit.gpgsign", "true");
            await GitInAsync(realGit, repo, "config", "--local", "gpg.format", "openpgp");
            await GitInAsync(realGit, repo, "config", "--local", "gpg.program", Environment.ProcessPath!);

            var signRevPre = await MergeRevertService.InspectRevertAsync(repo, v3Sha);
            var signRevRes = await MergeRevertService.ExecuteRevertAsync(
                repo,
                new RevertExecutionOptions { CommitSha = v3Sha, Preflight = signRevPre },
                journal);
            Check(!signRevRes.Succeeded &&
                  signRevRes.FailureKind == MergeRevertFailureKind.SigningFailed &&
                  !signRevRes.ConflictAbortedCleanly &&
                  signRevRes.DiagnosticDetail.Contains("gpg failed", StringComparison.Ordinal) &&
                  (await GitInAsync(realGit, repo, "status", "--porcelain")).Trim().Length > 0,
                  "Revert distinguishes GPG signing failure from conflicts and preserves staged diagnostic state");
            if (File.Exists(Path.Combine(gitDir, "REVERT_HEAD")))
                await GitInAsync(realGit, repo, "revert", "--abort");
            else
                await GitInAsync(realGit, repo, "reset", "--hard", "HEAD");
        }
        finally
        {
            Environment.SetEnvironmentVariable(FakeModeVariable, prevTestMode);
        }
    }
}
