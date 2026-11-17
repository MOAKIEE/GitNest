using System.Collections;
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using SourceGit;
using SourceGit.Models;
using VM = SourceGit.ViewModels;
using Views = SourceGit.Views;

internal static partial class Program
{
    static partial void RunSurface()
    {
        var repoPath = Path.Combine(_root, "surface-probe");
        Git(_root, "init", "-b", "main", repoPath);
        Git(repoPath, "config", "user.name", "Probe User");
        Git(repoPath, "config", "user.email", "probe@example.invalid");
        File.WriteAllText(Path.Combine(repoPath, "README.md"), "# GitNest Surface Probe\n");
        Git(repoPath, "add", ".");
        Git(repoPath, "commit", "-m", "initial commit");
        File.AppendAllText(Path.Combine(repoPath, "README.md"), "local modification\n");
        Git(repoPath, "branch", "feat/menu-probe");
        Git(repoPath, "update-ref", "refs/remotes/origin/main", "HEAD");

        App.SetTheme("Light", "");
        App.SetLocale("zh_CN");

        var launcher = AttachLauncher(repoPath);
        var repo = (VM.Repository)launcher.ActivePage.Data;
        var repoView = new Views.Repository { DataContext = repo };
        var window = _window = new Window
        {
            Content = repoView,
            Width = 1280,
            Height = 860,
            ShowInTaskbar = false,
            Title = "GitNest UI Surface Probe",
        };
        window.Show();

        Await(() =>
            repo.Branches.Any(b => b.Name == "feat/menu-probe") &&
            repo.Branches.Any(b => !b.IsLocal) &&
            repo.Histories is { IsLoading: false, Commits.Count: >= 1 } &&
            repo.LocalChangesCount >= 1);

        // 1. Repository chrome & Command Palette omit removed features
        var toolTips = repoView.GetVisualDescendants().OfType<Control>()
            .Select(ToolTip.GetTip).Select(t => t?.ToString() ?? string.Empty).Where(s => !string.IsNullOrEmpty(s)).ToList();
        string[] forbiddenTips = ["应用补丁", "Apply Patch", "Git-Flow", "Bisect", "二分", "新建标签", "Create Tag", "添加子模块", "Add Submodule", "AI 助手", "AI Assistant"];
        Check(forbiddenTips.All(f => toolTips.All(t => !t.Contains(f, StringComparison.OrdinalIgnoreCase))),
              "Repository chrome omits pruned feature toolbar tooltips");

        var palette = new VM.RepositoryCommandPalette(repo);
        var cmds = (IEnumerable)typeof(VM.RepositoryCommandPalette).GetField("_cmds", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(palette)!;
        var labels = cmds.Cast<object>().Select(item => item.GetType().GetProperty("Label")?.GetValue(item)?.ToString() ?? string.Empty).ToList();
        Check(labels.Count > 0 && labels.All(l => !l.Contains("新建标签", StringComparison.Ordinal) && !l.Contains("应用补丁", StringComparison.Ordinal) && !l.Contains("Custom", StringComparison.OrdinalIgnoreCase)),
              "RepositoryCommandPalette omits pruned commands");

        // 2. Context menus omit Rebase / MergeMultiple / AssumeUnchanged add
        static List<string> ExtractMenuHeaders(ContextMenu menu) =>
            menu.Items.OfType<MenuItem>().Select(m => m.Header?.ToString() ?? string.Empty).ToList();

        var branchTreeView = window.GetVisualDescendants().OfType<Views.BranchTree>().First();
        var historiesView = window.GetVisualDescendants().OfType<Views.Histories>().First();
        repo.SelectedViewIndex = 1;
        Pump();
        var workingCopyView = window.GetVisualDescendants().OfType<Views.WorkingCopy>().First();
        var wcVm = (VM.WorkingCopy)workingCopyView.DataContext!;

        var localOther = repo.Branches.First(b => b.IsLocal && !b.IsCurrent);
        var remoteBranch = repo.Branches.First(b => !b.IsLocal);
        var multiBranches = repo.Branches.Where(b => b.IsLocal).Take(2).ToList();
        var branchHeaders = ExtractMenuHeaders(branchTreeView.CreateContextMenuForLocalBranch(repo, localOther))
            .Concat(ExtractMenuHeaders(branchTreeView.CreateContextMenuForRemoteBranch(repo, remoteBranch)))
            .Concat(ExtractMenuHeaders(branchTreeView.CreateContextMenuForMultipleBranches(repo, multiBranches))).ToList();
        Check(branchHeaders.All(h => !h.Contains("Rebase", StringComparison.OrdinalIgnoreCase) && !h.Contains("变基", StringComparison.Ordinal) && !h.Contains("Merge selected", StringComparison.OrdinalIgnoreCase)),
              "BranchTree context menus omit removed Rebase and MergeMultiple items");

        var commitHeaders = ExtractMenuHeaders(historiesView.CreateContextMenuForSingleCommit(repo, repo.Histories.Commits[0])).ToList();
        var unstagedHeaders = ExtractMenuHeaders(workingCopyView.CreateContextMenuForUnstagedChanges(
            repo, wcVm, new VM.ChangeSelection(new List<Change> { new Change { Path = "README.md", WorkTree = ChangeState.Modified } })));
        Check(commitHeaders.All(h => !h.Contains("Rebase", StringComparison.OrdinalIgnoreCase) && !h.Contains("变基", StringComparison.Ordinal)) &&
              unstagedHeaders.All(h => !h.Contains("Assume unchanged", StringComparison.OrdinalIgnoreCase) && !h.Contains("不跟踪此文件的更改", StringComparison.Ordinal)),
              "Histories and WorkingCopy menus omit removed Rebase and AssumeUnchanged actions");

        var prevRemovalMode = VM.Preferences.Instance.FileRemovalMode;
        try
        {
            VM.Preferences.Instance.FileRemovalMode = FileRemovalMode.RecycleBin;
            var singleUntrackedSel = new VM.ChangeSelection(new List<Change> { new Change { Path = "new.txt", WorkTree = ChangeState.Untracked } });
            var multiUntrackedSel = new VM.ChangeSelection(new List<Change>
            {
                new Change { Path = "new1.txt", WorkTree = ChangeState.Untracked },
                new Change { Path = "new2.txt", WorkTree = ChangeState.Untracked },
            });
            var mixedSel = new VM.ChangeSelection(new List<Change>
            {
                new Change { Path = "README.md", WorkTree = ChangeState.Modified },
                new Change { Path = "new1.txt", WorkTree = ChangeState.Untracked },
            });

            var recycleSingleMenu = ExtractMenuHeaders(workingCopyView.CreateContextMenuForUnstagedChanges(repo, wcVm, singleUntrackedSel));
            Check(recycleSingleMenu.Contains(App.Text("FileCM.MoveToRecycleBin")),
                  "Untracked context menu uses MoveToRecycleBin wording under RecycleBin default");

            VM.Preferences.Instance.FileRemovalMode = FileRemovalMode.Direct;
            var directSingleMenu = ExtractMenuHeaders(workingCopyView.CreateContextMenuForUnstagedChanges(repo, wcVm, singleUntrackedSel));
            var directMultiMenu = ExtractMenuHeaders(workingCopyView.CreateContextMenuForUnstagedChanges(repo, wcVm, multiUntrackedSel));
            var mixedMenu = ExtractMenuHeaders(workingCopyView.CreateContextMenuForUnstagedChanges(repo, wcVm, mixedSel));
            Check(directSingleMenu.Contains(App.Text("FileCM.DirectDeleteUntracked")) &&
                  directMultiMenu.Contains(App.Text("FileCM.DirectDeleteUntrackedMulti", 2)) &&
                  mixedMenu.Contains(App.Text("FileCM.DiscardOrRecycleMulti", 2)),
                  "WorkingCopy unstaged menus reflect Direct Delete vs mixed selection copy dynamically");
        }
        finally
        {
            VM.Preferences.Instance.FileRemovalMode = prevRemovalMode;
        }

        // 3. CommitMessageToolBox history picker (without templates / AI button)
        var toolBox = workingCopyView.GetVisualDescendants().OfType<Views.CommitMessageToolBox>().First();
        var pickerHelperBtn = toolBox.GetVisualDescendants().OfType<Button>()
            .FirstOrDefault(b => Equals(ToolTip.GetTip(b)?.ToString(), App.Text("WorkingCopy.CommitMessageHelper")));
        repo.UIStates.AddRecentCommitMessage("feat: recent probe commit");
        static List<string> ExtractPickerTexts(ContextMenu menu) =>
            menu.Items.OfType<MenuItem>().Select(item => item.Header is TextBlock tb ? tb.Text ?? string.Empty : item.Header?.ToString() ?? string.Empty).ToList();
        var pickerTexts = ExtractPickerTexts(toolBox.CreateCommitMessagePickerMenu(wcVm));
        var helperTip = ToolTip.GetTip(pickerHelperBtn!)?.ToString() ?? string.Empty;
        Check(pickerHelperBtn != null && !helperTip.Contains("模板", StringComparison.Ordinal) &&
              pickerTexts.Any(t => t.Contains("feat: recent probe commit", StringComparison.Ordinal)) &&
              pickerTexts.Any(t => t.Equals(App.Text("WorkingCopy.ClearCommitHistories"), StringComparison.Ordinal)) &&
              pickerTexts.All(t => !t.StartsWith("模板：", StringComparison.Ordinal) && !t.StartsWith("Template:", StringComparison.OrdinalIgnoreCase)),
              "CommitMessageToolBox picker preserves recent commit history without template entries");

        Screenshot(window, "surface-workingcopy-light");
        window.Close();

        // 4. Read-only AssumeUnchangedManager tip, list, and unmark restore
        Git(repoPath, "update-index", "--assume-unchanged", "README.md");
        File.AppendAllText(Path.Combine(repoPath, "README.md"), "edited while assumed\n");
        var assumeVm = new VM.AssumeUnchangedManager(repo);
        var assumeWin = _window = new Views.AssumeUnchangedManager
        {
            DataContext = assumeVm,
            ShowInTaskbar = false,
            Title = "AssumeUnchanged Probe",
        };
        assumeWin.Show();
        Await(() => assumeVm.Files.Count == 1 &&
                    assumeWin.GetVisualDescendants().OfType<Button>().Any(b => Equals(ToolTip.GetTip(b), App.Text("AssumeUnchanged.RemoveTip"))));
        var removeBtn = assumeWin.GetVisualDescendants().OfType<Button>().FirstOrDefault(b => Equals(ToolTip.GetTip(b), App.Text("AssumeUnchanged.RemoveTip")));
        Check(removeBtn != null && assumeVm.Files[0] == "README.md",
              "AssumeUnchangedManager displays marked file and remove button");
        removeBtn!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Await(() => assumeVm.Files.Count == 0);
        Check(Git(repoPath, "ls-files", "-v", "--", "README.md").TrimStart().StartsWith("H ", StringComparison.Ordinal),
              "Removing file in AssumeUnchangedManager restores Git tracking");
        assumeWin.Close();

        // 5. Preferences 5 tabs & 2 languages
        var prefWin = _window = new Views.Preferences
        {
            DataContext = VM.Preferences.Instance,
            Width = 820,
            Height = 620,
            ShowInTaskbar = false,
            Title = "Preferences Surface Probe",
        };
        prefWin.Show();
        Pump();
        var prefTabControl = prefWin.GetVisualDescendants().OfType<TabControl>().First();
        var languagePicker = prefWin.GetVisualDescendants().OfType<ComboBox>().Single(c => ReferenceEquals(c.ItemsSource, Locale.Supported));
        Check(prefTabControl.Items.Count == 5 && languagePicker.Items.Count == 2,
              $"Preferences exposes 5 tabs and 2 languages (tabs={prefTabControl.Items.Count}, langs={languagePicker.Items.Count})");

        var removalPicker = prefWin.GetVisualDescendants().OfType<ComboBox>()
            .FirstOrDefault(c => c.Items.Count == 2 &&
                                 c.Items.OfType<ComboBoxItem>().Any(i => Equals(i.Content, App.Text("Preferences.General.FileRemovalMode.Direct"))));
        Check(removalPicker != null &&
              VM.Preferences.Instance.FileRemovalMode == FileRemovalMode.RecycleBin,
              "Preferences General tab exposes FileRemovalMode selector with RecycleBin default");

        removalPicker!.SelectedIndex = 1;
        Pump();
        Check(VM.Preferences.Instance.FileRemovalMode == FileRemovalMode.Direct &&
              prefWin.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == App.Text("Preferences.General.FileRemovalMode.Note")),
              "Switching FileRemovalMode ComboBox updates Preferences immediately without restart and displays backup note");

        App.SetLocale("en_US");
        Pump();
        Check(removalPicker.Items.OfType<ComboBoxItem>().Any(i => Equals(i.Content, App.Text("Preferences.General.FileRemovalMode.Direct"))),
              "Preferences FileRemovalMode options update live on locale switch");
        App.SetLocale("zh_CN");
        removalPicker.SelectedIndex = 0;
        Pump();
        Screenshot(prefWin, "surface-preferences-light");
        prefWin.Close();

        // 6. SelfUpdate dialog (available & unavailable states)
        App.SetLocale("en_US");
        var release = new SourceGit.Models.Version { TagName = "v0.2.0", PublishedAt = DateTime.UtcNow, Body = "# GitNest 0.2.0" };
        var updateVm = new VM.SelfUpdate { Data = release };
        var updateWin = _window = new Views.SelfUpdate { DataContext = updateVm, ShowInTaskbar = false };
        updateWin.Show();
        Pump();
        Check(updateWin.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == "v0.2.0"), "GitNest update dialog displays release tag");
        Screenshot(updateWin, "surface-update-light");

