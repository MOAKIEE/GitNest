using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Collections;
using Avalonia.Threading;

namespace SourceGit.ViewModels
{
    public sealed class DiscardTargetRow
    {
        public string Path { get; init; } = string.Empty;
        public string RoleLabel { get; init; } = string.Empty;
        public string Detail { get; init; } = string.Empty;
        public bool IsWarning { get; init; }
        public bool IsMuted { get; init; }
    }

    public class Discard : Popup
    {
        public Repository Repo => _repo;

        public bool IsAllChangesMode => _allChangesMode;

        public bool IsLoadingPreflight
        {
            get => _isLoadingPreflight;
            private set
            {
                if (SetProperty(ref _isLoadingPreflight, value))
                    NotifyPopupButtonsChanged();
            }
        }

        public bool HasPreflight
        {
            get => _hasPreflight;
            private set
            {
                if (SetProperty(ref _hasPreflight, value))
                    NotifyPopupButtonsChanged();
            }
        }

        public bool HasResultView
        {
            get => _hasResultView;
            private set
            {
                if (SetProperty(ref _hasResultView, value))
                    NotifyPopupButtonsChanged();
            }
        }

        public bool IsResultSuccess
        {
            get => _isResultSuccess;
            private set => SetProperty(ref _isResultSuccess, value);
        }

        public Models.DiscardBaselineKind SelectedBaseline
        {
            get => _selectedBaseline;
            set
            {
                if (SetProperty(ref _selectedBaseline, value))
                {
                    OnPropertyChanged(nameof(IsBaselineWorktreeToIndex));
                    OnPropertyChanged(nameof(IsBaselineToHead));
                    OnPropertyChanged(nameof(IsBaselineRecycleUntracked));
                    OnPropertyChanged(nameof(BaselineExplanation));
                    OnPropertyChanged(nameof(IsRecycleMode));
                    OnPropertyChanged(nameof(ShowUnbornHeadBlock));
                    NotifyPopupButtonsChanged();
                    _ = LoadPreflightAsync();
                }
            }
        }

        public bool IsBaselineWorktreeToIndex
        {
            get => _selectedBaseline == Models.DiscardBaselineKind.RestoreWorktreeToIndex;
            set
            {
                if (value)
                    SelectedBaseline = Models.DiscardBaselineKind.RestoreWorktreeToIndex;
            }
        }

        public bool IsBaselineToHead
        {
            get => _selectedBaseline == Models.DiscardBaselineKind.RestoreToHead;
            set
            {
                if (value)
                    SelectedBaseline = Models.DiscardBaselineKind.RestoreToHead;
            }
        }

        public bool IsBaselineRecycleUntracked
        {
            get => _selectedBaseline == Models.DiscardBaselineKind.MoveUntrackedToRecycleBin;
            set
            {
                if (value)
                    SelectedBaseline = Models.DiscardBaselineKind.MoveUntrackedToRecycleBin;
            }
        }

        public bool CanSelectWorktreeToIndex
        {
            get => _canSelectWorktreeToIndex;
            private set => SetProperty(ref _canSelectWorktreeToIndex, value);
        }

        public bool CanSelectToHead
        {
            get => _canSelectToHead;
            private set => SetProperty(ref _canSelectToHead, value);
        }

        public bool CanSelectRecycleUntracked
        {
            get => _canSelectRecycleUntracked;
            private set => SetProperty(ref _canSelectRecycleUntracked, value);
        }

        public bool IsRecycleMode => _selectedBaseline == Models.DiscardBaselineKind.MoveUntrackedToRecycleBin;

        public string BaselineExplanation => _selectedBaseline switch
        {
            Models.DiscardBaselineKind.RestoreToHead => App.Text("Discard.Baseline.ToHead.Desc"),
            Models.DiscardBaselineKind.MoveUntrackedToRecycleBin => App.Text("Discard.Baseline.Recycle.Desc"),
            _ => App.Text("Discard.Baseline.ToIndex.Desc"),
        };

        public bool ShowUnbornHeadBlock =>
            _selectedBaseline == Models.DiscardBaselineKind.RestoreToHead &&
            _preflight is { HasUnbornHead: true };

        public string UnbornHeadNotice
        {
            get => _unbornHeadNotice;
            private set => SetProperty(ref _unbornHeadNotice, value);
        }

        public bool HasNoTargetsForBaseline
        {
            get => _hasNoTargetsForBaseline;
            private set => SetProperty(ref _hasNoTargetsForBaseline, value);
        }

