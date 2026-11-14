using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace SourceGit.Commands
{
    public class Commit : Command
    {
        public Commit(string repo, string message, bool signOff, bool noVerify, bool amend, bool resetAuthor)
            : this(repo, message, signOff, noVerify, amend, resetAuthor, allowEmpty: true)
        {
        }

        public Commit(string repo, string message, bool signOff, bool noVerify, bool amend, bool resetAuthor, bool allowEmpty)
        {
            _tmpFile = Path.GetTempFileName();
            _message = message;

            WorkingDirectory = repo;
            Context = repo;

            var builder = new StringBuilder();
            builder.Append("commit ");
            if (allowEmpty)
                builder.Append("--allow-empty ");
            builder.Append("--file=");
            builder.Append(CommandArgumentHelper.Quote(_tmpFile));
            builder.Append(' ');

            if (signOff)
                builder.Append("--signoff ");

            if (noVerify)
                builder.Append("--no-verify ");

            if (amend)
            {
                builder.Append("--amend ");
                if (resetAuthor)
                    builder.Append("--reset-author ");
                builder.Append("--no-edit");
            }

            Args = builder.ToString();
        }

        public async Task<bool> RunAsync()
        {
            try
            {
                await File.WriteAllTextAsync(_tmpFile, _message ?? string.Empty, new System.Text.UTF8Encoding(false)).ConfigureAwait(false);
                return await ExecAsync().ConfigureAwait(false);
            }
            catch (System.Exception e)
            {
                if (RaiseError)
                    Models.Notification.Send(Context, e.Message, true);
                return false;
            }
            finally
            {
                CommandArgumentHelper.TryDeleteTempFile(_tmpFile);
            }
        }

        private readonly string _tmpFile;
        private readonly string _message;
    }
}
