using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;

using SourceGit;
using SourceGit.Commands;
using SourceGit.Models;

internal static partial class Program
{
    private static async Task RunNextStageTestsAsync(string realGit, string root)
    {
        var utf8NoBom = new UTF8Encoding(false);
        var stageRoot = Path.Combine(root, "next-stage");
        Directory.CreateDirectory(stageRoot);

        if (ShouldRunSuite("next-stage-1"))
            await RunBatch1TestsAsync(realGit, stageRoot, utf8NoBom);
        if (ShouldRunSuite("next-stage-2"))
            await RunBatch2TestsAsync(realGit, stageRoot, utf8NoBom);
        if (ShouldRunSuite("next-stage-3"))
            await RunBatch3TestsAsync(realGit, stageRoot, utf8NoBom);
        if (ShouldRunSuite("next-stage-4"))
            await RunBatch4TestsAsync(realGit, stageRoot, utf8NoBom);
        if (ShouldRunSuite("next-stage-5"))
            await RunBatch5TestsAsync(realGit, stageRoot, utf8NoBom);
    }

    private static async Task RunBatch1TestsAsync(string realGit, string stageRoot, UTF8Encoding utf8NoBom)
    {
        // Batch 1: Unreferenced command/model trimming + retained Worktree & Submodule clone init
        var appAssembly = typeof(App).Assembly;
        string[] batch1RemovedTypes =
        [
            "SourceGit.Commands.Am",
            "SourceGit.Commands.Clean",
            "SourceGit.Models.CleanMode",
        ];
        foreach (var typeName in batch1RemovedTypes)
            Check(appAssembly.GetType(typeName) == null, $"Batch 1 removed unreferenced type '{typeName}'");

        var worktreeCmdType = typeof(SourceGit.Commands.Worktree);
        Check(worktreeCmdType.GetMethod("AddAsync") == null &&
              worktreeCmdType.GetMethod("PruneAsync") == null &&
              worktreeCmdType.GetMethod("RemoveAsync") == null,
              "Commands.Worktree omits unused AddAsync, PruneAsync, and RemoveAsync methods");
        Check(worktreeCmdType.GetMethod("ReadAllAsync") != null &&
              worktreeCmdType.GetMethod("LockAsync") != null &&
              worktreeCmdType.GetMethod("UnlockAsync") != null,
              "Commands.Worktree retains ReadAllAsync, LockAsync, and UnlockAsync");

        var submoduleCmdType = typeof(SourceGit.Commands.Submodule);
        Check(submoduleCmdType.GetMethod("AddAsync") == null &&
              submoduleCmdType.GetMethod("SetURLAsync") == null &&
              submoduleCmdType.GetMethod("DeleteAsync") == null,
              "Commands.Submodule omits unused AddAsync, SetURLAsync, and DeleteAsync methods");
        Check(submoduleCmdType.GetMethod("UpdateAsync") != null,
              "Commands.Submodule retains UpdateAsync for clone and explicit submodule updates");

        // Real Git clone + submodule initialization behavior check
        var subSource = Path.Combine(stageRoot, "sub-source");
        await GitAsync(realGit, "init", "-b", "main", subSource);
        await GitAsync(realGit, "-C", subSource, "config", "user.name", "Sub Author");
        await GitAsync(realGit, "-C", subSource, "config", "user.email", "sub@example.invalid");
        File.WriteAllText(Path.Combine(subSource, "sub.txt"), "submodule payload\n", utf8NoBom);
        await GitAsync(realGit, "-C", subSource, "add", ".");
        await GitAsync(realGit, "-C", subSource, "commit", "-m", "sub init");
        var subCommitSha = (await GitAsync(realGit, "-C", subSource, "rev-parse", "HEAD")).Trim();

        var parentOrigin = Path.Combine(stageRoot, "parent-origin");
        await GitAsync(realGit, "init", "-b", "main", parentOrigin);
        await GitAsync(realGit, "-C", parentOrigin, "config", "user.name", "Parent Author");
        await GitAsync(realGit, "-C", parentOrigin, "config", "user.email", "parent@example.invalid");
        File.WriteAllText(Path.Combine(parentOrigin, "main.txt"), "main\n", utf8NoBom);
        await GitAsync(realGit, "-C", parentOrigin, "add", ".");
        await GitAsync(realGit, "-C", parentOrigin, "commit", "-m", "parent init");
        await GitAsync(realGit, "-C", parentOrigin, "-c", "protocol.file.allow=always", "submodule", "add", subSource, "libs/sub");
        await GitAsync(realGit, "-C", parentOrigin, "commit", "-m", "add submodule");

        var globalCfg = Environment.GetEnvironmentVariable("GIT_CONFIG_GLOBAL")!;
        await GitAsync(realGit, "config", "--file", globalCfg, "protocol.file.allow", "always");
        try
        {
            var cloneTargetParent = Path.Combine(stageRoot, "clones");
            Directory.CreateDirectory(cloneTargetParent);
            var cloned = await new SourceGit.Commands.Clone("test", cloneTargetParent, parentOrigin, "cloned-repo", string.Empty, string.Empty).ExecAsync();
            var clonedRepo = Path.Combine(cloneTargetParent, "cloned-repo");
            Check(cloned && Directory.Exists(clonedRepo), "Clone command succeeds on repository with submodules");

            var updatable = await new QueryUpdatableSubmodules(clonedRepo, true).GetResultAsync();
            Check(updatable.Count == 1 && updatable[0] == "libs/sub", "QueryUpdatableSubmodules detects uninitialized submodule after clone");

            var updated = await new SourceGit.Commands.Submodule(clonedRepo).UpdateAsync(updatable, true, true, false);
            Check(updated && File.Exists(Path.Combine(clonedRepo, "libs", "sub", "sub.txt")),
                  "Commands.Submodule.UpdateAsync initializes and populates submodule after clone");

            var submodules = await new QuerySubmodules(clonedRepo).GetResultAsync();
            Check(submodules.Count == 1 &&
                  submodules[0].Status == SubmoduleStatus.Normal &&
                  submodules[0].SHA.Equals(subCommitSha, StringComparison.OrdinalIgnoreCase),
                  "Initialized cloned submodule resolves exact expected commit SHA");
        }
        finally
        {
            await GitAsync(realGit, "config", "--file", globalCfg, "--unset-all", "protocol.file.allow");
        }
    }