        public string NoTargetsNotice
        {
            get => _noTargetsNotice;
            private set => SetProperty(ref _noTargetsNotice, value);
        }

        public bool HasSkippedSubmodules
        {
            get => _hasSkippedSubmodules;
            private set => SetProperty(ref _hasSkippedSubmodules, value);
        }

        public string SkippedSubmodulesSummary
        {
            get => _skippedSubmodulesSummary;
            private set => SetProperty(ref _skippedSubmodulesSummary, value);
        }

        public bool HasIndexAddedOnlyTargets
        {
            get => _hasIndexAddedOnlyTargets;
            private set => SetProperty(ref _hasIndexAddedOnlyTargets, value);
        }

        public string IndexAddedOnlyNotice
        {
            get => _indexAddedOnlyNotice;
            private set => SetProperty(ref _indexAddedOnlyNotice, value);
        }

        public string TargetCountSummary
        {
            get => _targetCountSummary;
            private set => SetProperty(ref _targetCountSummary, value);
        }

        public int ActionableCount
        {
            get => _actionableCount;
            private set
            {
                if (SetProperty(ref _actionableCount, value))
                    NotifyPopupButtonsChanged();
            }
        }

        public string LoadErrorMessage
        {
            get => _loadErrorMessage;
            private set
            {
                if (SetProperty(ref _loadErrorMessage, value))
                    OnPropertyChanged(nameof(HasLoadError));
            }
        }

        public bool HasLoadError => !string.IsNullOrEmpty(LoadErrorMessage);

        public AvaloniaList<DiscardTargetRow> TargetRows { get; } = [];

        public string ResultTitle
        {
            get => _resultTitle;
            private set => SetProperty(ref _resultTitle, value);
        }

        public string ResultSummary
        {
            get => _resultSummary;
            private set => SetProperty(ref _resultSummary, value);
        }

        public string ResultBackupId
        {
            get => _resultBackupId;
            private set
            {
                if (SetProperty(ref _resultBackupId, value))
                    OnPropertyChanged(nameof(HasResultBackupId));
            }
        }

        public bool HasResultBackupId => !string.IsNullOrEmpty(ResultBackupId);

        public AvaloniaList<string> ResultLines { get; } = [];

        public override bool ShowConfirmButton => !HasResultView;

        public override bool CanConfirm =>
            !HasResultView &&
            !IsLoadingPreflight &&
            HasPreflight &&
            !HasLoadError &&
            !ShowUnbornHeadBlock &&
            ActionableCount > 0;

        public override string ConfirmButtonText => _selectedBaseline switch
        {
            Models.DiscardBaselineKind.MoveUntrackedToRecycleBin => App.Text("Discard.Confirm.Recycle"),
            Models.DiscardBaselineKind.RestoreToHead => App.Text("Discard.Confirm.ToHead"),
            _ => App.Text("Discard.Confirm.ToIndex"),
        };

        public override string CancelButtonText => HasResultView ? App.Text("Close") : App.Text("Cancel");

        public Discard(Repository repo)
            : this(repo, null, null, null, null, null)
        {
        }

        public Discard(
            Repository repo,
            List<Models.Change> changes,
            Models.Change next = null,
            Models.DiscardBaselineKind? preferredBaseline = null)
            : this(repo, changes, next, preferredBaseline, null, null)
        {
        }

        public Discard(
            Repository repo,
            List<Models.Change> changes,
            Models.Change next,
            Models.DiscardBaselineKind? preferredBaseline,
            Models.DiscardBackupStore backupStore,
            Func<string, (bool Success, string Error)> recycleHandler)
        {
            _repo = repo;
            _explicitChanges = changes;
            _next = next;
            _allChangesMode = changes == null;
            _backupStore = backupStore;
            _recycleHandler = recycleHandler;

            _selectedBaseline = ResolveInitialBaseline(changes, preferredBaseline);
            UpdateBaselineAvailability(changes);

            _ = LoadPreflightAsync();
        }

