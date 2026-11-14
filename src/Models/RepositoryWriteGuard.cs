using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace SourceGit.Models
{
    public sealed class GitLockDetectionResult
    {
        public bool IsLocked => BlockingLockFiles.Count > 0;
        public IReadOnlyList<string> BlockingLockFiles { get; init; } = [];
        public string Summary { get; init; } = string.Empty;
    }

    public sealed class RepositoryWriteLease : IAsyncDisposable
    {
        private SemaphoreSlim _gate;

        internal RepositoryWriteLease(string canonicalCommonDir, string operationName, SemaphoreSlim gate)
        {
            CanonicalCommonDir = canonicalCommonDir;
            OperationName = operationName;
            _gate = gate;
        }

        public string CanonicalCommonDir { get; }
        public string OperationName { get; }

        public ValueTask DisposeAsync()
        {
            var gate = Interlocked.Exchange(ref _gate, null);
            gate?.Release();
            return ValueTask.CompletedTask;
        }
    }

    public sealed class RepositoryWriteGuardAcquireResult : IAsyncDisposable
    {
        public bool Acquired => Lease != null && !LockDetection.IsLocked;
        public RepositoryWriteLease Lease { get; init; }
        public GitLockDetectionResult LockDetection { get; init; } = new();

        public async ValueTask DisposeAsync()
        {
            if (Lease != null)
                await Lease.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Serializes repository write operations per shared Git common directory and respects native Git
    /// lock files without ever deleting them automatically.
    /// </summary>
    public static class RepositoryWriteGuard
    {
        private static readonly StringComparer PathComparer =
            OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

        private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new(PathComparer);

        public static async Task<(string GitDir, string GitCommonDir)> ResolveGitDirectoriesAsync(
            string repoPath,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(repoPath) || !Directory.Exists(repoPath))
                return (string.Empty, string.Empty);

            var res = await Commands.Command.RunGitDirectAsync(
                repoPath,
                cancellationToken,
                "rev-parse",
                "--path-format=absolute",
                "--git-dir",
                "--git-common-dir").ConfigureAwait(false);

            if (res.IsSuccess && !string.IsNullOrWhiteSpace(res.StdOut))
            {
                var lines = res.StdOut.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
                if (lines.Length >= 2)
                {
                    var gitDir = NormalizePath(lines[0]);
                    var commonDir = NormalizePath(lines[1]);
                    return (gitDir, commonDir);
                }
            }

            var fallbackGit = Path.Combine(repoPath, ".git");
            if (Directory.Exists(fallbackGit))
            {
                var normalized = NormalizePath(fallbackGit);
                return (normalized, normalized);
            }

            if (File.Exists(fallbackGit))
            {
                try
                {
                    var line = await File.ReadAllTextAsync(fallbackGit, cancellationToken).ConfigureAwait(false);
                    const string prefix = "gitdir:";
                    var trimmed = line.Trim();
                    if (trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    {
                        var relOrAbs = trimmed.Substring(prefix.Length).Trim();
                        var gitDir = NormalizePath(Path.IsPathRooted(relOrAbs)
                            ? relOrAbs
                            : Path.Combine(repoPath, relOrAbs));
                        var commonFile = Path.Combine(gitDir, "commondir");
                        var commonDir = gitDir;
                        if (File.Exists(commonFile))
                        {
                            var commonRel = (await File.ReadAllTextAsync(commonFile, cancellationToken).ConfigureAwait(false)).Trim();
                            commonDir = NormalizePath(Path.IsPathRooted(commonRel)
                                ? commonRel
                                : Path.Combine(gitDir, commonRel));
                        }

                        return (gitDir, commonDir);
                    }
                }
                catch
                {
                    // Fall back to empty paths below if reading .git pointer fails.
                }
            }

            var canonicalRepo = NormalizePath(repoPath);
            return (canonicalRepo, canonicalRepo);
        }

        public static async Task<GitLockDetectionResult> InspectLocksAsync(
            string repoPath,
            bool checkIndexLock = true,
            string branchNameToUpdate = null,
            CancellationToken cancellationToken = default)
        {
            var (gitDir, commonDir) = await ResolveGitDirectoriesAsync(repoPath, cancellationToken).ConfigureAwait(false);
            return InspectLocksFromDirectories(gitDir, commonDir, checkIndexLock, branchNameToUpdate);
        }

        public static async Task<RepositoryWriteGuardAcquireResult> TryEnterAsync(
            string repoPath,
            string operationName,
            bool checkIndexLock = true,
            string branchNameToUpdate = null,
            CancellationToken cancellationToken = default)
        {
            var (gitDir, commonDir) = await ResolveGitDirectoriesAsync(repoPath, cancellationToken).ConfigureAwait(false);
            var key = string.IsNullOrEmpty(commonDir) ? NormalizePath(repoPath) : commonDir;
            var gate = Gates.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));

            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var locks = InspectLocksFromDirectories(gitDir, commonDir, checkIndexLock, branchNameToUpdate);
                if (locks.IsLocked)
                {
                    gate.Release();
                    return new RepositoryWriteGuardAcquireResult
                    {
                        Lease = null,
                        LockDetection = locks,
                    };
                }

                return new RepositoryWriteGuardAcquireResult
                {
                    Lease = new RepositoryWriteLease(key, operationName, gate),
                    LockDetection = locks,
                };
            }
            catch
            {
                gate.Release();
                throw;
            }
        }

        private static GitLockDetectionResult InspectLocksFromDirectories(
            string gitDir,
            string commonDir,
            bool checkIndexLock,
            string branchNameToUpdate)
        {
            var found = new List<string>();
            var candidates = new List<string>();

            if (!string.IsNullOrEmpty(gitDir))
            {
                if (checkIndexLock)
                    candidates.Add(Path.Combine(gitDir, "index.lock"));
                candidates.Add(Path.Combine(gitDir, "HEAD.lock"));
            }

            if (!string.IsNullOrEmpty(commonDir))
            {
                candidates.Add(Path.Combine(commonDir, "packed-refs.lock"));
                candidates.Add(Path.Combine(commonDir, "shallow.lock"));

                if (!string.IsNullOrWhiteSpace(branchNameToUpdate))
                {
                    var normalizedBranch = branchNameToUpdate.Replace('/', Path.DirectorySeparatorChar);
                    candidates.Add(Path.Combine(commonDir, "refs", "heads", normalizedBranch + ".lock"));
                }
            }

            var seen = new HashSet<string>(PathComparer);
            foreach (var candidate in candidates)
            {
                var normalized = NormalizePath(candidate);
                if (seen.Add(normalized) && File.Exists(normalized))
                    found.Add(normalized);
            }

            if (found.Count == 0)
                return new GitLockDetectionResult();

            var summary = $"检测到 Git 原生锁文件，已有其他 Git 或外部工具正在操作仓库：{string.Join(", ", found)}";
            return new GitLockDetectionResult
            {
                BlockingLockFiles = found,
                Summary = summary,
            };
        }

        private static string NormalizePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return string.Empty;

            try
            {
                return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            }
            catch
            {
                return path;
            }
        }
    }
}
