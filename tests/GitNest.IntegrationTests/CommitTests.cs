using System.Text;

using SourceGit.Commands;
using SourceGit.Models;

internal static partial class Program
{
    private static async Task RunCommitTestsAsync(string realGit, string root)
    {
        var origGlobal = Environment.GetEnvironmentVariable("GIT_CONFIG_GLOBAL");
        var origNoSystem = Environment.GetEnvironmentVariable("GIT_CONFIG_NOSYSTEM");

        var suiteRoot = Path.Combine(root, "commit-suite");
        Directory.CreateDirectory(suiteRoot);

        var emptyGlobalCfg = Path.Combine(suiteRoot, "empty.gitconfig");
        File.WriteAllText(emptyGlobalCfg, string.Empty);

        try
        {
            Environment.SetEnvironmentVariable("GIT_CONFIG_GLOBAL", emptyGlobalCfg);
            Environment.SetEnvironmentVariable("GIT_CONFIG_NOSYSTEM", "1");

            // 1. Unborn repo: missing identity, special paths, NUL unstaging without losing disk file
            var unbornRepo = Path.Combine(suiteRoot, "unborn 仓库");
            await GitAsync(realGit, "init", "-b", "main", unbornRepo);

            var trickyDir = Path.Combine(unbornRepo, "中文 目录");
            Directory.CreateDirectory(trickyDir);
            var trickyRel = "中文 目录/[slug] & #1.txt";
            var trickyAbs = Path.Combine(unbornRepo, "中文 目录", "[slug] & #1.txt");
            await File.WriteAllTextAsync(trickyAbs, "unborn content\n", Utf8NoBom);

            var (stageUnbornOk, _, _) = await GitSyncService.StagePathsAsync(unbornRepo, [trickyRel]);
            Check(stageUnbornOk, "Staging handles Chinese, space, bracket and hash filename on unborn repo");

            var snapUnborn = await QueryStagedSnapshot.CaptureAsync(unbornRepo);
            Check(snapUnborn.IsUnborn && snapUnborn.StagedChanges.Count == 1 && snapUnborn.StagedChanges[0].Path == trickyRel,
                  "Snapshot captures unborn index state with exact non-ASCII path");

            var (unstageUnbornOk, _, _) = await GitSyncService.UnstagePathsAsync(unbornRepo, [trickyRel]);
            Check(unstageUnbornOk && File.Exists(trickyAbs),
                  "Unstaging on unborn repo succeeds and preserves working tree file");

            var snapAfterUnstage = await QueryStagedSnapshot.CaptureAsync(unbornRepo);
            Check(!snapAfterUnstage.HasStagedChanges && snapAfterUnstage.UnstagedChanges.Count == 1,
                  "Unstaging on unborn repo moves file cleanly back to unstaged");

            await GitSyncService.StagePathsAsync(unbornRepo, [trickyRel]);
            var snapReadyForIdentityCheck = await QueryStagedSnapshot.CaptureAsync(unbornRepo);

            var missingIdentityCommit = await GitSyncService.CommitAsync(
                unbornRepo,
                new CommitExecutionOptions
                {
                    Message = "initial without identity",
                    ExpectedSnapshot = snapReadyForIdentityCheck,
                });
            Check(!missingIdentityCommit.Succeeded &&
                  missingIdentityCommit.FailureKind == CommitFailureKind.MissingCommitIdentity,
                  "Commit blocks cleanly when user.name/user.email identity is missing");

            var emptyGlobalHashBefore = Hash(emptyGlobalCfg);
            var identitySetRes = await new Config(unbornRepo).SetScopedAsync(GitConfigWriteScope.Local, "user.name", "GitNest Author");
            var emailSetRes = await new Config(unbornRepo).SetScopedAsync(GitConfigWriteScope.Local, "user.email", "author@example.invalid");
            Check(identitySetRes.IsSuccess && emailSetRes.IsSuccess && Hash(emptyGlobalCfg) == emptyGlobalHashBefore,
                  "Local commit identity configures repository only without altering global config");

            var firstCommit = await GitSyncService.CommitAsync(
                unbornRepo,
                new CommitExecutionOptions
                {
                    Message = "feat: init tricky path",
                    ExpectedSnapshot = snapReadyForIdentityCheck,
                });
            Check(firstCommit.Succeeded && !string.IsNullOrEmpty(firstCommit.CommitSha),
                  "Commit succeeds after local identity configuration");

            // 2. Partial staging + external index mutation snapshot guard + empty commit + lock + hook
            var partialFile = Path.Combine(unbornRepo, "partial.txt");
            var sideFile = Path.Combine(unbornRepo, "side.txt");
            await CommitFileAsync(realGit, unbornRepo, "partial.txt", "line1\n", "add partial base");

            await File.WriteAllTextAsync(partialFile, "line1\nline2-staged\n", Utf8NoBom);
            await GitInAsync(realGit, unbornRepo, "add", "partial.txt");
            await File.WriteAllTextAsync(partialFile, "line1\nline2-staged\nline3-unstaged\n", Utf8NoBom);
            await File.WriteAllTextAsync(sideFile, "unstaged side edit\n", Utf8NoBom);

            var partialSnap = await QueryStagedSnapshot.CaptureAsync(unbornRepo);
            Check(partialSnap.PartiallyStagedPaths.Contains("partial.txt") &&
                  partialSnap.StagedChanges.Any(c => c.Path == "partial.txt" && c.IsPartiallyStaged) &&
                  partialSnap.UnstagedChanges.Any(c => c.Path == "partial.txt" && c.IsPartiallyStaged),
                  "Snapshot identifies partial staging split in both staged and unstaged lists");
            Check(partialSnap.GetPartiallyStagedTargets(["partial.txt", "side.txt"]).SequenceEqual(["partial.txt"]),
                  "Partial staging precheck flags only paths whose partial split would be overwritten");

            await File.WriteAllTextAsync(sideFile, "external unstaged edit\n", Utf8NoBom);
            var partialCommit = await GitSyncService.CommitAsync(
                unbornRepo,
                new CommitExecutionOptions
                {
                    Message = "feat: commit staged part only",
                    ExpectedSnapshot = partialSnap,
                });
            Check(partialCommit.Succeeded,
                  "Normal commit commits staged snapshot cleanly even when unstaged files change");

            var committedPartialContent = await GitInAsync(realGit, unbornRepo, "show", "HEAD:partial.txt");
            var diskPartialContent = await File.ReadAllTextAsync(partialFile, Encoding.UTF8);
            Check(committedPartialContent == "line1\nline2-staged\n" &&
                  diskPartialContent == "line1\nline2-staged\nline3-unstaged\n",
                  "Partial staging commit preserves unstaged modifications on disk without losing them");

            await GitSyncService.StagePathsAsync(unbornRepo, ["partial.txt", "side.txt"]);
            var beforeExternalTamper = await QueryStagedSnapshot.CaptureAsync(unbornRepo);
            var headBeforeTamper = (await GitInAsync(realGit, unbornRepo, "rev-parse", "HEAD")).Trim();

            await File.WriteAllTextAsync(partialFile, "line1\nline2-staged\nline3-AI-mutated\n", Utf8NoBom);
            await GitInAsync(realGit, unbornRepo, "add", "partial.txt");

            var tamperedResult = await GitSyncService.CommitAsync(
                unbornRepo,
                new CommitExecutionOptions
                {
                    Message = "should abort due to staged snapshot change",
                    ExpectedSnapshot = beforeExternalTamper,
                });
            var headAfterTamper = (await GitInAsync(realGit, unbornRepo, "rev-parse", "HEAD")).Trim();
            Check(!tamperedResult.Succeeded &&
                  tamperedResult.FailureKind == CommitFailureKind.StagedSnapshotChanged &&
                  headAfterTamper == headBeforeTamper,
                  "External staged index mutation aborts commit cleanly without modifying HEAD");

            var baselineBeforeReset = await QueryStagedSnapshot.CaptureAsync(unbornRepo);
            var afterTamperCommit = await GitSyncService.CommitAsync(
                unbornRepo,
                new CommitExecutionOptions
                {
                    Message = "after tamper commit",
                    ExpectedSnapshot = baselineBeforeReset,
                });
            Check(afterTamperCommit.Succeeded, "Re-confirming updated snapshot allows commit to proceed");

            var emptySnap = await QueryStagedSnapshot.CaptureAsync(unbornRepo);
            var refusedEmpty = await GitSyncService.CommitAsync(
                unbornRepo,
                new CommitExecutionOptions
                {
                    Message = "unexpected empty",
                    AllowEmpty = false,
                    ExpectedSnapshot = emptySnap,
                });
            Check(!refusedEmpty.Succeeded && refusedEmpty.FailureKind == CommitFailureKind.NothingStaged,
                  "Normal commit rejects empty commit cleanly when AllowEmpty is false");

            var indexLockPath = Path.Combine(unbornRepo, ".git", "index.lock");
            await File.WriteAllTextAsync(indexLockPath, "locked by another tool");
            try
            {
                await File.WriteAllTextAsync(sideFile, "edit while locked\n", Utf8NoBom);
                var (lockStageOk, lockInfo, _) = await GitSyncService.StagePathsAsync(unbornRepo, ["side.txt"]);
                Check(!lockStageOk && lockInfo.IsLocked && File.Exists(indexLockPath),
                      "Staging respects native index.lock and does not delete lock file");

                var lockCommitRes = await GitSyncService.CommitAsync(
                    unbornRepo,
                    new CommitExecutionOptions { Message = "locked commit", AllowEmpty = true });
                Check(!lockCommitRes.Succeeded &&
                      lockCommitRes.FailureKind == CommitFailureKind.RepositoryLocked &&
                      File.Exists(indexLockPath),
                      "Commit respects native index.lock and refuses to proceed without deleting lock file");
            }
            finally
            {
                if (File.Exists(indexLockPath))
                    File.Delete(indexLockPath);
            }

            // Pre-commit hook failure reporting
            var hookPath = Path.Combine(unbornRepo, ".git", "hooks", "pre-commit");
            await File.WriteAllTextAsync(hookPath, "#!/bin/sh\necho \"pre-commit check rejected by policy\" >&2\nexit 1\n", Utf8NoBom);
            await GitSyncService.StagePathsAsync(unbornRepo, ["side.txt"]);
            var hookSnap = await QueryStagedSnapshot.CaptureAsync(unbornRepo);
            var hookFailRes = await GitSyncService.CommitAsync(
                unbornRepo,
                new CommitExecutionOptions { Message = "hook blocked commit", ExpectedSnapshot = hookSnap });
            File.Delete(hookPath);
            Check(!hookFailRes.Succeeded &&
                  hookFailRes.FailureKind == CommitFailureKind.HookRejected &&
                  hookFailRes.DiagnosticDetail.Contains("pre-commit check rejected by policy", StringComparison.Ordinal),
                  "Pre-commit hook rejection fails explicitly and surfaces hook output");

            // 3. Git commit.template config & file stay untouched + recent history reuse
            var templateFile = Path.Combine(suiteRoot, ".git-commit-template.txt");
            await File.WriteAllTextAsync(templateFile, "# preserved user template\n", Utf8NoBom);
            await GitInAsync(realGit, unbornRepo, "config", "--local", "commit.template", templateFile);
            var templateFileBefore = await File.ReadAllBytesAsync(templateFile);
            var templateCfgBefore = (await GitInAsync(realGit, unbornRepo, "config", "--local", "--get", "commit.template")).Trim();

            var manualMessage = "feat: manual commit ignoring template";
            var commitRes = await GitSyncService.CommitAsync(
                unbornRepo,
                new CommitExecutionOptions { Message = manualMessage, ExpectedSnapshot = hookSnap });
            var headMsg = (await GitInAsync(realGit, unbornRepo, "log", "-1", "--pretty=%B")).Trim();
            var uiStates = RepositoryUIStates.Load(Path.Combine(unbornRepo, ".git"));
            uiStates.AddRecentCommitMessage(manualMessage);
            uiStates.Save();
            var reloadedUiStates = RepositoryUIStates.Load(Path.Combine(unbornRepo, ".git"));
            var changeRes = await new QueryLocalChanges(unbornRepo, includeUntracked: true).GetResultAsync();
            var templateFileAfter = await File.ReadAllBytesAsync(templateFile);
            Check(commitRes.Succeeded &&
                  headMsg == manualMessage &&
                  changeRes.Count == 0 &&
                  reloadedUiStates.RecentCommitMessages.Count == 1 &&
                  reloadedUiStates.RecentCommitMessages[0] == manualMessage &&
                  templateFileBefore.SequenceEqual(templateFileAfter) &&
                  templateCfgBefore == (await GitInAsync(realGit, unbornRepo, "config", "--local", "--get", "commit.template")).Trim(),
                  "Normal commit and recent history reuse succeed while preserving Git commit.template config and file untouched");
        }
        finally
        {
            Environment.SetEnvironmentVariable("GIT_CONFIG_GLOBAL", origGlobal);
            Environment.SetEnvironmentVariable("GIT_CONFIG_NOSYSTEM", origNoSystem);
        }
    }
}
