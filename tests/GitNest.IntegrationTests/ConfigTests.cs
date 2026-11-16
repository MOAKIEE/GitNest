using System.Diagnostics;
using System.Text;

using SourceGit.Commands;
using SourceGit.Models;

internal static partial class Program
{
    private static async Task RunConfigTestsAsync(string realGit, string root)
    {
        var work = Path.Combine(root, "config-suite");
        Directory.CreateDirectory(work);

        var globalCfg = Path.Combine(work, "global.gitconfig");
        var includeDir = Path.Combine(work, "included.gitconfig");
        var branchInclude = Path.Combine(work, "branch-feat.gitconfig");
        File.WriteAllText(includeDir, "[user]\n\temail = included@example.invalid\n[http \"https://corp.example.com\"]\n\tproxy = http://alice:s3cr3t@proxy.corp.invalid:8080\n");
        File.WriteAllText(branchInclude, "[user]\n\tname = Branch AI\n");
        File.WriteAllText(globalCfg,
            "[user]\n\tname = Global User\n\temail = global@example.invalid\n" +
            "[credential]\n\thelper = store\n\thelper = cache --timeout=60\n" +
            $"[include]\n\tpath = {includeDir.Replace('\\', '/')}\n" +
            $"[includeIf \"onbranch:feat/\"]\n\tpath = {branchInclude.Replace('\\', '/')}\n");

        Environment.SetEnvironmentVariable("GIT_CONFIG_GLOBAL", globalCfg);
        Environment.SetEnvironmentVariable("GIT_CONFIG_NOSYSTEM", "1");

        var repo = Path.Combine(work, "配置 仓库 & #1");
        await GitAsync(realGit, "init", "-b", "main", repo);
        await GitAsync(realGit, "-C", repo, "commit", "--allow-empty", "-m", "init");

        await GitAsync(realGit, "-C", repo, "remote", "add", "origin", "https://token:ghp_secret123@example.com/org/app.git");
        await GitAsync(realGit, "-C", repo, "remote", "set-url", "--add", "origin", "https://example.com/org/mirror.git");
        await GitAsync(realGit, "-C", repo, "remote", "set-url", "--add", "--push", "origin", "git@github.com:org/app.git");
        await GitAsync(realGit, "-C", repo, "config", "--local", "remote.origin.sshkey", "C:/keys/id_ed25519");
        await GitAsync(realGit, "-C", repo, "config", "--local", "--add", "credential.helper", "");
        await GitAsync(realGit, "-C", repo, "config", "--local", "--add", "credential.helper", "manager");
        await GitAsync(realGit, "-C", repo, "config", "--local", "http.extraheader", "AUTHORIZATION: bearer topsecret_hdr");
        await GitAsync(realGit, "-C", repo, "config", "--local", "http.proxy", "http://user:proxypass@127.0.0.1:7890");
        await GitAsync(realGit, "-C", repo, "config", "--local", "commit.gpgsign", "true");

        var repoCfgPath = Path.Combine(repo, ".git", "config");
        var globalHash = Hash(globalCfg);
        var incHash = Hash(includeDir);
        var branchIncHash = Hash(branchInclude);
        var repoHash = Hash(repoCfgPath);

        var cfg = new Config(repo);
        var snapshot = await cfg.ReadSnapshotAsync();
        Check(snapshot.IsReadable, "Config snapshot reads repository configuration cleanly");
        Check(snapshot.CurrentBranch == "main", "Config snapshot detects current branch name");
        Check(snapshot.GetEffectiveValue("user.name") == "Global User" && snapshot.GetEffectiveEntry("user.name")?.Scope == GitConfigScope.Global && !snapshot.GetEffectiveEntry("user.name")!.IsIncluded, "Global user.name is inherited when not overridden");
        Check(snapshot.GetEffectiveValue("user.email") == "included@example.invalid" && snapshot.GetEffectiveEntry("user.email")?.IsIncluded == true, "Included config file override is marked as included with its origin");

        var rawHelpers = snapshot.GetEntries("credential.helper");
        var effectiveHelpers = snapshot.GetEffectiveCredentialHelpers();
        Check(rawHelpers.Count == 4 && effectiveHelpers.Count == 1 && effectiveHelpers[0].Value == "manager", "Multi-value credential.helper chain preserves history and honors empty-string reset");

        var remotes = snapshot.GetRemotes();
        Check(remotes.Count == 1 && remotes[0].FetchUrls.Count == 2 && remotes[0].PushUrls.Count == 1 && remotes[0].FetchRefspecs.Count == 1 && remotes[0].SshKey?.Value == "C:/keys/id_ed25519", "Remote snapshot keeps multiple fetch/push URLs, refspecs and sshkey");

        var urlProxies = snapshot.GetUrlProxyOverrides();
        Check(urlProxies.Count == 1 && urlProxies[0].Pattern == "https://corp.example.com" && !urlProxies[0].Entry.DisplayValue.Contains("s3cr3t", StringComparison.Ordinal), "URL-specific proxy override is surfaced with credentials masked");

        var diagnostics = snapshot.BuildSanitizedDiagnostics();
        Check(!diagnostics.Contains("ghp_secret123", StringComparison.Ordinal) &&
              !diagnostics.Contains("topsecret_hdr", StringComparison.Ordinal) &&
              !diagnostics.Contains("proxypass", StringComparison.Ordinal) &&
              !diagnostics.Contains("s3cr3t", StringComparison.Ordinal) &&
              diagnostics.Contains("git@github.com:org/app.git", StringComparison.Ordinal),
              "Diagnostics redact secrets and embedded URL credentials while keeping SSH remote addresses");

        Check(Hash(globalCfg) == globalHash && Hash(includeDir) == incHash && Hash(branchInclude) == branchIncHash && Hash(repoCfgPath) == repoHash,
              "Snapshot reads are strictly read-only across all config files");

        // Subdirectory open and conditional onbranch include
        await GitAsync(realGit, "-C", repo, "checkout", "-b", "feat/ai-task");
        var subdir = Path.Combine(repo, "sub", "dir");
        Directory.CreateDirectory(subdir);
        var subSnapshot = await new Config(subdir).ReadSnapshotAsync();
        Check(subSnapshot.IsReadable && subSnapshot.GetEffectiveValue("user.name") == "Branch AI" && subSnapshot.GetEffectiveEntry("user.name")?.IsIncluded == true,
              "Opening a subdirectory resolves the enclosing repository and honors onbranch includeIf");
        Check(subSnapshot.LocalConfigPath.Replace('\\', '/').Equals(repoCfgPath.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase),
              "Subdirectory config resolves local origin to repository .git/config");

        // Runtime environment override
        var origCount = Environment.GetEnvironmentVariable("GIT_CONFIG_COUNT");
        var origKey0 = Environment.GetEnvironmentVariable("GIT_CONFIG_KEY_0");
        var origVal0 = Environment.GetEnvironmentVariable("GIT_CONFIG_VALUE_0");
        try
        {
            Environment.SetEnvironmentVariable("GIT_CONFIG_COUNT", "1");
            Environment.SetEnvironmentVariable("GIT_CONFIG_KEY_0", "user.name");
            Environment.SetEnvironmentVariable("GIT_CONFIG_VALUE_0", "Runtime Override");
            var runtimeSnap = await cfg.ReadSnapshotAsync();
            var runtimeEntry = runtimeSnap.GetEffectiveEntry("user.name");
            Check(runtimeEntry is { Scope: GitConfigScope.Command, IsRuntime: true, Value: "Runtime Override" },
                  "Runtime Git config environment override is reported as runtime Command scope");
        }
        finally
        {
            Environment.SetEnvironmentVariable("GIT_CONFIG_COUNT", origCount);
            Environment.SetEnvironmentVariable("GIT_CONFIG_KEY_0", origKey0);
            Environment.SetEnvironmentVariable("GIT_CONFIG_VALUE_0", origVal0);
        }

        // Scoped writes: local empty vs unset, multi-value guard, worktree scope guard
        await GitAsync(realGit, "-C", repo, "checkout", "main");
        var wtDenied = await cfg.SetScopedAsync(GitConfigWriteScope.Worktree, "user.name", "WT");
        Check(!wtDenied.IsSuccess && (await cfg.ReadSnapshotAsync()).WorktreeConfigEnabled == false,
              "Worktree write is refused without silently enabling extensions.worktreeConfig");

        var setEmpty = await cfg.SetScopedAsync(GitConfigWriteScope.Local, "user.name", "", unset: false, allowEmpty: true);
        Check(setEmpty.IsSuccess, "Setting an explicit empty local value succeeds");
        var emptySnap = await cfg.ReadSnapshotAsync();
        Check(emptySnap.GetEffectiveValue("user.name").Length == 0 && emptySnap.GetEffectiveEntry("user.name")?.Scope == GitConfigScope.Local && Hash(globalCfg) == globalHash,
              "Explicit empty local value overrides global inheritance without touching global config");

        var unsetLocal = await cfg.SetScopedAsync(GitConfigWriteScope.Local, "user.name", "", unset: true);
        Check(unsetLocal.IsSuccess, "Unsetting local override succeeds");
        var restoredSnap = await cfg.ReadSnapshotAsync();
        Check(restoredSnap.GetEffectiveValue("user.name") == "Global User" && restoredSnap.GetEffectiveEntry("user.name")?.Scope == GitConfigScope.Global,
              "Unsetting local override restores inherited global value");

        var multiOverwrite = await cfg.SetScopedAsync(GitConfigWriteScope.Local, "credential.helper", "single");
        Check(!multiOverwrite.IsSuccess && (await cfg.ReadSnapshotAsync()).GetEntriesInScope("credential.helper", GitConfigScope.Local).Count == 2,
              "Single-value write refuses to overwrite existing multi-value entries");

        // Linked worktree with extensions.worktreeConfig enabled
        var wtPath = Path.Combine(work, "wt-task");
        await GitAsync(realGit, "-C", repo, "worktree", "add", "-b", "task/cfg", wtPath);
        await GitAsync(realGit, "-C", repo, "config", "--local", "extensions.worktreeConfig", "true");
        var wtCfg = new Config(wtPath);
        var wtWrite = await wtCfg.SetScopedAsync(GitConfigWriteScope.Worktree, "user.name", "Worktree Author");
        Check(wtWrite.IsSuccess, "Worktree scoped write succeeds when extensions.worktreeConfig is enabled");
        var wtSnap = await wtCfg.ReadSnapshotAsync();
        var mainSnapAfterWt = await cfg.ReadSnapshotAsync();
        Check(wtSnap.GetEffectiveValue("user.name") == "Worktree Author" && wtSnap.GetEffectiveEntry("user.name")?.Scope == GitConfigScope.Worktree &&
              mainSnapAfterWt.GetEffectiveValue("user.name") == "Global User",
              "Worktree scoped setting affects only the target linked worktree");

        // Valueless boolean key (no '=') & signing status check
        File.AppendAllText(repoCfgPath, "\n[tag]\n\tgpgsign\n");
        var valuelessSnap = await cfg.ReadSnapshotAsync();
        Check(valuelessSnap.GetEffectiveBool("tag.gpgsign") == true && valuelessSnap.GetEffectiveEntry("tag.gpgsign")?.HasValue == false,
              "Valueless boolean config key parses as true");

        var missingKeySign = Config.InspectSigningStatus(valuelessSnap, new GitInstallation { Executable = realGit, Version = new System.Version(2, 45, 1), Status = GitInstallationStatus.Ready });
        Check(!missingKeySign.IsReady && missingKeySign.IssueKey == "Configure.Signing.MissingKey",
              "Signing dependency check reports missing signing key");

        await cfg.SetScopedAsync(GitConfigWriteScope.Local, "user.signingkey", "ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAITest");
        await cfg.SetScopedAsync(GitConfigWriteScope.Local, "gpg.format", "ssh");
        var sshReady = Config.InspectSigningStatus(await cfg.ReadSnapshotAsync(), new GitInstallation { Executable = realGit, Version = new System.Version(2, 45, 1), Status = GitInstallationStatus.Ready });
        Check(sshReady.IsReady, "SSH signing dependency check succeeds on Git >= 2.34 with key and ssh-keygen");
        var sshOldGit = Config.InspectSigningStatus(await cfg.ReadSnapshotAsync(), new GitInstallation { Executable = realGit, Version = new System.Version(2, 31, 0), Status = GitInstallationStatus.Ready });
        Check(!sshOldGit.IsReady && sshOldGit.IssueKey == "Configure.Signing.SshRequiresNewerGit",
              "SSH signing dependency check flags Git < 2.34.0");

        await cfg.SetScopedAsync(GitConfigWriteScope.Local, "gpg.format", "openpgp");
        await cfg.SetScopedAsync(GitConfigWriteScope.Local, "gpg.program", Path.Combine(work, "missing-gpg.exe"));
        var missingProgSign = Config.InspectSigningStatus(await cfg.ReadSnapshotAsync(), new GitInstallation { Executable = realGit, Version = new System.Version(2, 45, 1), Status = GitInstallationStatus.Ready });
        Check(!missingProgSign.IsReady && missingProgSign.IssueKey == "Configure.Signing.MissingProgram",
              "Signing dependency check reports missing configured signing program");

        // Bare repository config snapshot
        var bareRepo = Path.Combine(work, "bare.git");
        await GitAsync(realGit, "init", "--bare", bareRepo);
        var bareSnap = await new Config(bareRepo).ReadSnapshotAsync();
        Check(bareSnap.IsReadable && string.IsNullOrEmpty(bareSnap.TopLevel) && !string.IsNullOrEmpty(bareSnap.LocalConfigPath),
              "Bare repository configuration snapshot reads without failing on top-level worktree lookup");

        // Broken global config file fails cleanly
        File.WriteAllText(globalCfg, "[broken");
        var brokenSnap = await cfg.ReadSnapshotAsync();
        Check(!brokenSnap.IsReadable && !string.IsNullOrEmpty(brokenSnap.Error),
              "Malformed Git config reports a readable failure with error details");

        File.WriteAllText(globalCfg, string.Empty);

        await RunConfigureWorkflowTestsAsync(realGit, work, globalCfg);
        await RunConfigLockAndExternalSyncTestsAsync(realGit, work, globalCfg);
        await RunRepositorySettingsMigrationTestsAsync(realGit, work);
    }

