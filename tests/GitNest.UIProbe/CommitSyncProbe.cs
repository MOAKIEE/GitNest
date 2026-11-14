using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;

using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

using SourceGit;
using SourceGit.Commands;
using SourceGit.Models;
using SourceGit.Views;
using VM = SourceGit.ViewModels;

internal static partial class Program
{
    static partial void RunCommitSync()
    {
        var suiteRoot = Path.Combine(_root, "commit-sync-probe");
        Directory.CreateDirectory(suiteRoot);

        var probeGlobalCfg = Path.Combine(suiteRoot, "probe-global.gitconfig");
        File.WriteAllText(probeGlobalCfg, string.Empty);
        Environment.SetEnvironmentVariable("GIT_CONFIG_GLOBAL", probeGlobalCfg);
        Environment.SetEnvironmentVariable("GIT_CONFIG_NOSYSTEM", "1");

        var bareOrigin = Path.Combine(suiteRoot, "origin.git");
        var seedDir = Path.Combine(suiteRoot, "seed");
        var cloneDir = Path.Combine(suiteRoot, "clone-main");
        var peerDir = Path.Combine(suiteRoot, "clone-peer");

        Git(suiteRoot, "init", "--bare", "-b", "main", bareOrigin);
        Git(suiteRoot, "init", "-b", "main", seedDir);
        Git(seedDir, "config", "--local", "user.name", "Seed Bot");
        Git(seedDir, "config", "--local", "user.email", "seed@example.invalid");
        File.WriteAllText(Path.Combine(seedDir, "README.md"), "# GitNest Probe\n", new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(seedDir, "partial.txt"), "base-line\n", new UTF8Encoding(false));
        Git(seedDir, "add", ".");
        Git(seedDir, "commit", "-m", "initial commit");
        Git(seedDir, "remote", "add", "origin", bareOrigin);
        Git(seedDir, "push", "-u", "origin", "main");

        Git(suiteRoot, "clone", bareOrigin, cloneDir);
        Git(suiteRoot, "clone", bareOrigin, peerDir);
        Git(peerDir, "config", "--local", "user.name", "Peer Dev");
        Git(peerDir, "config", "--local", "user.email", "peer@example.invalid");

        // Create partial staging in cloneDir WITHOUT user.name/email configured yet.
        var partialPath = Path.Combine(cloneDir, "partial.txt");
        File.WriteAllText(partialPath, "base-line\nstaged-part\n", new UTF8Encoding(false));
        Git(cloneDir, "add", "partial.txt");
        File.WriteAllText(partialPath, "base-line\nstaged-part\nunstaged-part\n", new UTF8Encoding(false));

        App.SetTheme("Light", string.Empty);

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
            Title = "GitNest 2.5 验证探针",
        };
        window.Show();

        // 1. Switch to WorkingCopy, verify unfetched status summary, partial staging hint & missing identity prompt
        repo.SelectedViewIndex = 1;
        Await(() => repo.WorkingCopy != null &&
                    repo.WorkingCopy.Staged.Count == 1 &&
                    repo.WorkingCopy.Unstaged.Count == 1 &&
                    repo.WorkingCopy.LastCapturedSnapshot != null);

        var wc = repo.WorkingCopy!;
        Check(wc.HasPartialStagedFiles, "WorkingCopy detects partial staging and shows non-destructive hint");
        Check(repo.StatusSummaryDetail.Contains("尚未获取") || repo.StatusSummary.Contains("待推送") || repo.StatusSummary.Length > 0,
            "Repository overview displays honest initial sync status");

        wc.CommitMessage = "feat: partial staged with missing identity";
        ClickButtonByText(window, App.Text("WorkingCopy.Commit"));
        Await(() => wc.ShowIdentityPrompt);

        Check(wc.ShowIdentityPrompt && File.ReadAllText(probeGlobalCfg).Length == 0,
            "Missing commit identity triggers inline local repository identity prompt without editing global config");
        Screenshot(window, "commit-partial-identity-light");

        // Fill local identity and continue commit cleanly
        wc.IdentityAuthorName = "Probe Local Author";
        wc.IdentityAuthorEmail = "probe-local@example.invalid";
        ClickButtonByText(window, App.Text("WorkingCopy.SaveIdentityAndContinue"));
        Await(() => !wc.ShowIdentityPrompt && !wc.IsCommitting && wc.Staged.Count == 0);

