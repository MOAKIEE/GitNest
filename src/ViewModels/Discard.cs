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
                {
                    OnPropertyChanged(nameof(ShowRemovalModeSelector));
                    NotifyPopupButtonsChanged();
                }
            }
        }

        public bool HasPreflight
        {
            get => _hasPreflight;
            private set
            {
                if (SetProperty(ref _hasPreflight, value))
                {
                    OnPropertyChanged(nameof(ShowRemovalModeSelector));
                    NotifyPopupButtonsChanged();
                }
            }
        }

        public bool HasResultView
        {
            get => _hasResultView;
            private set
            {
                if (SetProperty(ref _hasResultView, value))
                {
                    OnPropertyChanged(nameof(ShowRemovalModeSelector));
                    NotifyPopupButtonsChanged();
                }
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
                    OnPropertyChanged(nameof(IncludesFileRemoval));
                    OnPropertyChanged(nameof(ShowRemovalModeSelector));
                    OnPropertyChanged(nameof(DialogTitle));
                    NotifyPopupButtonsChanged();
                    _ = LoadPreflightAsync();
                }
            }
        }

        public Models.FileRemovalMode SelectedRemovalMode
        {
            get => _selectedRemovalMode;
            set
            {
                var normalized = Models.FileRemovalModeExtensions.Normalize(value);
                if (SetProperty(ref _selectedRemovalMode, normalized))
                {
                    OnPropertyChanged(nameof(IsRemovalModeRecycleBin));
                    OnPropertyChanged(nameof(IsRemovalModeDirect));
                    OnPropertyChanged(nameof(IsDirectRemovalMode));
                    OnPropertyChanged(nameof(BaselineExplanation));
                    OnPropertyChanged(nameof(DialogTitle));
                    NotifyPopupButtonsChanged();
                    if (!HasResultView)
                        _ = LoadPreflightAsync();
                }
            }
        }

        public bool IsRemovalModeRecycleBin
        {
            get => _selectedRemovalMode == Models.FileRemovalMode.RecycleBin;
            set
            {
                if (value)
                    SelectedRemovalMode = Models.FileRemovalMode.RecycleBin;
            }
        }

        public bool IsRemovalModeDirect
        {
            get => _selectedRemovalMode == Models.FileRemovalMode.Direct;
            set
            {
                if (value)
                    SelectedRemovalMode = Models.FileRemovalMode.Direct;
            }
        }

        public bool IsDirectRemovalMode => _selectedRemovalMode == Models.FileRemovalMode.Direct;

        public bool IncludesFileRemoval => HasResultView
            ? HasRetryableFailures && (_retryPreflight?.IncludesFileRemoval ?? false)
            : (_preflight?.IncludesFileRemoval ?? false);

        public bool ShowRemovalModeSelector =>
            !HasResultView &&
            !IsLoadingPreflight &&
            HasPreflight &&
            !HasNoTargetsForBaseline &&
            (_preflight?.IncludesFileRemoval ?? false);

        public string DialogTitle
        {
            get
            {
                var activeBaseline = HasResultView && HasRetryableFailures ? _retryBaseline : _selectedBaseline;
                if (activeBaseline == Models.DiscardBaselineKind.MoveUntrackedToRecycleBin)
                {
                    return IsDirectRemovalMode
                        ? App.Text("Discard.Title.DirectDelete")
                        : App.Text("Discard.Title.RecycleBin");
                }

                return App.Text("Discard");
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
            Models.DiscardBaselineKind.MoveUntrackedToRecycleBin => IsDirectRemovalMode
                ? App.Text("Discard.Baseline.CleanUntracked.Direct.Desc")
                : App.Text("Discard.Baseline.CleanUntracked.Recycle.Desc"),
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
            private set
            {
                if (SetProperty(ref _hasNoTargetsForBaseline, value))
                    OnPropertyChanged(nameof(ShowRemovalModeSelector));
            }
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

        public bool HasSkippedNestedRepos
        {
            get => _hasSkippedNestedRepos;
            private set => SetProperty(ref _hasSkippedNestedRepos, value);
        }

        public string SkippedNestedReposSummary
        {
            get => _skippedNestedReposSummary;
            private set => SetProperty(ref _skippedNestedReposSummary, value);
        }

        public bool HasSkippedIgnoredPaths
        {
            get => _hasSkippedIgnoredPaths;
            private set => SetProperty(ref _hasSkippedIgnoredPaths, value);
        }

        public string SkippedIgnoredSummary
        {
            get => _skippedIgnoredSummary;
            private set => SetProperty(ref _skippedIgnoredSummary, value);
        }

        public bool HasRetryableFailures
        {
            get => _hasRetryableFailures;
            private set
            {
                if (SetProperty(ref _hasRetryableFailures, value))
                {
                    OnPropertyChanged(nameof(IncludesFileRemoval));
                    OnPropertyChanged(nameof(DialogTitle));
                    NotifyPopupButtonsChanged();
                }
            }
        }

        public string RetryNotice
        {
            get => _retryNotice;
            private set => SetProperty(ref _retryNotice, value);
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

        public override bool ShowConfirmButton => !HasResultView || HasRetryableFailures;

        public override bool CanConfirm =>
            !IsLoadingPreflight &&
            (!HasResultView
                ? HasPreflight && !HasLoadError && !ShowUnbornHeadBlock && ActionableCount > 0 && (_preflight?.CanProceed ?? false)
                : HasRetryableFailures && (_retryPreflight?.CanProceed ?? false));

        public override string ConfirmButtonText
        {
            get
            {
                if (HasResultView && HasRetryableFailures)
                {
                    if (_retryPreflight is { IncludesFileRemoval: true })
                    {
                        return IsDirectRemovalMode
                            ? App.Text("Discard.Confirm.RetryDirectDelete")
                            : App.Text("Discard.Confirm.RetryRecycle");
                    }

                    return App.Text("Discard.Confirm.Retry");
                }

                return _selectedBaseline switch
                {
                    Models.DiscardBaselineKind.MoveUntrackedToRecycleBin => IsDirectRemovalMode
                        ? App.Text("Discard.Confirm.DirectDelete")
                        : App.Text("Discard.Confirm.Recycle"),
                    Models.DiscardBaselineKind.RestoreToHead => App.Text("Discard.Confirm.ToHead"),
                    _ => App.Text("Discard.Confirm.ToIndex"),
                };
            }
        }

        public override string CancelButtonText => HasResultView ? App.Text("Close") : App.Text("Cancel");

        public Discard(Repository repo)
            : this(repo, null, null, null, null, null, null, null, null)
        {
        }

        public Discard(
            Repository repo,
            List<Models.Change> changes,
            Models.Change next = null,
            Models.DiscardBaselineKind? preferredBaseline = null,
            List<string> selectedFolderPaths = null)
            : this(repo, changes, next, preferredBaseline, null, null, null, null, selectedFolderPaths)
        {
        }

        public Discard(
            Repository repo,
            List<Models.Change> changes,
            Models.Change next,
            Models.DiscardBaselineKind? preferredBaseline,
            Models.DiscardBackupStore backupStore,
            Func<string, (bool Success, string Error)> recycleHandler,
            Models.FileRemovalMode? preferredRemovalMode = null,
            Func<string, (bool Success, string Error)> directDeleteHandler = null,
            List<string> selectedFolderPaths = null)
        {
            _repo = repo;
            _explicitChanges = changes;
            _selectedFolderPaths = selectedFolderPaths;
            _next = next;
            _allChangesMode = changes == null;
            _backupStore = backupStore;
            _recycleHandler = recycleHandler;
            _directDeleteHandler = directDeleteHandler;
            _selectedRemovalMode = Models.FileRemovalModeExtensions.Normalize(preferredRemovalMode ?? Preferences.Instance.FileRemovalMode);

            _selectedBaseline = ResolveInitialBaseline(changes, preferredBaseline);
            UpdateBaselineAvailability(changes);

            _ = LoadPreflightAsync();
        }

        public override async Task<bool> Sure()
        {
            if (IsLoadingPreflight || !CanConfirm || (HasResultView && !HasRetryableFailures))
                return false;

            using var lockWatcher = _repo.LockWatcher();
            ProgressDescription = ConfirmButtonText + " ...";

            var log = _repo.CreateLog("Discard Changes");
            Use(log);

            Models.DiscardExecutionResult res;
            List<string> skippedSubmodules;

            if (HasResultView && HasRetryableFailures)
            {
                var retryInspect = await Commands.DiscardBackupService.InspectDiscardAsync(
                    _repo.FullPath,
                    _retryTargets,
                    _retryBaseline,
                    SelectedRemovalMode,
                    _retryFolderPaths).ConfigureAwait(false);
                _retryPreflight = retryInspect;
                skippedSubmodules = retryInspect.SkippedSubmodulePaths ?? [];

                res = await Commands.DiscardBackupService.ExecuteDiscardAsync(
                    _repo.FullPath,
                    _retryTargets,
                    _retryBaseline,
                    backupStore: _backupStore ?? Models.DiscardBackupStore.Instance,
                    recycleHandler: _recycleHandler,
                    journalStore: Models.OperationJournalStore.Instance,
                    outerLog: log,
                    expectedPreflight: retryInspect,
                    removalMode: SelectedRemovalMode,
                    directDeleteHandler: _directDeleteHandler,
                    selectedFolderPaths: _retryFolderPaths).ConfigureAwait(false);
            }
            else
            {
                skippedSubmodules = _preflight?.SkippedSubmodulePaths ?? [];
                var targets = _resolvedSourceTargets;
                res = await Commands.DiscardBackupService.ExecuteDiscardAsync(
                    _repo.FullPath,
                    targets,
                    SelectedBaseline,
                    backupStore: _backupStore ?? Models.DiscardBackupStore.Instance,
                    recycleHandler: _recycleHandler,
                    journalStore: Models.OperationJournalStore.Instance,
                    outerLog: log,
                    expectedPreflight: _preflight,
                    removalMode: SelectedRemovalMode,
                    directDeleteHandler: _directDeleteHandler,
                    selectedFolderPaths: _selectedFolderPaths).ConfigureAwait(false);
            }

            log.Complete();

            if (res.BlockedBySnapshot)
            {
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    LoadErrorMessage = res.Summary;
                    _repo.MarkWorkingCopyDirtyManually();
                });
                if (!HasResultView)
                    await LoadPreflightAsync(preserveLoadError: true).ConfigureAwait(false);
                else
                    await PrepareRetryStateAsync(res).ConfigureAwait(false);
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

            await PrepareRetryStateAsync(res).ConfigureAwait(false);
            await Dispatcher.UIThread.InvokeAsync(() => PopulateResultView(res, skippedSubmodules));
            return false;
        }

        private async Task PrepareRetryStateAsync(Models.DiscardExecutionResult res)
        {
            if (res == null || res.FailedPaths.Count == 0)
            {
                _retryTargets = [];
                _retryFolderPaths = [];
                _retryPreflight = null;
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    HasRetryableFailures = false;
                    RetryNotice = string.Empty;
                });
                return;
            }

            var failedSet = new HashSet<string>(
                res.FailedPaths,
                OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
            var liveChanges = await new Commands.QueryLocalChanges(_repo.FullPath)
                .GetResultAsync()
                .ConfigureAwait(false);

            var retryTargets = new List<Models.Change>();
            var matched = new HashSet<string>(
                OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

            foreach (var c in liveChanges)
            {
                if (c != null && failedSet.Contains(c.Path))
                {
                    retryTargets.Add(c);
                    matched.Add(c.Path);
                }
            }

            foreach (var failPath in res.FailedPaths)
            {
                if (!matched.Contains(failPath))
                {
                    retryTargets.Add(new Models.Change
                    {
                        Path = failPath,
                        Index = Models.ChangeState.Untracked,
                        WorkTree = Models.ChangeState.Untracked,
                    });
                }
            }

            var retryBaseline = res.Baseline == Models.DiscardBaselineKind.RestoreToHead &&
                                retryTargets.All(c => c.Index == Models.ChangeState.Untracked || c.WorkTree == Models.ChangeState.Untracked)
                ? Models.DiscardBaselineKind.MoveUntrackedToRecycleBin
                : res.Baseline;

            var retryPreflight = await Commands.DiscardBackupService.InspectDiscardAsync(
                _repo.FullPath,
                retryTargets,
                retryBaseline,
                SelectedRemovalMode).ConfigureAwait(false);

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                _retryTargets = retryTargets;
                _retryFolderPaths = [];
                _retryBaseline = retryBaseline;
                _retryPreflight = retryPreflight;
                HasRetryableFailures = retryPreflight.CanProceed;
                RetryNotice = HasRetryableFailures
                    ? App.Text("Discard.Retry.Notice", res.FailedPaths.Count)
                    : string.Empty;
                OnPropertyChanged(nameof(IncludesFileRemoval));
                OnPropertyChanged(nameof(DialogTitle));
                NotifyPopupButtonsChanged();
            });
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
                    SelectedBaseline,
                    SelectedRemovalMode,
                    _selectedFolderPaths).ConfigureAwait(false);

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
            var cleanableDirs = preflight.CleanableDirectories ?? [];
            var indexAddedPaths = preflight.IndexAddedOnlyPaths ?? [];
            var submodules = preflight.SkippedSubmodulePaths ?? [];
            var nestedRepos = preflight.SkippedNestedRepoPaths ?? [];
            var ignored = preflight.SkippedIgnoredPaths ?? [];
            var isDirect = SelectedRemovalMode == Models.FileRemovalMode.Direct;

            if (SelectedBaseline == Models.DiscardBaselineKind.MoveUntrackedToRecycleBin)
            {
                foreach (var item in untracked)
                {
                    TargetRows.Add(new DiscardTargetRow
                    {
                        Path = item.Path,
                        RoleLabel = isDirect
                            ? App.Text("Discard.Role.UntrackedDirectDelete")
                            : App.Text("Discard.Role.UntrackedRecycle"),
                        Detail = item.IsSymlink
                            ? App.Text("Discard.Role.UntrackedSymlink.Detail")
                            : isDirect
                                ? App.Text("Discard.Role.UntrackedDirectDelete.Detail")
                                : App.Text("Discard.Role.UntrackedRecycle.Detail"),
                        IsWarning = isDirect,
                    });
                }

                if (untracked.Count == 0 && preflight.CanProceed)
                {
                    foreach (var dir in cleanableDirs)
                    {
                        TargetRows.Add(new DiscardTargetRow
                        {
                            Path = dir,
                            RoleLabel = isDirect
                                ? App.Text("Discard.Role.UntrackedDirectDelete")
                                : App.Text("Discard.Role.UntrackedRecycle"),
                            Detail = App.Text("Discard.Role.EmptyDir.Detail"),
                            IsWarning = isDirect,
                        });
                    }
                }
            }
            else
            {
                foreach (var item in tracked)
                {
                    if (item.IsIndexAddedOnly)
                    {
                        TargetRows.Add(new DiscardTargetRow
                        {
                            Path = item.Path,
                            RoleLabel = isDirect
                                ? App.Text("Discard.Role.IndexAddedDirectDelete")
                                : App.Text("Discard.Role.IndexAddedRecycle"),
                            Detail = isDirect
                                ? App.Text("Discard.Role.IndexAddedDirectDelete.Detail")
                                : App.Text("Discard.Role.IndexAddedRecycle.Detail"),
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

            HasSkippedNestedRepos = nestedRepos.Count > 0;
            SkippedNestedReposSummary = HasSkippedNestedRepos
                ? App.Text("Discard.SkippedNestedRepos", string.Join(", ", nestedRepos))
                : string.Empty;

            HasSkippedIgnoredPaths = ignored.Count > 0;
            SkippedIgnoredSummary = HasSkippedIgnoredPaths
                ? App.Text("Discard.SkippedIgnored", ignored.Count, string.Join(", ", ignored.Take(5)))
                : string.Empty;

            HasIndexAddedOnlyTargets = SelectedBaseline == Models.DiscardBaselineKind.RestoreToHead && indexAddedPaths.Count > 0;
            IndexAddedOnlyNotice = HasIndexAddedOnlyTargets
                ? App.Text(isDirect ? "Discard.IndexAddedOnlyNotice.Direct" : "Discard.IndexAddedOnlyNotice", indexAddedPaths.Count)
                : string.Empty;

            UnbornHeadNotice = preflight.HasUnbornHead && SelectedBaseline == Models.DiscardBaselineKind.RestoreToHead
                ? App.Text("Discard.UnbornHeadBlock")
                : string.Empty;

            var count = SelectedBaseline == Models.DiscardBaselineKind.MoveUntrackedToRecycleBin
                ? (untracked.Count > 0 ? untracked.Count : (preflight.CanProceed ? cleanableDirs.Count : 0))
                : tracked.Count;

            ActionableCount = count;
            HasNoTargetsForBaseline = count == 0 || (!preflight.HasUnbornHead && !preflight.CanProceed);
            NoTargetsNotice = !preflight.CanProceed || count == 0
                ? (!string.IsNullOrEmpty(preflight.BlockingReason) && !preflight.HasUnbornHead
                    ? preflight.BlockingReason
                    : BuildEmptyTargetsMessage(targets))
                : string.Empty;

            var trackedNonAddedCount = tracked.Count(t => !t.IsIndexAddedOnly);
            TargetCountSummary = SelectedBaseline switch
            {
                Models.DiscardBaselineKind.MoveUntrackedToRecycleBin => isDirect
                    ? App.Text("Discard.TargetSummary.DirectDelete", count)
                    : App.Text("Discard.TargetSummary.Recycle", count),
                Models.DiscardBaselineKind.RestoreToHead => indexAddedPaths.Count > 0 && isDirect
                    ? App.Text("Discard.TargetSummary.ToHeadDirect", trackedNonAddedCount, indexAddedPaths.Count)
                    : App.Text("Discard.TargetSummary.ToHead", trackedNonAddedCount, indexAddedPaths.Count),
                _ => App.Text("Discard.TargetSummary.ToIndex", count),
            };

            HasPreflight = true;
            OnPropertyChanged(nameof(IncludesFileRemoval));
            OnPropertyChanged(nameof(ShowRemovalModeSelector));
            OnPropertyChanged(nameof(DialogTitle));
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

            var restored = res.RestoredPaths ?? [];
            var recycled = res.RecycledPaths ?? [];
            var directDeleted = res.DirectDeletedPaths ?? [];
            var skipped = res.SkippedPaths ?? [];
            var failed = res.FailedPaths ?? [];
            var reasons = res.FailureReasons ?? new Dictionary<string, string>();
            var completedCount = restored.Count + recycled.Count + directDeleted.Count;

            IsResultSuccess = res.Success && failed.Count == 0;
            ResultTitle = !res.Success && completedCount == 0
                ? App.Text("Discard.Result.FailedTitle")
                : failed.Count > 0
                    ? App.Text("Discard.Result.PartialTitle")
                    : App.Text("Discard.Result.DoneWithWarningsTitle");
            ResultSummary = string.IsNullOrWhiteSpace(res.Summary)
                ? App.Text("Discard.Result.FailedTitle")
                : res.Summary;
            ResultBackupId = res.BackupId ?? string.Empty;

            foreach (var path in restored)
                ResultLines.Add($"[已还原] {path}");
            foreach (var path in recycled)
                ResultLines.Add($"[已移入回收站] {path}");
            foreach (var path in directDeleted)
                ResultLines.Add($"[已直接删除] {path}");
            foreach (var path in skipped)
                ResultLines.Add($"[已跳过] {path}");
            foreach (var sub in skippedSubmodules)
                ResultLines.Add($"[已跳过子模块] {sub}");
            foreach (var fail in failed)
            {
                ResultLines.Add(reasons.TryGetValue(fail, out var detailed) && !string.IsNullOrWhiteSpace(detailed)
                    ? $"[失败] {fail}: {detailed}"
                    : $"[失败] {fail}");
            }

            HasResultView = true;
            OnPropertyChanged(nameof(IncludesFileRemoval));
            OnPropertyChanged(nameof(ShowRemovalModeSelector));
            OnPropertyChanged(nameof(DialogTitle));
            NotifyPopupButtonsChanged();
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
        private readonly List<string> _selectedFolderPaths;
        private readonly Models.Change _next;
        private readonly bool _allChangesMode;
        private readonly Models.DiscardBackupStore _backupStore;
        private readonly Func<string, (bool Success, string Error)> _recycleHandler;
        private readonly Func<string, (bool Success, string Error)> _directDeleteHandler;

        private List<Models.Change> _resolvedSourceTargets = [];
        private Models.DiscardPreflight _preflight;
        private List<Models.Change> _retryTargets = [];
        private List<string> _retryFolderPaths = [];
        private Models.DiscardBaselineKind _retryBaseline;
        private Models.DiscardPreflight _retryPreflight;
        private Models.DiscardBaselineKind _selectedBaseline;
        private Models.FileRemovalMode _selectedRemovalMode;
        private int _preflightSeq = 0;

        private bool _isLoadingPreflight = false;
        private bool _hasPreflight = false;
        private bool _hasResultView = false;
        private bool _isResultSuccess = false;
        private bool _hasRetryableFailures = false;
        private bool _canSelectWorktreeToIndex = true;
        private bool _canSelectToHead = true;
        private bool _canSelectRecycleUntracked = true;
        private bool _hasNoTargetsForBaseline = false;
        private bool _hasSkippedSubmodules = false;
        private bool _hasSkippedNestedRepos = false;
        private bool _hasSkippedIgnoredPaths = false;
        private bool _hasIndexAddedOnlyTargets = false;
        private int _actionableCount = 0;

        private string _unbornHeadNotice = string.Empty;
        private string _noTargetsNotice = string.Empty;
        private string _skippedSubmodulesSummary = string.Empty;
        private string _skippedNestedReposSummary = string.Empty;
        private string _skippedIgnoredSummary = string.Empty;
        private string _indexAddedOnlyNotice = string.Empty;
        private string _targetCountSummary = string.Empty;
        private string _loadErrorMessage = string.Empty;
        private string _retryNotice = string.Empty;
        private string _resultTitle = string.Empty;
        private string _resultSummary = string.Empty;
        private string _resultBackupId = string.Empty;
    }
}
