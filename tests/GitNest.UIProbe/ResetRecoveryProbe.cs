using SourceGit;
using SourceGit.Commands;
using SourceGit.Models;
using VM = SourceGit.ViewModels;
using Views = SourceGit.Views;

internal static partial class Program
{
    static partial void RunResetRecovery()
    {
        var path = CreateRepository("reset-recovery");
        Git(path, "checkout", "-b", "feat/reset");
        var target = Git(path, "rev-parse", "HEAD");
        var original = CommitFile(path, "file.txt", "original work\n", "before reset");
        var journal = new OperationJournalStore(Path.Combine(_root, "reset-journal.json"));
        var preflight = HistoryResetService.InspectCommitResetAsync(path, target, probeRemote: false).GetAwaiter().GetResult();
        var outcome = HistoryResetService.ExecuteCommitResetAsync(path,
            new HistoryResetOptions { Mode = ResetCommitMode.DiscardCodeHard, Preflight = preflight }, journal).GetAwaiter().GetResult();
        if (!outcome.Succeeded)
            throw new InvalidOperationException("Reset recovery fixture failed: " + outcome.Summary);
        CommitFile(path, "new.txt", "newer work\n", "after reset");
        var (repo, _, window) = OpenRepository(path);
        var logs = new VM.ViewLogs(repo, focusRecoveryTab: true, journalStore: journal);
        var logsWindow = new Views.ViewLogs { DataContext = logs, ShowInTaskbar = false };
        try
        {
            logsWindow.Show(window);
            Await(() => !logs.IsBusy && logs.Operations.Count > 0);
            logs.SelectedOperation = logs.Operations.First(o => o.OperationId == outcome.OperationId);
            var inspection = logs.EnsureResetBackupInspectedAsync();
            Await(() => inspection.IsCompleted && !logs.IsInspectingResetBackup);
            Check(logs.IsSelectedResetBackup && logs.HasRestoreNewerCommitsWarning && !logs.CanSelectRestoreExistingBranch &&
                  logs.RestoreModeNewBranch && logs.RestoreTargetBranch.StartsWith("recover/", StringComparison.Ordinal),
                "Newer commits display a warning and select a new recovery branch");
            Screenshot(logsWindow, "reset-recovery-newer-commits");
            var branch = logs.RestoreTargetBranch;
            ClickButtonByText(logsWindow, App.Text("OperationRecovery.RestoreBackupBtn"));
            Await(() => !logs.IsBusy && logs.HasFeedbackMessage);
            Check(!logs.IsFeedbackError && Git(path, "rev-parse", branch) == original,
                "Reset recovery button restores the backup onto the displayed branch");
        }
        finally
        {
            logsWindow.Close();
        }
    }
}
