using Avalonia.Controls;
using Avalonia.VisualTree;
using SourceGit;
using VM = SourceGit.ViewModels;
using Views = SourceGit.Views;

internal static partial class Program
{
    static partial void RunAmend()
    {
        var suiteRoot = Path.Combine(_root, "amend-probe");
        Directory.CreateDirectory(suiteRoot);

        var amendOrigin = Path.Combine(suiteRoot, "origin.git");
        var amendOriginMoved = Path.Combine(suiteRoot, "origin-offline.git");
        var amendRepoPath = Path.Combine(suiteRoot, "repo");
        Git(suiteRoot, "init", "--bare", "-b", "main", amendOrigin);
        Git(suiteRoot, "clone", amendOrigin, amendRepoPath);
        Git(amendRepoPath, "config", "user.name", "Probe User");
        Git(amendRepoPath, "config", "user.email", "probe@example.invalid");
        File.WriteAllText(Path.Combine(amendRepoPath, "app.txt"), "v1\n");
        Git(amendRepoPath, "add", ".");
        Git(amendRepoPath, "commit", "-m", "chore: initial app");
        Git(amendRepoPath, "push", "-u", "origin", "main");
        Git(amendRepoPath, "fetch", "origin");

        App.SetTheme("Light", "");
        var launcher = AttachLauncher(amendRepoPath);
        var page = launcher.ActivePage;
        var repo = (VM.Repository)page.Data;
        repo.SelectedViewIndex = 1;

        var window = _window = new Window
        {
            Content = new Views.LauncherPage { DataContext = page },
            Width = 1080,
            Height = 680,
            ShowInTaskbar = false,
            Title = "GitNest Amend UI Probe",
        };
        window.Show();
        Await(() => window.GetVisualDescendants().OfType<Views.WorkingCopy>().FirstOrDefault()?.DataContext is VM.WorkingCopy && repo.CurrentBranch != null);

        var wcView = window.GetVisualDescendants().OfType<Views.WorkingCopy>().First();
        var wcVm = (VM.WorkingCopy)wcView.DataContext!;
        var hasAmendCheckbox = wcView.GetVisualDescendants().OfType<CheckBox>()
            .Any(c => Equals(c.Content?.ToString(), "Amend") || Equals(c.Content?.ToString(), "修补"));
        var moreBtn = wcView.FindControl<Button>("CommitMoreActionsButton");
        Check(!hasAmendCheckbox && moreBtn != null && wcVm.CanOpenAmendLastCommit,
              "WorkingCopy omits regular Amend checkbox and exposes More actions Amend menu");
        Screenshot(window, "amend-more-actions-light");

        // 1. Protected 'main' already pushed blocks Amend
        File.AppendAllText(Path.Combine(amendRepoPath, "app.txt"), "protected tweak\n");
        Git(amendRepoPath, "add", "app.txt");
        var protAmendVm = new VM.AmendLastCommit(repo, wcVm);
        Await(() => protAmendVm.EnsureLoadedAsync().IsCompleted);
        Check(protAmendVm.IsAlreadyPushed && protAmendVm.BlockedByBranchProtection && !protAmendVm.CanConfirm && protAmendVm.GuideNewCommitAsPrimary,
              "Pushed commit on protected main blocks Amend rewrite and guides Create New Commit Instead");

        App.SetTheme("Dark", "");
        window.Width = 680;
        page.Popup = protAmendVm;
        Pump();
        Screenshot(window, "amend-protected-dark-narrow");
        page.CancelPopup();

        // 2. Unpushed local branch with newly staged file executes cleanly
        Git(amendRepoPath, "checkout", "-b", "feat/amend-ui");
        File.WriteAllText(Path.Combine(amendRepoPath, "feature.txt"), "draft v1\n");
        Git(amendRepoPath, "add", "feature.txt");
        Git(amendRepoPath, "commit", "-m", "feat: local draft");
        File.AppendAllText(Path.Combine(amendRepoPath, "feature.txt"), "extra line\n");
        Git(amendRepoPath, "add", "feature.txt");

        var unpushedAmendVm = new VM.AmendLastCommit(repo, wcVm);
        Await(() => unpushedAmendVm.EnsureLoadedAsync().IsCompleted);
        Check(unpushedAmendVm.IsUnpushedLocalOnly && unpushedAmendVm.HasNewlyStagedChanges &&
              unpushedAmendVm.CanConfirm && unpushedAmendVm.CommitMessage == "feat: local draft",
              "Unpushed branch Amend modal preloads message, lists newly staged changes, and enables confirmation");

        App.SetTheme("Light", "");
        window.Width = 980;
        page.Popup = unpushedAmendVm;
        Pump();
        Screenshot(window, "amend-unpushed-light");
        var confirmUnpushed = page.ProcessPopupAsync();
        Await(() => confirmUnpushed.IsCompleted);
        Pump();
        Check(page.Popup == null, "Unpushed Amend executes cleanly and closes modal");

        // 3. Pushed feature branch -> explicit opt-in -> remote offline partial failure -> Retry Push Only
        Git(amendRepoPath, "push", "-u", "origin", "feat/amend-ui");
        Git(amendRepoPath, "fetch", "origin");
        File.WriteAllText(Path.Combine(amendRepoPath, "retry-file.txt"), "retry payload\n");
        Git(amendRepoPath, "add", "retry-file.txt");

        var pushedAmendVm = new VM.AmendLastCommit(repo, wcVm);
        Await(() => pushedAmendVm.EnsureLoadedAsync().IsCompleted);
        Check(pushedAmendVm.IsAlreadyPushed && pushedAmendVm.CanOptInPushedRewrite &&
              !pushedAmendVm.ConfirmRewritePushedWithLease && !pushedAmendVm.CanConfirm && pushedAmendVm.GuideNewCommitAsPrimary,
              "Pushed feature branch Amend defaults to New Commit guide with lease rewrite checkbox unchecked");

        pushedAmendVm.ConfirmRewritePushedWithLease = true;
        Check(pushedAmendVm.CanConfirm, "Checking explicit lease rewrite opt-in enables Amend & Lease Push");

        App.SetLocale("en_US");
        page.Popup = pushedAmendVm;
        Pump();

        try
        {
            Directory.Move(amendOrigin, amendOriginMoved);
            var partialTask = page.ProcessPopupAsync();
            Await(() => partialTask.IsCompleted);
            Pump();
            var retryBtn = window.GetVisualDescendants().OfType<Button>()
                .FirstOrDefault(b => Equals(b.Content?.ToString(), App.Text("AmendLastCommit.RetryPushOnly")) && b.IsVisible);
            Check(pushedAmendVm.IsPushRetryOnlyState && page.Popup == pushedAmendVm &&
                  pushedAmendVm.ConfirmButtonText == App.Text("AmendLastCommit.RetryPushOnly") &&
                  retryBtn != null && retryBtn.IsEffectivelyEnabled && wcVm.HasTwoStepStatus && wcVm.CanRetryPushOnly,
                  "Partial Amend failure keeps popup open with enabled Retry Push Only button and syncs WorkingCopy banner");
            Screenshot(window, "amend-partial-retry-light");

            Directory.Move(amendOriginMoved, amendOrigin);
            var amendedLocalHead = Git(amendRepoPath, "rev-parse", "HEAD");
            var retryPushTask = page.ProcessPopupAsync();
            Await(() => retryPushTask.IsCompleted);
            Pump();
            Check(page.Popup == null &&
                  string.Equals(Git(amendOrigin, "rev-parse", "refs/heads/feat/amend-ui"), amendedLocalHead, StringComparison.Ordinal) &&
                  !wcVm.CanRetryPushOnly,
                  "Retry Push Only completes lease push without re-amending commit");
        }
        finally
        {
            if (Directory.Exists(amendOriginMoved) && !Directory.Exists(amendOrigin))
                Directory.Move(amendOriginMoved, amendOrigin);
            App.SetLocale("zh_CN");
            window.Close();
            _window = null;
        }
    }
}
