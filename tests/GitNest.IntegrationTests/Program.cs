using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

using SourceGit.Models;
using NativeOS = SourceGit.Native.OS;

internal static partial class Program
{
    private const string FakeModeVariable = "GITNEST_TEST_GIT_MODE";
    private static readonly Encoding Utf8NoBom = new UTF8Encoding(false);
    private static int _checks;
    private static readonly HashSet<string> _selectedSuites = new(StringComparer.OrdinalIgnoreCase);

    private static readonly (string Name, string Description)[] SuiteCatalog =
    [
        ("core", "Git executable probing, commit identity, unborn reads, locale/assembly parity, codecs & archive/stats"),
        ("update", "Release SemVer comparison and UpdateChecker GitHub release parsing"),
        ("worktree", "Worktree status queries, task metadata store, editor launcher, admin watcher & close safety"),
        ("comparison", "Task comparison (MergeBase/TwoTips), renames, binary stats, snapshot invalidation & security"),
        ("history-diff", "Large history bounded queries, commit search, CJK/special path CompareRevisions & Diff/IsBinary"),
        ("config", "Scoped Git config, locks, external change reload/drafts, RepositoryConfigure, settings migration & IssueTracker"),
        ("operation-safety", "Operation snapshots, write guard serialization, BackupRefs, journal reconciliation & external CLI state protection"),
        ("commit", "Staging/unstaging, partial staging, identity prompt/guard, index.lock, pre-commit hook & commit.template safety"),
        ("sync", "Sync overview, Fetch, ff-only Pull, single-branch/revision Push, two-step Commit&Push retry & auth failure redaction"),
        ("amend", "Dedicated AmendLastCommit preflight, branch protection, lease push, race rejection & outage retry"),
        ("branch-delete", "Local/remote branch deletion, multi-remote upstream, unmerged -d/-D, split/multi push URLs, lease & protection"),
        ("worktree-delete", "Worktree preflight, ignored/locked/nested/submodule guards, detached backup, orchestrated cleanup & prune"),
        ("merge", "Directed FF/diverged merge, DontCommit, remote snapshot, conflict auto-abort/ownership & external conflict protection"),
        ("revert", "Single-commit revert, merge-commit block, revert conflict auto-abort/ownership, hook & signing classification"),
        ("discard", "Dual-baseline discard, 30-day backup restore/expiry, queued snapshot guard, rename collision & Recycle Bin safety"),
        ("stash-ignore", "Stash push/apply/SHA-shift/conflict retention, branch-switch StashAndReapply & IgnoreRuleService"),
        ("submodule-lfs", "Read-only tags/submodules, clone submodule update, Git LFS track/diff & Checkout submodule opt-in confirm"),
        ("history-reset", "Local Commit Reset guards, mixed/hard modes, ignored-file preservation, checked backup restore & ResetWithoutCheckout"),
        ("remote-rewrite", "Branch protection overrides, offline/diverged guards, split/multi push URLs, lease race, timeout ls-remote & journal verify"),
    ];

