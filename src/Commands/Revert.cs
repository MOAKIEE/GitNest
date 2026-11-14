using System.Text;

namespace SourceGit.Commands
{
    public class Revert : Command
    {
        public Revert(string repo, string commit, bool autoCommit)
        {
            WorkingDirectory = repo;
            Context = repo;

            var safeCommit = CommandArgumentHelper.ValidateNonOptionToken(commit, nameof(commit));
            var builder = new StringBuilder(512);
            builder.Append("revert --no-edit ");
            if (!autoCommit)
                builder.Append("--no-commit ");
            builder.Append(CommandArgumentHelper.Quote(safeCommit));

            Args = builder.ToString();
        }
    }
}