    private static async Task RunBatch2TestsAsync(string realGit, string stageRoot, UTF8Encoding utf8NoBom)
    {
        var appAssembly = typeof(App).Assembly;
        string[] batch2RemovedTypes =
        [
            "SourceGit.Commands.Rebase",
            "SourceGit.Commands.Replay",
            "SourceGit.ViewModels.Rebase",
            "SourceGit.Views.Rebase",
            "SourceGit.ViewModels.MergeMultiple",
            "SourceGit.Views.MergeMultiple",
            "SourceGit.Models.MergeStrategy",
        ];
        foreach (var typeName in batch2RemovedTypes)
            Check(appAssembly.GetType(typeName) == null, $"Batch 2 removed type '{typeName}'");

        var gitVersionsType = typeof(GitVersions);
        Check(gitVersionsType.GetField("REPLAY", BindingFlags.Public | BindingFlags.Static) == null,
              "GitVersions omits removed REPLAY minimum version");
        var editorType = typeof(Command).GetNestedType("EditorType", BindingFlags.Public);
        Check(editorType != null && !Enum.GetNames(editorType).Contains("RebaseEditor"),
              "Command.EditorType omits removed RebaseEditor");
        Check(typeof(SourceGit.ViewModels.Repository).GetMethod("MergeMultipleBranches", BindingFlags.Public | BindingFlags.Instance) == null,
              "Repository omits removed MergeMultipleBranches");
        Check(typeof(AssumeUnchanged).GetConstructor([typeof(string), typeof(string), typeof(bool)]) != null,
              "Commands.AssumeUnchanged remains available for removing existing assume-unchanged flags");

        var sourceRoot = LocateSourceRepositoryRoot();
        var iconsText = await File.ReadAllTextAsync(Path.Combine(sourceRoot, "src", "Resources", "Icons.axaml"), utf8NoBom);
        Check(!iconsText.Contains("x:Key=\"Icons.Rebase\"", StringComparison.Ordinal),
              "Icons.axaml omits removed Icons.Rebase");

        var enText = await File.ReadAllTextAsync(Path.Combine(sourceRoot, "src", "Resources", "Locales", "en_US.axaml"), utf8NoBom);
        var zhText = await File.ReadAllTextAsync(Path.Combine(sourceRoot, "src", "Resources", "Locales", "zh_CN.axaml"), utf8NoBom);
        string[] removedKeys =
        [
            "Text.BranchCM.MergeMultiBranches",
            "Text.BranchCM.Rebase",
            "Text.CommitCM.MergeMultiple",
            "Text.CommitCM.Rebase",
            "Text.FileCM.AssumeUnchanged",
            "Text.MergeMultiple",
            "Text.MergeMultiple.CommitChanges",
            "Text.MergeMultiple.Strategy",
            "Text.MergeMultiple.Targets",
            "Text.Rebase",
            "Text.Rebase.AutoStash",
            "Text.Rebase.NoVerify",
            "Text.Rebase.On",
            "Text.Rebase.Test",
            "Text.Repository.Skip",
        ];
        foreach (var key in removedKeys)
        {
            Check(!enText.Contains($"x:Key=\"{key}\"", StringComparison.Ordinal) &&
                  !zhText.Contains($"x:Key=\"{key}\"", StringComparison.Ordinal),
                  $"Locales omit removed Batch 2 key '{key}'");
        }

        string[] addedKeys =
        [
            "Text.AssumeUnchanged.RemoveTip",
            "Text.AssumeUnchanged.Tip",
            "Text.InProgress.Bisect",
            "Text.WorkingCopy.ExternalInProgressBlocked",
            "Text.WorkingCopy.ExternalInProgressGuide",
            "Text.WorkingCopy.InProgress.DontCommitOrigin",
        ];
        foreach (var key in addedKeys)
        {
            Check(enText.Contains($"x:Key=\"{key}\"", StringComparison.Ordinal) &&
                  zhText.Contains($"x:Key=\"{key}\"", StringComparison.Ordinal),
                  $"Locales provide symmetric Batch 2 key '{key}'");
        }

        var batch2Root = Path.Combine(stageRoot, "batch2");
        Directory.CreateDirectory(batch2Root);
        var journal = new OperationJournalStore(Path.Combine(batch2Root, "journal.json"));

        // 1. External git merge --no-commit vs GitNest MergeMode.DontCommit
        var dcRepo = Path.Combine(batch2Root, "dont-commit-repo");
        await GitAsync(realGit, "init", "-b", "main", dcRepo);
        await GitAsync(realGit, "-C", dcRepo, "config", "--local", "user.name", "Batch2 User");
        await GitAsync(realGit, "-C", dcRepo, "config", "--local", "user.email", "batch2@example.invalid");
        await File.WriteAllTextAsync(Path.Combine(dcRepo, "base.txt"), "base\n", utf8NoBom);
        await GitAsync(realGit, "-C", dcRepo, "add", ".");
        await GitAsync(realGit, "-C", dcRepo, "commit", "-m", "base");
        await GitAsync(realGit, "-C", dcRepo, "checkout", "-b", "feat/dc");
        await File.WriteAllTextAsync(Path.Combine(dcRepo, "feat.txt"), "feature\n", utf8NoBom);
        await GitAsync(realGit, "-C", dcRepo, "add", ".");
        await GitAsync(realGit, "-C", dcRepo, "commit", "-m", "feat change");
        await GitAsync(realGit, "-C", dcRepo, "checkout", "main");
        await File.WriteAllTextAsync(Path.Combine(dcRepo, "main.txt"), "main line\n", utf8NoBom);
        await GitAsync(realGit, "-C", dcRepo, "add", ".");
        await GitAsync(realGit, "-C", dcRepo, "commit", "-m", "main diverge");

        await GitAsync(realGit, "-C", dcRepo, "merge", "--no-ff", "--no-commit", "feat/dc");
        var extMergeInspect = await MergeRevertService.InspectRepositoryInProgressAsync(dcRepo, journal);
        Check(extMergeInspect.InProgressKind == GitInProgressKind.Merge &&
              extMergeInspect.Origin == ConflictOwnershipOrigin.ExternalOrUnknown &&
              !extMergeInspect.IsGitNestDontCommitMerge &&
              !extMergeInspect.CanOfferSafeAbort,
              "External git merge --no-commit stays ExternalOrUnknown and not GitNest DontCommit");

        await File.WriteAllTextAsync(Path.Combine(dcRepo, "extra.txt"), "blocked\n", utf8NoBom);
        await GitAsync(realGit, "-C", dcRepo, "add", "extra.txt");
        var extMergeSnap = await QueryStagedSnapshot.CaptureAsync(dcRepo);
        var extMergeCommit = await GitSyncService.CommitAsync(
            dcRepo,
            new CommitExecutionOptions { Message = "blocked commit", ExpectedSnapshot = extMergeSnap },
            journalStore: journal);
        Check(!extMergeCommit.Succeeded && extMergeCommit.FailureKind == CommitFailureKind.InProgressBlocked,
              "CommitAsync blocks committing during external MERGE_HEAD state");
        await GitAsync(realGit, "-C", dcRepo, "merge", "--abort");
        if (File.Exists(Path.Combine(dcRepo, "extra.txt")))
            File.Delete(Path.Combine(dcRepo, "extra.txt"));

        var dcPreflight = await MergeRevertService.InspectMergeAsync(dcRepo, "feat/dc", "main", MergeSourceKind.LocalBranch);
        var dcExec = await MergeRevertService.ExecuteMergeAsync(
            dcRepo,
            new MergeExecutionOptions
            {
                SourceKind = MergeSourceKind.LocalBranch,
                SourceRefOrSha = "feat/dc",
                TargetBranchName = "main",
                Mode = MergeMode.DontCommit,
                Preflight = dcPreflight,
            },
            journal);
        Check(dcExec.Succeeded, "GitNest MergeMode.DontCommit executes clean paused merge");
        var dcInspect = await MergeRevertService.InspectRepositoryInProgressAsync(dcRepo, journal);
        Check(dcInspect.InProgressKind == GitInProgressKind.Merge &&
              dcInspect.Origin == ConflictOwnershipOrigin.GitNestOwnedClean &&
              dcInspect.IsGitNestDontCommitMerge,
              "InspectRepositoryInProgressAsync marks GitNest DontCommit merge as IsGitNestDontCommitMerge");
        await GitAsync(realGit, "-C", dcRepo, "merge", "--abort");

        // 2. External git rebase --merge conflict: detection, blocking, byte-for-byte preservation
        var rmRepo = Path.Combine(batch2Root, "rebase-merge-repo");
        await GitAsync(realGit, "init", "-b", "main", rmRepo);
        await GitAsync(realGit, "-C", rmRepo, "config", "--local", "user.name", "Batch2 User");
        await GitAsync(realGit, "-C", rmRepo, "config", "--local", "user.email", "batch2@example.invalid");
        await File.WriteAllTextAsync(Path.Combine(rmRepo, "conflict.txt"), "line1\n", utf8NoBom);
        await GitAsync(realGit, "-C", rmRepo, "add", ".");
        await GitAsync(realGit, "-C", rmRepo, "commit", "-m", "init");
        await GitAsync(realGit, "-C", rmRepo, "checkout", "-b", "feat/rm");
        await File.WriteAllTextAsync(Path.Combine(rmRepo, "conflict.txt"), "feat line\n", utf8NoBom);
        await GitAsync(realGit, "-C", rmRepo, "add", ".");
        await GitAsync(realGit, "-C", rmRepo, "commit", "-m", "feat edit");
        await GitAsync(realGit, "-C", rmRepo, "checkout", "main");
        await File.WriteAllTextAsync(Path.Combine(rmRepo, "conflict.txt"), "main line\n", utf8NoBom);
        await GitAsync(realGit, "-C", rmRepo, "add", ".");
        await GitAsync(realGit, "-C", rmRepo, "commit", "-m", "main edit");
        await GitAsync(realGit, "-C", rmRepo, "checkout", "feat/rm");

        var (rmExit, _, _) = await GitAllowFailureAsync(realGit, "-C", rmRepo, "rebase", "--merge", "main");
        var rmDir = Path.Combine(rmRepo, ".git", "rebase-merge");
        Check(rmExit != 0 && Directory.Exists(rmDir), "External git rebase --merge stopped on conflict with .git/rebase-merge");
        var rmBeforeHashes = CaptureDirectoryHashes(rmDir);
        Check(CaptureOperationSnapshot.DetectInProgressKind(Path.Combine(rmRepo, ".git")) == GitInProgressKind.Rebase,
              "DetectInProgressKind identifies .git/rebase-merge as Rebase");
        var rmInspect = await MergeRevertService.InspectRepositoryInProgressAsync(rmRepo, journal);
        Check(rmInspect.InProgressKind == GitInProgressKind.Rebase &&
              rmInspect.Origin == ConflictOwnershipOrigin.ExternalOrUnknown &&
              !rmInspect.CanOfferSafeAbort &&
              !rmInspect.IsGitNestDontCommitMerge,
              "InspectRepositoryInProgressAsync marks external rebase-merge read-only");
        var rmConflictCommit = await GitSyncService.CommitAsync(
            rmRepo,
            new CommitExecutionOptions { Message = "blocked conflicted rebase-merge", ExpectedSnapshot = await QueryStagedSnapshot.CaptureAsync(rmRepo) },
            journalStore: journal);
        Check(!rmConflictCommit.Succeeded && rmConflictCommit.FailureKind == CommitFailureKind.ConflictsPresent,
              "CommitAsync blocks committing during conflicted external rebase --merge");
        await File.WriteAllTextAsync(Path.Combine(rmRepo, "conflict.txt"), "resolved in external tool\n", utf8NoBom);
        await GitAsync(realGit, "-C", rmRepo, "add", "conflict.txt");
        var rmResolvedBeforeHashes = CaptureDirectoryHashes(rmDir);
        var rmCommit = await GitSyncService.CommitAsync(
            rmRepo,
            new CommitExecutionOptions { Message = "blocked resolved rebase-merge", ExpectedSnapshot = await QueryStagedSnapshot.CaptureAsync(rmRepo) },
            journalStore: journal);
        Check(!rmCommit.Succeeded && rmCommit.FailureKind == CommitFailureKind.InProgressBlocked,
              "CommitAsync returns InProgressBlocked even after external conflict resolution during active rebase --merge");
        Check(HashesEqual(rmBeforeHashes, rmResolvedBeforeHashes) &&
              HashesEqual(rmResolvedBeforeHashes, CaptureDirectoryHashes(rmDir)),
              "External .git/rebase-merge files remain byte-identical after read-only inspection and blocked commit");
        await GitAsync(realGit, "-C", rmRepo, "rebase", "--abort");

        // 3. External git rebase --apply conflict (rebase-apply WITHOUT applying): never delete!
        var (raExit, _, _) = await GitAllowFailureAsync(realGit, "-C", rmRepo, "rebase", "--apply", "main");
        var raDir = Path.Combine(rmRepo, ".git", "rebase-apply");
        Check(raExit != 0 &&
              Directory.Exists(raDir) &&
              !File.Exists(Path.Combine(raDir, "applying")),
              "External git rebase --apply stopped with .git/rebase-apply and no applying marker");
        var raBeforeHashes = CaptureDirectoryHashes(raDir);
        Check(CaptureOperationSnapshot.DetectInProgressKind(Path.Combine(rmRepo, ".git")) == GitInProgressKind.Rebase,
              "DetectInProgressKind identifies rebase-apply without applying as Rebase");
        var raInspect = await MergeRevertService.InspectRepositoryInProgressAsync(rmRepo, journal);
        Check(raInspect.InProgressKind == GitInProgressKind.Rebase &&
              raInspect.Origin == ConflictOwnershipOrigin.ExternalOrUnknown &&
              !raInspect.CanOfferSafeAbort,
              "InspectRepositoryInProgressAsync treats rebase-apply without applying as external Rebase");
        var raConflictCommit = await GitSyncService.CommitAsync(
            rmRepo,
            new CommitExecutionOptions { Message = "blocked conflicted rebase-apply", ExpectedSnapshot = await QueryStagedSnapshot.CaptureAsync(rmRepo) },
            journalStore: journal);
        Check(!raConflictCommit.Succeeded && raConflictCommit.FailureKind == CommitFailureKind.ConflictsPresent,
              "CommitAsync blocks committing during conflicted external rebase --apply");
        await File.WriteAllTextAsync(Path.Combine(rmRepo, "conflict.txt"), "resolved apply in external tool\n", utf8NoBom);
        await GitAsync(realGit, "-C", rmRepo, "add", "conflict.txt");
        var raResolvedBeforeHashes = CaptureDirectoryHashes(raDir);
        var raCommit = await GitSyncService.CommitAsync(
            rmRepo,
            new CommitExecutionOptions { Message = "blocked rebase-apply", ExpectedSnapshot = await QueryStagedSnapshot.CaptureAsync(rmRepo) },
            journalStore: journal);
        Check(!raCommit.Succeeded && raCommit.FailureKind == CommitFailureKind.InProgressBlocked,
              "CommitAsync returns InProgressBlocked even after external conflict resolution during active rebase --apply");
        var raAfterHashes = CaptureDirectoryHashes(raDir);
        Check(Directory.Exists(raDir) &&
              HashesEqual(raBeforeHashes, raResolvedBeforeHashes) &&
              HashesEqual(raResolvedBeforeHashes, raAfterHashes),
              "External .git/rebase-apply directory and files are preserved intact");
        await GitAsync(realGit, "-C", rmRepo, "rebase", "--abort");

        // 4. External git bisect (BISECT_LOG): read-only detection & commit block
        await GitAsync(realGit, "-C", rmRepo, "checkout", "main");
        await File.WriteAllTextAsync(Path.Combine(rmRepo, "step2.txt"), "s2\n", utf8NoBom);
        await GitAsync(realGit, "-C", rmRepo, "add", ".");
        await GitAsync(realGit, "-C", rmRepo, "commit", "-m", "step2");
        await File.WriteAllTextAsync(Path.Combine(rmRepo, "step3.txt"), "s3\n", utf8NoBom);
        await GitAsync(realGit, "-C", rmRepo, "add", ".");
        await GitAsync(realGit, "-C", rmRepo, "commit", "-m", "step3");

        await GitAsync(realGit, "-C", rmRepo, "bisect", "start");
        await GitAsync(realGit, "-C", rmRepo, "bisect", "bad");
        await GitAsync(realGit, "-C", rmRepo, "bisect", "good", "HEAD~2");
        var bisectLogPath = Path.Combine(rmRepo, ".git", "BISECT_LOG");
        Check(File.Exists(bisectLogPath), "External git bisect generated .git/BISECT_LOG");
        var bisectLogBefore = await File.ReadAllBytesAsync(bisectLogPath);
        Check(CaptureOperationSnapshot.DetectInProgressKind(Path.Combine(rmRepo, ".git")) == GitInProgressKind.Bisect,
              "DetectInProgressKind identifies .git/BISECT_LOG as Bisect");
        var bisectInspect = await MergeRevertService.InspectRepositoryInProgressAsync(rmRepo, journal);
        Check(bisectInspect.InProgressKind == GitInProgressKind.Bisect &&
              bisectInspect.Origin == ConflictOwnershipOrigin.ExternalOrUnknown &&
              !bisectInspect.CanOfferSafeAbort,
              "InspectRepositoryInProgressAsync treats BISECT_LOG as external read-only state");
        await File.WriteAllTextAsync(Path.Combine(rmRepo, "bisect-stage.txt"), "staged\n", utf8NoBom);
        await GitAsync(realGit, "-C", rmRepo, "add", "bisect-stage.txt");
        var bisectCommit = await GitSyncService.CommitAsync(
            rmRepo,
            new CommitExecutionOptions { Message = "blocked bisect", ExpectedSnapshot = await QueryStagedSnapshot.CaptureAsync(rmRepo) },
            journalStore: journal);
        Check(!bisectCommit.Succeeded && bisectCommit.FailureKind == CommitFailureKind.InProgressBlocked,
              "CommitAsync blocks committing during active external git bisect");
        var bisectLogAfter = await File.ReadAllBytesAsync(bisectLogPath);
        Check(bisectLogBefore.SequenceEqual(bisectLogAfter), "External .git/BISECT_LOG stays byte-identical");
        await GitAsync(realGit, "-C", rmRepo, "rm", "-f", "bisect-stage.txt");
        await GitAsync(realGit, "-C", rmRepo, "bisect", "reset");

        // 5. Historical AssumeUnchanged files can still be inspected and cleared cleanly
        await GitAsync(realGit, "-C", rmRepo, "update-index", "--assume-unchanged", "step2.txt");
        await File.WriteAllTextAsync(Path.Combine(rmRepo, "step2.txt"), "modified tracked content\n", utf8NoBom);
        var assumedBefore = await new QueryAssumeUnchangedFiles(rmRepo).GetResultAsync();
        var statusBefore = await GitAsync(realGit, "-C", rmRepo, "status", "--porcelain", "--", "step2.txt");
        Check(assumedBefore.Count == 1 && assumedBefore[0] == "step2.txt" && string.IsNullOrWhiteSpace(statusBefore),
              "QueryAssumeUnchangedFiles detects historical assume-unchanged file hidden from git status");

        var cleared = await new AssumeUnchanged(rmRepo, "step2.txt", false).ExecAsync();
        var assumedAfter = await new QueryAssumeUnchangedFiles(rmRepo).GetResultAsync();
        var statusAfter = await GitAsync(realGit, "-C", rmRepo, "status", "--porcelain", "--", "step2.txt");
        Check(cleared && assumedAfter.Count == 0 && statusAfter.Contains("step2.txt", StringComparison.Ordinal),
              "AssumeUnchanged(false) removes assume-unchanged flag and restores normal Git working-tree tracking");
    }

