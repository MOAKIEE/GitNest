using System;
using System.Collections.Generic;

namespace SourceGit.Models
{
    public enum TaskComparisonMode
    {
        MergeBase,
        TwoTips,
    }

    public class TaskComparisonCommit
    {
        public string SHA { get; set; }
        public string Subject { get; set; }
    }

    public class TaskComparison
    {
        public string FullPath { get; set; }
        public string CommonDirectory { get; set; }
        public string SourceRef { get; set; }
        public string SourceHead { get; set; }
        public string TargetRef { get; set; }
        public string TargetHead { get; set; }
        public string BaseHead { get; set; }
        public TaskComparisonMode Mode { get; set; }
        public string Error { get; set; } = string.Empty;
        public bool IsStale { get; set; }
        public bool IsReadable => Error.Length == 0 && BaseHead != null;
        public List<Change> Changes { get; set; } = [];
        public List<TaskComparisonCommit> Commits { get; set; } = [];
        public int CommitCount { get; set; }
        public long AddedLines { get; set; }
        public long DeletedLines { get; set; }
        public int BinaryFiles { get; set; }
        public DateTime ReadAt { get; set; } = DateTime.Now;
        public WorktreeStatus WorkingCopy { get; set; }
    }
}
