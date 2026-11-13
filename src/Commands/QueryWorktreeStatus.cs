using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace SourceGit.Commands
{
    // Read each worktree in its own directory so its index, HEAD and config are honored.
    public class QueryWorktreeStatus : Command
    {
        public QueryWorktreeStatus(string path)
        {
            WorkingDirectory = path;
            Context = path;
            RaiseError = false;
        }

        public async Task<Models.WorktreeStatus> GetResultAsync(string expectedCommonDirectory)
        {
            var status = new Models.WorktreeStatus();
            if (!Directory.Exists(WorkingDirectory))
            {
                status.Error = "Worktree.Status.Missing";
                return status;
            }

            Args = "rev-parse --path-format=absolute --show-toplevel --git-common-dir --git-path FETCH_HEAD";
            var identity = await ReadWithTimeoutAsync().ConfigureAwait(false);
            if (!identity.IsSuccess)
            {
                status.Error = "Worktree.Status.ReadFailed";
                return status;
            }
            var paths = identity.StdOut.TrimEnd('\r', '\n').Split('\n');
            if (paths.Length != 3 ||
                !Models.WorktreeTaskStore.PathsEqual(paths[0].TrimEnd('\r'), WorkingDirectory) ||
                !Models.WorktreeTaskStore.PathsEqual(paths[1].TrimEnd('\r'), expectedCommonDirectory))
            {
                status.Error = "Worktree.Status.IdentityChanged";
                return status;
            }

            var fetchFile = paths[2].TrimEnd('\r');
            DateTime? fetchTime = File.Exists(fetchFile) ? File.GetLastWriteTime(fetchFile) : null;
            Args = "--no-optional-locks status --porcelain=v2 --branch --ahead-behind -z --untracked-files=all --ignore-submodules=none";
            var result = await ReadWithTimeoutAsync().ConfigureAwait(false);
            if (!result.IsSuccess)
            {
                status.Error = "Worktree.Status.ReadFailed";
                return status;
            }

            return Parse(result.StdOut, fetchTime);
        }

        private async Task<Result> ReadWithTimeoutAsync()
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(CancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            using var process = new Process() { StartInfo = CreateGitStartInfo(true) };
            var started = false;
            try
            {
                process.Start();
                started = true;
                var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
                var error = process.StandardError.ReadToEndAsync(timeout.Token);
                await Task.WhenAll(output, error, process.WaitForExitAsync(timeout.Token)).ConfigureAwait(false);
                return new Result() { IsSuccess = process.ExitCode == 0, ExitCode = process.ExitCode, StdOut = await output, StdErr = await error };
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                return Result.Failed(ex.Message);
            }
            finally
            {
                if (started && !process.HasExited)
                {
                    try { process.Kill(true); }
                    catch (InvalidOperationException) { }
                    catch (System.ComponentModel.Win32Exception) { }
                }
            }
        }

        private static Models.WorktreeStatus Parse(string output, DateTime? fetchTime)
        {
            var status = new Models.WorktreeStatus() { IsReadable = true, LastFetchAt = fetchTime };
            var records = output.Split('\0');
            int? ahead = null, behind = null;
            for (var i = 0; i < records.Length; i++)
            {
                var record = records[i];
                if (record.StartsWith("# branch.oid ", StringComparison.Ordinal))
                    status.Head = record.Substring(13);
                else if (record.StartsWith("# branch.head ", StringComparison.Ordinal))
                    status.Branch = record.Substring(14);
                else if (record.StartsWith("# branch.upstream ", StringComparison.Ordinal))
                    status.Upstream = record.Substring(18);
                else if (record.StartsWith("# branch.ab ", StringComparison.Ordinal))
                {
                    var counts = record.Substring(12).Split(' ');
                    if (counts.Length == 2 && int.TryParse(counts[0], out var a) && int.TryParse(counts[1], out var b))
                    {
                        ahead = a;
                        behind = -b;
                    }
                }
                else if (record.StartsWith("? ", StringComparison.Ordinal))
                {
                    status.Untracked++;
                    status.ChangedFiles++;
                }
                else if (record.Length > 4 && (record[0] == '1' || record[0] == '2' || record[0] == 'u'))
                {
                    status.ChangedFiles++;
                    if (record[0] == 'u')
                        status.Conflicted++;
                    else
                    {
                        if (record[2] != '.')
                            status.Staged++;
                        if (record[3] != '.')
                            status.Unstaged++;
                    }

                    // A rename/copy has a second NUL-delimited field containing the old path.
                    if (record[0] == '2')
                        i++;
                }
            }

            status.SyncState = status.Head == "(initial)" ? Models.WorktreeSyncState.Unborn :
                status.Branch == "(detached)" ? Models.WorktreeSyncState.Detached :
                status.Upstream.Length == 0 ? Models.WorktreeSyncState.NoUpstream :
                ahead == null ? Models.WorktreeSyncState.UpstreamGone :
                fetchTime == null ? Models.WorktreeSyncState.NotFetched : Models.WorktreeSyncState.Cached;
            if (status.SyncState == Models.WorktreeSyncState.Cached)
            {
                status.Ahead = ahead;
                status.Behind = behind;
            }
            return status;
        }
    }
}
