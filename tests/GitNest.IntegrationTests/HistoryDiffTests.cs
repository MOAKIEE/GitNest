using System.Diagnostics;
using System.Text;

using SourceGit.Commands;
using SourceGit.Models;

internal static partial class Program
{
    private static async Task RunHistoryDiffTestsAsync(string realGit, string suiteRoot)
    {
        var largeRepo = Path.Combine(suiteRoot, "大量历史-测试[仓库]");
        await InitRepoAsync(realGit, largeRepo, "main", "Perf Tester", "perf@example.invalid");
        await PopulateHistoryWithFastImportAsync(realGit, largeRepo, 250);

        var cjkRel = "文档/中文 配置 [v1]&说明.txt";
        var binRel = "assets/图标 [测试].bin";
        var cjkAbs = Path.Combine(largeRepo, cjkRel.Replace('/', Path.DirectorySeparatorChar));
        var binAbs = Path.Combine(largeRepo, binRel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(cjkAbs)!);
        Directory.CreateDirectory(Path.GetDirectoryName(binAbs)!);

        await File.WriteAllTextAsync(cjkAbs, "第一行内容\n基准配置=1\n", Utf8NoBom);
        await File.WriteAllBytesAsync(binAbs, [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x01, 0x02]);
        await GitInAsync(realGit, largeRepo, "add", "--", cjkRel, binRel);
        await GitInAsync(realGit, largeRepo, "commit", "-m", "添加中文特殊路径与二进制基准文件");

        await File.WriteAllTextAsync(cjkAbs, "第一行内容\n基准配置=2\n新增设置=true\n", Utf8NoBom);
        await File.WriteAllBytesAsync(binAbs, [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x09, 0x08, 0x07]);
        await GitInAsync(realGit, largeRepo, "add", "--", cjkRel, binRel);
        await GitInAsync(realGit, largeRepo, "commit", "-m", "修改中文配置文件并更新二进制载荷");

        var head200 = await new QueryCommits(largeRepo, "-200").GetResultAsync();
        var all252 = await new QueryCommits(largeRepo, "-500").GetResultAsync();
        var searched180 = await new QueryCommits(largeRepo, "第180条历史记录", CommitSearchMethod.ByMessage, true).GetResultAsync();
        Check(head200.Count == 200 && all252.Count == 252 && head200[0].SHA == all252[0].SHA &&
              all252[0].Subject == "修改中文配置文件并更新二进制载荷",
              "Commit history queries bounded slices (-200) and full multi-hundred set cleanly");
        Check(searched180.Count == 1 && searched180[0].Subject.Contains("第180条历史记录", StringComparison.Ordinal),
              "Commit search locates target item inside commit history");

        var latestCommit = all252[0];
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
    }

    private static async Task PopulateHistoryWithFastImportAsync(string git, string repoPath, int count)
    {
        var sb = new StringBuilder(count * 128);
        var baseTimestamp = 1700000000L;
        for (int i = 1; i <= count; i++)
        {
            var payload = $"commit-number={i}\n";
            var payloadBytes = Encoding.UTF8.GetByteCount(payload);
            var msg = $"第{i}条历史记录：性能与列表检查";
            var msgBytes = Encoding.UTF8.GetByteCount(msg);

            sb.Append($"commit refs/heads/main\n");
            sb.Append($"mark :{i}\n");
            sb.Append($"author Perf Tester <perf@example.invalid> {baseTimestamp + i * 60} +0000\n");
            sb.Append($"committer Perf Tester <perf@example.invalid> {baseTimestamp + i * 60} +0000\n");
            sb.Append($"data {msgBytes}\n{msg}\n");
            if (i > 1)
                sb.Append($"from :{i - 1}\n");
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
            StandardInputEncoding = Utf8NoBom,
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

        await GitInAsync(git, repoPath, "reset", "--hard", "main");
    }
}
