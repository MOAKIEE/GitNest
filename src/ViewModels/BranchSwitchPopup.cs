using System;
using System.Threading.Tasks;
using Avalonia.Collections;
using Avalonia.Threading;

namespace SourceGit.ViewModels
{
    public abstract class BranchSwitchPopup : Popup
    {
        public bool HasLocalChanges => _repo.LocalChangesCount > 0;

        public Models.DealWithLocalChanges DealWithLocalChanges
        {
            get => _dealWithLocalChanges;
            set
            {
                var safe = value == Models.DealWithLocalChanges.Discard
                    ? Models.DealWithLocalChanges.DoNothing
                    : value;
                SetProperty(ref _dealWithLocalChanges, safe);
            }
        }

        public bool HasResultView
        {
            get => _hasResultView;
            protected set
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
        public override bool CanConfirm => !HasResultView;
        public override string CancelButtonText => HasResultView ? App.Text("Close") : App.Text("Cancel");

        protected BranchSwitchPopup(Repository repo)
        {
            _repo = repo;
            _dealWithLocalChanges = Preferences.Instance.UseStashAndReapplyByDefault
                ? Models.DealWithLocalChanges.StashAndReapply
                : Models.DealWithLocalChanges.DoNothing;
        }

        protected async Task<bool> ExecuteSafeSwitchAsync(
            string logTitle,
            string progressDescription,
            string targetDescription,
            string[] checkoutGitArgs,
            Func<Task> onCheckoutSucceededAsync,
            bool skipDetachedHeadCheck = false)
        {
            using var lockWatcher = _repo.LockWatcher();
            ProgressDescription = progressDescription;

            var log = _repo.CreateLog(logTitle);
            Use(log);

            if (!skipDetachedHeadCheck && _repo.CurrentBranch is { IsDetachedHead: true } detached)
            {
                var refs = await new Commands.QueryRefsContainsCommit(_repo.FullPath, detached.Head)
                    .GetResultAsync()
                    .ConfigureAwait(false);
                if (refs.Count == 0)
                {
                    var shouldContinue = await App.AskConfirmAsync(App.Text("Checkout.WarnLostCommits"));
                    if (!shouldContinue)
                    {
                        log.Complete();
                        return true;
                    }
                }
            }

            var res = await Commands.StashWorkflowService.SwitchSafelyAsync(
                _repo.FullPath,
                checkoutGitArgs,
                _dealWithLocalChanges,
                targetDescription,
                journalStore: Models.OperationJournalStore.Instance,
                outerLog: log).ConfigureAwait(false);

            if (res.CheckoutSucceeded)
            {
                await _repo.AutoUpdateSubmodulesAsync(log).ConfigureAwait(false);
                if (onCheckoutSucceededAsync != null)
                    await onCheckoutSucceededAsync( ).ConfigureAwait(false);
            }

            log.Complete();

            _repo.MarkWorkingCopyDirtyManually();
            if (res.StashCreated || res.ReapplyAttempted)
                _repo.RefreshStashes();
            _ = _repo.RefreshInProgressStateAsync();
            _ = _repo.RefreshRecoveryStatusAsync();

            if (res.Success)
            {
                if (res.StashCreated && res.ReapplySucceeded)
                    Dispatcher.UIThread.Post(() => _repo.SendNotification(res.Summary, false));
                return true;
            }

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                ResultLines.Clear();
                IsResultConflictRetained = res.StashRetained || res.HasConflicts;
                ResultTitle = res.StashRetained
                    ? App.Text("Checkout.Result.StashRetainedTitle")
                    : App.Text("Checkout.Result.FailedTitle");
                ResultSummary = string.IsNullOrWhiteSpace(res.Summary)
                    ? ResultTitle
                    : res.Summary;

                foreach (var p in res.ConflictedPaths)
                    ResultLines.Add($"[冲突] {p}");
                if (res.StashRetained && !string.IsNullOrEmpty(res.StashSha))
                {
                    var shortSha = res.StashSha.Length > 8 ? res.StashSha[..8] : res.StashSha;
                    ResultLines.Add(App.Text("Checkout.Result.RetainedStashSha", shortSha));
                }

                HasResultView = true;
            });

            return false;
        }

        protected readonly Repository _repo;
        private Models.DealWithLocalChanges _dealWithLocalChanges;
        private bool _hasResultView = false;
        private bool _isResultConflictRetained = false;
        private string _resultTitle = string.Empty;
        private string _resultSummary = string.Empty;
    }
}
