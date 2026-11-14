using System.Diagnostics;
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using SourceGit;
using SourceGit.Models;
using VM = SourceGit.ViewModels;
using Views = SourceGit.Views;
using OS = SourceGit.Native.OS;

internal static partial class Program
{
    private static string _root, _output;
    private static int _checks;
    private static readonly List<string> _results = [];
    private static VM.Launcher _launcher;
    private static Window _window;

    [STAThread]
    public static int Main(string[] args)
    {
        if (Environment.GetEnvironmentVariable("GITNEST_PROBE_TOOL_OUTPUT") is { } capture)
        {
            File.WriteAllText(capture, Environment.CurrentDirectory + "\n" + string.Join("\n", args));
            return 0;
        }
        _output = Path.GetFullPath(args.Length > 0 ? args[0] : "artifacts/probes/phase-2.2");
        Directory.CreateDirectory(_output);
        _root = Path.Combine(Path.GetTempPath(), "GitNest-probe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        var originalGlobal = Environment.GetEnvironmentVariable("GIT_CONFIG_GLOBAL");
        var originalSystem = Environment.GetEnvironmentVariable("GIT_CONFIG_NOSYSTEM");
        try
        {
            OS.BasicDirectories.ConfigDir = Path.Combine(_root, "config");
            OS.BasicDirectories.CacheDir = Path.Combine(_root, "cache");
            Directory.CreateDirectory(OS.BasicDirectories.ConfigDir);
            Directory.CreateDirectory(OS.BasicDirectories.CacheDir);
            var global = Path.Combine(_root, "global.gitconfig");
            File.WriteAllText(global, "");
            Environment.SetEnvironmentVariable("GIT_CONFIG_GLOBAL", global);
            Environment.SetEnvironmentVariable("GIT_CONFIG_NOSYSTEM", "1");
            OS.GitExecutable = OS.FindGitExecutable();
            App.BuildAvaloniaApp().SetupWithoutStarting();
            App.SetLocale("zh_CN");
            OS.SetupExternalTools();
            if (args.Contains("--config"))
            {
                RunConfig();
            }
            else if (args.Contains("--commit-sync"))
            {
                RunCommitSync();
            }
            else
            {
                RunOverview();
                if (args.Contains("--comparison")) RunComparison();
            }
            _results.Add($"Passed {_checks} probe checks.");
            return 0;
        }
        catch (Exception ex)
        {
            _results.Add(ex.ToString());
            return 1;
        }
        finally
        {
            CloseFixture();
            Environment.SetEnvironmentVariable("GIT_CONFIG_GLOBAL", originalGlobal);
            Environment.SetEnvironmentVariable("GIT_CONFIG_NOSYSTEM", originalSystem);
            File.WriteAllLines(Path.Combine(_output, "results.txt"), _results);
            Console.WriteLine(_results[^1]);
            if (Directory.Exists(_root))
            {
                var temporaryRoot = Path.GetFullPath(_root);
                var expectedPrefix = Path.Combine(Path.GetTempPath(), "GitNest-probe-");
                if (!temporaryRoot.StartsWith(expectedPrefix, StringComparison.OrdinalIgnoreCase) ||
                    !Guid.TryParseExact(temporaryRoot[expectedPrefix.Length..], "N", out _))
                    throw new InvalidOperationException("Refusing to recycle a directory outside the probe fixture.");
                foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
                    File.SetAttributes(file, FileAttributes.Normal);
                Microsoft.VisualBasic.FileIO.FileSystem.DeleteDirectory(_root,
                    Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                    Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
            }
        }
    }

    private static void RunOverview()
    {
        var repoPath = Path.Combine(_root, "主仓库");
        Git(_root, "init", "-b", "main", repoPath);
        Git(repoPath, "config", "user.name", "Probe");
        Git(repoPath, "config", "user.email", "probe@example.invalid");
        File.WriteAllText(Path.Combine(repoPath, "file.txt"), "base\n");
        Git(repoPath, "add", ".");
        Git(repoPath, "commit", "-m", "base");
        var taskPath = Path.Combine(_root, "中文 任务 & #2");
        Git(repoPath, "worktree", "add", "-b", "task/probe", taskPath);
        File.AppendAllText(Path.Combine(taskPath, "file.txt"), "staged\n");
        Git(taskPath, "add", ".");
        File.AppendAllText(Path.Combine(taskPath, "file.txt"), "unstaged\n");
        File.WriteAllText(Path.Combine(taskPath, "新文件.txt"), "new\n");
        var launcher = _launcher = new VM.Launcher(repoPath);
        typeof(App).GetField("_launcher", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(Application.Current, launcher);
        var repo = (VM.Repository)launcher.ActivePage.Data;
        repo.IsWorktreeGroupExpanded = true;
        var view = new Views.Repository { DataContext = repo };
        var window = _window = new Window { Content = view, Width = 1280, Height = 900, ShowInTaskbar = false, Title = "GitNest 验证探针" };
        window.Show();
        Await(() => repo.Worktrees.Count == 2 && repo.Worktrees.All(t => t.Status?.IsReadable == true));
        var node = repo.Worktrees.Single(t => WorktreeTaskStore.PathsEqual(t.FullPath, taskPath));
        Check(node.Status.Staged == 1 && node.Status.Unstaged == 1 && node.Status.Untracked == 1, "Separate staged, unstaged and untracked counts");

        var edit = new VM.EditWorktreeTask(repo, node);
        var editView = new Views.EditWorktreeTask { DataContext = edit };
        window.Content = new Border { Child = editView, Width = 600, Padding = new Thickness(16), VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top };
        Pump();
        editView.GetVisualDescendants().OfType<TextBox>().Single().Text = "  探针保存的任务  ";
        editView.GetVisualDescendants().OfType<CheckBox>().Single().IsChecked = true;
        Pump();
        Check(edit.Alias.Trim() == "探针保存的任务" && edit.IsCompleted, "Editor bindings update the task model");
        Check(edit.Sure().GetAwaiter().GetResult(), "Save through the production popup action");
        var reloaded = new WorktreeTaskStore(Path.Combine(OS.BasicDirectories.ConfigDir, "worktree-tasks.json")).Get(Path.Combine(repoPath, ".git"), taskPath);
        Check(reloaded.Alias == "探针保存的任务" && reloaded.IsCompleted, "New store reloads the saved alias and completion flag");
        Screenshot(window, "overview-edit-light");
        window.Content = view;
        Await(() => repo.Worktrees.Any(t => t.Name == "探针保存的任务"));
        repo.SelectedViewIndex = 0;
        var changes = view.GetVisualDescendants().OfType<Button>().Single(b => b.Content?.ToString() == App.Text("Repository.ViewChanges"));
        changes.Command!.Execute(changes.CommandParameter);
        Check(repo.SelectedViewIndex == 1, "Bound show-changes button opens the working copy");
        Screenshot(window, "overview-light");
        repo.SelectedViewIndex = 0;
        App.SetTheme("Dark", "");
        window.Width = 1060;
        window.Height = 700;
        Screenshot(window, "overview-dark-narrow");

        node = repo.Worktrees.Single(t => WorktreeTaskStore.PathsEqual(t.FullPath, taskPath));
        repo.IsWorktreeGroupExpanded = true;
        Pump();
        view.FindControl<ListBox>("WorktreeList")!.ScrollIntoView(node);
        Pump();
        var item = view.GetVisualDescendants().OfType<Border>().First(b => b.DataContext == node && b.Height == 128);
        item.RaiseEvent(new TappedEventArgs(InputElement.DoubleTappedEvent, null!));
        Await(() => launcher.ActivePage.Data is VM.Repository active && WorktreeTaskStore.PathsEqual(active.FullPath, taskPath));
        var taskRepo = (VM.Repository)launcher.ActivePage.Data;
        view.DataContext = taskRepo;
        Await(() => taskRepo.Worktrees.Any(t => t.IsCurrent && t.Status?.Staged == 1));
        Check(taskRepo.FullPath == taskPath.Replace('\\', '/'), "Worktree double-tap opens the exact Unicode path");
        Screenshot(window, "overview-open-worktree");
        File.AppendAllText(Path.Combine(taskPath, "file.txt"), "external\n");
        var before = taskRepo.Worktrees.Single(t => t.IsCurrent).Status;
        Await(() => taskRepo.Worktrees.Single(t => t.IsCurrent).Status != before);
        Check(taskRepo.Worktrees.Single(t => t.IsCurrent).Status.Unstaged == 1, "File watcher refreshes external working-copy changes");
        Git(taskPath, "add", ".");
        Await(() => taskRepo.Worktrees.Single(t => t.IsCurrent).Status is { Staged: 2, Unstaged: 0, Untracked: 0 });
        Check(true, "Index-only change refreshes the summary");
        File.WriteAllText(Path.Combine(repoPath, "external.txt"), "other worktree\n");
        taskRepo.RefreshTaskOverview();
        Await(() => taskRepo.Worktrees.Any(t => !t.IsCurrent && t.Status?.Untracked == 1));
        Check(true, "Explicit refresh reads the other worktree");
        var output = Path.Combine(_root, "tool-output.txt");
        Environment.SetEnvironmentVariable("GITNEST_PROBE_TOOL_OUTPUT", output);
        using (var tool = Process.Start(WorktreeToolLauncher.CreateEditorStartInfo(Environment.ProcessPath!, taskPath)))
            tool!.WaitForExit();
        var toolLines = File.ReadAllLines(output);
        Check(WorktreeTaskStore.PathsEqual(toolLines[0], taskPath) && toolLines[1] == taskPath, "Editor receives one path argument and the exact working directory");
        File.Delete(output);
        VM.Preferences.Instance.ShellOrTerminalPath = Environment.ProcessPath!;
        VM.Preferences.Instance.ShellOrTerminalArgs = "";
        OS.OpenTerminal(taskPath);
        Await(() => File.Exists(output) && new FileInfo(output).Length > 0);
        Check(WorktreeTaskStore.PathsEqual(File.ReadAllLines(output)[0], taskPath), "Terminal starts in the selected worktree");
        Environment.SetEnvironmentVariable("GITNEST_PROBE_TOOL_OUTPUT", null);
        CloseFixture();
    }

    private static void CloseFixture()
    {
        var window = _window;
        var launcher = _launcher;
        _window = null;
        _launcher = null;
        window?.Close();
        if (launcher != null)
            foreach (var page in launcher.Pages)
                if (page.Data is VM.Repository open) open.Close();
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
        _checks++;
        _results.Add("PASS " + message);
        Console.WriteLine("PASS " + message);
    }

    private static void Pump()
    {
        Dispatcher.UIThread.RunJobs();
        Thread.Sleep(50);
        Dispatcher.UIThread.RunJobs();
    }

    private static void Await(Func<bool> condition)
    {
        var timer = Stopwatch.StartNew();
        while (!condition())
        {
            if (timer.Elapsed > TimeSpan.FromSeconds(20)) throw new TimeoutException("Probe condition timed out.");
            Pump();
        }
        Pump();
    }

    private static void Screenshot(Window window, string name)
    {
        Pump();
        window.UpdateLayout();
        using var bitmap = new RenderTargetBitmap(new PixelSize((int)window.Bounds.Width, (int)window.Bounds.Height));
        bitmap.Render(window);
        bitmap.Save(Path.Combine(_output, name + ".png"));
    }

    private static string Git(string directory, params string[] args)
    {
        var info = new ProcessStartInfo(OS.GitExecutable) { WorkingDirectory = directory, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true, UseShellExecute = false };
        foreach (var arg in args) info.ArgumentList.Add(arg);
        using var process = Process.Start(info)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        if (process.ExitCode != 0) throw new Exception(error.GetAwaiter().GetResult());
        return output.GetAwaiter().GetResult().TrimEnd('\r', '\n');
    }

    static partial void RunComparison();
    static partial void RunConfig();
    static partial void RunCommitSync();

}
