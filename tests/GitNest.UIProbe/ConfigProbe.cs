using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using SourceGit;
using SourceGit.Models;
using VM = SourceGit.ViewModels;
using Views = SourceGit.Views;
using OS = SourceGit.Native.OS;

internal static partial class Program
{
    static partial void RunConfig()
    {
        var globalCfg = Environment.GetEnvironmentVariable("GIT_CONFIG_GLOBAL")!;
        File.WriteAllText(globalCfg,
            "[user]\n\tname = 全球作者\n\temail = global@example.invalid\n" +
            "[http]\n\tproxy = http://global-proxy.local:8080\n" +
            "[credential]\n\thelper =\n\thelper = manager\n" +
            "[http \"https://corp.internal\"]\n\tproxy = http://alice:top_secret_ui_probe_pw@proxy.corp.internal:3128\n");

        var repoPath = Path.Combine(_root, "配置 主仓库");
        Git(_root, "init", "-b", "main", repoPath);
        Git(repoPath, "config", "--local", "user.name", "Probe Setup");
        Git(repoPath, "config", "--local", "user.email", "setup@example.invalid");
        File.WriteAllText(Path.Combine(repoPath, "README.md"), "# GitNest Config Probe\n");
        Git(repoPath, "add", ".");
        Git(repoPath, "commit", "-m", "initial commit");
        var headSha = Git(repoPath, "rev-parse", "HEAD");

        Git(repoPath, "checkout", "-q", "-b", "feature/ui-probe");
        Git(repoPath, "config", "--local", "--unset", "user.name");
        Git(repoPath, "config", "--local", "user.email", "repo-local@example.invalid");
        Git(repoPath, "config", "--local", "user.signingkey", "ABC123DEF456");
        Git(repoPath, "config", "--local", "commit.gpgsign", "true");
        Git(repoPath, "config", "--local", "gpg.program", Path.Combine(_root, "missing-gpg-probe.exe"));
        Git(repoPath, "config", "--local", "core.sshCommand", "ssh -F ~/.ssh/work_config");
        Git(repoPath, "remote", "add", "origin", "https://git.example.com/team/main.git");
        Git(repoPath, "remote", "set-url", "--add", "origin", "https://mirror.example.com/team/main.git");
        Git(repoPath, "remote", "set-url", "--push", "--add", "origin", "git@git.example.com:team/main.git");
        Git(repoPath, "config", "--local", "--add", "remote.origin.fetch", "+refs/tags/*:refs/tags/*");
        Git(repoPath, "remote", "add", "upstream", "https://upstream.example.com/project.git");
        Git(repoPath, "update-ref", "refs/remotes/origin/feature/ui-probe", headSha);
        Git(repoPath, "update-ref", "refs/remotes/origin/main", headSha);
        Git(repoPath, "config", "--local", "branch.feature/ui-probe.remote", "origin");
        Git(repoPath, "config", "--local", "branch.feature/ui-probe.merge", "refs/heads/feature/ui-probe");

        App.SetTheme("Light", "");
        var repoVm = new VM.Repository(false, repoPath, Path.Combine(repoPath, ".git"));
        var vm = new VM.RepositoryConfigure(repoVm);
        var configWin = new Views.RepositoryConfigure
        {
            DataContext = vm,
            Width = 820,
            Height = 660,
            ShowInTaskbar = false,
            Title = "GitNest 配置编辑器探针",
        };
        _window = configWin;
        configWin.Show();

        Await(() => !vm.IsLoading && vm.RemotesDrafts.Count == 2 && !string.IsNullOrEmpty(vm.CurrentBranchName));

        var allTexts = configWin.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text ?? string.Empty).ToList();
        Check(allTexts.Contains("Global") && allTexts.Contains("Local"),
              "RepositoryConfigure renders both Global and Local source badges");
        Check(allTexts.All(t => !t.Contains("top_secret_ui_probe_pw", StringComparison.Ordinal)),
              "Sensitive credentials remain masked across visible TextBlocks");
        Check(vm.UrlProxyOverrides.Count == 1 && vm.UrlProxyOverrides[0].Value.Contains("***"),
              "URL-specific proxy override is listed and credential-redacted");
        Check(vm.RemotesDrafts[0].FetchRefspecCount == 2 && vm.RemotesDrafts[0].ExtraFetchUrlCount == 1,
              "Primary remote card preserves secondary URLs and custom refspecs metadata");

