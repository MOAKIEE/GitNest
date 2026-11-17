using Avalonia.Controls;
using Avalonia.VisualTree;
using SourceGit;
using SourceGit.Models;
using VM = SourceGit.ViewModels;
using Views = SourceGit.Views;

internal static partial class Program
{
    static partial void RunConfigWorktree()
    {
        var globalCfg = Environment.GetEnvironmentVariable("GIT_CONFIG_GLOBAL")!;
        File.WriteAllText(globalCfg, "[http]\n\tproxy = http://global-proxy.local:8080\n");
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

        wtRepoVm.Close();
    }
}
