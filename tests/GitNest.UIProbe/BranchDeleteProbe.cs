using SourceGit;
using VM = SourceGit.ViewModels;

internal static partial class Program
{
    static partial void RunBranchDelete()
    {
        var path = CreateRepository("branch-delete");
        var origin = AddOrigin(path);
        Git(path, "branch", "feat/merged");
        Git(path, "checkout", "-b", "feat/unmerged");
        CommitFile(path, "unmerged.txt", "unmerged\n", "unmerged commit");
        Git(path, "checkout", "-b", "local/topic", "main");
        var topicTip = CommitFile(path, "topic.txt", "topic\n", "topic commit");
        Git(path, "push", "origin", "local/topic:refs/heads/server/topic");
        Git(path, "branch", "--set-upstream-to=origin/server/topic", "local/topic");
        Git(path, "checkout", "main");
        var peer = Path.Combine(_root, "branch-delete-peer");
        Git(_root, "clone", "--branch", "server/topic", origin, peer);
        var (repo, page, window) = OpenRepository(path);
        Await(() => repo.Branches.Any(b => b.IsLocal && b.Name == "feat/unmerged"));
        var blocked = new VM.DeleteBranch(repo, repo.CurrentBranch);
        page.Popup = blocked;
        Await(() => !blocked.IsLoadingPreflight);
        Check(!blocked.CanConfirm, "DeleteBranch blocks the current protected branch");
        ClickButtonByText(window, App.Text("Cancel"));
        Await(() => page.Popup == null);

        var unmerged = new VM.DeleteBranch(repo, repo.Branches.First(b => b.IsLocal && b.Name == "feat/unmerged"));
        page.Popup = unmerged;
        Await(() => !unmerged.IsLoadingPreflight);
        ClickButtonByText(window, App.Text("DeleteBranch.BtnDeleteLocal"));
        Await(() => unmerged.InForceLocalConfirmStep);
        Check(page.Popup == unmerged && unmerged.UnmergedCommits.Count == 1 &&
              unmerged.ConfirmButtonText == App.Text("DeleteBranch.BtnForceDelete"),
            "Unmerged deletion enters a separate Force Delete confirmation");
        Screenshot(window, "branch-force-confirm");
        ClickButtonByText(window, App.Text("DeleteBranch.BtnForceDelete"));
        Await(() => page.Popup == null);

        var topic = new VM.DeleteBranch(repo, repo.Branches.First(b => b.IsLocal && b.Name == "local/topic"));
        page.Popup = topic;
        Await(() => !topic.IsLoadingPreflight);
        Check(topic.ConfiguredUpstreamFriendly == "origin/server/topic" && topic.IsUpstreamNameDifferent && !topic.DeleteUpstream,
            "Linked deletion displays the exact upstream and defaults to local only");
        topic.DeleteUpstream = true;
        Await(() => !topic.IsProbingRemote && topic.RemoteQuerySucceeded && topic.RemoteExistsOnServer);
        Git(peer, "-c", "user.name=Peer", "-c", "user.email=peer@example.invalid", "commit", "--allow-empty", "-m", "concurrent commit");
        Git(peer, "push");
        ClickButtonByText(window, App.Text("DeleteBranch.BtnDeleteLocalAndRemote"));
        Await(() => topic.HasResultView);
        Check(page.Popup == topic && topic.CanRestoreLocalFromBackup,
            "Remote lease rejection keeps the partial result and recovery action visible");
        Screenshot(window, "branch-partial-result");
        ClickButtonByText(window, App.Text("DeleteBranch.RestoreLocalBtn"));
        Await(() => !topic.IsRestoringBackup && topic.HasRestoreFeedback);
        Check(!topic.HasRestoreError && Git(path, "rev-parse", "refs/heads/local/topic") == topicTip,
            "Restore button rebuilds the deleted local branch");
        ClickButtonByText(window, App.Text("Close"));
        Await(() => page.Popup == null);

        var batch = new VM.DeleteMultipleBranches(repo,
            [repo.CurrentBranch, repo.Branches.First(b => b.IsLocal && b.Name == "feat/merged")], isLocal: true);
        page.Popup = batch;
        Pump();
        ClickButtonByText(window, App.Text("DeleteMultiBranch.BtnDeleteLocal"));
        Await(() => batch.HasResultView);
        Check(page.Popup == batch && batch.ResultItems.Count == 2 && !batch.ResultItems[0].Succeeded && batch.ResultItems[1].Succeeded,
            "Batch deletion keeps per-branch success and blocked results visible");
        Screenshot(window, "branch-batch-result");
    }
}
