using System;
using System.Collections.Generic;
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
    static partial void RunStashDiscardIgnore()
    {
        var utf8NoBom = new UTF8Encoding(false);
        var suiteRoot = Path.Combine(_root, "stash-discard-ignore-probe");
        Directory.CreateDirectory(suiteRoot);

        var probeGlobalCfg = Path.Combine(suiteRoot, "probe-global.gitconfig");
        File.WriteAllText(probeGlobalCfg, string.Empty, utf8NoBom);
        Environment.SetEnvironmentVariable("GIT_CONFIG_GLOBAL", probeGlobalCfg);
        Environment.SetEnvironmentVariable("GIT_CONFIG_NOSYSTEM", "1");

        var backupStore = new DiscardBackupStore(Path.Combine(suiteRoot, "discard-backups"));

        // 1. Unborn-HEAD repo for RestoreToHead blocking + untracked-only recycle verification
        var unbornRepoDir = Path.Combine(suiteRoot, "unborn-repo");
        Directory.CreateDirectory(unbornRepoDir);
        Git(unbornRepoDir, "init", "-b", "main");
        Git(unbornRepoDir, "config", "--local", "user.name", "Probe User");
        Git(unbornRepoDir, "config", "--local", "user.email", "probe@example.invalid");
        File.WriteAllText(Path.Combine(unbornRepoDir, ".gitignore"), "*.ignored\n", utf8NoBom);
        File.WriteAllText(Path.Combine(unbornRepoDir, "staged-new.txt"), "staged content\n", utf8NoBom);
        File.WriteAllText(Path.Combine(unbornRepoDir, "untracked-tmp.txt"), "temp untracked\n", utf8NoBom);
        File.WriteAllText(Path.Combine(unbornRepoDir, "skip.ignored"), "ignored payload\n", utf8NoBom);
        Git(unbornRepoDir, "add", ".gitignore", "staged-new.txt");

        // 2. Main repo with branches for discard/backup/stash/switch/ignore probes
        var mainRepoDir = Path.Combine(suiteRoot, "main-repo");
        Directory.CreateDirectory(mainRepoDir);
        Git(mainRepoDir, "init", "-b", "main");
        Git(mainRepoDir, "config", "--local", "user.name", "Probe Alice");
        Git(mainRepoDir, "config", "--local", "user.email", "alice@example.invalid");
        File.WriteAllText(Path.Combine(mainRepoDir, ".gitignore"), "*.ignored\n!important.log\n", utf8NoBom);
        File.WriteAllText(Path.Combine(mainRepoDir, "tracked.txt"), "line-1\nline-2\n", utf8NoBom);
        File.WriteAllText(Path.Combine(mainRepoDir, "shared.txt"), "base-line\n", utf8NoBom);
        File.WriteAllText(Path.Combine(mainRepoDir, "tracked.log"), "tracked log line\n", utf8NoBom);
        Git(mainRepoDir, "add", ".");
        Git(mainRepoDir, "commit", "-m", "initial base");

        Git(mainRepoDir, "checkout", "-b", "feat/conflict");
        File.WriteAllText(Path.Combine(mainRepoDir, "shared.txt"), "feat-branch-version\n", utf8NoBom);
        Git(mainRepoDir, "add", "shared.txt");
        Git(mainRepoDir, "commit", "-m", "feat: diverge shared.txt");
        Git(mainRepoDir, "checkout", "main");

        // Stage + unstaged edit on tracked.txt (partial staged), plus untracked + ignored files
        File.WriteAllText(Path.Combine(mainRepoDir, "tracked.txt"), "line-1\nstaged-change\n", utf8NoBom);
        Git(mainRepoDir, "add", "tracked.txt");
        File.WriteAllText(Path.Combine(mainRepoDir, "tracked.txt"), "line-1\nstaged-change\nunstaged-edit\n", utf8NoBom);
        File.WriteAllText(Path.Combine(mainRepoDir, "build.ignored"), "keep ignored\n", utf8NoBom);

        App.SetTheme("Light", string.Empty);

        var launcher = _launcher = new VM.Launcher(unbornRepoDir);
        typeof(App).GetField("_launcher", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(Avalonia.Application.Current, launcher);

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
            Title = "GitNest 3.5b Stash / Discard / Ignore UI Probe",
        };
        window.Show();

        // === Step A: Unborn HEAD blocks RestoreToHead & offers zero ignore-clean checkbox ===
        var unbornPage = launcher.ActivePage;
        var unbornRepo = (VM.Repository)unbornPage.Data;
        Await(() => unbornRepo.LocalChangesCount >= 2);

        var unbornDiscardVm = new VM.Discard(
            unbornRepo,
            null,
            null,
            DiscardBaselineKind.RestoreToHead,
            backupStore,
            null);
        unbornPage.Popup = unbornDiscardVm;
        Await(() => !unbornDiscardVm.IsLoadingPreflight && unbornDiscardVm.HasPreflight);
        Check(unbornDiscardVm.ShowUnbornHeadBlock &&
              !unbornDiscardVm.CanConfirm &&
              unbornDiscardVm.TargetRows.Count == 0,
            "Unborn HEAD strictly blocks RestoreToHead baseline in Discard popup");
        var unbornStrings = CollectAllStrings(window);
        Check(unbornStrings.All(s => !s.Contains("git clean", StringComparison.OrdinalIgnoreCase)),
            "Discard popup never surfaces ignore-cleaning options");
        ClickButtonByText(window, App.Text("Cancel"));
        Await(() => unbornPage.Popup == null);

        // Open main-repo in a fresh tab
        var normalizedMainRepo = mainRepoDir.Replace('\\', '/');
        launcher.OpenRepositoryInTab(mainRepoDir, null);
        Await(() => launcher.ActivePage?.Data is VM.Repository r &&
                    string.Equals(r.FullPath.Replace('\\', '/'), normalizedMainRepo, StringComparison.OrdinalIgnoreCase) &&
                    r.CurrentBranch != null);
        var mainPage = launcher.ActivePage;
        var repo = (VM.Repository)mainPage.Data;
        var (_, mainCommonDir) = RepositoryWriteGuard.ResolveGitDirectoriesAsync(mainRepoDir).GetAwaiter().GetResult();

        // === Step B: Discard RestoreWorktreeToIndex keeps staged edits, saves 30d backup, leaves ignored intact ===
        var toIndexVm = new VM.Discard(
            repo,
            null,
            null,
            DiscardBaselineKind.RestoreWorktreeToIndex,
            backupStore,
            null);
        mainPage.Popup = toIndexVm;
        Await(() => !toIndexVm.IsLoadingPreflight && toIndexVm.HasPreflight && toIndexVm.CanConfirm);
        Check(toIndexVm.TargetRows.Any(r => r.Path == "tracked.txt") &&
              !toIndexVm.TargetRows.Any(r => r.Path == "build.ignored"),
            "RestoreWorktreeToIndex preflight lists tracked.txt and excludes ignored files");
        Screenshot(window, "discard-to-index-light");

        ClickButtonByText(window, App.Text("Discard.Confirm.ToIndex"));
        Await(() => mainPage.Popup == null);
        Check(File.ReadAllText(Path.Combine(mainRepoDir, "tracked.txt"), utf8NoBom) == "line-1\nstaged-change\n" &&
              File.Exists(Path.Combine(mainRepoDir, "build.ignored")) &&
              backupStore.ListForRepository(mainRepoDir, mainCommonDir).Any(b => b.Files.Any(f => f.RelativePath == "tracked.txt")),
            "RestoreWorktreeToIndex keeps staged index edits, saves 30-day backup, and leaves .gitignore'd files untouched");

        // === Step C: Untracked partial recycle failure keeps failed file on disk and keeps popup open ===
        var okUntracked = Path.Combine(mainRepoDir, "ok-temp.txt");
        var failUntracked = Path.Combine(mainRepoDir, "fail-temp.txt");
        File.WriteAllText(okUntracked, "ok temp\n", utf8NoBom);
        File.WriteAllText(failUntracked, "locked temp\n", utf8NoBom);

        var partialDiscardVm = new VM.Discard(
            repo,
            null,
            null,
            DiscardBaselineKind.MoveUntrackedToRecycleBin,
            backupStore,
            path => path.EndsWith("fail-temp.txt", StringComparison.OrdinalIgnoreCase)
                ? (false, "Simulated Recycle Bin lock")
                : (DeleteFileForTest(path), string.Empty));
        mainPage.Popup = partialDiscardVm;
        Await(() => !partialDiscardVm.IsLoadingPreflight && partialDiscardVm.CanConfirm);
        ClickButtonByText(window, App.Text("Discard.Confirm.Recycle"));
        Await(() => partialDiscardVm.HasResultView);
        Check(mainPage.Popup == partialDiscardVm &&
              !partialDiscardVm.IsResultSuccess &&
              !File.Exists(okUntracked) &&
              File.Exists(failUntracked) &&
              partialDiscardVm.ResultLines.Any(l => l.Contains("fail-temp.txt", StringComparison.Ordinal)),
            $"Partial recycle failure keeps failed file on disk and keeps Discard popup open [samePopup={mainPage.Popup == partialDiscardVm}, isSuccess={partialDiscardVm.IsResultSuccess}, summary='{partialDiscardVm.ResultSummary}', lines='{string.Join(" | ", partialDiscardVm.ResultLines)}']");
        Screenshot(window, "discard-recycle-partial-light");
        ClickButtonByText(window, App.Text("Close"));
        Await(() => mainPage.Popup == null);
        if (File.Exists(failUntracked))
            File.Delete(failUntracked);

        // === Step D: ViewLogs Tab 2 (Discard Backups 30d) subsequent modification conflict + overwrite + prune ===
        // Modify tracked.txt AFTER backup so restore preflight detects subsequent edit conflict
        File.WriteAllText(Path.Combine(mainRepoDir, "tracked.txt"), "line-1\nnewer-edit-after-discard\n", utf8NoBom);
        // Add an expired 40d backup entry to verify expired badge & prune button
        backupStore.Capture(
            mainRepoDir,
            mainCommonDir,
            Git(mainRepoDir, "rev-parse", "HEAD").Trim(),
            DiscardBaselineKind.MoveUntrackedToRecycleBin,
            "Expired 40-day probe entry",
            [
                new DiscardBackupCaptureInput
                {
                    RelativePath = "gone.txt",
                    Baseline = DiscardBaselineKind.MoveUntrackedToRecycleBin,
                    ExistsInWorktree = true,
                    WorktreeBytes = utf8NoBom.GetBytes("expired content\n"),
                },
            ],
            utcNowOverride: DateTime.UtcNow.AddDays(-40));

        var logsVm = new VM.ViewLogs(repo, focusRecoveryTab: false, journalStore: OperationJournalStore.Instance, focusDiscardBackupsTab: true, discardBackupStore: backupStore);
        var logsWin = new SourceGit.Views.ViewLogs
        {
            DataContext = logsVm,
        };
        logsWin.Show(window);
        Await(() => !logsVm.IsBusy && !logsVm.IsLoadingDiscardRestoreCheck && logsVm.DiscardBackups.Count >= 2);
        var trackedBackup = logsVm.DiscardBackups.First(b => b.Entry.Files.Any(f => f.RelativePath == "tracked.txt"));
        logsVm.SelectedDiscardBackup = trackedBackup;
        Await(() => !logsVm.IsLoadingDiscardRestoreCheck && logsVm.HasSelectedDiscardConflicts);
        Check(!logsVm.CanRestoreSelectedDiscardBackup &&
              logsVm.HasExpiredDiscardBackups &&
              logsVm.IsDiscardBackupsTabActive,
            "ViewLogs Discard Backups detects subsequent disk modifications and blocks overwrite until explicitly confirmed");
        Screenshot(logsWin, "viewlogs-discard-backups-light");

        logsVm.AllowOverwriteSubsequentEdits = true;
        Await(() => logsVm.CanRestoreSelectedDiscardBackup);
        ClickButtonByText(logsWin, App.Text("ViewLogs.DiscardBackups.Restore"));
        Await(() => !logsVm.IsBusy && File.ReadAllText(Path.Combine(mainRepoDir, "tracked.txt"), utf8NoBom).Contains("unstaged-edit", StringComparison.Ordinal));
        ClickButtonByText(logsWin, App.Text("ViewLogs.DiscardBackups.PruneExpired"));
        Await(() => !logsVm.IsBusy && !logsVm.HasExpiredDiscardBackups);

        App.SetTheme("Dark", string.Empty);
        Pump();
        Screenshot(logsWin, "viewlogs-discard-backups-dark");
        logsWin.Close();
        Pump();

        // === Step E: StashChanges preflight preview + ApplyStash dirty block & conflict retention ===
        Git(mainRepoDir, "reset", "HEAD", "--", "tracked.txt");
        Git(mainRepoDir, "checkout", "--", "tracked.txt");
        File.WriteAllText(Path.Combine(mainRepoDir, "stash-note.md"), "stash untracked content\n", utf8NoBom);
        File.WriteAllText(Path.Combine(mainRepoDir, "tracked.txt"), "line-1\nstashed-edit-body\n", utf8NoBom);

        App.SetTheme("Light", string.Empty);
        var stashVm = new VM.StashChanges(repo, null)
        {
            Message = "Probe stash with untracked",
            IncludeUntracked = true,
        };
        mainPage.Popup = stashVm;
        Await(() => !stashVm.IsLoadingPreflight && stashVm.HasPreflight && stashVm.CanConfirm);
        Check(stashVm.CandidateFiles.Any(c => c.Path == "stash-note.md") &&
              stashVm.HasIgnoredFiles &&
              stashVm.CandidateFiles.All(c => c.Path != "build.ignored"),
            "StashChanges preflight previews included untracked files and explicitly excludes ignored files");
        Screenshot(window, "stash-push-preflight-light");

        ClickButtonByText(window, App.Text("Stash"));
        Await(() => mainPage.Popup == null && !File.Exists(Path.Combine(mainRepoDir, "stash-note.md")));
        repo.RefreshStashes();
        Await(() => repo.StashesPage?.Stashes?.Count >= 1);
        var createdStash = repo.StashesPage.Stashes[0];

        // Dirty tree -> ApplyStash blocks initially; toggling AllowDirtyWorkingTree clears preflight block
        File.WriteAllText(Path.Combine(mainRepoDir, "shared.txt"), "dirty-unrelated-change\n", utf8NoBom);
        var dirtyApplyVm = new VM.ApplyStash(repo, createdStash);
        mainPage.Popup = dirtyApplyVm;
        Await(() => !dirtyApplyVm.IsLoadingPreflight && dirtyApplyVm.HasBlockingReasons);
        Check(!dirtyApplyVm.CanConfirm && dirtyApplyVm.ShowAllowDirtyOption,
            "ApplyStash blocks dirty working tree unless user opts in");
        Screenshot(window, "apply-stash-dirty-blocked-light");
        dirtyApplyVm.AllowDirtyWorkingTree = true;
        Await(() => !dirtyApplyVm.IsLoadingPreflight && dirtyApplyVm.CanConfirm);
        ClickButtonByText(window, App.Text("Cancel"));
        Await(() => mainPage.Popup == null);

        // Conflicting committed change + DropAfterApply=true -> triggers merge conflict and RETAINS stash safely
        Git(mainRepoDir, "checkout", "--", "shared.txt");
        File.WriteAllText(Path.Combine(mainRepoDir, "tracked.txt"), "line-1\nconflicting-committed-line\n", utf8NoBom);
        Git(mainRepoDir, "add", "tracked.txt");
        Git(mainRepoDir, "commit", "-m", "conflicting edit on tracked.txt");

        App.SetTheme("Dark", string.Empty);
        var applyVm = new VM.ApplyStash(repo, createdStash)
        {
            DropAfterApply = true,
        };
        mainPage.Popup = applyVm;
        Await(() => !applyVm.IsLoadingPreflight && applyVm.CanConfirm);
        ClickButtonByText(window, App.Text("ApplyStash.Confirm"));
        Await(() => applyVm.HasResultView);
        Check(mainPage.Popup == applyVm &&
              applyVm.IsResultConflictRetained &&
              repo.StashesPage.Stashes.Any(s => s.SHA == createdStash.SHA),
            $"ApplyStash retains stash on conflict even with DropAfterApply=true [samePopup={mainPage.Popup == applyVm}, conflictRetained={applyVm.IsResultConflictRetained}, title='{applyVm.ResultTitle}', summary='{applyVm.ResultSummary}']");
        Screenshot(window, "apply-stash-conflict-retained-dark");
        ClickButtonByText(window, App.Text("Close"));
        Await(() => mainPage.Popup == null);

        // Clean conflict state and verify DropStash popup succeeds by exact SHA
        Git(mainRepoDir, "reset", "--hard", "HEAD");
        if (File.Exists(Path.Combine(mainRepoDir, "stash-note.md")))
            File.Delete(Path.Combine(mainRepoDir, "stash-note.md"));
        repo.RefreshStashes();
        Await(() => repo.StashesPage?.Stashes?.Count >= 1);
        var dropVm = new VM.DropStash(repo, createdStash);
        mainPage.Popup = dropVm;
        Pump();
        ClickButtonByText(window, App.Text("DropStash.Confirm"));
        Await(() => mainPage.Popup == null);
        Check(string.IsNullOrWhiteSpace(Git(mainRepoDir, "stash", "list")),
            "DropStash drops pinned SHA cleanly");

        // === Step F: Branch switch DealWithLocalChangesMethod removes Discard + StashAndReapply retains conflict stash ===
        File.WriteAllText(Path.Combine(mainRepoDir, "shared.txt"), "local-conflicting-change\n", utf8NoBom);
        File.WriteAllText(Path.Combine(mainRepoDir, "keep-me-untracked.txt"), "carry across switch\n", utf8NoBom);
        repo.MarkWorkingCopyDirtyManually();
        repo.RefreshBranches();
        Await(() => repo.LocalChangesCount >= 2 && repo.Branches.Any(b => b.IsLocal && b.Name == "feat/conflict"));
        var targetBranch = repo.Branches.First(b => b.IsLocal && b.Name == "feat/conflict");

        App.SetTheme("Light", string.Empty);
        window.Width = 780;
        window.Height = 700;
        var checkoutVm = new VM.Checkout(repo, targetBranch)
        {
            DealWithLocalChanges = DealWithLocalChanges.StashAndReapply,
        };
        mainPage.Popup = checkoutVm;
        Pump();
        var methodControl = window.GetVisualDescendants().OfType<SourceGit.Views.DealWithLocalChangesMethod>().FirstOrDefault();
        var radioCount = methodControl?.GetVisualDescendants().OfType<RadioButton>().Count() ?? 0;
        Check(radioCount == 2 && !CollectAllStrings(window).Any(s => s == App.Text("DealWithLocalChanges.Discard")),
            $"DealWithLocalChangesMethod exposes only DoNothing and StashAndReapply without Discard [hasLocal={checkoutVm.HasLocalChanges}, found={methodControl != null}, radioCount={radioCount}]");

        ClickButtonByText(window, App.Text("Sure"));
        Await(() => checkoutVm.HasResultView);
        Check(mainPage.Popup == checkoutVm &&
              checkoutVm.IsResultConflictRetained &&
              File.Exists(Path.Combine(mainRepoDir, "keep-me-untracked.txt")) &&
              !string.IsNullOrWhiteSpace(Git(mainRepoDir, "stash", "list")),
            "Checkout StashAndReapply switches branch, restores untracked file, and keeps popup open with retained stash on conflict");
        Screenshot(window, "checkout-conflict-retained-light-narrow");

        App.SetTheme("Dark", string.Empty);
        Pump();
        Screenshot(window, "checkout-conflict-retained-dark-narrow");
        ClickButtonByText(window, App.Text("Close"));
        Await(() => mainPage.Popup == null);

        // Clean working copy before AddToIgnore probe
        window.Width = 1120;
        window.Height = 760;
        Git(mainRepoDir, "reset", "--hard", "HEAD");
        Git(mainRepoDir, "stash", "clear");
        Git(mainRepoDir, "checkout", "main");

        // === Step G: AddToIgnore preset + .git/info/exclude local scope + ! negation + matched tracked warning ===
        File.WriteAllText(Path.Combine(mainRepoDir, "important.log"), "untracked log\n", utf8NoBom);
        File.WriteAllText(Path.Combine(mainRepoDir, "app.log"), "another log\n", utf8NoBom);

        App.SetTheme("Light", string.Empty);
        var ignoreVm = new VM.AddToIgnore(repo, "important.log", isDirectory: false);
        mainPage.Popup = ignoreVm;
        Await(() => !ignoreVm.IsLoadingInspection && ignoreVm.Presets.Count >= 2 && ignoreVm.SupportedTargets.Count >= 2);
        ignoreVm.SelectedPreset = ignoreVm.Presets.First(p => p.Kind == IgnoreRulePresetKind.ByExtension);
        Await(() => !ignoreVm.IsLoadingInspection &&
                    ignoreVm.HasWarnings &&
                    ignoreVm.HasMatchedTracked &&
                    ignoreVm.HasMatchedUntracked &&
                    ignoreVm.CanConfirm);
        Check(ignoreVm.MatchedTrackedPaths.Contains("tracked.log") &&
              ignoreVm.Warnings.Any(w => w.Contains("important.log", StringComparison.Ordinal)),
            "AddToIgnore surfaces ! negation conflict warning, matched untracked, and tracked-file warning without untracking");
        Screenshot(window, "add-to-ignore-warnings-light");

        App.SetTheme("Dark", string.Empty);
        ignoreVm.SelectedTarget = ignoreVm.SupportedTargets.First(t => t.Option.IsLocalOnly);
        Await(() => !ignoreVm.IsLoadingInspection && ignoreVm.IsLocalTargetSelected && ignoreVm.CanConfirm);
        Check(ignoreVm.SupportedTargets.Count(t => t.Option.IsLocalOnly && t.DisplayText.Count(c => c == '本') == 1) == 1,
            "AddToIgnore local exclude label displays cleanly without duplicate local-only suffix");
        Screenshot(window, "add-to-ignore-warnings-dark");

        ClickButtonByText(window, App.Text("AddToIgnore.Confirm"));
        Await(() => mainPage.Popup == null);
        var excludeContent = File.ReadAllText(Path.Combine(mainCommonDir, "info", "exclude"), utf8NoBom);
        Check(excludeContent.Contains("*.log", StringComparison.Ordinal) &&
              Git(mainRepoDir, "ls-files", "tracked.log").Trim() == "tracked.log",
            "AddToIgnore writes rule to .git/info/exclude and leaves already tracked files tracked");

        window.Close();
    }

    private static bool DeleteFileForTest(string fullPath)
    {
        if (File.Exists(fullPath))
            File.Delete(fullPath);
        return true;
    }
}
