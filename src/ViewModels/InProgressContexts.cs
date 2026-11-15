using System;
using System.IO;
using System.Threading.Tasks;

namespace SourceGit.ViewModels
{
    public abstract class InProgressContext
    {
        public string Name
        {
            get;
            protected set;
        }

        public async Task ContinueAsync(CommandLog log)
        {
            if (_continueCmd != null)
                await _continueCmd.Use(log).ExecAsync();
        }

        public async Task SkipAsync(CommandLog log)
        {
            if (_skipCmd != null)
                await _skipCmd.Use(log).ExecAsync();
        }

        public async Task AbortAsync(CommandLog log)
        {
            if (_abortCmd != null)
                await _abortCmd.Use(log).ExecAsync();

            OnAborted();
        }

        protected static string ReadTrimmedFileIfExists(string path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
                return string.Empty;

            try
            {
                return File.ReadAllText(path).Trim();
            }
            catch
            {
                return string.Empty;
            }
        }

        protected virtual void OnAborted()
        {
        }

        protected Commands.Command _continueCmd = null;
        protected Commands.Command _skipCmd = null;
        protected Commands.Command _abortCmd = null;
    }

    public class CherryPickInProgress : InProgressContext
    {
        public Models.Commit Head
        {
            get;
        }

        public string HeadName
        {
            get;
        }

        public CherryPickInProgress(Repository repo)
        {
            Name = "Cherry-Pick";

            // External Cherry-Pick workflows are read-only inside GitNest.
            var headSHA = ReadTrimmedFileIfExists(Path.Combine(repo.GitDir, "CHERRY_PICK_HEAD"));
            if (!string.IsNullOrEmpty(headSHA))
            {
                Head = new Commands.QuerySingleCommit(repo.FullPath, headSHA).GetResult() ?? new Models.Commit() { SHA = headSHA };
                HeadName = Head.GetFriendlyName();
            }
            else
            {
                Head = new Models.Commit() { SHA = string.Empty };
                HeadName = string.Empty;
            }
        }
    }

    public class RebaseInProgress : InProgressContext
    {
        public string HeadName
        {
            get;
        }

        public string BaseName
        {
            get;
        }

        public Models.Commit StoppedAt
        {
            get;
        }

        public Models.Commit Onto
        {
            get;
        }

        public RebaseInProgress(Repository repo)
        {
            Name = "Rebase";

            // Rebase initiation/continuation/abortion is outside GitNest; keep inspection strictly read-only.
            var mergeDir = Path.Combine(repo.GitDir, "rebase-merge");
            var applyDir = Path.Combine(repo.GitDir, "rebase-apply");
            var stateDir = Directory.Exists(mergeDir) ? mergeDir : applyDir;

            HeadName = ReadTrimmedFileIfExists(Path.Combine(stateDir, "head-name"));
            if (HeadName.StartsWith("refs/heads/"))
                HeadName = HeadName.Substring(11);
            else if (HeadName.StartsWith("refs/tags/"))
                HeadName = HeadName.Substring(10);

            var stoppedSHA = ReadTrimmedFileIfExists(Path.Combine(mergeDir, "stopped-sha"));
            if (string.IsNullOrEmpty(stoppedSHA))
                stoppedSHA = ReadTrimmedFileIfExists(Path.Combine(applyDir, "original-commit"));
            if (string.IsNullOrEmpty(stoppedSHA) && !string.IsNullOrEmpty(HeadName))
                stoppedSHA = new Commands.QueryRevisionByRefName(repo.FullPath, HeadName).GetResult();

            if (!string.IsNullOrEmpty(stoppedSHA))
                StoppedAt = new Commands.QuerySingleCommit(repo.FullPath, stoppedSHA).GetResult() ?? new Models.Commit() { SHA = stoppedSHA };

            var ontoSHA = ReadTrimmedFileIfExists(Path.Combine(stateDir, "onto"));
            if (!string.IsNullOrEmpty(ontoSHA))
            {
                Onto = new Commands.QuerySingleCommit(repo.FullPath, ontoSHA).GetResult() ?? new Models.Commit() { SHA = ontoSHA };
                BaseName = Onto.GetFriendlyName();
            }
            else
            {
                BaseName = string.Empty;
            }
        }
    }

    public class AmInProgress : InProgressContext
    {
        public string Progress
        {
            get;
        }

        public bool CanSkip => false;

        public AmInProgress(Repository repo)
        {
            Name = "Apply Patch";

            // External AM workflows are read-only inside GitNest; never modify or delete rebase-apply.
            var next = ReadIntFromFile(Path.Combine(repo.GitDir, "rebase-apply", "next"));
            var total = ReadIntFromFile(Path.Combine(repo.GitDir, "rebase-apply", "last"));
            Progress = total > 0 ? $"({Math.Max(0, next - 1)}/{total})" : string.Empty;
        }

        private int ReadIntFromFile(string file)
        {
            if (!File.Exists(file))
                return 0;

            var content = File.ReadAllText(file).Trim();
            if (int.TryParse(content, out var value))
                return value;

            return 0;
        }
    }

    public class BisectInProgress : InProgressContext
    {
        public BisectInProgress()
        {
            Name = "Bisect";
        }
    }

    public class RevertInProgress : InProgressContext
    {
        public Models.Commit Head
        {
            get;
        }

        public RevertInProgress(Repository repo)
        {
            Name = "Revert";

            _abortCmd = new Commands.Command
            {
                WorkingDirectory = repo.FullPath,
                Context = repo.FullPath,
                Args = "revert --abort",
            };

            var headSHA = ReadTrimmedFileIfExists(Path.Combine(repo.GitDir, "REVERT_HEAD"));
            if (!string.IsNullOrEmpty(headSHA))
                Head = new Commands.QuerySingleCommit(repo.FullPath, headSHA).GetResult() ?? new Models.Commit() { SHA = headSHA };
            else
                Head = new Models.Commit() { SHA = string.Empty };
        }
    }

    public class MergeInProgress : InProgressContext
    {
        public string Current
        {
            get;
        }

        public Models.Commit Source
        {
            get;
        }

        public string SourceName
        {
            get;
        }

        public MergeInProgress(Repository repo)
        {
            Name = "Merge";

            _continueCmd = new Commands.Command
            {
                WorkingDirectory = repo.FullPath,
                Context = repo.FullPath,
                Editor = Commands.Command.EditorType.None,
                Args = "-c core.commentChar=\"^\" -c core.commentString=\"±\" merge --continue",
            };

            _abortCmd = new Commands.Command
            {
                WorkingDirectory = repo.FullPath,
                Context = repo.FullPath,
                Args = "merge --abort",
            };

            Current = new Commands.QueryCurrentBranch(repo.FullPath).GetResult();

            var sourceSHA = ReadTrimmedFileIfExists(Path.Combine(repo.GitDir, "MERGE_HEAD"));
            if (!string.IsNullOrEmpty(sourceSHA))
            {
                Source = new Commands.QuerySingleCommit(repo.FullPath, sourceSHA).GetResult() ?? new Models.Commit() { SHA = sourceSHA };
                SourceName = Source.GetFriendlyName();
            }
            else
            {
                Source = new Models.Commit() { SHA = string.Empty };
                SourceName = string.Empty;
            }
        }
    }
}
