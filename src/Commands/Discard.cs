using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace SourceGit.Commands
{
    public static class Discard
    {
        /// <summary>
        /// Safely restores local changes according to separated baselines and stores tracked baselines
        /// in the 30-day local recovery store before modifying the repository. Never runs destructive `git clean -fdx`
        /// or permanent `File.Delete` on untracked items.
        /// </summary>
        public static async Task AllAsync(string repo, bool includeModified, bool includeUntracked, bool includeIgnored, Models.ICommandLog log)
        {
            var changes = await new QueryLocalChanges(repo).GetResultAsync().ConfigureAwait(false);

            if (includeModified)
            {
                var tracked = changes.Where(c => c.Index != Models.ChangeState.Untracked && c.WorkTree != Models.ChangeState.Untracked).ToList();
                if (tracked.Count > 0)
                {
                    var res = await DiscardBackupService.ExecuteDiscardAsync(
                        repo,
                        tracked,
                        Models.DiscardBaselineKind.RestoreToHead,
                        outerLog: log).ConfigureAwait(false);

                    if (!res.Success)
                        Models.Notification.Send(repo, res.Summary, true);
                }
            }

            if (includeUntracked)
            {
                var untracked = changes.Where(c => c.Index == Models.ChangeState.Untracked || c.WorkTree == Models.ChangeState.Untracked).ToList();
                if (untracked.Count > 0)
                {
                    var res = await DiscardBackupService.ExecuteDiscardAsync(
                        repo,
                        untracked,
                        Models.DiscardBaselineKind.MoveUntrackedToRecycleBin,
                        outerLog: log).ConfigureAwait(false);

                    if (!res.Success)
                        Models.Notification.Send(repo, res.Summary, true);
                }
            }

            if (includeIgnored)
            {
                log?.AppendLine("[Discard] 按 GitNest 安全策略，不支持在批量丢弃中默认清理所有被忽略文件（git clean -fdx）。");
            }
        }

        /// <summary>
        /// Discard selected changes cleanly: tracked unstaged edits restore to index after saving to the 30-day
        /// recovery store; untracked entries move only via the OS Recycle Bin.
        /// </summary>
        public static async Task ChangesAsync(string repo, List<Models.Change> changes, Models.ICommandLog log)
        {
            if (changes == null || changes.Count == 0)
                return;

            var untracked = changes.Where(c => c.WorkTree == Models.ChangeState.Untracked || c.Index == Models.ChangeState.Untracked).ToList();
            var tracked = changes.Where(c => c.WorkTree != Models.ChangeState.Untracked && c.Index != Models.ChangeState.Untracked).ToList();

            if (tracked.Count > 0)
            {
                var res = await DiscardBackupService.ExecuteDiscardAsync(
                    repo,
                    tracked,
                    Models.DiscardBaselineKind.RestoreWorktreeToIndex,
                    outerLog: log).ConfigureAwait(false);

                if (!res.Success)
                    Models.Notification.Send(repo, res.Summary, true);
            }

            if (untracked.Count > 0)
            {
                var res = await DiscardBackupService.ExecuteDiscardAsync(
                    repo,
                    untracked,
                    Models.DiscardBaselineKind.MoveUntrackedToRecycleBin,
                    outerLog: log).ConfigureAwait(false);

                if (!res.Success)
                    Models.Notification.Send(repo, res.Summary, true);
            }
        }
    }
}
