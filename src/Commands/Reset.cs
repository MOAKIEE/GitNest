namespace SourceGit.Commands
{
    public class Reset : Command
    {
        public Reset(string repo, string revision, string mode)
        {
            WorkingDirectory = repo;
            Context = repo;
            Args = $"reset {mode} {revision}";
        }

        public Reset(string repo, string pathspec)
            : this(repo, pathspec, isNulDelimited: false)
        {
        }

        public Reset(string repo, string pathspec, bool isNulDelimited)
        {
            WorkingDirectory = repo;
            Context = repo;
            var literalFlag = isNulDelimited ? "--literal-pathspecs " : string.Empty;
            var nulFlag = isNulDelimited ? "--pathspec-file-nul " : string.Empty;
            Args = $"{literalFlag}reset {nulFlag}--pathspec-from-file={CommandArgumentHelper.Quote(pathspec)}";
        }
    }
}
