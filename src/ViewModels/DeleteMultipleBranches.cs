using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Collections;

namespace SourceGit.ViewModels
{
    public sealed class BatchBranchDeleteResultItem
    {
        public BatchBranchDeleteResultItem(Models.BranchDeleteOutcome outcome)
        {
            BranchName = string.IsNullOrEmpty(outcome.RemoteName)
                ? outcome.BranchName
                : $"{outcome.RemoteName}/{outcome.RemoteBranchName}";
            Succeeded = outcome.Succeeded;
            Summary = outcome.Summary;
            IconKey = outcome.Succeeded ? "Icons.CheckCircled" : "Icons.Error";
            IconBrush = outcome.Succeeded
                ? Avalonia.Media.Brushes.ForestGreen
                : Avalonia.Media.Brushes.OrangeRed;
        }

        public string BranchName { get; }
        public bool Succeeded { get; }
        public string Summary { get; }
        public string IconKey { get; }
        public Avalonia.Media.IBrush IconBrush { get; }
    }

    public class DeleteMultipleBranches : Popup
    {
        public List<Models.Branch> Targets { get; }

        public bool IsLocal => _isLocal;

        public bool Force
        {
            get => _force;
            set => SetProperty(ref _force, value);
        }

        public bool HasResultView
        {
            get => _hasResultView;
            private set
            {
                if (SetProperty(ref _hasResultView, value))
                {
                    OnPropertyChanged(nameof(ShowConfigurationForm));
                    NotifyPopupButtonsChanged();
                }
            }
        }
        public bool ShowConfigurationForm => !HasResultView;

        public string ResultSummary
        {
            get => _resultSummary;
            private set => SetProperty(ref _resultSummary, value);
        }

        public AvaloniaList<BatchBranchDeleteResultItem> ResultItems { get; } = [];

        public override string ConfirmButtonText => _isLocal
            ? App.Text("DeleteMultiBranch.BtnDeleteLocal")
            : App.Text("DeleteMultiBranch.BtnDeleteRemote");

        public override string CancelButtonText => HasResultView ? App.Text("Close") : App.Text("Cancel");
        public override bool ShowConfirmButton => !HasResultView;

        public DeleteMultipleBranches(
            Repository repo,
            List<Models.Branch> branches,
            bool isLocal,
            Models.OperationJournalStore journalStore = null)
        {
            _repo = repo;
            _isLocal = isLocal;
            _journalStore = journalStore ?? Models.OperationJournalStore.Instance;
            Targets = branches ?? [];
        }

        public override async Task<bool> Sure()
        {
            using var lockWatcher = _repo.LockWatcher();
            ProgressDescription = _isLocal ? "正在批量删除本地分支..." : "正在批量删除远程分支...";

            var log = _repo.CreateLog("Delete Multiple Branches");
            Use(log);

            try
            {
                var outcome = await Commands.BranchDeletionService.DeleteMultipleBranchesAsync(
                    _repo.FullPath,
                    Targets,
                    _isLocal,
                    _force,
                    _journalStore,
                    log).ConfigureAwait(true);

                foreach (var item in outcome.Items)
                {
                    if (item.LocalBranchDeleted)
                        _repo.UIStates.RemoveHistoryFilter($"refs/heads/{item.BranchName}", Models.FilterType.LocalBranch);
                    if (item.RemoteBranchDeleted)
                        _repo.UIStates.RemoveHistoryFilter($"refs/remotes/{item.RemoteName}/{item.RemoteBranchName}", Models.FilterType.RemoteBranch);
                }

                _repo.MarkBranchesDirtyManually();

                if (outcome.AllSucceeded)
                {
                    _repo.SendNotification(outcome.Summary);
                    return true;
                }

                ResultSummary = outcome.Summary;
                ResultItems.Clear();
                foreach (var item in outcome.Items)
                    ResultItems.Add(new BatchBranchDeleteResultItem(item));

                HasResultView = true;
                return false;
            }
            finally
            {
                log.Complete();
            }
        }

        private readonly Repository _repo;
        private readonly bool _isLocal;
        private readonly Models.OperationJournalStore _journalStore;
        private bool _force;
        private bool _hasResultView;
        private string _resultSummary = string.Empty;
    }
}