    private static async Task RunBatch3TestsAsync(string realGit, string stageRoot, UTF8Encoding utf8NoBom)
    {
        var appAssembly = typeof(App).Assembly;
        string[] batch3RemovedTypes =
        [
            "SourceGit.Models.CommitTemplate",
            "SourceGit.Models.TemplateEngine",
        ];
        foreach (var typeName in batch3RemovedTypes)
            Check(appAssembly.GetType(typeName) == null, $"Batch 3 removed type '{typeName}'");

        Check(typeof(SourceGit.ViewModels.WorkingCopy).GetMethod("ApplyCommitMessageTemplate") == null,
              "WorkingCopy omits removed ApplyCommitMessageTemplate");
        Check(typeof(RepositorySettings).GetProperty("CommitTemplates") == null,
              "RepositorySettings omits removed CommitTemplates property");
        var cfgVmType = typeof(SourceGit.ViewModels.RepositoryConfigure);
        Check(cfgVmType.GetProperty("CommitTemplates") == null &&
              cfgVmType.GetProperty("SelectedCommitTemplate") == null &&
              cfgVmType.GetMethod("AddCommitTemplate") == null &&
              cfgVmType.GetMethod("RemoveSelectedCommitTemplate") == null,
              "RepositoryConfigure omits removed CommitTemplates, SelectedCommitTemplate, AddCommitTemplate, and RemoveSelectedCommitTemplate");

        Check(appAssembly.GetType("SourceGit.Models.ConventionalCommitType") != null &&
              typeof(RepositoryUIStates).GetProperty("RecentCommitMessages") != null &&
              typeof(SourceGit.ViewModels.WorkingCopy).GetMethod("ClearCommitMessageHistoryAsync") != null,
              "Retained conventional commit helper and recent commit history remain available");

        var sourceRoot = LocateSourceRepositoryRoot();
        var iconsText = await File.ReadAllTextAsync(Path.Combine(sourceRoot, "src", "Resources", "Icons.axaml"), utf8NoBom);
        Check(!iconsText.Contains("x:Key=\"Icons.Code\"", StringComparison.Ordinal),
              "Icons.axaml omits removed Icons.Code");

        var enText = await File.ReadAllTextAsync(Path.Combine(sourceRoot, "src", "Resources", "Locales", "en_US.axaml"), utf8NoBom);
        var zhText = await File.ReadAllTextAsync(Path.Combine(sourceRoot, "src", "Resources", "Locales", "zh_CN.axaml"), utf8NoBom);
        string[] removedKeys =
        [
            "Text.Configure.CommitMessageTemplate",
            "Text.Configure.CommitMessageTemplate.BuiltinVars",
            "Text.Configure.CommitMessageTemplate.Content",
            "Text.Configure.CommitMessageTemplate.Name",
            "Text.WorkingCopy.NoCommitTemplates",
            "Text.WorkingCopy.UseCommitTemplate",
        ];
        foreach (var key in removedKeys)
        {
            Check(!enText.Contains($"x:Key=\"{key}\"", StringComparison.Ordinal) &&
                  !zhText.Contains($"x:Key=\"{key}\"", StringComparison.Ordinal),
                  $"Locales omit removed Batch 3 key '{key}'");
        }

        Check(enText.Contains("x:Key=\"Text.WorkingCopy.CommitMessageHelper\"", StringComparison.Ordinal) &&
              zhText.Contains("x:Key=\"Text.WorkingCopy.CommitMessageHelper\"", StringComparison.Ordinal) &&
              !enText.Contains("Template/History", StringComparison.Ordinal) &&
              !zhText.Contains("历史输入/模板", StringComparison.Ordinal),
              "Text.WorkingCopy.CommitMessageHelper remains symmetric without advertising templates");

        // Legacy gitnest.settings migration + backup before first write
        var batch3Root = Path.Combine(stageRoot, "batch3");
        Directory.CreateDirectory(batch3Root);
        var legacyRepo = Path.Combine(batch3Root, "legacy-settings-repo");
        await GitAsync(realGit, "init", "-b", "main", legacyRepo);
        var legacyGitDir = Path.Combine(legacyRepo, ".git");
        var settingsPath = Path.Combine(legacyGitDir, "gitnest.settings");
        var backupPath = settingsPath + ".bak";
        var legacyJson = """
            {
              "DefaultRemote": "upstream",
              "PreferredMergeMode": 1,
              "ConventionalTypesOverride": "feat,fix,docs",
              "EnableRecursiveWhenAutoUpdatingSubmodules": false,
              "AskBeforeAutoUpdatingSubmodules": false,
              "CommitTemplates": [
                {
                  "Name": "BranchFiles",
                  "Content": "${branch_name}: ${files:2}"
                }
              ],
              "UnprotectedBranches": [
                "feat/legacy"
              ]
            }
            """;
        await File.WriteAllTextAsync(settingsPath, legacyJson, utf8NoBom);
        var legacyRawBytes = await File.ReadAllBytesAsync(settingsPath);

        RepositorySettings.ClearCacheForRepository(legacyGitDir);
        var loadedSettings = RepositorySettings.Get(legacyGitDir);
        Check(loadedSettings.DefaultRemote == "upstream" &&
              loadedSettings.PreferredMergeMode == 1 &&
              loadedSettings.ConventionalTypesOverride == "feat,fix,docs" &&
              loadedSettings.IsBranchProtectionDisabled("feat/legacy"),
              "Legacy gitnest.settings preserves all non-template settings on load");
        var bytesAfterReadOnlyLoad = await File.ReadAllBytesAsync(settingsPath);
        Check(legacyRawBytes.SequenceEqual(bytesAfterReadOnlyLoad) && !File.Exists(backupPath),
              "Read-only loading of legacy gitnest.settings leaves file untouched and writes no backup eagerly");

        loadedSettings.Save();
        Check(File.Exists(backupPath) && (await File.ReadAllBytesAsync(backupPath)).SequenceEqual(legacyRawBytes),
              "First migration write creates byte-identical backup of legacy gitnest.settings");
        using (var migratedDoc = System.Text.Json.JsonDocument.Parse(await File.ReadAllBytesAsync(settingsPath)))
        {
            var hasTemplatesProp = migratedDoc.RootElement.TryGetProperty("CommitTemplates", out _);
            var hasAutoSubmoduleProp = migratedDoc.RootElement.TryGetProperty("AskBeforeAutoUpdatingSubmodules", out _) ||
                                       migratedDoc.RootElement.TryGetProperty("EnableRecursiveWhenAutoUpdatingSubmodules", out _);
            var preservedRemote = migratedDoc.RootElement.GetProperty("DefaultRemote").GetString();
            var preservedMode = migratedDoc.RootElement.GetProperty("PreferredMergeMode").GetInt32();
            var preservedConv = migratedDoc.RootElement.GetProperty("ConventionalTypesOverride").GetString();
            Check(!hasTemplatesProp &&
                  !hasAutoSubmoduleProp &&
                  preservedRemote == "upstream" &&
                  preservedMode == 1 &&
                  preservedConv == "feat,fix,docs",
                  "Migrated gitnest.settings strips legacy removed settings while preserving other repository settings");
        }

        var bakBytesAfterFirstSave = await File.ReadAllBytesAsync(backupPath);
        loadedSettings.Save();
        Check((await File.ReadAllBytesAsync(backupPath)).SequenceEqual(bakBytesAfterFirstSave),
              "Subsequent Save calls do not overwrite initial migration backup");

        RepositorySettings.ClearCacheForRepository(legacyGitDir);
        var reloadedSettings = RepositorySettings.Get(legacyGitDir);
        Check(reloadedSettings.DefaultRemote == "upstream" &&
              reloadedSettings.PreferredMergeMode == 1 &&
              reloadedSettings.ConventionalTypesOverride == "feat,fix,docs" &&
              reloadedSettings.IsBranchProtectionDisabled("feat/legacy"),
              "Cleared and reloaded migrated gitnest.settings preserves all settings cleanly");

        // Second legacy migration when .bak already exists avoids overwriting prior backup
        await File.WriteAllTextAsync(settingsPath, legacyJson, utf8NoBom);
        RepositorySettings.ClearCacheForRepository(legacyGitDir);
        var secondMigration = RepositorySettings.Get(legacyGitDir);
        secondMigration.Save();
        var altBackupPath = settingsPath + ".legacy-1.bak";
        Check(File.Exists(altBackupPath) &&
              (await File.ReadAllBytesAsync(altBackupPath)).SequenceEqual(legacyRawBytes) &&
              (await File.ReadAllBytesAsync(backupPath)).SequenceEqual(bakBytesAfterFirstSave),
              "Legacy migration uses unique alternate backup filename when .bak already exists");

        // Git commit.template config & template file stay untouched during real commits + recent history reuse
        var commitRepo = Path.Combine(batch3Root, "commit-template-untouched-repo");
        await GitAsync(realGit, "init", "-b", "main", commitRepo);
        await GitAsync(realGit, "-C", commitRepo, "config", "--local", "user.name", "Batch3 User");
        await GitAsync(realGit, "-C", commitRepo, "config", "--local", "user.email", "batch3@example.invalid");
        var templateFile = Path.Combine(commitRepo, ".git-commit-template.txt");
        await File.WriteAllTextAsync(templateFile, "# preserved user template\n", utf8NoBom);
        await GitAsync(realGit, "-C", commitRepo, "config", "--local", "commit.template", templateFile);
        var templateFileBefore = await File.ReadAllBytesAsync(templateFile);
        var templateCfgBefore = (await GitAsync(realGit, "-C", commitRepo, "config", "--local", "--get", "commit.template")).Trim();

        await File.WriteAllTextAsync(Path.Combine(commitRepo, "feature.txt"), "hello batch3\n", utf8NoBom);
        await GitAsync(realGit, "-C", commitRepo, "add", "feature.txt");
        var snap = await QueryStagedSnapshot.CaptureAsync(commitRepo);
        var journal = new OperationJournalStore(Path.Combine(batch3Root, "journal.json"));
        var manualMessage = "feat: manual commit ignoring template";
        var commitRes = await GitSyncService.CommitAsync(
            commitRepo,
            new CommitExecutionOptions { Message = manualMessage, ExpectedSnapshot = snap },
            journalStore: journal);
        var headMsg = (await GitAsync(realGit, "-C", commitRepo, "log", "-1", "--pretty=%B")).Trim();
        var uiStates = RepositoryUIStates.Load(Path.Combine(commitRepo, ".git"));
        uiStates.AddRecentCommitMessage(manualMessage);
        uiStates.Save();
        var reloadedUiStates = RepositoryUIStates.Load(Path.Combine(commitRepo, ".git"));
        var templateFileAfter = await File.ReadAllBytesAsync(templateFile);
        var templateCfgAfter = (await GitAsync(realGit, "-C", commitRepo, "config", "--local", "--get", "commit.template")).Trim();
        Check(commitRes.Succeeded &&
              headMsg == manualMessage &&
              reloadedUiStates.RecentCommitMessages.Count == 1 &&
              reloadedUiStates.RecentCommitMessages[0] == manualMessage &&
              templateFileBefore.SequenceEqual(templateFileAfter) &&
              templateCfgBefore == templateCfgAfter,
              "Normal commit + recent history reuse succeed while preserving Git commit.template config and file untouched");
    }

