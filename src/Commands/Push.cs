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
            builder.Append("--progress --verbose --no-follow-tags ");
            if (checkSubmodules)
                builder.Append("--recurse-submodules=check ");
            if (noVerify)
                builder.Append("--no-verify ");

            var refspec = $"{sourceSha}:refs/heads/{remoteBranchName}";
            Args = RemotePushTarget.BuildPushArguments(pushUrl, builder.ToString(), refspec);
        }
    }
}
