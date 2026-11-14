namespace SourceGit.Commands
{
    public class Add : Command
    {
        public Add(string repo, string pathspecFromFile)
            : this(repo, pathspecFromFile, isNulDelimited: false, force: true)
        {
        }

        public Add(string repo, string pathspecFromFile, bool isNulDelimited, bool force = false)
        {
            WorkingDirectory = repo;
            Context = repo;
            var literalFlag = isNulDelimited ? "--literal-pathspecs " : string.Empty;
            var forceFlag = force ? "--force " : string.Empty;
            var nulFlag = isNulDelimited ? "--pathspec-file-nul " : string.Empty;
            Args = $"{literalFlag}add {forceFlag}--verbose {nulFlag}--pathspec-from-file={CommandArgumentHelper.Quote(pathspecFromFile)}";
        }
    }
}
