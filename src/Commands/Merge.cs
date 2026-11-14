using System;
using System.Collections.Generic;
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

        public Merge(string repo, List<string> targets, bool autoCommit, string strategy)
        {
            WorkingDirectory = repo;
            Context = repo;

            var builder = new StringBuilder();
            builder.Append("merge --progress ");
            if (!string.IsNullOrWhiteSpace(strategy))
            {
                var safeStrategy = CommandArgumentHelper.ValidateNonOptionToken(strategy, nameof(strategy));
                builder.Append("--strategy=").Append(CommandArgumentHelper.Quote(safeStrategy)).Append(' ');
            }
            if (!autoCommit)
                builder.Append("--no-commit ");

            if (targets != null)
            {
                foreach (var t in targets)
                {
                    var safeTarget = CommandArgumentHelper.ValidateNonOptionToken(t, nameof(targets));
                    builder.Append(CommandArgumentHelper.Quote(safeTarget));
                    builder.Append(' ');
                }
            }

            Args = builder.ToString().TrimEnd();
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
