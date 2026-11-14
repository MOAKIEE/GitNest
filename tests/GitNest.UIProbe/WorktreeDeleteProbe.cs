using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;

using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.VisualTree;

using SourceGit;
using SourceGit.Commands;
using SourceGit.Models;
using VM = SourceGit.ViewModels;

internal static partial class Program
{
    static partial void RunWorktreeDelete()
    {
        var utf8NoBom = new UTF8Encoding(false);
        var suiteRoot = Path.Combine(_root, "worktree-delete-probe");
        Directory.CreateDirectory(suiteRoot);

        var probeGlobalCfg = Path.Combine(suiteRoot, "probe-global.gitconfig");
        File.WriteAllText(probeGlobalCfg, string.Empty, utf8NoBom);
        Environment.SetEnvironmentVariable("GIT_CONFIG_GLOBAL", probeGlobalCfg);
        Environment.SetEnvironmentVariable("GIT_CONFIG_NOSYSTEM", "1");

        var originBare = Path.Combine(suiteRoot, "origin.git");
        var forkBare = Path.Combine(suiteRoot, "fork.git");
        var clone1 = Path.Combine(suiteRoot, "clone1");
        var clone2 = Path.Combine(suiteRoot, "clone2");
        var wtDirtyPath = Path.Combine(suiteRoot, "wt-dirty");
        var wtLockedPath = Path.Combine(suiteRoot, "wt-locked");
        var wtNestedPath = Path.Combine(suiteRoot, "wt-nested");
        var wtOpenOrchPath = Path.Combine(suiteRoot, "wt-open-orch");
        var wtDetachedPath = Path.Combine(suiteRoot, "wt-detached");
        var wtMissingAPath = Path.Combine(suiteRoot, "wt-missing-a");
        var wtMissingBPath = Path.Combine(suiteRoot, "wt-missing-b");
        var wtMissingLockedPath = Path.Combine(suiteRoot, "wt-missing-locked");
        var journal = new OperationJournalStore(Path.Combine(suiteRoot, "worktree-delete-probe-journal.json"));

        Git(suiteRoot, "init", "--bare", "-b", "main", originBare);
        Git(suiteRoot, "init", "--bare", "-b", "main", forkBare);

        Git(suiteRoot, "clone", originBare, clone1);
        Git(clone1, "config", "--local", "user.name", "Probe Alice");
        Git(clone1, "config", "--local", "user.email", "alice@example.invalid");
        File.WriteAllText(Path.Combine(clone1, ".gitignore"), "*.ignored\n.env.local\n", utf8NoBom);
        File.WriteAllText(Path.Combine(clone1, "README.md"), "# Worktree Delete Probe\n", utf8NoBom);
        Git(clone1, "add", ".");
        Git(clone1, "commit", "-m", "initial commit");
        Git(clone1, "branch", "-M", "main");
        Git(clone1, "push", "-u", "origin", "main");
        var mainTip = Git(clone1, "rev-parse", "HEAD").Trim();

        Git(clone1, "remote", "add", "fork", forkBare);
        Git(clone1, "push", "fork", "main:refs/heads/main");

        // Collaborator clone on fork
        Git(suiteRoot, "clone", forkBare, clone2);
        Git(clone2, "config", "--local", "user.name", "Probe Bob");
        Git(clone2, "config", "--local", "user.email", "bob@example.invalid");

        // 1. Dirty + untracked + .gitignore'd worktree
        Git(clone1, "worktree", "add", "-b", "feat/dirty-wt", wtDirtyPath, mainTip);
        File.WriteAllText(Path.Combine(wtDirtyPath, "staged.txt"), "staged line\n", utf8NoBom);
        Git(wtDirtyPath, "add", "staged.txt");
        File.AppendAllText(Path.Combine(wtDirtyPath, "README.md"), "unstaged edit\n", utf8NoBom);
        File.WriteAllText(Path.Combine(wtDirtyPath, "untracked.tmp"), "untracked\n", utf8NoBom);
        File.WriteAllText(Path.Combine(wtDirtyPath, "local.ignored"), "ignored artifact\n", utf8NoBom);
        File.WriteAllText(Path.Combine(wtDirtyPath, ".env.local"), "SECRET=1\n", utf8NoBom);

        // 2. Locked linked worktree
        Git(clone1, "worktree", "add", "-b", "feat/locked-wt", wtLockedPath, mainTip);
        Git(clone1, "worktree", "lock", "--reason", "CI deployment artifact lock", wtLockedPath);

        // 3. Worktree with independent nested git repository
        Git(clone1, "worktree", "add", "-b", "feat/nested-wt", wtNestedPath, mainTip);
        var nestedSubRepo = Path.Combine(wtNestedPath, "subrepo");
        Directory.CreateDirectory(nestedSubRepo);
        Git(nestedSubRepo, "init", "-b", "main");
        Git(nestedSubRepo, "config", "--local", "user.name", "Probe Sub");
        Git(nestedSubRepo, "config", "--local", "user.email", "sub@example.invalid");
        File.WriteAllText(Path.Combine(nestedSubRepo, "sub.txt"), "nested content\n", utf8NoBom);
        Git(nestedSubRepo, "add", "sub.txt");
        Git(nestedSubRepo, "commit", "-m", "nested init");

        // 4. Orchestrated linked worktree + different upstream remote + unmerged local commit C2
        Git(clone1, "checkout", "-b", "feat/orch", mainTip);
        File.WriteAllText(Path.Combine(clone1, "orch.txt"), "orch v1\n", utf8NoBom);
        Git(clone1, "add", "orch.txt");
        Git(clone1, "commit", "-m", "orch v1 commit");
        Git(clone1, "push", "fork", "feat/orch:refs/heads/server/orch-ui");
        Git(clone1, "fetch", "fork");
        Git(clone1, "branch", "--set-upstream-to=fork/server/orch-ui", "feat/orch");
        File.WriteAllText(Path.Combine(clone1, "orch-local.txt"), "local unpushed v2\n", utf8NoBom);
        Git(clone1, "add", "orch-local.txt");
        Git(clone1, "commit", "-m", "orch local unpushed v2");
        var orchLocalC2 = Git(clone1, "rev-parse", "HEAD").Trim();
        Git(clone1, "checkout", "main");
        Git(clone1, "worktree", "add", wtOpenOrchPath, "feat/orch");

        // 5. Detached HEAD worktree with an extra detached commit
        Git(clone1, "worktree", "add", "--detach", wtDetachedPath, mainTip);
        Git(wtDetachedPath, "config", "--local", "user.name", "Probe Alice");
        Git(wtDetachedPath, "config", "--local", "user.email", "alice@example.invalid");
        File.WriteAllText(Path.Combine(wtDetachedPath, "detached.txt"), "detached commit\n", utf8NoBom);
        Git(wtDetachedPath, "add", "detached.txt");
        Git(wtDetachedPath, "commit", "-m", "detached head work");
        var detachedTip = Git(wtDetachedPath, "rev-parse", "HEAD").Trim();

        // 6. Missing-on-disk worktrees (two unlocked stale + one locked retained)
        Git(clone1, "worktree", "add", "-b", "feat/miss-a", wtMissingAPath, mainTip);
        Git(clone1, "worktree", "add", "-b", "feat/miss-b", wtMissingBPath, mainTip);
        Git(clone1, "worktree", "add", "-b", "feat/miss-locked", wtMissingLockedPath, mainTip);
        Git(clone1, "worktree", "lock", "--reason", "Keep stale locked entry", wtMissingLockedPath);
        Directory.Delete(wtMissingAPath, recursive: true);
        Directory.Delete(wtMissingBPath, recursive: true);
        Directory.Delete(wtMissingLockedPath, recursive: true);

        App.SetTheme("Light", string.Empty);

        var launcher = _launcher = new VM.Launcher(clone1);
        typeof(App).GetField("_launcher", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(Avalonia.Application.Current, launcher);

        var mainPage = launcher.ActivePage;
        var repo = (VM.Repository)mainPage.Data;
        var host = new ContentControl
        {
            DataContext = launcher,
            [!ContentControl.ContentProperty] = new Binding("ActivePage"),
            ContentTemplate = new FuncDataTemplate<VM.LauncherPage>((_, _) => new SourceGit.Views.LauncherPage()),
        };

        var window = _window = new Window
        {
            Content = host,
            Width = 1120,
            Height = 760,
            ShowInTaskbar = false,
            Title = "GitNest 3.3 工作树删除与清理 UI 探针",
        };
        window.Show();

        Await(() => repo.Worktrees.Count >= 6 && repo.Branches.Any(b => b.IsLocal && b.Name == "feat/dirty-wt"));

        // Step 1: Main worktree cannot be removed; Prune header button is wired into Repository view
        var mainWt = repo.Worktrees.First(w => w.IsMain);
        var mainRemoveVm = new VM.RemoveWorktree(repo, mainWt, journal);
        mainPage.Popup = mainRemoveVm;
        Await(() => !mainRemoveVm.IsLoadingPreflight);
        var headerPruneBtn = window.GetVisualDescendants()
            .OfType<Button>()
            .FirstOrDefault(b => Equals(ToolTip.GetTip(b), App.Text("PruneWorktrees")));
        Check(!mainRemoveVm.CanConfirm && !mainRemoveVm.CanShowForceOption && headerPruneBtn != null,
            "Main worktree deletion is strictly forbidden and Worktrees header exposes Prune Worktrees preview button");
        ClickButtonByText(window, App.Text("Cancel"));
        Await(() => mainPage.Popup == null);

        // Step 2: Dirty + untracked + .gitignore'd worktree blocks regular remove & reveals separate counts + force warning
        var dirtyWt = repo.Worktrees.First(w => WorktreeTaskStore.PathsEqual(w.FullPath, wtDirtyPath));
        var dirtyVm = new VM.RemoveWorktree(repo, dirtyWt, journal);
        mainPage.Popup = dirtyVm;
        Await(() => !dirtyVm.IsLoadingPreflight);
        Check(!dirtyVm.CanConfirm &&
              dirtyVm.CanShowForceOption &&
              dirtyVm.StagedCount == 1 &&
              dirtyVm.UnstagedCount == 1 &&
              dirtyVm.UntrackedCount == 1 &&
              dirtyVm.IgnoredCount >= 2 &&
              dirtyVm.BlockingFiles.Count >= 5,
            "Dirty worktree separately detects staged, unstaged, untracked, and .gitignore'd files and blocks non-force deletion");

        dirtyVm.Force = true;
        Await(() => dirtyVm.ShowForceLossWarning && dirtyVm.CanConfirm);
        Check(dirtyVm.ConfirmButtonText == App.Text("RemoveWorktree.BtnForceDelete") &&
              window.GetVisualDescendants().OfType<TextBlock>()
                  .Any(tb => tb.IsEffectivelyVisible &&
                             (tb.Text?.Contains("不可逆本地数据损失", StringComparison.Ordinal) ?? false)),
            "Checking explicit force displays irreversible directory loss warning and switches confirm button text");

        Screenshot(window, "worktree-remove-dirty-force-light");

        ClickButtonByText(window, App.Text("RemoveWorktree.BtnForceDelete"));
        Await(() => mainPage.Popup == null && !Directory.Exists(wtDirtyPath));
        Check(!Directory.Exists(wtDirtyPath) &&
              !string.IsNullOrWhiteSpace(Git(clone1, "branch", "--list", "feat/dirty-wt")),
            "Confirmed force removal deletes dirty and ignored files while preserving local branch feat/dirty-wt");

        // Step 3: Locked worktree blocks direct/force delete, shows lock reason, unlocks after explicit confirm, then removes cleanly
        Await(() => repo.Worktrees.Any(w => WorktreeTaskStore.PathsEqual(w.FullPath, wtLockedPath)));
        var lockedWt = repo.Worktrees.First(w => WorktreeTaskStore.PathsEqual(w.FullPath, wtLockedPath));
        var lockedVm = new VM.RemoveWorktree(repo, lockedWt, journal);
        mainPage.Popup = lockedVm;
        Await(() => !lockedVm.IsLoadingPreflight);
        Check(lockedVm.IsLocked &&
              lockedVm.LockReasonDisplay.Contains("CI deployment artifact lock", StringComparison.Ordinal) &&
              !lockedVm.CanConfirm &&
              !lockedVm.CanShowForceOption &&
              !lockedVm.CanUnlockWorktree,
            "Locked worktree blocks removal including force and displays exact lock reason");

        lockedVm.UnlockAcknowledged = true;
        Await(() => lockedVm.CanUnlockWorktree);
        ClickButtonByText(window, App.Text("RemoveWorktree.UnlockAndReinspectBtn"));
        Await(() => !lockedVm.IsLocked && lockedVm.CanConfirm);
        Check(lockedVm.ConfirmButtonText == App.Text("RemoveWorktree.BtnDeleteOnly"),
            "Explicit unlock and reinspect clears lock state and enables safe removal");

        ClickButtonByText(window, App.Text("RemoveWorktree.BtnDeleteOnly"));
        Await(() => mainPage.Popup == null && !Directory.Exists(wtLockedPath));
        Check(!Directory.Exists(wtLockedPath), "Unlocked linked worktree removes cleanly");

        // Step 4: Nested git repository blocks removal AND force removal (both in RemoveWorktree and DeleteBranch)
        var nestedWt = repo.Worktrees.First(w => WorktreeTaskStore.PathsEqual(w.FullPath, wtNestedPath));
        var nestedVm = new VM.RemoveWorktree(repo, nestedWt, journal);
        mainPage.Popup = nestedVm;
        Await(() => !nestedVm.IsLoadingPreflight);
        Check(nestedVm.HasNestedRepoOrSubmodule &&
              nestedVm.NestedPathsDisplay.Contains("subrepo", StringComparison.Ordinal) &&
              !nestedVm.CanShowForceOption &&
              !nestedVm.CanConfirm,
            "RemoveWorktree completely blocks worktrees containing an independent nested git repository");
        ClickButtonByText(window, App.Text("Cancel"));
        Await(() => mainPage.Popup == null);

        var nestedBranch = repo.Branches.First(b => b.IsLocal && b.Name == "feat/nested-wt");
        var nestedBranchVm = new VM.DeleteBranch(repo, nestedBranch, journal);
        mainPage.Popup = nestedBranchVm;
        Await(() => !nestedBranchVm.IsLoadingPreflight);
        Check(nestedBranchVm.IsCheckedOutInOtherWorktree &&
              nestedBranchVm.AttachedWorktreeHasNestedRepo &&
              !nestedBranchVm.CanCheckDeleteAttachedWorktree &&
              !nestedBranchVm.CanConfirm,
            "DeleteBranch shares worktree preflight and blocks deleting a branch attached to a nested-repo worktree");
        ClickButtonByText(window, App.Text("Cancel"));
        Await(() => mainPage.Popup == null);

        // Step 5: Active open linked worktree tab switches cleanly to main tab -> step 1 deletes worktree -> step 2 stops at -d -> follow-up -D + lease partial recovery
        launcher.AddNewTab();
        var wtPage = launcher.ActivePage;
        launcher.OpenRepositoryInTab(wtOpenOrchPath, wtPage);
        Await(() => launcher.Pages.Count == 2 &&
                    wtPage.Data is VM.Repository wr &&
                    WorktreeTaskStore.PathsEqual(wr.FullPath, wtOpenOrchPath) &&
                    wr.Worktrees.Any(w => WorktreeTaskStore.PathsEqual(w.FullPath, wtOpenOrchPath) && w.IsCurrent));

        var wtRepo = (VM.Repository)wtPage.Data;
        var wtSelfItem = wtRepo.Worktrees.First(w => WorktreeTaskStore.PathsEqual(w.FullPath, wtOpenOrchPath));
        var orchVm = new VM.RemoveWorktree(wtRepo, wtSelfItem, journal);
        wtPage.Popup = orchVm;
        Await(() => !orchVm.IsLoadingPreflight);
        Check(wtSelfItem.IsCurrent &&
              orchVm.IsTargetOpenInGitNest &&
              orchVm.CanOfferLocalBranchDelete &&
              !orchVm.DeleteLocalBranch,
            "Current open linked worktree tab allows initiating removal, warns about open tab switch, and leaves local branch unchecked by default");

        orchVm.DeleteLocalBranch = true;
        Await(() => orchVm.HasConfiguredUpstream);
        Check(orchVm.ConfiguredUpstreamFriendly == "fork/server/orch-ui" &&
              orchVm.IsUpstreamNameDifferent &&
              !orchVm.DeleteRemoteBranch,
            "Enabling linked branch delete resolves exact different-name upstream and leaves remote branch unchecked by default");

        orchVm.DeleteRemoteBranch = true;
        Await(() => !orchVm.IsProbingRemote &&
                    orchVm.CanConfirm &&
                    !string.IsNullOrEmpty(orchVm.RemoteLiveStatusText) &&
                    orchVm.RemoteLiveStatusText != App.Text("DeleteBranch.RemoteAbsentOnServer"));
        Check(orchVm.ConfirmButtonText == App.Text("RemoveWorktree.BtnDeleteAll"),
            "Checking linked remote probes live tip SHA and sets multi-target confirmation button text");

        // Collaborator pushes new commit C3 after user inspected C1 lease
        Git(clone2, "fetch", "origin");
        Git(clone2, "checkout", "-b", "orch-collab", "origin/server/orch-ui");
        File.WriteAllText(Path.Combine(clone2, "orch.txt"), "orch v3 collaborator\n", utf8NoBom);
        Git(clone2, "add", "orch.txt");
        Git(clone2, "commit", "-m", "collaborator orch v3");
        var collabC3 = Git(clone2, "rev-parse", "HEAD").Trim();
        Git(clone2, "push", "origin", "orch-collab:refs/heads/server/orch-ui");
        Git(clone1, "fetch", "fork");

        ClickButtonByText(window, App.Text("RemoveWorktree.BtnDeleteAll"));
        Await(() => mainPage.Popup == orchVm &&
                    orchVm.HasResultView &&
                    launcher.ActivePage == mainPage &&
                    launcher.Pages.Count == 1 &&
                    !Directory.Exists(wtOpenOrchPath));
        Check(launcher.ActivePage == mainPage &&
              launcher.Pages.Count == 1 &&
              orchVm.RequiresFollowUpForceLocalBranch &&
              orchVm.UnmergedCommits.Count == 2 &&
              Git(clone1, "rev-parse", "--verify", "refs/heads/feat/orch").Trim() == orchLocalC2,
            "Open worktree tab closes and switches to main tab, deletes worktree, and stops safely at -d keeping unmerged branch and collaborator commit intact");

        Screenshot(window, "worktree-remove-follow-up-unmerged-light");

        ClickButtonByText(window, App.Text("RemoveWorktree.ForceDeleteRemainingBranchBtn"));
        Await(() => !orchVm.IsExecutingFollowUp && !orchVm.RequiresFollowUpForceLocalBranch && orchVm.CanRestoreBackupBranch);
        Check(Git(forkBare, "rev-parse", "refs/heads/server/orch-ui").Trim() == collabC3 &&
              orchVm.CanRestoreBackupBranch,
            "Follow-up force delete (-D) saves backup ref and removes local branch while frozen lease rejects overwriting collaborator remote commit");

        ClickButtonByText(window, App.Text("DeleteBranch.RestoreLocalBtn"));
        Await(() => !orchVm.IsRestoringBackup && orchVm.HasRestoreFeedback);
        Check(!orchVm.HasRestoreError &&
              Git(clone1, "rev-parse", "refs/heads/feat/orch").Trim() == orchLocalC2,
            "Backup restore after orchestrated worktree removal restores deleted local unmerged branch tip");

        ClickButtonByText(window, App.Text("Close"));
        Await(() => mainPage.Popup == null);

        // Step 6: Detached HEAD worktree shows backup notice and creates backup ref (Dark theme + Narrow 780x700)
        App.SetTheme("Dark", string.Empty);
        window.Width = 780;
        window.Height = 700;
        Await(() => window.Bounds.Width <= 790 && repo.Worktrees.Any(w => WorktreeTaskStore.PathsEqual(w.FullPath, wtDetachedPath)));

        var detachedWt = repo.Worktrees.First(w => WorktreeTaskStore.PathsEqual(w.FullPath, wtDetachedPath));
        var detVm = new VM.RemoveWorktree(repo, detachedWt, journal);
        mainPage.Popup = detVm;
        Await(() => !detVm.IsLoadingPreflight);
        Check(detVm.IsDetached && !detVm.CanOfferLocalBranchDelete && detVm.CanConfirm,
            "Detached HEAD worktree displays detached backup explanation and disables local branch option");

        Screenshot(window, "worktree-remove-detached-dark-narrow");

        ClickButtonByText(window, App.Text("RemoveWorktree.BtnDeleteOnly"));
        Await(() => mainPage.Popup == null && !Directory.Exists(wtDetachedPath));
        var backups = BackupRefs.ListAsync(clone1).GetAwaiter().GetResult();
        Check(backups.Any(b => b.CommitSha == detachedTip),
            "Removing detached HEAD worktree persists recoverable local backup ref before deleting directory");

        // Step 7: Missing directory -> Open Prune preview -> multi-candidate warning + locked entry retained (Dark Narrow 780x700)
        Await(() => repo.Worktrees.Any(w => WorktreeTaskStore.PathsEqual(w.FullPath, wtMissingAPath)));
        var missAWt = repo.Worktrees.First(w => WorktreeTaskStore.PathsEqual(w.FullPath, wtMissingAPath));
        var missVm = new VM.RemoveWorktree(repo, missAWt, journal);
        mainPage.Popup = missVm;
        Await(() => !missVm.IsLoadingPreflight);
        Check(missVm.IsDirectoryMissing && !missVm.CanConfirm && !missVm.CanShowForceOption,
            "Missing worktree blocks direct remove and directs user to Prune Worktrees preview");

        ClickButtonByText(window, App.Text("RemoveWorktree.OpenPrunePreviewBtn"));
        Await(() => mainPage.Popup is VM.PruneWorktrees);
        var pruneVm = (VM.PruneWorktrees)mainPage.Popup;
        Await(() => !pruneVm.IsLoadingPreview);
        Check(pruneVm.Candidates.Count == 2 &&
              pruneVm.LockedRetainedItems.Count == 1 &&
              pruneVm.LockedRetainedItems[0].LockReason.Contains("Keep stale locked entry", StringComparison.Ordinal) &&
              pruneVm.CanConfirm,
            "PruneWorktrees dry-run preview lists two stale candidates alongside retained locked missing record");

        Screenshot(window, "worktree-prune-preview-dark-narrow");

        ClickButtonByText(window, App.Text("PruneWorktrees.ConfirmBtn"));
        Await(() => mainPage.Popup == null);
        var porcelainAfterPrune = Git(clone1, "worktree", "list", "--porcelain");
        Check(!porcelainAfterPrune.Contains("wt-missing-a", StringComparison.OrdinalIgnoreCase) &&
              !porcelainAfterPrune.Contains("wt-missing-b", StringComparison.OrdinalIgnoreCase) &&
              porcelainAfterPrune.Contains("wt-missing-locked", StringComparison.OrdinalIgnoreCase),
            "Confirmed prune cleans unlocked stale worktrees while retaining locked worktree record");

        window.Close();
        _window = null;
        File.WriteAllText(probeGlobalCfg, string.Empty, utf8NoBom);
    }
}
