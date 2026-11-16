using System.Diagnostics;
using System.IO.Compression;
using System.Xml.Linq;

using SourceGit;
using SourceGit.Commands;
using SourceGit.Models;
using NativeOS = SourceGit.Native.OS;
using VM = SourceGit.ViewModels;

internal static partial class Program
{
    private static async Task RunCoreGitProbeTestsAsync(string root)
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
        Check(!new VM.Preferences().IsGitConfigured(), "Repository entry requires a validated executable");
    }

    private static async Task RunCoreSuiteAsync(string realGit, string root, string global)
    {
        var coreRoot = Path.Combine(root, "core-suite");
        Directory.CreateDirectory(coreRoot);

        // 1. Commit identity resolution across global, local, includes, and invalid configs
        var identity = await new Config(null).ReadCommitIdentityAsync();
        Check(identity.IsReadable && !identity.IsComplete, "Unset identity is readable and does not block repository opening");

        var name = "AI 姓名 = 第一行\n第二行";
        await GitAsync(realGit, "config", "--file", global, "user.name", name);
        await GitAsync(realGit, "config", "--file", global, "user.email", "global@example.invalid");
        var globalHash = Hash(global);
        identity = await new Config(null).ReadCommitIdentityAsync();
        Check(identity.IsComplete && identity.Name == name && identity.Email == "global@example.invalid", "Global identity preserves Unicode, equals signs and newlines");
        Check(Hash(global) == globalHash, "Reading global identity does not rewrite the configuration");

        var repo = Path.Combine(coreRoot, "中文 仓库 #1");
        await GitAsync(realGit, "init", "-b", "main", repo);
        await GitInAsync(realGit, repo, "config", "user.name", "Repository identity");
        var repoConfig = Path.Combine(repo, ".git", "config");
        var repoHash = Hash(repoConfig);
        identity = await new Config(repo).ReadCommitIdentityAsync();
        Check(identity.Name == "Repository identity" && identity.Email == "global@example.invalid", "Repository identity inherits only the unset global fields");
        Check(Hash(repoConfig) == repoHash && Hash(global) == globalHash, "Identity reads leave both repository and global configuration unchanged");

        var included = Path.Combine(coreRoot, "included.gitconfig");
        File.WriteAllText(included, string.Empty);
        await GitAsync(realGit, "config", "--file", included, "user.email", "included@example.invalid");
        await GitAsync(realGit, "config", "--file", global, "include.path", included);
        identity = await new Config(null).ReadCommitIdentityAsync();
        Check(identity.Email == "included@example.invalid", "Existing global include files are honored");

        await GitInAsync(realGit, repo, "config", "user.name", string.Empty);
        identity = await new Config(repo).ReadCommitIdentityAsync();
        Check(!identity.IsComplete && identity.Name.Length == 0, "An explicit empty repository name is not replaced by an inherited value");

        File.WriteAllText(global, "[broken configuration");
        identity = await new Config(null).ReadCommitIdentityAsync();
        Check(!identity.IsReadable, "Invalid Git configuration is reported as a read failure");
        File.WriteAllText(global, string.Empty);

        // 2. Empty unborn repository safe queries
        var emptyCommits = await new QueryCommits(repo, "-100").GetResultAsync();
        var emptyBranches = await new QueryBranches(repo).GetResultAsync();
        var emptyTags = await new QueryTags(repo).GetResultAsync();
        var emptyStashes = await new QueryStashes(repo).GetResultAsync();
        Check(emptyCommits.Count == 0 && emptyBranches.Count == 0 && emptyTags.Count == 0 && emptyStashes.Count == 0,
              "Unborn empty repository returns zero commits/branches/tags/stashes without throwing");

        // 3. Assembly surface, localization key parity, and Preferences serialization
        var referencedAssemblies = typeof(App).Assembly.GetReferencedAssemblies().Select(a => a.Name ?? string.Empty).ToList();
        Check(!referencedAssemblies.Any(n => n.Contains("OpenAI", StringComparison.OrdinalIgnoreCase) ||
                                             n.StartsWith("Azure.", StringComparison.OrdinalIgnoreCase)),
              "Compiled GitNest assembly has zero OpenAI or Azure SDK assembly references");

        var sourceRoot = LocateSourceRepositoryRoot();
        Check(sourceRoot != null, "Located source repository root for locale parity checks");
        XNamespace xNs = "http://schemas.microsoft.com/winfx/2006/xaml";
        static HashSet<string> ReadLocaleKeys(string path, XNamespace x) =>
            XDocument.Load(path).Descendants()
                .Select(e => e.Attribute(x + "Key")?.Value)
                .Where(k => !string.IsNullOrEmpty(k))
                .Select(k => k!)
                .ToHashSet(StringComparer.Ordinal);

        var enKeys = ReadLocaleKeys(Path.Combine(sourceRoot!, "src", "Resources", "Locales", "en_US.axaml"), xNs);
        var zhKeys = ReadLocaleKeys(Path.Combine(sourceRoot!, "src", "Resources", "Locales", "zh_CN.axaml"), xNs);
        Check(enKeys.SetEquals(zhKeys), $"en_US ({enKeys.Count}) and zh_CN ({zhKeys.Count}) locale key sets have zero parity diff");

        var fallbackPrefs = System.Text.Json.JsonSerializer.Deserialize<VM.Preferences>("{\"Locale\":\"zh_TW\"}");
        Check(fallbackPrefs?.Locale == "zh_CN", "Saved settings in an unsupported locale migrate to Simplified Chinese");
        var enPrefs = System.Text.Json.JsonSerializer.Deserialize<VM.Preferences>("{\"Locale\":\"EN_us\"}");
        Check(enPrefs?.Locale == "en_US", "Saved English settings retain their canonical locale key");

        var prefs = new VM.Preferences();
        var serializedPrefs = System.Text.Json.JsonSerializer.Serialize(prefs, JsonCodeGen.Default.Preferences);
        var roundTrippedPrefs = System.Text.Json.JsonSerializer.Deserialize(serializedPrefs, JsonCodeGen.Default.Preferences);
        Check(prefs.UseGitHubStyleAvatar && roundTrippedPrefs is { UseGitHubStyleAvatar: true },
              "Preferences.UseGitHubStyleAvatar defaults enabled and round-trips through JsonCodeGen");
        Check(typeof(AvatarManager).GetMethod("Request") != null &&
              typeof(AvatarManager).GetMethod("SetFromLocal") != null &&
              typeof(AvatarManager).GetMethod("Subscribe") != null &&
              typeof(AvatarManager).GetMethod("Unsubscribe") != null,
              "Retained AvatarManager preserves cache and subscription API");

        // 4. Retained Statistics, Archive export, and special image codecs (Pfim, LibTiff, StbImageSharp)
        await GitInAsync(realGit, repo, "config", "user.name", "Stats Author");
        await GitInAsync(realGit, repo, "config", "user.email", "stats@example.invalid");
        await CommitFileAsync(realGit, repo, "hello.txt", "one\n", "first stats commit");
        await CommitFileAsync(realGit, repo, "hello.txt", "two\n", "second stats commit");

        var statistics = await new SourceGit.Commands.Statistics(repo, 100, null).ReadAsync();
        Check(statistics.All.Total == 2 &&
              statistics.All.Authors.Count == 1 &&
              statistics.All.Authors[0].User.Name == "Stats Author" &&
              statistics.All.Authors[0].Count == 2,
              "Commands.Statistics computes author and commit totals");

        var archiveZip = Path.Combine(coreRoot, "export-head.zip");
        var archiveOk = await new SourceGit.Commands.Archive(repo, "HEAD", archiveZip).ExecAsync();
        using (var zip = ZipFile.OpenRead(archiveZip))
        {
            Check(archiveOk && zip.Entries.Any(e => e.FullName == "hello.txt"),
                  "Commands.Archive exports HEAD zip archive containing repository files");
        }

        Check(VM.ImageSource.GetDecoder("sprite.tga") == ImageDecoder.Pfim &&
              VM.ImageSource.GetDecoder("texture.dds") == ImageDecoder.Pfim &&
              VM.ImageSource.GetDecoder("scan.tiff") == ImageDecoder.Tiff &&
              VM.ImageSource.GetDecoder("layer.psd") == ImageDecoder.StbImage &&
              VM.ImageSource.GetDecoder("shot.png") == ImageDecoder.Builtin,
              "ImageSource.GetDecoder preserves Pfim, Tiff, StbImage, and Builtin routing");

        using (var tgaStream = new MemoryStream(CreateMinimal2x2Tga()))
        using (var pfimImage = Pfim.Pfimage.FromStream(tgaStream))
        {
            Check(pfimImage.Width == 2 && pfimImage.Height == 2, "Pfim codec decodes 2x2 TGA image stream");
        }

        var tiffPath = Path.Combine(coreRoot, "sample.tif");
        using (var writer = BitMiracle.LibTiff.Classic.Tiff.Open(tiffPath, "w"))
        {
            writer.SetField(BitMiracle.LibTiff.Classic.TiffTag.IMAGEWIDTH, 2);
            writer.SetField(BitMiracle.LibTiff.Classic.TiffTag.IMAGELENGTH, 2);
            writer.SetField(BitMiracle.LibTiff.Classic.TiffTag.SAMPLESPERPIXEL, 4);
            writer.SetField(BitMiracle.LibTiff.Classic.TiffTag.BITSPERSAMPLE, 8);
            writer.SetField(BitMiracle.LibTiff.Classic.TiffTag.PHOTOMETRIC, BitMiracle.LibTiff.Classic.Photometric.RGB);
            writer.SetField(BitMiracle.LibTiff.Classic.TiffTag.PLANARCONFIG, BitMiracle.LibTiff.Classic.PlanarConfig.CONTIG);
            var row = new byte[] { 255, 0, 0, 255, 0, 255, 0, 255 };
            writer.WriteScanline(row, 0);
            writer.WriteScanline(row, 1);
            writer.FlushData();
        }
        using (var tiffReadStream = File.OpenRead(tiffPath))
        using (var reader = BitMiracle.LibTiff.Classic.Tiff.ClientOpen("mem-read.tif", "r", tiffReadStream, new BitMiracle.LibTiff.Classic.TiffStream()))
        {
            Check(reader != null &&
                  reader.GetField(BitMiracle.LibTiff.Classic.TiffTag.IMAGEWIDTH)[0].ToInt() == 2 &&
                  reader.GetField(BitMiracle.LibTiff.Classic.TiffTag.IMAGELENGTH)[0].ToInt() == 2,
                  "BitMiracle.LibTiff.NET codec reads written 2x2 TIFF stream");
        }

        using (var psdStream = new MemoryStream(CreateMinimal2x2Psd()))
        {
            var psdResult = StbImageSharp.ImageResult.FromStream(psdStream, StbImageSharp.ColorComponents.RedGreenBlueAlpha);
            Check(psdResult.Width == 2 && psdResult.Height == 2 && psdResult.Data.Length == 16,
                  "StbImageSharp codec decodes 2x2 uncompressed PSD stream");
        }
    }

    private static byte[] CreateMinimal2x2Tga()
    {
        var bytes = new byte[18 + 2 * 2 * 3];
        bytes[2] = 2;
        bytes[12] = 2;
        bytes[14] = 2;
        bytes[16] = 24;
        for (int i = 18; i < bytes.Length; i += 3)
        {
            bytes[i] = 0x20;
            bytes[i + 1] = 0x80;
            bytes[i + 2] = 0xE0;
        }
        return bytes;
    }

    private static byte[] CreateMinimal2x2Psd()
    {
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms, System.Text.Encoding.ASCII, leaveOpen: true);
        bw.Write(System.Text.Encoding.ASCII.GetBytes("8BPS"));
        bw.Write((byte)0); bw.Write((byte)1);
        bw.Write(new byte[6]);
        bw.Write((byte)0); bw.Write((byte)4);
        bw.Write((byte)0); bw.Write((byte)0); bw.Write((byte)0); bw.Write((byte)2);
        bw.Write((byte)0); bw.Write((byte)0); bw.Write((byte)0); bw.Write((byte)2);
        bw.Write((byte)0); bw.Write((byte)8);
        bw.Write((byte)0); bw.Write((byte)3);
        bw.Write(0);
        bw.Write(0);
        bw.Write(0);
        bw.Write((byte)0); bw.Write((byte)0);
        bw.Write(new byte[4 * 4]);
        bw.Flush();
        return ms.ToArray();
    }
}