        public override async Task<bool> Sure()
        {
            if (HasResultView || IsLoadingPreflight || !CanConfirm)
                return false;

            using var lockWatcher = _repo.LockWatcher();
            ProgressDescription = ConfirmButtonText + " ...";

            var log = _repo.CreateLog("Discard Changes");
            Use(log);

            var skippedSubmodules = _preflight?.SkippedSubmodulePaths ?? [];
            var targets = _resolvedSourceTargets;
            var res = await Commands.DiscardBackupService.ExecuteDiscardAsync(
                _repo.FullPath,
                targets,
                SelectedBaseline,
                backupStore: _backupStore ?? Models.DiscardBackupStore.Instance,
                recycleHandler: _recycleHandler,
                journalStore: Models.OperationJournalStore.Instance,
                outerLog: log,
                expectedPreflight: _preflight).ConfigureAwait(false);

            log.Complete();

            if (res.BlockedBySnapshot)
            {
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    LoadErrorMessage = res.Summary;
                    _repo.MarkWorkingCopyDirtyManually();
                });
                await LoadPreflightAsync(preserveLoadError: true).ConfigureAwait(false);
                return false;
            }

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (_explicitChanges != null && _repo.WorkingCopy != null)
                {
                    var selection = new ChangeSelection(null);
                    if (_next != null)
                        selection.Changes.Add(_next);
                    _repo.WorkingCopy.SelectedUnstaged = selection;
                }

