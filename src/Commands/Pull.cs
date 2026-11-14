using System.Text;

namespace SourceGit.Commands
{
    public class Pull : Command
    {
        public Pull(string repo, Models.Remote remote, Models.Branch remoteBranch, bool useRebase)
        {
            WorkingDirectory = repo;
            Context = repo;
            SSHKey = remote.PrivateSSHKey;

            var builder = new StringBuilder(512);
            builder
                .Append("pull --verbose --progress --rebase=")
                .Append(useRebase ? "true" : "false")
                .Append(' ')
                .Append(CommandArgumentHelper.Quote(remote.Name));

            if (remoteBranch != null)
                builder.Append(' ').Append(CommandArgumentHelper.Quote(remoteBranch.Name));

            Args = builder.ToString();
        }

        public Pull(string repo, Models.Remote remote, string remoteBranchName, bool fastForwardOnly = true)
        {
            WorkingDirectory = repo;
            Context = repo;
            SSHKey = remote?.PrivateSSHKey ?? string.Empty;

            var builder = new StringBuilder(512);
            if (fastForwardOnly)
                builder.Append("-c pull.rebase=false pull --verbose --progress --ff-only -- ");
            else
                builder.Append("pull --verbose --progress -- ");

            builder.Append(CommandArgumentHelper.Quote(remote?.Name ?? string.Empty));
            if (!string.IsNullOrWhiteSpace(remoteBranchName))
                builder.Append(' ').Append(CommandArgumentHelper.Quote(remoteBranchName));

            Args = builder.ToString();
        }
    }
}
