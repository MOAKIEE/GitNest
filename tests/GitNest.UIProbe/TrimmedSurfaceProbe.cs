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

        // Verify Batch 2 removed Rebase, MergeMultiple, and AssumeUnchanged add entries from context menus
        Git(repoPath, "branch", "feat/menu-probe");
        Git(repoPath, "update-ref", "refs/remotes/origin/main", "HEAD");
        repo.MarkBranchesDirtyManually();
        Await(() => repo.Branches.Any(b => b.Name == "feat/menu-probe") && repo.Branches.Any(b => !b.IsLocal));

        var branchTreeView = window.GetVisualDescendants().OfType<Views.BranchTree>().FirstOrDefault();
        var historiesView = window.GetVisualDescendants().OfType<Views.Histories>().FirstOrDefault();
        repo.SelectedViewIndex = 1;
        Pump();
        var workingCopyView = window.GetVisualDescendants().OfType<Views.WorkingCopy>().FirstOrDefault();

        var localOther = repo.Branches.First(b => b.IsLocal && !b.IsCurrent);
        var remoteBranch = repo.Branches.First(b => !b.IsLocal);
        var multiBranches = repo.Branches.Where(b => b.IsLocal).Take(2).ToList();
        var localMenu = branchTreeView!.CreateContextMenuForLocalBranch(repo, localOther);
        var remoteMenu = branchTreeView.CreateContextMenuForRemoteBranch(repo, remoteBranch);
        var multiBranchMenu = branchTreeView.CreateContextMenuForMultipleBranches(repo, multiBranches);

        static List<string> ExtractMenuHeaders(ContextMenu menu) =>
            menu.Items.OfType<MenuItem>().Select(m => m.Header?.ToString() ?? string.Empty).ToList();

        var localHeaders = ExtractMenuHeaders(localMenu);
        var remoteHeaders = ExtractMenuHeaders(remoteMenu);
        var multiBranchHeaders = ExtractMenuHeaders(multiBranchMenu);
        Check(localHeaders.All(h => !h.Contains("Rebase", StringComparison.OrdinalIgnoreCase) && !h.Contains("变基", StringComparison.Ordinal)) &&
              remoteHeaders.All(h => !h.Contains("Rebase", StringComparison.OrdinalIgnoreCase) && !h.Contains("变基", StringComparison.Ordinal)) &&
              multiBranchHeaders.All(h => !h.Contains("Merge selected", StringComparison.OrdinalIgnoreCase) && !h.Contains("合并(merge)", StringComparison.Ordinal)),
              "BranchTree context menus omit removed Rebase and MergeMultiple items");

        var singleCommitMenu = historiesView!.CreateContextMenuForSingleCommit(repo, repo.Histories.Commits[0]);
        var multiCommitMenu = historiesView.CreateContextMenuForMultipleCommits(repo.Histories.Commits.Take(2).ToList());
        var singleCommitHeaders = ExtractMenuHeaders(singleCommitMenu);
        var multiCommitHeaders = ExtractMenuHeaders(multiCommitMenu);
        Check(singleCommitHeaders.All(h => !h.Contains("Rebase", StringComparison.OrdinalIgnoreCase) && !h.Contains("变基", StringComparison.Ordinal)) &&
              multiCommitHeaders.All(h => !h.Equals("Merge...", StringComparison.OrdinalIgnoreCase) && !h.Contains("合并(merge)", StringComparison.Ordinal)),
              "Histories context menus omit removed Rebase and MergeMultiple items");

        var wcVm = (VM.WorkingCopy)workingCopyView!.DataContext!;
        var unstagedSelection = new VM.ChangeSelection(new List<Change>
        {
            new Change { Path = "README.md", WorkTree = ChangeState.Modified }
        });
        var unstagedMenu = workingCopyView.CreateContextMenuForUnstagedChanges(repo, wcVm, unstagedSelection);
        var unstagedHeaders = ExtractMenuHeaders(unstagedMenu);
        Check(unstagedHeaders.All(h => !h.Contains("Assume unchanged", StringComparison.OrdinalIgnoreCase) && !h.Contains("不跟踪此文件的更改", StringComparison.Ordinal)),
              "WorkingCopy unstaged context menu omits removed Assume unchanged item");

        // Verify Batch 3 removed custom CommitTemplates and git commit.template from CommitMessageToolBox picker while retaining recent history
        var toolBox = workingCopyView.GetVisualDescendants().OfType<Views.CommitMessageToolBox>().FirstOrDefault();
        var pickerHelperBtn = toolBox?.GetVisualDescendants()
            .OfType<Button>()
            .FirstOrDefault(b => (ToolTip.GetTip(b)?.ToString() ?? string.Empty).Equals(App.Text("WorkingCopy.CommitMessageHelper"), StringComparison.Ordinal));
        var tmplFile = Path.Combine(repoPath, ".git-commit-template.txt");
        File.WriteAllText(tmplFile, "chore: unused template\n");
        Git(repoPath, "config", "--local", "commit.template", tmplFile);
        repo.UIStates.AddRecentCommitMessage("feat: recent probe commit");
        var populatedPickerMenu = toolBox!.CreateCommitMessagePickerMenu(wcVm);
        static List<string> ExtractPickerTexts(ContextMenu menu)
        {
            var texts = new List<string>();
            foreach (var item in menu.Items.OfType<MenuItem>())
            {
                if (item.Header is TextBlock tb)
                    texts.Add(tb.Text ?? string.Empty);
                else if (item.Header is string s)
                    texts.Add(s);
            }
            return texts;
        }
        var pickerTexts = ExtractPickerTexts(populatedPickerMenu);
        var helperTip = ToolTip.GetTip(pickerHelperBtn!)?.ToString() ?? string.Empty;
        Check(pickerHelperBtn != null &&
              !helperTip.Contains("Template", StringComparison.OrdinalIgnoreCase) &&
              !helperTip.Contains("模板", StringComparison.Ordinal) &&
              pickerTexts.Any(t => t.Contains("feat: recent probe commit", StringComparison.Ordinal)) &&
              pickerTexts.Any(t => t.Equals(App.Text("WorkingCopy.ClearCommitHistories"), StringComparison.Ordinal)) &&
              pickerTexts.All(t => !t.StartsWith("Template:", StringComparison.OrdinalIgnoreCase) && !t.StartsWith("模板：", StringComparison.Ordinal)),
              "CommitMessageToolBox picker omits templates and commit.template while preserving recent commit history and clear action");

        repo.UIStates.RecentCommitMessages.Clear();
        var emptyPickerTexts = ExtractPickerTexts(toolBox.CreateCommitMessagePickerMenu(wcVm));
        Check(emptyPickerTexts.Any(t => t.Equals(App.Text("WorkingCopy.NoCommitHistories"), StringComparison.Ordinal)) &&
              emptyPickerTexts.All(t => !t.Contains("模板", StringComparison.Ordinal) && !t.Contains("Template", StringComparison.OrdinalIgnoreCase)),
              "CommitMessageToolBox picker displays empty-history state without template placeholders");

        // Verify read-only AssumeUnchangedManager tip, list, and unmark restore
        Git(repoPath, "update-index", "--assume-unchanged", "README.md");
        File.AppendAllText(Path.Combine(repoPath, "README.md"), "edited while assumed\n");
        var assumeVm = new VM.AssumeUnchangedManager(repo);
        App.SetTheme("Light", "");
        var assumeWin = new Views.AssumeUnchangedManager
        {
            DataContext = assumeVm,
            Width = 620,
            Height = 360,
            ShowInTaskbar = false,
            Title = "GitNest AssumeUnchanged 只读管理探针",
        };
        assumeWin.Show();
        Await(() => assumeVm.Files.Count == 1);
        var assumeTip = assumeWin.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == App.Text("AssumeUnchanged.Tip"));
        var removeBtn = assumeWin.GetVisualDescendants().OfType<Button>().FirstOrDefault(b => Equals(ToolTip.GetTip(b), App.Text("AssumeUnchanged.RemoveTip")));
        Check(assumeTip && removeBtn != null && assumeVm.Files[0] == "README.md",
              "AssumeUnchangedManager displays read-only guidance tip, marked file, and remove tooltip");
        Screenshot(assumeWin, "trim-assume-unchanged-light");
        App.SetTheme("Dark", "");
        assumeWin.Width = 500;
        Pump();
        Screenshot(assumeWin, "trim-assume-unchanged-dark-narrow");

        removeBtn!.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Await(() => assumeVm.Files.Count == 0);
        var lsAfter = Git(repoPath, "ls-files", "-v", "--", "README.md");
        Check(lsAfter.TrimStart().StartsWith("H ", StringComparison.Ordinal),
              "Removing file in AssumeUnchangedManager restores Git tracking");
        assumeWin.Close();
        App.SetTheme("Light", "");
        repo.SelectedViewIndex = 0;
        Pump();

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
        var cfgTabHeaders = cfgTabControl.Items
            .OfType<TabItem>()
            .Select(t => (t.Header as TextBlock)?.Text ?? t.Header?.ToString() ?? string.Empty)
            .ToList();
        Check(cfgTabControl.Items.Count == 2 &&
              cfgTabHeaders.All(h => !h.Contains("模板", StringComparison.Ordinal) && !h.Contains("Template", StringComparison.OrdinalIgnoreCase)),
              $"RepositoryConfigure exposes exactly 2 tabs after removing CommitTemplate, CustomAction, and OpenAI (actual={cfgTabControl.Items.Count})");
        Screenshot(cfgWin, "trim-config-tabs-light");
        App.SetTheme("Dark", "");
        cfgWin.Width = 680;
        Pump();
        Screenshot(cfgWin, "trim-config-tabs-dark-narrow");
        App.SetTheme("Light", "");
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
        var languagePicker = prefWin.GetVisualDescendants().OfType<ComboBox>()
            .Single(c => ReferenceEquals(c.ItemsSource, Locale.Supported));
        Check(languagePicker.Items.Count == 2, "Language picker exposes Simplified Chinese and English");
        Screenshot(prefWin, "trim-preferences-tabs-light");
        prefWin.Close();

        App.SetLocale("en_US");
        var release = new SourceGit.Models.Version
        {
            TagName = "v0.2.0",
            PublishedAt = DateTime.UtcNow,
            Body = "# GitNest 0.2.0\n\nReview changes and download the new version from GitNest Releases.",
        };
        var updateVm = new VM.SelfUpdate { Data = release };
        var updateWin = new Views.SelfUpdate { DataContext = updateVm, ShowInTaskbar = false };
        _window = updateWin;
        updateWin.Show();
        Pump();
        Check(updateWin.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == "v0.2.0"),
              "GitNest update dialog displays the release tag");
        Screenshot(updateWin, "gitnest-update-light");

        App.SetTheme("Dark", "");
        App.SetLocale("zh_CN");
        updateVm.Data = new SelfUpdateFailed(new InvalidOperationException(App.Text("SelfUpdate.Unavailable")));
        Pump();
        Check(updateWin.GetVisualDescendants().OfType<Button>().Any(b => b.Content as string == "打开 GitNest 发行页"),
              "Unavailable private or unpublished release offers the GitNest releases page");
        Screenshot(updateWin, "gitnest-update-unavailable-dark");
        updateWin.Close();
        _window = null;
    }
}
