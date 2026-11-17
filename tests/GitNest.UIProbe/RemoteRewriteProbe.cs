using Avalonia.Controls;
using Avalonia.VisualTree;
using SourceGit;
using VM = SourceGit.ViewModels;

internal static partial class Program
{
    static partial void RunRemoteRewrite()
    {
        var path = CreateRepository("remote-rewrite");
        var origin = AddOrigin(path);
        Git(path, "checkout", "-b", "feat/rewrite");
        CommitFile(path, "file.txt", "remote change\n", "remote commit");
        Git(path, "push", "-u", "origin", "feat/rewrite");
        Git(path, "reset", "--hard", "HEAD~1");
        var (repo, page, window) = OpenRepository(path);

        var ready = new VM.RewriteRemoteHistory(repo, repo.CurrentBranch);
        page.Popup = ready;
        Await(() => !ready.IsLoadingPreflight && ready.HasPreflight);
        Check(ready.CanConfirm && ready.HasPinnedLeaseNote && ready.HasCommitsToRemove &&
              window.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == ready.PinnedLeaseNote),
            "RewriteRemoteHistory renders the pinned lease and remote commit preview");
        Screenshot(window, "rewrite-ready");
        ClickButtonByText(window, App.Text("Cancel"));
        Await(() => page.Popup == null);

        Git(path, "remote", "set-url", "origin", origin + "-missing");
        var offline = new VM.RewriteRemoteHistory(repo, repo.CurrentBranch);
        page.Popup = offline;
        Await(() => !offline.IsLoadingPreflight && offline.HasPreflight);
        Check(!offline.CanConfirm && offline.IsRemoteStateUnknown &&
              window.GetVisualDescendants().OfType<Button>()
                  .Any(b => Equals(b.Content, offline.ConfirmButtonText) && !b.IsEffectivelyEnabled),
            "Offline rewrite disables the actual confirmation button");
        Screenshot(window, "rewrite-offline");
    }
}
