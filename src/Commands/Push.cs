using System.Text;

namespace SourceGit.Commands
{
    public class Push : Command
    {
        public Push(
            string repo,
            string sourceSha,
            Models.Remote remote,
            string remoteBranchName,
            bool checkSubmodules,
            bool noVerify,
            string pushUrl)
        {
            WorkingDirectory = repo;
            Context = repo;
            SSHKey = remote?.PrivateSSHKey ?? string.Empty;

            var builder = new StringBuilder(1024);
            builder.Append("push --progress --verbose --no-follow-tags ");
            if (checkSubmodules)
                builder.Append("--recurse-submodules=check ");
            if (noVerify)
                builder.Append("--no-verify ");

            var refspec = $"{sourceSha}:refs/heads/{remoteBranchName}";
            builder.Append("-- ")
                .Append(CommandArgumentHelper.Quote(pushUrl))
                .Append(' ')
                .Append(CommandArgumentHelper.Quote(refspec));

            Args = builder.ToString();
        }
    }
}
