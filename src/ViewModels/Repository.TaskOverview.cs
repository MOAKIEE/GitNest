using System;
using System.Threading;
using System.Threading.Tasks;

using Avalonia.Threading;

namespace SourceGit.ViewModels
{
    public partial class Repository
    {
        public string StatusSummary
        {
            get
            {
                if (_currentTaskStatus == null)
                    return App.Text("Worktree.Status.Pending");
                if (!_currentTaskStatus.IsReadable)
                    return Worktree.DescribeChanges(_currentTaskStatus);
                var branch = _currentTaskStatus.SyncState == Models.WorktreeSyncState.Detached ? "detached HEAD" : _currentTaskStatus.Branch;
                return App.Text("Repository.StatusSummary", branch, _currentTaskStatus.ChangedFiles) + " · " + Worktree.DescribeSync(_currentTaskStatus);
            }
        }

        public string StatusSummaryDetail => Worktree.DescribeChanges(_currentTaskStatus) + "\n" +
            (_currentTaskStatus?.LastFetchAt is { } time ? App.Text("Worktree.Status.FetchTime", time.ToString("yyyy-MM-dd HH:mm:ss")) : App.Text("Worktree.Status.FetchUnknown"));

        public void ShowChanges() => SelectedViewIndex = 1;

        public void RefreshTaskOverview() => RefreshWorktreeList(true);

        private void RefreshWorktreeList(bool refreshAllStatuses)
        {
            var generation = Interlocked.Increment(ref _worktreeListGeneration);
            _ = Task.Run(async () =>
            {
                var worktrees = await new Commands.Worktree(FullPath).ReadAllAsync().ConfigureAwait(false);
                var nodes = Worktree.Build(FullPath, worktrees);
                Dispatcher.UIThread.Post(() =>
                {
                    if (_isTaskOverviewClosed || generation != Interlocked.Read(ref _worktreeListGeneration))
                        return;

                    foreach (var node in nodes)
                    {
                        node.LoadTask(_gitCommonDir);
                        var previous = _worktrees.Find(t => Models.WorktreeTaskStore.PathsEqual(t.FullPath, node.FullPath));
                        if (previous?.Status != null)
                            node.SetStatus(previous.Status);
                    }
                    Worktrees = nodes;
                    foreach (var node in nodes)
                    {
                        if (refreshAllStatuses || node.IsCurrent || node.Status == null)
                            _ = RefreshTaskStatusAsync(node);
                    }
                });
            });
        }

        private void RefreshCurrentTaskStatus()
        {
            Dispatcher.UIThread.Post(() =>
            {
                var node = _worktrees.Find(t => t.IsCurrent);
                if (!_isTaskOverviewClosed && node != null)
                    _ = RefreshTaskStatusAsync(node);
            });
        }

        private async Task RefreshTaskStatusAsync(Worktree node)
        {
            var generation = Interlocked.Increment(ref node.ReadGeneration);
            await _taskStatusSlots.WaitAsync().ConfigureAwait(false);
            try
            {
                if (_isTaskOverviewClosed || generation != node.ReadGeneration || !_worktrees.Contains(node))
                    return;
                var status = node.Backend.IsBare
                    ? new Models.WorktreeStatus() { Error = "Worktree.Status.Bare" }
                    : await new Commands.QueryWorktreeStatus(node.FullPath).GetResultAsync(_gitCommonDir).ConfigureAwait(false);
                Dispatcher.UIThread.Post(() =>
                {
                    if (_isTaskOverviewClosed || generation != node.ReadGeneration || !_worktrees.Contains(node))
                        return;
                    node.SetStatus(status);
                    if (node.IsCurrent)
                    {
                        _currentTaskStatus = status;
                        OnPropertyChanged(nameof(StatusSummary));
                        OnPropertyChanged(nameof(StatusSummaryDetail));
                    }
                });
            }
            catch (Exception)
            {
                Dispatcher.UIThread.Post(() =>
                {
                    if (!_isTaskOverviewClosed && generation == node.ReadGeneration && _worktrees.Contains(node))
                    {
                        var failed = new Models.WorktreeStatus() { Error = "Worktree.Status.ReadFailed" };
                        node.SetStatus(failed);
                        if (node.IsCurrent)
                        {
                            _currentTaskStatus = failed;
                            OnPropertyChanged(nameof(StatusSummary));
                            OnPropertyChanged(nameof(StatusSummaryDetail));
                        }
                    }
                });
            }
            finally
            {
                _taskStatusSlots.Release();
            }
        }

        private readonly SemaphoreSlim _taskStatusSlots = new(2);
        private Models.WorktreeStatus _currentTaskStatus;
        private long _worktreeListGeneration;
        private bool _isTaskOverviewClosed;
    }
}
