using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using SourceGit.Commands;
using SourceGit.Models;
using NativeOS = SourceGit.Native.OS;

internal static partial class Program
{
    private static async Task RunPhase43AcceptanceTestsAsync(string realGit, string suiteRoot)
    {
        var sectionRoot = Path.Combine(suiteRoot, "phase-4.3-acceptance");
        Directory.CreateDirectory(sectionRoot);

        // 1. Missing / invalid Git setup inspection -> valid recovery & immediate repo query usability
        var originalInstall = NativeOS.GitInstallation;
        try
        {
            var missingInstall = await GitInstallation.InspectAsync(Path.Combine(sectionRoot, "nonexistent", "git.exe"));
            NativeOS.UseGitInstallation(missingInstall);
            Check(missingInstall.Status == GitInstallationStatus.Missing && !NativeOS.GitInstallation.IsReady,
                "Missing Git executable path reports Missing status and unready state");

            var fakeExe = Path.Combine(sectionRoot, "fake-git.exe");
            await File.WriteAllBytesAsync(fakeExe, [0x4D, 0x5A, 0x00, 0x00]); // Minimal invalid PE stub -> InvalidExecutable
            var badInstall = await GitInstallation.InspectAsync(fakeExe);
            NativeOS.UseGitInstallation(badInstall);
            Check(!badInstall.IsReady && badInstall.Status is GitInstallationStatus.InvalidExecutable or GitInstallationStatus.InvalidVersion,
                "Corrupt/incompatible Git binary is rejected safely without marking installation ready");

            var nonGitExe = Path.Combine(Environment.SystemDirectory, "where.exe");
            if (File.Exists(nonGitExe))
            {
                var wrongBinary = await GitInstallation.InspectAsync(nonGitExe);
                Check(!wrongBinary.IsReady && wrongBinary.Status != GitInstallationStatus.Ready,
                    "Non-Git system binary fails version inspection cleanly");
            }

            var recoveredInstall = await GitInstallation.InspectAsync(realGit);
            NativeOS.UseGitInstallation(recoveredInstall);
            Check(recoveredInstall.IsReady && recoveredInstall.Status == GitInstallationStatus.Ready &&
                  NativeOS.GitInstallation.IsReady && NativeOS.GitVersion >= GitVersions.MINIMAL,
                "Valid Git executable inspection recovers Ready state cleanly");
        }
        finally
        {
            NativeOS.UseGitInstallation(originalInstall);
        }

        // 2. Empty (unborn) repository safe queries
        var emptyRepo = Path.Combine(sectionRoot, "empty-unborn-仓");
        await GitAsync(realGit, "init", "-b", "main", emptyRepo);
        var emptyCommits = await new QueryCommits(emptyRepo, "-100").GetResultAsync();
        var emptyBranches = await new QueryBranches(emptyRepo).GetResultAsync();
        var emptyTags = await new QueryTags(emptyRepo).GetResultAsync();
        var emptyStashes = await new QueryStashes(emptyRepo).GetResultAsync();
        Check(emptyCommits.Count == 0 && emptyBranches.Count == 0 && emptyTags.Count == 0 && emptyStashes.Count == 0,
            "Unborn empty repository returns zero commits/branches/tags/stashes without throwing");

        // 3. Large commit history (650 commits) + CJK/special filenames + binary diff via real git fast-import & commits
        var largeRepo = Path.Combine(sectionRoot, "大量历史-测试[仓库]");
        await GitAsync(realGit, "init", "-b", "main", largeRepo);
        await GitAsync(realGit, "-C", largeRepo, "config", "user.name", "Perf Tester");
        await GitAsync(realGit, "-C", largeRepo, "config", "user.email", "perf@example.invalid");
        await PopulateLargeHistoryWithFastImportAsync(realGit, largeRepo, 648);

        var cjkRel = "文档/中文 配置 [v1]&说明.txt";
        var binRel = "assets/图标 [测试].bin";
        var cjkAbs = Path.Combine(largeRepo, cjkRel.Replace('/', Path.DirectorySeparatorChar));
        var binAbs = Path.Combine(largeRepo, binRel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(cjkAbs)!);
        Directory.CreateDirectory(Path.GetDirectoryName(binAbs)!);

        await File.WriteAllTextAsync(cjkAbs, "第一行内容\n基准配置=1\n", new UTF8Encoding(false));
        await File.WriteAllBytesAsync(binAbs, [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x01, 0x02]);
        await GitAsync(realGit, "-C", largeRepo, "add", "--", cjkRel, binRel);
        await GitAsync(realGit, "-C", largeRepo, "commit", "-m", "添加中文特殊路径与二进制基准文件");

        await File.WriteAllTextAsync(cjkAbs, "第一行内容\n基准配置=2\n新增设置=true\n", new UTF8Encoding(false));
        await File.WriteAllBytesAsync(binAbs, [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x09, 0x08, 0x07]);
        await GitAsync(realGit, "-C", largeRepo, "add", "--", cjkRel, binRel);
        await GitAsync(realGit, "-C", largeRepo, "commit", "-m", "修改中文配置文件并更新二进制载荷");

        var head200 = await new QueryCommits(largeRepo, "-200").GetResultAsync();
        var all650 = await new QueryCommits(largeRepo, "-1000").GetResultAsync();
        var searched420 = await new QueryCommits(largeRepo, "第420条历史记录", CommitSearchMethod.ByMessage, true).GetResultAsync();
        Check(head200.Count == 200 && all650.Count == 650 && head200[0].SHA == all650[0].SHA &&
              all650[0].Subject == "修改中文配置文件并更新二进制载荷",
            "Large commit history (650 commits) queries bounded slices (-200) and full set cleanly");
        Check(searched420.Count == 1 && searched420[0].Subject.Contains("第420条历史记录", StringComparison.Ordinal),
            "Commit search locates target item inside 650-commit history");

        var latestCommit = all650[0];
        var commitChanges = await new CompareRevisions(largeRepo, latestCommit.FirstParentToCompare, latestCommit.SHA).ReadAsync();
        var cjkChange = commitChanges.FirstOrDefault(c => c.Path == cjkRel);
        var binChange = commitChanges.FirstOrDefault(c => c.Path == binRel);
        Check(cjkChange != null && binChange != null,
            "CompareRevisions preserves unescaped CJK, space, bracket, ampersand, and binary filenames");

        var textDiff = await new Diff(largeRepo, new DiffOption(latestCommit, cjkChange!), 3, false, false).ReadAsync();
        var binDiff = await new Diff(largeRepo, new DiffOption(latestCommit, binChange!), 3, false, false).ReadAsync();
        var isTextBinary = await new IsBinary(largeRepo, latestCommit.SHA, cjkRel).GetResultAsync();
        var isBinBinary = await new IsBinary(largeRepo, latestCommit.SHA, binRel).GetResultAsync();

        Check(!textDiff.IsBinary && !isTextBinary &&
              textDiff.TextDiff.Lines.Any(l => l.Content.Contains("新增设置=true", StringComparison.Ordinal)),
            "CJK text file diff renders added UTF-8 lines without binary misclassification");
        Check(binDiff.IsBinary && isBinBinary && binDiff.TextDiff == null,
            "Binary file change with NUL bytes is accurately detected as binary diff");

        // 4. AuthCanceled, AuthFailed, preserved local commit state & secret redaction in diagnostics/journal
        var journalFile = Path.Combine(sectionRoot, "journal.json");
        var journalStore = new OperationJournalStore(journalFile);
        var authRepo = Path.Combine(sectionRoot, "auth-sync-repo");
        await GitAsync(realGit, "init", "-b", "main", authRepo);
        await GitAsync(realGit, "-C", authRepo, "config", "user.name", "Normal Author");
        await GitAsync(realGit, "-C", authRepo, "config", "user.email", "author@example.invalid");

        var secretToken = "ghp_SuperSecretToken987654321";
        var secretHeader = "Bearer SecretBearerXYZ999";
        await GitAsync(realGit, "-C", authRepo, "config", "--local", "http.extraheader", $"AUTHORIZATION: {secretHeader}");
        await GitAsync(realGit, "-C", authRepo, "remote", "add", "origin", $"https://oauth2:{secretToken}@127.0.0.1:1/org/private.git");

        await File.WriteAllTextAsync(Path.Combine(authRepo, "work.txt"), "local commit preserved\n");
        var stageOk = await GitSyncService.StagePathsAsync(authRepo, ["work.txt"]);
        Check(stageOk.Succeeded, "Stage local file before auth push test");
        var snapBeforeCommit = await QueryStagedSnapshot.CaptureAsync(authRepo);
        var commitRes = await GitSyncService.CommitAsync(
            authRepo,
            new CommitExecutionOptions
            {
                Message = "本地提交不与远端认证混淆",
                ExpectedSnapshot = snapBeforeCommit,
            });
        Check(commitRes.Succeeded && commitRes.Identity?.Name == "Normal Author",
            "Local commit uses repository commit identity independent of remote credential URLs");

        var headBeforePush = (await GitAsync(realGit, "-C", authRepo, "rev-parse", "HEAD")).Trim();
        var remoteList = await new QueryRemotes(authRepo).GetResultAsync();
        var originRemote = remoteList.First(r => r.Name == "origin");

        // Configure a real per-repo SSH remote & sh wrapper simulating ASKPASS_CANCELLED
        var cancelScript = Path.Combine(sectionRoot, "ssh-cancel.sh");
        await File.WriteAllTextAsync(cancelScript, "#!/bin/sh\necho ASKPASS_CANCELLED >&2\nexit 128\n", new UTF8Encoding(false));
        await GitAsync(realGit, "-C", authRepo, "remote", "add", "sshremote", "ssh://git@example.invalid/org/repo.git");
        await GitAsync(realGit, "-C", authRepo, "config", "--local", "core.sshCommand", $"sh \"{cancelScript.Replace('\\', '/')}\"");
        var sshRemote = (await new QueryRemotes(authRepo).GetResultAsync()).First(r => r.Name == "sshremote");

        var cancelFetchOrigin = await GitSyncService.FetchAsync(authRepo, [originRemote], journalStore: journalStore);
        Check(!cancelFetchOrigin.Succeeded &&
              !cancelFetchOrigin.DiagnosticDetail.Contains(secretToken, StringComparison.Ordinal) &&
              !cancelFetchOrigin.DiagnosticDetail.Contains(secretHeader, StringComparison.Ordinal),
            "Fetch failure against credentialed HTTPS remote redacts secrets cleanly");

        var cancelPush = await GitSyncService.PushSingleBranchAsync(
            authRepo,
            new PushExecutionOptions
            {
                LocalBranch = "main",
                Remote = sshRemote,
                RemoteBranch = "main",
            },
            journalStore: journalStore);

        Check(!cancelPush.Succeeded && cancelPush.Outcome == PushOutcomeKind.AuthCanceled &&
              cancelPush.Summary.Contains("取消", StringComparison.Ordinal),
            "Authentication cancellation returns AuthCanceled outcome and explicit feedback");

        // Real local TCP listener returning HTTP 401 Unauthorized to trigger authentic Git HTTP AuthFailed without external network
        using var httpListener = new TcpListener(IPAddress.Loopback, 0);
        httpListener.Start();
        var port = ((IPEndPoint)httpListener.LocalEndpoint).Port;
        var ctsListener = new CancellationTokenSource();
        var listenerTask = Task.Run(async () =>
        {
            while (!ctsListener.IsCancellationRequested)
            {
                try
                {
                    using var client = await httpListener.AcceptTcpClientAsync(ctsListener.Token);
                    using var stream = client.GetStream();
                    var buf = new byte[4096];
                    _ = await stream.ReadAsync(buf, ctsListener.Token);
                    var resp = Encoding.ASCII.GetBytes(
                        "HTTP/1.1 401 Unauthorized\r\n" +
                        "WWW-Authenticate: Basic realm=\"GitNestTest\"\r\n" +
                        "Content-Length: 0\r\n" +
                        "Connection: close\r\n\r\n");
                    await stream.WriteAsync(resp, ctsListener.Token);
                }
                catch
                {
                    break;
                }
            }
        });

        try
        {
            await GitAsync(realGit, "-C", authRepo, "config", "--local", "credential.helper", "");
            await GitAsync(realGit, "-C", authRepo, "remote", "add", "localhttp", $"http://user:{secretToken}@127.0.0.1:{port}/org/repo.git");
            var httpRemote = (await new QueryRemotes(authRepo).GetResultAsync()).First(r => r.Name == "localhttp");

            var prevAskPass = Environment.GetEnvironmentVariable("GIT_TERMINAL_PROMPT");
            Environment.SetEnvironmentVariable("GIT_TERMINAL_PROMPT", "0");
            try
            {
                var httpAuthFail = await GitSyncService.PushSingleBranchAsync(
                    authRepo,
                    new PushExecutionOptions
                    {
                        LocalBranch = "main",
                        Remote = httpRemote,
                        RemoteBranch = "main",
                    },
                    journalStore: journalStore);

                var headAfterPush = (await GitAsync(realGit, "-C", authRepo, "rev-parse", "HEAD")).Trim();
                var workStatusAfterPush = (await GitAsync(realGit, "-C", authRepo, "status", "--short")).Trim();
                Check(!httpAuthFail.Succeeded &&
                      httpAuthFail.Outcome is PushOutcomeKind.AuthFailed or PushOutcomeKind.AuthCanceled,
                    "Local HTTP 401 authentication failure returns explicit AuthFailed/AuthCanceled outcome");
                Check(headAfterPush == headBeforePush && string.IsNullOrEmpty(workStatusAfterPush),
                    "Failed authenticated push leaves local commit HEAD and clean working tree untouched");

                Check(File.Exists(journalFile), "Operation journal persisted diagnostic records to JSON file");
                var allJournalText = File.ReadAllText(journalFile);
                Check(allJournalText.Length > 0 &&
                      !allJournalText.Contains(secretToken, StringComparison.Ordinal) &&
                      !allJournalText.Contains(secretHeader, StringComparison.Ordinal) &&
                      !httpAuthFail.DiagnosticDetail.Contains(secretToken, StringComparison.Ordinal) &&
                      !httpAuthFail.Summary.Contains(secretToken, StringComparison.Ordinal),
                    "Sensitive credentials and extraheader tokens never appear in diagnostics or persisted operation logs");
            }
            finally
            {
                Environment.SetEnvironmentVariable("GIT_TERMINAL_PROMPT", prevAskPass);
            }
        }
        finally
        {
            ctsListener.Cancel();
            httpListener.Stop();
            await listenerTask;
        }
    }

