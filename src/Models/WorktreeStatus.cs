using System;

namespace SourceGit.Models
{
    public enum WorktreeSyncState
    {
        Unknown,
        NoUpstream,
        UpstreamGone,
        NotFetched,
        Cached,
        Detached,
        Unborn,
    }

    public class WorktreeStatus
    {
        public bool IsReadable { get; set; }
        public string Error { get; set; } = string.Empty;
        public string Branch { get; set; } = string.Empty;
        public string Head { get; set; } = string.Empty;
        public string Upstream { get; set; } = string.Empty;
        public int ChangedFiles { get; set; }
        public int Staged { get; set; }
        public int Unstaged { get; set; }
        public int Untracked { get; set; }
        public int Conflicted { get; set; }
        public int? Ahead { get; set; }
        public int? Behind { get; set; }
        public WorktreeSyncState SyncState { get; set; }
        public DateTime RefreshedAt { get; set; } = DateTime.Now;
        public DateTime? LastFetchAt { get; set; }
    }
}
