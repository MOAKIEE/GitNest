using System.Diagnostics;
using System.IO;

namespace SourceGit.Models
{
    public static class WorktreeToolLauncher
    {
        public static ProcessStartInfo CreateEditorStartInfo(string executable, string worktreePath)
        {
            var fullPath = Path.GetFullPath(worktreePath);
            if (!Directory.Exists(fullPath))
                throw new DirectoryNotFoundException(fullPath);
            var start = new ProcessStartInfo(executable)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = fullPath,
            };
            start.ArgumentList.Add(fullPath);
            return start;
        }
    }
}
