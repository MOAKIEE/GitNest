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
    }
}
