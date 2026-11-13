using System;
using System.Collections.Generic;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;

namespace SourceGit.ViewModels
{
    public class Worktree : ObservableObject
    {
        public Models.Worktree Backend { get; private set; }
        public bool IsMain { get; private set; }
        public bool IsCurrent { get; private set; }
        public bool IsLast { get; private set; }
        public string DisplayPath { get; private set; }
        public string Name => string.IsNullOrWhiteSpace(Task.Alias) ? _name : Task.Alias;
        public string Branch { get; private set; }
        public Models.WorktreeTask Task { get; private set; } = new();
        public Models.WorktreeStatus Status { get; private set; }
        public string TaskState => App.Text(Task.IsCompleted ? "Worktree.Task.Completed" : "Worktree.Task.Active");
        public string BranchDescription => $"{Branch} · {TaskState}";
        public string ChangesDescription => DescribeChanges(Status);
        public string SyncDescription => DescribeSync(Status);
        public string RefreshDescription => Status == null ? App.Text("Worktree.Status.Pending") :
            App.Text("Worktree.Status.Refreshed", Status.RefreshedAt.ToString("HH:mm:ss"));
        public string FetchDescription => Status?.LastFetchAt is { } time ?
            App.Text("Worktree.Status.FetchTime", time.ToString("yyyy-MM-dd HH:mm:ss")) : App.Text("Worktree.Status.FetchUnknown");
        public string AvailabilityDescription => Backend.IsBare ? App.Text("Worktree.Status.Bare") :
            !Directory.Exists(FullPath) ? App.Text("Worktree.Status.Missing") :
            IsLocked ? App.Text("Worktree.Status.Locked", Backend.LockReason) :
            IsMain ? App.Text("Worktree.Status.Main") : App.Text("Worktree.Status.Linked");
        internal int ReadGeneration;

        public bool IsLocked
        {
            get => _isLocked;
            set => SetProperty(ref _isLocked, value);
        }

        public string FullPath => Backend.FullPath;
        public string Head => Status is { IsReadable: true } ? Status.Head : Backend.Head;

        public Worktree(DirectoryInfo repo, Models.Worktree wt, bool isMain, bool isLast)
        {
            Backend = wt;
            IsMain = isMain;
            IsCurrent = IsCurrentWorktree(repo, wt);
            IsLast = isLast;
            DisplayPath = IsCurrent ? string.Empty : Path.GetRelativePath(repo.FullName, wt.FullPath);
            _name = GenerateName();
            Branch = GenerateBranchName();
            IsLocked = wt.IsLocked;
        }

        public static List<Worktree> Build(string repo, List<Models.Worktree> worktrees)
        {
            if (worktrees is not { Count: > 0 })
                return [];

            var repoDir = new DirectoryInfo(repo);
            var nodes = new List<Worktree>();
            nodes.Add(new(repoDir, worktrees[0], true, worktrees.Count == 1));
            for (int i = 1; i < worktrees.Count; i++)
                nodes.Add(new(repoDir, worktrees[i], false, i == worktrees.Count - 1));

            return nodes;
        }

        public void LoadTask(string commonDirectory)
        {
            Task = Models.WorktreeTaskStore.Instance.Get(commonDirectory, FullPath);
            NotifyTaskChanged();
        }

        public void NotifyTaskChanged()
        {
            OnPropertyChanged(nameof(Name));
            OnPropertyChanged(nameof(TaskState));
            OnPropertyChanged(nameof(BranchDescription));
        }

        public void SetStatus(Models.WorktreeStatus status)
        {
            Status = status;
            if (status is { IsReadable: true })
                Branch = status.SyncState == Models.WorktreeSyncState.Detached ? "detached HEAD" : status.Branch;
            OnPropertyChanged(nameof(Branch));
            OnPropertyChanged(nameof(Head));
            OnPropertyChanged(nameof(BranchDescription));
            OnPropertyChanged(nameof(ChangesDescription));
            OnPropertyChanged(nameof(SyncDescription));
            OnPropertyChanged(nameof(RefreshDescription));
            OnPropertyChanged(nameof(FetchDescription));
            OnPropertyChanged(nameof(AvailabilityDescription));
        }

        public static string DescribeChanges(Models.WorktreeStatus status)
        {
            if (status == null)
                return App.Text("Worktree.Status.Pending");
            if (!status.IsReadable)
                return App.Text(status.Error);
            var text = App.Text("Worktree.Status.Changes", status.Staged, status.Unstaged, status.Untracked);
            return status.Conflicted > 0 ? text + " · " + App.Text("Worktree.Status.Conflicts", status.Conflicted) : text;
        }

        public static string DescribeSync(Models.WorktreeStatus status)
        {
            if (status is not { IsReadable: true })
                return App.Text("Worktree.Status.SyncUnknown");
            return status.SyncState switch
            {
                Models.WorktreeSyncState.Cached => App.Text("Worktree.Status.Cached", status.Upstream, status.Ahead, status.Behind),
                Models.WorktreeSyncState.NoUpstream => App.Text("Worktree.Status.NoUpstream"),
                Models.WorktreeSyncState.UpstreamGone => App.Text("Worktree.Status.UpstreamGone", status.Upstream),
                Models.WorktreeSyncState.NotFetched => App.Text("Worktree.Status.NotFetched", status.Upstream),
                Models.WorktreeSyncState.Detached => App.Text("Worktree.Status.Detached"),
                Models.WorktreeSyncState.Unborn => App.Text("Worktree.Status.Unborn"),
                _ => App.Text("Worktree.Status.SyncUnknown"),
            };
        }

        public bool IsAttachedTo(Models.Branch branch)
        {
            if (string.IsNullOrEmpty(branch.WorktreePath))
                return false;

            var wtDir = new DirectoryInfo(Backend.FullPath);
            var test = new DirectoryInfo(branch.WorktreePath);
            return Models.WorktreeTaskStore.PathsEqual(test.FullName, wtDir.FullName);
        }

        private bool IsCurrentWorktree(DirectoryInfo repo, Models.Worktree wt)
        {
            var wtDir = new DirectoryInfo(wt.FullPath);
            return Models.WorktreeTaskStore.PathsEqual(wtDir.FullName, repo.FullName);
        }

        private string GenerateName()
        {
            if (IsMain)
                return Path.GetFileName(Backend.FullPath);

            if (Backend.IsDetached)
                return $"detached HEAD · {Backend.Head.AsSpan(0, Math.Min(10, Backend.Head.Length))}";

            var b = Backend.Branch;

            if (b.StartsWith("refs/heads/", StringComparison.Ordinal))
                return b.Substring(11);

            if (b.StartsWith("refs/remotes/", StringComparison.Ordinal))
                return b.Substring(13);

            return b;
        }

        private string GenerateBranchName()
        {
            if (Backend.IsBare)
                return "-- (default)";

            if (Backend.IsDetached)
                return "-- (detached)";

            if (string.IsNullOrEmpty(Backend.Branch))
                return "-- (unknown)";

            var b = Backend.Branch;

            if (b.StartsWith("refs/heads/", StringComparison.Ordinal))
                return b.Substring(11);

            if (b.StartsWith("refs/remotes/", StringComparison.Ordinal))
                return b.Substring(13);

            return b;
        }

        private bool _isLocked = false;
        private readonly string _name;
    }
}
