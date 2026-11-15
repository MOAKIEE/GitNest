using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.VisualTree;

using SourceGit;
using SourceGit.Commands;
using SourceGit.Models;
using VM = SourceGit.ViewModels;
using V = SourceGit.Views;

internal static partial class Program
{
    static partial void RunResetRewrite()
    {
        var utf8NoBom = new UTF8Encoding(false);
        var suiteRoot = Path.Combine(_root, "reset-rewrite-probe");
        Directory.CreateDirectory(suiteRoot);

        var probeGlobalCfg = Path.Combine(suiteRoot, "probe-global.gitconfig");
        File.WriteAllText(probeGlobalCfg, string.Empty, utf8NoBom);
        Environment.SetEnvironmentVariable("GIT_CONFIG_GLOBAL", probeGlobalCfg);
        Environment.SetEnvironmentVariable("GIT_CONFIG_NOSYSTEM", "1");

        // 1. Bare origin remote + peer collaborator clone + local main clone
        var remoteDir = Path.Combine(suiteRoot, "origin.git");
        Git(suiteRoot, "init", "--bare", remoteDir);
        Git(remoteDir, "symbolic-ref", "HEAD", "refs/heads/main");

        var seedDir = Path.Combine(suiteRoot, "seed");
        Git(suiteRoot, "clone", remoteDir, seedDir);
        Git(seedDir, "checkout", "-b", "main");
        Git(seedDir, "config", "--local", "user.name", "Local Alice");
        Git(seedDir, "config", "--local", "user.email", "alice@example.invalid");

        File.WriteAllText(Path.Combine(seedDir, "app.txt"), "base-v1\n", utf8NoBom);
        Git(seedDir, "add", ".");
        Git(seedDir, "commit", "-m", "c1: initial project baseline");
        var c1Sha = Git(seedDir, "rev-parse", "HEAD").Trim();

        // Side branch authored by peer -> merged back to main (cross-merge + multi-author)
        Git(seedDir, "checkout", "-b", "feat/side");
        Git(seedDir, "config", "--local", "user.name", "Peer Bob");
        Git(seedDir, "config", "--local", "user.email", "bob@example.invalid");
        File.WriteAllText(Path.Combine(seedDir, "side.txt"), "peer bob feature work\n", utf8NoBom);
        Git(seedDir, "add", "side.txt");
        Git(seedDir, "commit", "-m", "c2: bob adds side feature");

        Git(seedDir, "checkout", "main");
        Git(seedDir, "config", "--local", "user.name", "Local Alice");
        Git(seedDir, "config", "--local", "user.email", "alice@example.invalid");
        Git(seedDir, "merge", "--no-ff", "feat/side", "-m", "m3: merge feat/side into main");

        File.WriteAllText(Path.Combine(seedDir, "collision.txt"), "committed payload\n", utf8NoBom);
        Git(seedDir, "add", "collision.txt");
        Git(seedDir, "commit", "-m", "c4: add collision.txt on main");

        File.WriteAllText(Path.Combine(seedDir, "app.txt"), "base-v1\nline-5\n", utf8NoBom);
        Git(seedDir, "add", "app.txt");
        Git(seedDir, "commit", "-m", "c5: latest commit on main");
        Git(seedDir, "push", "-u", "origin", "main");

        // Unprotected feature branch pushed to origin for live lease-rejection & rewrite tests
        Git(seedDir, "checkout", "-b", "feat/rewrite", c1Sha);
        File.WriteAllText(Path.Combine(seedDir, "feat.txt"), "f1\n", utf8NoBom);
        Git(seedDir, "add", "feat.txt");
        Git(seedDir, "commit", "-m", "f1: initial feature checkpoint");
        var f1Sha = Git(seedDir, "rev-parse", "HEAD").Trim();

        File.WriteAllText(Path.Combine(seedDir, "feat.txt"), "f1\nf2\n", utf8NoBom);
        Git(seedDir, "add", "feat.txt");
        Git(seedDir, "commit", "-m", "f2: second feature commit");

        File.WriteAllText(Path.Combine(seedDir, "feat.txt"), "f1\nf2\nf3\n", utf8NoBom);
        Git(seedDir, "add", "feat.txt");
        Git(seedDir, "commit", "-m", "f3: third feature commit");
        var f3Sha = Git(seedDir, "rev-parse", "HEAD").Trim();
        Git(seedDir, "push", "-u", "origin", "feat/rewrite");

        // Busy branch checked out in secondary worktree
        Git(seedDir, "checkout", "-b", "wt/busy", c1Sha);
        Git(seedDir, "push", "-u", "origin", "wt/busy");

        // Clean pushed branch for lease-ready RewriteRemoteHistory popup screenshot
        Git(seedDir, "checkout", "-b", "feat/ready-rewrite", c1Sha);
        File.WriteAllText(Path.Combine(seedDir, "ready.txt"), "r1\n", utf8NoBom);
        Git(seedDir, "add", "ready.txt");
        Git(seedDir, "commit", "-m", "r1: keep baseline");
        var r1Sha = Git(seedDir, "rev-parse", "HEAD").Trim();
        File.WriteAllText(Path.Combine(seedDir, "ready.txt"), "r1\nr2\n", utf8NoBom);
        Git(seedDir, "add", "ready.txt");
        Git(seedDir, "commit", "-m", "r2: remote commit to rewrite away");
        Git(seedDir, "push", "-u", "origin", "feat/ready-rewrite");

        // Primary local repository clone + peer clone
        var workDir = Path.Combine(suiteRoot, "work-repo");
        Git(suiteRoot, "clone", remoteDir, workDir);
        Git(workDir, "config", "--local", "user.name", "Local Alice");
        Git(workDir, "config", "--local", "user.email", "alice@example.invalid");
        Git(workDir, "checkout", "-B", "feat/rewrite", "origin/feat/rewrite");
        Git(workDir, "checkout", "-B", "feat/ready-rewrite", "origin/feat/ready-rewrite");
        Git(workDir, "reset", "--hard", r1Sha);
        Git(workDir, "checkout", "main");
        Git(workDir, "remote", "set-head", "origin", "main");

        var busyWtDir = Path.Combine(suiteRoot, "busy-worktree");
        Git(workDir, "worktree", "add", "-b", "wt/busy", busyWtDir, "origin/wt/busy");

        var peerDir = Path.Combine(suiteRoot, "peer-clone");
        Git(suiteRoot, "clone", remoteDir, peerDir);
        Git(peerDir, "config", "--local", "user.name", "Collaborator Carol");
        Git(peerDir, "config", "--local", "user.email", "carol@example.invalid");
        Git(peerDir, "checkout", "-B", "feat/rewrite", "origin/feat/rewrite");

        App.SetTheme("Light", string.Empty);

        var journalStore = new OperationJournalStore(Path.Combine(suiteRoot, "journal.json"));
        var launcher = _launcher = new VM.Launcher(workDir);
        typeof(App).GetField("_launcher", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(Application.Current, launcher);

        var host = new ContentControl
        {
            DataContext = launcher,
            [!ContentControl.ContentProperty] = new Binding("ActivePage"),
            ContentTemplate = new FuncDataTemplate<VM.LauncherPage>((_, _) => new V.LauncherPage()),
        };

        var window = _window = new Window
        {
            Content = host,
            Width = 1140,
            Height = 780,
            ShowInTaskbar = false,
            Title = "GitNest 3.6b Reset & Remote Rewrite UI Probe",
        };
        window.Show();

        var normalizedWork = workDir.Replace('\\', '/');
        Await(() => launcher.ActivePage?.Data is VM.Repository r &&
                    string.Equals(r.FullPath.Replace('\\', '/'), normalizedWork, StringComparison.OrdinalIgnoreCase) &&
                    r.CurrentBranch != null &&
                    r.Branches.Any(b => b.IsLocal && b.Name == "feat/rewrite"));

        var page = launcher.ActivePage;
        var repo = (VM.Repository)page.Data;
        var c1Commit = new QuerySingleCommit(workDir, c1Sha).GetResultAsync().GetAwaiter().GetResult()!;
        var f1Commit = new QuerySingleCommit(workDir, f1Sha).GetResultAsync().GetAwaiter().GetResult()!;

        // === Step A: Untracked file collision blocks Reset popup ===
        // On main, c4 added collision.txt; returning to c1 drops collision.txt, so place an untracked collision file?
        // Actually going from HEAD (where collision.txt is tracked) to c1 removes tracked collision.txt;
        // let's test going from c1 -> main or untracked file vs commit diff:
        // Wait: when resetting from HEAD to c1, collision.txt exists in HEAD. If instead we delete collision.txt from HEAD...
        // Even simpler: c5 added app.txt edits, what about an untracked file matching a file DIFFERENT between HEAD and target when HEAD does NOT track it or untracked collides with target:
        // Wait: in main, switch to feat/rewrite (does not have collision.txt), place untracked collision.txt, and try resetting feat/rewrite? No, target is c4Commit on main? Target must be first-parent of HEAD.
        // Instead, on main, delete collision.txt in index? No, clean working tree has collision.txt. Wait: in HistoryResetService, untracked collision hits when HEAD does NOT have path P (or P differs) AND target has P or diff changes it while untracked P exists on disk! Let's create a commit c6 that removes untracked-clash.txt, wait: HEAD deleted clash.txt in c6, target c5 has clash.txt, user places untracked clash.txt on disk!
        File.WriteAllText(Path.Combine(workDir, "clash.txt"), "tracked in c5b\n", utf8NoBom);
        Git(workDir, "add", "clash.txt");
        Git(workDir, "commit", "-m", "c5b: add clash.txt");
        var c5bSha = Git(workDir, "rev-parse", "HEAD").Trim();
        var c5bCommit = new QuerySingleCommit(workDir, c5bSha).GetResultAsync().GetAwaiter().GetResult()!;
        Git(workDir, "rm", "clash.txt");
        Git(workDir, "commit", "-m", "c6: delete clash.txt");
        File.WriteAllText(Path.Combine(workDir, "clash.txt"), "user untracked file\n", utf8NoBom);

        var collisionVm = new VM.Reset(repo, repo.CurrentBranch, c5bCommit, journalStore);
        page.Popup = collisionVm;
        Await(() => !collisionVm.IsLoadingPreflight && collisionVm.HasPreflight);
        Check(!collisionVm.CanConfirm && collisionVm.HasBlockReason && collisionVm.CollisionPaths.Contains("clash.txt"),
            $"Untracked collision blocks Reset popup [canConfirm={collisionVm.CanConfirm}, collisions={string.Join(",", collisionVm.CollisionPaths)}]");
        Screenshot(window, "reset-collision-blocked-light");
        ClickButtonByText(window, App.Text("Cancel"));
        Await(() => page.Popup == null);

        // Clean probe commits clash.txt back to c5 on main
        File.Delete(Path.Combine(workDir, "clash.txt"));
        Git(workDir, "reset", "--hard", "origin/main");

        // === Step B: Cross-merge + multi-author warning + protected main lock + strictly 2 modes ===
        var mergeProtectedVm = new VM.Reset(repo, repo.CurrentBranch, c1Commit, journalStore);
        page.Popup = mergeProtectedVm;
        Await(() => !mergeProtectedVm.IsLoadingPreflight && mergeProtectedVm.HasPreflight);
        var modeRadios = window.GetVisualDescendants().OfType<RadioButton>().ToList();
        Check(modeRadios.Count == 2 &&
              mergeProtectedVm.HasCrossMergeNotice &&
              mergeProtectedVm.HasOtherAuthorsInLocal &&
              mergeProtectedVm.IsProtectionEffectivelyLocked &&
              !mergeProtectedVm.CanEnableAlsoRewriteRemote,
            $"Reset exposes 2 modes only, cross-merge & multi-author warnings, and locks remote rewrite on protected main [radios={modeRadios.Count}, crossMerge={mergeProtectedVm.HasCrossMergeNotice}, multiAuthor={mergeProtectedVm.HasOtherAuthorsInLocal}]");
        Screenshot(window, "reset-merge-protected-light");
        App.SetTheme("Dark", string.Empty);
        Pump();
        Screenshot(window, "reset-merge-protected-dark");
        App.SetTheme("Light", string.Empty);
        ClickButtonByText(window, App.Text("Cancel"));
        Await(() => page.Popup == null);

        // === Step C: Local Reset + Concurrent Remote Rejection -> "本地已撤回，远端未更新" non-auto-closing card ===
        Git(workDir, "checkout", "feat/rewrite");
        repo.RefreshBranches();
        Await(() => repo.CurrentBranch?.Name == "feat/rewrite");

        var partialVm = new VM.Reset(repo, repo.CurrentBranch, f1Commit, journalStore);
        page.Popup = partialVm;
        Await(() => !partialVm.IsLoadingPreflight && partialVm.HasPreflight && partialVm.CanEnableAlsoRewriteRemote);
        partialVm.AlsoRewriteRemote = true;
        Pump();

        // Simulate collaborator pushing a newer commit onto origin/feat/rewrite right before confirm!
        File.WriteAllText(Path.Combine(peerDir, "carol-work.txt"), "carol important work\n", utf8NoBom);
        Git(peerDir, "add", "carol-work.txt");
        Git(peerDir, "commit", "-m", "carol: concurrent shared commit");
        Git(peerDir, "push", "origin", "feat/rewrite");
        var carolTip = Git(peerDir, "rev-parse", "HEAD").Trim();

        window.Width = 780;
        window.Height = 720;
        ClickButtonByText(window, App.Text("Reset.ConfirmWithRemote"));
        Await(() => partialVm.HasResultView);
        Check(page.Popup == partialVm &&
              partialVm.LastOutcome is { IsPartialSuccess: true, CollaboratorCommitsPreserved: true, LocalBranchReset: true, RemoteBranchRewritten: false } &&
              partialVm.ResultTitle == App.Text("Reset.Result.PartialTitle") &&
              partialVm.HasResultBackupRef &&
              Git(workDir, "rev-parse", "HEAD").Trim() == f1Sha &&
              Git(remoteDir, "rev-parse", "refs/heads/feat/rewrite").Trim() == carolTip,
            $"Reset partial failure keeps popup open with '本地已撤回，远端未更新', preserves collaborator tip & creates backup ref");
        Screenshot(window, "reset-partial-failure-light-narrow");
        App.SetTheme("Dark", string.Empty);
        Pump();
        Screenshot(window, "reset-partial-failure-dark-narrow");
        ClickButtonByText(window, App.Text("Revert.CloseResult"));
        Await(() => page.Popup == null);

        window.Width = 1140;
        window.Height = 780;
        App.SetTheme("Light", string.Empty);

        // === Step D: RepositoryConfigure Protected Branch Overrides + RewriteRemoteHistory popup ===
        Check(BranchProtectionPolicy.IsBranchEffectivelyProtectedFast(repo.GitCommonDir, repo.Settings, "main", "main"),
            "BranchProtectionPolicy fast check blocks main before repository unlock");

        var cfgVm = new VM.RepositoryConfigure(repo);
        var cfgWin = new V.RepositoryConfigure { DataContext = cfgVm };
        cfgWin.Show();
        Await(() => !cfgVm.IsLoading && cfgVm.ProtectedBranches.Any(b => b.BranchName == "main"));
        cfgWin.FindControl<ScrollViewer>("GitConfigScroller")?.ScrollToEnd();
        Pump();
        Screenshot(cfgWin, "configure-branch-protection-light");

        var mainProtectItem = cfgVm.ProtectedBranches.First(b => b.BranchName == "main");
        mainProtectItem.AllowDestructiveHistoryRewrite = true;
        Pump();
        Check(cfgVm.PendingChangesCount >= 1, "Toggling branch protection increments pending changes count");
        ClickButtonByText(cfgWin, App.Text("Configure.Actions.Save"));
        Await(() => !cfgVm.IsSaving && !mainProtectItem.HasPendingChange);
        Check(!BranchProtectionPolicy.IsBranchEffectivelyProtectedFast(repo.GitCommonDir, repo.Settings, "main", "main"),
            "Saving branch protection override in RepositoryConfigure immediately unlocks main locally");
        // Re-lock main immediately after verifying toggle save so probe ends cleanly
        var reloadedMainProtectItem = cfgVm.ProtectedBranches.First(b => b.BranchName == "main");
        reloadedMainProtectItem.AllowDestructiveHistoryRewrite = false;
        ClickButtonByText(cfgWin, App.Text("Configure.Actions.Save"));
        Await(() => !cfgVm.IsSaving && !cfgVm.ProtectedBranches.First(b => b.BranchName == "main").HasPendingChange);
        Check(BranchProtectionPolicy.IsBranchEffectivelyProtectedFast(repo.GitCommonDir, repo.Settings, "main", "main"),
            "Re-locking main in RepositoryConfigure immediately restores protection");
        cfgWin.Close();

        // RewriteRemoteHistory offline ("远端状态未知") vs ready state
        var origUrl = Git(workDir, "remote", "get-url", "origin").Trim();
        Git(workDir, "remote", "set-url", "origin", Path.Combine(suiteRoot, "missing-offline.git"));
        var featBranch = repo.Branches.First(b => b.IsLocal && b.Name == "feat/rewrite");
        var offlineRewriteVm = new VM.RewriteRemoteHistory(repo, featBranch, f1Commit);
        page.Popup = offlineRewriteVm;
        Await(() => !offlineRewriteVm.IsLoadingPreflight && offlineRewriteVm.HasPreflight);
        Check(!offlineRewriteVm.CanConfirm && offlineRewriteVm.IsRemoteStateUnknown,
            "RewriteRemoteHistory blocks when remote is unreachable (远端状态未知)");
        App.SetTheme("Dark", string.Empty);
        Pump();
        Screenshot(window, "rewrite-remote-unknown-dark");
        ClickButtonByText(window, App.Text("Cancel"));
        Await(() => page.Popup == null);

        Git(workDir, "remote", "set-url", "origin", origUrl);
        App.SetTheme("Light", string.Empty);
        var readyBranch = repo.Branches.First(b => b.IsLocal && b.Name == "feat/ready-rewrite");
        var readyRewriteVm = new VM.RewriteRemoteHistory(repo, readyBranch);
        page.Popup = readyRewriteVm;
        Await(() => !readyRewriteVm.IsLoadingPreflight && readyRewriteVm.HasPreflight);
        Check(readyRewriteVm.CanConfirm && readyRewriteVm.HasPinnedLeaseNote && readyRewriteVm.HasCommitsToRemove,
            "RewriteRemoteHistory enables confirm with pinned lease and remote commit preview when eligible");
        Screenshot(window, "rewrite-remote-ready-light");
        ClickButtonByText(window, App.Text("Cancel"));
        Await(() => page.Popup == null);

        // === Step E: ResetWithoutCheckout worktree block + PushRevision protected force block ===
        // Verify Step C KeepCodeMixed actually kept feat.txt modifications in working tree, then discard probe edit before switching branch
        Check(Git(workDir, "status", "--porcelain").Contains("feat.txt", StringComparison.Ordinal),
            "KeepCodeMixed (--mixed) preserves reverted commit edits in working tree");
        Git(workDir, "checkout", "--", "feat.txt");
        Git(workDir, "checkout", "main");
        repo.RefreshBranches();
        Await(() => repo.CurrentBranch?.Name == "main");

        var busyBranch = repo.Branches.First(b => b.IsLocal && b.Name == "wt/busy");
        var rwcVm = new VM.ResetWithoutCheckout(repo, busyBranch, c1Commit, journalStore);
        page.Popup = rwcVm;
        Await(() => !rwcVm.IsLoadingPreflight && rwcVm.HasPreflight);
        Check(!rwcVm.CanConfirm && rwcVm.HasBlockReason,
            "ResetWithoutCheckout blocks moving branch currently checked out in another worktree");
        Screenshot(window, "reset-without-checkout-blocked-light");
        ClickButtonByText(window, App.Text("Cancel"));
        Await(() => page.Popup == null);

        var remoteMainBranch = repo.Branches.First(b => !b.IsLocal && b.Remote == "origin" && b.Name == "main");
        var pushRevVm = new VM.PushRevision(repo, c1Commit, remoteMainBranch);
        page.Popup = pushRevVm;
        pushRevVm.Force = true;
        Await(() => pushRevVm.EnsureForceGuardsLoadedAsync().IsCompleted && !pushRevVm.IsCheckingForceGuards);
        Pump();
        Check(!pushRevVm.CanConfirm && pushRevVm.HasForceBlockMessage,
            "PushRevision blocks forced overwrite against locked protected branch");
        App.SetTheme("Dark", string.Empty);
        Pump();
        Screenshot(window, "push-revision-protected-dark");
        ClickButtonByText(window, App.Text("Cancel"));
        Await(() => page.Popup == null);

        // === Step F: ViewLogs online verification & checked reset backup restoration ===
        Git(workDir, "checkout", "feat/rewrite");
        File.WriteAllText(Path.Combine(workDir, "after-reset.txt"), "new commit after reset\n", utf8NoBom);
        Git(workDir, "add", "after-reset.txt");
        Git(workDir, "commit", "-m", "post-reset newer commit");

        var logsVm = new VM.ViewLogs(repo, focusRecoveryTab: true, journalStore: journalStore);
        var logsWin = new V.ViewLogs { DataContext = logsVm };
        logsWin.Show();
        Await(() => !logsVm.IsBusy && logsVm.Operations.Count > 0 && !logsVm.IsInspectingResetBackup);

        // Pick the PartialSuccess reset+rewrite record
        var partialRecordItem = logsVm.Operations.First(o => o.Record.OperationId == partialVm.LastOutcome.OperationId);
        logsVm.SelectedOperation = partialRecordItem;
        Await(() => logsVm.EnsureResetBackupInspectedAsync().IsCompleted && !logsVm.IsInspectingResetBackup);
        Pump();

        Check(logsVm.CanVerifyRemoteSelectedOperation &&
              logsVm.IsSelectedResetBackup &&
              logsVm.HasRestoreNewerCommitsWarning &&
              !logsVm.CanSelectRestoreExistingBranch &&
              logsVm.RestoreModeNewBranch &&
              logsVm.RestoreTargetBranch.StartsWith("recover/", StringComparison.Ordinal),
            $"ViewLogs reset backup inspection enforces safe new recovery branch when newer commits exist [target='{logsVm.RestoreTargetBranch}', canOverwrite={logsVm.CanSelectRestoreExistingBranch}]");

        ClickButtonByText(logsWin, App.Text("OperationRecovery.VerifyRemoteSelected"));
        Await(() => !logsVm.IsBusy &&
                    logsVm.EnsureResetBackupInspectedAsync().IsCompleted &&
                    !logsVm.IsInspectingResetBackup &&
                    logsVm.HasFeedbackMessage &&
                    logsVm.RestoreTargetBranch.StartsWith("recover/", StringComparison.Ordinal));
        Pump();
        Screenshot(logsWin, "viewlogs-reset-recovery-newer-commits-dark");

        logsWin.FindControl<ScrollViewer>("OperationDetailsScroller")?.ScrollToEnd();
        Pump();
        Screenshot(logsWin, "viewlogs-reset-recovery-card-dark");

        App.SetTheme("Light", string.Empty);
        Pump();
        Screenshot(logsWin, "viewlogs-reset-recovery-card-light");
        logsWin.FindControl<ScrollViewer>("OperationDetailsScroller")?.ScrollToHome();
        Pump();
        Screenshot(logsWin, "viewlogs-reset-recovery-newer-commits-light");

        var expectedRecoverBranch = logsVm.RestoreTargetBranch;
        ClickButtonByText(logsWin, App.Text("OperationRecovery.RestoreBackupBtn"));
        Await(() => !logsVm.IsBusy && Git(workDir, "branch", "--list", expectedRecoverBranch).Trim().Length > 0);
        var recoveredSha = Git(workDir, "rev-parse", expectedRecoverBranch).Trim();
        Check(recoveredSha == f3Sha,
            $"ViewLogs checked backup restore creates recovery branch at exact pre-reset SHA [expected={f3Sha}, actual={recoveredSha}]");

        logsWin.Close();

        // Exercise the history-menu push entry, including changes after force confirmation.
        Git(workDir, "checkout", "-b", "feat/push-revision", c1Sha);
        Git(workDir, "push", "-u", "origin", "feat/push-revision");
        File.WriteAllText(Path.Combine(workDir, "revision.txt"), "selected\n", utf8NoBom);
        Git(workDir, "add", "revision.txt");
        Git(workDir, "commit", "-m", "selected revision");
        var selectedSha = Git(workDir, "rev-parse", "HEAD").Trim();
        File.AppendAllText(Path.Combine(workDir, "revision.txt"), "later commit\n", utf8NoBom);
        Git(workDir, "commit", "-am", "later revision");
        var laterSha = Git(workDir, "rev-parse", "HEAD").Trim();
        Git(workDir, "config", "push.followTags", "true");
        Git(workDir, "tag", "-a", "private-base", "-m", "unselected tag", c1Sha);
        repo.RefreshBranches();
        Await(() => repo.CurrentBranch?.Name == "feat/push-revision" &&
                    repo.Branches.Any(b => !b.IsLocal && b.Name == "feat/push-revision"));
        var revisionTarget = repo.Branches.First(b => !b.IsLocal && b.Name == "feat/push-revision");
        var selectedCommit = new QuerySingleCommit(workDir, selectedSha).GetResultAsync().GetAwaiter().GetResult()!;
        var revisionVm = new VM.PushRevision(repo, selectedCommit, revisionTarget);
        page.Popup = revisionVm;
        var revisionTask = revisionVm.Sure();
        Await(() => revisionTask.IsCompleted);
        Check(revisionTask.GetAwaiter().GetResult() &&
              Git(remoteDir, "rev-parse", "refs/heads/feat/push-revision").Trim() == selectedSha &&
              string.IsNullOrWhiteSpace(Git(remoteDir, "tag", "--list")),
            "PushRevision publishes the selected commit through safe single-branch push without followTags");
        page.Popup = null;

        var otherPushDir = Path.Combine(suiteRoot, "other-push.git");
        Git(suiteRoot, "init", "--bare", otherPushDir);
        Git(workDir, "push", "--no-follow-tags", otherPushDir, $"{selectedSha}:refs/heads/feat/push-revision");
        Git(workDir, "remote", "set-url", "--push", "origin", remoteDir);
        Git(workDir, "remote", "set-url", "--push", "--add", "origin", otherPushDir);
        var multiRevisionVm = new VM.PushRevision(repo, selectedCommit, revisionTarget);
        var multiRevisionTask = multiRevisionVm.Sure();
        Await(() => multiRevisionTask.IsCompleted);
        Check(!multiRevisionTask.GetAwaiter().GetResult() &&
              Git(remoteDir, "rev-parse", "refs/heads/feat/push-revision").Trim() == selectedSha &&
              Git(otherPushDir, "rev-parse", "refs/heads/feat/push-revision").Trim() == selectedSha,
            "PushRevision rejects multiple push URLs without changing either server");
        Git(workDir, "config", "--unset-all", "remote.origin.pushurl");

        var changedRevisionVm = new VM.PushRevision(repo, c1Commit, revisionTarget) { Force = true };
        Await(() => changedRevisionVm.EnsureForceGuardsLoadedAsync().IsCompleted && !changedRevisionVm.IsCheckingForceGuards);
        Git(workDir, "remote", "set-url", "--push", "origin", otherPushDir);
        var changedRevisionTask = changedRevisionVm.Sure();
        Await(() => changedRevisionTask.IsCompleted);
        Check(!changedRevisionTask.GetAwaiter().GetResult() &&
              Git(remoteDir, "rev-parse", "refs/heads/feat/push-revision").Trim() == selectedSha &&
              Git(otherPushDir, "rev-parse", "refs/heads/feat/push-revision").Trim() == selectedSha,
            "PushRevision refuses an endpoint changed after force confirmation");
        Git(workDir, "config", "--unset-all", "remote.origin.pushurl");

        var protectedRevisionVm = new VM.PushRevision(repo, c1Commit, revisionTarget) { Force = true };
        Await(() => protectedRevisionVm.EnsureForceGuardsLoadedAsync().IsCompleted && !protectedRevisionVm.IsCheckingForceGuards);
        Git(workDir, "symbolic-ref", "refs/remotes/origin/HEAD", "refs/remotes/origin/feat/push-revision");
        var protectedRevisionTask = protectedRevisionVm.Sure();
        Await(() => protectedRevisionTask.IsCompleted);
        Check(!protectedRevisionTask.GetAwaiter().GetResult() &&
              Git(remoteDir, "rev-parse", "refs/heads/feat/push-revision").Trim() == selectedSha,
            "PushRevision rechecks default-branch protection after confirmation");
        Git(workDir, "symbolic-ref", "refs/remotes/origin/HEAD", "refs/remotes/origin/main");

        var safeForceVm = new VM.PushRevision(repo, c1Commit, revisionTarget) { Force = true };
        Await(() => safeForceVm.EnsureForceGuardsLoadedAsync().IsCompleted && !safeForceVm.IsCheckingForceGuards);
        var safeForceTask = safeForceVm.Sure();
        Await(() => safeForceTask.IsCompleted);
        Check(safeForceTask.GetAwaiter().GetResult() &&
              Git(remoteDir, "rev-parse", "refs/heads/feat/push-revision").Trim() == c1Sha &&
              Git(workDir, "rev-parse", "HEAD").Trim() == laterSha &&
              Git(workDir, "for-each-ref", "--format=%(objectname)", "refs/gitnest/backups").Contains(selectedSha, StringComparison.Ordinal) &&
              string.IsNullOrWhiteSpace(Git(remoteDir, "tag", "--list")),
            "PushRevision force uses lease rewrite with a recovery ref and preserves the local branch and private tags");
        window.Close();
    }
}
