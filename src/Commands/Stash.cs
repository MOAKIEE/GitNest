using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace SourceGit.Commands
{
    public class Stash : Command
    {
        public Stash(string repo)
        {
            WorkingDirectory = repo;
            Context = repo;
        }

        public async Task<bool> PushAsync(string message, bool includeUntracked = true, bool keepIndex = false)
        {
            var args = new List<string> { "stash", "push" };
            if (includeUntracked)
                args.Add("--include-untracked");
            if (keepIndex)
                args.Add("--keep-index");
            if (!string.IsNullOrEmpty(message))
            {
                args.Add("-m");
                args.Add(SanitizeMessage(message));
            }

            return await ExecDirectAsync(args, null).ConfigureAwait(false);
        }

        public async Task<bool> PushAsync(string message, List<Models.Change> changes, bool keepIndex)
        {
            var args = new List<string> { "--literal-pathspecs", "stash", "push", "--include-untracked" };
            if (keepIndex)
                args.Add("--keep-index");
            if (!string.IsNullOrEmpty(message))
            {
                args.Add("-m");
                args.Add(SanitizeMessage(message));
            }

            args.Add("--pathspec-from-file=-");
            args.Add("--pathspec-file-nul");
            var stdin = CommandArgumentHelper.BuildNulPathspecBytes(changes.Select(c => c.Path));
            return await ExecDirectAsync(args, stdin).ConfigureAwait(false);
        }

        public async Task<bool> PushAsync(string message, string pathspecFromFile, bool keepIndex)
        {
            var args = new List<string> { "--literal-pathspecs", "stash", "push", "--include-untracked", $"--pathspec-from-file={pathspecFromFile}" };
            if (keepIndex)
                args.Add("--keep-index");
            if (!string.IsNullOrEmpty(message))
            {
                args.Add("-m");
                args.Add(SanitizeMessage(message));
            }

            return await ExecDirectAsync(args, null).ConfigureAwait(false);
        }

        public async Task<bool> PushOnlyStagedAsync(string message, bool keepIndex)
        {
            var args = new List<string> { "stash", "push", "--staged" };
            if (keepIndex)
                args.Add("--keep-index");
            if (!string.IsNullOrEmpty(message))
            {
                args.Add("-m");
                args.Add(SanitizeMessage(message));
            }

            return await ExecDirectAsync(args, null).ConfigureAwait(false);
        }

        public async Task<bool> ApplyAsync(string name, bool restoreIndex)
        {
            var safeName = CommandArgumentHelper.ValidateNonOptionToken(name, nameof(name));
            var args = new List<string> { "stash", "apply", "-q" };
            if (restoreIndex)
                args.Add("--index");
            args.Add(safeName);

            return await ExecDirectAsync(args, null).ConfigureAwait(false);
        }

        public async Task<bool> CheckoutBranchAsync(string name, string branch)
        {
            var safeName = CommandArgumentHelper.ValidateNonOptionToken(name, nameof(name));
            var safeBranch = CommandArgumentHelper.ValidateNonOptionToken(branch, nameof(branch));
            return await ExecDirectAsync(["stash", "branch", safeBranch, safeName], null).ConfigureAwait(false);
        }

        public async Task<bool> PopAsync(string name)
        {
            var safeName = CommandArgumentHelper.ValidateNonOptionToken(name, nameof(name));
            return await ExecDirectAsync(["stash", "pop", "-q", "--index", safeName], null).ConfigureAwait(false);
        }

        public async Task<bool> DropAsync(string name)
        {
            var safeName = CommandArgumentHelper.ValidateNonOptionToken(name, nameof(name));
            return await ExecDirectAsync(["stash", "drop", "-q", safeName], null).ConfigureAwait(false);
        }

        public async Task<bool> ClearAsync()
        {
            return await ExecDirectAsync(["stash", "clear"], null).ConfigureAwait(false);
        }

        private async Task<bool> ExecDirectAsync(IReadOnlyList<string> args, byte[] stdinBytes)
        {
            Log?.AppendLine($"$ git {string.Join(" ", args.Select(CommandArgumentHelper.Quote))}");
            var res = await RunGitDirectWithInputAsync(
                WorkingDirectory,
                TimeSpan.FromSeconds(30),
                stdinBytes,
                CancellationToken.None,
                [.. args]).ConfigureAwait(false);

            if (!string.IsNullOrWhiteSpace(res.StdOut))
                Log?.AppendLine(res.StdOut.TrimEnd());
            if (!string.IsNullOrWhiteSpace(res.StdErr))
                Log?.AppendLine(res.StdErr.TrimEnd());

            return res.IsSuccess;
        }

        private static string SanitizeMessage(string raw) =>
            (raw ?? string.Empty).Replace("\0", string.Empty).Replace('\r', ' ').Replace('\n', ' ');
    }
}
