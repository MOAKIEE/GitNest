using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Collections;
using Avalonia.Threading;

namespace SourceGit.ViewModels
{
    public sealed class StashCandidateRow
    {
        public string Path { get; init; } = string.Empty;
        public string StateLabel { get; init; } = string.Empty;
        public bool IsIncluded { get; init; }
        public string IncludedLabel => IsIncluded ? App.Text("Stash.Preview.Included") : App.Text("Stash.Preview.Skipped");
    }

    public enum StashPlacementMode
    {
        DiscardAfterStash = 0,
        KeepIndex = 1,
        KeepAll = 2,
    }

    public class StashChanges : Popup
    {
        public bool HasSelectedChanges => _changes is { Count: > 0 };
        public bool CanToggleOnlyStaged => !HasSelectedChanges && !HasResultView;
        public bool CanToggleIncludeUntracked => !OnlyStaged && !HasResultView;

        public string Message
        {
            get => _message;
            set => SetProperty(ref _message, value);
        }

        public StashPlacementMode PlacementMode
        {
            get => _placementMode;
            set
            {
                if (SetProperty(ref _placementMode, value))
                {
                    OnPropertyChanged(nameof(IsDiscardMode));
                    OnPropertyChanged(nameof(IsKeepIndexMode));
                    OnPropertyChanged(nameof(IsKeepAllMode));
                }
            }
        }

        public bool IsDiscardMode
        {
            get => _placementMode == StashPlacementMode.DiscardAfterStash;
            set
            {
                if (value)
                    PlacementMode = StashPlacementMode.DiscardAfterStash;
            }
        }

        public bool IsKeepIndexMode
        {
            get => _placementMode == StashPlacementMode.KeepIndex;
            set
            {
                if (value)
                    PlacementMode = StashPlacementMode.KeepIndex;
            }
        }

        public bool IsKeepAllMode
        {
            get => _placementMode == StashPlacementMode.KeepAll;
            set
            {
                if (value)
                    PlacementMode = StashPlacementMode.KeepAll;
            }
        }

        public bool IncludeUntracked
        {
            get => _includeUntracked;
            set
            {
                if (SetProperty(ref _includeUntracked, value))
                    _ = LoadPreflightAsync();
            }
        }

        public bool OnlyStaged
        {
            get => _onlyStaged;
            set
            {
                if (SetProperty(ref _onlyStaged, value))
                {
                    OnPropertyChanged(nameof(CanToggleIncludeUntracked));
                    _ = LoadPreflightAsync();
                }
            }
        }

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

        public bool HasBlockingReason => !string.IsNullOrEmpty(BlockingReason);

        public string BlockingReason
        {
            get => _blockingReason;
            private set
            {
                if (SetProperty(ref _blockingReason, value))
                {
                    OnPropertyChanged(nameof(HasBlockingReason));
                    NotifyPopupButtonsChanged();
                }
            }
        }

        public string PreflightSummary
        {
            get => _preflightSummary;
            private set => SetProperty(ref _preflightSummary, value);
        }

        public bool HasIgnoredFiles => !string.IsNullOrEmpty(IgnoredNotice);

        public string IgnoredNotice
        {
            get => _ignoredNotice;
            private set
            {
                if (SetProperty(ref _ignoredNotice, value))
                    OnPropertyChanged(nameof(HasIgnoredFiles));
            }
        }

        public bool HasCandidateFiles => CandidateFiles.Count > 0;
        public AvaloniaList<StashCandidateRow> CandidateFiles { get; } = [];

        public bool HasResultView
        {
            get => _hasResultView;
            private set
            {
                if (SetProperty(ref _hasResultView, value))
                    NotifyPopupButtonsChanged();
            }
        }

        public string ResultSummary
        {
            get => _resultSummary;
            private set => SetProperty(ref _resultSummary, value);
        }

        public override bool ShowConfirmButton => !HasResultView;

        public override bool CanConfirm =>
            !HasResultView &&
            !IsLoadingPreflight &&
            _preflight is { CanProceed: true, HasChangesToStash: true };

        public override string ConfirmButtonText => App.Text("Stash");
        public override string CancelButtonText => HasResultView ? App.Text("Close") : App.Text("Cancel");

        public StashChanges(Repository repo, List<Models.Change> changes, bool stagedOnlyDefault = false)
        {
            _repo = repo;
            _changes = changes;
            _onlyStaged = changes == null && stagedOnlyDefault;
            _ = LoadPreflightAsync();
        }

        public override async Task<bool> Sure()
        {
            if (HasResultView)
                return false;

            using var lockWatcher = _repo.LockWatcher();
            ProgressDescription = "正在创建暂存 ...";

            var preflight = _preflight ?? await Commands.StashWorkflowService.InspectPushAsync(
                _repo.FullPath,
                _changes,
                _includeUntracked,
                _onlyStaged).ConfigureAwait(false);

            if (!preflight.CanProceed || !preflight.HasChangesToStash)
            {
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    ApplyPreflight(preflight);
                    ResultSummary = string.IsNullOrWhiteSpace(preflight.BlockingReason)
                        ? preflight.Summary
                        : preflight.BlockingReason;
                    HasResultView = true;
                });
                return false;
            }

