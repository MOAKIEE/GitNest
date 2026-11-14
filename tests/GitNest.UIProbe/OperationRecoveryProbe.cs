using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;

using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;

using SourceGit;
using SourceGit.Commands;
using SourceGit.Models;
using SourceGit.Views;
using VM = SourceGit.ViewModels;
using OS = SourceGit.Native.OS;

internal static partial class Program
{
    static partial void RunOperationRecovery()
    {
        var suiteRoot = Path.Combine(_root, "operation-recovery-probe");
        Directory.CreateDirectory(suiteRoot);

        var probeGlobalCfg = Path.Combine(suiteRoot, "probe-global.gitconfig");
        File.WriteAllText(probeGlobalCfg, string.Empty);
        Environment.SetEnvironmentVariable("GIT_CONFIG_GLOBAL", probeGlobalCfg);
        Environment.SetEnvironmentVariable("GIT_CONFIG_NOSYSTEM", "1");

        var bareOrigin = Path.Combine(suiteRoot, "origin.git");
        var seedDir = Path.Combine(suiteRoot, "seed");
        var cloneDir = Path.Combine(suiteRoot, "clone-main");

        Git(suiteRoot, "init", "--bare", "-b", "main", bareOrigin);
        Git(suiteRoot, "init", "-b", "main", seedDir);
        Git(seedDir, "config", "--local", "user.name", "Seed Bot");
        Git(seedDir, "config", "--local", "user.email", "seed@example.invalid");
        File.WriteAllText(Path.Combine(seedDir, "README.md"), "# GitNest 3.1 Probe\n", new UTF8Encoding(false));
        Git(seedDir, "add", ".");
        Git(seedDir, "commit", "-m", "initial main commit");
        var mainTip = Git(seedDir, "rev-parse", "HEAD").Trim();
        Git(seedDir, "remote", "add", "origin", bareOrigin);
        Git(seedDir, "push", "-u", "origin", "main");

        Git(seedDir, "checkout", "-b", "feat/crash-lost");
        File.WriteAllText(Path.Combine(seedDir, "feature.txt"), "valuable crash recovery work\n", new UTF8Encoding(false));
        Git(seedDir, "add", "feature.txt");
        Git(seedDir, "commit", "-m", "feat: commit preserved only in backup ref");
        var featTip = Git(seedDir, "rev-parse", "HEAD").Trim();

        Git(suiteRoot, "clone", bareOrigin, cloneDir);
        Git(cloneDir, "config", "--local", "user.name", "Probe User");
        Git(cloneDir, "config", "--local", "user.email", "probe@example.invalid");

        // Fetch feature commit objects directly without creating refs/remotes or leaving FETCH_HEAD behind.
        Git(cloneDir, "fetch", seedDir, featTip);
        var fetchHeadPath = Path.Combine(cloneDir, ".git", "FETCH_HEAD");
        if (File.Exists(fetchHeadPath))
            File.Delete(fetchHeadPath);

        App.SetTheme("Light", string.Empty);

        // 1. Open unfetched repository cleanly first: must NOT fabricate LastFetch time and shows no false recovery alert.
        var launcher = _launcher = new VM.Launcher(cloneDir);
        typeof(App).GetField("_launcher", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(Avalonia.Application.Current, launcher);

        var page = launcher.ActivePage;
        var repo = (VM.Repository)page.Data;
        var pageView = new SourceGit.Views.LauncherPage { DataContext = page };
        var window = _window = new Window
        {
            Content = pageView,
            Width = 1280,
            Height = 860,
            ShowInTaskbar = false,
            Title = "GitNest 3.1 操作安全与恢复探针",
        };
        window.Show();

        Await(() => repo.CurrentBranch != null);
        Check(string.IsNullOrEmpty(repo.LastFetchDisplayTime) &&
              !repo.HasPendingRecoveryOperations &&
              !repo.ShowPendingRecoveryBanner,
            "Unfetched repository shows honest unfetched state without fake fetch time or phantom recovery alerts");

        // Verify window-activation refresh picks up external CLI branch changes cleanly.
        Git(cloneDir, "branch", "feat/external-cli-branch", mainTip);
        repo.RefreshOnWindowActivated();
        Await(() => repo.Branches.Any(b => b.Name == "feat/external-cli-branch"));
        Check(repo.Branches.Any(b => b.Name == "feat/external-cli-branch"),
            "RefreshOnWindowActivated discovers branch created outside GitNest");

        // 2. Simulate crash + secret URL failure + existing branch conflict + 40-day expired backup ref
        var (_, commonDir) = RepositoryWriteGuard.ResolveGitDirectoriesAsync(cloneDir).GetAwaiter().GetResult();

        // Occupy refs/heads/feat/crash-lost with mainTip so default restore is blocked without overwriting!
        Git(cloneDir, "branch", "feat/crash-lost", mainTip);

        var crashBackup = BackupRefs.CreateAsync(cloneDir, "20261114-crash-01", featTip).GetAwaiter().GetResult();
        Check(crashBackup.Succeeded, "Created local backup ref for simulated crash recovery");

        // Create an old backup ref with reflog timestamp 40 days ago (while crashBackup remains fresh from today)
        var expiredBackupRef = BackupRefs.NamespacePrefix + "20261001-expired-ref";
        Git(cloneDir, "-c", "core.logAllRefUpdates=always", "update-ref", "--no-deref", "-m", "old expired backup", expiredBackupRef, mainTip);
        var expiredUnix = DateTimeOffset.UtcNow.AddDays(-40).ToUnixTimeSeconds();
        var expiredLogPath = Path.Combine(commonDir, "logs", "refs", "gitnest", "backups", "20261001-expired-ref");
        Directory.CreateDirectory(Path.GetDirectoryName(expiredLogPath)!);
        File.WriteAllText(
            expiredLogPath,
            $"0000000000000000000000000000000000000000 {mainTip} Probe Bot <probe@example.invalid> {expiredUnix} +0000\told expired backup\n",
            new UTF8Encoding(false));

        const string secretToken = "ghp_UiProbeSecret999XYZ";
        OperationJournalService.RecordCompletedOperation(
            cloneDir,
            commonDir,
            GitOperationKind.Push,
            "推送失败示例（敏感地址校验）",
            GitOperationStage.Failed,
            $"推送到 https://alice:{secretToken}@example.invalid/org/repo.git 被远端拒绝",
            commandLogText: $"$ git push https://alice:{secretToken}@example.invalid/org/repo.git main",
            branchName: "main",
            remoteName: "origin",
            remoteBranchName: "main",
            originalTipSha: mainTip,
            journalStore: OperationJournalStore.Instance,
            diagnosticDetail: $"fatal: authentication failed for 'https://alice:{secretToken}@example.invalid/org/repo.git/'");

        OperationJournalStore.Instance.AppendOrUpdate(new GitOperationRecord
        {
            OperationId = "20261114-crash-01",
            RepositoryCommonDir = commonDir,
            WorktreePath = cloneDir,
            Kind = GitOperationKind.DeleteBranch,
            Title = "删除本地分支 feat/crash-lost",
            BranchName = "feat/crash-lost",
            OriginalTipSha = featTip,
            TargetTipSha = string.Empty,
            BackupRefName = crashBackup.BackupRefName,
            Stage = GitOperationStage.Executing,
            StepSummary = "进程中断前正在删除本地分支 feat/crash-lost。",
            CommandSummary = "$ git branch -D feat/crash-lost",
        });

        repo.RefreshRecoveryStatusAsync(OperationJournalStore.Instance).GetAwaiter().GetResult();
        Await(() => repo.HasPendingRecoveryOperations && repo.ShowPendingRecoveryBanner);

        Check(repo.HasPendingRecoveryOperations &&
              repo.ShowPendingRecoveryBanner &&
              repo.PendingRecoverySummary.Contains("待核实", StringComparison.Ordinal),
            "Repository surfaces startup recovery banner and toolbar alert for interrupted Executing operation");
        Screenshot(window, "repo-recovery-banner-light");

        // Test dismiss keeps toolbar badge while hiding banner
        ClickButtonByText(window, App.Text("OperationRecovery.BannerDismiss"));
        Await(() => !repo.ShowPendingRecoveryBanner && repo.HasPendingRecoveryOperations);
        Check(!repo.ShowPendingRecoveryBanner && repo.HasPendingRecoveryOperations,
            "Dismissing banner hides alert strip while preserving toolbar pending badge until resolved");

        // 3. Open native ViewLogs window focused on Operations & Recovery tab
        var logsVm = new VM.ViewLogs(repo, focusRecoveryTab: true, journalStore: OperationJournalStore.Instance);
        var logsWin = new SourceGit.Views.ViewLogs
        {
            DataContext = logsVm,
        };
        logsWin.Show(window);
        Await(() => !logsVm.IsBusy && logsVm.Operations.Count >= 2 && logsVm.SelectedOperation != null);

        Check(logsVm.SelectedOperation!.OperationId == "20261114-crash-01" &&
              logsVm.SelectedOperation.HasReconciliationNote &&
              logsVm.SelectedOperation.ReconciliationNote.Contains("feat/crash-lost", StringComparison.Ordinal),
            "ViewLogs opens on Operations & Recovery tab and selects interrupted record with live disk reconciliation note");

        Screenshot(logsWin, "viewlogs-recovery-light");

        // 4. Verify secret URL is redacted across rendered ViewLogs controls
        var secretRecordItem = logsVm.Operations.First(o => o.KindText == nameof(GitOperationKind.Push));
        logsVm.SelectedOperation = secretRecordItem;
        Pump();
        var visibleTexts = CollectAllStrings(logsWin);
        Check(!visibleTexts.Any(t => t.Contains(secretToken, StringComparison.Ordinal)) &&
              secretRecordItem.DiagnosticText.Contains("https://***@example.invalid/org/repo.git", StringComparison.Ordinal),
            "UI never exposes raw URL passwords/tokens and displays redacted credentials instead");

        // 5. Switch back to interrupted crash record; test occupied-branch block then renamed restore
        logsVm.SelectedOperation = logsVm.Operations.First(o => o.OperationId == "20261114-crash-01");
        Pump();

        ClickButtonByText(logsWin, App.Text("OperationRecovery.RestoreBackupBtn"));
        Await(() => !logsVm.IsBusy && logsVm.HasFeedbackMessage);
        Check(logsVm.IsFeedbackError &&
              logsVm.FeedbackMessage.Contains("占用", StringComparison.Ordinal) &&
              Git(cloneDir, "rev-parse", "refs/heads/feat/crash-lost") == mainTip,
            "Restoring backup refuses to overwrite existing feat/crash-lost branch of same name");

        logsVm.RestoreTargetBranch = "feat/recovered-from-backup";
        ClickButtonByText(logsWin, App.Text("OperationRecovery.RestoreBackupBtn"));
        Await(() => !logsVm.IsBusy && !logsVm.IsFeedbackError &&
                    Git(cloneDir, "show-ref", "--verify", "--quiet", "refs/heads/feat/recovered-from-backup") != null);
        Check(Git(cloneDir, "rev-parse", "refs/heads/feat/recovered-from-backup") == featTip,
            "Renamed restore from local backup ref reconstructs branch accurately at backed-up commit SHA");

        // 6. Confirm/acknowledge interrupted record -> clears main repo pending alert
        ClickButtonByText(logsWin, App.Text("OperationRecovery.AcknowledgeSelected"));
        Await(() => !logsVm.IsBusy && !repo.HasPendingRecoveryOperations);
        Check(!repo.HasPendingRecoveryOperations && !repo.ShowPendingRecoveryBanner,
            "Marking interrupted operation resolved updates journal and clears repository alert");

        // 7. Dark theme + Narrow window: Preview & confirm pruning only 40-day expired backup ref
        App.SetTheme("Dark", string.Empty);
        logsWin.Width = 760;
        logsWin.Height = 500;
        Await(() => logsWin.Bounds.Width <= 770);

        ClickButtonByText(logsWin, App.Text("OperationRecovery.PreviewExpiredBackups"));
        Await(() => !logsVm.IsBusy && logsVm.HasExpiredBackupsToPrune);
        Check(logsVm.PrunePreview!.ExpiredBackups.Count == 1 &&
              logsVm.PrunePreview.ExpiredBackups[0].FullRefName == expiredBackupRef &&
              logsVm.PrunePreview.RetainedBackups.Any(b => b.FullRefName == crashBackup.BackupRefName),
            "Expired backup preview identifies 40-day expired ref while keeping fresh backup ref protected");

        Screenshot(logsWin, "viewlogs-recovery-prune-dark-narrow");

        ClickButtonByText(logsWin, App.Text("OperationRecovery.PruneConfirmBtn"));
        Await(() => !logsVm.IsBusy && !logsVm.HasExpiredBackupsToPrune);
        var backupRefsAfterPrune = BackupRefs.ListAsync(cloneDir, 30, OperationJournalStore.Instance).GetAwaiter().GetResult();
        Check(backupRefsAfterPrune.All(b => b.FullRefName != expiredBackupRef) &&
              backupRefsAfterPrune.Any(b => b.FullRefName == crashBackup.BackupRefName),
            "Confirmed prune removes only expired backup ref and retains non-expired backup ref");

        // 8. Switch to Raw Command Logs tab and verify log list works seamlessly
        var cmdLog = repo.CreateLog("Restore Branch Check");
        cmdLog.AppendLine("$ git branch feat/recovered-from-backup " + featTip[..8]);
        cmdLog.Complete();

        ClickButtonByText(logsWin, App.Text("ViewLogs.TabCommandLogs"));
        Await(() => logsVm.IsRawLogsTabActive);
        if (logsVm.Logs.Count > 0 && logsVm.SelectedLog == null)
            logsVm.SelectedLog = logsVm.Logs[0];
        Pump();
        Check(logsVm.IsRawLogsTabActive && logsVm.Logs.Count > 0,
            "Raw Command Logs tab preserves native SourceGit command output viewer");

        Screenshot(logsWin, "viewlogs-rawlogs-dark");

        logsWin.Close();
        window.Close();
        _window = null;
        File.WriteAllText(probeGlobalCfg, string.Empty);
    }

    private static List<string> CollectAllStrings(Control root)
    {
        var items = new List<string>();
        foreach (var tb in root.GetVisualDescendants().OfType<TextBlock>())
        {
            if (!string.IsNullOrEmpty(tb.Text))
                items.Add(tb.Text);
        }
        foreach (var box in root.GetVisualDescendants().OfType<TextBox>())
        {
            if (!string.IsNullOrEmpty(box.Text))
                items.Add(box.Text);
        }
        return items;
    }
}