    private static async Task RunConfigureWorkflowTestsAsync(string realGit, string root, string globalCfg)
    {
        // 1. Targeted remote URL editing preserves extra URLs and custom refspecs
        var remoteRepo = Path.Combine(root, "remote-preserve");
        await GitAsync(realGit, "init", "-b", "main", remoteRepo);
        await GitInAsync(realGit, remoteRepo, "config", "--local", "user.name", "Tester");
        await GitInAsync(realGit, remoteRepo, "config", "--local", "user.email", "tester@example.invalid");
        File.WriteAllText(Path.Combine(remoteRepo, "a.txt"), "hi\n");
        await GitInAsync(realGit, remoteRepo, "add", ".");
        await GitInAsync(realGit, remoteRepo, "commit", "-m", "init");

        await GitInAsync(realGit, remoteRepo, "remote", "add", "origin", "https://example.com/repo.git?x=1&a+b");
        await GitInAsync(realGit, remoteRepo, "remote", "set-url", "--add", "origin", "https://example.com/repo.git?x=12");
        await GitInAsync(realGit, remoteRepo, "remote", "set-url", "--push", "--add", "origin", "git@example.com:push1.git");
        await GitInAsync(realGit, remoteRepo, "remote", "set-url", "--push", "--add", "origin", "git@example.com:push2.git");
        await GitInAsync(realGit, remoteRepo, "config", "--local", "--add", "remote.origin.fetch", "+refs/notes/*:refs/notes/*");

        var remoteCmd = new SourceGit.Commands.Remote(remoteRepo);
        var editFetchRes = await remoteCmd.SetUrlTargetedAsync(
            "origin",
            "https://updated-primary.example/repo.git",
            "https://example.com/repo.git?x=1&a+b",
            isPush: false,
            isDelete: false);
        Check(editFetchRes.IsSuccess, "Targeted SetUrlTargetedAsync replaces only matching primary fetch URL");

        var afterRemoteSnap = await new Config(remoteRepo).ReadSnapshotAsync();
        var originRemote = afterRemoteSnap.GetRemotes().Single(x => x.Name == "origin");
        Check(originRemote.FetchUrls.Count == 2 &&
              originRemote.FetchUrls[0].Value == "https://updated-primary.example/repo.git" &&
              originRemote.FetchUrls[1].Value == "https://example.com/repo.git?x=12" &&
              originRemote.PushUrls.Count == 2 &&
              originRemote.FetchRefspecs.Count == 2,
              "Targeted remote URL update preserves secondary fetch URLs, push URLs, and custom fetch refspecs");

        var mismatchRes = await remoteCmd.SetUrlTargetedAsync(
            "origin",
            "https://other.example/repo.git",
            "https://nonexistent.example/old.git",
            isPush: false,
            isDelete: false);
        Check(!mismatchRes.IsSuccess, "Targeted remote URL update fails safely when expected old URL does not match disk");

        // 2. Upstream config set/unset works both with local tracking refs and unborn/unfetched branches
        var branchCmd = new SourceGit.Commands.Branch(remoteRepo, "main");
        var setUpRes = await branchCmd.SetUpstreamTargetedAsync("origin", "main");
        Check(setUpRes.IsSuccess, "SetUpstreamTargetedAsync sets local branch.<name>.remote/merge even before remote-tracking ref is fetched");

        var upSnap = await new Config(remoteRepo).ReadSnapshotAsync();
        Check(upSnap.GetEffectiveValue("branch.main.remote") == "origin" &&
              upSnap.GetEffectiveValue("branch.main.merge") == "refs/heads/main",
              "Branch upstream keys are written cleanly");

        var unsetUpRes = await branchCmd.UnsetUpstreamTargetedAsync();
        Check(unsetUpRes.IsSuccess, "UnsetUpstreamTargetedAsync cleanly removes upstream config");
        var afterUnsetUpSnap = await new Config(remoteRepo).ReadSnapshotAsync();
        Check(string.IsNullOrEmpty(afterUnsetUpSnap.GetEffectiveValue("branch.main.remote")) &&
              string.IsNullOrEmpty(afterUnsetUpSnap.GetEffectiveValue("branch.main.merge")),
              "UnsetUpstreamTargetedAsync leaves branch without upstream tracking keys");

        // 3. ViewModel end-to-end: inheritance vs explicit empty "" vs --unset, multi-value preservation, and diagnostics redaction
        File.WriteAllText(globalCfg,
            "[user]\n\tname = Global Author\n\temail = global@example.invalid\n" +
            "[http]\n\tproxy = http://global-proxy:8080\n" +
            "[credential]\n\thelper =\n\thelper = cache --timeout=60\n" +
            "[http \"https://secret.example\"]\n\tproxy = http://alice:super_secret_token_xyz@proxy.local:3128\n");

        var wfRepo = Path.Combine(root, "configure-vm");
        await GitAsync(realGit, "init", "-b", "main", wfRepo);
        await GitInAsync(realGit, wfRepo, "remote", "add", "origin", "https://example.com/a.git");
        await GitInAsync(realGit, wfRepo, "remote", "set-url", "--add", "origin", "https://example.com/b.git");
        await GitInAsync(realGit, wfRepo, "config", "--local", "--add", "remote.origin.fetch", "+refs/tags/*:refs/tags/*");
        await GitInAsync(realGit, wfRepo, "config", "--local", "custom.keep", "must-stay");
        await GitInAsync(realGit, wfRepo, "config", "--local", "user.name", "Local Base");

        var repoVm = new SourceGit.ViewModels.Repository(false, wfRepo, Path.Combine(wfRepo, ".git"));
        var cfgVm = new SourceGit.ViewModels.RepositoryConfigure(repoVm);
        await cfgVm.EnsureLoadedAsync();

        Check(!cfgVm.WorktreeConfigEnabled &&
              cfgVm.AvailableWriteScopes.All(s => s.Scope != GitConfigWriteScope.Worktree),
              "RepositoryConfigure hides worktree scope when extensions.worktreeConfig is disabled");
        Check(!cfgVm.UserNameField.IsInherited && cfgVm.UserNameField.SourceBadge == "Local" &&
              cfgVm.UserEmailField.IsInherited && cfgVm.UserEmailField.SourceBadge == "Global",
              "RepositoryConfigure identifies Local vs inherited Global field sources accurately");
        Check(cfgVm.CredentialHelpers.Count == 2 &&
              cfgVm.CredentialHelpers[0].Value.Contains("重置") &&
              cfgVm.UrlProxyOverrides.Count == 1 &&
              cfgVm.UrlProxyOverrides[0].Value.Contains("***") &&
              !cfgVm.SanitizedDiagnostics.Contains("super_secret_token_xyz", StringComparison.Ordinal),
              "RepositoryConfigure preserves credential helper chains and redacts secrets in UI/diagnostics");

        // Test explicit empty "" on inherited http.proxy vs --unset on local user.name
        cfgVm.HttpProxyField.MarkExplicitEmpty();
        cfgVm.UserNameField.MarkUnsetLayer();
        cfgVm.SelectedRemoteDraft!.PrimaryFetchUrl = "https://example.com/a-updated.git";
        var summary1 = await cfgVm.SaveAsync();
        Check(summary1.IsSuccess && summary1.SucceededCount == 3 && summary1.FailedCount == 0,
              "RepositoryConfigure saves explicit empty, unset, and primary remote URL edit cleanly");

        var verifySnap1 = await new Config(wfRepo).ReadSnapshotAsync();
        var localProxyEntries = verifySnap1.GetEntriesInScope("http.proxy", GitConfigScope.Local);
        Check(localProxyEntries.Count == 1 && localProxyEntries[0].HasValue && localProxyEntries[0].Value.Length == 0 &&
              verifySnap1.GetEffectiveValue("http.proxy").Length == 0,
              "Explicit empty \"\" shadows inherited global proxy instead of unsetting");
        Check(verifySnap1.GetEntriesInScope("user.name", GitConfigScope.Local).Count == 0 &&
              verifySnap1.GetEffectiveValue("user.name") == "Global Author",
              "Unset layer removes local override and restores global inherited user.name");
        Check(verifySnap1.GetEffectiveValue("custom.keep") == "must-stay" &&
              verifySnap1.GetEntries("credential.helper").Count == 2 &&
              verifySnap1.GetEffectiveCredentialHelpers().Count == 1 &&
              verifySnap1.GetRemotes().Single(x => x.Name == "origin").FetchUrls.Count == 2 &&
              verifySnap1.GetRemotes().Single(x => x.Name == "origin").FetchRefspecs.Count == 2,
              "Unrelated keys, multi-value helpers, extra remote URLs, and refspecs stay untouched");

        // 4. Higher-scope override detection when writing Global while Local shadows it
        await GitInAsync(realGit, wfRepo, "config", "--local", "user.email", "local-shadow@example.invalid");
        await cfgVm.ReloadAsync();
        cfgVm.TargetScope = GitConfigWriteScope.Global;
        cfgVm.UserEmailField.EditText = "global-new@example.invalid";
        var shadowSummary = await cfgVm.SaveAsync();
        Check(shadowSummary.IsWarning && shadowSummary.ShadowedCount == 1 &&
              shadowSummary.ShadowedLines.Any(l => l.Contains("user.email", StringComparison.Ordinal)),
              "Saving into Global when Local shadows it reports an explicit higher-scope override warning");

        // 5. Pre-save conflict check & partial failure isolation
        cfgVm.TargetScope = GitConfigWriteScope.Local;
        await cfgVm.ReloadAsync();
        await GitInAsync(realGit, wfRepo, "config", "--local", "user.email", "external-race@example.invalid");
        cfgVm.UserEmailField.EditText = "ui-overwrite@example.invalid";
        cfgVm.UserNameField.EditText = "Partial Winner";
        var conflictSummary = await cfgVm.SaveAsync();
        Check(conflictSummary.SucceededCount == 1 && conflictSummary.ConflictCount == 1 &&
              conflictSummary.FailureAndConflictLines.Any(l => l.Contains("user.email", StringComparison.Ordinal)),
              "Pre-save check skips overwriting externally changed key while reporting conflict accurately");

        var afterConflictSnap = await new Config(wfRepo).ReadSnapshotAsync();
        Check(afterConflictSnap.GetEffectiveValue("user.email") == "external-race@example.invalid" &&
              afterConflictSnap.GetEffectiveValue("user.name") == "Partial Winner",
              "External change on user.email was preserved while non-conflicting user.name saved cleanly");

        // 6. Partial failure (one valid field + incomplete upstream) does not report full success
        cfgVm.UserNameField.EditText = "Before Partial Fail";
        cfgVm.SelectedUpstreamRemote = "origin";
        cfgVm.UpstreamTargetBranch = string.Empty; // Invalid half-specified upstream
        var partialSummary = await cfgVm.SaveAsync();
        Check(partialSummary.IsError && partialSummary.SucceededCount == 1 && partialSummary.FailedCount == 1 &&
              partialSummary.FailureAndConflictLines.Any(l => l.Contains("upstream", StringComparison.Ordinal)),
              "Partial failure reports exact per-item failure rather than overall success");

        // 7. IssueTracker rules persist to .git/config and shared .issuetracker and match commit links
        await cfgVm.ReloadAsync();
        cfgVm.AddIssueTracker("LocalJira", @"PROJ-(\d+)", "https://jira.example.invalid/browse/PROJ-$1");
        cfgVm.SelectedIssueTracker!.IsShared = false;
        cfgVm.AddIssueTracker("SharedGitHub", @"#(\d+)", "https://github.example.invalid/team/repo/issues/$1");
        cfgVm.SelectedIssueTracker!.IsShared = true;
        await cfgVm.SaveAsync();

        var localIssueRegex = (await GitInAsync(realGit, wfRepo, "config", "--local", "--get", "issuetracker.LocalJira.regex")).Trim();
        var sharedIssueFile = Path.Combine(wfRepo, ".issuetracker");
        var sharedIssueRegex = (await GitInAsync(realGit, wfRepo, "config", "-f", sharedIssueFile, "--get", "issuetracker.SharedGitHub.regex")).Trim();

        var loadedRules = new List<SourceGit.Models.IssueTracker>();
        await new SourceGit.Commands.IssueTracker(wfRepo, true).ReadAllAsync(loadedRules, true);
        await new SourceGit.Commands.IssueTracker(wfRepo, false).ReadAllAsync(loadedRules, false);

        var collector = new InlineElementCollector();
        foreach (var rule in loadedRules)
            rule.Matches(collector, "Fix PROJ-42 and #101");
        var matchedUrls = Enumerable.Range(0, collector.Count)
            .Select(i => collector[i])
            .Where(e => e.Type == InlineElementType.Link)
            .Select(e => e.Link)
            .ToList();

        Check(localIssueRegex == @"PROJ-(\d+)" &&
              File.Exists(sharedIssueFile) &&
              sharedIssueRegex == @"#(\d+)" &&
              matchedUrls.Contains("https://jira.example.invalid/browse/PROJ-42") &&
              matchedUrls.Contains("https://github.example.invalid/team/repo/issues/101"),
              "RepositoryConfigure IssueTracker rules persist to .git/config and .issuetracker and match commit links");

        repoVm.Close();
        File.WriteAllText(globalCfg, string.Empty);
    }

