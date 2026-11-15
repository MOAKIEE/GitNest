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
