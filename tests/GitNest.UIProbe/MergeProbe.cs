using Avalonia.Controls;
using Avalonia.VisualTree;
using SourceGit;
using VM = SourceGit.ViewModels;

internal static partial class Program
{
    static partial void RunMerge()
    {
        var path = CreateRepository("merge");
        Git(path, "checkout", "-b", "feat/clean");
        CommitFile(path, "feature.txt", "feature\n", "feature commit");
        Git(path, "checkout", "main");
        CommitFile(path, "file.txt", "main edit\n", "main commit");
        var (repo, page, window) = OpenRepository(path);
        Await(() => repo.Branches.Any(b => b.IsLocal && b.Name == "feat/clean"));
        var feature = repo.Branches.First(b => b.IsLocal && b.Name == "feat/clean");
        var merge = new VM.Merge(repo, feature, "main", false);
        page.Popup = merge;
        Await(() => !merge.IsLoadingPreflight && merge.HasPreflight && merge.TestingState == VM.MergeTestingState.NoConflicts);
        Check(merge.CanConfirm && merge.IncomingChanges.Any(f => f.Path == "feature.txt") &&
              window.GetVisualDescendants().OfType<TextBlock>()
                  .Any(t => t.IsEffectivelyVisible && (t.Text?.Contains("将【feat/clean】的改动合并到【main】", StringComparison.Ordinal) ?? false)),
            "Merge popup renders source-to-target direction and incoming files");
        Screenshot(window, "merge-preview");
        ClickButtonByText(window, App.Text("Sure"));
        Await(() => page.Popup == null);
        Check(File.Exists(Path.Combine(path, "feature.txt")), "Merge confirmation executes the selected merge");

        Git(path, "checkout", "-b", "feat/conflict", "HEAD~1");
        CommitFile(path, "file.txt", "conflicting edit\n", "conflict");
        Git(path, "checkout", "main");
        CommitFile(path, "file.txt", "another main edit\n", "advance main");
        repo.RefreshBranches();
        Await(() => repo.Branches.Any(b => b.Name == "feat/conflict"));
        var conflict = new VM.Merge(repo, repo.Branches.First(b => b.Name == "feat/conflict"), "main", false);
        page.Popup = conflict;
        Await(() => !conflict.IsLoadingPreflight && conflict.HasPreflight);
        ClickButtonByText(window, App.Text("Sure"));
        Await(() => conflict.HasResultView && !conflict.ShowConfirmButton);
        Check(page.Popup == conflict && conflict.ResultLines.Contains("file.txt", StringComparer.Ordinal) &&
              !File.Exists(Path.Combine(path, ".git", "MERGE_HEAD")),
            "Aborted conflict keeps the result card open with the conflicting path");
        Screenshot(window, "merge-conflict-result");
        ClickButtonByText(window, App.Text("Merge.CloseResult"));
        Await(() => page.Popup == null);
    }
}