        Screenshot(configWin, "config-light-overview");

        // Scroll to inspect Signing & Credential Helpers before expanding Advanced section
        var scroller = configWin.FindControl<ScrollViewer>("GitConfigScroller")!;
        scroller.Offset = new Vector(0, 740);
        Pump();
        Screenshot(configWin, "config-light-signing-auth");

        // Expand Advanced & scroll to verify Signing issue banner, Credential Helper chain, SSH & CRLF warnings
        var expander = configWin.FindControl<ToggleButton>("AdvancedExpander")!;
        expander.IsChecked = true;
        Pump();
        scroller.Offset = new Vector(0, scroller.Extent.Height);
        Pump();

        allTexts = configWin.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text ?? string.Empty).ToList();
        Check(vm.SigningStatusHasIssue && allTexts.Any(t => t.Contains("missing-gpg-probe.exe", StringComparison.Ordinal)),
              "Signing diagnostic banner flags missing signing program in visible UI");
        Check(vm.CredentialHelpers.Count == 2 && vm.CredentialHelpers[0].Value.Contains("重置"),
              "Read-only credential helper chain preserves empty reset entry and order");
        Check(allTexts.Any(t => t.Contains("不会自动重新规范化", StringComparison.Ordinal)),
              "Advanced CRLF section renders clear non-renormalization notice");
        Check(!vm.SanitizedDiagnostics.Contains("top_secret_ui_probe_pw", StringComparison.Ordinal),
              "Sanitized diagnostic text excludes secrets");

        Screenshot(configWin, "config-light-signing-advanced");

        // Switch to Dark + compact narrow viewport, select Global scope to show warning + trigger shadowed save warning
        expander.IsChecked = false;
        scroller.Offset = new Vector(0, 0);
        App.SetTheme("Dark", "");
        configWin.Width = 680;
        configWin.Height = 560;
        Pump();

        var scopeCombo = configWin.FindControl<ComboBox>("ScopeSelectorCombo")!;
        scopeCombo.SelectedItem = vm.AvailableWriteScopes.Single(s => s.Scope == GitConfigWriteScope.Global);
        Await(() => vm.TargetScope == GitConfigWriteScope.Global && vm.ShowGlobalScopeWarning);
        Check(vm.UserEmailField.IsOverriddenByHigherScope,
              "Selecting Global scope flags higher-priority Local override on user.email");

        vm.UserEmailField.EditText = "global-new@example.invalid";
        ClickButtonByText(configWin, App.Text("Configure.Actions.Save"));
        Await(() => !vm.IsSaving && vm.SaveFeedback.IsVisible);
        Check(vm.SaveFeedback.IsWarning && vm.SaveFeedback.ShadowedCount >= 1,
              "Saving into Global when Local overrides reports higher-scope shadowed warning banner");
        Screenshot(configWin, "config-dark-narrow-shadow-warning");

        // Trigger partial failure (one valid field + half-specified upstream) and verify per-item feedback
        scopeCombo.SelectedItem = vm.AvailableWriteScopes.Single(s => s.Scope == GitConfigWriteScope.Local);
        Await(() => vm.TargetScope == GitConfigWriteScope.Local);
        vm.UserNameField.EditText = "局部成功作者";
        vm.SelectedUpstreamRemote = "upstream";
        vm.UpstreamTargetBranch = string.Empty;
        ClickButtonByText(configWin, App.Text("Configure.Actions.Save"));
        Await(() => !vm.IsSaving && vm.SaveFeedback.IsError && vm.SaveFeedback.SucceededCount == 1 && vm.SaveFeedback.FailedCount == 1);
        Check(Git(repoPath, "config", "--local", "user.name") == "局部成功作者",
              "Partial save persists valid changes while listing failing upstream item explicitly");
        Screenshot(configWin, "config-dark-partial-failure");

        configWin.Close();
        _window = null;

        // Scenario 4: Repository with extensions.worktreeConfig enabled
        var wtMain = Path.Combine(_root, "worktree-main");
        var wtLinked = Path.Combine(_root, "worktree-linked");
        Git(_root, "init", "-b", "main", wtMain);
        Git(wtMain, "config", "--local", "user.name", "WT Main");
        Git(wtMain, "config", "--local", "user.email", "wt-main@example.invalid");
        File.WriteAllText(Path.Combine(wtMain, "f.txt"), "base\n");
        Git(wtMain, "add", ".");
        Git(wtMain, "commit", "-m", "base");
        Git(wtMain, "config", "--local", "extensions.worktreeConfig", "true");
        Git(wtMain, "worktree", "add", "-b", "wt/config-scope", wtLinked);

        App.SetTheme("Light", "");
        var wtRepoVm = new VM.Repository(false, wtLinked, Git(wtLinked, "rev-parse", "--path-format=absolute", "--git-dir"));
        var wtCfgVm = new VM.RepositoryConfigure(wtRepoVm);
        var wtWin = new Views.RepositoryConfigure
        {
            DataContext = wtCfgVm,
            Width = 820,
            Height = 640,
            ShowInTaskbar = false,
            Title = "GitNest Worktree 作用域探针",
        };
        _window = wtWin;
        wtWin.Show();

        Await(() => !wtCfgVm.IsLoading && wtCfgVm.WorktreeConfigEnabled);
        var wtScopeCombo = wtWin.FindControl<ComboBox>("ScopeSelectorCombo")!;
        Check(wtCfgVm.AvailableWriteScopes.Any(s => s.Scope == GitConfigWriteScope.Worktree),
              "Worktree write scope is available only when extensions.worktreeConfig is enabled");

        wtScopeCombo.SelectedItem = wtCfgVm.AvailableWriteScopes.Single(s => s.Scope == GitConfigWriteScope.Worktree);
        Await(() => wtCfgVm.TargetScope == GitConfigWriteScope.Worktree);

        // Explicitly set empty "" on inherited http.proxy inside worktree scope via real UI button click
        var proxyMetaBar = wtWin.GetVisualDescendants().OfType<Views.ConfigFieldMetaBar>().Single(b => ReferenceEquals(b.DataContext, wtCfgVm.HttpProxyField));
        ClickButtonByText(proxyMetaBar, App.Text("Configure.Field.SetEmpty"));
        Await(() => wtCfgVm.HttpProxyField.PendingExplicitEmpty);

        ClickButtonByText(wtWin, App.Text("Configure.Actions.Save"));
        Await(() => !wtCfgVm.IsSaving && wtCfgVm.SaveFeedback.IsSuccess);
        Check(Git(wtLinked, "config", "--worktree", "http.proxy").Length == 0 &&
              Git(wtMain, "config", "--get", "http.proxy") == "http://global-proxy.local:8080",
              "Worktree explicit empty \"\" overrides global proxy without modifying main worktree or global config");

        Screenshot(wtWin, "config-worktree-scope");

        wtWin.Close();
        _window = null;
        File.WriteAllText(globalCfg, string.Empty);
    }

    private static void ClickButtonByText(Control root, string text)
    {
        var button = root.GetVisualDescendants()
            .OfType<Button>()
            .First(b => b.IsEffectivelyVisible && string.Equals(b.Content?.ToString(), text, StringComparison.Ordinal));
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, button));
    }
}
