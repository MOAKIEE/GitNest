using SourceGit.Models;

internal static partial class Program
{
    private static async Task RunWorktreeTestsAsync(string git, string root)
    {
        var repo = Path.Combine(root, "任务 主仓库 #2");
        var common = Path.Combine(repo, ".git");
        await GitAsync(git, "init", "-b", "main", repo);
        await GitAsync(git, "-C", repo, "config", "user.name", "Task tests");
        await GitAsync(git, "-C", repo, "config", "user.email", "tests@example.invalid");
        async Task<WorktreeStatus> Read(string path) => await new SourceGit.Commands.QueryWorktreeStatus(path).GetResultAsync(common);

        var status = await Read(repo);
        Check(status.IsReadable && status.SyncState == WorktreeSyncState.Unborn && status.Branch == "main", "An empty repository reports its unborn branch");
        File.WriteAllText(Path.Combine(repo, "部分 暂存.txt"), "base\n");
        File.WriteAllText(Path.Combine(repo, "重命名.txt"), "rename\n");
        await GitAsync(git, "-C", repo, "add", ".");
        await GitAsync(git, "-C", repo, "commit", "-m", "base");
        var initialHead = (await GitAsync(git, "-C", repo, "rev-parse", "HEAD")).Trim();
        var initialConfig = Hash(Path.Combine(common, "config"));
        var initialIndex = Hash(Path.Combine(common, "index"));
        status = await Read(repo);
        Check(status.ChangedFiles == 0 && status.SyncState == WorktreeSyncState.NoUpstream && status.Ahead == null, "A clean branch without upstream does not invent synchronization counts");
        Check(initialConfig == Hash(Path.Combine(common, "config")) && initialIndex == Hash(Path.Combine(common, "index")), "Status reads preserve Git config and index bytes");

        var linked = Path.Combine(root, "中文 任务 & #3");
        await GitAsync(git, "-C", repo, "worktree", "add", "-b", "task/ui", linked);
        File.AppendAllText(Path.Combine(linked, "部分 暂存.txt"), "staged\n");
        await GitAsync(git, "-C", linked, "add", "部分 暂存.txt");
        File.AppendAllText(Path.Combine(linked, "部分 暂存.txt"), "unstaged\n");
        File.WriteAllText(Path.Combine(linked, "新 文件 # &.txt"), "untracked\n");
        await GitAsync(git, "-C", linked, "mv", "重命名.txt", "重命名 后.txt");
        var linkedIndex = (await GitAsync(git, "-C", linked, "rev-parse", "--path-format=absolute", "--git-path", "index")).Trim();
        var indexHash = Hash(linkedIndex);
        status = await Read(linked);
        Check(status.IsReadable && status.Branch == "task/ui" && status.Head == initialHead, "Linked worktree status uses its own branch and HEAD");
        Check(status.ChangedFiles == 3 && status.Staged == 2 && status.Unstaged == 1 && status.Untracked == 1, "Partial staging and renames count files once and retain both index/worktree counts");
        Check(indexHash == Hash(linkedIndex), "Linked worktree status does not refresh or rewrite its index");
        Check((await Read(repo)).ChangedFiles == 0, "A linked worktree's changes do not contaminate the main worktree");

        await GitAsync(git, "-C", repo, "worktree", "lock", "--reason", "任务进行中\n请保留", linked);
        var trees = await new SourceGit.Commands.Worktree(repo).ReadAllAsync();
        var tree = trees.Single(t => WorktreeTaskStore.PathsEqual(t.FullPath, linked));
        Check(trees.Count == 2 && tree.Branch == "refs/heads/task/ui", "Registered worktrees retain complete Unicode and special-character paths");
        Check(tree.IsLocked && tree.LockReason == "任务进行中\n请保留", "NUL-delimited worktree records preserve multiline lock reasons");

        var detached = Path.Combine(root, "detached task");
        await GitAsync(git, "-C", repo, "worktree", "add", "--detach", detached, "HEAD");
        Check((await Read(detached)).SyncState == WorktreeSyncState.Detached, "Detached HEAD reports an explicit synchronization state");
        Directory.Move(detached, detached + " moved");
        status = await Read(detached);
        Check(!status.IsReadable && status.Error == "Worktree.Status.Missing" && status.Ahead == null, "A missing worktree has unknown status rather than zero changes");
        trees = await new SourceGit.Commands.Worktree(repo).ReadAllAsync();
        Check(trees.Count == 3 && trees.Any(t => t.IsDetached && t.PrunableReason.Length > 0), "Missing registered worktrees remain visible with Git's prunable reason");

        var remote = Path.Combine(root, "task-remote.git");
        await GitAsync(git, "init", "--bare", remote);
        await GitAsync(git, "-C", repo, "remote", "add", "origin", remote);
        await GitAsync(git, "-C", repo, "push", "-u", "origin", "main");
        status = await Read(repo);
        Check(status.SyncState == WorktreeSyncState.NotFetched && status.Ahead == null, "A configured upstream without a fetch record remains explicitly unknown");
        await GitAsync(git, "-C", repo, "fetch", "origin");
        status = await Read(repo);
        Check(status.SyncState == WorktreeSyncState.Cached && status.Upstream == "origin/main" && status.Ahead == 0 && status.Behind == 0 && status.LastFetchAt != null, "Fetched synchronization counts are labeled as local cache with fetch time");

        var other = Path.Combine(root, "independent clone");
        await GitAsync(git, "clone", "--branch", "main", remote, other);
        await GitAsync(git, "-C", other, "config", "user.name", "Collaborator");
        await GitAsync(git, "-C", other, "config", "user.email", "collaborator@example.invalid");
        File.WriteAllText(Path.Combine(other, "remote.txt"), "remote\n");
        await GitAsync(git, "-C", other, "add", ".");
        await GitAsync(git, "-C", other, "commit", "-m", "remote commit");
        await GitAsync(git, "-C", other, "push");
        await GitAsync(git, "-C", repo, "commit", "--allow-empty", "-m", "local commit");
        await GitAsync(git, "-C", repo, "fetch", "origin");
        status = await Read(repo);
        Check(status.Ahead == 1 && status.Behind == 1, "Diverged branches report actual ahead and behind counts");
        Check(!(await Read(other)).IsReadable, "An independent clone with the same remote fails common-directory identity validation");
        trees = await new SourceGit.Commands.Worktree(repo).ReadAllAsync();
        Check(trees.Count == 3 && trees.All(t => !WorktreeTaskStore.PathsEqual(t.FullPath, other)), "An independent clone is not discovered as a linked worktree");
        await GitAsync(git, "-C", repo, "push", "origin", "--delete", "main");
        status = await Read(repo);
        Check(status.SyncState == WorktreeSyncState.UpstreamGone && status.Ahead == null, "A deleted tracking ref is distinct from no upstream and cached zero counts");

        // Both sides modify the same tracked file to exercise real unmerged index entries.
        await GitAsync(git, "-C", repo, "checkout", "-b", "conflict-source");
        File.WriteAllText(Path.Combine(repo, "部分 暂存.txt"), "source\n");
        await GitAsync(git, "-C", repo, "commit", "-am", "source change");
        await GitAsync(git, "-C", repo, "checkout", "main");
        File.WriteAllText(Path.Combine(repo, "部分 暂存.txt"), "target\n");
        await GitAsync(git, "-C", repo, "commit", "-am", "target change");
        try { await GitAsync(git, "-C", repo, "merge", "conflict-source"); }
        catch (InvalidOperationException) { }
        status = await Read(repo);
        Check(status.IsReadable && status.Conflicted == 1 && status.ChangedFiles == 1, "A real merge conflict is counted explicitly");

        var storeFile = Path.Combine(root, "app-settings", "worktree-tasks.json");
        var store = new WorktreeTaskStore(storeFile);
        var task = store.Get(common, linked);
        task.Alias = "  修复 中文界面  ";
        task.IsCompleted = true;
        task.ComparisonTarget = "refs/heads/main";
        store.Save(task);
        var saved = new WorktreeTaskStore(storeFile).Get(common, linked.Replace('\\', '/') + "/");
        Check(saved.Alias == "修复 中文界面" && saved.IsCompleted && saved.ComparisonTarget == "refs/heads/main", "Local task metadata survives reload and normalized path lookup");
        Check(store.Get(Path.Combine(other, ".git"), linked).Alias.Length == 0, "Task metadata is scoped to both repository identity and exact worktree path");
        task.Alias = string.Empty;
        store.Save(task);
        Check(new WorktreeTaskStore(storeFile).Get(common, linked).Alias.Length == 0, "Clearing an alias persists without renaming a Git branch");
        Check((await GitAsync(git, "-C", linked, "branch", "--show-current")).Trim() == "task/ui", "Saving aliases leaves Git branch names unchanged");
        File.WriteAllText(storeFile, "broken json");
        var broken = new WorktreeTaskStore(storeFile);
        var refused = false;
        try { broken.Save(task); }
        catch (InvalidDataException) { refused = true; }
        Check(refused && File.ReadAllText(storeFile) == "broken json", "Unreadable task settings are preserved and saving reports failure");

        var nodes = SourceGit.ViewModels.Worktree.Build(repo, [new Worktree() { FullPath = repo, Branch = "refs/heads/main", Head = initialHead }]);
        Check(nodes.Count == 1 && nodes[0].IsMain && nodes[0].IsCurrent && !nodes[0].Task.IsCompleted, "A single main worktree is visible and a clean tree is not marked complete");
        var detachedNodes = SourceGit.ViewModels.Worktree.Build(repo, [new Worktree() { FullPath = repo, IsDetached = true, Head = initialHead }]);
        Check(detachedNodes[0].IsCurrent, "Detached current worktrees remain addressable in the task overview");

        var toolOutput = Path.Combine(root, "tool-output.txt");
        var startInfo = WorktreeToolLauncher.CreateEditorStartInfo(Environment.ProcessPath!, linked);
        startInfo.Environment["GITNEST_TEST_TOOL_OUTPUT"] = toolOutput;
        using var toolProcess = System.Diagnostics.Process.Start(startInfo)!;
        await toolProcess.WaitForExitAsync();
        var toolResult = File.ReadAllLines(toolOutput);
        Check(toolProcess.ExitCode == 0 && toolResult.Length == 2 && WorktreeTaskStore.PathsEqual(toolResult[0], linked) && WorktreeTaskStore.PathsEqual(toolResult[1], linked), "Launching an editor passes the exact Unicode/special-character folder as one argument and working directory");

        Check(Watcher.IsWorktreeAdminChange("worktrees/wt1") &&
              Watcher.IsWorktreeAdminChange("worktrees/wt1/HEAD") &&
              Watcher.IsWorktreeAdminChange("worktrees/wt1/locked") &&
              Watcher.IsWorktreeAdminChange("worktrees/wt1/config.worktree") &&
              !Watcher.IsWorktreeAdminChange("worktrees/wt1/index.lock") &&
              !Watcher.IsWorktreeAdminChange("objects/pack/pack-123.idx"),
              "Watcher.IsWorktreeAdminChange matches linked worktree metadata files accurately");

        var repoVm = new SourceGit.ViewModels.Repository(false, repo, common);
        var cfgChangedCount = 0;
        repoVm.ConfigurationStateChanged += () => Interlocked.Increment(ref cfgChangedCount);
        repoVm.Close();
        repoVm.RefreshConfigurationState();
        await Task.Delay(150);
        Check(cfgChangedCount == 0,
              "Repository.Close prevents post-close RefreshConfigurationState dispatch cleanly");
    }
}
