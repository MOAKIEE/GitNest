using SourceGit;
using SourceGit.Commands;
using VM = SourceGit.ViewModels;

internal static partial class Program
{
    static partial void RunPushRevision()
    {
        var path = CreateRepository("push-revision");
        var initial = new QuerySingleCommit(path, "HEAD").GetResult()!;
        var origin = AddOrigin(path);
        Git(path, "checkout", "-b", "feat/revision");
        Git(path, "push", "-u", "origin", "feat/revision");
        var selectedSha = CommitFile(path, "revision.txt", "selected\n", "selected commit");
        var selected = new QuerySingleCommit(path, selectedSha).GetResult()!;
        var laterSha = CommitFile(path, "revision.txt", "later\n", "later commit");
        Git(path, "config", "push.followTags", "true");
        Git(path, "tag", "-a", "private", "-m", "private tag", initial.SHA);
        var (repo, page, window) = OpenRepository(path);
        Await(() => repo.Branches.Any(b => !b.IsLocal && b.Name == "feat/revision"));
        var remote = repo.Branches.First(b => !b.IsLocal && b.Name == "feat/revision");
        page.Popup = new VM.PushRevision(repo, selected, remote);
        Pump();
        Screenshot(window, "push-revision");
        ClickButtonByText(window, App.Text("Sure"));
        Await(() => page.Popup == null);
        Check(Git(origin, "rev-parse", "refs/heads/feat/revision") == selectedSha && Git(origin, "tag", "--list").Length == 0,
            "Push Revision button publishes the selected commit without private tags");

        var changed = new VM.PushRevision(repo, initial, remote) { Force = true };
        var guards = changed.EnsureForceGuardsLoadedAsync();
        Await(() => guards.IsCompleted && !changed.IsCheckingForceGuards);
        var other = Path.Combine(_root, "other-push.git");
        Git(_root, "init", "--bare", "-b", "main", other);
        Git(path, "push", "--no-follow-tags", other, $"{selectedSha}:refs/heads/feat/revision");
        Git(path, "remote", "set-url", "--push", "origin", other);
        var attempt = changed.Sure();
        Await(() => attempt.IsCompleted);
        Check(!attempt.GetAwaiter().GetResult() && Git(other, "rev-parse", "refs/heads/feat/revision") == selectedSha,
            "Push Revision rechecks the endpoint after force confirmation");
        Git(path, "config", "--unset-all", "remote.origin.pushurl");

        var force = new VM.PushRevision(repo, initial, remote) { Force = true };
        page.Popup = force;
        guards = force.EnsureForceGuardsLoadedAsync();
        Await(() => guards.IsCompleted && !force.IsCheckingForceGuards);
        ClickButtonByText(window, App.Text("Sure"));
        Await(() => page.Popup == null);
        Check(Git(origin, "rev-parse", "refs/heads/feat/revision") == initial.SHA && Git(path, "rev-parse", "HEAD") == laterSha &&
              Git(path, "for-each-ref", "--format=%(objectname)", "refs/gitnest/backups").Contains(selectedSha, StringComparison.Ordinal),
            "Confirmed force push preserves the local branch and a remote recovery reference");
    }
}
