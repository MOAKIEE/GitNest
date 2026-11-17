using Avalonia.Controls;
using Avalonia.VisualTree;
using SourceGit;
using SourceGit.Models;
using VM = SourceGit.ViewModels;
using Views = SourceGit.Views;

internal static partial class Program
{
    static partial void RunDiscard()
    {
        var path = CreateRepository("discard");
        File.AppendAllText(Path.Combine(path, "file.txt"), "staged\n");
        Git(path, "add", "file.txt");
        File.AppendAllText(Path.Combine(path, "file.txt"), "unstaged\n");
        var store = new DiscardBackupStore(Path.Combine(_root, "discard-backups"));
        var (repo, page, window) = OpenRepository(path);
        var discard = new VM.Discard(repo, null, null, DiscardBaselineKind.RestoreWorktreeToIndex, store, null);
        page.Popup = discard;
        Await(() => !discard.IsLoadingPreflight && discard.HasPreflight && discard.CanConfirm);
        Check(discard.TargetRows.Any(r => r.Path == "file.txt"), "Discard previews the changed file");
        var modes = window.GetVisualDescendants().OfType<RadioButton>().Where(r => r.GroupName == "DiscardBaseline").ToList();
        modes.Single(r => Equals(r.Content, App.Text("Discard.Baseline.ToHead"))).IsChecked = true;
        Await(() => discard.IsBaselineToHead && !discard.IsLoadingPreflight);
        modes.Single(r => Equals(r.Content, App.Text("Discard.Baseline.ToIndex"))).IsChecked = true;
        Await(() => discard.IsBaselineWorktreeToIndex && !discard.IsLoadingPreflight && discard.CanConfirm);
        Check(discard.IsBaselineWorktreeToIndex, "Discard baseline radio buttons update the model");
        Screenshot(window, "discard-to-index");
        ClickButtonByText(window, App.Text("Discard.Confirm.ToIndex"));
        Await(() => page.Popup == null);
        Check(File.ReadAllText(Path.Combine(path, "file.txt")) == "base\nstaged\n",
            "Discard confirmation preserves staged edits");

        var logs = new VM.ViewLogs(repo, focusDiscardBackupsTab: true, discardBackupStore: store);
        var logsWindow = new Views.ViewLogs { DataContext = logs, ShowInTaskbar = false };
        try
        {
            logsWindow.Show(window);
            Await(() => !logs.IsBusy && logs.DiscardBackups.Count == 1);
            Check(logs.IsDiscardBackupsTabActive && logs.DiscardBackups[0].Entry.Files.Any(f => f.RelativePath == "file.txt"),
                "Recovery window opens the discard backup list");
            Screenshot(logsWindow, "discard-backups");
        }
        finally
        {
            logsWindow.Close();
        }
    }
}