    private static async Task RunConfigLockAndExternalSyncTestsAsync(string realGit, string work, string globalCfg)
    {
        var originBare = Path.Combine(work, "lock-origin.git");
        await InitBareRepoAsync(realGit, originBare);

        var mainRepo = Path.Combine(work, "lock-sync-repo");
        await InitRepoAsync(realGit, mainRepo, "main", "Base Author", "base@example.invalid");
        await GitInAsync(realGit, mainRepo, "remote", "add", "origin", originBare);
        await CommitFileAsync(realGit, mainRepo, "app.txt", "line1\n", "initial commit");
        await GitInAsync(realGit, mainRepo, "push", "-u", "origin", "main");
        await GitInAsync(realGit, mainRepo, "branch", "release/v1");

        var linkedWt = Path.Combine(work, "lock-linked-wt");
        await GitInAsync(realGit, mainRepo, "worktree", "add", linkedWt, "-b", "wt/config-lock");

        // 1. Scoped write lock awareness (config.lock, config.worktree.lock, global .lock) never deletes locks
        var commonDir = (await GitInAsync(realGit, mainRepo, "rev-parse", "--path-format=absolute", "--git-common-dir")).Trim();
        var repoConfigLock = Path.Combine(commonDir, "config.lock");
        File.WriteAllText(repoConfigLock, "locked by external git process\n", Utf8NoBom);

        var scopedCfgCmd = new Config(mainRepo);
        var localLockedRes = await scopedCfgCmd.SetScopedAsync(GitConfigWriteScope.Local, "user.name", "ShouldFail");
        var remoteLockedRes = await new SourceGit.Commands.Remote(mainRepo).AddTargetedAsync("upstream", originBare);
        var branchLockedRes = await new SourceGit.Commands.Branch(mainRepo, "main")
            .SetUpstreamTargetedAsync("origin", "main", allowUnfetchedOrUnborn: true);

        Check(!localLockedRes.IsSuccess &&
              File.Exists(repoConfigLock) &&
              (await GitInAsync(realGit, mainRepo, "config", "--local", "--get", "user.name")).Trim() == "Base Author",
              "Config.SetScopedAsync aborts when repo config.lock exists and preserves lock file untouched");
        Check(!remoteLockedRes.IsSuccess && File.Exists(repoConfigLock),
              "Remote.AddTargetedAsync aborts when config.lock exists without deleting lock file");
        Check(!branchLockedRes.IsSuccess && File.Exists(repoConfigLock),
              "Branch.SetUpstreamTargetedAsync aborts when config.lock exists without deleting lock file");
        File.Delete(repoConfigLock);

        await GitInAsync(realGit, mainRepo, "config", "--local", "extensions.worktreeConfig", "true");
        var linkedGitDir = (await GitInAsync(realGit, linkedWt, "rev-parse", "--path-format=absolute", "--git-dir")).Trim();
        var wtConfigLock = Path.Combine(linkedGitDir, "config.worktree.lock");
        File.WriteAllText(wtConfigLock, "locked worktree config\n", Utf8NoBom);
        var wtLockedRes = await new Config(linkedWt).SetScopedAsync(GitConfigWriteScope.Worktree, "user.name", "WTBlocked");
        Check(!wtLockedRes.IsSuccess && File.Exists(wtConfigLock),
              "Worktree-scoped config write aborts when config.worktree.lock exists and never deletes lock file");
        File.Delete(wtConfigLock);

        File.WriteAllText(globalCfg, string.Empty, Utf8NoBom);
        var globalLockPath = globalCfg + ".lock";
        File.WriteAllText(globalLockPath, "locked global config\n", Utf8NoBom);

        var globalLockedRes = await scopedCfgCmd.SetScopedAsync(GitConfigWriteScope.Global, "user.name", "GlobalBlocked");
        Check(!globalLockedRes.IsSuccess && File.Exists(globalLockPath),
              "Global-scoped config write aborts when global config .lock exists without deleting lock file");

        var repoVm = new SourceGit.ViewModels.Repository(false, mainRepo, Path.Combine(mainRepo, ".git"));
        var cfgVm = new SourceGit.ViewModels.RepositoryConfigure(repoVm);
        await cfgVm.EnsureLoadedAsync();
        cfgVm.TargetScope = GitConfigWriteScope.Global;
        cfgVm.UserNameField.EditText = "BlockedByGlobalLock";
        var globalVmSave = await cfgVm.SaveAsync();
        Check(globalVmSave.IsError && File.Exists(globalLockPath),
              "RepositoryConfigure.SaveAsync aborts on Global lock and preserves user draft");
        File.Delete(globalLockPath);

        // 2. External change detection: clean auto-reload vs dirty draft diff banner + includeIf onbranch
        var releaseInc = Path.Combine(work, "release-override.gitconfig");
        await GitAsync(realGit, "config", "--file", releaseInc, "user.name", "Release Bot");
        await GitAsync(realGit, "config", "--file", globalCfg, "includeIf.onbranch:release/*.path", releaseInc.Replace('\\', '/'));

        cfgVm.TargetScope = GitConfigWriteScope.Local;
        await cfgVm.ReloadAsync();
        Check(!cfgVm.HasExternalConfigChanges && cfgVm.UserNameField.EditText == "Base Author",
              "Clean RepositoryConfigure loads initial local baseline cleanly");

        await GitInAsync(realGit, mainRepo, "config", "--local", "user.name", "Clean External Update");
        await cfgVm.CheckExternalChangesAsync();
        Check(!cfgVm.HasExternalConfigChanges && cfgVm.UserNameField.EditText == "Clean External Update",
              "CheckExternalChangesAsync auto-reloads clean RepositoryConfigure without diff banner");

        await GitInAsync(realGit, mainRepo, "config", "--local", "--unset", "user.name");
        await GitInAsync(realGit, mainRepo, "config", "--local", "user.name", "Initial Before Conflict");
        await cfgVm.ReloadAsync();
        cfgVm.UserEmailField.EditText = "dirty-draft@example.invalid";

        await GitInAsync(realGit, mainRepo, "config", "--local", "--unset", "user.name");
        await GitInAsync(realGit, mainRepo, "checkout", "release/v1");
        await cfgVm.CheckExternalChangesAsync();

        Check(cfgVm.HasExternalConfigChanges &&
              cfgVm.ExternalConfigDiffItems.Any(d => d.TargetName == "user.name") &&
              cfgVm.UserEmailField.EditText == "dirty-draft@example.invalid",
              "Dirty RepositoryConfigure preserves drafts and shows ExternalConfigDiffItems on external branch includeIf change");

        await cfgVm.KeepDraftsWithExternalBaselineAsync();
        Check(!cfgVm.HasExternalConfigChanges && cfgVm.UserEmailField.EditText == "dirty-draft@example.invalid",
              "KeepDraftsWithExternalBaselineAsync updates baseline while keeping user draft intact");

        var afterKeepSave = await cfgVm.SaveAsync();
        var diskEmailAfterSave = (await GitInAsync(realGit, mainRepo, "config", "--local", "--get", "user.email")).Trim();
        var effectiveNameAfterSave = (await GitInAsync(realGit, mainRepo, "config", "--get", "user.name")).Trim();
        Check(!afterKeepSave.IsError &&
              diskEmailAfterSave == "dirty-draft@example.invalid" &&
              effectiveNameAfterSave == "Release Bot",
              "Saving after KeepDraftsWithExternalBaselineAsync writes non-conflicting draft while preserving external includeIf change");

        cfgVm.UserEmailField.EditText = "abandoned-draft@example.invalid";
        await GitInAsync(realGit, mainRepo, "config", "--local", "user.email", "accepted-external@example.invalid");
        await cfgVm.CheckExternalChangesAsync();
        Check(cfgVm.HasExternalConfigChanges,
              "Direct external edit to dirty key raises ExternalConfigChangeBanner");
        await cfgVm.AcceptExternalReloadAsync();
        Check(!cfgVm.HasExternalConfigChanges && cfgVm.UserEmailField.EditText == "accepted-external@example.invalid",
              "AcceptExternalReloadAsync discards conflicting draft and reloads external value");

        repoVm.Close();
        await GitInAsync(realGit, mainRepo, "worktree", "remove", "--force", linkedWt);
        File.WriteAllText(globalCfg, string.Empty, Utf8NoBom);
    }