    private static async Task RunBatch4TestsAsync(string realGit, string stageRoot, UTF8Encoding utf8NoBom)
    {
        // Batch 4: Narrow LFS management & submodule opt-in checkout + legacy migration
        var appAssembly = typeof(App).Assembly;
        string[] removedLfsTypes =
        [
            "SourceGit.Models.LFSLock",
            "SourceGit.ViewModels.LFSLocks",
            "SourceGit.ViewModels.LFSPrune",
            "SourceGit.Views.LFSLocks",
            "SourceGit.Views.LFSPrune",
        ];
        foreach (var typeName in removedLfsTypes)
            Check(appAssembly.GetType(typeName) == null, $"Trimmed LFS lock/prune type '{typeName}' is absent");

        string[] retainedLfsTypes =
        [
            "SourceGit.Commands.LFS",
            "SourceGit.Commands.IsLFSFiltered",
            "SourceGit.Commands.Diff",
            "SourceGit.Models.LFSDiff",
            "SourceGit.Models.LFSObject",
            "SourceGit.ViewModels.LFSFetch",
            "SourceGit.ViewModels.LFSPull",
            "SourceGit.ViewModels.LFSPush",
            "SourceGit.ViewModels.LFSTrackCustomPattern",
            "SourceGit.ViewModels.LFSImageDiff",
            "SourceGit.ViewModels.RevisionLFSImage",
        ];
        foreach (var typeName in retainedLfsTypes)
            Check(appAssembly.GetType(typeName) != null, $"Retained LFS pointer/preview/core type '{typeName}' exists");

        var lfsCmdMethods = typeof(LFS).GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
            .Select(m => m.Name)
            .ToHashSet(StringComparer.Ordinal);
        Check(!lfsCmdMethods.Contains("PruneAsync") &&
              !lfsCmdMethods.Contains("GetLocksAsync") &&
              !lfsCmdMethods.Contains("LockAsync") &&
              !lfsCmdMethods.Contains("UnlockAsync") &&
              !lfsCmdMethods.Contains("UnlockMultipleAsync") &&
              lfsCmdMethods.Contains("InstallAsync") &&
              lfsCmdMethods.Contains("TrackAsync") &&
              lfsCmdMethods.Contains("FetchAsync") &&
              lfsCmdMethods.Contains("PullAsync") &&
              lfsCmdMethods.Contains("PushAsync"),
              "Commands.LFS retains Install/Track/Fetch/Pull/Push and omits Prune/Lock APIs");

        var repoVmMethods = typeof(SourceGit.ViewModels.Repository)
            .GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
            .Select(m => m.Name)
            .ToHashSet(StringComparer.Ordinal);
        Check(!repoVmMethods.Contains("LockLFSFileAsync") &&
              !repoVmMethods.Contains("UnlockLFSFileAsync") &&
              !repoVmMethods.Contains("AutoUpdateSubmodulesAsync"),
              "Repository ViewModel omits LFS lock/unlock and implicit AutoUpdateSubmodulesAsync");

        var settingsProps = typeof(RepositorySettings)
            .GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
            .Select(p => p.Name)
            .ToHashSet(StringComparer.Ordinal);
        Check(!settingsProps.Contains("AskBeforeAutoUpdatingSubmodules") &&
              !settingsProps.Contains("EnableRecursiveWhenAutoUpdatingSubmodules"),
              "RepositorySettings removes legacy submodule auto-update flags");

        var configureProps = typeof(SourceGit.ViewModels.RepositoryConfigure)
            .GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
            .Select(p => p.Name)
            .ToHashSet(StringComparer.Ordinal);
        Check(!configureProps.Contains("AskBeforeAutoUpdatingSubmodules") &&
              !configureProps.Contains("EnableRecursiveWhenAutoUpdatingSubmodules"),
              "RepositoryConfigure removes legacy submodule auto-update flags");

        Check(SourceGit.JsonCodeGen.Default.GetTypeInfo(typeof(System.Collections.Generic.List<object>)) == null &&
              typeof(SourceGit.JsonCodeGen).GetProperty("ListLFSLock") == null,
              "JsonCodeGen no longer serializes LFSLock");

        var repoRootDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var enText = await File.ReadAllTextAsync(Path.Combine(repoRootDir, "src", "Resources", "Locales", "en_US.axaml"));
        var zhText = await File.ReadAllTextAsync(Path.Combine(repoRootDir, "src", "Resources", "Locales", "zh_CN.axaml"));
        string[] removedKeys =
        [
            "Text.GitLFS.Prune",
            "Text.GitLFS.Prune.Tips",
            "Text.GitLFS.Locks",
            "Text.GitLFS.Locks.Empty",
            "Text.GitLFS.Locks.Lock",
            "Text.GitLFS.Locks.OnlyMine",
            "Text.GitLFS.Locks.Title",
            "Text.GitLFS.Locks.Unlock",
            "Text.GitLFS.Locks.UnlockAllMyLocks",
            "Text.GitLFS.Locks.UnlockAllMyLocks.Confirm",
            "Text.GitLFS.Locks.UnlockForce",
            "Text.Configure.Git.AskBeforeAutoUpdatingSubmodules",
            "Text.Configure.Git.EnableRecursiveWhenAutoUpdatingSubmodules",
            "Text.Checkout.WarnUpdatingSubmodules",
        ];
        foreach (var key in removedKeys)
        {
            Check(!enText.Contains($"x:Key=\"{key}\"", StringComparison.Ordinal) &&
                  !zhText.Contains($"x:Key=\"{key}\"", StringComparison.Ordinal),
                  $"Locales omit removed Batch 4 key '{key}'");
        }

        string[] addedKeys =
        [
            "Text.GitLFS.Advanced",
            "Text.GitLFS.Status.Enabled",
            "Text.GitLFS.Status.NotInstalled",
            "Text.Checkout.Submodules",
            "Text.Checkout.UpdateSubmodulesAfterSwitch",
            "Text.Checkout.UpdateSubmodulesRecursive",
            "Text.Checkout.Submodules.ConfirmTitle",
            "Text.Checkout.Submodules.ConfirmUpdate",
            "Text.Checkout.Submodules.SkipAndClose",
            "Text.Checkout.Result.SubmoduleUpdateFailedTitle",
        ];
        foreach (var key in addedKeys)
        {
            Check(enText.Contains($"x:Key=\"{key}\"", StringComparison.Ordinal) &&
                  zhText.Contains($"x:Key=\"{key}\"", StringComparison.Ordinal),
                  $"Locales expose symmetric Batch 4 key '{key}'");
        }

        // Allow local file:// submodules inside isolated test GIT_CONFIG_GLOBAL
        await GitAsync(realGit, "config", "--global", "protocol.file.allow", "always");

        var batch4Root = Path.Combine(stageRoot, "batch4");
        Directory.CreateDirectory(batch4Root);

        // Real Git LFS repository behavior: install, track, pointer, commit with hooks preserved (no --no-verify, no prune)
        var lfsRemoteBare = Path.Combine(batch4Root, "lfs-remote.git");
        await GitAsync(realGit, "init", "--bare", lfsRemoteBare);
        var lfsRepo = Path.Combine(batch4Root, "lfs-repo");
        await GitAsync(realGit, "init", "-b", "main", lfsRepo);
        await GitAsync(realGit, "-C", lfsRepo, "config", "user.name", "LfsTester");
        await GitAsync(realGit, "-C", lfsRepo, "config", "user.email", "lfs@example.invalid");
        await GitAsync(realGit, "-C", lfsRepo, "remote", "add", "origin", new Uri(lfsRemoteBare).AbsoluteUri);

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
        await GitAsync(realGit, "-C", lfsRepo, "add", ".gitattributes", "asset.bin");
        var stagedPointer = await GitAsync(realGit, "-C", lfsRepo, "cat-file", "-p", ":asset.bin");
        Check(new IsLFSFiltered(lfsRepo, "asset.bin").GetResult() &&
              stagedPointer.Contains("version https://git-lfs.github.com/spec/v1", StringComparison.Ordinal) &&
              stagedPointer.Contains("oid sha256:", StringComparison.Ordinal),
              "Git LFS smudge/clean pointer detection works after TrackAsync");

        var preCommitHook = Path.Combine(lfsRepo, ".git", "hooks", "pre-commit");
        var hookMarker = Path.Combine(lfsRepo, ".git", "precommit-invoked.txt");
        await File.WriteAllTextAsync(preCommitHook, "#!/bin/sh\necho invoked > \"" + hookMarker.Replace('\\', '/') + "\"\nexit 0\n", utf8NoBom);
        var lfsSnap = await QueryStagedSnapshot.CaptureAsync(lfsRepo);
        var lfsJournal = new OperationJournalStore(Path.Combine(batch4Root, "lfs-journal.json"));
        var lfsCommitRes = await GitSyncService.CommitAsync(
            lfsRepo,
            new CommitExecutionOptions { Message = "feat: add lfs binary asset", ExpectedSnapshot = lfsSnap },
            journalStore: lfsJournal);
        var lfsObjectsDir = Path.Combine(lfsRepo, ".git", "lfs", "objects");
        var lfsObjectFiles = Directory.Exists(lfsObjectsDir)
            ? Directory.GetFiles(lfsObjectsDir, "*", SearchOption.AllDirectories)
            : [];
        Check(lfsCommitRes.Succeeded && File.Exists(hookMarker) && lfsObjectFiles.Length >= 1,
              "GitSyncService commit succeeds without bypassing hooks (--no-verify) and keeps local LFS objects intact");

        var lfsSecondPayload = new byte[512];
        for (int i = 0; i < lfsSecondPayload.Length; i++)
            lfsSecondPayload[i] = (byte)(((i * 17) + 3) & 0xFF);
        await File.WriteAllBytesAsync(binPath, lfsSecondPayload);
        await GitAsync(realGit, "-C", lfsRepo, "add", "asset.bin");
        var diffOpt = new DiffOption(new Change { Path = "asset.bin", Index = ChangeState.Modified }, false);
        var diffRes = await new Diff(lfsRepo, diffOpt, 3, false, false).ReadAsync();
        var lfsDiff = diffRes.LFSDiff;
        Check(lfsDiff != null &&
              lfsDiff.Old.Size == 512 &&
              lfsDiff.New.Size == 512 &&
              !string.IsNullOrEmpty(lfsDiff.Old.Oid) &&
              lfsDiff.Old.Oid != lfsDiff.New.Oid,
              "Commands.Diff resolves real LFS pointer metadata across staged LFS revisions");

        // Submodule opt-in branch switch, recursive update, legacy settings non-authorization, and partial failure reporting
        var nestedOrigin = Path.Combine(batch4Root, "nested-origin");
        await GitAsync(realGit, "init", "-b", "main", nestedOrigin);
        await GitAsync(realGit, "-C", nestedOrigin, "config", "user.name", "NestedUser");
        await GitAsync(realGit, "-C", nestedOrigin, "config", "user.email", "nested@example.invalid");
        await File.WriteAllTextAsync(Path.Combine(nestedOrigin, "nested.txt"), "nested v1\n", utf8NoBom);
        await GitAsync(realGit, "-C", nestedOrigin, "add", ".");
        await GitAsync(realGit, "-C", nestedOrigin, "commit", "-m", "nested v1");
        var nestedV1 = (await GitAsync(realGit, "-C", nestedOrigin, "rev-parse", "HEAD")).Trim();
        await File.WriteAllTextAsync(Path.Combine(nestedOrigin, "nested.txt"), "nested v2\n", utf8NoBom);
        await GitAsync(realGit, "-C", nestedOrigin, "add", ".");
        await GitAsync(realGit, "-C", nestedOrigin, "commit", "-m", "nested v2");
        var nestedV2 = (await GitAsync(realGit, "-C", nestedOrigin, "rev-parse", "HEAD")).Trim();

        var subOrigin = Path.Combine(batch4Root, "sub-origin");
        await GitAsync(realGit, "init", "-b", "main", subOrigin);
        await GitAsync(realGit, "-C", subOrigin, "config", "user.name", "SubUser");
        await GitAsync(realGit, "-C", subOrigin, "config", "user.email", "sub@example.invalid");
        await GitAsync(realGit, "-C", subOrigin, "submodule", "add", nestedOrigin, "nested");
        await GitAsync(realGit, "-C", Path.Combine(subOrigin, "nested"), "checkout", nestedV1);
        await GitAsync(realGit, "-C", subOrigin, "add", ".");
        await GitAsync(realGit, "-C", subOrigin, "commit", "-m", "sub v1 pointing nested v1");
        var subV1 = (await GitAsync(realGit, "-C", subOrigin, "rev-parse", "HEAD")).Trim();

        await GitAsync(realGit, "-C", Path.Combine(subOrigin, "nested"), "checkout", nestedV2);
        await GitAsync(realGit, "-C", subOrigin, "add", "nested");
        await GitAsync(realGit, "-C", subOrigin, "commit", "-m", "sub v2 pointing nested v2");
        var subV2 = (await GitAsync(realGit, "-C", subOrigin, "rev-parse", "HEAD")).Trim();

        var superRepo = Path.Combine(batch4Root, "super-repo");
        await GitAsync(realGit, "init", "-b", "main", superRepo);
        await GitAsync(realGit, "-C", superRepo, "config", "user.name", "SuperUser");
        await GitAsync(realGit, "-C", superRepo, "config", "user.email", "super@example.invalid");
        await GitAsync(realGit, "-C", superRepo, "submodule", "add", subOrigin, "deps/sub");
        await GitAsync(realGit, "-C", Path.Combine(superRepo, "deps", "sub"), "checkout", subV1);
        await GitAsync(realGit, "-C", Path.Combine(superRepo, "deps", "sub"), "submodule", "update", "--init", "--recursive");
        await GitAsync(realGit, "-C", superRepo, "add", ".");
        await GitAsync(realGit, "-C", superRepo, "commit", "-m", "main at sub v1");

        await GitAsync(realGit, "-C", superRepo, "checkout", "-b", "feat/bump");
        await GitAsync(realGit, "-C", Path.Combine(superRepo, "deps", "sub"), "checkout", subV2);
        await GitAsync(realGit, "-C", Path.Combine(superRepo, "deps", "sub"), "submodule", "update", "--recursive");
        await GitAsync(realGit, "-C", superRepo, "add", "deps/sub");
        await GitAsync(realGit, "-C", superRepo, "commit", "-m", "bump sub to v2");

        await GitAsync(realGit, "-C", superRepo, "checkout", "-b", "feat/broken-sub");
        await GitAsync(realGit, "-C", superRepo, "update-index", "--add", "--cacheinfo", "160000,0123456789abcdef0123456789abcdef01234567,deps/sub");
        await GitAsync(realGit, "-C", superRepo, "commit", "-m", "broken sub pointer");

        // Switch back to main with nested submodule aligned to v1
        await GitAsync(realGit, "-C", superRepo, "checkout", "main");
        await GitAsync(realGit, "-C", superRepo, "submodule", "update", "--init", "--recursive");

        // Write legacy settings claiming AskBeforeAutoUpdatingSubmodules = false (old silent auto-update)
        var superGitDir = Path.Combine(superRepo, ".git");
        var legacySuperSettingsPath = Path.Combine(superGitDir, "gitnest.settings");
        await File.WriteAllTextAsync(
            legacySuperSettingsPath,
            """
            {
              "DefaultRemote": "origin",
              "EnableRecursiveWhenAutoUpdatingSubmodules": true,
              "AskBeforeAutoUpdatingSubmodules": false
            }
            """,
            utf8NoBom);
        RepositorySettings.ClearCacheForRepository(superGitDir);

        var superVm = new SourceGit.ViewModels.Repository(false, superRepo, superGitDir);
        try
        {
            var loadedBranches = await new QueryBranches(superRepo).GetResultAsync();
            var mainBranch = loadedBranches.Single(b => b.IsLocal && b.Name == "main");
            var bumpBranch = loadedBranches.Single(b => b.IsLocal && b.Name == "feat/bump");
            var brokenBranch = loadedBranches.Single(b => b.IsLocal && b.Name == "feat/broken-sub");

            // 1) Default Checkout must keep UpdateSubmodulesAfterSwitch unchecked and must NOT auto-update
            var defaultCheckout = new SourceGit.ViewModels.Checkout(superVm, bumpBranch);
            Check(!defaultCheckout.UpdateSubmodulesAfterSwitch &&
                  !defaultCheckout.RecursiveSubmoduleUpdate &&
                  defaultCheckout.HasSubmodules &&
                  defaultCheckout.ShowSubmoduleUpdateOption,
                  "Legacy auto-update settings do NOT authorize default Checkout submodule updates");
            var defaultOk = await defaultCheckout.Sure();
            var subHeadAfterDefault = (await GitAsync(realGit, "-C", Path.Combine(superRepo, "deps", "sub"), "rev-parse", "HEAD")).Trim();
            var nestedHeadAfterDefault = (await GitAsync(realGit, "-C", Path.Combine(superRepo, "deps", "sub", "nested"), "rev-parse", "HEAD")).Trim();
            var currentAfterDefault = (await GitAsync(realGit, "-C", superRepo, "symbolic-ref", "--short", "HEAD")).Trim();
            Check(defaultOk &&
                  currentAfterDefault == "feat/bump" &&
                  subHeadAfterDefault == subV1 &&
                  nestedHeadAfterDefault == nestedV1,
                  "Default branch switch leaves submodule and nested submodule HEAD untouched while switching main branch");

            // Align submodule manually to v2 so switching back to main tests opt-in + recursive + 2-step confirm
            await GitAsync(realGit, "-C", superRepo, "submodule", "update", "--recursive");
            var subBeforeOptIn = (await GitAsync(realGit, "-C", Path.Combine(superRepo, "deps", "sub"), "rev-parse", "HEAD")).Trim();
            Check(subBeforeOptIn == subV2, "Submodule setup aligned to v2 on feat/bump");

            var optInCheckout = new SourceGit.ViewModels.Checkout(superVm, mainBranch)
            {
                UpdateSubmodulesAfterSwitch = true,
                RecursiveSubmoduleUpdate = true,
            };
            var phase1Result = await optInCheckout.Sure();
            var subAfterPhase1 = (await GitAsync(realGit, "-C", Path.Combine(superRepo, "deps", "sub"), "rev-parse", "HEAD")).Trim();
            Check(!phase1Result &&
                  optInCheckout.IsAwaitingSubmoduleConfirm &&
                  optInCheckout.AffectedSubmodules.Contains("deps/sub") &&
                  subAfterPhase1 == subV2 &&
                  (await GitAsync(realGit, "-C", superRepo, "symbolic-ref", "--short", "HEAD")).Trim() == "main",
                  "Opt-in Checkout pauses after main switch with affected submodule paths awaiting explicit confirmation");

            var phase2Result = await optInCheckout.Sure();
            var subAfterPhase2 = (await GitAsync(realGit, "-C", Path.Combine(superRepo, "deps", "sub"), "rev-parse", "HEAD")).Trim();
            var nestedAfterPhase2 = (await GitAsync(realGit, "-C", Path.Combine(superRepo, "deps", "sub", "nested"), "rev-parse", "HEAD")).Trim();
            Check(phase2Result &&
                  subAfterPhase2 == subV1 &&
                  nestedAfterPhase2 == nestedV1,
                  "Confirmed recursive submodule update updates deps/sub and nested submodule to target revisions");

            // 3) Branch switch succeeds while submodule update fails -> separate outcome reporting and keep main switched
            var failCheckout = new SourceGit.ViewModels.Checkout(superVm, brokenBranch)
            {
                UpdateSubmodulesAfterSwitch = true,
                RecursiveSubmoduleUpdate = false,
            };
            var failPhase1 = await failCheckout.Sure();
            Check(!failPhase1 && failCheckout.IsAwaitingSubmoduleConfirm && failCheckout.AffectedSubmodules.Contains("deps/sub"),
                  "Checkout to branch with broken submodule revision pauses at secondary confirmation with path list");

            var failPhase2 = await failCheckout.Sure();
            var currentAfterBroken = (await GitAsync(realGit, "-C", superRepo, "symbolic-ref", "--short", "HEAD")).Trim();
            var subAfterBroken = (await GitAsync(realGit, "-C", Path.Combine(superRepo, "deps", "sub"), "rev-parse", "HEAD")).Trim();
            var refreshedUpdatable = await new QueryUpdatableSubmodules(superRepo, false).GetResultAsync();
            Check(!failPhase2 &&
                  !failCheckout.IsAwaitingSubmoduleConfirm &&
                  failCheckout.HasResultView &&
                  failCheckout.IsSubmoduleFailureResult &&
                  !string.IsNullOrWhiteSpace(failCheckout.MainBranchOutcomeText) &&
                  !string.IsNullOrWhiteSpace(failCheckout.SubmoduleFailureText) &&
                  failCheckout.ResultLines.Contains("deps/sub") &&
                  currentAfterBroken == "feat/broken-sub" &&
                  subAfterBroken == subV1 &&
                  refreshedUpdatable.Contains("deps/sub"),
                  "Submodule update failure reports main branch success and submodule failure separately while refreshing real state");
        }
        finally
        {
            superVm.Close();
        }

        // 4) Switching from branch with NO .gitmodules into branch WITH uninitialized submodule detects HasSubmodules & --init confirmation
        var initRepo = Path.Combine(batch4Root, "init-on-switch-repo");
        await GitAsync(realGit, "init", "-b", "no-sub", initRepo);
        await GitAsync(realGit, "-C", initRepo, "config", "user.name", "InitUser");
        await GitAsync(realGit, "-C", initRepo, "config", "user.email", "init@example.invalid");
        await File.WriteAllTextAsync(Path.Combine(initRepo, "app.txt"), "base\n", utf8NoBom);
        await GitAsync(realGit, "-C", initRepo, "add", ".");
        await GitAsync(realGit, "-C", initRepo, "commit", "-m", "base without submodules");

        await GitAsync(realGit, "-C", initRepo, "checkout", "-b", "with-sub");
        await GitAsync(realGit, "-C", initRepo, "submodule", "add", nestedOrigin, "deps/added");
        await GitAsync(realGit, "-C", initRepo, "add", ".");
        await GitAsync(realGit, "-C", initRepo, "commit", "-m", "branch with submodule");
        await GitAsync(realGit, "-C", initRepo, "submodule", "deinit", "-f", "deps/added");
        var gitModulesCache = Path.Combine(initRepo, ".git", "modules", "deps", "added");
        if (Directory.Exists(gitModulesCache))
        {
            foreach (var f in Directory.EnumerateFiles(gitModulesCache, "*", SearchOption.AllDirectories))
                File.SetAttributes(f, FileAttributes.Normal);
            Directory.Delete(gitModulesCache, true);
        }
        await GitAsync(realGit, "-C", initRepo, "checkout", "-f", "no-sub");
        var leftoverDir = Path.Combine(initRepo, "deps", "added");
        if (Directory.Exists(leftoverDir))
            Directory.Delete(leftoverDir, true);

        var initVm = new SourceGit.ViewModels.Repository(false, initRepo, Path.Combine(initRepo, ".git"));
        try
        {
            var initBranches = await new QueryBranches(initRepo).GetResultAsync();
            var withSubBranch = initBranches.Single(b => b.IsLocal && b.Name == "with-sub");
            var targetProbeCheckout = new SourceGit.ViewModels.Checkout(initVm, withSubBranch)
            {
                UpdateSubmodulesAfterSwitch = true,
            };
            var initPhase1 = await targetProbeCheckout.Sure();
            Check(!initPhase1 &&
                  targetProbeCheckout.IsAwaitingSubmoduleConfirm &&
                  targetProbeCheckout.HasUninitializedSubmodules &&
                  targetProbeCheckout.AffectedSubmodules.Contains("deps/added"),
                  "Switching into branch with uninitialized submodule requires explicit --init confirmation");
            var initPhase2 = await targetProbeCheckout.Sure();
            var addedHead = (await GitAsync(realGit, "-C", Path.Combine(initRepo, "deps", "added"), "rev-parse", "HEAD")).Trim();
            Check(initPhase2 && addedHead == nestedV2,
                  "Confirming uninitialized submodule update initializes and checks out target commit");
        }
        finally
        {
            initVm.Close();
            await Task.Delay(200);
        }
    }

