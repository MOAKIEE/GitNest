using System.Text;

namespace SourceGit.Commands
{
    public class Push : Command
    {
        public Push(string repo, Models.Branch local, Models.Remote remote, Models.Branch remoteBranch, bool withTags, bool checkSubmodules, bool track, bool force, bool noVerify)
        {
            WorkingDirectory = repo;
            Context = repo;
            SSHKey = remote.PrivateSSHKey;

            var builder = new StringBuilder(1024);
            builder.Append("push --progress --verbose ");
            if (withTags)
                builder.Append("--tags ");
            if (checkSubmodules)
                builder.Append("--recurse-submodules=check ");
            if (track)
                builder.Append("-u ");
            if (force)
                builder.Append("--force-with-lease ");
            if (noVerify)
                builder.Append("--no-verify ");

            builder.Append(remote.Name).Append(" refs/heads/").Append(local.Name).Append(":refs/heads/").Append(remoteBranch.Name);
            Args = builder.ToString();
        }

        public Push(
            string repo,
            string localBranchName,
            Models.Remote remote,
            string remoteBranchName,
            bool checkSubmodules,
            bool track,
            bool noVerify)
        {
            WorkingDirectory = repo;
            Context = repo;
            SSHKey = remote?.PrivateSSHKey ?? string.Empty;

            var builder = new StringBuilder(1024);
            builder.Append("push --progress --verbose ");
            if (checkSubmodules)
                builder.Append("--recurse-submodules=check ");
            if (track)
                builder.Append("-u ");
            if (noVerify)
                builder.Append("--no-verify ");

            var refspec = $"refs/heads/{localBranchName}:refs/heads/{remoteBranchName}";
            builder.Append("-- ")
                .Append(CommandArgumentHelper.Quote(remote?.Name ?? string.Empty))
                .Append(' ')
                .Append(CommandArgumentHelper.Quote(refspec));

            Args = builder.ToString();
        }

        public Push(string repo, Models.Commit revision, Models.Remote remote, Models.Branch remoteBranch, bool force)
            : this(repo, revision, remote, remoteBranch, force, expectedLeaseTipSha: null)
        {
        }

        public Push(string repo, Models.Commit revision, Models.Remote remote, Models.Branch remoteBranch, bool force, string expectedLeaseTipSha)
        {
            WorkingDirectory = repo;
            Context = repo;
            SSHKey = remote.PrivateSSHKey;

            var validRemote = CommandArgumentHelper.ValidateNonOptionToken(remote.Name, nameof(remote));
            var validRemoteBranch = CommandArgumentHelper.ValidateNonOptionToken(remoteBranch.Name, nameof(remoteBranch));
            var validCommit = CommandArgumentHelper.ValidateNonOptionToken(revision.SHA, nameof(revision));

            var builder = new StringBuilder(1024);
            builder.Append("push --progress --verbose ");
            if (force)
            {
                if (!string.IsNullOrWhiteSpace(expectedLeaseTipSha))
                {
                    var validExpected = CommandArgumentHelper.ValidateNonOptionToken(expectedLeaseTipSha.Trim(), nameof(expectedLeaseTipSha));
                    var leaseArg = $"--force-with-lease=refs/heads/{validRemoteBranch}:{validExpected}";
                    builder.Append(CommandArgumentHelper.Quote(leaseArg)).Append(' ');
                }
                else
                {
                    builder.Append("--force-with-lease ");
                }
            }

            var refspec = $"{validCommit}:refs/heads/{validRemoteBranch}";
            builder.Append("-- ")
                .Append(CommandArgumentHelper.Quote(validRemote))
                .Append(' ')
                .Append(CommandArgumentHelper.Quote(refspec));
            Args = builder.ToString();
        }

        public Push(string repo, Models.Remote remote, string refname, bool isDelete)
        {
            WorkingDirectory = repo;
            Context = repo;
            SSHKey = remote.PrivateSSHKey;

            var builder = new StringBuilder(512);
            builder.Append("push ");
            if (isDelete)
                builder.Append("--delete ");
            builder.Append(remote.Name).Append(' ').Append(refname);

            Args = builder.ToString();
        }
    }
}
