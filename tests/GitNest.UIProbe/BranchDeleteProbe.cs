using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;

using Avalonia.Controls;
using Avalonia.VisualTree;

using SourceGit;
using SourceGit.Commands;
using SourceGit.Models;
using VM = SourceGit.ViewModels;

internal static partial class Program
{
    static partial void RunBranchDelete()
    {
        var utf8NoBom = new UTF8Encoding(false);
        var suiteRoot = Path.Combine(_root, "branch-delete-probe");
        Directory.CreateDirectory(suiteRoot);

        var probeGlobalCfg = Path.Combine(suiteRoot, "probe-global.gitconfig");
        File.WriteAllText(probeGlobalCfg, string.Empty);
        Environment.SetEnvironmentVariable("GIT_CONFIG_GLOBAL", probeGlobalCfg);
        Environment.SetEnvironmentVariable("GIT_CONFIG_NOSYSTEM", "1");

        var originBare = Path.Combine(suiteRoot, "origin.git");
        var forkBare = Path.Combine(suiteRoot, "fork.git");
        var clone1 = Path.Combine(suiteRoot, "clone1");
        var clone2 = Path.Combine(suiteRoot, "clone2");
        var wtPath = Path.Combine(suiteRoot, "wt-branch-probe");
        var journal = new OperationJournalStore(Path.Combine(suiteRoot, "branch-delete-probe-journal.json"));

        Git(suiteRoot, "init", "--bare", "-b", "main", originBare);
        Git(suiteRoot, "init", "--bare", "-b", "main", forkBare);

        Git(suiteRoot, "clone", originBare, clone1);
        Git(clone1, "config", "--local", "user.name", "Probe Alice");
        Git(clone1, "config", "--local", "user.email", "alice@example.invalid");
        File.WriteAllText(Path.Combine(clone1, "README.md"), "# Branch Delete Probe\n", utf8NoBom);
        Git(clone1, "add", ".");
        Git(clone1, "commit", "-m", "initial commit");
        Git(clone1, "branch", "-M", "main");
        Git(clone1, "push", "-u", "origin", "main");
        var mainTip = Git(clone1, "rev-parse", "HEAD").Trim();

        Git(clone1, "remote", "add", "fork", forkBare);
        Git(clone1, "push", "fork", "main:refs/heads/main");

        // Prepare:
        // 1) Branch checked out in linked worktree
        Git(clone1, "worktree", "add", "-b", "feat/in-wt", wtPath, "main");

        // 2) Unmerged local branch with no upstream
        Git(clone1, "checkout", "-b", "feat/unmerged", mainTip);
        File.WriteAllText(Path.Combine(clone1, "unmerged.txt"), "unmerged feature\n", utf8NoBom);
        Git(clone1, "add", "unmerged.txt");
        Git(clone1, "commit", "-m", "feat: unmerged local work");
        var unmergedTip = Git(clone1, "rev-parse", "HEAD").Trim();

        // 3) Local branch mapped to different remote & different branch name (fork/server/topic-ui)
        Git(clone1, "checkout", "-b", "local/topic", mainTip);
        File.WriteAllText(Path.Combine(clone1, "topic.txt"), "topic v1\n", utf8NoBom);
        Git(clone1, "add", "topic.txt");
        Git(clone1, "commit", "-m", "topic v1 commit");
        var topicV1 = Git(clone1, "rev-parse", "HEAD").Trim();
        Git(clone1, "push", "fork", "local/topic:refs/heads/server/topic-ui");
        Git(clone1, "fetch", "fork");
        Git(clone1, "branch", "--set-upstream-to=fork/server/topic-ui", "local/topic");

        // 4) Remote-only target branch (also has a same-name local branch to verify local is untouched)
        Git(clone1, "checkout", "-b", "feat/remote-only", mainTip);
        File.WriteAllText(Path.Combine(clone1, "remote-only.txt"), "remote only\n", utf8NoBom);
        Git(clone1, "add", "remote-only.txt");
        Git(clone1, "commit", "-m", "remote-only commit");
        Git(clone1, "push", "-u", "origin", "feat/remote-only");
        var remoteOnlyTip = Git(clone1, "rev-parse", "HEAD").Trim();
        Git(clone1, "branch", "feat/merged-clean", mainTip);
        Git(clone1, "checkout", "main");

        // Collaborator clone on fork
        Git(suiteRoot, "clone", forkBare, clone2);
        Git(clone2, "config", "--local", "user.name", "Probe Bob");
        Git(clone2, "config", "--local", "user.email", "bob@example.invalid");

        App.SetTheme("Light", string.Empty);

        var launcher = _launcher = new VM.Launcher(clone1);
        typeof(App).GetField("_launcher", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(Avalonia.Application.Current, launcher);

        var page = launcher.ActivePage;
        var repo = (VM.Repository)page.Data;
        var pageView = new SourceGit.Views.LauncherPage { DataContext = page };
        var window = _window = new Window
        {
            Content = pageView,
            Width = 1120,
            Height = 760,
            ShowInTaskbar = false,
            Title = "GitNest 3.2 分支删除与 lease UI 探针",
        };
        window.Show();

        Await(() => repo.Branches.Count >= 4 && repo.Worktrees.Count >= 2);
        Check(!string.Equals(App.Text("BranchCM.DeleteLocal", "x"), App.Text("BranchCM.DeleteRemote", "x"), StringComparison.Ordinal) &&
              App.Text("BranchCM.DeleteLocal", "feat/a").Contains("本地", StringComparison.Ordinal) &&
              App.Text("BranchCM.DeleteRemote", "origin/feat/a").Contains("远程", StringComparison.Ordinal),
            "Branch context menu keys distinguish Delete Local Branch vs Delete Remote Branch clearly");

        // 1. Worktree-attached branch: direct deletion blocked until explicit ordered worktree cleanup checked
        var wtBranch = repo.Branches.First(b => b.IsLocal && b.Name == "feat/in-wt");
        var wtVm = new VM.DeleteBranch(repo, wtBranch, journal);
        page.Popup = wtVm;
        Await(() => !wtVm.IsLoadingPreflight);
        Check(wtVm.IsCheckedOutInOtherWorktree && !wtVm.CanConfirm,
            "DeleteBranch blocks direct deletion when branch is checked out in another linked worktree");

        wtVm.DeleteWorktree = true;
        Await(() => wtVm.CanConfirm);
        ClickButtonByText(window, App.Text("DeleteBranch.BtnDeleteLocal"));
        Await(() => page.Popup == null);
        Await(() => !Directory.Exists(wtPath) && repo.Branches.All(b => !(b.IsLocal && b.Name == "feat/in-wt")));
        Check(!Directory.Exists(wtPath) && repo.Branches.All(b => !(b.IsLocal && b.Name == "feat/in-wt")),
            "Checking explicit worktree cleanup removes linked worktree first and then deletes local branch");

        // 2. Unmerged local branch: regular -d blocks with unmerged commits, enters explicit Force Delete step & creates backup ref
        var unmergedBranch = repo.Branches.First(b => b.IsLocal && b.Name == "feat/unmerged");
        var unmergedVm = new VM.DeleteBranch(repo, unmergedBranch, journal);
        page.Popup = unmergedVm;
        Await(() => !unmergedVm.IsLoadingPreflight);
        Check(!unmergedVm.HasConfiguredUpstream &&
              unmergedVm.ShowUnmergedSection &&
              unmergedVm.ConfirmButtonText == App.Text("DeleteBranch.BtnDeleteLocal"),
            "Unmerged branch shows unmerged commits vs HEAD without linked upstream guess and uses regular delete initially");

        ClickButtonByText(window, App.Text("DeleteBranch.BtnDeleteLocal"));
        Await(() => unmergedVm.InForceLocalConfirmStep);
        Check(page.Popup == unmergedVm &&
              unmergedVm.InForceLocalConfirmStep &&
              unmergedVm.UnmergedCommits.Count == 1 &&
              unmergedVm.ConfirmButtonText == App.Text("DeleteBranch.BtnForceDelete"),
            "Regular -d refusal keeps popup open, lists unmerged commit reason, and enters distinct Force Delete confirmation step");

        Screenshot(window, "delete-local-unmerged-force-step-light");

        ClickButtonByText(window, App.Text("DeleteBranch.BtnForceDelete"));
        Await(() => page.Popup == null);
        Await(() => repo.Branches.All(b => !(b.IsLocal && b.Name == "feat/unmerged")));
        var backupsAfterForce = BackupRefs.ListAsync(clone1).GetAwaiter().GetResult();
        Check(backupsAfterForce.Any(b => b.CommitSha == unmergedTip) &&
              repo.Branches.All(b => !(b.IsLocal && b.Name == "feat/unmerged")),
            "Confirmed force delete removes local unmerged branch while preserving recoverable backup ref");

        // 3. Linked different-name upstream on fork + collaborator push + background fetch + lease partial failure + inline restore
        var topicBranch = repo.Branches.First(b => b.IsLocal && b.Name == "local/topic");
        var topicVm = new VM.DeleteBranch(repo, topicBranch, journal);
        page.Popup = topicVm;
        Await(() => !topicVm.IsLoadingPreflight);
        Check(topicVm.HasConfiguredUpstream &&
              topicVm.ConfiguredUpstreamFriendly == "fork/server/topic-ui" &&
              topicVm.IsUpstreamNameDifferent &&
              !topicVm.DeleteUpstream,
            "DeleteBranch resolves exact configured different-name multi-remote upstream with DeleteUpstream unchecked by default");

        topicVm.DeleteUpstream = true;
        Await(() => !topicVm.IsProbingRemote && topicVm.RemoteQuerySucceeded && topicVm.RemoteExistsOnServer);
        Check(topicVm.ShowLinkedTargetsBreakdown &&
              topicVm.ConfirmButtonText == App.Text("DeleteBranch.BtnDeleteLocalAndRemote"),
            "Checking linked upstream lists both local & remote targets explicitly and updates button to Delete Local & Remote");

        // Collaborator pushes new commit to fork/server/topic-ui AFTER user inspected/checked linked delete,
        // AND local clone runs background fetch without changing the frozen confirmation lease!
        Git(clone2, "checkout", "-b", "topic-collab", "origin/server/topic-ui");
        File.WriteAllText(Path.Combine(clone2, "topic.txt"), "topic v2 collaborator\n", utf8NoBom);
        Git(clone2, "add", "topic.txt");
        Git(clone2, "commit", "-m", "collaborator commit during delete");
        var topicV2 = Git(clone2, "rev-parse", "HEAD").Trim();
        Git(clone2, "push", "origin", "topic-collab:refs/heads/server/topic-ui");
        Git(clone1, "fetch", "fork");

        ClickButtonByText(window, App.Text("DeleteBranch.BtnDeleteLocalAndRemote"));
        Await(() => topicVm.HasResultView);
        var serverTopicAfterLeaseReject = Git(forkBare, "rev-parse", "refs/heads/server/topic-ui").Trim();
        var visibleTexts = CollectAllStrings(pageView);
        Check(page.Popup == topicVm &&
              !topicVm.ShowConfirmButton &&
              topicVm.CancelButtonText == App.Text("Close") &&
              topicVm.CanRestoreLocalFromBackup &&
              topicVm.ResultSummary.Contains("本地已删除，远程未删除", StringComparison.Ordinal) &&
              serverTopicAfterLeaseReject == topicV2 &&
              visibleTexts.Any(t => t.Contains("已保存本地恢复引用", StringComparison.Ordinal)),
            "Lease rejection keeps popup open with step-by-step partial failure summary, preserves collaborator commit, and offers backup restore");

        Screenshot(window, "delete-linked-partial-recovery-light");

        ClickButtonByText(window, App.Text("DeleteBranch.RestoreLocalBtn"));
        Await(() => !topicVm.IsRestoringBackup && topicVm.HasRestoreFeedback);
        var recoveredTopicTip = Git(clone1, "rev-parse", "refs/heads/local/topic").Trim();
        Check(!topicVm.HasRestoreError &&
              recoveredTopicTip == topicV1 &&
              !topicVm.CanRestoreLocalFromBackup,
            "One-click restore from partial failure result view accurately rebuilds deleted local branch");

        ClickButtonByText(window, App.Text("Close"));
        Await(() => page.Popup == null);

        // 4. Standalone remote branch deletion (Dark theme + Narrow 780x700) keeps local same-name branch untouched
        App.SetTheme("Dark", string.Empty);
        window.Width = 780;
        window.Height = 700;
        Await(() => window.Bounds.Width <= 790);

        var remoteTarget = repo.Branches.First(b => !b.IsLocal && b.Remote == "origin" && b.Name == "feat/remote-only");
        var remoteVm = new VM.DeleteBranch(repo, remoteTarget, journal);
        page.Popup = remoteVm;
        Await(() => !remoteVm.IsLoadingPreflight && !remoteVm.IsProbingRemote);
        Check(remoteVm.IsRemote &&
              remoteVm.RemoteQuerySucceeded &&
              remoteVm.RemoteExistsOnServer &&
              remoteVm.ConfirmButtonText == App.Text("DeleteBranch.BtnDeleteRemote"),
            "Standalone remote deletion shows server URL and live server tip with explicit Delete Remote Branch button");

        Screenshot(window, "delete-remote-dark-narrow");

        ClickButtonByText(window, App.Text("DeleteBranch.BtnDeleteRemote"));
        Await(() => page.Popup == null);
        Await(() =>
        {
            var localCopy = repo.Branches.FirstOrDefault(b => b.IsLocal && b.Name == "feat/remote-only");
            return localCopy != null && localCopy.IsUpstreamGone;
        });
        var originHasRemoteOnly = Git(originBare, "branch", "--list", "feat/remote-only");
        Check(string.IsNullOrWhiteSpace(originHasRemoteOnly) &&
              Git(clone1, "rev-parse", "refs/heads/feat/remote-only").Trim() == remoteOnlyTip &&
              repo.Branches.Any(b => b.IsLocal && b.Name == "feat/remote-only" && b.IsUpstreamGone),
            "Standalone remote deletion removes server branch, keeps local same-name branch intact, and marks upstream gone");

        // 5. Batch multi-branch delete shows explicit per-item results (protected + deleted + unmerged-skipped)
        var currentMain = repo.Branches.First(b => b.IsLocal && b.Name == "main");
        var mergedClean = repo.Branches.First(b => b.IsLocal && b.Name == "feat/merged-clean");
        var localRemoteOnly = repo.Branches.First(b => b.IsLocal && b.Name == "feat/remote-only");
        var multiVm = new VM.DeleteMultipleBranches(repo, [currentMain, mergedClean, localRemoteOnly], isLocal: true, journalStore: journal);
        page.Popup = multiVm;
        Await(() => multiVm.Targets.Count == 3);

        ClickButtonByText(window, App.Text("DeleteMultiBranch.BtnDeleteLocal"));
        Await(() => multiVm.HasResultView);
        Check(page.Popup == multiVm &&
              multiVm.ResultItems.Count == 3 &&
              !multiVm.ResultItems[0].Succeeded &&
              multiVm.ResultItems[1].Succeeded &&
              !multiVm.ResultItems[2].Succeeded,
            "DeleteMultipleBranches stays open to show per-item protected/deleted/skipped results without silently closing");

        Screenshot(window, "delete-multiple-result-dark-narrow");

        ClickButtonByText(window, App.Text("Close"));
        Await(() => page.Popup == null);

        window.Close();
        _window = null;
        File.WriteAllText(probeGlobalCfg, string.Empty);
    }
}