    private static async Task RunRepositorySettingsMigrationTestsAsync(string realGit, string work)
    {
        var legacyRepo = Path.Combine(work, "legacy-settings-repo");
        await GitAsync(realGit, "init", "-b", "main", legacyRepo);
        var legacyGitDir = Path.Combine(legacyRepo, ".git");
        var settingsPath = Path.Combine(legacyGitDir, "gitnest.settings");
        var backupPath = settingsPath + ".bak";
        var legacyJson = """
            {
              "DefaultRemote": "upstream",
              "PreferredMergeMode": 1,
              "ConventionalTypesOverride": "feat,fix,docs",
              "EnableRecursiveWhenAutoUpdatingSubmodules": false,
              "AskBeforeAutoUpdatingSubmodules": false,
              "CommitTemplates": [
                {
                  "Name": "BranchFiles",
                  "Content": "${branch_name}: ${files:2}"
                }
              ],
              "UnprotectedBranches": [
                "feat/legacy"
              ]
            }
            """;
        await File.WriteAllTextAsync(settingsPath, legacyJson, Utf8NoBom);
        var legacyRawBytes = await File.ReadAllBytesAsync(settingsPath);

        RepositorySettings.ClearCacheForRepository(legacyGitDir);
        var loadedSettings = RepositorySettings.Get(legacyGitDir);
        Check(loadedSettings.DefaultRemote == "upstream" &&
              loadedSettings.PreferredMergeMode == 1 &&
              loadedSettings.ConventionalTypesOverride == "feat,fix,docs" &&
              loadedSettings.IsBranchProtectionDisabled("feat/legacy"),
              "Legacy gitnest.settings preserves all active settings on load");
        var bytesAfterReadOnlyLoad = await File.ReadAllBytesAsync(settingsPath);
        Check(legacyRawBytes.SequenceEqual(bytesAfterReadOnlyLoad) && !File.Exists(backupPath),
              "Read-only loading of legacy gitnest.settings leaves file untouched and writes no backup eagerly");

        loadedSettings.Save();
        Check(File.Exists(backupPath) && (await File.ReadAllBytesAsync(backupPath)).SequenceEqual(legacyRawBytes),
              "First migration write creates byte-identical backup of legacy gitnest.settings");
        using (var migratedDoc = System.Text.Json.JsonDocument.Parse(await File.ReadAllBytesAsync(settingsPath)))
        {
            var hasRemovedProps = migratedDoc.RootElement.TryGetProperty("CommitTemplates", out _) ||
                                  migratedDoc.RootElement.TryGetProperty("AskBeforeAutoUpdatingSubmodules", out _) ||
                                  migratedDoc.RootElement.TryGetProperty("EnableRecursiveWhenAutoUpdatingSubmodules", out _);
            Check(!hasRemovedProps &&
                  migratedDoc.RootElement.GetProperty("DefaultRemote").GetString() == "upstream" &&
                  migratedDoc.RootElement.GetProperty("PreferredMergeMode").GetInt32() == 1 &&
                  migratedDoc.RootElement.GetProperty("ConventionalTypesOverride").GetString() == "feat,fix,docs",
                  "Migrated gitnest.settings strips removed legacy keys while preserving active repository settings");
        }

        var bakBytesAfterFirstSave = await File.ReadAllBytesAsync(backupPath);
        loadedSettings.Save();
        Check((await File.ReadAllBytesAsync(backupPath)).SequenceEqual(bakBytesAfterFirstSave),
              "Subsequent Save calls do not overwrite initial migration backup");

        RepositorySettings.ClearCacheForRepository(legacyGitDir);
        var reloadedSettings = RepositorySettings.Get(legacyGitDir);
        Check(reloadedSettings.DefaultRemote == "upstream" &&
              reloadedSettings.PreferredMergeMode == 1 &&
              reloadedSettings.ConventionalTypesOverride == "feat,fix,docs" &&
              reloadedSettings.IsBranchProtectionDisabled("feat/legacy"),
              "Cleared and reloaded migrated gitnest.settings preserves all settings cleanly");

        await File.WriteAllTextAsync(settingsPath, legacyJson, Utf8NoBom);
        RepositorySettings.ClearCacheForRepository(legacyGitDir);
        RepositorySettings.Get(legacyGitDir).Save();
        var altBackupPath = settingsPath + ".legacy-1.bak";
        Check(File.Exists(altBackupPath) &&
              (await File.ReadAllBytesAsync(altBackupPath)).SequenceEqual(legacyRawBytes) &&
              (await File.ReadAllBytesAsync(backupPath)).SequenceEqual(bakBytesAfterFirstSave),
              "Legacy migration uses unique alternate backup filename when .bak already exists");
    }
}
