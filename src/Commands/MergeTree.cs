using System.Diagnostics;
using System.Threading.Tasks;

namespace SourceGit.Commands
{
    public class MergeTree : Command
    {
        public MergeTree(string repo, string source, string dest)
        {
            WorkingDirectory = repo;
            var safeSource = CommandArgumentHelper.ValidateNonOptionToken(source, nameof(source));
            var safeDest = CommandArgumentHelper.ValidateNonOptionToken(dest, nameof(dest));
            Args = $"merge-tree --write-tree {CommandArgumentHelper.Quote(safeSource)} {CommandArgumentHelper.Quote(safeDest)}";
        }

        public async Task<int> GetExitCodeAsync()
        {
            using var proc = new Process();
            proc.StartInfo = CreateGitStartInfo(false);

            var exitCode = -1;
            try
            {
                proc.Start();
                await proc.WaitForExitAsync().ConfigureAwait(false);
                exitCode = proc.ExitCode;
            }
            catch
            {
                // Ignore any exceptions and just return -1
            }

            return exitCode;
        }
    }
}
