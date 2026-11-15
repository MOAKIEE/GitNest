using System.Text;
using System.Threading.Tasks;

namespace SourceGit.Commands
{
    public class LFS : Command
    {
        public LFS(string repo)
        {
            WorkingDirectory = repo;
            Context = repo;
        }

        public async Task<bool> InstallAsync()
        {
            Args = "lfs install --local";
            return await ExecAsync().ConfigureAwait(false);
        }

        public async Task<bool> TrackAsync(string pattern, bool isFilenameMode)
        {
            var builder = new StringBuilder();
            builder.Append("lfs track ");
            builder.Append(isFilenameMode ? "--filename " : string.Empty);
            builder.Append(pattern.Quoted());

            Args = builder.ToString();
            return await ExecAsync().ConfigureAwait(false);
        }

        public async Task FetchAsync(string remote)
        {
            Args = $"lfs fetch {remote}";
            await ExecAsync().ConfigureAwait(false);
        }

        public async Task PullAsync(string remote)
        {
            Args = $"lfs pull {remote}";
            await ExecAsync().ConfigureAwait(false);
        }

        public async Task PushAsync(string remote)
        {
            Args = $"lfs push {remote}";
            await ExecAsync().ConfigureAwait(false);
        }
    }
}
