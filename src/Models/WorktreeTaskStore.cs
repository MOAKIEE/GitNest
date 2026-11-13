using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace SourceGit.Models
{
    public class WorktreeTask
    {
        public string CommonDirectory { get; set; } = string.Empty;
        public string FullPath { get; set; } = string.Empty;
        public string Alias { get; set; } = string.Empty;
        public bool IsCompleted { get; set; }
        public string ComparisonTarget { get; set; } = string.Empty;
    }

    public class WorktreeTaskStore
    {
        public static WorktreeTaskStore Instance => _instance ??= new(Path.Combine(Native.OS.BasicDirectories.ConfigDir, "worktree-tasks.json"));

        public WorktreeTaskStore(string file)
        {
            _file = file;
            try
            {
                if (File.Exists(file))
                    _tasks = JsonSerializer.Deserialize(File.ReadAllText(file), JsonCodeGen.Default.ListWorktreeTask) ?? [];
                foreach (var task in _tasks)
                {
                    if (task == null || !Path.IsPathFullyQualified(task.CommonDirectory) || !Path.IsPathFullyQualified(task.FullPath))
                        throw new InvalidDataException("Task settings contain an invalid repository or worktree path.");
                }
            }
            catch (Exception ex)
            {
                _readError = ex.Message;
                _tasks = [];
            }
        }

        public WorktreeTask Get(string commonDirectory, string path)
        {
            lock (_gate)
            {
                var task = _tasks.Find(t => PathsEqual(t.CommonDirectory, commonDirectory) && PathsEqual(t.FullPath, path));
                return new WorktreeTask()
                {
                    CommonDirectory = NormalizePath(commonDirectory),
                    FullPath = NormalizePath(path),
                    Alias = task?.Alias ?? string.Empty,
                    IsCompleted = task?.IsCompleted ?? false,
                    ComparisonTarget = task?.ComparisonTarget ?? string.Empty,
                };
            }
        }

        public void Save(WorktreeTask task)
        {
            lock (_gate)
            {
                if (_readError != null)
                    throw new InvalidDataException(_readError);

                var tasks = new List<WorktreeTask>(_tasks);
                tasks.RemoveAll(t => PathsEqual(t.CommonDirectory, task.CommonDirectory) && PathsEqual(t.FullPath, task.FullPath));
                tasks.Add(new WorktreeTask()
                {
                    CommonDirectory = NormalizePath(task.CommonDirectory),
                    FullPath = NormalizePath(task.FullPath),
                    Alias = task.Alias.Trim(),
                    IsCompleted = task.IsCompleted,
                    ComparisonTarget = task.ComparisonTarget,
                });
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_file)));
                var temporaryFile = _file + ".tmp";
                File.WriteAllText(temporaryFile, JsonSerializer.Serialize(tasks, JsonCodeGen.Default.ListWorktreeTask));
                File.Move(temporaryFile, _file, true);
                _tasks = tasks;
            }
        }

        public static bool PathsEqual(string left, string right) => string.Equals(NormalizePath(left), NormalizePath(right),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

        private static string NormalizePath(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)).Replace('\\', '/');

        private static WorktreeTaskStore _instance;
        private readonly string _file;
        private readonly string _readError;
        private readonly object _gate = new();
        private List<WorktreeTask> _tasks = [];
    }
}
