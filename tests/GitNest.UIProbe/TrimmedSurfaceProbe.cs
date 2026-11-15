using System.Collections;
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
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

        var cfgTabControls = cfgWin.GetVisualDescendants().OfType<TabControl>().ToList();
        var cfgExpander = cfgWin.FindControl<ToggleButton>("AdvancedExpander");
        var cfgScroller = cfgWin.FindControl<ScrollViewer>("GitConfigScroller");
        var cfgIssueList = cfgWin.GetVisualDescendants().OfType<ListBox>().FirstOrDefault(l => ReferenceEquals(l.ItemsSource, cfgVm.IssueTrackers));
        Check(cfgTabControls.Count == 0 &&
              cfgExpander != null &&
              cfgExpander.IsChecked == false &&
              cfgIssueList != null &&
              !cfgIssueList.IsEffectivelyVisible,
              "RepositoryConfigure renders as a single scrollable Git page with IssueTracker collapsed under Advanced");
        Screenshot(cfgWin, "trim-config-singlepage-light");

        cfgExpander!.IsChecked = true;
        Pump();
        cfgScroller?.ScrollToEnd();
        Pump();
        Check(cfgIssueList!.IsEffectivelyVisible,
              "Expanding Advanced reveals IssueTracker rules inside the single RepositoryConfigure page");
        Screenshot(cfgWin, "trim-config-advanced-issues-light");

        App.SetTheme("Dark", "");
        cfgWin.Width = 680;
        Pump();
        Screenshot(cfgWin, "trim-config-advanced-issues-dark-narrow");
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

        // Batch 4: Narrowed LFS toolbar menu, removed legacy submodule auto-update UI, and Checkout submodule opt-in + 2-step confirm
        static List<string> CollectAllMenuTexts(MenuItem root)
        {
            var texts = new List<string>();
            if (root.Header != null)
                texts.Add(root.Header.ToString() ?? string.Empty);
            foreach (var child in root.Items.OfType<MenuItem>())
                texts.AddRange(CollectAllMenuTexts(child));
            return texts;
        }

        static List<string> CollectContextMenuTexts(ContextMenu menu) =>
            menu.Items.OfType<MenuItem>().SelectMany(CollectAllMenuTexts).ToList();

        App.SetLocale("zh_CN");
        var toolbar = repoView.GetVisualDescendants().OfType<Views.RepositoryToolbar>().FirstOrDefault()
            ?? new Views.RepositoryToolbar();
        var lfsMenuBeforeInstallZh = toolbar.CreateGitLFSMenu(repo);
        var lfsLabelsBeforeZh = CollectContextMenuTexts(lfsMenuBeforeInstallZh);
        Check(!lfsLabelsBeforeZh.Any(t => t.Contains("精简", StringComparison.Ordinal) || t.Contains('锁') || t.Contains("Prune", StringComparison.OrdinalIgnoreCase)),
              "Zh un-installed Git LFS toolbar menu omits Prune and Locks");
        Check(lfsLabelsBeforeZh.Any(t => t.Contains("状态：", StringComparison.Ordinal)) &&
              lfsLabelsBeforeZh.Any(t => t.Contains("高级 LFS 操作", StringComparison.Ordinal)) &&
              lfsLabelsBeforeZh.Any(t => t.Contains("启用Git LFS支持", StringComparison.Ordinal)),
              "Zh un-installed Git LFS toolbar menu shows status and puts Install under 高级 LFS 操作");

        var installTask = repo.InstallLFSAsync();
        Await(() => installTask.IsCompleted);
        Check(installTask.IsCompletedSuccessfully && repo.IsLFSEnabled(), "Installing Git LFS in UI probe repo succeeds");

        var lfsMenuZh = toolbar.CreateGitLFSMenu(repo);
        var lfsMenuLabelsZh = CollectContextMenuTexts(lfsMenuZh);
        Check(!lfsMenuLabelsZh.Any(t => t.Contains("精简", StringComparison.Ordinal) || t.Contains('锁') || t.Contains("Prune", StringComparison.OrdinalIgnoreCase)),
              "Zh Git LFS toolbar menu omits Prune and Locks");
        Check(lfsMenuLabelsZh.Any(t => t.Contains("高级 LFS 操作", StringComparison.Ordinal)) &&
              lfsMenuLabelsZh.Any(t => t.Contains("状态：", StringComparison.Ordinal)) &&
              lfsMenuLabelsZh.Any(t => t.Contains("添加追踪文件规则", StringComparison.Ordinal)) &&
              lfsMenuLabelsZh.Any(t => t.Contains("fetch", StringComparison.OrdinalIgnoreCase)) &&
              lfsMenuLabelsZh.Any(t => t.Contains("pull", StringComparison.OrdinalIgnoreCase)),
              "Zh Git LFS toolbar menu keeps explicit status and groups operations under 高级 LFS 操作");

        App.SetLocale("en_US");
        var lfsMenuEn = toolbar.CreateGitLFSMenu(repo);
        var lfsMenuLabelsEn = CollectContextMenuTexts(lfsMenuEn);
        Check(!lfsMenuLabelsEn.Any(t => t.Contains("Prune", StringComparison.OrdinalIgnoreCase) || t.Contains("Lock", StringComparison.OrdinalIgnoreCase)),
              "En Git LFS toolbar menu omits Prune and Locks");
        Check(lfsMenuLabelsEn.Any(t => t.Contains("Advanced LFS Operations", StringComparison.Ordinal)) &&
              lfsMenuLabelsEn.Any(t => t.Contains("Status:", StringComparison.Ordinal)) &&
              lfsMenuLabelsEn.Any(t => t.Contains("Add Track Pattern", StringComparison.Ordinal)) &&
              lfsMenuLabelsEn.Any(t => t == "Fetch") &&
              lfsMenuLabelsEn.Any(t => t == "Pull") &&
              lfsMenuLabelsEn.Any(t => t == "Push"),
              "En Git LFS toolbar menu keeps explicit status and groups operations under Advanced LFS Operations");

        // Verify RepositoryConfigure Advanced section omits removed submodule auto-update checkboxes
        App.SetLocale("zh_CN");
        var cfgBatch4Vm = new VM.RepositoryConfigure(repo);
        var cfgBatch4Win = new Views.RepositoryConfigure
        {
            DataContext = cfgBatch4Vm,
            Width = 760,
            Height = 620,
            ShowInTaskbar = false,
            Title = "Batch4 RepositoryConfigure Check",
        };
        _window = cfgBatch4Win;
        cfgBatch4Win.Show();
        Pump();
        var allCfgTexts = cfgBatch4Win.GetVisualDescendants().OfType<TextBlock>()
            .Select(tb => tb.Text ?? string.Empty)
            .Concat(cfgBatch4Win.GetVisualDescendants().OfType<CheckBox>().Select(cb => cb.Content?.ToString() ?? string.Empty))
            .ToList();
        Check(!allCfgTexts.Any(t => t.Contains("自动更新子模块", StringComparison.Ordinal) || t.Contains("auto-updating submodules", StringComparison.OrdinalIgnoreCase)),
              "RepositoryConfigure omits legacy submodule auto-update settings");
        cfgBatch4Win.Close();

        // Prepare second submodule commit and broken submodule commit for Checkout opt-in UI probe
        Git(_root, "config", "--global", "protocol.file.allow", "always");
        File.WriteAllText(Path.Combine(subOrigin, "core.txt"), "lib v2\n");
        Git(subOrigin, "add", ".");
        Git(subOrigin, "commit", "-m", "sub v2");

        // Reset clean main worktree on repoPath (undo sample README append so direct checkout works cleanly)
        Git(repoPath, "checkout", "--", "README.md");
        Git(repoPath, "checkout", "-b", "feat/sub-bump");
        Git(Path.Combine(repoPath, "deps", "lib-core"), "pull", subOrigin, "main");
        Git(repoPath, "add", "deps/lib-core");
        Git(repoPath, "commit", "-m", "bump deps/lib-core to v2");
        Git(repoPath, "checkout", "-b", "feat/sub-broken");
        Git(repoPath, "update-index", "--add", "--cacheinfo", "160000,0123456789abcdef0123456789abcdef01234567,deps/lib-core");
        Git(repoPath, "commit", "-m", "broken deps/lib-core pointer");
        Git(repoPath, "checkout", "main");
        Git(repoPath, "submodule", "update", "--recursive");

        repo.MarkBranchesDirtyManually();
        Await(() => repo.Branches.Any(b => b.IsLocal && b.Name == "feat/sub-bump") &&
                    repo.Branches.Any(b => b.IsLocal && b.Name == "feat/sub-broken"));

        var bumpBranch = repo.Branches.Single(b => b.IsLocal && b.Name == "feat/sub-bump");
        var brokenBranch = repo.Branches.Single(b => b.IsLocal && b.Name == "feat/sub-broken");

        var checkoutVm = new VM.Checkout(repo, bumpBranch);
        Check(!checkoutVm.UpdateSubmodulesAfterSwitch && checkoutVm.ShowSubmoduleUpdateOption,
              "Checkout popup defaults UpdateSubmodulesAfterSwitch to false when submodules exist");

        App.SetTheme("Light", "");
        App.SetLocale("zh_CN");
        launcher.ActivePage.Popup = checkoutVm;
        var checkoutHostWin = new Window
        {
            Content = new Views.LauncherPage { DataContext = launcher.ActivePage },
            Width = 960,
            Height = 560,
            ShowInTaskbar = false,
            Title = "Batch4 Checkout Default Opt-In Probe",
        };
        _window = checkoutHostWin;
        checkoutHostWin.Show();
        Pump();

        var defaultOptionCheck = checkoutHostWin.GetVisualDescendants()
            .OfType<CheckBox>()
            .FirstOrDefault(cb => cb.GetVisualDescendants().OfType<TextBlock>().Any(tb => tb.Text == App.Text("Checkout.UpdateSubmodulesAfterSwitch")));
        Check(defaultOptionCheck != null && defaultOptionCheck.IsChecked == false,
              "Checkout popup renders unchecked '切换后更新子模块' checkbox by default");
        Screenshot(checkoutHostWin, "trim-checkout-submodule-default-light");

        // Tick opt-in, execute phase 1 (switches main branch and opens phase 2 confirm) in narrow Dark theme
        checkoutVm.UpdateSubmodulesAfterSwitch = true;
        Pump();
        var phase1Done = launcher.ActivePage.ProcessPopupAsync();
        Await(() => phase1Done.IsCompleted);
        Pump();
        Check(checkoutVm.IsAwaitingSubmoduleConfirm &&
              checkoutVm.AffectedSubmodules.Contains("deps/lib-core") &&
              checkoutVm.ConfirmButtonText == App.Text("Checkout.Submodules.ConfirmUpdate") &&
              checkoutVm.CancelButtonText == App.Text("Checkout.Submodules.SkipAndClose"),
              "Phase 1 switches main branch and shows affected submodule paths & recursive confirmation card");

        App.SetTheme("Dark", "");
        checkoutHostWin.Width = 600;
        checkoutHostWin.Height = 560;
        Pump();
        var popupPanel = checkoutHostWin.GetVisualDescendants().OfType<ContentControl>().First(c => c.Name == "PopupPanel");
        var overflowTexts = new List<string>();
        foreach (var tb in popupPanel.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsVisible && t.Bounds.Width > 0))
        {
            var topLeft = tb.TranslatePoint(new Point(0, 0), checkoutHostWin);
            if (topLeft.HasValue && topLeft.Value.X + tb.Bounds.Width > checkoutHostWin.Bounds.Width + 2)
                overflowTexts.Add($"{tb.Text} ({topLeft.Value.X + tb.Bounds.Width:F1} > {checkoutHostWin.Bounds.Width:F1})");
        }
        Check(overflowTexts.Count == 0,
              $"Dark narrow submodule confirm popup fits within 600px window bounds without horizontal clipping (overflows: {string.Join("; ", overflowTexts)})");
        Screenshot(checkoutHostWin, "trim-checkout-submodule-confirm-dark-narrow");

        // Complete phase 2 confirm on feat/sub-bump
        checkoutVm.RecursiveSubmoduleUpdate = true;
        var phase2Done = launcher.ActivePage.ProcessPopupAsync();
        Await(() => phase2Done.IsCompleted);
        Pump();
        Check(launcher.ActivePage.Popup == null,
              "Confirming phase 2 submodule update succeeds and closes checkout popup");

        // Now probe broken submodule update partial failure card (Light, en_US)
        App.SetTheme("Light", "");
        App.SetLocale("en_US");
        checkoutHostWin.Width = 680;
        var failCheckoutVm = new VM.Checkout(repo, brokenBranch)
        {
            UpdateSubmodulesAfterSwitch = true,
        };
        launcher.ActivePage.Popup = failCheckoutVm;
        Pump();
        var failPhase1 = launcher.ActivePage.ProcessPopupAsync();
        Await(() => failPhase1.IsCompleted);
        Pump();
        Check(failCheckoutVm.IsAwaitingSubmoduleConfirm, "Broken submodule checkout enters phase 2 confirm");
        var failPhase2 = launcher.ActivePage.ProcessPopupAsync();
        Await(() => failPhase2.IsCompleted);
        Pump();
        Check(failCheckoutVm.HasResultView &&
              failCheckoutVm.IsSubmoduleFailureResult &&
              launcher.ActivePage.Popup == failCheckoutVm,
              "Broken submodule update keeps popup open and displays separated main-success / submodule-failure result");
        Screenshot(checkoutHostWin, "trim-checkout-submodule-partial-failure-light");
        checkoutHostWin.Close();
        launcher.ActivePage.Popup = null;
        App.SetTheme("Light", "");
        App.SetLocale("zh_CN");

        // Batch 5: Dedicated AmendLastCommit modal + More menu + Protected/Unpushed/Partial-Retry states
        var amendOrigin = Path.Combine(_root, "amend-probe-origin.git");
        var amendOriginMoved = Path.Combine(_root, "amend-probe-origin-offline.git");
        var amendRepoPath = Path.Combine(_root, "amend-ui-probe");
        Git(_root, "init", "--bare", "-b", "main", amendOrigin);
        Git(_root, "clone", amendOrigin, amendRepoPath);
        Git(amendRepoPath, "config", "user.name", "Probe User");
        Git(amendRepoPath, "config", "user.email", "probe@example.invalid");
        File.WriteAllText(Path.Combine(amendRepoPath, "app.txt"), "v1\n");
        Git(amendRepoPath, "add", ".");
        Git(amendRepoPath, "commit", "-m", "chore: initial app");
        Git(amendRepoPath, "push", "-u", "origin", "main");
        Git(amendRepoPath, "fetch", "origin");

        var prevPage5 = launcher.ActivePage;
        var normalizedAmendRepo = amendRepoPath.Replace('\\', '/');
        launcher.OpenRepositoryInTab(amendRepoPath, null);
        Await(() => launcher.ActivePage?.Data is VM.Repository r &&
                    string.Equals(r.FullPath.Replace('\\', '/'), normalizedAmendRepo, StringComparison.OrdinalIgnoreCase) &&
                    r.CurrentBranch != null);
        var amendPage = launcher.ActivePage;
        var amendRepo = (VM.Repository)amendPage.Data;
        try
        {
            amendRepo.SelectedViewIndex = 1; // Working Copy view
            Pump();

            var amendHostWin = new Window
            {
                Content = new Views.LauncherPage { DataContext = amendPage },
                Width = 1080,
                Height = 680,
                ShowInTaskbar = false,
                Title = "Batch5 Amend UI Probe",
            };
            _window = amendHostWin;
            amendHostWin.Show();
            Await(() => amendHostWin.GetVisualDescendants().OfType<Views.WorkingCopy>().FirstOrDefault()?.DataContext is VM.WorkingCopy);

            var wcView5 = amendHostWin.GetVisualDescendants().OfType<Views.WorkingCopy>().First();
            var wcVm5 = (VM.WorkingCopy)wcView5.DataContext!;
            var hasAmendCheckbox5 = wcView5.GetVisualDescendants().OfType<CheckBox>()
                .Any(c => Equals(c.Content?.ToString(), "Amend") || Equals(c.Content?.ToString(), "修补"));
            var moreBtn5 = wcView5.FindControl<Button>("CommitMoreActionsButton");
            Check(!hasAmendCheckbox5 && moreBtn5 != null && wcVm5.CanOpenAmendLastCommit,
                  "WorkingCopy removes regular Amend checkbox and exposes More actions Amend menu");
            Screenshot(amendHostWin, "trim-workingcopy-more-actions-light");

            // Case 1: Already pushed on protected 'main' (Dark, zh_CN, Narrow 680px)
            File.AppendAllText(Path.Combine(amendRepoPath, "app.txt"), "protected tweak\n");
            Git(amendRepoPath, "add", "app.txt");
            var protAmendVm = new VM.AmendLastCommit(amendRepo, wcVm5);
            Await(() => protAmendVm.EnsureLoadedAsync().IsCompleted);
            Check(protAmendVm.IsAlreadyPushed &&
                  protAmendVm.BlockedByBranchProtection &&
                  !protAmendVm.CanConfirm &&
                  protAmendVm.GuideNewCommitAsPrimary,
                  "Pushed commit on protected main blocks Amend rewrite and guides Create New Commit Instead");

            App.SetTheme("Dark", "");
            App.SetLocale("zh_CN");
            amendHostWin.Width = 680;
            amendPage.Popup = protAmendVm;
            Pump();
            Screenshot(amendHostWin, "trim-amend-protected-dark-narrow");
            amendPage.CancelPopup();

            // Case 2: Unpushed local feature branch with newly staged file (Light, zh_CN)
            Git(amendRepoPath, "checkout", "-b", "feat/amend-ui");
            File.WriteAllText(Path.Combine(amendRepoPath, "feature.txt"), "draft v1\n");
            Git(amendRepoPath, "add", "feature.txt");
            Git(amendRepoPath, "commit", "-m", "feat: local draft");
            File.AppendAllText(Path.Combine(amendRepoPath, "feature.txt"), "extra line\n");
            Git(amendRepoPath, "add", "feature.txt");

            var unpushedAmendVm = new VM.AmendLastCommit(amendRepo, wcVm5);
            Await(() => unpushedAmendVm.EnsureLoadedAsync().IsCompleted);
            Check(unpushedAmendVm.IsUnpushedLocalOnly &&
                  unpushedAmendVm.HasNewlyStagedChanges &&
                  unpushedAmendVm.CanConfirm &&
                  unpushedAmendVm.CommitMessage == "feat: local draft",
                  "Unpushed branch Amend modal preloads message, lists newly staged changes, and enables confirmation");

            App.SetTheme("Light", "");
            amendHostWin.Width = 980;
            amendPage.Popup = unpushedAmendVm;
            Pump();
            Screenshot(amendHostWin, "trim-amend-unpushed-light");
            var confirmUnpushed = amendPage.ProcessPopupAsync();
            Await(() => confirmUnpushed.IsCompleted);
            Pump();
            Check(amendPage.Popup == null, "Unpushed Amend executes cleanly and closes modal");

            // Case 3: Pushed feature branch -> explicit opt-in -> remote offline partial failure -> Retry Push Only
            Git(amendRepoPath, "push", "-u", "origin", "feat/amend-ui");
            Git(amendRepoPath, "fetch", "origin");
            File.WriteAllText(Path.Combine(amendRepoPath, "retry-file.txt"), "retry payload\n");
            Git(amendRepoPath, "add", "retry-file.txt");

            var pushedAmendVm = new VM.AmendLastCommit(amendRepo, wcVm5);
            Await(() => pushedAmendVm.EnsureLoadedAsync().IsCompleted);
            Check(pushedAmendVm.IsAlreadyPushed &&
                  pushedAmendVm.CanOptInPushedRewrite &&
                  !pushedAmendVm.ConfirmRewritePushedWithLease &&
                  !pushedAmendVm.CanConfirm &&
                  pushedAmendVm.GuideNewCommitAsPrimary,
                  "Pushed feature branch Amend defaults to New Commit guide with lease rewrite checkbox unchecked");

            pushedAmendVm.ConfirmRewritePushedWithLease = true;
            Check(pushedAmendVm.CanConfirm, "Checking explicit lease rewrite opt-in enables Amend & Lease Push");

            App.SetLocale("en_US");
            amendPage.Popup = pushedAmendVm;
            Pump();

            // Simulate remote failure after preflight succeeded so local Amend succeeds while push fails
            Directory.Move(amendOrigin, amendOriginMoved);
            var partialTask = amendPage.ProcessPopupAsync();
            Await(() => partialTask.IsCompleted);
            Pump();
            var retryBtn5 = amendHostWin.GetVisualDescendants().OfType<Button>()
                .FirstOrDefault(b => Equals(b.Content?.ToString(), App.Text("AmendLastCommit.RetryPushOnly")) && b.IsVisible);
            Check(pushedAmendVm.IsPushRetryOnlyState &&
                  amendPage.Popup == pushedAmendVm &&
                  pushedAmendVm.ConfirmButtonText == App.Text("AmendLastCommit.RetryPushOnly") &&
                  retryBtn5 != null &&
                  retryBtn5.IsEffectivelyEnabled &&
                  wcVm5.HasTwoStepStatus &&
                  wcVm5.CanRetryPushOnly,
                  "Partial Amend failure keeps popup open with enabled Retry Push Only button and syncs WorkingCopy banner");
            Screenshot(amendHostWin, "trim-amend-partial-retry-light");

            // Restore remote and retry push ONLY (no second amend)
            Directory.Move(amendOriginMoved, amendOrigin);
            var amendedLocalHead = Git(amendRepoPath, "rev-parse", "HEAD");
            var retryPushTask = amendPage.ProcessPopupAsync();
            Await(() => retryPushTask.IsCompleted);
            Pump();
            var remoteAfterRetry = Git(amendOrigin, "rev-parse", "refs/heads/feat/amend-ui");
            Check(amendPage.Popup == null &&
                  string.Equals(remoteAfterRetry, amendedLocalHead, StringComparison.Ordinal) &&
                  !wcVm5.CanRetryPushOnly,
                  "Retry Push Only completes lease push without re-amending commit");

            amendHostWin.Close();
        }
        finally
        {
            if (Directory.Exists(amendOriginMoved) && !Directory.Exists(amendOrigin))
                Directory.Move(amendOriginMoved, amendOrigin);
            amendPage.Popup = null;
            launcher.CloseTab(amendPage);
            launcher.ActivePage = prevPage5;
        }

        App.SetLocale("zh_CN");

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

        // Batch 6: Retained visual capability spot-checks (Statistics window & special image decoding)
        var mediaDir = Path.Combine(_root, "retained-image-decoders");
        Directory.CreateDirectory(mediaDir);
        var tgaFile = Path.Combine(mediaDir, "sample.tga");
        var tgaBytes = new byte[18 + 2 * 2 * 3];
        tgaBytes[2] = 2;
        tgaBytes[12] = 2;
        tgaBytes[14] = 2;
        tgaBytes[16] = 24;
        File.WriteAllBytes(tgaFile, tgaBytes);

        var tiffFile = Path.Combine(mediaDir, "sample.tif");
        using (var tiffWriter = BitMiracle.LibTiff.Classic.Tiff.Open(tiffFile, "w"))
        {
            tiffWriter.SetField(BitMiracle.LibTiff.Classic.TiffTag.IMAGEWIDTH, 2);
            tiffWriter.SetField(BitMiracle.LibTiff.Classic.TiffTag.IMAGELENGTH, 2);
            tiffWriter.SetField(BitMiracle.LibTiff.Classic.TiffTag.SAMPLESPERPIXEL, 4);
            tiffWriter.SetField(BitMiracle.LibTiff.Classic.TiffTag.BITSPERSAMPLE, 8);
            tiffWriter.SetField(BitMiracle.LibTiff.Classic.TiffTag.PHOTOMETRIC, BitMiracle.LibTiff.Classic.Photometric.RGB);
            tiffWriter.SetField(BitMiracle.LibTiff.Classic.TiffTag.PLANARCONFIG, BitMiracle.LibTiff.Classic.PlanarConfig.CONTIG);
            tiffWriter.SetField(BitMiracle.LibTiff.Classic.TiffTag.COMPRESSION, BitMiracle.LibTiff.Classic.Compression.NONE);
            tiffWriter.SetField(BitMiracle.LibTiff.Classic.TiffTag.ROWSPERSTRIP, 2);
            var row = new byte[] { 255, 0, 0, 255, 0, 255, 0, 255 };
            tiffWriter.WriteScanline(row, 0);
            tiffWriter.WriteScanline(row, 1);
            tiffWriter.FlushData();
        }

        var tgaTask = VM.ImageSource.FromFileAsync(tgaFile, ImageDecoder.Pfim);
        var tiffTask = VM.ImageSource.FromFileAsync(tiffFile, ImageDecoder.Tiff);
        Await(() => tgaTask.IsCompleted && tiffTask.IsCompleted);
        var tgaSrc = tgaTask.GetAwaiter().GetResult();
        var tiffSrc = tiffTask.GetAwaiter().GetResult();
        Check(tgaSrc.Bitmap is { PixelSize: { Width: 2, Height: 2 } },
              "ImageSource decodes real TGA (Pfim) bitmap in Avalonia runtime");
        Check(tiffSrc.Bitmap is { PixelSize: { Width: 2, Height: 2 } },
              "ImageSource decodes real TIFF (LibTiff) bitmap in Avalonia runtime");

        var statsVm = new VM.Statistics(repoPath);
        var statsWin = new Views.Statistics { DataContext = statsVm, ShowInTaskbar = false };
        _window = statsWin;
        statsWin.Show();
        Await(() => !statsVm.IsLoading && statsVm.SelectedReport != null);
        Pump();
        Check(statsVm.SelectedReport is { Total: >= 1 },
              "Retained Statistics window loads repository commit report");
        statsWin.Close();
        _window = null;
    }
}
