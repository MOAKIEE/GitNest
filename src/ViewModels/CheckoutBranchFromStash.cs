using System.ComponentModel.DataAnnotations;
using System.Threading.Tasks;
using Avalonia.Threading;

namespace SourceGit.ViewModels
{
    public class CheckoutBranchFromStash : Popup
    {
        public Models.Stash Target { get; }

        public string SourceBranchDisplay => string.IsNullOrEmpty(Target.SourceBranch)
            ? App.Text("Stash.Meta.ExternalOrNoBranch")
            : Target.SourceBranch;

        [Required(ErrorMessage = "Branch name is required!!!")]
        [CustomValidation(typeof(CheckoutBranchFromStash), nameof(ValidateBranchName))]
        public string BranchName
        {
            get => _branchName;
            set => SetProperty(ref _branchName, value, true);
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

        public string ResultSummary
        {
            get => _resultSummary;
            private set => SetProperty(ref _resultSummary, value);
        }

        public override bool ShowConfirmButton => !HasResultView;
        public override bool CanConfirm => !HasResultView;
        public override string ConfirmButtonText => App.Text("CheckoutBranchFromStash.Confirm");
        public override string CancelButtonText => HasResultView ? App.Text("Close") : App.Text("Cancel");

        public CheckoutBranchFromStash(Repository repo, Models.Stash stash)
        {
            _repo = repo;
            Target = stash;
        }

        public static ValidationResult ValidateBranchName(string name, ValidationContext ctx)
        {
            if (ctx.ObjectInstance is CheckoutBranchFromStash creator)
            {
                foreach (var b in creator._repo.Branches)
                {
                    if (b.FriendlyName == name)
                        return new ValidationResult("A branch with same name already exists!!!");
                }
            }

            return ValidationResult.Success;
        }

        public override async Task<bool> Sure()
        {
            if (HasResultView)
                return false;

            if (!Check())
                return false;

            using var lockWatcher = _repo.LockWatcher();
            ProgressDescription = $"Create branch '{_branchName}' from stash ...";

            var log = _repo.CreateLog("Checkout Branch from Stash");
            Use(log);

            var res = await Commands.StashWorkflowService.CheckoutBranchFromStashAsync(
                _repo.FullPath,
                Target.Name,
                _branchName,
                Target.SHA,
                journalStore: Models.OperationJournalStore.Instance,
                outerLog: log).ConfigureAwait(false);

            log.Complete();

            _repo.MarkBranchesDirtyManually();
            _repo.MarkWorkingCopyDirtyManually();
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
                    ? App.Text("CheckoutBranchFromStash.FailedFallback")
                    : res.Summary;
                HasResultView = true;
            });
            return false;
        }

        private readonly Repository _repo;
        private string _branchName = string.Empty;
        private bool _hasResultView = false;
        private string _resultSummary = string.Empty;
    }
}