                _repo.MarkWorkingCopyDirtyManually();
            });

            _ = _repo.RefreshRecoveryStatusAsync();

            var hasWarningsOrFailures =
                !res.Success ||
                res.FailedPaths.Count > 0 ||
                skippedSubmodules.Count > 0;

            if (!hasWarningsOrFailures)
            {
                var backupTag = string.IsNullOrEmpty(res.BackupId)
                    ? string.Empty
                    : $"（30 天恢复编号：{res.BackupId[..Math.Min(8, res.BackupId.Length)]}）";
                Dispatcher.UIThread.Post(() => _repo.SendNotification(res.Summary + backupTag, false));
                return true;
            }

            await Dispatcher.UIThread.InvokeAsync(() => PopulateResultView(res, skippedSubmodules));
            return false;
        }

        private async Task LoadPreflightAsync(bool preserveLoadError = false)
        {
            var seq = Interlocked.Increment(ref _preflightSeq);
            IsLoadingPreflight = true;
            if (!preserveLoadError)
                LoadErrorMessage = string.Empty;

            try
            {
                List<Models.Change> targets;
                if (_allChangesMode)
                {
                    targets = await new Commands.QueryLocalChanges(_repo.FullPath)
                        .GetResultAsync()
                        .ConfigureAwait(false);
                }
                else
                {
                    targets = _explicitChanges ?? [];
                }

                var preflight = await Commands.DiscardBackupService.InspectDiscardAsync(
                    _repo.FullPath,
                    targets,
                    SelectedBaseline).ConfigureAwait(false);

                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    if (seq != Volatile.Read(ref _preflightSeq))
                        return;

                    _resolvedSourceTargets = targets;
                    _preflight = preflight;
                    ApplyPreflight(targets, preflight);
                });
            }
            catch (Exception ex)
            {
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    if (seq != Volatile.Read(ref _preflightSeq))
                        return;

                    HasPreflight = false;
                    ActionableCount = 0;
                    LoadErrorMessage = ex.Message;
                });
            }
            finally
            {
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    if (seq == Volatile.Read(ref _preflightSeq))
                    {
                        IsLoadingPreflight = false;
                        NotifyPopupButtonsChanged();
                    }
                });
            }
        }

        private void ApplyPreflight(List<Models.Change> targets, Models.DiscardPreflight preflight)
        {
            UpdateBaselineAvailability(targets);

            TargetRows.Clear();

            var tracked = preflight.TrackedTargets ?? [];
            var untracked = preflight.UntrackedTargets ?? [];
            var indexAddedPaths = preflight.IndexAddedOnlyPaths ?? [];
            var submodules = preflight.SkippedSubmodulePaths ?? [];

            foreach (var item in tracked)
            {
                if (item.IsIndexAddedOnly)
                {
                    TargetRows.Add(new DiscardTargetRow
                    {
                        Path = item.Path,
                        RoleLabel = App.Text("Discard.Role.IndexAddedRecycle"),
                        Detail = App.Text("Discard.Role.IndexAddedRecycle.Detail"),
                        IsWarning = true,
                    });
                }
                else
                {
                    TargetRows.Add(new DiscardTargetRow
                    {
                        Path = item.Path,
                        RoleLabel = SelectedBaseline == Models.DiscardBaselineKind.RestoreToHead
                            ? App.Text("Discard.Role.TrackedToHead")
                            : App.Text("Discard.Role.TrackedToIndex"),
                        Detail = BuildChangeDetail(item),
                        IsWarning = SelectedBaseline == Models.DiscardBaselineKind.RestoreToHead,
                    });
                }
            }

            foreach (var item in untracked)
            {
                TargetRows.Add(new DiscardTargetRow
                {
                    Path = item.Path,
                    RoleLabel = App.Text("Discard.Role.UntrackedRecycle"),
                    Detail = App.Text("Discard.Role.UntrackedRecycle.Detail"),
                    IsWarning = false,
                });
            }

            foreach (var sub in submodules)
            {
                TargetRows.Add(new DiscardTargetRow
                {
                    Path = sub,
                    RoleLabel = App.Text("Discard.Role.SubmoduleSkipped"),
                    Detail = App.Text("Discard.Role.SubmoduleSkipped.Detail"),
                    IsMuted = true,
                });
            }

            HasSkippedSubmodules = submodules.Count > 0;
            SkippedSubmodulesSummary = HasSkippedSubmodules
                ? App.Text("Discard.SkippedSubmodules", string.Join(", ", submodules))
                : string.Empty;

            HasIndexAddedOnlyTargets = SelectedBaseline == Models.DiscardBaselineKind.RestoreToHead && indexAddedPaths.Count > 0;
            IndexAddedOnlyNotice = HasIndexAddedOnlyTargets
                ? App.Text("Discard.IndexAddedOnlyNotice", indexAddedPaths.Count)
                : string.Empty;

            UnbornHeadNotice = preflight.HasUnbornHead && SelectedBaseline == Models.DiscardBaselineKind.RestoreToHead
                ? App.Text("Discard.UnbornHeadBlock")
                : string.Empty;

            var count = SelectedBaseline == Models.DiscardBaselineKind.MoveUntrackedToRecycleBin
                ? untracked.Count
                : tracked.Count;

            ActionableCount = count;
            HasNoTargetsForBaseline = count == 0;
            NoTargetsNotice = count == 0
                ? (!string.IsNullOrEmpty(preflight.BlockingReason) && !preflight.HasUnbornHead
                    ? preflight.BlockingReason
                    : BuildEmptyTargetsMessage(targets))
                : string.Empty;

            var trackedNonAddedCount = tracked.Count(t => !t.IsIndexAddedOnly);
            TargetCountSummary = SelectedBaseline switch
            {
                Models.DiscardBaselineKind.MoveUntrackedToRecycleBin => App.Text("Discard.TargetSummary.Recycle", count),
                Models.DiscardBaselineKind.RestoreToHead => App.Text("Discard.TargetSummary.ToHead", trackedNonAddedCount, indexAddedPaths.Count),
                _ => App.Text("Discard.TargetSummary.ToIndex", count),
            };

            HasPreflight = true;
            OnPropertyChanged(nameof(ShowUnbornHeadBlock));
            NotifyPopupButtonsChanged();
        }

        private string BuildEmptyTargetsMessage(List<Models.Change> targets)
        {
            if (targets == null || targets.Count == 0)
                return App.Text("Discard.Empty.WorkingCopy");

            return SelectedBaseline switch
            {
                Models.DiscardBaselineKind.RestoreWorktreeToIndex => App.Text("Discard.Empty.ToIndex"),
                Models.DiscardBaselineKind.RestoreToHead => App.Text("Discard.Empty.ToHead"),
                Models.DiscardBaselineKind.MoveUntrackedToRecycleBin => App.Text("Discard.Empty.Untracked"),
                _ => App.Text("Discard.Empty.WorkingCopy"),
            };
        }

        private static string BuildChangeDetail(Models.DiscardTargetItem item)
        {
            if (item.Index == Models.ChangeState.None && item.WorkTree != Models.ChangeState.None)
                return $"工作区：{item.WorkTree}";
            if (item.Index != Models.ChangeState.None && item.WorkTree == Models.ChangeState.None)
                return $"暂存区：{item.Index}";
            return $"暂存区：{item.Index} · 工作区：{item.WorkTree}";
        }

        private void PopulateResultView(Models.DiscardExecutionResult res, List<string> skippedSubmodules)
        {
            ResultLines.Clear();

            var processedCount = res.ProcessedPaths.Count + res.RecycledPaths.Count;
            IsResultSuccess = res.Success && res.FailedPaths.Count == 0;
            ResultTitle = !res.Success && processedCount == 0
                ? App.Text("Discard.Result.FailedTitle")
                : res.FailedPaths.Count > 0
                    ? App.Text("Discard.Result.PartialTitle")
                    : App.Text("Discard.Result.DoneWithWarningsTitle");
            ResultSummary = string.IsNullOrWhiteSpace(res.Summary)
                ? App.Text("Discard.Result.FailedTitle")
                : res.Summary;
            ResultBackupId = res.BackupId ?? string.Empty;

            var recycledSet = new HashSet<string>(res.RecycledPaths, StringComparer.OrdinalIgnoreCase);
            foreach (var path in res.ProcessedPaths)
            {
                if (!recycledSet.Contains(path))
                    ResultLines.Add($"[已还原] {path}");
            }
            foreach (var path in res.RecycledPaths)
                ResultLines.Add($"[已移至回收站] {path}");
            foreach (var sub in skippedSubmodules)
                ResultLines.Add($"[已跳过子模块] {sub}");
            foreach (var fail in res.FailedPaths)
                ResultLines.Add($"[失败] {fail}");

            HasResultView = true;
        }

        private static Models.DiscardBaselineKind ResolveInitialBaseline(
            List<Models.Change> changes,
            Models.DiscardBaselineKind? preferred)
        {
            if (preferred.HasValue)
                return preferred.Value;

            if (changes == null || changes.Count == 0)
                return Models.DiscardBaselineKind.RestoreWorktreeToIndex;

            var hasUnstagedTracked = changes.Any(c =>
                c.WorkTree != Models.ChangeState.None &&
                c.WorkTree != Models.ChangeState.Untracked);
            var hasStagedTracked = changes.Any(c =>
                c.Index != Models.ChangeState.None &&
                c.Index != Models.ChangeState.Untracked);
            var hasUntracked = changes.Any(c => c.WorkTree == Models.ChangeState.Untracked);

            if (hasUnstagedTracked)
                return Models.DiscardBaselineKind.RestoreWorktreeToIndex;
            if (hasStagedTracked)
                return Models.DiscardBaselineKind.RestoreToHead;
            if (hasUntracked)
                return Models.DiscardBaselineKind.MoveUntrackedToRecycleBin;

            return Models.DiscardBaselineKind.RestoreWorktreeToIndex;
        }

        private void UpdateBaselineAvailability(List<Models.Change> targets)
        {
            if (_allChangesMode)
            {
                CanSelectWorktreeToIndex = true;
                CanSelectToHead = true;
                CanSelectRecycleUntracked = true;
                return;
            }

            var hasTrackedWorktree = targets != null && targets.Any(c =>
                c.WorkTree != Models.ChangeState.None &&
                c.WorkTree != Models.ChangeState.Untracked);
            var hasTrackedAny = targets != null && targets.Any(c =>
                c.Index != Models.ChangeState.None ||
                (c.WorkTree != Models.ChangeState.None && c.WorkTree != Models.ChangeState.Untracked));
            var hasUntracked = targets != null && targets.Any(c => c.WorkTree == Models.ChangeState.Untracked);

            CanSelectWorktreeToIndex = hasTrackedWorktree || SelectedBaseline == Models.DiscardBaselineKind.RestoreWorktreeToIndex;
            CanSelectToHead = hasTrackedAny || SelectedBaseline == Models.DiscardBaselineKind.RestoreToHead;
            CanSelectRecycleUntracked = hasUntracked || SelectedBaseline == Models.DiscardBaselineKind.MoveUntrackedToRecycleBin;
        }

        private readonly Repository _repo;
        private readonly List<Models.Change> _explicitChanges;
        private readonly Models.Change _next;
        private readonly bool _allChangesMode;
        private readonly Models.DiscardBackupStore _backupStore;
        private readonly Func<string, (bool Success, string Error)> _recycleHandler;

        private List<Models.Change> _resolvedSourceTargets = [];
        private Models.DiscardPreflight _preflight;
        private Models.DiscardBaselineKind _selectedBaseline;
        private int _preflightSeq = 0;

        private bool _isLoadingPreflight = false;
        private bool _hasPreflight = false;
        private bool _hasResultView = false;
        private bool _isResultSuccess = false;
        private bool _canSelectWorktreeToIndex = true;
        private bool _canSelectToHead = true;
        private bool _canSelectRecycleUntracked = true;
        private bool _hasNoTargetsForBaseline = false;
        private bool _hasSkippedSubmodules = false;
        private bool _hasIndexAddedOnlyTargets = false;
        private int _actionableCount = 0;

        private string _unbornHeadNotice = string.Empty;
        private string _noTargetsNotice = string.Empty;
        private string _skippedSubmodulesSummary = string.Empty;
        private string _indexAddedOnlyNotice = string.Empty;
        private string _targetCountSummary = string.Empty;
        private string _loadErrorMessage = string.Empty;
        private string _resultTitle = string.Empty;
        private string _resultSummary = string.Empty;
        private string _resultBackupId = string.Empty;
    }
}
