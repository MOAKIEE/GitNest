using Avalonia.Controls;
using Avalonia.VisualTree;
using SourceGit;
using VM = SourceGit.ViewModels;
using Views = SourceGit.Views;

internal static partial class Program
{
    static partial void RunConflict()
    {
        var path = CreateRepository("conflict");
        Git(path, "checkout", "-b", "side");
        CommitFile(path, "file.txt", "side\n", "side commit");
        Git(path, "checkout", "main");
        CommitFile(path, "file.txt", "main\n", "main commit");
        Check(RunGitAllowFailure(path, "merge", "side").ExitCode != 0, "Fixture creates an external merge conflict");
        var (repo, _, window) = OpenRepository(path);
        repo.SelectedViewIndex = 1;
        Await(() => repo.HasInProgressBanner && window.GetVisualDescendants().OfType<Views.Conflict>().Any(c => c.IsEffectivelyVisible));
        var view = window.GetVisualDescendants().OfType<Views.Conflict>().First(c => c.IsEffectivelyVisible);
        var vm = (VM.Conflict)view.DataContext!;
        Await(() => vm.IsExternalOrigin && vm.ConflictedFiles.Any(f => f.Path == "file.txt"));
        string[] forbidden = ["WorkingCopy.Conflicts.UseTheirs", "WorkingCopy.Conflicts.UseMine", "WorkingCopy.Conflicts.Merge"];
        Check(!repo.CanAbortInProgressFromBanner && vm.ShowExternalAbortDisabledNotice &&
              view.GetVisualDescendants().OfType<Button>()
                  .All(b => !b.IsEffectivelyVisible || !forbidden.Any(k => Equals(b.Content, App.Text(k)))),
            "External conflict panel displays files with read-only controls");
        ClickButtonByText(view, App.Text("Repository.RefreshStatus"));
        Await(() => !vm.IsRefreshing && vm.Mine != null && vm.Theirs != null);
        Check(File.Exists(Path.Combine(path, ".git", "MERGE_HEAD")), "Conflict Refresh preserves the external operation");
        Screenshot(window, "conflict-readonly");
    }
}
