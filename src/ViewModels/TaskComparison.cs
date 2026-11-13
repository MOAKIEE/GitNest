using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;

namespace SourceGit.ViewModels
{
    public class TaskComparison : ObservableObject, IDisposable
    {
        public string FullPath { get; }
        public List<string> Modes { get; } = [App.Text("TaskCompare.MergeBase"), App.Text("TaskCompare.TwoTips")];
        public List<string> Targets { get => _targets; private set => SetProperty(ref _targets, value); }
        public string SelectedTarget
        {
            get => _selectedTarget;
            set
            {
                if (_updatingTargets || _disposed || value == null) return;
                if (SetProperty(ref _selectedTarget, value))
                {
                    SaveTarget();
                    _ = RefreshAsync();
                }
            }
        }
        public int SelectedMode
        {
            get => _selectedMode;
            set
            {
                if (value is < 0 or > 1) return;
                if (SetProperty(ref _selectedMode, value)) _ = RefreshAsync();
            }
        }
        public Models.TaskComparison Snapshot { get => _snapshot; private set => SetProperty(ref _snapshot, value); }
        public bool IsLoading { get => _isLoading; private set => SetProperty(ref _isLoading, value); }
        public bool IsStale { get => _isStale; private set => SetProperty(ref _isStale, value); }
        public string ErrorText { get => _errorText; private set => SetProperty(ref _errorText, value); }
        public string SaveError { get => _saveError; private set => SetProperty(ref _saveError, value); }
        public bool CanDisplay => Snapshot?.IsReadable == true && !IsLoading;
        public string SourceDescription => Snapshot?.SourceHead is { } head ? Snapshot.SourceRef + "\n" + head : string.Empty;
        public string TargetDescription => Snapshot?.TargetHead is { } head ? Snapshot.TargetRef + "\n" + head : string.Empty;
        public string TargetCacheDescription => Snapshot?.TargetRef?.StartsWith("refs/remotes/", StringComparison.Ordinal) == true ? App.Text("TaskCompare.CachedTarget") : string.Empty;
        public string BaseDescription => Snapshot?.BaseHead is { } head ? App.Text(SelectedMode == 0 ? "TaskCompare.Base" : "TaskCompare.TipBase", head) : string.Empty;
        public string Summary => CanDisplay ? App.Text("TaskCompare.Summary", Snapshot.Changes.Count, Snapshot.AddedLines, Snapshot.DeletedLines, Snapshot.BinaryFiles) : string.Empty;
        public string CommitSummary => CanDisplay ? App.Text("TaskCompare.Commits", Snapshot.Commits.Count, Snapshot.CommitCount) : string.Empty;
        public string WorkingCopyDescription { get => _workingCopyDescription; private set => SetProperty(ref _workingCopyDescription, value); }
        public List<Models.Change> Changes => CanDisplay ? Snapshot.Changes : [];
        public List<Models.TaskComparisonCommit> Commits => CanDisplay ? Snapshot.Commits : [];
        public ChangeSelection ChangeSelection
        {
            get => _changeSelection;
            set
            {
                if (SetProperty(ref _changeSelection, value))
                    DiffContext = CanDisplay && value is { Count: 1, HasFolder: false }
                        ? new DiffContext(FullPath, new Models.DiffOption(Snapshot.BaseHead, Snapshot.SourceHead, value.Changes[0]) { DisableTextConversion = true }, _diffContext)
                        : null;
            }
        }
        public DiffContext DiffContext { get => _diffContext; private set => SetProperty(ref _diffContext, value); }

        public TaskComparison(Repository repo, string path, string commonDirectory)
        {
            _repo = repo;
            FullPath = path;
            _commonDirectory = commonDirectory;
            _query = new Commands.QueryTaskComparison(path, commonDirectory);
            _selectedTarget = Models.WorktreeTaskStore.Instance.Get(commonDirectory, path).ComparisonTarget;
            _timer = new DispatcherTimer(TimeSpan.FromSeconds(2), DispatcherPriority.Background, async (_, _) =>
            {
                if (_repo.IsTaskComparisonVisible && ReferenceEquals(App.GetLauncher()?.ActivePage.Data, _repo))
                    await ValidateSnapshotAsync();
            });
            _timer.Start();
        }