            var log = _repo.CreateLog("Stash Changes");
            Use(log);

            var keepIndex = _placementMode == StashPlacementMode.KeepIndex;
            var keepAll = _placementMode == StashPlacementMode.KeepAll;
            var res = await Commands.StashWorkflowService.ExecutePushAsync(
                _repo.FullPath,
                _message,
                _changes,
                _includeUntracked,
                keepIndex,
                keepAll,
                _onlyStaged,
                journalStore: Models.OperationJournalStore.Instance,
                outerLog: log).ConfigureAwait(false);

            log.Complete();

            _repo.MarkWorkingCopyDirtyManually();
            _repo.RefreshStashes();
            _ = _repo.RefreshRecoveryStatusAsync();

            if (res.Success && !res.WasNoOp)
            {
                if (!keepAll && _changes == null && !_onlyStaged)
                    Dispatcher.UIThread.Post(() => _repo.ClearCommitMessage());

                Dispatcher.UIThread.Post(() => _repo.SendNotification(res.Message, false));
                return true;
            }

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                ResultSummary = string.IsNullOrWhiteSpace(res.Message)
                    ? App.Text("Stash.Push.FailedFallback")
                    : res.Message;
                HasResultView = true;
            });
            return false;
        }

        private async Task LoadPreflightAsync()
        {
            var seq = Interlocked.Increment(ref _preflightSeq);
            IsLoadingPreflight = true;

            try
            {
                var res = await Commands.StashWorkflowService.InspectPushAsync(
                    _repo.FullPath,
                    _changes,
                    _includeUntracked,
                    _onlyStaged).ConfigureAwait(false);

                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    if (seq != Volatile.Read(ref _preflightSeq))
                        return;

                    ApplyPreflight(res);
                });
            }
            catch (Exception ex)
            {
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    if (seq != Volatile.Read(ref _preflightSeq))
                        return;

                    _preflight = null;
                    HasPreflight = false;
                    BlockingReason = ex.Message;
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

        private void ApplyPreflight(Models.StashPushPreflight res)
        {
            _preflight = res;
            CandidateFiles.Clear();

            foreach (var item in res.CandidateFiles)
            {
                var stateLabel = item.IsUntracked
                    ? App.Text("Stash.Preview.State.Untracked")
                    : item.Index != Models.ChangeState.None && item.WorkTree != Models.ChangeState.None
                        ? App.Text("Stash.Preview.State.Both")
                        : item.Index != Models.ChangeState.None
                            ? App.Text("Stash.Preview.State.Staged")
                            : App.Text("Stash.Preview.State.Unstaged");

                CandidateFiles.Add(new StashCandidateRow
                {
                    Path = item.Path,
                    StateLabel = stateLabel,
                    IsIncluded = item.Included,
                });
            }

            OnPropertyChanged(nameof(HasCandidateFiles));

            BlockingReason = !res.CanProceed || !res.HasChangesToStash
                ? (!string.IsNullOrWhiteSpace(res.BlockingReason) ? res.BlockingReason : res.Summary)
                : string.Empty;

            PreflightSummary = App.Text("Stash.Preview.Summary", res.IncludedCount, res.UntrackedIncludedCount);

            if (res.IgnoredFileCount > 0)
            {
                var sample = res.IgnoredSamplePaths.Count > 0
                    ? string.Join(", ", res.IgnoredSamplePaths.Take(3))
                    : string.Empty;
                IgnoredNotice = string.IsNullOrEmpty(sample)
                    ? App.Text("Stash.Preview.IgnoredNoticeNoSample", res.IgnoredFileCount)
                    : App.Text("Stash.Preview.IgnoredNotice", res.IgnoredFileCount, sample);
            }
            else
            {
                IgnoredNotice = string.Empty;
            }

            HasPreflight = true;
            NotifyPopupButtonsChanged();
        }

        private readonly Repository _repo;
        private readonly List<Models.Change> _changes;
        private Models.StashPushPreflight _preflight;
        private int _preflightSeq = 0;

        private string _message = string.Empty;
        private StashPlacementMode _placementMode = StashPlacementMode.DiscardAfterStash;
        private bool _includeUntracked = true;
        private bool _onlyStaged = false;
        private bool _isLoadingPreflight = false;
        private bool _hasPreflight = false;
        private bool _hasResultView = false;
        private string _blockingReason = string.Empty;
        private string _preflightSummary = string.Empty;
        private string _ignoredNotice = string.Empty;
        private string _resultSummary = string.Empty;
    }
}
