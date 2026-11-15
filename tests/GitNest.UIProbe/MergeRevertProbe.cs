using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

using SourceGit;
using SourceGit.Models;
using OS = SourceGit.Native.OS;
using VM = SourceGit.ViewModels;
using Views = SourceGit.Views;

internal static partial class Program
{
    static partial void RunMergeRevert()
    {
        var utf8NoBom = new UTF8Encoding(false);
        var suiteRoot = Path.Combine(_root, "merge-revert-probe");
        Directory.CreateDirectory(suiteRoot);

        var probeGlobalCfg = Path.Combine(suiteRoot, "probe-global.gitconfig");
        File.WriteAllText(probeGlobalCfg, string.Empty, utf8NoBom);
        Environment.SetEnvironmentVariable("GIT_CONFIG_GLOBAL", probeGlobalCfg);
        Environment.SetEnvironmentVariable("GIT_CONFIG_NOSYSTEM", "1");

        var originBare = Path.Combine(suiteRoot, "origin.git");
        var repoPath = Path.Combine(suiteRoot, "repo");

        Git(suiteRoot, "init", "--bare", "-b", "main", originBare);
        Git(suiteRoot, "clone", originBare, repoPath);
        Git(repoPath, "config", "--local", "user.name", "Probe Alice");
        Git(repoPath, "config", "--local", "user.email", "alice@example.invalid");

        File.WriteAllText(Path.Combine(repoPath, "shared.txt"), "line1 base\nline2 keep\n", utf8NoBom);
        File.WriteAllText(Path.Combine(repoPath, "normal.txt"), "normal initial\n", utf8NoBom);
        Git(repoPath, "add", ".");
        Git(repoPath, "commit", "-m", "initial commit");
        Git(repoPath, "branch", "-M", "main");
        Git(repoPath, "push", "-u", "origin", "main");
        var baseSha = Git(repoPath, "rev-parse", "HEAD").Trim();

        // feat/clean branch (diverges cleanly with main later)
        Git(repoPath, "checkout", "-b", "feat/clean", baseSha);
        File.WriteAllText(Path.Combine(repoPath, "feat.txt"), "clean feature content\n", utf8NoBom);
        Git(repoPath, "add", "feat.txt");
        Git(repoPath, "commit", "-m", "feat: add clean module");
        var featCleanTip = Git(repoPath, "rev-parse", "HEAD").Trim();

        // feat/conflict branch (modifies line 1 of shared.txt differently from main)
        Git(repoPath, "checkout", "-b", "feat/conflict", baseSha);
        File.WriteAllText(Path.Combine(repoPath, "shared.txt"), "line1 from conflict branch\nline2 keep\n", utf8NoBom);
        Git(repoPath, "add", "shared.txt");
        Git(repoPath, "commit", "-m", "feat: incompatible line1 edit");

        // side/topic branch for a 2-parent merge commit on main (for Revert merge-commit block test)
        Git(repoPath, "checkout", "-b", "side/topic", baseSha);
        File.WriteAllText(Path.Combine(repoPath, "topic.txt"), "topic work\n", utf8NoBom);
        Git(repoPath, "add", "topic.txt");
        Git(repoPath, "commit", "-m", "side: topic commit");
        var sideTopicTip = Git(repoPath, "rev-parse", "HEAD").Trim();

        // main advances with a single normal commit, a separate shared.txt edit, and a 2-parent merge commit
        Git(repoPath, "checkout", "main");
        File.WriteAllText(Path.Combine(repoPath, "normal.txt"), "normal v2\n", utf8NoBom);
        Git(repoPath, "add", "normal.txt");
        Git(repoPath, "commit", "-m", "main: update normal file");
        var normalCommitSha = Git(repoPath, "rev-parse", "HEAD").Trim();

        File.WriteAllText(Path.Combine(repoPath, "shared.txt"), "line1 from main\nline2 keep\n", utf8NoBom);
        Git(repoPath, "add", "shared.txt");
        Git(repoPath, "commit", "-m", "main: incompatible shared line1");

        Git(repoPath, "merge", "--no-ff", "--no-edit", sideTopicTip);
        var mergeCommitSha = Git(repoPath, "rev-parse", "HEAD").Trim();
        Git(repoPath, "push", "origin", "main");
        var originMainBeforeMerge = Git(originBare, "rev-parse", "refs/heads/main").Trim();

        // Start checked out on a separate clean branch so directed target switching -> main is also exercised
        Git(repoPath, "checkout", "-b", "work/view-branch", "main");

        App.SetTheme("Light", string.Empty);

        var launcher = _launcher = new VM.Launcher(repoPath);
        typeof(App).GetField("_launcher", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(Avalonia.Application.Current, launcher);

        var mainPage = launcher.ActivePage;
        var repo = (VM.Repository)mainPage.Data;
        var host = new ContentControl
        {
            DataContext = launcher,
            [!ContentControl.ContentProperty] = new Binding("ActivePage"),
            ContentTemplate = new FuncDataTemplate<VM.LauncherPage>((_, _) => new Views.LauncherPage()),
        };

        var window = _window = new Window
        {
            Content = host,
            Width = 1120,
            Height = 760,
            ShowInTaskbar = false,
            Title = "GitNest 3.4 合并、撤销与只读冲突 UI 探针",
        };
        window.Show();

        Await(() => repo.Branches.Any(b => b.IsLocal && b.Name == "feat/clean") &&
                    repo.CurrentBranch != null);

        var normalCommit = new SourceGit.Commands.QuerySingleCommit(repoPath, normalCommitSha).GetResult()!;
        var mergeCommit = new SourceGit.Commands.QuerySingleCommit(repoPath, mergeCommitSha).GetResult()!;

        // 1. Directed Merge Dialog (Light): target switched cleanly to main, incoming commits & files loaded, no conflicts
        var featCleanBranch = repo.Branches.First(b => b.IsLocal && b.Name == "feat/clean");
        var mergeCleanVm = new VM.Merge(repo, featCleanBranch, repo.CurrentBranch!.Name, false);
        mainPage.Popup = mergeCleanVm;
        Await(() => !mergeCleanVm.IsLoadingPreflight && mergeCleanVm.HasPreflight);
        mergeCleanVm.Into = "main";
        Await(() => !mergeCleanVm.IsLoadingPreflight &&
                    mergeCleanVm.HasPreflight &&
                    mergeCleanVm.Into == "main" &&
                    mergeCleanVm.TestingState == VM.MergeTestingState.NoConflicts);

        Check(mergeCleanVm.CanConfirm &&
              mergeCleanVm.NeedsCheckoutNotice &&
              mergeCleanVm.IncomingCommits.Any(c => c.ShortSha == featCleanTip[..8]) &&
              mergeCleanVm.IncomingChanges.Any(f => f.Path == "feat.txt") &&
              window.GetVisualDescendants().OfType<TextBlock>()
                  .Any(tb => tb.IsEffectivelyVisible &&
                             (tb.Text?.Contains("将【feat/clean】的改动合并到【main】", StringComparison.Ordinal) ?? false)),
            "Directed Merge popup displays clear source->target direction, incoming commit/file preview, and non-mutating conflict preflight");

        Screenshot(window, "merge-directed-preview-light");

        ClickButtonByText(window, App.Text("Sure"));
        Await(() => mainPage.Popup == null &&
                    Git(repoPath, "rev-parse", "--abbrev-ref", "HEAD").Trim() == "main");

        var mainAfterMerge = Git(repoPath, "rev-parse", "refs/heads/main").Trim();
        Check(Git(repoPath, "rev-parse", "--abbrev-ref", "HEAD").Trim() == "main" &&
              Git(repoPath, "rev-parse", "refs/heads/feat/clean").Trim() == featCleanTip &&
              Git(originBare, "rev-parse", "refs/heads/main").Trim() == originMainBeforeMerge &&
              mainAfterMerge != originMainBeforeMerge,
            "Confirmed directed merge updates target branch main, keeps source branch unchanged, stays on main locally, and never auto-pushes");

        // 2. Merge Conflict Auto-Abort Keeps Result Card Open (Dark Narrow 780x700)
        App.SetTheme("Dark", string.Empty);
        window.Width = 780;
        window.Height = 700;
        Await(() => window.Bounds.Width <= 790);

        var featConflictBranch = repo.Branches.First(b => b.IsLocal && b.Name == "feat/conflict");
        var mergeConflictVm = new VM.Merge(repo, featConflictBranch, "main", false);
        mainPage.Popup = mergeConflictVm;
        Await(() => !mergeConflictVm.IsLoadingPreflight && mergeConflictVm.HasPreflight);
        Check(mergeConflictVm.CanConfirm &&
              mergeConflictVm.TestingState == VM.MergeTestingState.HasConflicts &&
              mergeConflictVm.PredictedConflictPaths.Contains("shared.txt", StringComparer.Ordinal),
            "Conflict prediction preflight detects exact colliding file before merge execution");

        ClickButtonByText(window, App.Text("Sure"));
        Await(() => mergeConflictVm.HasResultView && !mergeConflictVm.ShowConfirmButton);

        Check(mainPage.Popup == mergeConflictVm &&
              mergeConflictVm.ResultSummary.Contains("合并存在冲突，已取消，目标分支未合并", StringComparison.Ordinal) &&
              mergeConflictVm.ResultLines.Contains("shared.txt", StringComparer.Ordinal) &&
              !File.Exists(Path.Combine(repoPath, ".git", "MERGE_HEAD")) &&
              string.IsNullOrWhiteSpace(Git(repoPath, "status", "--porcelain")) &&
              Git(repoPath, "rev-parse", "refs/heads/main").Trim() == mainAfterMerge,
            "Clean merge conflict auto-aborts target cleanly, leaves popup open with explicit status summary, and keeps target commit unchanged");

        Screenshot(window, "merge-conflict-aborted-dark-narrow");

        ClickButtonByText(window, App.Text("Merge.CloseResult"));
        Await(() => mainPage.Popup == null);

        // 3. Revert Single Normal Commit (Light Wide 1120x760)
        App.SetTheme("Light", string.Empty);
        window.Width = 1120;
        window.Height = 760;
        Await(() => window.Bounds.Width >= 1100);

        var revertNormalVm = new VM.Revert(repo, normalCommit);
        mainPage.Popup = revertNormalVm;
        Await(() => !revertNormalVm.IsLoadingPreflight && revertNormalVm.HasPreflight);

        Check(!revertNormalVm.IsMergeCommitBlock &&
              revertNormalVm.CanConfirm &&
              revertNormalVm.AffectedFiles.Any(f => f.Path == "normal.txt"),
            "Single normal commit Revert preflight enables confirmation and lists affected files");

        Screenshot(window, "revert-normal-preview-light");

        ClickButtonByText(window, App.Text("Sure"));
        Await(() => mainPage.Popup == null &&
                    Git(repoPath, "rev-parse", "HEAD").Trim() != mainAfterMerge);

        var headAfterRevert = Git(repoPath, "rev-parse", "HEAD").Trim();
        Check(headAfterRevert != mainAfterMerge &&
              Git(repoPath, "rev-parse", "HEAD~1").Trim() == mainAfterMerge &&
              File.ReadAllText(Path.Combine(repoPath, "normal.txt")).Contains("normal initial", StringComparison.Ordinal),
            "Confirmed single-commit revert appends a reverse commit while preserving full prior history");

        // 4. Revert 2-Parent Merge Commit Block Guard (Dark Narrow 780x700)
        App.SetTheme("Dark", string.Empty);
        window.Width = 780;
        window.Height = 700;
        Await(() => window.Bounds.Width <= 790);

        var revertMergeVm = new VM.Revert(repo, mergeCommit);
        mainPage.Popup = revertMergeVm;
        Await(() => !revertMergeVm.IsLoadingPreflight && revertMergeVm.HasPreflight);

        Check(revertMergeVm.IsMergeCommitBlock &&
              !revertMergeVm.CanConfirm &&
              window.GetVisualDescendants().OfType<TextBlock>()
                  .Any(tb => tb.IsEffectivelyVisible &&
                             (tb.Text?.Contains("-m 1", StringComparison.Ordinal) ?? false)),
            "Multi-parent merge commit blocks Revert without guessing -m 1 mainline");

        Screenshot(window, "revert-merge-commit-blocked-dark-narrow");

        ClickButtonByText(window, App.Text("Revert.CloseResult"));
        Await(() => mainPage.Popup == null);

        // 5. External CLI Conflict Opened & Read-Only Banner + Conflict Panel + Refresh (Light 1120x760)
        App.SetTheme("Light", string.Empty);
        window.Width = 1120;
        window.Height = 760;
        Await(() => window.Bounds.Width >= 1100);
        mainPage.ClearNotifications();

        var (extExit, _, _) = RunGitAllowFailure(repoPath, "merge", "--no-edit", "feat/conflict");
        Check(extExit != 0 && File.Exists(Path.Combine(repoPath, ".git", "MERGE_HEAD")),
            "External CLI merge leaves real MERGE_HEAD and conflict markers before GitNest refreshes/opens");

        repo.MarkWorkingCopyDirtyManually();
        Await(() => repo.HasInProgressBanner &&
                    repo.InProgressOriginLabel == App.Text("WorkingCopy.Conflicts.Origin.External") &&
                    !repo.CanAbortInProgressFromBanner);

        Check(File.Exists(Path.Combine(repoPath, ".git", "MERGE_HEAD")) &&
              !repo.CanAbortInProgressFromBanner,
            "GitNest never auto-aborts external conflict, labels it as external, and hides direct Abort button");

        ClickButtonByText(window, App.Text("Repository.Resolve"));
        Await(() => repo.SelectedViewIndex == 1 &&
                    window.GetVisualDescendants().OfType<Views.Conflict>().Any(c => c.IsEffectivelyVisible));

        var conflictView = window.GetVisualDescendants().OfType<Views.Conflict>().First(c => c.IsEffectivelyVisible);
        var conflictVm = (VM.Conflict)conflictView.DataContext!;
        Await(() => conflictVm.IsExternalOrigin &&
                    conflictVm.ConflictedFiles.Any(f => f.Path == "shared.txt"));

        var forbiddenConflictButtons = conflictView.GetVisualDescendants()
            .OfType<Button>()
            .Where(b => b.IsEffectivelyVisible &&
                        (string.Equals(b.Content?.ToString(), App.Text("WorkingCopy.Conflicts.UseTheirs"), StringComparison.Ordinal) ||
                         string.Equals(b.Content?.ToString(), App.Text("WorkingCopy.Conflicts.UseMine"), StringComparison.Ordinal) ||
                         string.Equals(b.Content?.ToString(), App.Text("WorkingCopy.Conflicts.Merge"), StringComparison.Ordinal)))
            .ToList();

        Check(forbiddenConflictButtons.Count == 0 &&
              conflictVm.ShowExternalAbortDisabledNotice &&
              conflictVm.ConflictedFiles.Any(f => f.Path == "shared.txt"),
            "Read-only Conflict panel displays external origin badge and conflicted file list without internal editor or UseMine/UseTheirs buttons");

        ClickButtonByText(conflictView, App.Text("Repository.RefreshStatus"));
        Await(() =>
        {
            var currentConflict = window.GetVisualDescendants().OfType<Views.Conflict>().FirstOrDefault(c => c.IsEffectivelyVisible);
            return currentConflict?.DataContext is VM.Conflict vm &&
                   !vm.IsRefreshing &&
                   vm.Mine != null &&
                   vm.Theirs != null &&
                   vm.ConflictedFiles.Any(f => f.Path == "shared.txt");
        });
        conflictView = window.GetVisualDescendants().OfType<Views.Conflict>().First(c => c.IsEffectivelyVisible);
        conflictVm = (VM.Conflict)conflictView.DataContext!;
        Check(File.Exists(Path.Combine(repoPath, ".git", "MERGE_HEAD")) &&
              repo.HasInProgressBanner &&
              conflictVm.Mine != null &&
              conflictVm.Theirs != null &&
              conflictVm.ConflictedFiles.Any(f => f.Path == "shared.txt"),
            "Clicking Refresh Status in Conflict panel preserves external MERGE_HEAD and conflict markers safely");

        Screenshot(window, "external-conflict-readonly-light");

        // 6. GitNest DontCommit paused merge vs external rebase (--merge / --apply) and bisect read-only states
        RunGitAllowFailure(repoPath, "merge", "--abort");
        RunGitAllowFailure(repoPath, "checkout", "-b", "feat/dc-clean");
        File.WriteAllText(Path.Combine(repoPath, "dc-clean.txt"), "clean feature\n", utf8NoBom);
        RunGitAllowFailure(repoPath, "add", "dc-clean.txt");
        RunGitAllowFailure(repoPath, "commit", "-m", "clean feature for dontcommit");
        RunGitAllowFailure(repoPath, "checkout", "main");

        var dcPreflight = SourceGit.Commands.MergeRevertService.InspectMergeAsync(repoPath, "feat/dc-clean", "main", MergeSourceKind.LocalBranch).GetAwaiter().GetResult();
        var dcExec = SourceGit.Commands.MergeRevertService.ExecuteMergeAsync(
            repoPath,
            new MergeExecutionOptions
            {
                SourceKind = MergeSourceKind.LocalBranch,
                SourceRefOrSha = "feat/dc-clean",
                TargetBranchName = "main",
                Mode = MergeMode.DontCommit,
                Preflight = dcPreflight,
            },
            OperationJournalStore.Instance).GetAwaiter().GetResult();
        Check(dcExec.Succeeded, "UIProbe DontCommit clean merge pauses with MERGE_HEAD");

        repo.MarkWorkingCopyDirtyManually();
        repo.SelectedViewIndex = 1;
        Await(() =>
        {
            var wc = window.GetVisualDescendants().OfType<Views.WorkingCopy>().FirstOrDefault()?.DataContext as VM.WorkingCopy;
            return wc is { HasInProgressBanner: true, CanContinueInProgress: true, ShowExternalInProgressFooter: false, IsRefreshingInProgressOwnership: false } &&
                   wc.InProgressInspection is { IsGitNestDontCommitMerge: true } &&
                   repo.InProgressOriginLabel == App.Text("WorkingCopy.InProgress.DontCommitOrigin");
        });
        var wcView = window.GetVisualDescendants().OfType<Views.WorkingCopy>().First();
        var wcVm = (VM.WorkingCopy)wcView.DataContext!;
        var splitContinue = wcView.GetVisualDescendants().OfType<SplitButton>().FirstOrDefault(b => Equals(b.Content?.ToString(), App.Text("Repository.Continue")));
        var amendCheck = wcView.GetVisualDescendants().OfType<CheckBox>().FirstOrDefault(c => Equals(c.Content?.ToString(), App.Text("WorkingCopy.Amend")));
        Check(splitContinue != null &&
              splitContinue.IsVisible &&
              !wcVm.UseAmend &&
              amendCheck != null &&
              !amendCheck.IsEnabled,
              "GitNest DontCommit merge enables Continue SplitButton and disables Amend checkbox");
        Screenshot(window, "workingcopy-dontcommit-merge-light");
        RunGitAllowFailure(repoPath, "merge", "--abort");

        // External git rebase --merge conflict
        RunGitAllowFailure(repoPath, "checkout", "-b", "feat/rm-probe");
        File.WriteAllText(Path.Combine(repoPath, "rebase-conflict.txt"), "feature change\n", utf8NoBom);
        RunGitAllowFailure(repoPath, "add", "rebase-conflict.txt");
        RunGitAllowFailure(repoPath, "commit", "-m", "feat rebase conflict");
        RunGitAllowFailure(repoPath, "checkout", "main");
        File.WriteAllText(Path.Combine(repoPath, "rebase-conflict.txt"), "main change\n", utf8NoBom);
        RunGitAllowFailure(repoPath, "add", "rebase-conflict.txt");
        RunGitAllowFailure(repoPath, "commit", "-m", "main rebase conflict");
        RunGitAllowFailure(repoPath, "checkout", "feat/rm-probe");
        var (rmExit, _, _) = RunGitAllowFailure(repoPath, "rebase", "--merge", "main");
        var rmDir = Path.Combine(repoPath, ".git", "rebase-merge");
        Check(rmExit != 0 && Directory.Exists(rmDir), "External git rebase --merge stopped in conflict for UIProbe");

        repo.MarkWorkingCopyDirtyManually();
        Await(() =>
            repo.InProgressContext is VM.RebaseInProgress &&
            wcVm.HasInProgressBanner &&
            wcVm.ShowExternalInProgressFooter &&
            !wcVm.CanContinueInProgress &&
            !wcVm.IsRefreshingInProgressOwnership &&
            wcVm.InProgressInspection is { InProgressKind: GitInProgressKind.Rebase } &&
            repo.InProgressOriginLabel == App.Text("WorkingCopy.Conflicts.Origin.External"));

        var forbiddenInProgressActions = window.GetVisualDescendants()
            .OfType<Button>()
            .Where(b => b.IsEffectivelyVisible &&
                        (string.Equals(b.Content?.ToString(), "SKIP", StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(b.Content?.ToString(), "跳过此提交", StringComparison.Ordinal) ||
                         string.Equals(b.Content?.ToString(), App.Text("Repository.Continue"), StringComparison.Ordinal) ||
                         string.Equals(b.Content?.ToString(), App.Text("Repository.Abort"), StringComparison.Ordinal)))
            .ToList();
        var extGuideBlock = wcView.GetVisualDescendants().OfType<TextBlock>().FirstOrDefault(t => t.IsEffectivelyVisible && t.Text == App.Text("WorkingCopy.ExternalInProgressGuide"));
        Check(forbiddenInProgressActions.Count == 0 &&
              extGuideBlock != null &&
              extGuideBlock.Bounds.Width > 0 &&
              extGuideBlock.Bounds.Width <= wcView.Bounds.Width &&
              (splitContinue == null || !splitContinue.IsVisible) &&
              Directory.Exists(rmDir),
              "External rebase --merge renders read-only banner and external footer without Skip/Continue/Abort buttons");
        Screenshot(window, "external-rebase-readonly-light");

        App.SetTheme("Dark", string.Empty);
        window.Width = 860;
        Pump();
        var narrowExtGuideBlock = wcView.GetVisualDescendants().OfType<TextBlock>().FirstOrDefault(t => t.IsEffectivelyVisible && t.Text == App.Text("WorkingCopy.ExternalInProgressGuide"));
        var wcRightInWindow = wcView.TranslatePoint(new Avalonia.Point(wcView.Bounds.Width, 0), window);
        Check(narrowExtGuideBlock != null &&
              narrowExtGuideBlock.Bounds.Width > 0 &&
              narrowExtGuideBlock.Bounds.Width <= wcView.Bounds.Width &&
              wcRightInWindow is { X: var wcRightX } &&
              wcRightX <= window.Bounds.Width + 1.0,
              "External rebase guide text wraps cleanly within narrow 860px Dark layout");
        Screenshot(window, "external-rebase-readonly-dark-narrow");

        App.SetTheme("Light", string.Empty);
        window.Width = 1120;
        RunGitAllowFailure(repoPath, "rebase", "--abort");

        // External git rebase --apply conflict (no applying file -> never delete!)
        var (raExit, _, _) = RunGitAllowFailure(repoPath, "rebase", "--apply", "main");
        var raDir = Path.Combine(repoPath, ".git", "rebase-apply");
        Check(raExit != 0 && Directory.Exists(raDir) && !File.Exists(Path.Combine(raDir, "applying")),
              "External git rebase --apply stopped with .git/rebase-apply and no applying marker");
        repo.MarkWorkingCopyDirtyManually();
        Await(() =>
            repo.InProgressContext is VM.RebaseInProgress &&
            wcVm.ShowExternalInProgressFooter &&
            !wcVm.IsRefreshingInProgressOwnership &&
            wcVm.InProgressInspection is { InProgressKind: GitInProgressKind.Rebase });
        Check(Directory.Exists(raDir), "UI refresh preserves external .git/rebase-apply without deleting it");
        RunGitAllowFailure(repoPath, "rebase", "--abort");

        // External git bisect (BISECT_LOG)
        RunGitAllowFailure(repoPath, "checkout", "main");
        RunGitAllowFailure(repoPath, "bisect", "start");
        RunGitAllowFailure(repoPath, "bisect", "bad");
        RunGitAllowFailure(repoPath, "bisect", "good", "HEAD~2");
        repo.MarkWorkingCopyDirtyManually();
        Await(() =>
            repo.InProgressContext is VM.BisectInProgress &&
            wcVm.ShowExternalInProgressFooter &&
            !wcVm.IsRefreshingInProgressOwnership &&
            wcVm.InProgressInspection is { InProgressKind: GitInProgressKind.Bisect } &&
            repo.InProgressOriginLabel == App.Text("WorkingCopy.Conflicts.Origin.External"));
        Check(File.Exists(Path.Combine(repoPath, ".git", "BISECT_LOG")) &&
              window.GetVisualDescendants().OfType<TextBlock>().Any(t => t.IsEffectivelyVisible && t.Text == App.Text("InProgress.Bisect")),
              "External BISECT_LOG displays read-only BisectInProgress banner and blocks writes");
        Screenshot(window, "external-bisect-readonly-light");
        RunGitAllowFailure(repoPath, "bisect", "reset");

        window.Close();
        _window = null;
        File.WriteAllText(probeGlobalCfg, string.Empty, utf8NoBom);
    }

    private static (int ExitCode, string StdOut, string StdErr) RunGitAllowFailure(string directory, params string[] args)
    {
        var info = new ProcessStartInfo(OS.GitExecutable)
        {
            WorkingDirectory = directory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            UseShellExecute = false,
        };
        foreach (var arg in args)
            info.ArgumentList.Add(arg);

        using var process = Process.Start(info)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        return (process.ExitCode, stdout.GetAwaiter().GetResult(), stderr.GetAwaiter().GetResult());
    }
}
