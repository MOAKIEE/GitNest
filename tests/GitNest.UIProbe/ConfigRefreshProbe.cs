using Avalonia.Controls;
using Avalonia.Interactivity;
using SourceGit;
using SourceGit.Models;
using VM = SourceGit.ViewModels;
using Views = SourceGit.Views;

internal static partial class Program
{
    static partial void RunConfigRefresh()
    {
        var phaseRoot = Path.Combine(_root, "config-refresh-probe");
        Directory.CreateDirectory(phaseRoot);
        var repoPath = Path.Combine(phaseRoot, "repo");
        Directory.CreateDirectory(repoPath);

        Git(repoPath, "init", "-b", "main");
        Git(repoPath, "config", "user.name", "Probe Author");
        Git(repoPath, "config", "user.email", "probe@example.invalid");
        File.WriteAllText(Path.Combine(repoPath, "README.md"), "init\n");
        Git(repoPath, "add", "README.md");
        Git(repoPath, "commit", "-m", "init");

        var globalCfg = Environment.GetEnvironmentVariable("GIT_CONFIG_GLOBAL")!;
        File.WriteAllText(globalCfg, string.Empty);

        App.SetTheme("Light", "");
        var repoVm = new VM.Repository(false, repoPath, Path.Combine(repoPath, ".git"));
        var cfgChangedEvents = 0;
        repoVm.ConfigurationStateChanged += () => cfgChangedEvents++;

        var vm = new VM.RepositoryConfigure(repoVm);
        var win = new Views.RepositoryConfigure
        {
            DataContext = vm,
            Width = 860,
            Height = 700,
            ShowInTaskbar = false,
            Title = "GitNest 3.7 配置外部变更与刷新探针",
        };
        _window = win;
        win.Show();

        Await(() => !vm.IsLoading && vm.UserNameField.EditText == "Probe Author");

        // 1. Switch to Global scope and verify GlobalScopeTargetHint renders
        var scopeCombo = win.FindControl<ComboBox>("ScopeSelectorCombo")!;
        var globalIdx = vm.AvailableWriteScopes.ToList().FindIndex(s => s.Scope == GitConfigWriteScope.Global);
        scopeCombo.SelectedIndex = globalIdx;
        Await(() => vm.ShowGlobalScopeWarning && !string.IsNullOrWhiteSpace(vm.GlobalScopeTargetHint));
        Check(vm.GlobalScopeTargetHint.Contains(Path.GetFileName(globalCfg), StringComparison.Ordinal),
              "GlobalScopeTargetHint surfaces effective global config file target clearly");
        Screenshot(win, "config-global-target-hint");

        // 2. Switch back to Local, make dirty draft, apply external Git change, trigger RefreshConfigurationState()
        var localIdx = vm.AvailableWriteScopes.ToList().FindIndex(s => s.Scope == GitConfigWriteScope.Local);
        scopeCombo.SelectedIndex = localIdx;
        Await(() => vm.TargetScope == GitConfigWriteScope.Local);

        vm.UserEmailField.EditText = "draft-user@example.invalid";
        Git(repoPath, "config", "--local", "user.name", "External Collaborator");

        repoVm.RefreshConfigurationState();
        Await(() => cfgChangedEvents > 0 && vm.HasExternalConfigChanges && vm.ExternalConfigDiffItems.Count > 0);
        var externalBanner = win.FindControl<Border>("ExternalConfigChangeBanner")!;
        Check(externalBanner.IsVisible && vm.UserEmailField.EditText == "draft-user@example.invalid",
              "ExternalConfigChangeBanner becomes visible on external change while preserving dirty draft");
        Screenshot(win, "config-external-diff-banner");

        // 3. Click KeepDraftsExternalBtn -> baseline updated, draft kept, Save writes draft cleanly
        var keepBtn = win.FindControl<Button>("KeepDraftsExternalBtn")!;
        keepBtn.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, keepBtn));
        Await(() => !vm.HasExternalConfigChanges && !externalBanner.IsVisible && vm.UserEmailField.EditText == "draft-user@example.invalid");

        ClickButtonByText(win, App.Text("Configure.Actions.Save"));
        Await(() => !vm.IsSaving && vm.SaveFeedback.IsSuccess);
        Check(Git(repoPath, "config", "--local", "--get", "user.name") == "External Collaborator" &&
              Git(repoPath, "config", "--local", "--get", "user.email") == "draft-user@example.invalid",
              "Saving after KeepDraftsExternalBtn preserves external update while persisting user draft");
        Screenshot(win, "config-keep-drafts-saved");

        // 4. Close Repository and confirm RefreshConfigurationState does not dispatch post-close
        var beforeCloseEvents = cfgChangedEvents;
        repoVm.Close();
        repoVm.RefreshConfigurationState();
        Pump();
        Pump();
        Check(cfgChangedEvents == beforeCloseEvents,
              "Repository.Close blocks subsequent RefreshConfigurationState UI notifications");

        win.Close();
        _window = null;
        File.WriteAllText(globalCfg, string.Empty);
    }
}
