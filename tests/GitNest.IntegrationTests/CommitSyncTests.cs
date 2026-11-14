using System.Diagnostics;
using System.Text;

using SourceGit.Commands;
using SourceGit.Models;

internal static partial class Program
{
    private static async Task RunCommitSyncTestsAsync(string realGit, string root)
    {
        var origGlobal = Environment.GetEnvironmentVariable("GIT_CONFIG_GLOBAL");
        var origNoSystem = Environment.GetEnvironmentVariable("GIT_CONFIG_NOSYSTEM");

        var suiteRoot = Path.Combine(root, "commit-sync-suite");
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
            await File.WriteAllTextAsync(trickyAbs, "unborn content\n", new UTF8Encoding(false));

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

            // Set repository-local identity explicitly (do not mutate global config)
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

            // 2. Partial staging + external index mutation snapshot guard + lock check
            var partialFile = Path.Combine(unbornRepo, "partial.txt");
            var sideFile = Path.Combine(unbornRepo, "side.txt");
            await File.WriteAllTextAsync(partialFile, "line1\n", new UTF8Encoding(false));
            await GitAsync(realGit, "-C", unbornRepo, "add", "partial.txt");
            await GitAsync(realGit, "-C", unbornRepo, "commit", "-m", "add partial base");

            await File.WriteAllTextAsync(partialFile, "line1\nline2-staged\n", new UTF8Encoding(false));
            await GitAsync(realGit, "-C", unbornRepo, "add", "partial.txt");
            await File.WriteAllTextAsync(partialFile, "line1\nline2-staged\nline3-unstaged\n", new UTF8Encoding(false));
            await File.WriteAllTextAsync(sideFile, "unstaged side edit\n", new UTF8Encoding(false));

            var partialSnap = await QueryStagedSnapshot.CaptureAsync(unbornRepo);
            Check(partialSnap.PartiallyStagedPaths.Contains("partial.txt") &&
                  partialSnap.StagedChanges.Any(c => c.Path == "partial.txt" && c.IsPartiallyStaged) &&
                  partialSnap.UnstagedChanges.Any(c => c.Path == "partial.txt" && c.IsPartiallyStaged),
                "Snapshot identifies partial staging split in both staged and unstaged lists");
            Check(partialSnap.GetPartiallyStagedTargets(["partial.txt", "side.txt"]).SequenceEqual(["partial.txt"]),
                "Partial staging precheck flags only paths whose partial split would be overwritten");

            // External unstaged modification on unrelated file must NOT invalidate staged snapshot
            await File.WriteAllTextAsync(sideFile, "external unstaged edit\n", new UTF8Encoding(false));
            var partialCommit = await GitSyncService.CommitAsync(
                unbornRepo,
                new CommitExecutionOptions
                {
                    Message = "feat: commit staged part only",
                    ExpectedSnapshot = partialSnap,
                });
            Check(partialCommit.Succeeded,
                "Normal commit commits staged snapshot cleanly even when unstaged files change");

            var committedPartialContent = await GitAsync(realGit, "-C", unbornRepo, "show", "HEAD:partial.txt");
            var diskPartialContent = await File.ReadAllTextAsync(partialFile, Encoding.UTF8);
            Check(committedPartialContent == "line1\nline2-staged\n" &&
                  diskPartialContent == "line1\nline2-staged\nline3-unstaged\n",
                "Partial staging commit preserves unstaged modifications on disk without losing them");

            // Now test external index mutation invalidating confirmation snapshot
            await GitSyncService.StagePathsAsync(unbornRepo, ["partial.txt", "side.txt"]);
            var beforeExternalTamper = await QueryStagedSnapshot.CaptureAsync(unbornRepo);
            var headBeforeTamper = (await GitAsync(realGit, "-C", unbornRepo, "rev-parse", "HEAD")).Trim();

            await File.WriteAllTextAsync(partialFile, "line1\nline2-staged\nline3-AI-mutated\n", new UTF8Encoding(false));
            await GitAsync(realGit, "-C", unbornRepo, "add", "partial.txt");

            var tamperedResult = await GitSyncService.CommitAsync(
                unbornRepo,
                new CommitExecutionOptions
                {
                    Message = "should abort due to staged snapshot change",
                    ExpectedSnapshot = beforeExternalTamper,
                });
            var headAfterTamper = (await GitAsync(realGit, "-C", unbornRepo, "rev-parse", "HEAD")).Trim();
            Check(!tamperedResult.Succeeded &&
                  tamperedResult.FailureKind == CommitFailureKind.StagedSnapshotChanged &&
                  headAfterTamper == headBeforeTamper,
                "External staged index mutation aborts commit cleanly without modifying HEAD");

            // Empty commit refused when nothing staged unless AllowEmpty=true
            var baselineBeforeReset = await QueryStagedSnapshot.CaptureAsync(unbornRepo);
            var emptyDenied = await GitSyncService.CommitAsync(
                unbornRepo,
                new CommitExecutionOptions
                {
                    Message = "after tamper commit",
                    ExpectedSnapshot = baselineBeforeReset,
                });
            Check(emptyDenied.Succeeded, "Re-confirming updated snapshot allows commit to proceed");

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

            // Git native lock respect (.git/index.lock must never be deleted automatically)
            var indexLockPath = Path.Combine(unbornRepo, ".git", "index.lock");
            await File.WriteAllTextAsync(indexLockPath, "locked by another tool");
            try
            {
                await File.WriteAllTextAsync(sideFile, "edit while locked\n", new UTF8Encoding(false));
                var (lockStageOk, lockInfo, _) = await GitSyncService.StagePathsAsync(unbornRepo, ["side.txt"]);
                Check(!lockStageOk && lockInfo.IsLocked && File.Exists(indexLockPath),
                    "Staging respects native index.lock and does not delete lock file");

                var lockCommitRes = await GitSyncService.CommitAsync(
                    unbornRepo,
                    new CommitExecutionOptions
                    {
                        Message = "locked commit",
                        AllowEmpty = true,
                    });
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

            // Pre-commit hook failure reporting and temp file cleanup
            var hookPath = Path.Combine(unbornRepo, ".git", "hooks", "pre-commit");
            await File.WriteAllTextAsync(hookPath, "#!/bin/sh\necho \"pre-commit check rejected by policy\" >&2\nexit 1\n", new UTF8Encoding(false));
            await GitSyncService.StagePathsAsync(unbornRepo, ["side.txt"]);
            var hookSnap = await QueryStagedSnapshot.CaptureAsync(unbornRepo);
            var hookFailRes = await GitSyncService.CommitAsync(
                unbornRepo,
                new CommitExecutionOptions
                {
                    Message = "hook blocked commit",
                    ExpectedSnapshot = hookSnap,
                });
            File.Delete(hookPath);
            Check(!hookFailRes.Succeeded &&
                  hookFailRes.FailureKind == CommitFailureKind.HookRejected &&
                  hookFailRes.DiagnosticDetail.Contains("pre-commit check rejected by policy", StringComparison.Ordinal),
                "Pre-commit hook rejection fails explicitly and surfaces hook output");

            // Amend unstage with non-ASCII path
            await GitSyncService.CommitAsync(
                unbornRepo,
                new CommitExecutionOptions
                {
                    Message = "commit side before amend unstage",
                    ExpectedSnapshot = hookSnap,
                });
            var changeRes = await new QueryLocalChanges(unbornRepo, includeUntracked: true).GetResultAsync();
            Check(changeRes.Count == 0, "Working copy is clean after commit");

            // Linked worktree shared common-dir write guard resolution
            var linkedWt = Path.Combine(suiteRoot, "linked-wt");
            await GitAsync(realGit, "-C", unbornRepo, "worktree", "add", "-b", "wt-branch", linkedWt, "HEAD");
            var (mainGitDir, mainCommonDir) = await RepositoryWriteGuard.ResolveGitDirectoriesAsync(unbornRepo);
            var (wtGitDir, wtCommonDir) = await RepositoryWriteGuard.ResolveGitDirectoriesAsync(linkedWt);
            Check(!string.Equals(mainGitDir, wtGitDir, StringComparison.OrdinalIgnoreCase) &&
                  string.Equals(mainCommonDir, wtCommonDir, StringComparison.OrdinalIgnoreCase),
                "Linked worktree resolves distinct git-dir and shared canonical common-dir for write serialization");

            var activeConcurrency = 0;
            var maxObservedConcurrency = 0;
            async Task RunSerializedSlotAsync(string path, string opName)
            {
                await using var guard = await RepositoryWriteGuard.TryEnterAsync(path, opName, checkIndexLock: false);
                var nowActive = Interlocked.Increment(ref activeConcurrency);
                if (nowActive > maxObservedConcurrency)
                    maxObservedConcurrency = nowActive;
                await Task.Delay(35);
                Interlocked.Decrement(ref activeConcurrency);
            }

            await Task.WhenAll(
                RunSerializedSlotAsync(unbornRepo, "OpMain"),
                RunSerializedSlotAsync(linkedWt, "OpWorktree"));
            Check(maxObservedConcurrency == 1,
                "Write guard serializes concurrent operations across main repo and linked worktree");

            await GitAsync(realGit, "-C", unbornRepo, "worktree", "remove", "--force", linkedWt);

            // 3. Remote sync suite: Fetch, Fast-Forward Pull, Diverged Pull, Push Single Branch, Commit & Push two-step retry
            var bareOrigin = Path.Combine(suiteRoot, "origin.git");
            var bareBackup = Path.Combine(suiteRoot, "backup.git");
            var cloneA = Path.Combine(suiteRoot, "clone-a");
            var cloneB = Path.Combine(suiteRoot, "clone-b");

            await GitAsync(realGit, "init", "--bare", "-b", "main", bareOrigin);
            await GitAsync(realGit, "init", "--bare", "-b", "main", bareBackup);

            var seedDir = Path.Combine(suiteRoot, "seed");
            await GitAsync(realGit, "init", "-b", "main", seedDir);
            await GitAsync(realGit, "-C", seedDir, "config", "--local", "user.name", "Seed Bot");
            await GitAsync(realGit, "-C", seedDir, "config", "--local", "user.email", "seed@example.invalid");
            await File.WriteAllTextAsync(Path.Combine(seedDir, "shared.txt"), "seed-v1\n", new UTF8Encoding(false));
            await File.WriteAllTextAsync(Path.Combine(seedDir, "other.txt"), "other-v1\n", new UTF8Encoding(false));
            await GitAsync(realGit, "-C", seedDir, "add", ".");
            await GitAsync(realGit, "-C", seedDir, "commit", "-m", "seed commit");
            await GitAsync(realGit, "-C", seedDir, "remote", "add", "origin", bareOrigin);
            await GitAsync(realGit, "-C", seedDir, "push", "-u", "origin", "main");
            await GitAsync(realGit, "-C", seedDir, "remote", "add", "backup", bareBackup);
            await GitAsync(realGit, "-C", seedDir, "checkout", "-b", "feat/no-upstream");
            await File.WriteAllTextAsync(Path.Combine(seedDir, "backup-only.txt"), "backup\n", new UTF8Encoding(false));
            await GitAsync(realGit, "-C", seedDir, "add", "backup-only.txt");
            await GitAsync(realGit, "-C", seedDir, "commit", "-m", "existing branch on backup");
            await GitAsync(realGit, "-C", seedDir, "push", "backup", "refs/heads/feat/no-upstream:refs/heads/feat/no-upstream");

            await GitAsync(realGit, "clone", bareOrigin, cloneA);
            await GitAsync(realGit, "clone", bareOrigin, cloneB);
            foreach (var c in new[] { cloneA, cloneB })
            {
                await GitAsync(realGit, "-C", c, "config", "--local", "user.name", "Clone User");
                await GitAsync(realGit, "-C", c, "config", "--local", "user.email", "clone@example.invalid");
            }
            await GitAsync(realGit, "-C", cloneA, "remote", "add", "backup", bareBackup);

            var originRemote = new SourceGit.Models.Remote { Name = "origin", URL = bareOrigin };
            var backupRemote = new SourceGit.Models.Remote { Name = "backup", URL = bareBackup };

            // Overview helper never fakes unknown/unfetched state as zero
            var unfetchedSummary = RepositorySyncOverviewBuilder.Build(
                uncommittedCount: 2,
                currentBranchName: "main",
                upstreamFriendlyName: "origin/main",
                aheadCount: 1,
                behindCount: 0,
                hasRemotes: true,
                isUpstreamGone: false,
                lastFetchAt: null,
                lastFetchFailed: false,
                lastFetchError: string.Empty);
            Check(unfetchedSummary.HasUnknownRemoteState &&
                  unfetchedSummary.SummaryText.Contains("远端状态未获取", StringComparison.Ordinal) &&
                  !unfetchedSummary.SummaryText.Contains("0 次远端提交待拉取", StringComparison.Ordinal),
                "Status summary distinguishes unfetched state from 0 behind commits");

            var noUpstreamSummary = RepositorySyncOverviewBuilder.Build(
                uncommittedCount: 1,
                currentBranchName: "feat/local",
                upstreamFriendlyName: string.Empty,
                aheadCount: 0,
                behindCount: 0,
                hasRemotes: true,
                isUpstreamGone: false,
                lastFetchAt: DateTime.Now,
                lastFetchFailed: false,
                lastFetchError: string.Empty);
            Check(noUpstreamSummary.SummaryText.Contains("未设置上游分支", StringComparison.Ordinal),
                "Status summary states missing upstream explicitly instead of showing 0");

            // Push single-branch only (never push unpushed local branches or tags!)
            await GitAsync(realGit, "-C", cloneA, "checkout", "-b", "local-unpushed-branch");
            await File.WriteAllTextAsync(Path.Combine(cloneA, "unpushed.txt"), "branch only\n", new UTF8Encoding(false));
            await GitAsync(realGit, "-C", cloneA, "add", "unpushed.txt");
            await GitAsync(realGit, "-C", cloneA, "commit", "-m", "local branch commit");

            await GitAsync(realGit, "-C", cloneA, "checkout", "main");
            await File.WriteAllTextAsync(Path.Combine(cloneA, "shared.txt"), "cloneA-v2\n", new UTF8Encoding(false));
            await GitAsync(realGit, "-C", cloneA, "add", "shared.txt");
            await GitAsync(realGit, "-C", cloneA, "commit", "-m", "main commit v2");
            await GitAsync(realGit, "-C", cloneA, "tag", "v9.9.9-local");

            var singlePushRes = await GitSyncService.PushSingleBranchAsync(
                cloneA,
                new PushExecutionOptions
                {
                    LocalBranch = "main",
                    Remote = originRemote,
                    RemoteBranch = "main",
                    SetUpstream = false,
                });
            var bareBranchesAfterPush = await GitAsync(realGit, "-C", bareOrigin, "for-each-ref", "--format=%(refname)", "refs/");
            Check(singlePushRes.Succeeded &&
                  bareBranchesAfterPush.Contains("refs/heads/main", StringComparison.Ordinal) &&
                  !bareBranchesAfterPush.Contains("local-unpushed-branch", StringComparison.Ordinal) &&
                  !bareBranchesAfterPush.Contains("v9.9.9-local", StringComparison.Ordinal),
                "Single-branch push updates only target branch without pushing local tags or other branches");

            // No-upstream branch checks multi-remote target collision vs clean tracking creation
            await GitAsync(realGit, "-C", cloneA, "checkout", "-b", "feat/no-upstream");
            await File.WriteAllTextAsync(Path.Combine(cloneA, "feat.txt"), "new feat\n", new UTF8Encoding(false));
            await GitAsync(realGit, "-C", cloneA, "add", "feat.txt");
            await GitAsync(realGit, "-C", cloneA, "commit", "-m", "feat commit");

            var collisionPush = await GitSyncService.PushSingleBranchAsync(
                cloneA,
                new PushExecutionOptions
                {
                    LocalBranch = "feat/no-upstream",
                    Remote = backupRemote,
                    RemoteBranch = "feat/no-upstream",
                    SetUpstream = true,
                    ConfirmOverwriteExistingRemoteBranchWithoutUpstream = false,
                });
            Check(!collisionPush.Succeeded && collisionPush.Outcome == PushOutcomeKind.RemoteBranchExistsWithoutUpstream,
                "Push without upstream stops to confirm when selected remote branch already exists on target remote");

            var originNewBranchPush = await GitSyncService.PushSingleBranchAsync(
                cloneA,
                new PushExecutionOptions
                {
                    LocalBranch = "feat/no-upstream",
                    Remote = originRemote,
                    RemoteBranch = "feat/no-upstream",
                    SetUpstream = true,
                });
            var configuredUpstream = (await GitAsync(realGit, "-C", cloneA, "rev-parse", "--abbrev-ref", "--symbolic-full-name", "@{u}")).Trim();
            Check(originNewBranchPush.Succeeded && configuredUpstream == "origin/feat/no-upstream",
                "Push without upstream sets explicit tracking branch on selected remote");

            await GitAsync(realGit, "-C", cloneA, "checkout", "main");

            // Two-step Commit & Push: push failure preserves commit and RetryPushOnly pushes without duplicating commit
            var preReceiveHook = Path.Combine(bareOrigin, "hooks", "pre-receive");
            await File.WriteAllTextAsync(preReceiveHook, "#!/bin/sh\necho \"protected branch rule declined push\" >&2\nexit 1\n", new UTF8Encoding(false));

            await File.WriteAllTextAsync(Path.Combine(cloneA, "two-step.txt"), "step1\n", new UTF8Encoding(false));
            await GitSyncService.StagePathsAsync(cloneA, ["two-step.txt"]);
            var twoStepSnap = await QueryStagedSnapshot.CaptureAsync(cloneA);
            var commitCountBefore = int.Parse((await GitAsync(realGit, "-C", cloneA, "rev-list", "--count", "HEAD")).Trim());

            var twoStepSession = await GitSyncService.CommitAndPushAsync(
                cloneA,
                new CommitExecutionOptions
                {
                    Message = "feat: two-step commit",
                    ExpectedSnapshot = twoStepSnap,
                },
                new PushExecutionOptions
                {
                    LocalBranch = "main",
                    Remote = originRemote,
                    RemoteBranch = "main",
                });

            var commitCountAfterFailPush = int.Parse((await GitAsync(realGit, "-C", cloneA, "rev-list", "--count", "HEAD")).Trim());
            Check(twoStepSession.CommitSucceeded &&
                  twoStepSession.PushAttempted &&
                  !twoStepSession.PushSucceeded &&
                  twoStepSession.PushResult.Outcome == PushOutcomeKind.ProtectedOrHookRejected &&
                  twoStepSession.CanRetryPushOnly &&
                  commitCountAfterFailPush == commitCountBefore + 1,
                "Two-step Commit & Push retains local commit when push is rejected and enables retry-push-only");

            File.Delete(preReceiveHook);
            var retriedSession = await GitSyncService.RetryPushSessionAsync(twoStepSession);
            var commitCountAfterRetry = int.Parse((await GitAsync(realGit, "-C", cloneA, "rev-list", "--count", "HEAD")).Trim());
            var bareMainShaAfterRetry = (await GitAsync(realGit, "-C", bareOrigin, "rev-parse", "refs/heads/main")).Trim();
            Check(retriedSession.IsFullySucceeded &&
                  commitCountAfterRetry == commitCountAfterFailPush &&
                  bareMainShaAfterRetry == retriedSession.CommitResult.CommitSha,
                "Retrying push pushes the existing commit cleanly without creating duplicate commits");

            // Fast-Forward Pull respects pull.rebase=true without mutating config
            await GitAsync(realGit, "-C", cloneB, "config", "--local", "pull.rebase", "true");
            var fetchRes = await GitSyncService.FetchAsync(cloneB, [originRemote]);
            Check(fetchRes.Succeeded && fetchRes.FetchedAtUtc.HasValue,
                "Fetch updates remote refs and records timestamp");

            var ffPullRes = await GitSyncService.PullFastForwardAsync(cloneB, originRemote, "main");
            var pullRebaseStillSet = (await GitAsync(realGit, "-C", cloneB, "config", "--local", "--get", "pull.rebase")).Trim();
            Check(ffPullRes.Succeeded &&
                  ffPullRes.Outcome == PullOutcomeKind.UpdatedFastForward &&
                  ffPullRes.AfterHead == bareMainShaAfterRetry &&
                  pullRebaseStillSet == "true",
                "Fast-forward pull advances cleanly while preserving user pull.rebase config untouched");

            // Local uncommitted modification conflict during fast-forward pull is blocked safely
            await File.WriteAllTextAsync(Path.Combine(cloneA, "other.txt"), "remote-change\n", new UTF8Encoding(false));
            await GitAsync(realGit, "-C", cloneA, "add", "other.txt");
            await GitAsync(realGit, "-C", cloneA, "commit", "-m", "remote touches other.txt");
            await GitAsync(realGit, "-C", cloneA, "push", "origin", "main");

            await File.WriteAllTextAsync(Path.Combine(cloneB, "other.txt"), "local-dirty-uncommitted\n", new UTF8Encoding(false));
            var dirtyPullRes = await GitSyncService.PullFastForwardAsync(cloneB, originRemote, "main");
            var dirtyContentPreserved = await File.ReadAllTextAsync(Path.Combine(cloneB, "other.txt"), Encoding.UTF8);
            Check(!dirtyPullRes.Succeeded &&
                  dirtyPullRes.Outcome == PullOutcomeKind.LocalChangesConflict &&
                  dirtyContentPreserved == "local-dirty-uncommitted\n",
                "Fast-forward pull refuses to overwrite dirty local modifications and preserves working tree");
            await GitAsync(realGit, "-C", cloneB, "checkout", "--", "other.txt");

            // Divergence detection (no silent rebase!)
            await GitAsync(realGit, "-C", cloneB, "pull", "--ff-only", "origin", "main");
            await File.WriteAllTextAsync(Path.Combine(cloneB, "cloneb.txt"), "cloneB diverge\n", new UTF8Encoding(false));
            await GitAsync(realGit, "-C", cloneB, "add", "cloneb.txt");
            await GitAsync(realGit, "-C", cloneB, "commit", "-m", "cloneB ahead");

            await File.WriteAllTextAsync(Path.Combine(cloneA, "clonea.txt"), "cloneA diverge\n", new UTF8Encoding(false));
            await GitAsync(realGit, "-C", cloneA, "add", "clonea.txt");
            await GitAsync(realGit, "-C", cloneA, "commit", "-m", "cloneA ahead");
            await GitAsync(realGit, "-C", cloneA, "push", "origin", "main");

            var cloneBHeadBeforeDivergedPull = (await GitAsync(realGit, "-C", cloneB, "rev-parse", "HEAD")).Trim();
            var divergedPullRes = await GitSyncService.PullFastForwardAsync(cloneB, originRemote, "main");
            var cloneBHeadAfterDivergedPull = (await GitAsync(realGit, "-C", cloneB, "rev-parse", "HEAD")).Trim();
            Check(!divergedPullRes.Succeeded &&
                  divergedPullRes.Outcome == PullOutcomeKind.Diverged &&
                  divergedPullRes.RequiresMergeHandoff &&
                  divergedPullRes.LocalAheadCount == 1 &&
                  divergedPullRes.RemoteBehindCount == 1 &&
                  cloneBHeadAfterDivergedPull == cloneBHeadBeforeDivergedPull,
                "Diverged pull stops cleanly with exact counts, requires merge handoff, and never rewrites local commit");

            // Non-fast-forward push rejected cleanly on diverged cloneB
            var nonFfPush = await GitSyncService.PushSingleBranchAsync(
                cloneB,
                new PushExecutionOptions
                {
                    LocalBranch = "main",
                    Remote = originRemote,
                    RemoteBranch = "main",
                });
            Check(!nonFfPush.Succeeded && nonFfPush.Outcome == PushOutcomeKind.NonFastForwardRejected,
                "Normal push refuses non-fast-forward update when remote branch has diverged");

            // Upstream deleted & unreachable remote errors
            var gonePullRes = await GitSyncService.PullFastForwardAsync(cloneB, originRemote, "deleted-remote-branch");
            Check(!gonePullRes.Succeeded && gonePullRes.Outcome == PullOutcomeKind.UpstreamGone,
                "Pull identifies deleted/missing remote branch accurately");

            var missingRemote = new SourceGit.Models.Remote { Name = "missing-remote", URL = Path.Combine(suiteRoot, "does-not-exist.git") };
            await GitAsync(realGit, "-C", cloneB, "remote", "add", "missing-remote", missingRemote.URL);
            var unreachableFetch = await GitSyncService.FetchAsync(cloneB, [missingRemote]);
            Check(!unreachableFetch.Succeeded &&
                  (unreachableFetch.Outcome == FetchOutcomeKind.RemoteNotFound || unreachableFetch.Outcome == FetchOutcomeKind.OfflineOrUnreachable),
                "Fetch reports unreachable/missing remote cleanly without false success");

            // Detached HEAD blocks fast-forward pull & single-branch push cleanly
            await GitAsync(realGit, "-C", cloneB, "checkout", "--detach", "HEAD");
            var detachedPull = await GitSyncService.PullFastForwardAsync(cloneB, originRemote, "main");
            var detachedPush = await GitSyncService.PushSingleBranchAsync(
                cloneB,
                new PushExecutionOptions
                {
                    LocalBranch = "main",
                    Remote = originRemote,
                    RemoteBranch = "main",
                });
            Check(!detachedPull.Succeeded && detachedPull.Outcome == PullOutcomeKind.DetachedHead &&
                  !detachedPush.Succeeded && detachedPush.Outcome == PushOutcomeKind.DetachedHead,
                "Detached HEAD prevents unsafe pull and single-branch push explicitly");
        }
        finally
        {
            Environment.SetEnvironmentVariable("GIT_CONFIG_GLOBAL", origGlobal);
            Environment.SetEnvironmentVariable("GIT_CONFIG_NOSYSTEM", origNoSystem);
        }
    }
}