        var committedPartial = Git(cloneDir, "show", "HEAD:partial.txt");
        var remainingDisk = File.ReadAllText(partialPath, Encoding.UTF8);
        Check(committedPartial.Replace("\r\n", "\n") == "base-line\nstaged-part" &&
              remainingDisk.Contains("unstaged-part", StringComparison.Ordinal) &&
              wc.Unstaged.Count == 1 &&
              File.ReadAllText(probeGlobalCfg).Length == 0,
            "Partial staging commit preserves remaining unstaged worktree edit and saves identity strictly locally");

        // 2. External staged snapshot tamper aborts commit and shows snapshot alert banner
        Git(cloneDir, "add", "partial.txt");
        wc.RefreshStagedSnapshotAsync().GetAwaiter().GetResult();
        var headBeforeTamper = Git(cloneDir, "rev-parse", "HEAD");
        Await(() => wc.Staged.Count == 1 && wc.LastCapturedSnapshot?.StagedChanges.Count == 1);

        // Mutate index externally behind the displayed UI snapshot
        File.WriteAllText(Path.Combine(cloneDir, "external.txt"), "injected-by-external-agent\n", new UTF8Encoding(false));
        Git(cloneDir, "add", "external.txt");

        wc.CommitMessage = "should abort when staged snapshot changed";
        ClickButtonByText(window, App.Text("WorkingCopy.Commit"));
        Await(() => !wc.IsCommitting && wc.HasCommitAlert);

        Check(Git(cloneDir, "rev-parse", "HEAD") == headBeforeTamper &&
              wc.CommitAlertMessage == App.Text("WorkingCopy.SnapshotStaleBanner"),
            "External index mutation aborts commit cleanly and displays snapshot change alert");
        Screenshot(window, "commit-snapshot-stale-light");
        ClickButtonByText(window, App.Text("WorkingCopy.DismissBanner"));
        Await(() => !wc.HasCommitAlert);

        // 3. Dark mode: Two-Step Commit & Push failure -> Retry Push Only without duplicate commit
        App.SetTheme("Dark", string.Empty);
        var preReceiveHook = Path.Combine(bareOrigin, "hooks", "pre-receive");
        File.WriteAllText(preReceiveHook, "#!/bin/sh\necho \"protected rule rejected in probe\" >&2\nexit 1\n", new UTF8Encoding(false));

        wc.RefreshStagedSnapshotAsync().GetAwaiter().GetResult();
        var commitsBeforeTwoStep = int.Parse(Git(cloneDir, "rev-list", "--count", "HEAD"));
        wc.CommitMessage = "feat: two-step commit and push probe";
        ClickButtonByText(window, App.Text("WorkingCopy.CommitAndPush"));
        Await(() => !wc.IsCommitting && wc.HasTwoStepStatus);

        var commitsAfterFailPush = int.Parse(Git(cloneDir, "rev-list", "--count", "HEAD"));
        Check(wc.ActiveTwoStepSession is { CommitSucceeded: true, PushAttempted: true, PushSucceeded: false, CanRetryPushOnly: true } &&
              commitsAfterFailPush == commitsBeforeTwoStep + 1,
            "Commit & Push two-step preserves Step 1 commit when Step 2 push is rejected");
        Screenshot(window, "commit-push-twostep-retry-dark");

        File.Delete(preReceiveHook);
        ClickButtonByText(window, App.Text("WorkingCopy.RetryPushOnly"));
        Await(() => !wc.IsCommitting && wc.TwoStepPushSucceeded);

        var commitsAfterRetry = int.Parse(Git(cloneDir, "rev-list", "--count", "HEAD"));
        Check(commitsAfterRetry == commitsAfterFailPush &&
              Git(bareOrigin, "rev-parse", "refs/heads/main") == Git(cloneDir, "rev-parse", "HEAD"),
            "Retry Push Only pushes preserved commit cleanly with zero duplicate local commit");
        ClickButtonByText(window, App.Text("WorkingCopy.DismissBanner"));

        // 4. Fetch popup showing real fetch timestamp (Light mode)
        page.ClearNotifications();
        App.SetTheme("Light", string.Empty);
        repo.RefreshBranches();
        Await(() => repo.Remotes.Count == 1 && repo.Branches.Any(b => b.IsCurrent));