    private static async Task RunBatch5TestsAsync(string realGit, string stageRoot, UTF8Encoding utf8NoBom)
    {
        // Batch 5: Merged RepositoryConfigure (IssueTracker under Advanced) + Dedicated Amend Last Commit safety
        var wcType = typeof(SourceGit.ViewModels.WorkingCopy);
        Check(wcType.GetProperty("UseAmend") == null && wcType.GetProperty("ResetAuthor") == null,
              "WorkingCopy no longer exposes inline UseAmend or ResetAuthor properties");
        Check(wcType.GetMethod("OpenAmendLastCommit") != null && wcType.GetProperty("CanOpenAmendLastCommit") != null,
              "WorkingCopy exposes dedicated OpenAmendLastCommit entry point");

        var batch5Root = Path.Combine(stageRoot, "batch5-amend");
        var originBare = Path.Combine(batch5Root, "origin.git");
        var originMoved = Path.Combine(batch5Root, "origin-offline.git");
        var cloneA = Path.Combine(batch5Root, "clone-a");
        var cloneB = Path.Combine(batch5Root, "clone-b");
        Directory.CreateDirectory(batch5Root);

        await GitAsync(realGit, "init", "--bare", "-b", "main", originBare);
        await GitAsync(realGit, "clone", originBare, cloneA);
        await GitAsync(realGit, "-C", cloneA, "config", "user.name", "Alice");
        await GitAsync(realGit, "-C", cloneA, "config", "user.email", "alice@example.invalid");
        await File.WriteAllTextAsync(Path.Combine(cloneA, "README.md"), "base\n", utf8NoBom);
        await GitAsync(realGit, "-C", cloneA, "add", ".");
        await GitAsync(realGit, "-C", cloneA, "commit", "-m", "chore: base on #101");
        await GitAsync(realGit, "-C", cloneA, "push", "-u", "origin", "main");
        await GitAsync(realGit, "clone", originBare, cloneB);
        await GitAsync(realGit, "-C", cloneB, "config", "user.name", "Bob Collaborator");
        await GitAsync(realGit, "-C", cloneB, "config", "user.email", "bob@example.invalid");

        // 1. IssueTracker rules work from RepositoryConfigure (private + shared .issuetracker) and match commit text
        var cfgRepoVm = new SourceGit.ViewModels.Repository(false, cloneA, Path.Combine(cloneA, ".git"));
        try
        {
            var cfgVm = new SourceGit.ViewModels.RepositoryConfigure(cfgRepoVm);
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (cfgVm.IsLoading && DateTime.UtcNow < deadline)
                await Task.Delay(30);

            cfgVm.AddIssueTracker("LocalJira", @"PROJ-(\d+)", "https://jira.example.invalid/browse/PROJ-$1");
            cfgVm.SelectedIssueTracker!.IsShared = false;
            cfgVm.AddIssueTracker("SharedGitHub", @"#(\d+)", "https://github.example.invalid/team/repo/issues/$1");
            cfgVm.SelectedIssueTracker!.IsShared = true;
            await cfgVm.SaveAsync();

            var localIssueRegex = (await GitAsync(realGit, "-C", cloneA, "config", "--local", "--get", "issuetracker.LocalJira.regex")).Trim();
            var sharedIssueFile = Path.Combine(cloneA, ".issuetracker");
            var sharedIssueRegex = (await GitAsync(realGit, "-C", cloneA, "config", "-f", sharedIssueFile, "--get", "issuetracker.SharedGitHub.regex")).Trim();

            var loadedRules = new List<SourceGit.Models.IssueTracker>();
            await new SourceGit.Commands.IssueTracker(cloneA, true).ReadAllAsync(loadedRules, true);
            await new SourceGit.Commands.IssueTracker(cloneA, false).ReadAllAsync(loadedRules, false);

            var collector = new InlineElementCollector();
            foreach (var rule in loadedRules)
                rule.Matches(collector, "Fix PROJ-42 and #101");
            var matchedUrls = Enumerable.Range(0, collector.Count)
                .Select(i => collector[i])
                .Where(e => e.Type == InlineElementType.Link)
                .Select(e => e.Link)
                .ToList();

            Check(localIssueRegex == @"PROJ-(\d+)" &&
                  File.Exists(sharedIssueFile) &&
                  sharedIssueRegex == @"#(\d+)" &&
                  matchedUrls.Contains("https://jira.example.invalid/browse/PROJ-42") &&
                  matchedUrls.Contains("https://github.example.invalid/team/repo/issues/101"),
                  "RepositoryConfigure IssueTracker rules persist to .git/config and .issuetracker and match commit links");
        }
        finally
        {
            cfgRepoVm.Close();
            await Task.Delay(150);
        }

        // 2. Unpushed local branch Amend succeeds, preserves history depth, creates 30-day backup ref, supports reset-author/signoff
        await GitAsync(realGit, "-C", cloneA, "checkout", "-b", "feat/amend-local");
        await File.WriteAllTextAsync(Path.Combine(cloneA, "local.txt"), "v1\n", utf8NoBom);
        await GitAsync(realGit, "-C", cloneA, "add", "local.txt");
        await GitAsync(realGit, "-C", cloneA, "commit", "-m", "feat: local amend target");
        var localBeforeSha = (await GitAsync(realGit, "-C", cloneA, "rev-parse", "HEAD")).Trim();
        var countBefore = int.Parse((await GitAsync(realGit, "-C", cloneA, "rev-list", "--count", "HEAD")).Trim());

        await File.WriteAllTextAsync(Path.Combine(cloneA, "local.txt"), "v2\n", utf8NoBom);
        await GitAsync(realGit, "-C", cloneA, "add", "local.txt");

        var journalStore = new OperationJournalStore(Path.Combine(stageRoot, "batch5-journal.json"));
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
        var localAfterSha = (await GitAsync(realGit, "-C", cloneA, "rev-parse", "HEAD")).Trim();
        var countAfter = int.Parse((await GitAsync(realGit, "-C", cloneA, "rev-list", "--count", "HEAD")).Trim());
        var localMsg = (await GitAsync(realGit, "-C", cloneA, "log", "-1", "--format=%B")).Trim();
        var backupSha = (await GitAsync(realGit, "-C", cloneA, "rev-parse", "--verify", localAmendOut.BackupRefName)).Trim();

        Check(localAmendOut.Succeeded &&
              localAmendOut.AmendSucceeded &&
              !localAmendOut.PushAttempted &&
              countAfter == countBefore &&
              localAfterSha != localBeforeSha &&
              backupSha == localBeforeSha &&
              localMsg.Contains("Signed-off-by: Alice <alice@example.invalid>", StringComparison.Ordinal),
              "Local unpushed Amend updates HEAD in place with SignOff and retains original SHA in backup ref");

        // 3. UnknownRemoteState (unreachable remote) refuses silent Amend
        await GitAsync(realGit, "-C", cloneA, "checkout", "-b", "feat/amend-unknown");
        await GitAsync(realGit, "-C", cloneA, "remote", "add", "broken", Path.Combine(batch5Root, "does-not-exist.git"));
        await GitAsync(realGit, "-C", cloneA, "config", "branch.feat/amend-unknown.remote", "broken");
        await GitAsync(realGit, "-C", cloneA, "config", "branch.feat/amend-unknown.merge", "refs/heads/feat/amend-unknown");
        await File.WriteAllTextAsync(Path.Combine(cloneA, "unk.txt"), "1\n", utf8NoBom);
        await GitAsync(realGit, "-C", cloneA, "add", "unk.txt");
        var headBeforeUnknown = (await GitAsync(realGit, "-C", cloneA, "rev-parse", "HEAD")).Trim();
        var unknownPre = await GitSyncService.InspectAmendLastCommitAsync(cloneA);
        var unknownExec = await GitSyncService.ExecuteAmendLastCommitAsync(
            cloneA,
            new AmendLastCommitExecutionOptions { Preflight = unknownPre, Message = "should fail" },
            journalStore: journalStore);
        var headAfterUnknown = (await GitAsync(realGit, "-C", cloneA, "rev-parse", "HEAD")).Trim();
        Check(unknownPre.RemotePublishState == AmendRemotePublishState.UnknownRemoteState &&
              !unknownPre.CanAmendDirectly &&
              !unknownExec.Succeeded &&
              unknownExec.BlockedByRemoteState &&
              headAfterUnknown == headBeforeUnknown,
              "Unknown/offline remote state refuses Amend and leaves HEAD untouched");
        await GitAsync(realGit, "-C", cloneA, "remote", "remove", "broken");

        // 4. UnfetchedOrStaleRemote refuses direct Amend until fetched
        await GitAsync(realGit, "-C", cloneA, "checkout", "feat/amend-local");
        await GitAsync(realGit, "-C", cloneA, "push", "-u", "origin", "feat/amend-local");
        await GitAsync(realGit, "-C", cloneB, "fetch", "origin");
        await GitAsync(realGit, "-C", cloneB, "checkout", "-B", "feat/amend-local", "origin/feat/amend-local");
        await File.WriteAllTextAsync(Path.Combine(cloneB, "bob-unfetched.txt"), "bob\n", utf8NoBom);
        await GitAsync(realGit, "-C", cloneB, "add", "bob-unfetched.txt");
        await GitAsync(realGit, "-C", cloneB, "commit", "-m", "bob commit ahead on server");
        await GitAsync(realGit, "-C", cloneB, "push", "origin", "feat/amend-local");
        var serverAheadSha = (await GitAsync(realGit, "-C", originBare, "rev-parse", "refs/heads/feat/amend-local")).Trim();

        await File.WriteAllTextAsync(Path.Combine(cloneA, "stale-stage.txt"), "s\n", utf8NoBom);
        await GitAsync(realGit, "-C", cloneA, "add", "stale-stage.txt");
        var unfetchedPre = await GitSyncService.InspectAmendLastCommitAsync(cloneA);
        var unfetchedExec = await GitSyncService.ExecuteAmendLastCommitAsync(
            cloneA,
            new AmendLastCommitExecutionOptions { Preflight = unfetchedPre, Message = "blocked stale" },
            journalStore: journalStore);
        Check(unfetchedPre.RemotePublishState == AmendRemotePublishState.UnfetchedOrStaleRemote &&
              !unfetchedExec.Succeeded &&
              unfetchedExec.BlockedByRemoteState &&
              (await GitAsync(realGit, "-C", cloneA, "rev-parse", "HEAD")).Trim() == localAfterSha,
              "Unfetched remote commits block silent Amend with UnfetchedOrStaleRemote");

        // 5. Protected branch 'main' AlreadyPushed blocks rewrite even with RewritePushedRemoteWithLease=true
        await GitAsync(realGit, "-C", cloneA, "reset", "--hard");
        await GitAsync(realGit, "-C", cloneA, "checkout", "main");
        await GitAsync(realGit, "-C", cloneA, "fetch", "origin");
        await File.WriteAllTextAsync(Path.Combine(cloneA, "main-edit.txt"), "blocked\n", utf8NoBom);
        await GitAsync(realGit, "-C", cloneA, "add", "main-edit.txt");
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
        await GitAsync(realGit, "-C", cloneA, "reset", "--hard");

        // 6. Unlocked pushed feature branch: default blocked -> opt-in lease push succeeds -> collaborator lease race rejected -> partial outage Retry Push Only succeeds
        await GitAsync(realGit, "-C", cloneA, "checkout", "-b", "feat/amend-lease");
        await File.WriteAllTextAsync(Path.Combine(cloneA, "lease.txt"), "v1\n", utf8NoBom);
        await GitAsync(realGit, "-C", cloneA, "add", "lease.txt");
        await GitAsync(realGit, "-C", cloneA, "commit", "-m", "feat: lease base");
        await GitAsync(realGit, "-C", cloneA, "push", "-u", "origin", "feat/amend-lease");
        await GitAsync(realGit, "-C", cloneA, "fetch", "origin");

        await File.WriteAllTextAsync(Path.Combine(cloneA, "lease.txt"), "v2\n", utf8NoBom);
        await GitAsync(realGit, "-C", cloneA, "add", "lease.txt");
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
        var leaseTipRemote1 = (await GitAsync(realGit, "-C", originBare, "rev-parse", "refs/heads/feat/amend-lease")).Trim();
        var leaseTipLocal1 = (await GitAsync(realGit, "-C", cloneA, "rev-parse", "HEAD")).Trim();
        Check(optInExec.Succeeded &&
              optInExec.PushSucceeded &&
              leaseTipRemote1 == leaseTipLocal1,
              "Explicit opt-in Amend rewrites local HEAD and updates pushed remote via strict --force-with-lease");

        // Race: preflight on cloneA matches tip1, then cloneB pushes collaborator commit tip2 BEFORE cloneA executes lease push
        await GitAsync(realGit, "-C", cloneA, "fetch", "origin");
        await File.WriteAllTextAsync(Path.Combine(cloneA, "lease.txt"), "v3-local\n", utf8NoBom);
        await GitAsync(realGit, "-C", cloneA, "add", "lease.txt");
        var racePre = await GitSyncService.InspectAmendLastCommitAsync(cloneA);

        await GitAsync(realGit, "-C", cloneB, "fetch", "origin");
        await GitAsync(realGit, "-C", cloneB, "checkout", "-B", "feat/amend-lease", "origin/feat/amend-lease");
        await File.WriteAllTextAsync(Path.Combine(cloneB, "bob-race.txt"), "collaborator wins\n", utf8NoBom);
        await GitAsync(realGit, "-C", cloneB, "add", "bob-race.txt");
        await GitAsync(realGit, "-C", cloneB, "commit", "-m", "bob concurrent commit");
        await GitAsync(realGit, "-C", cloneB, "push", "origin", "feat/amend-lease");
        var collaboratorSha = (await GitAsync(realGit, "-C", originBare, "rev-parse", "refs/heads/feat/amend-lease")).Trim();

        var raceExec = await GitSyncService.ExecuteAmendLastCommitAsync(
            cloneA,
            new AmendLastCommitExecutionOptions
            {
                Preflight = racePre,
                Message = "feat: race amend",
                RewritePushedRemoteWithLease = true,
            },
            journalStore: journalStore);
        var serverTipAfterRace = (await GitAsync(realGit, "-C", originBare, "rev-parse", "refs/heads/feat/amend-lease")).Trim();
        Check(!raceExec.Succeeded &&
              raceExec.IsPartialSuccess &&
              raceExec.RejectedByLease &&
              serverTipAfterRace == collaboratorSha &&
              raceExec.CanRetryPushOnly,
              "Concurrent collaborator push rejects pinned --force-with-lease, preserves server commit, and enters Push-Retry-Only state");

        // Partial outage + Retry Push Only recovery (backup ref untouched, no duplicate amend)
        await GitAsync(realGit, "-C", cloneA, "checkout", "-b", "feat/amend-retry");
        await File.WriteAllTextAsync(Path.Combine(cloneA, "retry.txt"), "r1\n", utf8NoBom);
        await GitAsync(realGit, "-C", cloneA, "add", "retry.txt");
        await GitAsync(realGit, "-C", cloneA, "commit", "-m", "feat: retry base");
        await GitAsync(realGit, "-C", cloneA, "push", "-u", "origin", "feat/amend-retry");
        await GitAsync(realGit, "-C", cloneA, "fetch", "origin");

        await File.WriteAllTextAsync(Path.Combine(cloneA, "retry.txt"), "r2\n", utf8NoBom);
        await GitAsync(realGit, "-C", cloneA, "add", "retry.txt");
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

        var amendedHeadSha = (await GitAsync(realGit, "-C", cloneA, "rev-parse", "HEAD")).Trim();
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
        var remoteRetryTip = (await GitAsync(realGit, "-C", originBare, "rev-parse", "refs/heads/feat/amend-retry")).Trim();
        var headAfterRetry = (await GitAsync(realGit, "-C", cloneA, "rev-parse", "HEAD")).Trim();
        Check(retrySessionRes.IsFullySucceeded &&
              headAfterRetry == amendedHeadSha &&
              remoteRetryTip == amendedHeadSha &&
              serverAheadSha.Length == 40,
              "RetryPushSessionAsync pushes already-amended HEAD with lease without re-amending");
    }

