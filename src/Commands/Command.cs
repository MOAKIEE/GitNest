using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace SourceGit.Commands
{
    public partial class Command
    {
        public class Result
        {
            public bool IsSuccess { get; set; } = false;
            public int ExitCode { get; set; } = -1;
            public string StdOut { get; set; } = string.Empty;
            public string StdErr { get; set; } = string.Empty;

            public static Result Failed(string reason) => new Result() { StdErr = reason };
        }

        public enum EditorType
        {
            None,
            CoreEditor,
            RebaseEditor,
        }

        public string Context { get; set; } = string.Empty;
        public string WorkingDirectory { get; set; } = null;
        public EditorType Editor { get; set; } = EditorType.CoreEditor;
        public string SSHKey { get; set; } = string.Empty;
        public string Args { get; set; } = string.Empty;

        // Only used in `ExecAsync` mode.
        public CancellationToken CancellationToken { get; set; } = CancellationToken.None;
        public bool RaiseError { get; set; } = true;
        public Models.ICommandLog Log { get; set; } = null;

        public async Task<bool> ExecAsync()
        {
            Log?.AppendLine($"$ git {Args}\n");

            var errs = new List<string>();

            using var proc = new Process();
            proc.StartInfo = CreateGitStartInfo(true);
            proc.OutputDataReceived += (_, e) => HandleOutput(e.Data, errs);
            proc.ErrorDataReceived += (_, e) => HandleOutput(e.Data, errs);

            var captured = new CapturedProcess() { Process = proc };
            var capturedLock = new object();
            try
            {
                proc.Start();

                // Not safe, please only use `CancellationToken` in readonly commands.
                if (CancellationToken.CanBeCanceled)
                {
                    CancellationToken.Register(() =>
                    {
                        lock (capturedLock)
                        {
                            if (captured is { Process: { HasExited: false } })
                                Native.OS.TerminateProcess(captured.Process);
                        }
                    });
                }
            }
            catch (Exception e)
            {
                if (RaiseError)
                    RaiseException(e.Message);

                Log?.AppendLine(string.Empty);
                return false;
            }

            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();

            try
            {
                await proc.WaitForExitAsync(CancellationToken).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                HandleOutput(e.Message, errs);
            }

            lock (capturedLock)
            {
                captured.Process = null;
            }

            Log?.AppendLine(string.Empty);

            if (!CancellationToken.IsCancellationRequested && proc.ExitCode != 0)
            {
                if (RaiseError)
                {
                    var errMsg = string.Join("\n", errs).Trim();
                    if (!string.IsNullOrEmpty(errMsg))
                        RaiseException(errMsg);
                }

                return false;
            }

            return true;
        }

        protected Result ReadToEnd()
        {
            using var proc = new Process();
            proc.StartInfo = CreateGitStartInfo(true);

            try
            {
                proc.Start();
            }
            catch (Exception e)
            {
                return Result.Failed(e.Message);
            }

            var rs = new Result() { IsSuccess = true };
            rs.StdOut = proc.StandardOutput.ReadToEnd();
            rs.StdErr = proc.StandardError.ReadToEnd();
            proc.WaitForExit();

            rs.IsSuccess = proc.ExitCode == 0;
            rs.ExitCode = proc.ExitCode;
            return rs;
        }

        protected async Task<Result> ReadToEndAsync()
        {
            using var proc = new Process();
            proc.StartInfo = CreateGitStartInfo(true);

            try
            {
                proc.Start();
            }
            catch (Exception e)
            {
                return Result.Failed(e.Message);
            }

            var stdOutTask = proc.StandardOutput.ReadToEndAsync(CancellationToken);
            var stdErrTask = proc.StandardError.ReadToEndAsync(CancellationToken);
            var waitForExitTask = proc.WaitForExitAsync(CancellationToken);

            await Task.WhenAll(stdOutTask, stdErrTask, waitForExitTask).ConfigureAwait(false);

            return new Result
            {
                IsSuccess = proc.ExitCode == 0,
                ExitCode = proc.ExitCode,
                StdOut = await stdOutTask.ConfigureAwait(false),
                StdErr = await stdErrTask.ConfigureAwait(false)
            };
        }

        protected ProcessStartInfo CreateGitStartInfo(bool redirect)
        {
            var useSetSid = CancellationToken.CanBeCanceled && Native.OS.SupportSetSid();
            var selfExecFile = Environment.ProcessPath;
            var builder = new StringBuilder(2048);

            if (useSetSid)
                builder.Append(Native.OS.GitExecutable.Quoted()).Append(' ');

            builder
                .Append("--no-pager -c core.quotepath=off -c credential.helper=")
                .Append(Native.OS.CredentialHelper)
                .Append(' ');

            switch (Editor)
            {
                case EditorType.CoreEditor:
                    builder.Append($"""-c core.editor="\"{selfExecFile}\" --core-editor" """);
                    break;
                case EditorType.RebaseEditor:
                    builder.Append($"""-c core.editor="\"{selfExecFile}\" --rebase-message-editor" -c sequence.editor="\"{selfExecFile}\" --rebase-todo-editor" -c rebase.abbreviateCommands=true """);
                    break;
                default:
                    builder.Append("-c core.editor=true ");
                    break;
            }

            builder.Append(Args);

            var start = new ProcessStartInfo();
            start.FileName = useSetSid ? Native.OS.GetSetSidExecutable() : Native.OS.GitExecutable;
            start.Arguments = builder.ToString();
            start.UseShellExecute = false;
            start.CreateNoWindow = true;

            if (redirect)
            {
                start.RedirectStandardOutput = true;
                start.RedirectStandardError = true;
                start.StandardOutputEncoding = Encoding.UTF8;
                start.StandardErrorEncoding = Encoding.UTF8;
            }

            // Force using this app as SSH askpass program
            start.Environment.Add("SSH_ASKPASS", selfExecFile); // Can not use parameter here, because it invoked by SSH with `exec`
            start.Environment.Add("SSH_ASKPASS_REQUIRE", "prefer");
            start.Environment.Add("GITNEST_LAUNCH_AS_ASKPASS", "TRUE");
            if (!OperatingSystem.IsLinux())
                start.Environment.Add("DISPLAY", "required");

            // If an SSH private key was provided, sets the environment.
            if (!start.Environment.ContainsKey("GIT_SSH_COMMAND") && !string.IsNullOrEmpty(SSHKey))
                start.Environment.Add("GIT_SSH_COMMAND", $"ssh -i '{SSHKey}' -o AddKeysToAgent=yes");

            // Force the C locale on Unix, so that git's output (including fatal errors) can always be parsed.
            if (!OperatingSystem.IsWindows())
            {
                start.Environment.Add("LANG", "C");
                start.Environment.Add("LC_ALL", "C");
            }

            // Working directory
            if (!string.IsNullOrEmpty(WorkingDirectory))
                start.WorkingDirectory = WorkingDirectory;

            return start;
        }

        protected void RaiseException(string error)
        {
            Models.Notification.Send(Context, error, true);
        }

        internal static Result RedactResult(Result result)
        {
            if (result == null)
                return Result.Failed("Unknown error.");

            return new Result
            {
                IsSuccess = result.IsSuccess,
                ExitCode = result.ExitCode,
                StdOut = Models.GitConfigSnapshot.RedactText(result.StdOut),
                StdErr = Models.GitConfigSnapshot.RedactText(result.StdErr),
            };
        }

        internal static async Task<Result> RunGitDirectAsync(
            string workingDirectory,
            CancellationToken cancellationToken,
            params string[] args)
        {
            var gitExecutable = Native.OS.GitExecutable;
            if (string.IsNullOrWhiteSpace(gitExecutable) || !File.Exists(gitExecutable))
                return Result.Failed("Git executable not found.");

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));

            using var proc = new Process();
            proc.StartInfo = new ProcessStartInfo(gitExecutable)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };

            if (!string.IsNullOrEmpty(workingDirectory))
                proc.StartInfo.WorkingDirectory = workingDirectory;

            if (!OperatingSystem.IsLinux())
                proc.StartInfo.Environment["DISPLAY"] = "required";
            if (!OperatingSystem.IsWindows())
            {
                proc.StartInfo.Environment["LANG"] = "C";
                proc.StartInfo.Environment["LC_ALL"] = "C";
            }

            foreach (var arg in args)
                proc.StartInfo.ArgumentList.Add(arg);

            var started = false;
            try
            {
                proc.Start();
                started = true;
                var stdOut = proc.StandardOutput.ReadToEndAsync(timeout.Token);
                var stdErr = proc.StandardError.ReadToEndAsync(timeout.Token);
                await Task.WhenAll(stdOut, stdErr, proc.WaitForExitAsync(timeout.Token)).ConfigureAwait(false);
                return new Result
                {
                    IsSuccess = proc.ExitCode == 0,
                    ExitCode = proc.ExitCode,
                    StdOut = await stdOut.ConfigureAwait(false),
                    StdErr = await stdErr.ConfigureAwait(false),
                };
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                return Result.Failed(Models.GitConfigSnapshot.RedactText(ex.Message));
            }
            finally
            {
                if (started && !proc.HasExited)
                {
                    try { proc.Kill(true); }
                    catch (InvalidOperationException) { }
                    catch (System.ComponentModel.Win32Exception) { }
                }
            }
        }

        private void HandleOutput(string line, List<string> errs)
        {
            if (line == null)
                return;

            Log?.AppendLine(line);

            // Lines to hide in error message.
            if (line.Length > 0)
            {
                if (line.StartsWith("remote: Enumerating objects:", StringComparison.Ordinal) ||
                    line.StartsWith("remote: Counting objects:", StringComparison.Ordinal) ||
                    line.StartsWith("remote: Compressing objects:", StringComparison.Ordinal) ||
                    line.StartsWith("Filtering content:", StringComparison.Ordinal) ||
                    line.StartsWith("hint:", StringComparison.Ordinal))
                    return;

                if (REG_PROGRESS().IsMatch(line))
                    return;
            }

            errs.Add(line);
        }

        private class CapturedProcess
        {
            public Process Process { get; set; } = null;
        }

        [GeneratedRegex(@"\d+%")]
        private static partial Regex REG_PROGRESS();
    }
}
