using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace SourceGit.Models
{
    public enum GitInstallationStatus
    {
        Missing,
        InvalidExecutable,
        InvalidVersion,
        Unsupported,
        TimedOut,
        Ready,
    }

    public sealed partial class GitInstallation
    {
        public string Executable { get; init; } = string.Empty;
        public System.Version Version { get; init; } = new(0, 0, 0);
        public string VersionText { get; init; } = string.Empty;
        public GitInstallationStatus Status { get; init; } = GitInstallationStatus.Missing;
        public bool IsReady => Status == GitInstallationStatus.Ready;

        public static async Task<GitInstallation> InspectAsync(string executable, TimeSpan? timeout = null)
        {
            executable ??= string.Empty;
            if (string.IsNullOrWhiteSpace(executable) || !Path.IsPathFullyQualified(executable) || !File.Exists(executable))
                return new GitInstallation { Executable = executable };

            using var process = new Process();
            process.StartInfo = new ProcessStartInfo(executable)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            process.StartInfo.ArgumentList.Add("--version");

            using var cancellation = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(3));
            try
            {
                process.Start();
                var output = process.StandardOutput.ReadToEndAsync(cancellation.Token);
                var error = process.StandardError.ReadToEndAsync(cancellation.Token);
                await Task.WhenAll(output, error, process.WaitForExitAsync(cancellation.Token)).ConfigureAwait(false);
                if (process.ExitCode != 0)
                    return new GitInstallation { Executable = executable, Status = GitInstallationStatus.InvalidExecutable };

                var text = (await output.ConfigureAwait(false)).Trim();
                var match = REG_VERSION().Match(text);
                if (!match.Success ||
                    !int.TryParse(match.Groups[1].Value, out var major) ||
                    !int.TryParse(match.Groups[2].Value, out var minor) ||
                    !int.TryParse(match.Groups[3].Value, out var patch))
                    return new GitInstallation { Executable = executable, Status = GitInstallationStatus.InvalidVersion };

                var version = new System.Version(major, minor, patch);
                return new GitInstallation
                {
                    Executable = executable,
                    Version = version,
                    VersionText = text.Substring("git version ".Length),
                    Status = version >= GitVersions.MINIMAL ? GitInstallationStatus.Ready : GitInstallationStatus.Unsupported,
                };
            }
            catch (OperationCanceledException)
            {
                try
                {
                    if (!process.HasExited)
                        process.Kill(true);
                }
                catch (InvalidOperationException) { }
                catch (System.ComponentModel.Win32Exception) { }
                return new GitInstallation { Executable = executable, Status = GitInstallationStatus.TimedOut };
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException or UnauthorizedAccessException)
            {
                return new GitInstallation { Executable = executable, Status = GitInstallationStatus.InvalidExecutable };
            }
        }

        [GeneratedRegex(@"\Agit version (\d+)\.(\d+)\.(\d+)(?:[.\-][\w.\-]+)?(?: \(Apple Git-[\w.\-]+\))?\z")]
        private static partial Regex REG_VERSION();
    }
}