    private static async Task<(int ExitCode, string StdOut, string StdErr)> GitAllowFailureAsync(string git, params string[] arguments)
    {
        using var process = new System.Diagnostics.Process();
        process.StartInfo = new System.Diagnostics.ProcessStartInfo(git)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var argument in arguments)
            process.StartInfo.ArgumentList.Add(argument);
        process.Start();
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await Task.WhenAll(output, error, process.WaitForExitAsync());
        return (process.ExitCode, await output, await error);
    }

    private static System.Collections.Generic.SortedDictionary<string, string> CaptureDirectoryHashes(string dir)
    {
        var map = new System.Collections.Generic.SortedDictionary<string, string>(StringComparer.Ordinal);
        if (!Directory.Exists(dir))
            return map;

        foreach (var file in Directory.GetFiles(dir, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(dir, file).Replace('\\', '/');
            var bytes = File.ReadAllBytes(file);
            map[rel] = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes));
        }

        return map;
    }

    private static bool HashesEqual(
        System.Collections.Generic.SortedDictionary<string, string> left,
        System.Collections.Generic.SortedDictionary<string, string> right)
    {
        if (left.Count != right.Count)
            return false;

        foreach (var kv in left)
        {
            if (!right.TryGetValue(kv.Key, out var value) || !string.Equals(kv.Value, value, StringComparison.Ordinal))
                return false;
        }

        return true;
    }
}
