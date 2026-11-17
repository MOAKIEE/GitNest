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

        // Mixed selection baseline separation + live Preferences isolation + Direct Delete UI flow
        var prevMode = VM.Preferences.Instance.FileRemovalMode;
        try
        {
            VM.Preferences.Instance.FileRemovalMode = FileRemovalMode.RecycleBin;
            File.AppendAllText(Path.Combine(path, "file.txt"), "mod2\n");
            File.WriteAllText(Path.Combine(path, "untracked-probe.txt"), "untracked probe payload\n");
            var stagedNew = Path.Combine(path, "staged-new.txt");
            File.WriteAllText(stagedNew, "staged new payload\n");
            Git(path, "add", "staged-new.txt");

            var trackedMod = new Change { Path = "file.txt", Index = ChangeState.None, WorkTree = ChangeState.Modified };
            var untrackedProbe = new Change { Path = "untracked-probe.txt", Index = ChangeState.Untracked, WorkTree = ChangeState.Untracked };
            var stagedAdded = new Change { Path = "staged-new.txt", Index = ChangeState.Added, WorkTree = ChangeState.None };

            var mixedPopup = new VM.Discard(repo, [trackedMod, untrackedProbe], null, DiscardBaselineKind.RestoreWorktreeToIndex, store, null);
            page.Popup = mixedPopup;
            Await(() => !mixedPopup.IsLoadingPreflight && mixedPopup.HasPreflight);
            Check(!mixedPopup.ShowRemovalModeSelector &&
                  mixedPopup.TargetRows.Any(r => r.Path == "file.txt") &&
                  mixedPopup.TargetRows.All(r => r.Path != "untracked-probe.txt"),
                  "RestoreWorktreeToIndex hides removal mode selector and shows only tracked targets in mixed selection");

            var baselineRadios = window.GetVisualDescendants().OfType<RadioButton>().Where(r => r.GroupName == "DiscardBaseline").ToList();
            baselineRadios.Single(r => Equals(r.Content, App.Text("Discard.Baseline.CleanUntracked"))).IsChecked = true;
            Await(() => mixedPopup.IsBaselineRecycleUntracked && !mixedPopup.IsLoadingPreflight);
            Check(mixedPopup.ShowRemovalModeSelector &&
                  mixedPopup.SelectedRemovalMode == FileRemovalMode.RecycleBin &&
                  mixedPopup.TargetRows.Any(r => r.Path == "untracked-probe.txt") &&
                  mixedPopup.TargetRows.All(r => r.Path != "file.txt"),
                  "CleanUntracked shows removal mode selector and lists only untracked targets in mixed selection");

            // Changing Preferences while dialog is open keeps dialog's existing selection intact
            VM.Preferences.Instance.FileRemovalMode = FileRemovalMode.Direct;
            Pump();
            Check(mixedPopup.SelectedRemovalMode == FileRemovalMode.RecycleBin,
                  "Changing Preferences default removal mode does not alter already-open Discard dialog");
            page.Popup = null;
            Pump();

            // Newly opened dialog reads new Preferences default (Direct) and updates title/warning/confirm button
            var directPopup = new VM.Discard(repo, [untrackedProbe], null, DiscardBaselineKind.MoveUntrackedToRecycleBin, store, null);
            page.Popup = directPopup;
            Await(() => !directPopup.IsLoadingPreflight && directPopup.HasPreflight && directPopup.CanConfirm);
            Pump();
            Check(directPopup.SelectedRemovalMode == FileRemovalMode.Direct &&
                  directPopup.ShowRemovalModeSelector &&
                  directPopup.DialogTitle == App.Text("Discard.Title.DirectDelete") &&
                  directPopup.ConfirmButtonText == App.Text("Discard.Confirm.DirectDelete") &&
                  window.GetVisualDescendants().OfType<RadioButton>().Any(r =>
                      r.GroupName == "DiscardRemovalMode" &&
                      r.IsEffectivelyVisible &&
                      r.IsChecked == true &&
                      Equals(r.Content, App.Text("Preferences.General.FileRemovalMode.Direct"))) &&
                  window.GetVisualDescendants().OfType<TextBlock>().Any(t => t.IsEffectivelyVisible && t.Text == App.Text("Discard.DirectRemovalWarning")),
                  "Newly opened Discard dialog inherits Direct mode from Preferences and displays direct deletion warning");
            Screenshot(window, "discard-direct-delete");

            ClickButtonByText(window, App.Text("Discard.Confirm.DirectDelete"));
            Await(() => !directPopup.InProgress);
            Check(page.Popup == null,
                  $"Direct Delete closes the dialog [resultView={directPopup.HasResultView}, loadError='{directPopup.LoadErrorMessage}', summary='{directPopup.ResultSummary}', canConfirm={directPopup.CanConfirm}]");
            Check(!File.Exists(Path.Combine(path, "untracked-probe.txt")),
                  "Confirming Direct Delete in Discard dialog deletes untracked file");

            // RestoreToHead on IndexAddedOnly shows RemovalMode selector
            var headPopup = new VM.Discard(repo, [stagedAdded], null, DiscardBaselineKind.RestoreToHead, store, null);
            page.Popup = headPopup;
            Await(() => !headPopup.IsLoadingPreflight && headPopup.HasPreflight && headPopup.CanConfirm);
            Check(headPopup.ShowRemovalModeSelector && headPopup.IncludesFileRemoval,
                  "RestoreToHead with IndexAddedOnly file displays removal mode selector");
            page.Popup = null;
            Pump();

            // Partial recycle failure stays in result view and allows switching to Direct for remaining items only
            VM.Preferences.Instance.FileRemovalMode = FileRemovalMode.RecycleBin;
            var okRetryName = "retry-ok.txt";
            var failRetryName = "retry-fail.txt";
            File.WriteAllText(Path.Combine(path, okRetryName), "ok\n");
            File.WriteAllText(Path.Combine(path, failRetryName), "fail-once\n");
            var okItem = new Change { Path = okRetryName, Index = ChangeState.Untracked, WorkTree = ChangeState.Untracked };
            var failItem = new Change { Path = failRetryName, Index = ChangeState.Untracked, WorkTree = ChangeState.Untracked };
            var retryPopup = new VM.Discard(
                repo,
                [okItem, failItem],
                null,
                DiscardBaselineKind.MoveUntrackedToRecycleBin,
                store,
                targetAbs =>
                {
                    if (Path.GetFileName(targetAbs) == failRetryName)
                        return (false, "Simulated recycle bin denial");
                    File.Delete(targetAbs);
                    return (true, string.Empty);
                });
            page.Popup = retryPopup;
            Await(() => !retryPopup.IsLoadingPreflight && retryPopup.HasPreflight && retryPopup.CanConfirm);
            ClickButtonByText(window, App.Text("Discard.Confirm.Recycle"));
            Await(() => retryPopup.HasResultView);
            Check(retryPopup.HasRetryableFailures &&
                  !File.Exists(Path.Combine(path, okRetryName)) &&
                  File.Exists(Path.Combine(path, failRetryName)) &&
                  retryPopup.ResultLines.Any(l => l.Contains("[已移入回收站]", StringComparison.Ordinal) && l.Contains(okRetryName, StringComparison.Ordinal)) &&
                  retryPopup.ResultLines.Any(l => l.Contains("[失败]", StringComparison.Ordinal) && l.Contains(failRetryName, StringComparison.Ordinal) && l.Contains("Simulated recycle bin denial", StringComparison.Ordinal)),
                  "Partial recycle failure keeps dialog open with non-double-counted result lines and failure reason");
            Screenshot(window, "discard-retry");

            var retryRadios = window.GetVisualDescendants().OfType<RadioButton>().Where(r => r.GroupName == "DiscardRetryRemovalMode").ToList();
            retryRadios.Single(r => Equals(r.Content, App.Text("Preferences.General.FileRemovalMode.Direct"))).IsChecked = true;
            Pump();
            Check(retryPopup.ConfirmButtonText == App.Text("Discard.Confirm.RetryDirectDelete"),
                  "Switching failed item removal mode to Direct updates retry confirm button");
            ClickButtonByText(window, App.Text("Discard.Confirm.RetryDirectDelete"));
            Await(() => page.Popup == null);
            Check(!File.Exists(Path.Combine(path, failRetryName)),
                  "Retrying remaining failed file with Direct Delete deletes it cleanly and closes dialog");

            // DiscardBackups view records actual Direct vs RecycleBin removal mode
            var logs2 = new VM.ViewLogs(repo, focusDiscardBackupsTab: true, discardBackupStore: store);
            var logsWin2 = new Views.ViewLogs { DataContext = logs2, ShowInTaskbar = false };
            try
            {
                logsWin2.Show(window);
                Await(() => !logs2.IsBusy && logs2.DiscardBackups.Count >= 3);
                var directItem = logs2.DiscardBackups.FirstOrDefault(b => b.BaselineLabel == App.Text("Discard.BaselineLabel.DirectDelete"));
                Check(directItem != null, "ViewLogs.DiscardBackups displays Direct Delete removal mode badge");
                logs2.SelectedDiscardBackup = directItem;
                Await(() => logs2.SelectedDiscardBackupFiles.Any(f => f.StateLabel.Contains("直接删除", StringComparison.Ordinal)));
                Check(logs2.SelectedDiscardBackupFiles.Any(f => f.StateLabel.Contains("直接删除", StringComparison.Ordinal)),
                      "ViewLogs.DiscardBackups file detail row displays direct deletion state");
                Screenshot(logsWin2, "discard-backups-direct");
            }
            finally
            {
                logsWin2.Close();
            }
        }
        finally
        {
            VM.Preferences.Instance.FileRemovalMode = prevMode;
        }
    }
}
