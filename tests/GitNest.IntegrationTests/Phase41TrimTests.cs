using System.Text;
using System.Xml.Linq;

using SourceGit;
using SourceGit.Commands;
using SourceGit.Models;
using VM = SourceGit.ViewModels;

internal static partial class Program
{
    private static async Task RunPhase41TrimTestsAsync(string realGit, string root)
    {
        // 1. Assembly & API surface trimming verification
        var appAssembly = typeof(App).Assembly;
        var referencedAssemblies = appAssembly.GetReferencedAssemblies().Select(a => a.Name ?? string.Empty).ToList();
        Check(!referencedAssemblies.Any(n => n.Contains("OpenAI", StringComparison.OrdinalIgnoreCase) ||
                                             n.StartsWith("Azure.", StringComparison.OrdinalIgnoreCase)),
              "Compiled GitNest assembly has zero OpenAI or Azure SDK assembly references");

        string[] prunedTypes =
        [
            "SourceGit.ViewModels.AIAssistant",
            "SourceGit.ViewModels.ExecuteCustomAction",
            "SourceGit.ViewModels.InitGitFlow",
            "SourceGit.ViewModels.GitFlowStart",
            "SourceGit.ViewModels.GitFlowFinish",
            "SourceGit.ViewModels.InteractiveRebase",
            "SourceGit.ViewModels.CherryPick",
            "SourceGit.ViewModels.MergeConflictEditor",
            "SourceGit.ViewModels.Apply",
            "SourceGit.ViewModels.CreateTag",
            "SourceGit.ViewModels.DeleteTag",
            "SourceGit.ViewModels.DeleteMultipleTags",
            "SourceGit.ViewModels.PushTag",
            "SourceGit.ViewModels.AddSubmodule",
            "SourceGit.ViewModels.UpdateSubmodules",
            "SourceGit.ViewModels.DeleteSubmodule",
            "SourceGit.ViewModels.DeinitSubmodule",
            "SourceGit.ViewModels.MoveSubmodule",
            "SourceGit.ViewModels.SetSubmoduleBranch",
            "SourceGit.ViewModels.ChangeSubmoduleUrl",
            "SourceGit.ViewModels.AddWorktree",
            "SourceGit.ViewModels.CheckoutAsWorktree",
            "SourceGit.ViewModels.CheckoutRemoteBranchAsWorktree",
            "SourceGit.Commands.Bisect",
            "SourceGit.Commands.CherryPick",
            "SourceGit.Commands.FormatPatch",
            "SourceGit.Commands.GitFlow",
            "SourceGit.Commands.InteractiveRebase",
            "SourceGit.Commands.Tag",
            "SourceGit.Models.CustomAction",
            "SourceGit.Models.Bisect",
            "SourceGit.Models.GitFlow",
            "SourceGit.Models.InteractiveRebaseJob",
        ];
        foreach (var typeName in prunedTypes)
            Check(appAssembly.GetType(typeName) == null, $"Pruned type '{typeName}' is absent from GitNest assembly");

        Check(typeof(VM.Preferences).GetProperty("CustomActions") == null &&
              typeof(VM.Preferences).GetProperty("OpenAIServices") == null,
              "Preferences omits CustomActions and OpenAIServices");
        Check(typeof(VM.RepositoryConfigure).GetProperty("CustomActions") == null &&
              typeof(VM.RepositoryConfigure).GetProperty("AvailableOpenAIServices") == null &&
              typeof(VM.RepositoryConfigure).GetProperty("PreferredOpenAIService") == null,
              "RepositoryConfigure omits CustomActions and OpenAI service properties");
        Check(typeof(VM.Repository).GetMethod("ApplyPatch") == null &&
              typeof(VM.Repository).GetMethod("CreateNewTag") == null &&
              typeof(VM.Repository).GetMethod("AddSubmodule") == null &&
              typeof(VM.Repository).GetMethod("UpdateSubmodules") == null &&
              typeof(VM.Repository).GetMethod("AddWorktree") == null,
              "Repository ViewModel omits pruned patch, tag-write, submodule-write, and worktree-create commands");
        Check(typeof(SourceGit.Models.Remote).GetMethod("TryGetCreatePullRequestURL") == null,
              "Remote model omits platform-specific PR creation links");

        // 2. Icons & en_US/zh_CN localization resource parity & cleanup verification
        var repoRoot = LocateSourceRepositoryRoot();
        Check(repoRoot != null, "Located source repository root for resource checks");

        var iconsDoc = XDocument.Load(Path.Combine(repoRoot!, "src", "Resources", "Icons.axaml"));
        XNamespace xNs = "http://schemas.microsoft.com/winfx/2006/xaml";
        var iconKeys = iconsDoc.Descendants()
            .Select(e => e.Attribute(xNs + "Key")?.Value)
            .Where(k => !string.IsNullOrEmpty(k))
            .ToHashSet(StringComparer.Ordinal);
        string[] prunedIcons =
        [
            "Icons.Action", "Icons.AIAssist", "Icons.ApplyPatch", "Icons.Bisect",
            "Icons.CherryPick", "Icons.CreatePR", "Icons.GitFlow", "Icons.GitFlow.Finish",
            "Icons.InteractiveRebase", "Icons.Submodule.Add", "Icons.Tag.Add", "Icons.Worktree.Add"
        ];
        Check(prunedIcons.All(k => !iconKeys.Contains(k)), "Icons.axaml excludes all pruned feature icon keys");

        static HashSet<string> ReadLocaleKeys(string path, XNamespace x)
        {
            var doc = XDocument.Load(path);
            return doc.Descendants()
                .Select(e => e.Attribute(x + "Key")?.Value)
                .Where(k => !string.IsNullOrEmpty(k))
                .Select(k => k!)
                .ToHashSet(StringComparer.Ordinal);
        }

        var enKeys = ReadLocaleKeys(Path.Combine(repoRoot!, "src", "Resources", "Locales", "en_US.axaml"), xNs);
        var zhKeys = ReadLocaleKeys(Path.Combine(repoRoot!, "src", "Resources", "Locales", "zh_CN.axaml"), xNs);
        Check(enKeys.SetEquals(zhKeys), $"en_US ({enKeys.Count}) and zh_CN ({zhKeys.Count}) locale key sets have zero parity diff");
        Check(enKeys.Contains("Text.Tag.Annotated") && zhKeys.Contains("Text.Tag.Annotated"),
              "Added Text.Tag.Annotated exists symmetrically in en_US and zh_CN for read-only TagsView");

        string[] prunedLocaleKeys =
        [
            "Text.AIAssistant", "Text.Apply.Title", "Text.Bisect", "Text.CherryPick",
            "Text.Configure.CustomAction", "Text.CreateTag", "Text.GitFlow",
            "Text.InteractiveRebase", "Text.MergeConflictEditor.Title",
            "Text.Preferences.AI", "Text.Submodule.Add", "Text.AddWorktree"
        ];
        Check(prunedLocaleKeys.All(k => !enKeys.Contains(k) && !zhKeys.Contains(k)),
              "Both en_US and zh_CN exclude pruned feature localization keys");

        // 3. Read-only retention checks on real Git repository (worktree, submodule, LFS pointer, tags, external in-progress)
        var phaseRoot = Path.Combine(root, "phase-4.1-trim");
        Directory.CreateDirectory(phaseRoot);
        var utf8NoBom = new UTF8Encoding(false);

        var subOrigin = Path.Combine(phaseRoot, "sub-origin");
        await GitAsync(realGit, "init", "-b", "main", subOrigin);
        await GitAsync(realGit, "-C", subOrigin, "config", "user.name", "Sub Author");
        await GitAsync(realGit, "-C", subOrigin, "config", "user.email", "sub@example.invalid");
        File.WriteAllText(Path.Combine(subOrigin, "lib.txt"), "v1\n", utf8NoBom);
        await GitAsync(realGit, "-C", subOrigin, "add", ".");
        await GitAsync(realGit, "-C", subOrigin, "commit", "-m", "sub v1");
        var subHeadSha = (await GitAsync(realGit, "-C", subOrigin, "rev-parse", "HEAD")).Trim();

        var mainRepo = Path.Combine(phaseRoot, "main-repo");
        await GitAsync(realGit, "init", "-b", "main", mainRepo);
        await GitAsync(realGit, "-C", mainRepo, "config", "user.name", "Main Author");
        await GitAsync(realGit, "-C", mainRepo, "config", "user.email", "main@example.invalid");
        File.WriteAllText(Path.Combine(mainRepo, "README.md"), "# GitNest v1 Retention\n", utf8NoBom);
        File.WriteAllText(Path.Combine(mainRepo, ".gitattributes"), "*.bin filter=lfs diff=lfs merge=lfs -text\n", utf8NoBom);
        Directory.CreateDirectory(Path.Combine(mainRepo, "assets"));
        var lfsSha = new string('a', 64);
        var lfsPointer = $"version https://git-lfs.github.com/spec/v1\noid sha256:{lfsSha}\nsize 4096\n";
        File.WriteAllText(Path.Combine(mainRepo, "assets", "model.bin"), lfsPointer, utf8NoBom);
        await GitAsync(realGit, "-C", mainRepo, "add", ".");
        await GitAsync(realGit, "-C", mainRepo, "commit", "-m", "initial with lfs pointer");

        await GitAsync(realGit, "-C", mainRepo, "-c", "protocol.file.allow=always", "submodule", "add", subOrigin, "deps/lib-core");
        await GitAsync(realGit, "-C", mainRepo, "commit", "-m", "add submodule");
        await GitAsync(realGit, "-C", mainRepo, "tag", "v1.0.0");
        await GitAsync(realGit, "-C", mainRepo, "tag", "-a", "v1.0.1", "-m", "Release v1.0.1");

        var wtFeature = Path.Combine(phaseRoot, "wt-feature");
        await GitAsync(realGit, "-C", mainRepo, "worktree", "add", "-b", "feature/read-only", wtFeature);

        var submodules = await new QuerySubmodules(mainRepo).GetResultAsync();
        Check(submodules.Count == 1 &&
              submodules[0].Path == "deps/lib-core" &&
              submodules[0].Status == SubmoduleStatus.Normal &&
              submodules[0].SHA.Equals(subHeadSha, StringComparison.OrdinalIgnoreCase),
              "Read-only QuerySubmodules discovers and inspects initialized submodule cleanly");

        var subRev = await new QuerySubmoduleRevision(Path.Combine(mainRepo, "deps", "lib-core").Replace('\\', '/'), subHeadSha).GetResultAsync();
        Check(subRev?.Commit != null && subRev.Commit.SHA.Equals(subHeadSha, StringComparison.OrdinalIgnoreCase),
              "Read-only QuerySubmoduleRevision inspects submodule commit cleanly");

        var tags = await new QueryTags(mainRepo).GetResultAsync();
        var lightTag = tags.FirstOrDefault(t => t.Name == "v1.0.0");
        var annoTag = tags.FirstOrDefault(t => t.Name == "v1.0.1");
        Check(tags.Count == 2 &&
              lightTag is { IsAnnotated: false } &&
              annoTag is { IsAnnotated: true } &&
              annoTag.Message.Contains("Release v1.0.1", StringComparison.Ordinal),
              "Read-only QueryTags preserves lightweight and annotated tag metadata");

        var worktrees = await new SourceGit.Commands.Worktree(mainRepo).ReadAllAsync();
        Check(worktrees.Count == 2,
              "Read-only Worktree.ReadAllAsync discovers main and linked worktree cleanly");

        var committedLfsRaw = await GitAsync(realGit, "-C", mainRepo, "show", "HEAD:assets/model.bin");
        var parsedLfs = LFSObject.Parse(committedLfsRaw.Replace("\r\n", "\n"));
        Check(parsedLfs != null && parsedLfs.Oid == lfsSha && parsedLfs.Size == 4096,
              "LFS pointer blob is recognized read-only with exact OID and size");

        // External CHERRY_PICK_HEAD & BISECT_LOG detection remains active for safety blocking
        var bisectRepo = Path.Combine(phaseRoot, "bisect-marker-repo");
        await GitAsync(realGit, "init", "-b", "main", bisectRepo);
        await GitAsync(realGit, "-C", bisectRepo, "config", "user.name", "Author");
        await GitAsync(realGit, "-C", bisectRepo, "config", "user.email", "author@example.invalid");
        File.WriteAllText(Path.Combine(bisectRepo, "a.txt"), "1\n", utf8NoBom);
        await GitAsync(realGit, "-C", bisectRepo, "add", ".");
        await GitAsync(realGit, "-C", bisectRepo, "commit", "-m", "init");
        File.WriteAllText(Path.Combine(bisectRepo, ".git", "BISECT_LOG"), "# bad\n", utf8NoBom);
        var bisectSnap = await CaptureOperationSnapshot.CaptureAsync(bisectRepo);
        Check(bisectSnap.InProgressKind == GitInProgressKind.Bisect,
              "External BISECT_LOG continues to be recognized read-only for write blocking");
        File.Delete(Path.Combine(bisectRepo, ".git", "BISECT_LOG"));

        var headSha = (await GitAsync(realGit, "-C", bisectRepo, "rev-parse", "HEAD")).Trim();
        File.WriteAllText(Path.Combine(bisectRepo, ".git", "CHERRY_PICK_HEAD"), headSha + "\n", utf8NoBom);
        var cherrySnap = await CaptureOperationSnapshot.CaptureAsync(bisectRepo);
        Check(cherrySnap.InProgressKind == GitInProgressKind.CherryPick,
              "External CHERRY_PICK_HEAD continues to be recognized read-only for write blocking");
    }

    private static string LocateSourceRepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "SourceGit.slnx")))
                return dir.FullName;
            dir = dir.Parent;
        }
        return null;
    }
}