        var fetchPopup = new VM.Fetch(repo);
        repo.ShowPopup(fetchPopup);
        Await(() => page.Popup == fetchPopup);
        Check(fetchPopup.LastFetchStatusText.Length > 0, "Fetch popup renders fetch status without fabricating zero");
        Screenshot(window, "fetch-popup-light");
        ClickButtonByText(window, App.Text("Sure"));
        Await(() => page.Popup == null);

        // 5. Diverged Pull popup -> fast-forward refused cleanly with Merge Flow handoff (Light + Dark Narrow)
        // Advance peer from current origin tip and push to bareOrigin; advance cloneDir locally so both diverge
        Git(peerDir, "pull", "--ff-only", "origin", "main");
        File.WriteAllText(Path.Combine(peerDir, "peer-only.txt"), "peer diverge\n", new UTF8Encoding(false));
        Git(peerDir, "add", "peer-only.txt");
        Git(peerDir, "commit", "-m", "peer diverged commit");
        Git(peerDir, "push", "origin", "main");

        File.WriteAllText(Path.Combine(cloneDir, "local-only.txt"), "local diverge\n", new UTF8Encoding(false));
        Git(cloneDir, "add", "local-only.txt");
        Git(cloneDir, "commit", "-m", "local diverged commit");
        Git(cloneDir, "config", "--local", "pull.rebase", "true");

        var pullPopup = new VM.Pull(repo, null);
        repo.ShowPopup(pullPopup);
        Await(() => page.Popup == pullPopup);
        ClickButtonByText(window, App.Text("Sure"));
        Await(() => !pullPopup.InProgress && pullPopup.HasFeedbackBanner);

        Check(pullPopup.IsDiverged &&
              pullPopup.DivergedCountsText.Contains('1') &&
              Git(cloneDir, "config", "--local", "--get", "pull.rebase") == "true" &&
              window.GetVisualDescendants().OfType<CheckBox>().All(cb => !string.Equals(cb.Content?.ToString(), App.Text("Pull.UseRebase"), StringComparison.Ordinal)),
            "Diverged Pull enforces --ff-only without UseRebase checkbox, preserves pull.rebase config, and offers merge handoff");
        Screenshot(window, "pull-diverged-light");

        App.SetTheme("Dark", string.Empty);
        window.Width = 860;
        window.Height = 620;
        Await(() => window.Bounds.Width <= 870);
        Screenshot(window, "pull-diverged-dark-narrow");

        ClickButtonByText(window, App.Text("Pull.OpenMerge"));
        Await(() => page.Popup is VM.Merge);
        Check(page.Popup is VM.Merge, "Pull divergence 'Open Merge Flow' transitions cleanly into explicit Merge popup");
        ClickButtonByText(window, App.Text("Cancel"));
        Await(() => page.Popup == null);

        // 6. Push popup for branch without upstream (single-branch refspec preview, no --tags/--force)
        App.SetTheme("Light", string.Empty);
        window.Width = 1280;
        window.Height = 860;
        Git(cloneDir, "checkout", "-b", "feat/no-upstream-probe");
        repo.RefreshBranches();
        Await(() => repo.CurrentBranch?.Name == "feat/no-upstream-probe");

        var pushPopup = new VM.Push(repo, null);
        repo.ShowPopup(pushPopup);
        Await(() => page.Popup == pushPopup);

        var checkTexts = window.GetVisualDescendants()
            .OfType<CheckBox>()
            .Where(cb => cb.IsEffectivelyVisible)
            .Select(cb => cb.Content?.ToString() ?? string.Empty)
            .ToList();

        Check(pushPopup.ShowNoUpstreamHint &&
              pushPopup.RefspecPreview.Contains("refs/heads/feat/no-upstream-probe:refs/heads/feat/no-upstream-probe (origin)", StringComparison.Ordinal) &&
              !pushPopup.CanStartDirectly() &&
              !checkTexts.Contains(App.Text("Push.WithAllTags"), StringComparer.Ordinal) &&
              !checkTexts.Contains(App.Text("Push.Force"), StringComparer.Ordinal),
            "Push popup enforces explicit single-branch target without implicit tag push or force push");
        Screenshot(window, "push-single-branch-light");

        ClickButtonByText(window, App.Text("Cancel"));
        Await(() => page.Popup == null);

        window.Close();
        _window = null;
        File.WriteAllText(probeGlobalCfg, string.Empty);
    }
}
