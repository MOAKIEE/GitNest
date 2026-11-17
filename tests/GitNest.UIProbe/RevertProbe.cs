using Avalonia.Controls;
using Avalonia.VisualTree;
using SourceGit;
using SourceGit.Commands;
using VM = SourceGit.ViewModels;

internal static partial class Program
{
    static partial void RunRevert()
    {
        var path = CreateRepository("revert");
        Git(path, "branch", "side");
        var sha = CommitFile(path, "file.txt", "changed\n", "change");
        var commit = new QuerySingleCommit(path, sha).GetResult()!;
        var (repo, page, window) = OpenRepository(path);
        var revert = new VM.Revert(repo, commit);
        page.Popup = revert;
        Await(() => !revert.IsLoadingPreflight && revert.HasPreflight);
        Check(revert.CanConfirm && revert.AffectedFiles.Any(f => f.Path == "file.txt"),
            "Revert popup previews the selected commit's files");
        Screenshot(window, "revert-preview");
        ClickButtonByText(window, App.Text("Sure"));
        Await(() => page.Popup == null);
        Check(File.ReadAllText(Path.Combine(path, "file.txt")) == "base\n", "Revert confirmation restores the file");

        Git(path, "checkout", "side");
        CommitFile(path, "side.txt", "side\n", "side commit");
        Git(path, "checkout", "main");
        Git(path, "merge", "--no-ff", "--no-edit", "side");
        var merged = new QuerySingleCommit(path, "HEAD").GetResult()!;
        var blocked = new VM.Revert(repo, merged);
        page.Popup = blocked;
        Await(() => !blocked.IsLoadingPreflight && blocked.HasPreflight);
        Check(blocked.IsMergeCommitBlock && !blocked.CanConfirm && window.GetVisualDescendants().OfType<TextBlock>()
                .Any(t => t.IsEffectivelyVisible && (t.Text?.Contains("-m 1", StringComparison.Ordinal) ?? false)),
            "Merge-commit Revert renders the mainline block");
        Screenshot(window, "revert-merge-blocked");
    }
}
