using System.Diagnostics;
using System.Text;

using SourceGit.Commands;
using SourceGit.Models;
using SourceGit.ViewModels;

internal static partial class Program
{
    private static async Task RunPhase37SyncTestsAsync(string realGit, string root)
    {
        var phaseRoot = Path.Combine(root, "phase-3.7-sync");
        Directory.CreateDirectory(phaseRoot);
        var utf8NoBom = new UTF8Encoding(false);

        var originBare = Path.Combine(phaseRoot, "origin.git");
        await GitAsync(realGit, "init", "--bare", originBare);

        var mainRepo = Path.Combine(phaseRoot, "main-repo");
        await GitAsync(realGit, "init", "-b", "main", mainRepo);
        await GitAsync(realGit, "-C", mainRepo, "config", "user.name", "Base Author");
        await GitAsync(realGit, "-C", mainRepo, "config", "user.email", "base@example.invalid");
        await GitAsync(realGit, "-C", mainRepo, "remote", "add", "origin", originBare);

        var trackedFile = Path.Combine(mainRepo, "app.txt");
        File.WriteAllText(trackedFile, "line1\n", utf8NoBom);
        await GitAsync(realGit, "-C", mainRepo, "add", "app.txt");
        await GitAsync(realGit, "-C", mainRepo, "commit", "-m", "initial commit");
        await GitAsync(realGit, "-C", mainRepo, "push", "-u", "origin", "main");

        // Prepare release/v1 branch for includeIf onbranch test + linked worktree
        await GitAsync(realGit, "-C", mainRepo, "branch", "release/v1");
        var linkedWt = Path.Combine(phaseRoot, "linked-wt");
        await GitAsync(realGit, "-C", mainRepo, "worktree", "add", linkedWt, "-b", "wt/config-lock");

        // ------------------------------------------------------------
        // 1. Scoped write lock awareness (config.lock, config.worktree.lock, global .lock) never deletes locks
        // ------------------------------------------------------------
        var commonDir = (await GitAsync(realGit, "-C", mainRepo, "rev-parse", "--path-format=absolute", "--git-common-dir")).Trim();
        var repoConfigLock = Path.Combine(commonDir, "config.lock");
        File.WriteAllText(repoConfigLock, "locked by external git process\n", utf8NoBom);

        var scopedCfgCmd = new Config(mainRepo);
        var localLockedRes = await scopedCfgCmd.SetScopedAsync(GitConfigWriteScope.Local, "user.name", "ShouldFail");
        var remoteLockedRes = await new SourceGit.Commands.Remote(mainRepo).AddTargetedAsync("upstream", originBare);
        var branchLockedRes = await new SourceGit.Commands.Branch(mainRepo, "main")
            .SetUpstreamTargetedAsync("origin", "main", allowUnfetchedOrUnborn: true);

        Check(!localLockedRes.IsSuccess &&
              File.Exists(repoConfigLock) &&
              (await GitAsync(realGit, "-C", mainRepo, "config", "--local", "--get", "user.name")).Trim() == "Base Author",
              "Config.SetScopedAsync aborts when repo config.lock exists and preserves lock file untouched");
        Check(!remoteLockedRes.IsSuccess && File.Exists(repoConfigLock),
              "Remote.AddTargetedAsync aborts when config.lock exists without deleting lock file");
        Check(!branchLockedRes.IsSuccess && File.Exists(repoConfigLock),
              "Branch.SetUpstreamTargetedAsync aborts when config.lock exists without deleting lock file");

        File.Delete(repoConfigLock);

        // Enable extensions.worktreeConfig and test linked config.worktree.lock
        await GitAsync(realGit, "-C", mainRepo, "config", "--local", "extensions.worktreeConfig", "true");
        var linkedGitDir = (await GitAsync(realGit, "-C", linkedWt, "rev-parse", "--path-format=absolute", "--git-dir")).Trim();
        var wtConfigLock = Path.Combine(linkedGitDir, "config.worktree.lock");
        File.WriteAllText(wtConfigLock, "locked worktree config\n", utf8NoBom);

        var wtCfgCmd = new Config(linkedWt);
        var wtLockedRes = await wtCfgCmd.SetScopedAsync(GitConfigWriteScope.Worktree, "user.name", "WTBlocked");
        Check(!wtLockedRes.IsSuccess && File.Exists(wtConfigLock),
              "Worktree-scoped config write aborts when config.worktree.lock exists and never deletes lock file");
        File.Delete(wtConfigLock);

        // Test Global .lock enforcement
        var globalPath = Environment.GetEnvironmentVariable("GIT_CONFIG_GLOBAL")!;
        File.WriteAllText(globalPath, string.Empty, utf8NoBom);
        var globalLockPath = globalPath + ".lock";
        File.WriteAllText(globalLockPath, "locked global config\n", utf8NoBom);

        var globalLockedRes = await scopedCfgCmd.SetScopedAsync(GitConfigWriteScope.Global, "user.name", "GlobalBlocked");
        Check(!globalLockedRes.IsSuccess && File.Exists(globalLockPath),
              "Global-scoped config write aborts when global config .lock exists without deleting lock file");

        var repoVm = new SourceGit.ViewModels.Repository(false, mainRepo, Path.Combine(mainRepo, ".git"));
        var cfgVm = new RepositoryConfigure(repoVm);
        await cfgVm.EnsureLoadedAsync();
        cfgVm.TargetScope = GitConfigWriteScope.Global;
        cfgVm.UserNameField.EditText = "BlockedByGlobalLock";
        var globalVmSave = await cfgVm.SaveAsync();
        Check(globalVmSave.IsError && File.Exists(globalLockPath),
              "RepositoryConfigure.SaveAsync aborts on Global lock and preserves user draft");
        File.Delete(globalLockPath);

        // ------------------------------------------------------------
        // 2. External change detection: clean auto-reload vs dirty draft diff banner + includeIf onbranch
        // ------------------------------------------------------------
        var releaseInc = Path.Combine(phaseRoot, "release-override.gitconfig");
        await GitAsync(realGit, "config", "--file", releaseInc, "user.name", "Release Bot");
        var releaseIncNormalized = releaseInc.Replace('\\', '/');
        await GitAsync(realGit, "config", "--file", globalPath, "includeIf.onbranch:release/*.path", releaseIncNormalized);

        cfgVm.TargetScope = GitConfigWriteScope.Local;
        await cfgVm.ReloadAsync();
        Check(!cfgVm.HasExternalConfigChanges && cfgVm.UserNameField.EditText == "Base Author",
              "Clean RepositoryConfigure loads initial local baseline cleanly");

        // External write while editor is clean -> CheckExternalChangesAsync auto-reloads seamlessly
        await GitAsync(realGit, "-C", mainRepo, "config", "--local", "user.name", "Clean External Update");
        await cfgVm.CheckExternalChangesAsync();
        Check(!cfgVm.HasExternalConfigChanges && cfgVm.UserNameField.EditText == "Clean External Update",
              "CheckExternalChangesAsync auto-reloads clean RepositoryConfigure without interrupting user with diff banner");

        // Now dirty a draft (user.email) while external changes user.name AND switches branch triggering includeIf onbranch
        await GitAsync(realGit, "-C", mainRepo, "config", "--local", "--unset", "user.name");
        await GitAsync(realGit, "-C", mainRepo, "config", "--local", "user.name", "Initial Before Conflict");
        await cfgVm.ReloadAsync();
        cfgVm.UserEmailField.EditText = "dirty-draft@example.invalid";

        await GitAsync(realGit, "-C", mainRepo, "config", "--local", "--unset", "user.name");
        await GitAsync(realGit, "-C", mainRepo, "checkout", "release/v1");
        await cfgVm.CheckExternalChangesAsync();

        Check(cfgVm.HasExternalConfigChanges &&
              cfgVm.ExternalConfigDiffItems.Any(d => d.TargetName == "user.name") &&
              cfgVm.UserEmailField.EditText == "dirty-draft@example.invalid",
              "Dirty RepositoryConfigure preserves drafts and shows ExternalConfigDiffItems on external branch includeIf change");

        // Keep drafts with external baseline -> keeps userEmail draft, clears banner, saves cleanly!
        await cfgVm.KeepDraftsWithExternalBaselineAsync();
        Check(!cfgVm.HasExternalConfigChanges && cfgVm.UserEmailField.EditText == "dirty-draft@example.invalid",
              "KeepDraftsWithExternalBaselineAsync updates baseline while keeping user draft intact");

        var afterKeepSave = await cfgVm.SaveAsync();
        var diskEmailAfterSave = (await GitAsync(realGit, "-C", mainRepo, "config", "--local", "--get", "user.email")).Trim();
        var effectiveNameAfterSave = (await GitAsync(realGit, "-C", mainRepo, "config", "--get", "user.name")).Trim();
        Check(!afterKeepSave.IsError &&
              diskEmailAfterSave == "dirty-draft@example.invalid" &&
              effectiveNameAfterSave == "Release Bot",
              "Saving after KeepDraftsWithExternalBaselineAsync writes non-conflicting draft while preserving external includeIf change");

        // Test AcceptExternalReloadAsync drops dirty draft and loads external value
        cfgVm.UserEmailField.EditText = "abandoned-draft@example.invalid";
        await GitAsync(realGit, "-C", mainRepo, "config", "--local", "user.email", "accepted-external@example.invalid");
        await cfgVm.CheckExternalChangesAsync();
        Check(cfgVm.HasExternalConfigChanges,
              "Direct external edit to dirty key raises ExternalConfigChangeBanner");
        await cfgVm.AcceptExternalReloadAsync();
        Check(!cfgVm.HasExternalConfigChanges && cfgVm.UserEmailField.EditText == "accepted-external@example.invalid",
              "AcceptExternalReloadAsync discards conflicting draft and reloads external value");

        // Switch back to main before remaining tests
        await GitAsync(realGit, "-C", mainRepo, "checkout", "main");

        // ------------------------------------------------------------
        // 3. Confirmation snapshot stale invalidation in DiscardBackupService
        // ------------------------------------------------------------
        File.WriteAllText(trackedFile, "line1\nlocal edit 1\n", utf8NoBom);
        var discardChange = new Change { Path = "app.txt", Index = ChangeState.None, WorkTree = ChangeState.Modified };
        var preflight1 = await DiscardBackupService.InspectDiscardAsync(mainRepo, [discardChange], DiscardBaselineKind.RestoreToHead);
        Check(preflight1.CanProceed && preflight1.ConfirmationSnapshot != null,
              "DiscardBackupService.InspectDiscardAsync captures ConfirmationSnapshot");

        // Mutate target file after confirmation dialog opened
        File.WriteAllText(trackedFile, "line1\nexternal appended edit 2\n", utf8NoBom);
        var journal = new OperationJournalStore(Path.Combine(phaseRoot, "journal.json"));
        var staleDiscard = await DiscardBackupService.ExecuteDiscardAsync(
            mainRepo,
            [discardChange],
            DiscardBaselineKind.RestoreToHead,
            journalStore: journal,
            expectedPreflight: preflight1);
        Check(!staleDiscard.Success &&
              staleDiscard.BlockedBySnapshot &&
              File.ReadAllText(trackedFile, utf8NoBom).Contains("external appended edit 2", StringComparison.Ordinal),
              "ExecuteDiscardAsync blocks execution when expectedPreflight target file mutated externally without losing new content");

        var preflight2 = await DiscardBackupService.InspectDiscardAsync(mainRepo, [discardChange], DiscardBaselineKind.RestoreToHead);
        var freshDiscard = await DiscardBackupService.ExecuteDiscardAsync(
            mainRepo,
            [discardChange],
            DiscardBaselineKind.RestoreToHead,
            journalStore: journal,
            expectedPreflight: preflight2);
        Check(freshDiscard.Success && File.ReadAllText(trackedFile, utf8NoBom) == "line1\n",
              "Re-inspecting discard snapshot allows safe discard after external modification review");

        // ------------------------------------------------------------
        // 4. Watcher admin change detection + Repository Close safety
        // ------------------------------------------------------------
        Check(Watcher.IsWorktreeAdminChange("worktrees/wt1") &&
              Watcher.IsWorktreeAdminChange("worktrees/wt1/HEAD") &&
              Watcher.IsWorktreeAdminChange("worktrees/wt1/locked") &&
              Watcher.IsWorktreeAdminChange("worktrees/wt1/config.worktree") &&
              !Watcher.IsWorktreeAdminChange("worktrees/wt1/index.lock") &&
              !Watcher.IsWorktreeAdminChange("objects/pack/pack-123.idx"),
              "Watcher.IsWorktreeAdminChange matches linked worktree metadata files accurately");

        var cfgChangedCount = 0;
        repoVm.ConfigurationStateChanged += () => Interlocked.Increment(ref cfgChangedCount);
        repoVm.Close();
        repoVm.RefreshConfigurationState();
        await Task.Delay(150);
        Check(cfgChangedCount == 0,
              "Repository.Close prevents post-close RefreshConfigurationState dispatch cleanly");

        // Cleanup linked worktree & restore clean global config
        await GitAsync(realGit, "-C", mainRepo, "worktree", "remove", "--force", linkedWt);
        File.WriteAllText(globalPath, string.Empty, utf8NoBom);
    }
}
