using System;
using System.Text;

namespace SourceGit.Commands
{
    public class Merge : Command
    {
        public Merge(string repo, string source, string mode, bool edit)
        {
            WorkingDirectory = repo;
            Context = repo;

            var safeSource = CommandArgumentHelper.ValidateNonOptionToken(source, nameof(source));
            var safeMode = ValidateMergeModeArg(mode);

            var builder = new StringBuilder();
            builder.Append("merge --progress ");
            builder.Append(edit ? "--edit " : "--no-edit ");
            if (!string.IsNullOrEmpty(safeMode))
                builder.Append(safeMode).Append(' ');
            builder.Append(CommandArgumentHelper.Quote(safeSource));

            Args = builder.ToString();
        }

        private static string ValidateMergeModeArg(string mode)
        {
            if (string.IsNullOrWhiteSpace(mode))
                return string.Empty;

            var trimmed = mode.Trim();
            return trimmed switch
            {
                "--ff-only" or "--no-ff" or "--squash" or "--no-ff --no-commit" => trimmed,
                _ => throw new ArgumentException("不支持的合并模式参数。", nameof(mode)),
            };
        }
    }
}