    private static void AddRequestedSuites(string raw)
    {
        foreach (var token in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!SuiteCatalog.Any(s => string.Equals(s.Name, token, StringComparison.OrdinalIgnoreCase)))
                throw new ArgumentException($"Unknown suite '{token}'. Available: {string.Join(", ", SuiteCatalog.Select(s => s.Name))}");
            _selectedSuites.Add(token);
        }
    }

    private static bool ShouldRunSuite(string name) =>
        _selectedSuites.Count == 0 || _selectedSuites.Contains(name);

    public static async Task<int> Main(string[] args)
    {
        var remaining = new List<string>(args.Length);
        for (int i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (arg == "--list-suites")
            {
                foreach (var (name, _) in SuiteCatalog)
                    Console.WriteLine(name);
                return 0;
            }
            if (arg == "--list-suites-detailed")
            {
                foreach (var (name, desc) in SuiteCatalog)
                    Console.WriteLine($"{name}\t{desc}");
                return 0;
            }
            if (arg == "--suite" && i + 1 < args.Length)
            {
                AddRequestedSuites(args[++i]);
                continue;
            }
            if (arg.StartsWith("--suite=", StringComparison.Ordinal))
            {
                AddRequestedSuites(arg["--suite=".Length..]);
                continue;
            }
            remaining.Add(arg);
        }

        args = remaining.ToArray();
        if (args.Length > 0)
        {
            if (string.Equals(Environment.GetEnvironmentVariable(FakeModeVariable), "gpg-fail", StringComparison.Ordinal))
            {
                await Console.Error.WriteLineAsync("gpg: skipped \"BADKEY\": No secret key");
                await Console.Error.WriteLineAsync("gpg: signing failed: No secret key");
                return 2;
            }
            if (Environment.GetEnvironmentVariable("GITNEST_TEST_TOOL_OUTPUT") is { } outputFile)
            {
                File.WriteAllText(outputFile, Environment.CurrentDirectory + "\n" + string.Join("\n", args));
                return 0;
            }
            return args is ["--version"] ? await RunFakeGitAsync() : 1;
        }

        try
        {
            await RunTestsAsync();
            Console.WriteLine($"Passed {_checks} checks.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 1;
        }
    }

    private static async Task<int> RunFakeGitAsync()
    {
        switch (Environment.GetEnvironmentVariable(FakeModeVariable))
        {
            case "timeout":
                await Task.Delay(TimeSpan.FromSeconds(20));
                return 0;
            case "stderr":
                await Console.Error.WriteAsync(new string('x', 256 * 1024));
                Console.WriteLine("git version 2.45.1.windows.1");
                return 0;
            case "old": Console.WriteLine("git version 2.30.9"); return 0;
            case "boundary": Console.WriteLine("git version 2.31.0"); return 0;
            case "apple": Console.WriteLine("git version 2.39.5 (Apple Git-154)"); return 0;
            case "overflow": Console.WriteLine("git version 99999999999999999.1.0"); return 0;
            case "nonzero": Console.WriteLine("git version 2.45.1"); return 7;
            case "invalid": Console.WriteLine("some other program 2.45.1"); return 0;
            default: Console.WriteLine("git version 2.45.1.windows.1"); return 0;
        }
    }

    private static async Task RunTestsAsync()
    {
        var originalMode = Environment.GetEnvironmentVariable(FakeModeVariable);
        var originalGlobal = Environment.GetEnvironmentVariable("GIT_CONFIG_GLOBAL");
        var originalNoSystem = Environment.GetEnvironmentVariable("GIT_CONFIG_NOSYSTEM");
        var originalGit = NativeOS.GitExecutable;
        var originalGitDir = Environment.GetEnvironmentVariable("GIT_DIR");
        var originalWorkTree = Environment.GetEnvironmentVariable("GIT_WORK_TREE");
        var originalConfigDir = NativeOS.BasicDirectories.ConfigDir;
        var originalCacheDir = NativeOS.BasicDirectories.CacheDir;
        var root = Path.Combine(Path.GetTempPath(), "GitNest-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        NativeOS.BasicDirectories.ConfigDir = Path.Combine(root, ".gitnest-config");
        NativeOS.BasicDirectories.CacheDir = Path.Combine(root, ".gitnest-cache");
        Directory.CreateDirectory(NativeOS.BasicDirectories.ConfigDir);
        Directory.CreateDirectory(NativeOS.BasicDirectories.CacheDir);
        try
        {
            if (ShouldRunSuite("core"))
                await RunCoreGitProbeTestsAsync(root);

            var realGit = Environment.GetEnvironmentVariable("GITNEST_TEST_GIT_EXECUTABLE") ?? NativeOS.FindGitExecutable();
            var installed = await GitInstallation.InspectAsync(realGit);
            if (!installed.IsReady)
                throw new InvalidOperationException("Git executable is not ready: " + realGit);
            if (ShouldRunSuite("core"))
                Check(installed.IsReady, "The installed Git for Windows passes the real probe");
            NativeOS.UseGitInstallation(installed);

            Environment.SetEnvironmentVariable("GIT_DIR", null);
            Environment.SetEnvironmentVariable("GIT_WORK_TREE", null);
            Environment.SetEnvironmentVariable("GIT_CONFIG_NOSYSTEM", "1");
            var global = Path.Combine(root, "global.gitconfig");
            File.WriteAllText(global, string.Empty);
            Environment.SetEnvironmentVariable("GIT_CONFIG_GLOBAL", global);

            if (ShouldRunSuite("core"))
                await RunCoreSuiteAsync(realGit, root, global);

            File.WriteAllText(global, string.Empty);
            if (ShouldRunSuite("update"))
                await RunUpdateTestsAsync();
            if (ShouldRunSuite("worktree"))
                await RunWorktreeTestsAsync(realGit, root);
            if (ShouldRunSuite("comparison"))
                await RunComparisonTestsAsync(realGit, root);
            if (ShouldRunSuite("history-diff"))
                await RunHistoryDiffTestsAsync(realGit, root);
            if (ShouldRunSuite("config"))
                await RunConfigTestsAsync(realGit, root);
            if (ShouldRunSuite("operation-safety"))
                await RunOperationSafetyTestsAsync(realGit, root);
            if (ShouldRunSuite("commit"))
                await RunCommitTestsAsync(realGit, root);
            if (ShouldRunSuite("sync"))
                await RunSyncTestsAsync(realGit, root);
            if (ShouldRunSuite("amend"))
                await RunAmendTestsAsync(realGit, root);
            if (ShouldRunSuite("branch-delete"))
                await RunBranchDeleteTestsAsync(realGit, root);
            if (ShouldRunSuite("worktree-delete"))
                await RunWorktreeDeleteTestsAsync(realGit, root);
            if (ShouldRunSuite("merge"))
                await RunMergeTestsAsync(realGit, root);
            if (ShouldRunSuite("revert"))
                await RunRevertTestsAsync(realGit, root);
            if (ShouldRunSuite("discard"))
                await RunDiscardTestsAsync(realGit, root);
            if (ShouldRunSuite("stash-ignore"))
                await RunStashIgnoreTestsAsync(realGit, root);
            if (ShouldRunSuite("submodule-lfs"))
                await RunSubmoduleLfsTestsAsync(realGit, root);
            if (ShouldRunSuite("history-reset"))
                await RunHistoryResetTestsAsync(realGit, root);
            if (ShouldRunSuite("remote-rewrite"))
                await RunRemoteRewriteTestsAsync(realGit, root);
        }
        finally
        {
            Environment.SetEnvironmentVariable(FakeModeVariable, originalMode);
            Environment.SetEnvironmentVariable("GIT_CONFIG_GLOBAL", originalGlobal);
            Environment.SetEnvironmentVariable("GIT_CONFIG_NOSYSTEM", originalNoSystem);
            Environment.SetEnvironmentVariable("GIT_DIR", originalGitDir);
            Environment.SetEnvironmentVariable("GIT_WORK_TREE", originalWorkTree);
            NativeOS.GitExecutable = originalGit;
            NativeOS.BasicDirectories.ConfigDir = originalConfigDir;
            NativeOS.BasicDirectories.CacheDir = originalCacheDir;
            var resolvedRoot = Path.GetFullPath(root);
            var temporaryDirectory = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!resolvedRoot.StartsWith(temporaryDirectory, StringComparison.OrdinalIgnoreCase) ||
                !Path.GetFileName(resolvedRoot).StartsWith("GitNest-tests-", StringComparison.Ordinal))
                throw new InvalidOperationException("Unexpected test cleanup directory.");
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    foreach (var file in Directory.EnumerateFiles(resolvedRoot, "*", SearchOption.AllDirectories))
                        File.SetAttributes(file, FileAttributes.Normal);
                    Directory.Delete(resolvedRoot, true);
                    break;
                }
                catch (IOException) when (attempt < 15)
                {
                    await Task.Delay(200);
                }
            }
        }
    }

    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    private static async Task<string> GitAsync(string git, params string[] arguments)
    {
        var (exitCode, output, error) = await GitAllowFailureAsync(git, arguments);
        if (exitCode != 0)
            throw new InvalidOperationException("Git fixture failed: " + error);
        return output;
    }

    private static Task<string> GitInAsync(string git, string workDir, params string[] arguments)
    {
        var allArgs = new string[arguments.Length + 2];
        allArgs[0] = "-C";
        allArgs[1] = workDir;
        Array.Copy(arguments, 0, allArgs, 2, arguments.Length);
        return GitAsync(git, allArgs);
    }

    private static async Task<(int ExitCode, string StdOut, string StdErr)> GitAllowFailureAsync(string git, params string[] arguments)
    {
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo(git)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var argument in arguments)
            process.StartInfo.ArgumentList.Add(argument);
        process.Start();
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await Task.WhenAll(output, error, process.WaitForExitAsync());
        return (process.ExitCode, await output, await error);
    }

    private static Task InitBareRepoAsync(string git, string barePath, string branch = "main") =>
        GitAsync(git, "init", "--bare", "-b", branch, barePath);

    private static async Task InitRepoAsync(
        string git,
        string repoPath,
        string branch = "main",
        string userName = "Test User",
        string userEmail = "test@example.invalid")
    {
        await GitAsync(git, "init", "-b", branch, repoPath);
        await GitInAsync(git, repoPath, "config", "--local", "user.name", userName);
        await GitInAsync(git, repoPath, "config", "--local", "user.email", userEmail);
    }

    private static async Task<string> CommitFileAsync(
        string git,
        string repoPath,
        string relPath,
        string content,
        string message)
    {
        var absPath = Path.Combine(repoPath, relPath.Replace('/', Path.DirectorySeparatorChar));
        var dir = Path.GetDirectoryName(absPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(absPath, content, Utf8NoBom);
        await GitInAsync(git, repoPath, "add", "--", relPath);
        await GitInAsync(git, repoPath, "commit", "-m", message);
        return (await GitInAsync(git, repoPath, "rev-parse", "HEAD")).Trim();
    }

    private static string LocateSourceRepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "SourceGit.slnx")))
                return dir.FullName;
            dir = dir.Parent;
        }
        return null;
    }

    private static void Check(bool condition, string description)
    {
        if (!condition)
            throw new InvalidOperationException("FAIL: " + description);
        _checks++;
        Console.WriteLine("PASS: " + description);
    }
}
