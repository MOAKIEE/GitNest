using System.Threading.Tasks;
using Avalonia.Threading;

namespace SourceGit.ViewModels
{
    public class DropStash : Popup
    {
        public Models.Stash Stash { get; }

        public string SourceBranchDisplay => string.IsNullOrEmpty(Stash.SourceBranch)
            ? App.Text("Stash.Meta.ExternalOrNoBranch")
            : Stash.SourceBranch;

        public string StashShortSha => Stash.SHA.Length > 8 ? Stash.SHA[..8] : Stash.SHA;

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
        public override bool CanConfirm => !HasResultView;
        public override string ConfirmButtonText => App.Text("DropStash.Confirm");
        public override string CancelButtonText => HasResultView ? App.Text("Close") : App.Text("Cancel");

        public DropStash(Repository repo, Models.Stash stash)
        {
            _repo = repo;
            Stash = stash;
        }

        public override async Task<bool> Sure()
        {
            if (HasResultView)
                return false;

            using var lockWatcher = _repo.LockWatcher();
            ProgressDescription = $"Drop {Stash.Name} ...";

            var log = _repo.CreateLog("Drop Stash");
            Use(log);

            var res = await Commands.StashWorkflowService.DropStashAsync(
                _repo.FullPath,
                Stash.Name,
                Stash.SHA,
                journalStore: Models.OperationJournalStore.Instance,
                outerLog: log).ConfigureAwait(false);

            log.Complete();
            _repo.RefreshStashes();
            _ = _repo.RefreshRecoveryStatusAsync();

            if (res.Succeeded)
            {
                Dispatcher.UIThread.Post(() => _repo.SendNotification(res.Summary, false));
                return true;
            }

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                ResultSummary = string.IsNullOrWhiteSpace(res.Summary)
                    ? App.Text("DropStash.FailedFallback")
                    : res.Summary;
                HasResultView = true;
            });
            return false;
        }

        private readonly Repository _repo;
        private bool _hasResultView = false;
        private string _resultSummary = string.Empty;
    }
}
