using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

using SourceGit.Models;
using NativeOS = SourceGit.Native.OS;

internal static partial class Program
{
    private const string FakeModeVariable = "GITNEST_TEST_GIT_MODE";
    private static int _checks;

    public static async Task<int> Main(string[] args)
    {
        if (args.Length > 0)
        {
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
        var root = Path.Combine(Path.GetTempPath(), "GitNest-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var self = Environment.ProcessPath!;
            Check(Path.GetFileName(self) == "GitNest.IntegrationTests.exe", "The process fixture runs through its own apphost");
            var missing = Path.Combine(root, "missing-git.exe");
            Check((await GitInstallation.InspectAsync(null)).Status == GitInstallationStatus.Missing, "No configured path is missing");
            Check((await GitInstallation.InspectAsync(missing)).Status == GitInstallationStatus.Missing, "A missing file is rejected");
            Check((await GitInstallation.InspectAsync("git.exe")).Status == GitInstallationStatus.Missing, "A relative path cannot silently use another executable");

            var textFile = Path.Combine(root, "not-a-program.txt");
            File.WriteAllText(textFile, "not an executable");
            Check((await GitInstallation.InspectAsync(textFile)).Status == GitInstallationStatus.InvalidExecutable, "An existing non-program file is rejected");

            async Task<GitInstallation> Probe(string mode, TimeSpan? timeout = null)
            {
                Environment.SetEnvironmentVariable(FakeModeVariable, mode);
                return await GitInstallation.InspectAsync(self, timeout);
            }

            var valid = await Probe("valid");
            Check(valid.IsReady && valid.Version == new System.Version(2, 45, 1) && valid.VersionText == "2.45.1.windows.1", "Git for Windows version and suffix are preserved");
            var old = await Probe("old");
            Check(old.Status == GitInstallationStatus.Unsupported && old.VersionText == "2.30.9", "An old version is displayed and blocked");
            Check((await Probe("boundary")).IsReady, "The capability requirement is inclusive");
            Check((await Probe("apple")).IsReady, "Apple Git output remains readable");
            Check((await Probe("invalid")).Status == GitInstallationStatus.InvalidVersion, "Another program's version is rejected");
            Check((await Probe("overflow")).Status == GitInstallationStatus.InvalidVersion, "Oversized numeric output is rejected without throwing");
            Check((await Probe("nonzero")).Status == GitInstallationStatus.InvalidExecutable, "A failed process cannot pass with plausible stdout");
            Check((await Probe("stderr")).IsReady, "Full stderr and stdout pipes are drained concurrently");

            var timer = Stopwatch.StartNew();
            Check((await Probe("timeout", TimeSpan.FromMilliseconds(250))).Status == GitInstallationStatus.TimedOut, "An unresponsive program times out");
            Check(timer.Elapsed < TimeSpan.FromSeconds(5), "Timeout terminates the probe promptly");

            NativeOS.UseGitInstallation(valid);
            NativeOS.GitExecutable = missing;
            Check(NativeOS.GitVersion == new System.Version(0, 0, 0) && NativeOS.GitVersionString.Length == 0, "An invalid replacement clears the previous version");
            Check(!new SourceGit.ViewModels.Preferences().IsGitConfigured(), "Repository entry requires a validated executable");

            var realGit = NativeOS.FindGitExecutable();
            var installed = await GitInstallation.InspectAsync(realGit);
            Check(installed.IsReady, "The installed Git for Windows passes the real probe");
            NativeOS.UseGitInstallation(installed);

            Environment.SetEnvironmentVariable("GIT_DIR", null);
            Environment.SetEnvironmentVariable("GIT_WORK_TREE", null);
            Environment.SetEnvironmentVariable("GIT_CONFIG_NOSYSTEM", "1");
            var global = Path.Combine(root, "global.gitconfig");
            File.WriteAllText(global, string.Empty);
            Environment.SetEnvironmentVariable("GIT_CONFIG_GLOBAL", global);

            var identity = await new SourceGit.Commands.Config(null).ReadCommitIdentityAsync();
            Check(identity.IsReadable && !identity.IsComplete, "Unset identity is readable and does not block repository opening");

            var name = "AI 姓名 = 第一行\n第二行";
            await GitAsync(realGit, "config", "--file", global, "user.name", name);
            await GitAsync(realGit, "config", "--file", global, "user.email", "global@example.invalid");
            var globalHash = Hash(global);
            identity = await new SourceGit.Commands.Config(null).ReadCommitIdentityAsync();
            Check(identity.IsComplete && identity.Name == name && identity.Email == "global@example.invalid", "Global identity preserves Unicode, equals signs and newlines");
            Check(Hash(global) == globalHash, "Reading global identity does not rewrite the configuration");

            var repo = Path.Combine(root, "中文 仓库 #1");
            await GitAsync(realGit, "init", "-b", "main", repo);
            await GitAsync(realGit, "-C", repo, "config", "user.name", "Repository identity");
            var repoConfig = Path.Combine(repo, ".git", "config");
            var repoHash = Hash(repoConfig);
            identity = await new SourceGit.Commands.Config(repo).ReadCommitIdentityAsync();
            Check(identity.Name == "Repository identity" && identity.Email == "global@example.invalid", "Repository identity inherits only the unset global fields");
            Check(Hash(repoConfig) == repoHash && Hash(global) == globalHash, "Identity reads leave both repository and global configuration unchanged");

            var included = Path.Combine(root, "included.gitconfig");
            File.WriteAllText(included, string.Empty);
            await GitAsync(realGit, "config", "--file", included, "user.email", "included@example.invalid");
            await GitAsync(realGit, "config", "--file", global, "include.path", included);
            identity = await new SourceGit.Commands.Config(null).ReadCommitIdentityAsync();
            Check(identity.Email == "included@example.invalid", "Existing global include files are honored");

            await GitAsync(realGit, "-C", repo, "config", "user.name", string.Empty);
            identity = await new SourceGit.Commands.Config(repo).ReadCommitIdentityAsync();
            Check(!identity.IsComplete && identity.Name.Length == 0, "An explicit empty repository name is not replaced by an inherited value");

            File.WriteAllText(global, "[broken configuration");
            identity = await new SourceGit.Commands.Config(null).ReadCommitIdentityAsync();
            Check(!identity.IsReadable, "Invalid Git configuration is reported as a read failure");

            File.WriteAllText(global, string.Empty);
            await RunWorktreeTestsAsync(realGit, root);
            await RunComparisonTestsAsync(realGit, root);
            await RunConfigTestsAsync(realGit, root);
            await RunCommitSyncTestsAsync(realGit, root);
        }
        finally
        {
            Environment.SetEnvironmentVariable(FakeModeVariable, originalMode);
            Environment.SetEnvironmentVariable("GIT_CONFIG_GLOBAL", originalGlobal);
            Environment.SetEnvironmentVariable("GIT_CONFIG_NOSYSTEM", originalNoSystem);
            Environment.SetEnvironmentVariable("GIT_DIR", originalGitDir);
            Environment.SetEnvironmentVariable("GIT_WORK_TREE", originalWorkTree);
            NativeOS.GitExecutable = originalGit;
            var resolvedRoot = Path.GetFullPath(root);
            var temporaryDirectory = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!resolvedRoot.StartsWith(temporaryDirectory, StringComparison.OrdinalIgnoreCase) ||
                !Path.GetFileName(resolvedRoot).StartsWith("GitNest-tests-", StringComparison.Ordinal))
                throw new InvalidOperationException("Unexpected test cleanup directory.");
            foreach (var file in Directory.EnumerateFiles(resolvedRoot, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(resolvedRoot, true);
        }
    }

    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    private static async Task<string> GitAsync(string git, params string[] arguments)
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
        if (process.ExitCode != 0)
            throw new InvalidOperationException("Git fixture failed: " + await error);
        return await output;
    }

    private static void Check(bool condition, string description)
    {
        if (!condition)
            throw new InvalidOperationException("FAIL: " + description);
        _checks++;
        Console.WriteLine("PASS: " + description);
    }
}
