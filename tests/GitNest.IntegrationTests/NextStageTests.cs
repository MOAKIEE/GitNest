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
}
