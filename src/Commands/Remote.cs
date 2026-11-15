using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SourceGit.Commands
{
    public class Remote : Command
    {
        public Remote(string repo)
        {
            WorkingDirectory = repo;
            Context = repo;
        }

        public async Task<bool> AddAsync(string name, string url)
        {
            Args = $"remote add {name} {url}";
            return await ExecAsync();
        }

        public async Task<bool> DeleteAsync(string name)
        {
            Args = $"remote remove {name}";
            return await ExecAsync();
        }

        public async Task<bool> RenameAsync(string name, string to)
        {
            Args = $"remote rename {name} {to}";
            return await ExecAsync();
        }

        public async Task<bool> PruneAsync(string name, string sshKey)
        {
            SSHKey = sshKey;
            Args = $"remote prune {name}";
            return await ExecAsync();
        }

        public async Task<bool> SetURLAsync(string name, string url, bool isDelete, bool isPush)
        {
            var builder = new StringBuilder();
            builder.Append("remote set-url ");
            if (isDelete)
                builder.Append("--delete ");
            if (isPush)
                builder.Append("--push ");
            builder.Append(name).Append(' ').Append(url);

            Args = builder.ToString();
            return await ExecAsync();
        }

        /// <summary>
        /// Safely add a remote using argument-list execution and redacted diagnostics.
        /// </summary>
        public async Task<Result> AddTargetedAsync(string name, string url, CancellationToken cancellationToken = default, bool acquireWriteGuard = true)
        {
            if (string.IsNullOrWhiteSpace(name) || name.StartsWith('-'))
                return Result.Failed("Invalid remote name.");
            if (string.IsNullOrWhiteSpace(url) || url.StartsWith('-'))
                return Result.Failed("Invalid remote URL.");

            var (guardOk, guardErr, guardLease) = await AcquireConfigWriteGuardAsync($"添加远端 {name.Trim()}", acquireWriteGuard, cancellationToken).ConfigureAwait(false);
            await using var _ = guardLease;
            if (!guardOk)
                return guardErr;

            return RedactResult(await RunGitDirectAsync(WorkingDirectory, cancellationToken,
                "remote", "add", "--", name.Trim(), url.Trim()).ConfigureAwait(false));
        }

        /// <summary>
        /// Safely remove a remote using argument-list execution and redacted diagnostics.
        /// </summary>
        public async Task<Result> DeleteTargetedAsync(string name, CancellationToken cancellationToken = default, bool acquireWriteGuard = true)
        {
            if (string.IsNullOrWhiteSpace(name) || name.StartsWith('-'))
                return Result.Failed("Invalid remote name.");

            var (guardOk, guardErr, guardLease) = await AcquireConfigWriteGuardAsync($"删除远端 {name.Trim()}", acquireWriteGuard, cancellationToken).ConfigureAwait(false);
            await using var _ = guardLease;
            if (!guardOk)
                return guardErr;

            return RedactResult(await RunGitDirectAsync(WorkingDirectory, cancellationToken,
                "remote", "remove", name.Trim()).ConfigureAwait(false));
        }

        /// <summary>
        /// Safely rename a remote using argument-list execution and redacted diagnostics.
        /// </summary>
        public async Task<Result> RenameTargetedAsync(string name, string to, CancellationToken cancellationToken = default, bool acquireWriteGuard = true)
        {
            if (string.IsNullOrWhiteSpace(name) || name.StartsWith('-'))
                return Result.Failed("Invalid remote name.");
            if (string.IsNullOrWhiteSpace(to) || to.StartsWith('-'))
                return Result.Failed("Invalid target remote name.");

            var (guardOk, guardErr, guardLease) = await AcquireConfigWriteGuardAsync($"重命名远端 {name.Trim()}", acquireWriteGuard, cancellationToken).ConfigureAwait(false);
            await using var _ = guardLease;
            if (!guardOk)
                return guardErr;

            return RedactResult(await RunGitDirectAsync(WorkingDirectory, cancellationToken,
                "remote", "rename", name.Trim(), to.Trim()).ConfigureAwait(false));
        }

        /// <summary>
        /// Update or remove a specific remote fetch/push URL while preserving additional URLs and fetch refspecs.
        /// </summary>
        public async Task<Result> SetUrlTargetedAsync(
            string name,
            string newUrl,
            string oldUrl,
            bool isPush,
            bool isDelete,
            CancellationToken cancellationToken = default,
            bool acquireWriteGuard = true)
        {
            if (string.IsNullOrWhiteSpace(name) || name.StartsWith('-'))
                return Result.Failed("Invalid remote name.");

            var (guardOk, guardErr, guardLease) = await AcquireConfigWriteGuardAsync($"更新远端 {name.Trim()} URL", acquireWriteGuard, cancellationToken).ConfigureAwait(false);
            await using var _ = guardLease;
            if (!guardOk)
                return guardErr;

            var trimmedName = name.Trim();
            var args = new List<string> { "remote", "set-url" };
            if (isDelete)
            {
                if (string.IsNullOrWhiteSpace(oldUrl))
                    return Result.Failed("Missing URL to delete.");

                args.Add("--delete");
                if (isPush)
                    args.Add("--push");
                args.Add("--");
                args.Add(trimmedName);
                args.Add(BuildExactPosixEre(oldUrl.Trim()));
            }
            else
            {
                if (string.IsNullOrWhiteSpace(newUrl) || newUrl.StartsWith('-'))
                    return Result.Failed("Invalid remote URL.");

                if (isPush)
                    args.Add("--push");
                args.Add("--");
                args.Add(trimmedName);
                args.Add(newUrl.Trim());
                if (!string.IsNullOrWhiteSpace(oldUrl))
                    args.Add(BuildExactPosixEre(oldUrl.Trim()));
            }

            return RedactResult(await RunGitDirectAsync(WorkingDirectory, cancellationToken, args.ToArray()).ConfigureAwait(false));
        }

        private async Task<(bool Succeeded, Result ErrorResult, Models.RepositoryWriteGuardAcquireResult Guard)> AcquireConfigWriteGuardAsync(
            string operationName,
            bool acquireWriteGuard,
            CancellationToken cancellationToken)
        {
            if (acquireWriteGuard)
            {
                var guard = await Models.RepositoryWriteGuard.TryEnterAsync(
                    WorkingDirectory,
                    operationName,
                    checkIndexLock: false,
                    checkConfigLock: true,
                    cancellationToken: cancellationToken).ConfigureAwait(false);
                if (!guard.Acquired)
                {
                    await guard.DisposeAsync().ConfigureAwait(false);
                    return (false, Result.Failed(guard.LockDetection.Summary), null);
                }

                return (true, new Result { IsSuccess = true }, guard);
            }

            var locks = await Models.RepositoryWriteGuard.InspectLocksAsync(
                WorkingDirectory,
                checkIndexLock: false,
                checkConfigLock: true,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            if (locks.IsLocked)
                return (false, Result.Failed(locks.Summary), null);

            return (true, new Result { IsSuccess = true }, null);
        }

        private static string BuildExactPosixEre(string literal)
        {
            const string meta = @"\.^$|?*+()[]{}";
            var sb = new StringBuilder(literal.Length + 8);
            sb.Append('^');
            foreach (var ch in literal)
            {
                if (meta.Contains(ch, StringComparison.Ordinal))
                    sb.Append('\\');
                sb.Append(ch);
            }
            sb.Append('$');
            return sb.ToString();
        }
    }
}
