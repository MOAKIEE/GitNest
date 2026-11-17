using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using SourceGit;
using VM = SourceGit.ViewModels;
using Views = SourceGit.Views;

internal static partial class Program
{
    static partial void RunHighDpi()
    {
        // 1. Verify PerMonitorV2 DPI awareness manifest is embedded in published GitNest.exe PE resource
        var repoRoot = LocateSourceRepositoryRoot();
        Check(repoRoot != null, "Located repository root for published binary inspection");
        var publishedExe = Path.Combine(repoRoot!, "artifacts", "windows-x64", "GitNest.exe");
        Check(File.Exists(publishedExe), "Published artifacts/windows-x64/GitNest.exe exists");

        var manifestXml = ReadEmbeddedWin32Manifest(publishedExe);
        Check(!string.IsNullOrWhiteSpace(manifestXml) &&
              manifestXml.Contains("PerMonitorV2", StringComparison.Ordinal) &&
              manifestXml.Contains("true/pm", StringComparison.Ordinal),
              "Published GitNest.exe PE RT_MANIFEST embeds PerMonitorV2 and true/pm DPI awareness");

        // 2. Render Repository & RepositoryConfigure & Reset surfaces across high-DPI scales (150% & 200%)
        var repoPath = Path.Combine(_root, "高DPI测试仓库");
        Git(_root, "init", "-b", "main", repoPath);
        Git(repoPath, "config", "user.name", "DPI Tester");
        Git(repoPath, "config", "user.email", "dpi@example.invalid");
        File.WriteAllText(Path.Combine(repoPath, "app.cs"), "Console.WriteLine(1);\n");
        Git(repoPath, "add", ".");
        Git(repoPath, "commit", "-m", "initial commit");
        File.WriteAllText(Path.Combine(repoPath, "app.cs"), "Console.WriteLine(2);\n");
        Git(repoPath, "add", ".");
        Git(repoPath, "commit", "-m", "second commit for high dpi graph");
        File.AppendAllText(Path.Combine(repoPath, "app.cs"), "// local edit\n");

        var wtPath = Path.Combine(_root, "工作树-高DPI");
        Git(repoPath, "worktree", "add", "-b", "feat/hidpi", wtPath);

        App.SetTheme("Light", "");
        App.SetLocale("zh_CN");

        var launcher = _launcher = new VM.Launcher(repoPath);
        typeof(App).GetField("_launcher", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(Application.Current, launcher);
        var repo = (VM.Repository)launcher.ActivePage.Data;
        repo.IsWorktreeGroupExpanded = true;

        var repoView = new Views.Repository { DataContext = repo };
        // Compact logical viewport representing 1920x1080 display at 150% DPI scaling (1280x720 DIPs)
        var repoWin = _window = new Window
        {
            Content = repoView,
            Width = 1024,
            Height = 640,
            ShowInTaskbar = false,
            Title = "GitNest High-DPI 150% Repository",
        };
        repoWin.Show();

        Await(() => repo.Worktrees.Count == 2 && repo.Histories is { IsLoading: false, Commits.Count: >= 2 } && repo.LocalChangesCount == 1);
        AssertNoClippedPrimaryButtons(repoWin, "Repository 1024x640 DIP (150% equivalent)");
        ScreenshotAtDpi(repoWin, "hidpi-150-repo-light", 1.5);

        // Dark 200% equivalent compact logical viewport (960x580 DIPs = 1920x1160 physical px at 200%)
        App.SetTheme("Dark", "");
        repoWin.Width = 960;
        repoWin.Height = 580;
        Pump();
        AssertNoClippedPrimaryButtons(repoWin, "Repository 960x580 DIP (200% equivalent)");
        ScreenshotAtDpi(repoWin, "hidpi-200-repo-dark", 2.0);
        repoWin.Close();

        // RepositoryConfigure at 150% Light DPI
        App.SetTheme("Light", "");
        var cfgVm = new VM.RepositoryConfigure(repo);
        var cfgWin = _window = new Views.RepositoryConfigure
        {
            DataContext = cfgVm,
            Width = 760,
            Height = 600,
            ShowInTaskbar = false,
            Title = "GitNest High-DPI 150% RepositoryConfigure",
        };
        cfgWin.Show();
        Await(() => !cfgVm.IsLoading);
        AssertNoClippedPrimaryButtons(cfgWin, "RepositoryConfigure 760x600 DIP (150% equivalent)");
        ScreenshotAtDpi(cfgWin, "hidpi-150-config-light", 1.5);
        cfgWin.Close();

        // DeleteBranch confirmation at 200% DPI density
        var targetBranch = repo.Branches.First(b => b.IsLocal && !b.IsCurrent);
        var delVm = new VM.DeleteBranch(repo, targetBranch);
        var delView = new Views.DeleteBranch { DataContext = delVm };
        var delHost = _window = new Window
        {
            Content = new Border
            {
                Padding = new Thickness(16),
                Width = 640,
                Child = delView,
            },
            SizeToContent = SizeToContent.WidthAndHeight,
            ShowInTaskbar = false,
            Title = "GitNest High-DPI 200% Popup",
        };
        delHost.Show();
        Await(() => !delVm.IsLoadingPreflight);
        AssertNoClippedPrimaryButtons(delHost, "DeleteBranch Popup (200% equivalent)");
        ScreenshotAtDpi(delHost, "hidpi-200-popup-light", 2.0);
        delHost.Close();

        // Preferences window & Discard Direct Delete popup at 150% Light and 200% Dark DPI
        var prevDpiRemoval = VM.Preferences.Instance.FileRemovalMode;
        try
        {
            VM.Preferences.Instance.FileRemovalMode = SourceGit.Models.FileRemovalMode.Direct;
            App.SetTheme("Light", "");
            var prefWin = _window = new Views.Preferences
            {
                DataContext = VM.Preferences.Instance,
                ShowInTaskbar = false,
                Title = "GitNest High-DPI 150% Preferences",
            };
            prefWin.Show();
            Pump();
            AssertNoClippedPrimaryButtons(prefWin, "Preferences (150% equivalent)");
            ScreenshotAtDpi(prefWin, "hidpi-150-preferences-light", 1.5);
            prefWin.Close();

            var hidpiUntracked = Path.Combine(repoPath, "hidpi-untracked.txt");
            File.WriteAllText(hidpiUntracked, "high dpi direct delete preview\n");
            App.SetTheme("Dark", "");
            var discardDpiVm = new VM.Discard(
                repo,
                [new SourceGit.Models.Change { Path = "hidpi-untracked.txt", Index = SourceGit.Models.ChangeState.Untracked, WorkTree = SourceGit.Models.ChangeState.Untracked }],
                null,
                SourceGit.Models.DiscardBaselineKind.MoveUntrackedToRecycleBin);
            var discardDpiView = new Views.Discard { DataContext = discardDpiVm };
            var discardHost = _window = new Window
            {
                Content = new Border
                {
                    Padding = new Thickness(16),
                    Width = 640,
                    Child = discardDpiView,
                },
                SizeToContent = SizeToContent.WidthAndHeight,
                ShowInTaskbar = false,
                Title = "GitNest High-DPI 200% Discard Direct",
            };
            discardHost.Show();
            Await(() => !discardDpiVm.IsLoadingPreflight && discardDpiVm.HasPreflight);
            Pump();
            Check(discardHost.GetVisualDescendants().OfType<RadioButton>().Any(r =>
                      r.GroupName == "DiscardRemovalMode" &&
                      r.IsEffectivelyVisible &&
                      r.IsChecked == true),
                  "High-DPI Discard Direct popup renders visible removal mode selector");
            AssertNoClippedPrimaryButtons(discardHost, "Discard Direct Popup (200% equivalent)");
            ScreenshotAtDpi(discardHost, "hidpi-200-discard-direct-dark", 2.0);
            discardHost.Close();
        }
        finally
        {
            App.SetTheme("Light", "");
            VM.Preferences.Instance.FileRemovalMode = prevDpiRemoval;
            _window = null;
        }
    }

    private static void AssertNoClippedPrimaryButtons(Window window, string stage)
    {
        window.UpdateLayout();
        var invalidButtons = window.GetVisualDescendants()
            .OfType<Button>()
            .Where(b => b.IsVisible && b.Bounds.Width > 0 && b.Bounds.Height > 0 &&
                        (b.Bounds.Right > window.Bounds.Width + 2 || b.Bounds.Bottom > window.Bounds.Height + 2))
            .ToList();
        Check(invalidButtons.Count == 0, $"{stage} has zero overflowing/clipped visible buttons inside viewport");
    }

    private static void ScreenshotAtDpi(Window window, string name, double scale)
    {
        if (!_screenshots)
            return;
        Pump();
        window.UpdateLayout();
        var widthPx = Math.Max(1, (int)Math.Round(window.Bounds.Width * scale));
        var heightPx = Math.Max(1, (int)Math.Round(window.Bounds.Height * scale));
        var dpi = new Vector(96.0 * scale, 96.0 * scale);
        using var bitmap = new RenderTargetBitmap(new PixelSize(widthPx, heightPx), dpi);
        bitmap.Render(window);
        bitmap.Save(Path.Combine(_output, name + ".png"));
    }

    private static string ReadEmbeddedWin32Manifest(string exePath)
    {
        const uint LOAD_LIBRARY_AS_DATAFILE = 0x00000002;
        const int RT_MANIFEST = 24;
        var hModule = LoadLibraryExW(exePath, IntPtr.Zero, LOAD_LIBRARY_AS_DATAFILE);
        if (hModule == IntPtr.Zero)
            return string.Empty;

        try
        {
            foreach (var id in new[] { 1, 2 })
            {
                var hRes = FindResourceW(hModule, new IntPtr(id), new IntPtr(RT_MANIFEST));
                if (hRes == IntPtr.Zero)
                    continue;
                var hData = LoadResource(hModule, hRes);
                var ptr = LockResource(hData);
                var size = SizeofResource(hModule, hRes);
                if (ptr != IntPtr.Zero && size > 0)
                {
                    var bytes = new byte[size];
                    Marshal.Copy(ptr, bytes, 0, (int)size);
                    return Encoding.UTF8.GetString(bytes);
                }
            }
            return string.Empty;
        }
        finally
        {
            FreeLibrary(hModule);
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadLibraryExW(string lpFileName, IntPtr hFile, uint dwFlags);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr FindResourceW(IntPtr hModule, IntPtr lpName, IntPtr lpType);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr LoadResource(IntPtr hModule, IntPtr hResInfo);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LockResource(IntPtr hResData);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint SizeofResource(IntPtr hModule, IntPtr hResInfo);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool FreeLibrary(IntPtr hModule);
}
