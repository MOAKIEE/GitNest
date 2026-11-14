using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SourceGit.Commands
{
    public static class QueryStagedSnapshot
    {
        public static async Task<Models.StagedSnapshot> CaptureAsync(
            string repoPath,
            CancellationToken cancellationToken = default)
        {
            var (gitDir, gitCommonDir) = await Models.RepositoryWriteGuard
                .ResolveGitDirectoriesAsync(repoPath, cancellationToken)
                .ConfigureAwait(false);

            var headRes = await Command.RunGitDirectAsync(
                repoPath,
                cancellationToken,
                "rev-parse",
                "--verify",
                "-q",
                "HEAD").ConfigureAwait(false);

            var isUnborn = !headRes.IsSuccess || string.IsNullOrWhiteSpace(headRes.StdOut);
            var headOid = isUnborn ? string.Empty : headRes.StdOut.Trim();

            var branchRes = await Command.RunGitDirectAsync(
                repoPath,
                cancellationToken,
                "symbolic-ref",
                "--quiet",
                "--short",
                "HEAD").ConfigureAwait(false);

            var isDetached = !branchRes.IsSuccess || string.IsNullOrWhiteSpace(branchRes.StdOut);
            var currentBranch = !isDetached
                ? branchRes.StdOut.Trim()
                : (headOid.Length >= 8 ? headOid[..8] : headOid);

            var lsFilesRes = await Command.RunGitDirectAsync(
                repoPath,
                cancellationToken,
                "-c",
                "core.quotepath=off",
                "ls-files",
                "--stage",
                "-z").ConfigureAwait(false);

            var statusRes = await Command.RunGitDirectAsync(
                repoPath,
                cancellationToken,
                "-c",
                "core.quotepath=off",
                "status",
                "--porcelain=v1",
                "-z",
                "--untracked-files=all").ConfigureAwait(false);

            var allChanges = ParsePorcelainZ(statusRes.IsSuccess ? statusRes.StdOut : string.Empty);
            var staged = new List<Models.StagedChangeEntry>();
            var unstaged = new List<Models.StagedChangeEntry>();
            var partial = new List<string>();
            var hasConflicts = false;

            foreach (var entry in allChanges)
            {
                if (entry.IsConflicted)
                    hasConflicts = true;
                if (entry.IsStaged)
                    staged.Add(entry);
                if (entry.IsUnstaged)
                    unstaged.Add(entry);
                if (entry.IsPartiallyStaged)
                    partial.Add(entry.Path);
            }

            var fingerprint = ComputeIndexFingerprint(
                isUnborn,
                isDetached,
                headOid,
                currentBranch,
                lsFilesRes.IsSuccess ? lsFilesRes.StdOut : string.Empty);

            return new Models.StagedSnapshot
            {
                RepoPath = repoPath,
                GitDir = gitDir,
                GitCommonDir = gitCommonDir,
                HeadOid = headOid,
                IsUnborn = isUnborn,
                IsDetachedHead = isDetached,
                CurrentBranchName = currentBranch,
                IndexFingerprint = fingerprint,
                HasConflicts = hasConflicts,
                AllChanges = allChanges,
                StagedChanges = staged,
                UnstagedChanges = unstaged,
                PartiallyStagedPaths = partial,
                CapturedAtUtc = DateTimeOffset.UtcNow,
            };
        }

        public static async Task<bool> HasHeadCommitAsync(string repoPath, CancellationToken cancellationToken = default)
        {
            var res = await Command.RunGitDirectAsync(
                repoPath,
                cancellationToken,
                "rev-parse",
                "--verify",
                "-q",
                "HEAD").ConfigureAwait(false);

            return res.IsSuccess && !string.IsNullOrWhiteSpace(res.StdOut);
        }

        public static async Task<(bool Ok, int Ahead, int Behind)> TryGetDivergenceCountsAsync(
            string repoPath,
            string localRef,
            string remoteRef,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(localRef) || string.IsNullOrWhiteSpace(remoteRef))
                return (false, 0, 0);

            var res = await Command.RunGitDirectAsync(
                repoPath,
                cancellationToken,
                "rev-list",
                "--left-right",
                "--count",
                $"{localRef}...{remoteRef}").ConfigureAwait(false);

            if (!res.IsSuccess || string.IsNullOrWhiteSpace(res.StdOut))
                return (false, 0, 0);

            var parts = res.StdOut.Trim().Split((char[])[' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2 &&
                int.TryParse(parts[0], out var ahead) &&
                int.TryParse(parts[1], out var behind))
            {
                return (true, ahead, behind);
            }

            return (false, 0, 0);
        }

        internal static List<Models.StagedChangeEntry> ParsePorcelainZ(string output)
        {
            var results = new List<Models.StagedChangeEntry>();
            if (string.IsNullOrEmpty(output))
                return results;

            var tokens = output.Split('\0', StringSplitOptions.None);
            for (var i = 0; i < tokens.Length; i++)
            {
                var record = tokens[i];
                if (string.IsNullOrEmpty(record) || record.Length < 4 || record[2] != ' ')
                    continue;

                var x = record[0];
                var y = record[1];
                var path = record.Substring(3);
                var originalPath = string.Empty;

                // In porcelain=v1 -z, renamed/copied entries emit "XY new_path\0orig_path\0".
                if ((x == 'R' || x == 'C' || y == 'R' || y == 'C') && i + 1 < tokens.Length)
                {
                    originalPath = tokens[i + 1];
                    i++;
                }

                results.Add(new Models.StagedChangeEntry
                {
                    Path = path,
                    OriginalPath = originalPath,
                    IndexState = x == '?' ? Models.ChangeState.None : MapStatusChar(x),
                    WorkTreeState = MapStatusChar(y),
                });
            }

            return results;
        }

        private static Models.ChangeState MapStatusChar(char c)
        {
            return c switch
            {
                'M' => Models.ChangeState.Modified,
                'T' => Models.ChangeState.TypeChanged,
                'A' => Models.ChangeState.Added,
                'D' => Models.ChangeState.Deleted,
                'R' => Models.ChangeState.Renamed,
                'C' => Models.ChangeState.Copied,
                'U' => Models.ChangeState.Conflicted,
                '?' => Models.ChangeState.Untracked,
                _ => Models.ChangeState.None,
            };
        }

        private static string ComputeIndexFingerprint(
            bool isUnborn,
            bool isDetached,
            string headOid,
            string currentBranch,
            string lsFilesOutput)
        {
            var payload = new StringBuilder(lsFilesOutput.Length + 128);
            payload.Append(isUnborn ? "U" : "H").Append('|')
                .Append(isDetached ? "D" : "B").Append('|')
                .Append(headOid).Append('|')
                .Append(currentBranch).Append('\0')
                .Append(lsFilesOutput);

            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(payload.ToString()));
            return Convert.ToHexString(hash);
        }
    }
}