    private static async Task PopulateLargeHistoryWithFastImportAsync(string git, string repoPath, int count)
    {
        var sb = new StringBuilder(count * 128);
        var baseTimestamp = 1700000000L;
        for (int i = 1; i <= count; i++)
        {
            var mark = i;
            var ts = baseTimestamp + i * 60;
            var payload = $"commit-number={i}\n";
            var payloadBytes = Encoding.UTF8.GetByteCount(payload);
            var msg = $"第{i}条历史记录：性能与列表检查";
            var msgBytes = Encoding.UTF8.GetByteCount(msg);

            sb.Append($"commit refs/heads/main\n");
            sb.Append($"mark :{mark}\n");
            sb.Append($"author Perf Tester <perf@example.invalid> {ts} +0000\n");
            sb.Append($"committer Perf Tester <perf@example.invalid> {ts} +0000\n");
            sb.Append($"data {msgBytes}\n{msg}\n");
            if (i > 1)
                sb.Append($"from :{mark - 1}\n");
            sb.Append($"M 100644 inline counter.txt\ndata {payloadBytes}\n{payload}\n");
        }

        using var proc = new Process();
        proc.StartInfo = new ProcessStartInfo(git)
        {
            WorkingDirectory = repoPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        proc.StartInfo.ArgumentList.Add("fast-import");
        proc.StartInfo.ArgumentList.Add("--quiet");
        proc.Start();
        await proc.StandardInput.WriteAsync(sb.ToString());
        proc.StandardInput.Close();
        var stdErr = proc.StandardError.ReadToEndAsync();
        await Task.WhenAll(stdErr, proc.WaitForExitAsync());
        if (proc.ExitCode != 0)
            throw new InvalidOperationException("fast-import failed: " + await stdErr);

        await GitAsync(git, "-C", repoPath, "reset", "--hard", "main");
    }
}
