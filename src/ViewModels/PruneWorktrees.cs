using System;
using System.Threading.Tasks;
using Avalonia.Collections;

namespace SourceGit.ViewModels
{
    public class PruneWorktrees : Popup
    {
        public bool IsLoadingPreview
        {
            get => _isLoadingPreview;
            private set
            {
                if (SetProperty(ref _isLoadingPreview, value))
                    NotifyPopupButtonsChanged();
            }
        }

        public bool HasCandidates => Candidates.Count > 0;
        public bool HasLockedRetainedItems => LockedRetainedItems.Count > 0;

        public string PreviewSummary
        {
            get => _previewSummary;
            private set => SetProperty(ref _previewSummary, value);
        }

        public string InlineErrorMessage
        {
            get => _inlineErrorMessage;
            private set
            {
                if (SetProperty(ref _inlineErrorMessage, value))
                    OnPropertyChanged(nameof(HasInlineError));
            }
        }
        public bool HasInlineError => !string.IsNullOrEmpty(_inlineErrorMessage);

        public AvaloniaList<Models.WorktreePruneCandidate> Candidates { get; } = [];
        public AvaloniaList<Models.WorktreeLockedMissingItem> LockedRetainedItems { get; } = [];

        public override string ConfirmButtonText => App.Text("PruneWorktrees.ConfirmBtn");
        public override bool CanConfirm => !IsLoadingPreview && HasCandidates;

        public Task PreviewTask => _previewTask;

        public PruneWorktrees(Repository repo, Models.OperationJournalStore journalStore = null)
        {
            _repo = repo;
            _journalStore = journalStore ?? Models.OperationJournalStore.Instance;
            _previewTask = LoadPreviewAsync();
        }

        public Task RefreshPreviewAsync() => LoadPreviewAsync();

        public override async Task<bool> Sure()
        {
            await _previewTask;
            if (!CanConfirm)
                return false;

            using var lockWatcher = _repo.LockWatcher();
            ProgressDescription = "正在清理失效工作树管理记录...";
            InlineErrorMessage = string.Empty;

            var log = _repo.CreateLog("Prune Worktrees");
            Use(log);

            try
            {
                var res = await Commands.WorktreeDeletionService.PruneWorktreesAsync(
                    _repo.FullPath,
                    _journalStore,
                    log).ConfigureAwait(true);

                _repo.RefreshTaskOverview();
                _repo.MarkBranchesDirtyManually();

                if (!res.Succeeded)
                {
                    InlineErrorMessage = res.Summary;
                    return false;
                }

                _repo.SendNotification(res.Summary);
                return true;
            }
            finally
            {
                log.Complete();
            }
        }

        private async Task LoadPreviewAsync()
        {
            IsLoadingPreview = true;
            InlineErrorMessage = string.Empty;
            try
            {
                var preview = await Commands.WorktreeDeletionService.PreviewPruneWorktreesAsync(_repo.FullPath)
                    .ConfigureAwait(true);

                Candidates.Clear();
                foreach (var c in preview.Candidates)
                    Candidates.Add(c);

                LockedRetainedItems.Clear();
                foreach (var r in preview.LockedRetainedItems)
                    LockedRetainedItems.Add(r);

                PreviewSummary = preview.ConfirmationNote;
                OnPropertyChanged(nameof(HasCandidates));
                OnPropertyChanged(nameof(HasLockedRetainedItems));
                NotifyPopupButtonsChanged();
            }
            catch (Exception ex)
            {
                InlineErrorMessage = Models.GitConfigSnapshot.RedactText(ex.Message);
            }
            finally
            {
                IsLoadingPreview = false;
            }
        }

        private readonly Repository _repo;
        private readonly Models.OperationJournalStore _journalStore;
        private readonly Task _previewTask;
        private bool _isLoadingPreview;
        private string _previewSummary = string.Empty;
        private string _inlineErrorMessage = string.Empty;
    }
}
