using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SourceGit.Commands
{
    public class QueryTaskComparison
    {
        public QueryTaskComparison(string path, string commonDirectory)
        {
            _path = path;
            _commonDirectory = commonDirectory;
        }

        public async Task<List<string>> ReadTargetsAsync(CancellationToken cancellationToken = default)
        {
            var result = await RunAsync(cancellationToken, "for-each-ref", "--format=%(refname)%00%(symref)", "refs/heads/", "refs/remotes/").ConfigureAwait(false);
            Require(result);
            var targets = new List<string>();
            foreach (var line in result.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = line.TrimEnd('\r').Split('\0');
                if (parts.Length == 2 && parts[1].Length == 0)
                    targets.Add(parts[0]);
            }
            return targets;
        }

        public async Task<Models.TaskComparison> ReadAsync(string targetRef, Models.TaskComparisonMode mode, CancellationToken cancellationToken = default)
        {
            var snapshot = new Models.TaskComparison() { FullPath = _path, CommonDirectory = _commonDirectory, TargetRef = targetRef, Mode = mode };
            try
            {
                var status = await new QueryWorktreeStatus(_path) { CancellationToken = cancellationToken }.GetResultAsync(_commonDirectory).ConfigureAwait(false);
                snapshot.WorkingCopy = status;
                if (!status.IsReadable)
                    snapshot.Error = status.Error;
                else if (status.SyncState == Models.WorktreeSyncState.Unborn)
                    snapshot.Error = "TaskCompare.Unborn";
                if (snapshot.Error.Length > 0)
                    return snapshot;

                snapshot.SourceRef = status.Branch == "(detached)" ? "HEAD" : "refs/heads/" + status.Branch;
                snapshot.SourceHead = status.Head;
                snapshot.TargetHead = await ResolveTargetAsync(targetRef, cancellationToken).ConfigureAwait(false);
                if (snapshot.TargetHead == null)
                {
                    snapshot.Error = "TaskCompare.TargetMissing";
                    return snapshot;
                }

                if (mode == Models.TaskComparisonMode.MergeBase)
                {
                    var bases = await RunAsync(cancellationToken, "merge-base", "--all", snapshot.TargetHead, snapshot.SourceHead).ConfigureAwait(false);
                    if (bases.ExitCode != 0 && bases.ExitCode != 1)
                        Require(bases);
                    var hashes = bases.StdOut.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
                    if (hashes.Length != 1)
                    {
                        snapshot.Error = hashes.Length == 0 ? "TaskCompare.NoMergeBase" : "TaskCompare.MultipleMergeBases";
                        return snapshot;
                    }
                    snapshot.BaseHead = hashes[0];
                }
                else
                {
                    snapshot.BaseHead = snapshot.TargetHead;
                }

                // All content reads use immutable object IDs, including when refs move mid-query.
                var raw = await RunAsync(cancellationToken, "diff", "--raw", "-z", "--no-abbrev", "--find-renames", "--no-ext-diff", "--no-textconv", snapshot.BaseHead, snapshot.SourceHead, "--").ConfigureAwait(false);
                Require(raw);
                snapshot.Changes = ParseChanges(raw.StdOut);
                var stats = await RunAsync(cancellationToken, "diff", "--numstat", "-z", "--find-renames", "--no-ext-diff", "--no-textconv", snapshot.BaseHead, snapshot.SourceHead, "--").ConfigureAwait(false);
                Require(stats);
                ReadStats(stats.StdOut, snapshot);
                var range = snapshot.TargetHead + ".." + snapshot.SourceHead;
                var count = await RunAsync(cancellationToken, "rev-list", "--count", range, "--").ConfigureAwait(false);
                Require(count);
                snapshot.CommitCount = int.Parse(count.StdOut.Trim(), System.Globalization.CultureInfo.InvariantCulture);
                var log = await RunAsync(cancellationToken, "log", "--no-show-signature", "--format=%H%x00%s", "-z", "--max-count=500", range, "--").ConfigureAwait(false);
                Require(log);
                var fields = log.StdOut.Split('\0');
                for (var i = 0; i + 1 < fields.Length; i += 2)
                    snapshot.Commits.Add(new Models.TaskComparisonCommit() { SHA = fields[i], Subject = fields[i + 1] });
                snapshot.IsStale = !await IsCurrentAsync(snapshot, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or System.ComponentModel.Win32Exception or FormatException)
            {
                snapshot.Error = "TaskCompare.ReadFailed";
            }
            return snapshot;
        }

        public async Task<bool> IsCurrentAsync(Models.TaskComparison snapshot, CancellationToken cancellationToken = default)
        {
            if (!Directory.Exists(_path) || snapshot.SourceHead == null || snapshot.TargetHead == null)
                return false;
            var identity = await RunAsync(cancellationToken, "rev-parse", "--path-format=absolute", "--show-toplevel", "--git-common-dir").ConfigureAwait(false);
            var paths = identity.StdOut.TrimEnd('\r', '\n').Split('\n');
            if (!identity.IsSuccess || paths.Length != 2 ||
                !Models.WorktreeTaskStore.PathsEqual(paths[0].TrimEnd('\r'), _path) ||
                !Models.WorktreeTaskStore.PathsEqual(paths[1].TrimEnd('\r'), _commonDirectory))
                return false;
            var head = await RunAsync(cancellationToken, "rev-parse", "--verify", "HEAD").ConfigureAwait(false);
            var branch = await RunAsync(cancellationToken, "symbolic-ref", "-q", "HEAD").ConfigureAwait(false);
            var sourceRef = branch.IsSuccess ? branch.StdOut.TrimEnd('\r', '\n') : "HEAD";
            return head.IsSuccess && sourceRef == snapshot.SourceRef && head.StdOut.Trim() == snapshot.SourceHead &&
                await ResolveTargetAsync(snapshot.TargetRef, cancellationToken).ConfigureAwait(false) == snapshot.TargetHead;
        }

        private async Task<string> ResolveTargetAsync(string targetRef, CancellationToken cancellationToken)
        {
            if (string.IsNullOrEmpty(targetRef) ||
                (!targetRef.StartsWith("refs/heads/", StringComparison.Ordinal) && !targetRef.StartsWith("refs/remotes/", StringComparison.Ordinal)))
                return null;
            var valid = await RunAsync(cancellationToken, "check-ref-format", targetRef).ConfigureAwait(false);
            if (!valid.IsSuccess)
                return null;
            var result = await RunAsync(cancellationToken, "rev-parse", "--verify", "--end-of-options", targetRef + "^{commit}").ConfigureAwait(false);
            return result.IsSuccess ? result.StdOut.TrimEnd('\r', '\n') : null;
        }

        private static List<Models.Change> ParseChanges(string output)
        {
            var changes = new List<Models.Change>();
            var fields = output.Split('\0');
            for (var i = 0; i + 1 < fields.Length;)
            {
                var header = fields[i++];
                if (header.Length == 0) break;
                var type = header[(header.LastIndexOf(' ') + 1)];
                var path = fields[i++];
                var change = new Models.Change() { Path = path, Index = type switch
                {
                    'A' => Models.ChangeState.Added,
                    'D' => Models.ChangeState.Deleted,
                    'R' => Models.ChangeState.Renamed,
                    'C' => Models.ChangeState.Copied,
                    'T' => Models.ChangeState.TypeChanged,
                    _ => Models.ChangeState.Modified,
                } };
                if (type is 'R' or 'C')
                {
                    change.OriginalPath = path;
                    change.Path = fields[i++];
                }
                changes.Add(change);
            }
            return changes;
        }

        private static void ReadStats(string output, Models.TaskComparison snapshot)
        {
            var fields = output.Split('\0');
            for (var i = 0; i < fields.Length; i++)
            {
                if (fields[i].Length == 0) continue;
                var parts = fields[i].Split('\t', 3);
                if (parts.Length != 3) throw new FormatException("Invalid diff statistics.");
                if (parts[0] == "-") snapshot.BinaryFiles++;
                else
                {
                    snapshot.AddedLines += long.Parse(parts[0], System.Globalization.CultureInfo.InvariantCulture);
                    snapshot.DeletedLines += long.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture);
                }
                if (parts[2].Length == 0) i += 2; // Rename paths follow as separate NUL fields.
            }
        }

        private async Task<Command.Result> RunAsync(CancellationToken cancellationToken, params string[] args)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            var info = new ProcessStartInfo(Native.OS.GitExecutable)
            {
                WorkingDirectory = _path, UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
            };
            foreach (var arg in new[] { "--no-pager", "--no-optional-locks", "-c", "core.quotepath=off" }) info.ArgumentList.Add(arg);
            foreach (var arg in args) info.ArgumentList.Add(arg);
            using var process = new Process() { StartInfo = info };
            process.Start();
            try
            {
                var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
                var error = process.StandardError.ReadToEndAsync(timeout.Token);
                await Task.WhenAll(output, error, process.WaitForExitAsync(timeout.Token)).ConfigureAwait(false);
                return new Command.Result() { IsSuccess = process.ExitCode == 0, ExitCode = process.ExitCode, StdOut = await output, StdErr = await error };
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new IOException("Comparison query timed out.");
            }
            finally
            {
                if (!process.HasExited)
                {
                    try { process.Kill(true); }
                    catch (InvalidOperationException) { }
                    catch (System.ComponentModel.Win32Exception) { }
                }
            }
        }

        private static void Require(Command.Result result)
        {
            if (!result.IsSuccess) throw new IOException("Comparison query failed.");
        }

        private readonly string _path;
        private readonly string _commonDirectory;
    }
}
