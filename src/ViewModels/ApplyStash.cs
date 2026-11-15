using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Collections;
using Avalonia.Threading;

namespace SourceGit.ViewModels
{
    public class ApplyStash : Popup
    {
        public Models.Stash Stash { get; }

        public string SourceBranchDisplay => string.IsNullOrEmpty(Stash.SourceBranch)
            ? App.Text("Stash.Meta.ExternalOrNoBranch")
            : Stash.SourceBranch;

        public string StashShortSha => Stash.SHA.Length > 8 ? Stash.SHA[..8] : Stash.SHA;

        public bool RestoreIndex
        {
            get => _restoreIndex;
            set => SetProperty(ref _restoreIndex, value);
        }

        public bool DropAfterApply
        {
            get => _dropAfterApply;
            set => SetProperty(ref _dropAfterApply, value);
        }

        public bool AllowDirtyWorkingTree
        {
            get => _allowDirtyWorkingTree;
            set
            {
                if (SetProperty(ref _allowDirtyWorkingTree, value))
                    _ = LoadPreflightAsync();
            }
        }

        public bool ShowAllowDirtyOption
        {
            get => _showAllowDirtyOption;
            private set => SetProperty(ref _showAllowDirtyOption, value);
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

        public bool HasBlockingReasons => BlockingReasons.Count > 0;
        public AvaloniaList<string> BlockingReasons { get; } = [];

        public bool HasCollidingPaths => CollidingPaths.Count > 0;
        public AvaloniaList<string> CollidingPaths { get; } = [];

        public bool HasEstimatedPaths => EstimatedPaths.Count > 0;
        public AvaloniaList<string> EstimatedPaths { get; } = [];

        public string PreflightSummary
        {
            get => _preflightSummary;
            private set => SetProperty(ref _preflightSummary, value);
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

        public bool IsResultConflictRetained
        {
            get => _isResultConflictRetained;
            private set => SetProperty(ref _isResultConflictRetained, value);
        }

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

        public AvaloniaList<string> ResultLines { get; } = [];

        public override bool ShowConfirmButton => !HasResultView;

        public override bool CanConfirm =>
            !HasResultView &&
            !IsLoadingPreflight &&
            _preflight is { CanProceed: true };

        public override string ConfirmButtonText => App.Text("ApplyStash.Confirm");
        public override string CancelButtonText => HasResultView ? App.Text("Close") : App.Text("Cancel");

        public ApplyStash(Repository repo, Models.Stash stash)
        {
            _repo = repo;
            Stash = stash;
            _ = LoadPreflightAsync();
        }

        public override async Task<bool> Sure()
        {
            if (HasResultView || !CanConfirm)
                return false;

            using var lockWatcher = _repo.LockWatcher();
            ProgressDescription = $"Applying {Stash.Name} ...";

            var log = _repo.CreateLog("Apply Stash");
            Use(log);

            var res = await Commands.StashWorkflowService.ExecuteApplyAsync(
                _repo.FullPath,
                Stash.Name,
                Stash.SHA,
                _restoreIndex,
                _dropAfterApply,
                _allowDirtyWorkingTree,
                expectedSnapshot: _preflight?.Snapshot,
                journalStore: Models.OperationJournalStore.Instance,
                outerLog: log).ConfigureAwait(false);

            log.Complete();

            _repo.MarkWorkingCopyDirtyManually();
            _repo.RefreshStashes();
            _ = _repo.RefreshInProgressStateAsync();
            _ = _repo.RefreshRecoveryStatusAsync();

            if (res.Status == Models.StashApplyStatus.CleanApplied)
            {
                Dispatcher.UIThread.Post(() => _repo.SendNotification(res.Summary, false));
                return true;
            }

            await Dispatcher.UIThread.InvokeAsync(() => PopulateResultView(res));
            return false;
        }

        private async Task LoadPreflightAsync()
        {
            var seq = Interlocked.Increment(ref _preflightSeq);
            IsLoadingPreflight = true;

            try
            {
                var res = await Commands.StashWorkflowService.InspectApplyAsync(
                    _repo.FullPath,
                    Stash,
                    requireCleanWorkingTree: !_allowDirtyWorkingTree).ConfigureAwait(false);

                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    if (seq != Volatile.Read(ref _preflightSeq))
                        return;

                    _preflight = res;
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
                    BlockingReasons.Clear();
                    BlockingReasons.Add(ex.Message);
                    OnPropertyChanged(nameof(HasBlockingReasons));
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

        private void ApplyPreflight(Models.StashApplyPreflight res)
        {
            BlockingReasons.Clear();
            foreach (var r in res.BlockingReasons)
                BlockingReasons.Add(r);

            CollidingPaths.Clear();
            foreach (var p in res.CollidingUntrackedPaths)
                CollidingPaths.Add($"[未跟踪冲突] {p}");
            foreach (var p in res.CollidingIgnoredPaths)
                CollidingPaths.Add($"[忽略文件冲突] {p}");

            EstimatedPaths.Clear();
            foreach (var p in res.EstimatedAffectedPaths)
                EstimatedPaths.Add(p);

            ShowAllowDirtyOption =
                (!res.IsWorkingTreeClean || !res.IsIndexClean) &&
                !res.HasInProgressConflict &&
                res.StashExists;

            PreflightSummary = App.Text("ApplyStash.EstimatedFiles", EstimatedPaths.Count);

            OnPropertyChanged(nameof(HasBlockingReasons));
            OnPropertyChanged(nameof(HasCollidingPaths));
            OnPropertyChanged(nameof(HasEstimatedPaths));
            NotifyPopupButtonsChanged();
        }

        private void PopulateResultView(Models.StashApplyResult res)
        {
            ResultLines.Clear();

            if (res.Status == Models.StashApplyStatus.AppliedWithConflictsRetained)
            {
                IsResultConflictRetained = true;
                ResultTitle = App.Text("ApplyStash.Result.ConflictRetainedTitle");
                foreach (var p in res.ConflictedPaths)
                    ResultLines.Add($"[冲突] {p}");
            }
            else if (res.Status == Models.StashApplyStatus.PartialDropFailed)
            {
                IsResultConflictRetained = true;
                ResultTitle = App.Text("ApplyStash.Result.DropFailedTitle");
            }
            else
            {
                IsResultConflictRetained = false;
                ResultTitle = App.Text("ApplyStash.Result.FailedTitle");
            }

            ResultSummary = string.IsNullOrWhiteSpace(res.Summary)
                ? ResultTitle
                : res.Summary;

            HasResultView = true;
        }

        private readonly Repository _repo;
        private Models.StashApplyPreflight _preflight;
        private int _preflightSeq = 0;

        private bool _restoreIndex = true;
        private bool _dropAfterApply = false;
        private bool _allowDirtyWorkingTree = false;
        private bool _showAllowDirtyOption = false;
        private bool _isLoadingPreflight = false;
        private bool _hasResultView = false;
        private bool _isResultConflictRetained = false;
        private string _preflightSummary = string.Empty;
        private string _resultTitle = string.Empty;
        private string _resultSummary = string.Empty;
    }
}
