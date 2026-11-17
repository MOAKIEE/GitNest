using Avalonia.Controls;
using Avalonia.VisualTree;
using SourceGit;

internal static partial class Program
{
    static partial void RunCommit()
    {
        var path = CreateRepository("commit");
        Git(path, "config", "--unset", "user.name");
        Git(path, "config", "--unset", "user.email");
        File.AppendAllText(Path.Combine(path, "file.txt"), "staged\n");
        Git(path, "add", "file.txt");
        File.AppendAllText(Path.Combine(path, "file.txt"), "unstaged\n");

        var (repo, _, window) = OpenRepository(path);
        repo.SelectedViewIndex = 1;
        Await(() => repo.WorkingCopy is { Staged.Count: 1, Unstaged.Count: 1, LastCapturedSnapshot: not null });
        var wc = repo.WorkingCopy;
        Check(wc.HasPartialStagedFiles &&
              window.GetVisualDescendants().OfType<TextBlock>()
                  .Any(t => t.IsEffectivelyVisible && t.Text == App.Text("WorkingCopy.PartialCommitHint")),
            "WorkingCopy renders partial-staging hint");

        wc.CommitMessage = "partial staged commit";
        ClickButtonByText(window, App.Text("WorkingCopy.Commit"));
        Await(() => wc.ShowIdentityPrompt);
        Check(window.GetVisualDescendants().OfType<Button>()
                .Any(b => b.IsEffectivelyVisible && Equals(b.Content, App.Text("WorkingCopy.SaveIdentityAndContinue"))),
            "Commit opens the inline identity prompt");
        Screenshot(window, "commit-identity");

        wc.IdentityAuthorName = "Probe Local Author";
        wc.IdentityAuthorEmail = "local@example.invalid";
        ClickButtonByText(window, App.Text("WorkingCopy.SaveIdentityAndContinue"));
        Await(() => !wc.ShowIdentityPrompt && !wc.IsCommitting && wc.Staged.Count == 0);
        Check(wc.Unstaged.Count == 1 &&
              Git(path, "show", "HEAD:file.txt").Replace("\r\n", "\n") == "base\nstaged" &&
              File.ReadAllText(Environment.GetEnvironmentVariable("GIT_CONFIG_GLOBAL")!).Length == 0,
            "Identity confirmation commits staged content and preserves the remaining edit");
    }
}
