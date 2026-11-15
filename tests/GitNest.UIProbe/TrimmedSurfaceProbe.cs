using System.Collections;
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;
using SourceGit;
using SourceGit.Models;
using VM = SourceGit.ViewModels;
using Views = SourceGit.Views;

internal static partial class Program
{
    static partial void RunTrimSurface()
    {
        var subOrigin = Path.Combine(_root, "sub-lib-core");
        Git(_root, "init", "-b", "main", subOrigin);
        Git(subOrigin, "config", "user.name", "Sub Probe");
        Git(subOrigin, "config", "user.email", "sub@example.invalid");
        File.WriteAllText(Path.Combine(subOrigin, "core.txt"), "lib v1\n");
        Git(subOrigin, "add", ".");
        Git(subOrigin, "commit", "-m", "sub v1");

        var repoPath = Path.Combine(_root, "主仓-裁剪探针");
        Git(_root, "init", "-b", "main", repoPath);
        Git(repoPath, "config", "user.name", "Probe User");
        Git(repoPath, "config", "user.email", "probe@example.invalid");
        File.WriteAllText(Path.Combine(repoPath, "README.md"), "# GitNest Trim Probe\n");
        File.WriteAllText(Path.Combine(repoPath, ".gitattributes"), "*.bin filter=lfs diff=lfs merge=lfs -text\n");
        Directory.CreateDirectory(Path.Combine(repoPath, "assets"));
        var lfsSha = new string('b', 64);
        File.WriteAllText(
            Path.Combine(repoPath, "assets", "model.bin"),
            $"version https://git-lfs.github.com/spec/v1\noid sha256:{lfsSha}\nsize 2048\n");
        Git(repoPath, "add", ".");
        Git(repoPath, "commit", "-m", "initial with lfs pointer");

        Git(repoPath, "-c", "protocol.file.allow=always", "submodule", "add", subOrigin, "deps/lib-core");
        Git(repoPath, "commit", "-m", "add read-only submodule");
        Git(repoPath, "tag", "v1.0.0");
        Git(repoPath, "tag", "-a", "v1.0.1", "-m", "Release v1.0.1");

        var wtPath = Path.Combine(_root, "联动工作树");
        Git(repoPath, "worktree", "add", "-b", "feat/trim-check", wtPath);
        File.AppendAllText(Path.Combine(repoPath, "README.md"), "local modification\n");

        App.SetTheme("Light", "");
        App.SetLocale("zh_CN");

        var launcher = _launcher = new VM.Launcher(repoPath);
        typeof(App).GetField("_launcher", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(Application.Current, launcher);
        var repo = (VM.Repository)launcher.ActivePage.Data;
        repo.IsWorktreeGroupExpanded = true;
        repo.IsSubmoduleGroupExpanded = true;
        repo.IsTagGroupExpanded = true;

        var repoView = new Views.Repository { DataContext = repo };
        var window = _window = new Window
        {
            Content = repoView,
            Width = 1280,
            Height = 860,
            ShowInTaskbar = false,
            Title = "GitNest 裁剪入口与只读识别探针",
        };
        window.Show();

        Await(() =>
            repo.Worktrees.Count == 2 &&
            repo.Submodules.Count == 1 &&
            repo.Tags.Count == 2 &&
            repo.Histories is { IsLoading: false, Commits.Count: >= 2 } &&
            repo.LocalChangesCount >= 1);

        Check(repo.Tags.Any(t => t.Name == "v1.0.1" && t.IsAnnotated) && App.Text("Tag.Annotated") == "附注标签",
              "Read-only Tags group renders annotated tag label cleanly from zh_CN locale");
        Check(repo.Submodules.Any(s => s.Path == "deps/lib-core"),
              "Read-only Submodules group displays deps/lib-core");

        var toolTips = repoView.GetVisualDescendants()
            .OfType<Control>()
            .Select(ToolTip.GetTip)
            .Select(tip => tip?.ToString() ?? string.Empty)
            .Where(s => !string.IsNullOrEmpty(s))
            .ToList();
        string[] forbiddenTips =
        [
            "应用补丁", "Apply Patch",
            "Git-Flow", "Bisect", "二分",
            "新建标签", "Create Tag",
            "添加子模块", "Add Submodule",
            "更新子模块", "Update Submodules",
            "新建工作树", "Add Worktree",
            "AI 助手", "AI Assistant", "使用 AI", "Use AI", "OpenAI",
        ];
        foreach (var forbidden in forbiddenTips)
        {
            Check(toolTips.All(t => !t.Contains(forbidden, StringComparison.OrdinalIgnoreCase)),
                  $"Repository chrome omits forbidden tooltip '{forbidden}'");
        }

        // Verify Command Palette excludes removed commands
        var palette = new VM.RepositoryCommandPalette(repo);
        var cmdsField = typeof(VM.RepositoryCommandPalette).GetField("_cmds", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var cmds = (IEnumerable)cmdsField.GetValue(palette)!;
        var labels = new List<string>();
        foreach (var item in cmds)
            labels.Add(item?.GetType().GetProperty("Label")?.GetValue(item)?.ToString() ?? string.Empty);
        Check(labels.Count > 0 &&
              labels.All(l => !l.Contains("新建标签", StringComparison.Ordinal) &&
                              !l.Contains("应用补丁", StringComparison.Ordinal) &&
                              !l.Contains("Custom", StringComparison.OrdinalIgnoreCase)),
              "RepositoryCommandPalette omits pruned CreateTag, Apply, and CustomAction commands");

        // Switch to WorkingCopy briefly to verify CommitMessageToolBox omits AI helper
        repo.SelectedViewIndex = 1;
        Pump();
        var wcToolTips = repoView.GetVisualDescendants()
            .OfType<Control>()
            .Select(ToolTip.GetTip)
            .Select(tip => tip?.ToString() ?? string.Empty)
            .ToList();
        Check(wcToolTips.All(t => !t.Contains("AI 助手", StringComparison.OrdinalIgnoreCase) &&
                                  !t.Contains("Use AI", StringComparison.OrdinalIgnoreCase) &&
                                  !t.Contains("使用 AI", StringComparison.OrdinalIgnoreCase)),
              "WorkingCopy & CommitMessageToolBox omit AI assistant buttons");

        repo.SelectedViewIndex = 0;
        Pump();
        Screenshot(window, "trim-repo-light");

        App.SetTheme("Dark", "");
        window.Width = 1080;
        window.Height = 720;
        Pump();
        Screenshot(window, "trim-repo-dark-narrow");

        // RepositoryConfigure tab surface check
        App.SetTheme("Light", "");
        var cfgVm = new VM.RepositoryConfigure(repo);
        var cfgWin = new Views.RepositoryConfigure
        {
            DataContext = cfgVm,
            Width = 820,
            Height = 640,
            ShowInTaskbar = false,
            Title = "GitNest 仓库配置裁剪探针",
        };
        _window = cfgWin;
        window.Close();
        cfgWin.Show();
        Await(() => !cfgVm.IsLoading);

        var cfgTabControl = cfgWin.GetVisualDescendants().OfType<TabControl>().First();
        Check(cfgTabControl.Items.Count == 3,
              $"RepositoryConfigure exposes exactly 3 tabs after removing CustomAction and OpenAI (actual={cfgTabControl.Items.Count})");
        Screenshot(cfgWin, "trim-config-tabs-light");
        cfgWin.Close();

        // Preferences tab surface check
        var prefWin = new Views.Preferences
        {
            DataContext = VM.Preferences.Instance,
            Width = 820,
            Height = 620,
            ShowInTaskbar = false,
            Title = "GitNest 偏好设置裁剪探针",
        };
        _window = prefWin;
        prefWin.Show();
        Pump();

        var prefTabControl = prefWin.GetVisualDescendants().OfType<TabControl>().First();
        Check(prefTabControl.Items.Count == 5,
              $"Preferences exposes exactly 5 tabs after removing CustomAction and AI (actual={prefTabControl.Items.Count})");
        Screenshot(prefWin, "trim-preferences-tabs-light");
        prefWin.Close();
        _window = null;
    }
}
