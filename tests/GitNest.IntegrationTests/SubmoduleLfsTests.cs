using SourceGit.Commands;
using SourceGit.Models;
using VM = SourceGit.ViewModels;

internal static partial class Program
{
    private static async Task RunSubmoduleLfsTestsAsync(string realGit, string suiteRoot)
    {
        var slRoot = Path.Combine(suiteRoot, "submodule-lfs-suite");
        Directory.CreateDirectory(slRoot);

        // Allow local file:// submodules inside isolated test GIT_CONFIG_GLOBAL
        var globalCfg = Environment.GetEnvironmentVariable("GIT_CONFIG_GLOBAL")!;
        await GitAsync(realGit, "config", "--file", globalCfg, "protocol.file.allow", "always");
        try
        {
            // 1. Git LFS: install, track, pointer detection, commit hooks preserved, Diff pointer metadata
            var lfsRemoteBare = Path.Combine(slRoot, "lfs-remote.git");
            await InitBareRepoAsync(realGit, lfsRemoteBare);
            var lfsRepo = Path.Combine(slRoot, "lfs-repo");
            await InitRepoAsync(realGit, lfsRepo, "main", "LfsTester", "lfs@example.invalid");
            await GitInAsync(realGit, lfsRepo, "remote", "add", "origin", new Uri(lfsRemoteBare).AbsoluteUri);

            var installedLfs = await new LFS(lfsRepo).InstallAsync();
            var trackedLfs = await new LFS(lfsRepo).TrackAsync("*.bin", false);
            Check(installedLfs && trackedLfs &&
                  File.Exists(Path.Combine(lfsRepo, ".git", "hooks", "pre-push")) &&
                  File.Exists(Path.Combine(lfsRepo, ".gitattributes")) &&
                  (await File.ReadAllTextAsync(Path.Combine(lfsRepo, ".gitattributes"))).Contains("filter=lfs", StringComparison.Ordinal),
                  "Commands.LFS InstallAsync and TrackAsync install LFS hooks and write .gitattributes");

            var payload = new byte[512];
            for (int i = 0; i < payload.Length; i++)
                payload[i] = (byte)((i * 31) & 0xFF);
            var binPath = Path.Combine(lfsRepo, "asset.bin");
            await File.WriteAllBytesAsync(binPath, payload);
            await GitInAsync(realGit, lfsRepo, "add", ".gitattributes", "asset.bin");
            var stagedPointer = await GitInAsync(realGit, lfsRepo, "cat-file", "-p", ":asset.bin");
            var parsedPointer = LFSObject.Parse(stagedPointer.Replace("\r\n", "\n"));
            Check(new IsLFSFiltered(lfsRepo, "asset.bin").GetResult() &&
                  parsedPointer is { Size: 512 } && !string.IsNullOrEmpty(parsedPointer.Oid),
                  "Git LFS smudge/clean pointer detection and LFSObject.Parse work after TrackAsync");

            var hookMarker = Path.Combine(lfsRepo, ".git", "precommit-invoked.txt");
            await File.WriteAllTextAsync(
                Path.Combine(lfsRepo, ".git", "hooks", "pre-commit"),
                "#!/bin/sh\necho invoked > \"" + hookMarker.Replace('\\', '/') + "\"\nexit 0\n",
                Utf8NoBom);
            var lfsSnap = await QueryStagedSnapshot.CaptureAsync(lfsRepo);
            var lfsCommitRes = await GitSyncService.CommitAsync(
                lfsRepo,
                new CommitExecutionOptions { Message = "feat: add lfs binary asset", ExpectedSnapshot = lfsSnap },
                journalStore: new OperationJournalStore(Path.Combine(slRoot, "lfs-journal.json")));
            var lfsObjectsDir = Path.Combine(lfsRepo, ".git", "lfs", "objects");
            Check(lfsCommitRes.Succeeded && File.Exists(hookMarker) &&
                  Directory.Exists(lfsObjectsDir) && Directory.GetFiles(lfsObjectsDir, "*", SearchOption.AllDirectories).Length >= 1,
                  "GitSyncService commit succeeds without bypassing hooks and keeps local LFS objects intact");

            var lfsSecondPayload = new byte[512];
            for (int i = 0; i < lfsSecondPayload.Length; i++)
                lfsSecondPayload[i] = (byte)(((i * 17) + 3) & 0xFF);
            await File.WriteAllBytesAsync(binPath, lfsSecondPayload);
            await GitInAsync(realGit, lfsRepo, "add", "asset.bin");
            var diffRes = await new Diff(lfsRepo, new DiffOption(new Change { Path = "asset.bin", Index = ChangeState.Modified }, false), 3, false, false).ReadAsync();
            Check(diffRes.LFSDiff != null &&
                  diffRes.LFSDiff.Old.Size == 512 &&
                  diffRes.LFSDiff.New.Size == 512 &&
                  !string.IsNullOrEmpty(diffRes.LFSDiff.Old.Oid) &&
                  diffRes.LFSDiff.Old.Oid != diffRes.LFSDiff.New.Oid,
                  "Commands.Diff resolves real LFS pointer metadata across staged LFS revisions");

            // 2. Tags + Nested Submodules + Clone UpdateAsync + Checkout opt-in & 2-step confirmation
            var nestedOrigin = Path.Combine(slRoot, "nested-origin");
            await InitRepoAsync(realGit, nestedOrigin, "main", "NestedUser", "nested@example.invalid");
            var nestedV1 = await CommitFileAsync(realGit, nestedOrigin, "nested.txt", "nested v1\n", "nested v1");
            var nestedV2 = await CommitFileAsync(realGit, nestedOrigin, "nested.txt", "nested v2\n", "nested v2");

            var subOrigin = Path.Combine(slRoot, "sub-origin");
            await InitRepoAsync(realGit, subOrigin, "main", "SubUser", "sub@example.invalid");
            await GitInAsync(realGit, subOrigin, "submodule", "add", nestedOrigin, "nested");
            await GitInAsync(realGit, Path.Combine(subOrigin, "nested"), "checkout", nestedV1);
            await GitInAsync(realGit, subOrigin, "add", ".");
            await GitInAsync(realGit, subOrigin, "commit", "-m", "sub v1 pointing nested v1");
            var subV1 = (await GitInAsync(realGit, subOrigin, "rev-parse", "HEAD")).Trim();

            await GitInAsync(realGit, Path.Combine(subOrigin, "nested"), "checkout", nestedV2);
            await GitInAsync(realGit, subOrigin, "add", "nested");
            await GitInAsync(realGit, subOrigin, "commit", "-m", "sub v2 pointing nested v2");
            var subV2 = (await GitInAsync(realGit, subOrigin, "rev-parse", "HEAD")).Trim();

            var superRepo = Path.Combine(slRoot, "super-repo");
            await InitRepoAsync(realGit, superRepo, "main", "SuperUser", "super@example.invalid");
            await GitInAsync(realGit, superRepo, "submodule", "add", subOrigin, "deps/sub");
            await GitInAsync(realGit, Path.Combine(superRepo, "deps", "sub"), "checkout", subV1);
            await GitInAsync(realGit, Path.Combine(superRepo, "deps", "sub"), "submodule", "update", "--init", "--recursive");
            await GitInAsync(realGit, superRepo, "add", ".");
            await GitInAsync(realGit, superRepo, "commit", "-m", "main at sub v1");
            await GitInAsync(realGit, superRepo, "tag", "v1.0.0");
            await GitInAsync(realGit, superRepo, "tag", "-a", "v1.0.1", "-m", "Release v1.0.1");

            // Lightweight/annotated tags & read-only submodule queries
            var tags = await new QueryTags(superRepo).GetResultAsync();
            Check(tags.Count == 2 &&
                  tags.FirstOrDefault(t => t.Name == "v1.0.0") is { IsAnnotated: false } &&
                  tags.FirstOrDefault(t => t.Name == "v1.0.1") is { IsAnnotated: true } anno &&
                  anno.Message.Contains("Release v1.0.1", StringComparison.Ordinal),
                  "Read-only QueryTags preserves lightweight and annotated tag metadata");

            var directSubs = await new QuerySubmodules(superRepo).GetResultAsync();
            var subRev = await new QuerySubmoduleRevision(Path.Combine(superRepo, "deps", "sub").Replace('\\', '/'), subV1).GetResultAsync();
            Check(directSubs.Count == 1 && directSubs[0].Path == "deps/sub" &&
                  directSubs[0].Status == SubmoduleStatus.Normal &&
                  directSubs[0].SHA.Equals(subV1, StringComparison.OrdinalIgnoreCase) &&
                  subRev?.Commit != null && subRev.Commit.SHA.Equals(subV1, StringComparison.OrdinalIgnoreCase),
                  "Read-only QuerySubmodules and QuerySubmoduleRevision inspect submodule state cleanly");

            // Clone + QueryUpdatableSubmodules + Submodule.UpdateAsync
            var cloneParent = Path.Combine(slRoot, "clones");
            Directory.CreateDirectory(cloneParent);
            var cloned = await new SourceGit.Commands.Clone("test", cloneParent, superRepo, "cloned-repo", string.Empty, string.Empty).ExecAsync();
            var clonedRepo = Path.Combine(cloneParent, "cloned-repo");
            var updatable = await new QueryUpdatableSubmodules(clonedRepo, true).GetResultAsync();
            var updated = await new SourceGit.Commands.Submodule(clonedRepo).UpdateAsync(updatable, true, true, false);
            var clonedSubs = await new QuerySubmodules(clonedRepo).GetResultAsync();
            Check(cloned && updatable.Count == 1 && updatable[0] == "deps/sub" && updated &&
                  clonedSubs.Count == 1 && clonedSubs[0].Status == SubmoduleStatus.Normal &&
                  clonedSubs[0].SHA.Equals(subV1, StringComparison.OrdinalIgnoreCase),
                  "Clone detects uninitialized submodule and Submodule.UpdateAsync populates expected revision");

            // Checkout submodule opt-in, legacy setting non-authorization, recursive update, broken pointer separation
            await GitInAsync(realGit, superRepo, "checkout", "-b", "feat/bump");
            await GitInAsync(realGit, Path.Combine(superRepo, "deps", "sub"), "checkout", subV2);
            await GitInAsync(realGit, Path.Combine(superRepo, "deps", "sub"), "submodule", "update", "--recursive");
            await GitInAsync(realGit, superRepo, "add", "deps/sub");
            await GitInAsync(realGit, superRepo, "commit", "-m", "bump sub to v2");

            await GitInAsync(realGit, superRepo, "checkout", "-b", "feat/broken-sub");
            await GitInAsync(realGit, superRepo, "update-index", "--add", "--cacheinfo", "160000,0123456789abcdef0123456789abcdef01234567,deps/sub");
            await GitInAsync(realGit, superRepo, "commit", "-m", "broken sub pointer");

            await GitInAsync(realGit, superRepo, "checkout", "main");
            await GitInAsync(realGit, superRepo, "submodule", "update", "--init", "--recursive");

            var superGitDir = Path.Combine(superRepo, ".git");
            await File.WriteAllTextAsync(
                Path.Combine(superGitDir, "gitnest.settings"),
                """
                {
                  "DefaultRemote": "origin",
                  "EnableRecursiveWhenAutoUpdatingSubmodules": true,
                  "AskBeforeAutoUpdatingSubmodules": false
                }
                """,
                Utf8NoBom);
            RepositorySettings.ClearCacheForRepository(superGitDir);

            var superVm = new VM.Repository(false, superRepo, superGitDir);
            try
            {
                var branches = await new QueryBranches(superRepo).GetResultAsync();
                var mainBranch = branches.Single(b => b.IsLocal && b.Name == "main");
                var bumpBranch = branches.Single(b => b.IsLocal && b.Name == "feat/bump");
                var brokenBranch = branches.Single(b => b.IsLocal && b.Name == "feat/broken-sub");

                var defaultCheckout = new VM.Checkout(superVm, bumpBranch);
                Check(!defaultCheckout.UpdateSubmodulesAfterSwitch && !defaultCheckout.RecursiveSubmoduleUpdate &&
                      defaultCheckout.HasSubmodules && defaultCheckout.ShowSubmoduleUpdateOption,
                      "Legacy auto-update settings do NOT authorize default Checkout submodule updates");
                var defaultOk = await defaultCheckout.Sure();
                Check(defaultOk &&
                      (await GitInAsync(realGit, superRepo, "symbolic-ref", "--short", "HEAD")).Trim() == "feat/bump" &&
                      (await GitInAsync(realGit, Path.Combine(superRepo, "deps", "sub"), "rev-parse", "HEAD")).Trim() == subV1 &&
                      (await GitInAsync(realGit, Path.Combine(superRepo, "deps", "sub", "nested"), "rev-parse", "HEAD")).Trim() == nestedV1,
                      "Default branch switch leaves submodule and nested submodule HEAD untouched");

                await GitInAsync(realGit, superRepo, "submodule", "update", "--recursive");
                var optInCheckout = new VM.Checkout(superVm, mainBranch) { UpdateSubmodulesAfterSwitch = true, RecursiveSubmoduleUpdate = true };
                var phase1Result = await optInCheckout.Sure();
                Check(!phase1Result && optInCheckout.IsAwaitingSubmoduleConfirm &&
                      optInCheckout.AffectedSubmodules.Contains("deps/sub") &&
                      (await GitInAsync(realGit, Path.Combine(superRepo, "deps", "sub"), "rev-parse", "HEAD")).Trim() == subV2 &&
                      (await GitInAsync(realGit, superRepo, "symbolic-ref", "--short", "HEAD")).Trim() == "main",
                      "Opt-in Checkout pauses after main switch with affected submodule paths awaiting explicit confirmation");

                var phase2Result = await optInCheckout.Sure();
                Check(phase2Result &&
                      (await GitInAsync(realGit, Path.Combine(superRepo, "deps", "sub"), "rev-parse", "HEAD")).Trim() == subV1 &&
                      (await GitInAsync(realGit, Path.Combine(superRepo, "deps", "sub", "nested"), "rev-parse", "HEAD")).Trim() == nestedV1,
                      "Confirmed recursive submodule update updates deps/sub and nested submodule to target revisions");

                var failCheckout = new VM.Checkout(superVm, brokenBranch) { UpdateSubmodulesAfterSwitch = true, RecursiveSubmoduleUpdate = false };
                var failPhase1 = await failCheckout.Sure();
                var failPhase2 = await failCheckout.Sure();
                var refreshedUpdatable = await new QueryUpdatableSubmodules(superRepo, false).GetResultAsync();
                Check(!failPhase1 && !failPhase2 && !failCheckout.IsAwaitingSubmoduleConfirm &&
                      failCheckout.HasResultView && failCheckout.IsSubmoduleFailureResult &&
                      !string.IsNullOrWhiteSpace(failCheckout.MainBranchOutcomeText) &&
                      !string.IsNullOrWhiteSpace(failCheckout.SubmoduleFailureText) &&
                      failCheckout.ResultLines.Contains("deps/sub") &&
                      (await GitInAsync(realGit, superRepo, "symbolic-ref", "--short", "HEAD")).Trim() == "feat/broken-sub" &&
                      (await GitInAsync(realGit, Path.Combine(superRepo, "deps", "sub"), "rev-parse", "HEAD")).Trim() == subV1 &&
                      refreshedUpdatable.Contains("deps/sub"),
                      "Submodule update failure reports main branch success and submodule failure separately");
            }
            finally
            {
                superVm.Close();
            }

            // Switching from branch without .gitmodules into branch with uninitialized submodule requires --init confirm
            var initRepo = Path.Combine(slRoot, "init-on-switch-repo");
            await InitRepoAsync(realGit, initRepo, "no-sub", "InitUser", "init@example.invalid");
            await CommitFileAsync(realGit, initRepo, "app.txt", "base\n", "base without submodules");
            await GitInAsync(realGit, initRepo, "checkout", "-b", "with-sub");
            await GitInAsync(realGit, initRepo, "submodule", "add", nestedOrigin, "deps/added");
            await GitInAsync(realGit, initRepo, "add", ".");
            await GitInAsync(realGit, initRepo, "commit", "-m", "branch with submodule");
            await GitInAsync(realGit, initRepo, "submodule", "deinit", "-f", "deps/added");
            var gitModulesCache = Path.Combine(initRepo, ".git", "modules", "deps", "added");
            if (Directory.Exists(gitModulesCache))
            {
                foreach (var f in Directory.EnumerateFiles(gitModulesCache, "*", SearchOption.AllDirectories))
                    File.SetAttributes(f, FileAttributes.Normal);
                Directory.Delete(gitModulesCache, true);
            }
            await GitInAsync(realGit, initRepo, "checkout", "-f", "no-sub");
            var leftoverDir = Path.Combine(initRepo, "deps", "added");
            if (Directory.Exists(leftoverDir))
                Directory.Delete(leftoverDir, true);

            var initVm = new VM.Repository(false, initRepo, Path.Combine(initRepo, ".git"));
            try
            {
                var withSubBranch = (await new QueryBranches(initRepo).GetResultAsync()).Single(b => b.IsLocal && b.Name == "with-sub");
                var targetProbeCheckout = new VM.Checkout(initVm, withSubBranch) { UpdateSubmodulesAfterSwitch = true };
                var initPhase1 = await targetProbeCheckout.Sure();
                Check(!initPhase1 && targetProbeCheckout.IsAwaitingSubmoduleConfirm &&
                      targetProbeCheckout.HasUninitializedSubmodules &&
                      targetProbeCheckout.AffectedSubmodules.Contains("deps/added"),
                      "Switching into branch with uninitialized submodule requires explicit --init confirmation");
                var initPhase2 = await targetProbeCheckout.Sure();
                Check(initPhase2 &&
                      (await GitInAsync(realGit, Path.Combine(initRepo, "deps", "added"), "rev-parse", "HEAD")).Trim() == nestedV2,
                      "Confirming uninitialized submodule update initializes and checks out target commit");
            }
            finally
            {
                initVm.Close();
                await Task.Delay(150);
            }
        }
        finally
        {
            await GitAsync(realGit, "config", "--file", globalCfg, "--unset-all", "protocol.file.allow");
        }
    }
}