        public async Task RefreshAsync()
        {
            if (_disposed) return;
            var generation = ++_generation;
            _readCancellation?.Cancel();
            _readCancellation?.Dispose();
            _readCancellation = new CancellationTokenSource();
            var token = _readCancellation.Token;
            var target = SelectedTarget;
            var mode = (Models.TaskComparisonMode)SelectedMode;
            Snapshot = null;
            IsStale = false;
            IsLoading = true;
            ErrorText = string.Empty;
            ChangeSelection = new(null);
            WorkingCopyDescription = string.Empty;
            NotifySnapshot();
            try
            {
                var targets = await _query.ReadTargetsAsync(token).ConfigureAwait(false);
                if (!string.IsNullOrEmpty(target) && !targets.Contains(target)) targets.Insert(0, target);
                var result = string.IsNullOrEmpty(target) ? null : await _query.ReadAsync(target, mode, token).ConfigureAwait(false);
                var workingCopy = result?.WorkingCopy ?? await new Commands.QueryWorktreeStatus(FullPath) { CancellationToken = token }.GetResultAsync(_commonDirectory).ConfigureAwait(false);
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    if (_disposed || generation != _generation) return;
                    _updatingTargets = true;
                    Targets = targets;
                    // Replacing ItemsSource can clear ComboBox selection. Restore the explicit target.
                    _selectedTarget = target;
                    OnPropertyChanged(nameof(SelectedTarget));
                    _updatingTargets = false;
                    Snapshot = result;
                    IsLoading = false;
                    IsStale = result?.IsStale == true;
                    ErrorText = result == null ? App.Text("TaskCompare.ChooseTarget") : result.Error.Length > 0 ? App.Text(result.Error) : string.Empty;
                    WorkingCopyDescription = Worktree.DescribeChanges(workingCopy);
                    NotifySnapshot();
                    if (CanDisplay && result.Changes.Count > 0) ChangeSelection = new(result.Changes.GetRange(0, 1));
                });
            }
            catch (OperationCanceledException) { }
            catch (Exception)
            {
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    if (_disposed || generation != _generation) return;
                    IsLoading = false;
                    ErrorText = App.Text("TaskCompare.ReadFailed");
                    NotifySnapshot();
                });
            }
        }

        public async Task ValidateSnapshotAsync()
        {
            var snapshot = Snapshot;
            if (_disposed || IsLoading || _validating) return;
            _validating = true;
            var token = _readCancellation.Token;
            try
            {
                var current = snapshot?.SourceHead == null || snapshot.TargetHead == null || await _query.IsCurrentAsync(snapshot, token).ConfigureAwait(false);
                var status = await new Commands.QueryWorktreeStatus(FullPath) { CancellationToken = token }.GetResultAsync(_commonDirectory).ConfigureAwait(false);
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    if (_disposed || snapshot != Snapshot) return;
                    if (snapshot != null)
                    {
                        snapshot.IsStale |= !current;
                        IsStale = snapshot.IsStale;
                    }
                    WorkingCopyDescription = Worktree.DescribeChanges(status);
                });
            }
            catch (OperationCanceledException) { }
            catch (Exception)
            {
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    if (!_disposed && snapshot != null && snapshot == Snapshot)
                    {
                        snapshot.IsStale = true;
                        IsStale = true;
                    }
                });
            }
            finally { _validating = false; }
        }

        public void ShowUncommittedChanges()
        {
            if (Models.WorktreeTaskStore.PathsEqual(FullPath, _repo.FullPath)) _repo.ShowChanges();
            else
            {
                var worktree = _repo.Worktrees.Find(t => Models.WorktreeTaskStore.PathsEqual(t.FullPath, FullPath));
                if (worktree == null) return;
                _repo.OpenWorktree(worktree);
                if (App.GetLauncher()?.ActivePage.Data is Repository opened && Models.WorktreeTaskStore.PathsEqual(opened.FullPath, FullPath))
                    opened.ShowChanges();
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            ++_generation;
            _timer.Stop();
            _readCancellation?.Cancel();
            _readCancellation?.Dispose();
        }

        private void SaveTarget()
        {
            try
            {
                var task = Models.WorktreeTaskStore.Instance.Get(_commonDirectory, FullPath);
                task.ComparisonTarget = _selectedTarget ?? string.Empty;
                Models.WorktreeTaskStore.Instance.Save(task);
                SaveError = string.Empty;
                _repo.RefreshWorktrees();
            }
            catch (Exception ex) { SaveError = App.Text("Worktree.Task.SaveFailed", ex.Message); }
        }

        private void NotifySnapshot()
        {
            foreach (var name in new[] { nameof(CanDisplay), nameof(SourceDescription), nameof(TargetDescription), nameof(TargetCacheDescription), nameof(BaseDescription), nameof(Summary), nameof(CommitSummary), nameof(Changes), nameof(Commits) })
                OnPropertyChanged(name);
        }

        private readonly Repository _repo;
        private readonly string _commonDirectory;
        private readonly Commands.QueryTaskComparison _query;
        private readonly DispatcherTimer _timer;
        private CancellationTokenSource _readCancellation;
        private long _generation;
        private bool _disposed, _validating, _isStale, _updatingTargets;
        private bool _isLoading = true;
        private int _selectedMode;
        private string _selectedTarget, _errorText = string.Empty, _saveError = string.Empty, _workingCopyDescription = string.Empty;
        private List<string> _targets = [];
        private Models.TaskComparison _snapshot;
        private ChangeSelection _changeSelection = new(null);
        private DiffContext _diffContext;
    }
}
