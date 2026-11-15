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

        public async Task<bool> UpdateAsync(List<string> modules, bool init, bool recursive, bool useRemote)
        {
            Args = BuildUpdateArgs(modules, init, recursive, useRemote);
            return await ExecAsync().ConfigureAwait(false);
        }

        public async Task<(bool Success, string ErrorMessage)> UpdateDetailedAsync(List<string> modules, bool init, bool recursive, bool useRemote)
        {
            Args = BuildUpdateArgs(modules, init, recursive, useRemote);
            RaiseError = false;
            return await ExecDetailedAsync().ConfigureAwait(false);
        }

        private static string BuildUpdateArgs(List<string> modules, bool init, bool recursive, bool useRemote)
        {
            var builder = new StringBuilder();
            builder.Append("submodule update");

            if (init)
                builder.Append(" --init");
            if (recursive)
                builder.Append(" --recursive");
            if (useRemote)
                builder.Append(" --remote");
            if (modules is { Count: > 0 })
            {
                builder.Append(" --");
                foreach (var module in modules)
                    builder.Append(' ').Append(module.Quoted());
            }

            return builder.ToString();
        }
    }
}
