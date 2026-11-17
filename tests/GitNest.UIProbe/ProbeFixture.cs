using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using SourceGit;
using VM = SourceGit.ViewModels;
using Views = SourceGit.Views;

internal static partial class Program
{
    private static string CreateRepository(string name)
    {
        var path = Path.Combine(_root, name);
        Git(_root, "init", "-b", "main", path);
        Git(path, "config", "user.name", "Probe User");
        Git(path, "config", "user.email", "probe@example.invalid");
        CommitFile(path, "file.txt", "base\n", "initial commit");
        return path;
    }

    private static string CommitFile(string path, string file, string content, string message)
    {
        File.WriteAllText(Path.Combine(path, file), content);
        Git(path, "add", "--", file);
        Git(path, "commit", "-m", message);
        return Git(path, "rev-parse", "HEAD");
    }

    private static string AddOrigin(string path)
    {
        var origin = path + "-origin.git";
        Git(_root, "init", "--bare", "-b", "main", origin);
        Git(path, "remote", "add", "origin", origin);
        Git(path, "push", "-u", "origin", "main");
        return origin;
    }

    private static (VM.Repository Repo, VM.LauncherPage Page, Window Window) OpenRepository(string path)
    {
        App.SetTheme("Light", "");
        var launcher = AttachLauncher(path);
        var page = launcher.ActivePage;
        var repo = (VM.Repository)page.Data;
        var window = _window = new Window
        {
            Content = new ContentControl
            {
                DataContext = launcher,
                [!ContentControl.ContentProperty] = new Binding("ActivePage"),
                ContentTemplate = new FuncDataTemplate<VM.LauncherPage>((_, _) => new Views.LauncherPage()),
            },
            Width = 1120,
            Height = 760,
            ShowInTaskbar = false,
            Title = "GitNest UI Probe",
        };
        window.Show();
        Await(() => repo.CurrentBranch != null);
        return (repo, page, window);
    }
}
