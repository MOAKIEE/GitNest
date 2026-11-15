using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;

namespace SourceGit.Commands
{
    public class Submodule : Command
    {
        public Submodule(string repo)
        {
            WorkingDirectory = repo;
            Context = repo;
        }

        public async Task<bool> AddAsync(string url, string relativePath, bool recursive)
        {
            Args = $"-c protocol.file.allow=always submodule add {url.Quoted()} {relativePath.Quoted()}";

            var succ = await ExecAsync().ConfigureAwait(false);
            if (!succ)
                return false;

            if (recursive)
                Args = $"submodule update --init --recursive -- {relativePath.Quoted()}";
            else
                Args = $"submodule update --init -- {relativePath.Quoted()}";
            return await ExecAsync().ConfigureAwait(false);
        }

        public async Task<bool> SetURLAsync(string path, string url)
        {
            Args = $"submodule set-url -- {path.Quoted()} {url.Quoted()}";
            return await ExecAsync().ConfigureAwait(false);
        }

        public async Task<bool> UpdateAsync(List<string> modules, bool init, bool recursive, bool useRemote)
        {
            var builder = new StringBuilder();
            builder.Append("submodule update");

            if (init)
                builder.Append(" --init");
            if (recursive)
                builder.Append(" --recursive");
            if (useRemote)
                builder.Append(" --remote");
            if (modules.Count > 0)
            {
                builder.Append(" --");
                foreach (var module in modules)
                    builder.Append(' ').Append(module.Quoted());
            }

            Args = builder.ToString();
            return await ExecAsync().ConfigureAwait(false);
        }

        public async Task<bool> DeleteAsync(string module)
        {
            Args = $"rm -rf {module.Quoted()}";
            return await ExecAsync().ConfigureAwait(false);
        }
    }
}
