using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace SourceGit.Commands
{
    public class Worktree : Command
    {
        public Worktree(string repo)
        {
            WorkingDirectory = repo;
            Context = repo;
        }

        public async Task<List<Models.Worktree>> ReadAllAsync()
        {
            Args = "worktree list --porcelain -z";

            var rs = await ReadToEndAsync().ConfigureAwait(false);
            // Older supported Git versions do not have `worktree list -z`.
            if (!rs.IsSuccess)
            {
                Args = "worktree list --porcelain";
                rs = await ReadToEndAsync().ConfigureAwait(false);
            }
            var worktrees = new List<Models.Worktree>();
            Models.Worktree last = null;
            if (rs.IsSuccess)
            {
                var lines = rs.StdOut.Contains('\0')
                    ? rs.StdOut.Split('\0', StringSplitOptions.RemoveEmptyEntries)
                    : rs.StdOut.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
                foreach (var line in lines)
                {
                    if (line.StartsWith("worktree ", StringComparison.Ordinal))
                    {
                        last = new Models.Worktree() { FullPath = line.Substring(9) };
                        worktrees.Add(last);
                        continue;
                    }

                    if (last == null)
                        continue;

                    if (line.StartsWith("bare", StringComparison.Ordinal))
                    {
                        last.IsBare = true;
                    }
                    else if (line.StartsWith("HEAD ", StringComparison.Ordinal))
                    {
                        last.Head = line.Substring(5).Trim();
                    }
                    else if (line.StartsWith("branch ", StringComparison.Ordinal))
                    {
                        last.Branch = line.Substring(7).Trim();
                    }
                    else if (line.StartsWith("detached", StringComparison.Ordinal))
                    {
                        last.IsDetached = true;
                    }
                    else if (line.StartsWith("locked", StringComparison.Ordinal))
                    {
                        last.IsLocked = true;
                        last.LockReason = line.Length > 7 ? line.Substring(7) : string.Empty;
                    }
                    else if (line.StartsWith("prunable", StringComparison.Ordinal))
                    {
                        last.PrunableReason = line.Length > 9 ? line.Substring(9) : string.Empty;
                    }
                }
            }

            return worktrees;
        }

        public async Task<bool> LockAsync(string fullpath)
        {
            Args = $"worktree lock {fullpath.Quoted()}";
            return await ExecAsync().ConfigureAwait(false);
        }

        public async Task<bool> UnlockAsync(string fullpath)
        {
            Args = $"worktree unlock {fullpath.Quoted()}";
            return await ExecAsync().ConfigureAwait(false);
        }
    }
}
