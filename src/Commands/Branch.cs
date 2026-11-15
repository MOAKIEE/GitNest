using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SourceGit.Commands
{
    public class Branch : Command
    {
        public Branch(string repo, string name)
        {
            WorkingDirectory = repo;
            Context = repo;
            _name = name;
        }

        public async Task<bool> CreateAsync(string basedOn, bool force)
        {
            var builder = new StringBuilder();
            builder.Append("branch ");
            if (force)
                builder.Append("-f ");
            builder.Append(_name);
            builder.Append(" ");
            builder.Append(basedOn);

            Args = builder.ToString();
            return await ExecAsync().ConfigureAwait(false);
        }

        public async Task<bool> RenameAsync(string to)
        {
            Args = $"branch -M {_name} {to}";
            return await ExecAsync().ConfigureAwait(false);
        }

        public async Task<bool> SetUpstreamAsync(Models.Branch tracking)
        {
            if (tracking == null)
                Args = $"branch {_name} --unset-upstream";
            else
                Args = $"branch {_name} -u {tracking.FriendlyName}";

            return await ExecAsync().ConfigureAwait(false);
        }

        public async Task<bool> DeleteLocalAsync(bool force)
        {
            Args = $"branch {(force ? "-D" : "-d")} {_name}";
            return await ExecAsync().ConfigureAwait(false);
        }

        public async Task<bool> DeleteRemoteAsync(string remote, bool force)
        {
            Args = $"branch {(force ? "-D" : "-d")} -r {remote}/{_name}";
            return await ExecAsync().ConfigureAwait(false);
        }

        /// <summary>
        /// Set the upstream tracking branch for this local branch, falling back to local branch.* keys when unborn or unfetched.
        /// </summary>
        public async Task<Result> SetUpstreamTargetedAsync(
            string remoteName,
            string remoteBranchName,
            bool allowUnfetchedOrUnborn = true,
            CancellationToken cancellationToken = default,
            bool acquireWriteGuard = true)
        {
            if (string.IsNullOrWhiteSpace(_name) || _name.StartsWith('-'))
                return Result.Failed("Invalid local branch name.");
            if (string.IsNullOrWhiteSpace(remoteName) || remoteName.StartsWith('-'))
                return Result.Failed("Invalid remote name.");
            if (string.IsNullOrWhiteSpace(remoteBranchName) || remoteBranchName.StartsWith('-'))
                return Result.Failed("Invalid upstream branch name.");

            var trimmedRemote = remoteName.Trim();
            var trimmedTarget = remoteBranchName.Trim();
            if (trimmedTarget.StartsWith($"refs/remotes/{trimmedRemote}/", StringComparison.Ordinal))
                trimmedTarget = trimmedTarget.Substring($"refs/remotes/{trimmedRemote}/".Length);
            else if (trimmedTarget.StartsWith($"{trimmedRemote}/", StringComparison.Ordinal))
                trimmedTarget = trimmedTarget.Substring(trimmedRemote.Length + 1);
            else if (trimmedTarget.StartsWith("refs/heads/", StringComparison.Ordinal))
                trimmedTarget = trimmedTarget.Substring("refs/heads/".Length);

            if (string.IsNullOrEmpty(trimmedTarget))
                return Result.Failed("Invalid upstream branch name.");

            await using var guard = acquireWriteGuard
                ? await Models.RepositoryWriteGuard.TryEnterAsync(
                    WorkingDirectory,
                    $"设置分支 {_name} 上游",
                    checkIndexLock: false,
                    checkConfigLock: true,
                    cancellationToken: cancellationToken).ConfigureAwait(false)
                : null;
            if (guard != null && !guard.Acquired)
                return Result.Failed(guard.LockDetection.Summary);

            if (!acquireWriteGuard)
            {
                var locks = await Models.RepositoryWriteGuard.InspectLocksAsync(
                    WorkingDirectory,
                    checkIndexLock: false,
                    checkConfigLock: true,
                    cancellationToken: cancellationToken).ConfigureAwait(false);
                if (locks.IsLocked)
                    return Result.Failed(locks.Summary);
            }

            var remoteTrackingRef = trimmedRemote == "." ? $"refs/heads/{trimmedTarget}" : $"refs/remotes/{trimmedRemote}/{trimmedTarget}";
            var refCheck = await RunGitDirectAsync(WorkingDirectory, cancellationToken, "show-ref", "--verify", "--quiet", remoteTrackingRef).ConfigureAwait(false);
            var headCheck = await RunGitDirectAsync(WorkingDirectory, cancellationToken, "rev-parse", "--verify", "--quiet", "HEAD").ConfigureAwait(false);

            if (refCheck.IsSuccess && headCheck.IsSuccess)
            {
                var trackingArg = trimmedRemote == "." ? trimmedTarget : $"{trimmedRemote}/{trimmedTarget}";
                var setRes = await RunGitDirectAsync(WorkingDirectory, cancellationToken, "branch", $"--set-upstream-to={trackingArg}", _name).ConfigureAwait(false);
                return RedactResult(setRes);
            }

            if (!allowUnfetchedOrUnborn)
                return Result.Failed($"Upstream branch '{trimmedRemote}/{trimmedTarget}' does not exist locally.");

            var cfg = new Config(WorkingDirectory);
            var remoteRes = await cfg.SetScopedAsync(Models.GitConfigWriteScope.Local, $"branch.{_name}.remote", trimmedRemote, cancellationToken: cancellationToken, acquireWriteGuard: false).ConfigureAwait(false);
            if (!remoteRes.IsSuccess)
                return RedactResult(remoteRes);

            var mergeRes = await cfg.SetScopedAsync(Models.GitConfigWriteScope.Local, $"branch.{_name}.merge", $"refs/heads/{trimmedTarget}", cancellationToken: cancellationToken, acquireWriteGuard: false).ConfigureAwait(false);
            return RedactResult(mergeRes);
        }

        /// <summary>
        /// Remove upstream tracking configuration for this local branch cleanly, including on unborn branches.
        /// </summary>
        public async Task<Result> UnsetUpstreamTargetedAsync(CancellationToken cancellationToken = default, bool acquireWriteGuard = true)
        {
            if (string.IsNullOrWhiteSpace(_name) || _name.StartsWith('-'))
                return Result.Failed("Invalid local branch name.");

            await using var guard = acquireWriteGuard
                ? await Models.RepositoryWriteGuard.TryEnterAsync(
                    WorkingDirectory,
                    $"清除分支 {_name} 上游",
                    checkIndexLock: false,
                    checkConfigLock: true,
                    cancellationToken: cancellationToken).ConfigureAwait(false)
                : null;
            if (guard != null && !guard.Acquired)
                return Result.Failed(guard.LockDetection.Summary);

            if (!acquireWriteGuard)
            {
                var locks = await Models.RepositoryWriteGuard.InspectLocksAsync(
                    WorkingDirectory,
                    checkIndexLock: false,
                    checkConfigLock: true,
                    cancellationToken: cancellationToken).ConfigureAwait(false);
                if (locks.IsLocked)
                    return Result.Failed(locks.Summary);
            }

            var unsetRes = await RunGitDirectAsync(WorkingDirectory, cancellationToken, "branch", "--unset-upstream", _name).ConfigureAwait(false);
            if (unsetRes.IsSuccess)
                return unsetRes;

            var cfg = new Config(WorkingDirectory);
            var r1 = await cfg.SetScopedAsync(Models.GitConfigWriteScope.Local, $"branch.{_name}.remote", string.Empty, unset: true, cancellationToken: cancellationToken, acquireWriteGuard: false).ConfigureAwait(false);
            var r2 = await cfg.SetScopedAsync(Models.GitConfigWriteScope.Local, $"branch.{_name}.merge", string.Empty, unset: true, cancellationToken: cancellationToken, acquireWriteGuard: false).ConfigureAwait(false);
            if (r1.IsSuccess && r2.IsSuccess)
                return new Result { IsSuccess = true, ExitCode = 0 };

            return RedactResult(!r1.IsSuccess ? r1 : r2);
        }

        private readonly string _name;
    }
}
