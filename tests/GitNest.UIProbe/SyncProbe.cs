using Avalonia.Controls;
using Avalonia.VisualTree;
using SourceGit;
using VM = SourceGit.ViewModels;

internal static partial class Program
{
    static partial void RunSync()
    {
        var path = CreateRepository("sync");
        var origin = AddOrigin(path);
        var peer = Path.Combine(_root, "sync-peer");
        Git(_root, "clone", origin, peer);
        File.WriteAllText(Path.Combine(path, "new.txt"), "payload\n");
        Git(path, "add", "new.txt");
        var hook = Path.Combine(origin, "hooks", "pre-receive");
        File.WriteAllText(hook, "#!/bin/sh\nexit 1\n");

        var (repo, page, window) = OpenRepository(path);
        repo.SelectedViewIndex = 1;
        Await(() => repo.WorkingCopy is { Staged.Count: 1, LastCapturedSnapshot: not null });
        var wc = repo.WorkingCopy;
        wc.CommitMessage = "commit and push";
        ClickButtonByText(window, App.Text("WorkingCopy.CommitAndPush"));
        Await(() => !wc.IsCommitting && wc.HasTwoStepStatus);
        Check(wc.ActiveTwoStepSession is { CommitSucceeded: true, CanRetryPushOnly: true },
            "Rejected Commit & Push exposes Retry Push Only");
        Screenshot(window, "sync-retry");

        var head = Git(path, "rev-parse", "HEAD");
        File.Delete(hook);
        ClickButtonByText(window, App.Text("WorkingCopy.RetryPushOnly"));
        Await(() => !wc.IsCommitting && wc.TwoStepPushSucceeded);
        Check(Git(path, "rev-parse", "HEAD") == head && Git(origin, "rev-parse", "main") == head,
            "Retry Push Only completes without a second commit");
        ClickButtonByText(window, App.Text("WorkingCopy.DismissBanner"));

        var fetch = new VM.Fetch(repo);
        repo.ShowPopup(fetch);
        Await(() => page.Popup == fetch);
        Check(fetch.LastFetchStatusText.Length > 0, "Fetch popup renders its status");
        ClickButtonByText(window, App.Text("Sure"));
        Await(() => page.Popup == null);

        Git(peer, "pull", "--ff-only");
        Git(peer, "-c", "user.name=Peer", "-c", "user.email=peer@example.invalid", "commit", "--allow-empty", "-m", "peer");
        Git(peer, "push");
        Git(path, "commit", "--allow-empty", "-m", "local");
        var pull = new VM.Pull(repo, null);
        repo.ShowPopup(pull);
        Pump();
        ClickButtonByText(window, App.Text("Sure"));
        Await(() => !pull.InProgress && pull.HasFeedbackBanner);
        Check(pull.IsDiverged && window.GetVisualDescendants().OfType<CheckBox>()
                .All(c => !Equals(c.Content, App.Text("Pull.UseRebase"))),
            "Diverged Pull shows merge handoff without rebase controls");
        Screenshot(window, "sync-diverged");
        ClickButtonByText(window, App.Text("Pull.OpenMerge"));
        Await(() => page.Popup is VM.Merge);
        Check(page.Popup is VM.Merge, "Open Merge Flow opens the Merge popup");
        ClickButtonByText(window, App.Text("Cancel"));
        Await(() => page.Popup == null);

        Git(path, "checkout", "-b", "feat/no-upstream");
        repo.RefreshBranches();
        Await(() => repo.CurrentBranch?.Name == "feat/no-upstream");
        var push = new VM.Push(repo, null);
        repo.ShowPopup(push);
        Pump();
        Check(push.ShowNoUpstreamHint && !push.CanStartDirectly() &&
              push.RefspecPreview.Contains("refs/heads/feat/no-upstream:refs/heads/feat/no-upstream (origin)", StringComparison.Ordinal),
            "Push popup previews the selected branch and requires explicit upstream choice");
        Screenshot(window, "sync-push-preview");
    }
}
