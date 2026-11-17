using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;
using SourceGit;
using VM = SourceGit.ViewModels;
using Views = SourceGit.Views;

internal static partial class Program
{
    static partial void RunSubmoduleLfs()
    {
        var globalCfg = Environment.GetEnvironmentVariable("GIT_CONFIG_GLOBAL")!;
        Git(_root, "config", "--file", globalCfg, "protocol.file.allow", "always");

        var subOrigin = Path.Combine(_root, "sub-lib-core");
        Git(_root, "init", "-b", "main", subOrigin);
        Git(subOrigin, "config", "user.name", "Sub Probe");
        Git(subOrigin, "config", "user.email", "sub@example.invalid");
        File.WriteAllText(Path.Combine(subOrigin, "core.txt"), "lib v1\n");
        Git(subOrigin, "add", ".");
        Git(subOrigin, "commit", "-m", "sub v1");

        var repoPath = Path.Combine(_root, "主仓-子模块与LFS探针");
        Git(_root, "init", "-b", "main", repoPath);
        Git(repoPath, "config", "user.name", "Probe User");
        Git(repoPath, "config", "user.email", "probe@example.invalid");
        File.WriteAllText(Path.Combine(repoPath, "README.md"), "# GitNest Submodule & LFS Probe\n");
        File.WriteAllText(Path.Combine(repoPath, ".gitattributes"), "*.bin filter=lfs diff=lfs merge=lfs -text\n");
        Directory.CreateDirectory(Path.Combine(repoPath, "assets"));
        var lfsSha = new string('b', 64);
        File.WriteAllText(Path.Combine(repoPath, "assets", "model.bin"), $"version https://git-lfs.github.com/spec/v1\noid sha256:{lfsSha}\nsize 2048\n");
        Git(repoPath, "add", ".");
        Git(repoPath, "commit", "-m", "initial with lfs pointer");

        Git(repoPath, "-c", "protocol.file.allow=always", "submodule", "add", subOrigin, "deps/lib-core");
        Git(repoPath, "commit", "-m", "add read-only submodule");
        Git(repoPath, "tag", "v1.0.0");
        Git(repoPath, "tag", "-a", "v1.0.1", "-m", "Release v1.0.1");

        File.WriteAllText(Path.Combine(subOrigin, "core.txt"), "lib v2\n");
        Git(subOrigin, "add", ".");
        Git(subOrigin, "commit", "-m", "sub v2");

        Git(repoPath, "checkout", "-b", "feat/sub-bump");
        Git(Path.Combine(repoPath, "deps", "lib-core"), "pull", subOrigin, "main");
        Git(repoPath, "add", "deps/lib-core");
        Git(repoPath, "commit", "-m", "bump deps/lib-core to v2");
        Git(repoPath, "checkout", "-b", "feat/sub-broken");
        Git(repoPath, "update-index", "--add", "--cacheinfo", "160000,0123456789abcdef0123456789abcdef01234567,deps/lib-core");
        Git(repoPath, "commit", "-m", "broken deps/lib-core pointer");
        Git(repoPath, "checkout", "main");
        Git(repoPath, "submodule", "update", "--recursive");

        App.SetTheme("Light", "");
        App.SetLocale("zh_CN");

        var launcher = AttachLauncher(repoPath);
        var repo = (VM.Repository)launcher.ActivePage.Data;
        repo.IsSubmoduleGroupExpanded = true;
        repo.IsTagGroupExpanded = true;

        var repoView = new Views.Repository { DataContext = repo };
        var window = _window = new Window
        {
            Content = repoView,
            Width = 1120,
            Height = 760,
            ShowInTaskbar = false,
            Title = "GitNest Submodule & LFS Probe",
        };
        window.Show();

        Await(() =>
            repo.Submodules.Count == 1 &&
            repo.Tags.Count == 2 &&
            repo.Branches.Any(b => b.IsLocal && b.Name == "feat/sub-bump") &&
            repo.Branches.Any(b => b.IsLocal && b.Name == "feat/sub-broken"));

        Check(repo.Tags.Any(t => t.Name == "v1.0.1" && t.IsAnnotated) && App.Text("Tag.Annotated") == "附注标签",
              "Read-only Tags group renders annotated tag label cleanly from zh_CN locale");
        Check(repo.Submodules.Any(s => s.Path == "deps/lib-core"),
              "Read-only Submodules group displays deps/lib-core");

        // Narrowed Git LFS toolbar menu (before & after install, zh_CN & en_US)
        static List<string> CollectAllMenuTexts(MenuItem root)
        {
            var texts = new List<string>();
            if (root.Header != null)
                texts.Add(root.Header.ToString() ?? string.Empty);
            foreach (var child in root.Items.OfType<MenuItem>())
                texts.AddRange(CollectAllMenuTexts(child));
            return texts;
        }
        static List<string> CollectContextMenuTexts(ContextMenu menu) =>
            menu.Items.OfType<MenuItem>().SelectMany(CollectAllMenuTexts).ToList();

        var toolbar = repoView.GetVisualDescendants().OfType<Views.RepositoryToolbar>().FirstOrDefault()
            ?? new Views.RepositoryToolbar();
        var lfsLabelsBeforeZh = CollectContextMenuTexts(toolbar.CreateGitLFSMenu(repo));
        Check(!lfsLabelsBeforeZh.Any(t => t.Contains("精简", StringComparison.Ordinal) || t.Contains('锁') || t.Contains("Prune", StringComparison.OrdinalIgnoreCase)) &&
              lfsLabelsBeforeZh.Any(t => t.Contains("状态：", StringComparison.Ordinal)) &&
              lfsLabelsBeforeZh.Any(t => t.Contains("高级 LFS 操作", StringComparison.Ordinal)),
              "Zh un-installed Git LFS toolbar menu omits Prune/Locks and groups Install under 高级 LFS 操作");

        var installTask = repo.InstallLFSAsync();
        Await(() => installTask.IsCompleted);
        Check(installTask.IsCompletedSuccessfully && repo.IsLFSEnabled(), "Installing Git LFS in UI probe repo succeeds");

        var lfsMenuLabelsZh = CollectContextMenuTexts(toolbar.CreateGitLFSMenu(repo));
        Check(lfsMenuLabelsZh.Any(t => t.Contains("添加追踪文件规则", StringComparison.Ordinal)) &&
              lfsMenuLabelsZh.Any(t => t.Contains("fetch", StringComparison.OrdinalIgnoreCase)) &&
              lfsMenuLabelsZh.Any(t => t.Contains("pull", StringComparison.OrdinalIgnoreCase)),
              "Zh Git LFS toolbar menu groups active LFS operations under 高级 LFS 操作");

        App.SetLocale("en_US");
        var lfsMenuLabelsEn = CollectContextMenuTexts(toolbar.CreateGitLFSMenu(repo));
        Check(!lfsMenuLabelsEn.Any(t => t.Contains("Prune", StringComparison.OrdinalIgnoreCase) || t.Contains("Lock", StringComparison.OrdinalIgnoreCase)) &&
              lfsMenuLabelsEn.Any(t => t.Contains("Advanced LFS Operations", StringComparison.Ordinal)) &&
              lfsMenuLabelsEn.Any(t => t == "Fetch") && lfsMenuLabelsEn.Any(t => t == "Pull") && lfsMenuLabelsEn.Any(t => t == "Push"),
              "En Git LFS toolbar menu omits Prune/Locks and preserves Fetch/Pull/Push");
        window.Close();

        // Checkout submodule opt-in default & 2-step confirmation & broken submodule partial failure
        App.SetTheme("Light", "");
        App.SetLocale("zh_CN");
        var bumpBranch = repo.Branches.Single(b => b.IsLocal && b.Name == "feat/sub-bump");
        var brokenBranch = repo.Branches.Single(b => b.IsLocal && b.Name == "feat/sub-broken");

        var checkoutVm = new VM.Checkout(repo, bumpBranch);
        Check(!checkoutVm.UpdateSubmodulesAfterSwitch && checkoutVm.ShowSubmoduleUpdateOption,
              "Checkout popup defaults UpdateSubmodulesAfterSwitch to false when submodules exist");

        launcher.ActivePage.Popup = checkoutVm;
        var checkoutHostWin = _window = new Window
        {
            Content = new Views.LauncherPage { DataContext = launcher.ActivePage },
            Width = 960,
            Height = 560,
            ShowInTaskbar = false,
            Title = "Checkout Submodule Opt-In Probe",
        };
        checkoutHostWin.Show();
        Pump();

        var defaultOptionCheck = checkoutHostWin.GetVisualDescendants()
            .OfType<CheckBox>()
            .FirstOrDefault(cb => cb.GetVisualDescendants().OfType<TextBlock>().Any(tb => tb.Text == App.Text("Checkout.UpdateSubmodulesAfterSwitch")));
        Check(defaultOptionCheck != null && defaultOptionCheck.IsChecked == false,
              "Checkout popup renders unchecked submodule update checkbox by default");
        Screenshot(checkoutHostWin, "checkout-submodule-default-light");

        checkoutVm.UpdateSubmodulesAfterSwitch = true;
        Pump();
        var phase1Done = launcher.ActivePage.ProcessPopupAsync();
        Await(() => phase1Done.IsCompleted);
        Pump();
        Check(checkoutVm.IsAwaitingSubmoduleConfirm &&
              checkoutVm.AffectedSubmodules.Contains("deps/lib-core") &&
              checkoutVm.ConfirmButtonText == App.Text("Checkout.Submodules.ConfirmUpdate") &&
              checkoutVm.CancelButtonText == App.Text("Checkout.Submodules.SkipAndClose"),
              "Phase 1 switches main branch and shows affected submodule paths & confirmation card");

        App.SetTheme("Dark", "");
        checkoutHostWin.Width = 600;
        Pump();
        var popupPanel = checkoutHostWin.GetVisualDescendants().OfType<ContentControl>().First(c => c.Name == "PopupPanel");
        var overflows = popupPanel.GetVisualDescendants().OfType<TextBlock>()
            .Where(t => t.IsVisible && t.Bounds.Width > 0)
            .Select(tb => (tb, pos: tb.TranslatePoint(new Point(0, 0), checkoutHostWin)))
            .Where(x => x.pos.HasValue && x.pos.Value.X + x.tb.Bounds.Width > checkoutHostWin.Bounds.Width + 2)
            .Select(x => $"{x.tb.Text} ({x.pos!.Value.X + x.tb.Bounds.Width:F1} > {checkoutHostWin.Bounds.Width:F1})")
            .ToList();
        Check(overflows.Count == 0,
              $"Dark narrow submodule confirm popup fits within 600px bounds (overflows: {string.Join("; ", overflows)})");
        Screenshot(checkoutHostWin, "checkout-submodule-confirm-dark-narrow");

        checkoutVm.RecursiveSubmoduleUpdate = true;
        var phase2Done = launcher.ActivePage.ProcessPopupAsync();
        Await(() => phase2Done.IsCompleted);
        Pump();
        Check(launcher.ActivePage.Popup == null, "Confirming phase 2 submodule update succeeds and closes checkout popup");

        App.SetTheme("Light", "");
        App.SetLocale("en_US");
        checkoutHostWin.Width = 680;
        var failCheckoutVm = new VM.Checkout(repo, brokenBranch) { UpdateSubmodulesAfterSwitch = true };
        launcher.ActivePage.Popup = failCheckoutVm;
        Pump();
        var failPhase1 = launcher.ActivePage.ProcessPopupAsync();
        Await(() => failPhase1.IsCompleted);
        Pump();
        var failPhase2 = launcher.ActivePage.ProcessPopupAsync();
        Await(() => failPhase2.IsCompleted);
        Pump();
        Check(failCheckoutVm.IsSubmoduleFailureResult && failCheckoutVm.HasResultView && launcher.ActivePage.Popup == failCheckoutVm,
              $"Broken submodule update keeps its partial result [failure={failCheckoutVm.IsSubmoduleFailureResult}, result={failCheckoutVm.HasResultView}, popup={launcher.ActivePage.Popup == failCheckoutVm}, summary={failCheckoutVm.ResultSummary}]");
        Screenshot(checkoutHostWin, "checkout-submodule-partial-failure-light");

        checkoutHostWin.Close();
        _window = null;
        App.SetLocale("zh_CN");
        Git(_root, "config", "--file", globalCfg, "--unset-all", "protocol.file.allow");
    }
}