        App.SetTheme("Dark", "");
        App.SetLocale("zh_CN");
        updateVm.Data = new SelfUpdateFailed(new InvalidOperationException(App.Text("SelfUpdate.Unavailable")));
        Pump();
        Check(updateWin.GetVisualDescendants().OfType<Button>().Any(b => b.Content as string == "打开 GitNest 发行页"),
              "Unavailable release offers GitNest releases page");
        Screenshot(updateWin, "surface-update-unavailable-dark");
        updateWin.Close();

        // 7. ImageSource TGA/TIFF runtime decode & Statistics window
        App.SetTheme("Light", "");
        var mediaDir = Path.Combine(_root, "retained-image-decoders");
        Directory.CreateDirectory(mediaDir);
        var tgaFile = Path.Combine(mediaDir, "sample.tga");
        var tgaBytes = new byte[18 + 2 * 2 * 3];
        tgaBytes[2] = 2; tgaBytes[12] = 2; tgaBytes[14] = 2; tgaBytes[16] = 24;
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
        Check(tgaTask.GetAwaiter().GetResult().Bitmap is { PixelSize: { Width: 2, Height: 2 } } &&
              tiffTask.GetAwaiter().GetResult().Bitmap is { PixelSize: { Width: 2, Height: 2 } },
              "ImageSource decodes TGA (Pfim) and TIFF (LibTiff) bitmaps in Avalonia runtime");

        var statsVm = new VM.Statistics(repoPath);
        var statsWin = _window = new Views.Statistics { DataContext = statsVm, ShowInTaskbar = false };
        statsWin.Show();
        Await(() => !statsVm.IsLoading && statsVm.SelectedReport != null);
        Check(statsVm.SelectedReport is { Total: >= 1 }, "Retained Statistics window loads repository commit report");
        statsWin.Close();
        _window = null;
    }
}
