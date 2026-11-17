using Avalonia.Controls;
using Avalonia.VisualTree;
using SourceGit;
using SourceGit.Commands;
using SourceGit.Models;
using VM = SourceGit.ViewModels;

internal static partial class Program
{
    static partial void RunHistoryReset()
    {
        var path = CreateRepository("history-reset");
        var target = new QuerySingleCommit(path, "HEAD").GetResult()!;
        CommitFile(path, "file.txt", "base\nsecond\n", "second commit");
        AddOrigin(path);
        var (repo, page, window) = OpenRepository(path);
        var reset = new VM.Reset(repo, repo.CurrentBranch, target);
        page.Popup = reset;
        Await(() => !reset.IsLoadingPreflight && reset.HasPreflight);
        var modes = window.GetVisualDescendants().OfType<RadioButton>()
            .Where(r => r.GroupName == "GitNestResetMode").ToList();
        Check(modes.Count == 2 && reset.CanConfirm && reset.IsProtectionEffectivelyLocked && !reset.CanEnableAlsoRewriteRemote,
            "Reset exposes two local modes and locks remote rewrite on protected main");
        modes[1].IsChecked = true;
        Pump();
        Check(reset.SelectedMode == ResetCommitMode.DiscardCodeHard, "Hard mode radio updates the Reset model");
        modes[0].IsChecked = true;
        Pump();
        Check(reset.SelectedMode == ResetCommitMode.KeepCodeMixed, "Mixed mode radio updates the Reset model");
        Screenshot(window, "reset-modes");

        ClickButtonByText(window, reset.ConfirmButtonText);
        Await(() => page.Popup == null);
        Check(Git(path, "rev-parse", "HEAD") == target.SHA &&
              File.ReadAllText(Path.Combine(path, "file.txt")).Contains("second", StringComparison.Ordinal),
            "Reset confirmation keeps the removed commit's edits in mixed mode");
    }
}
