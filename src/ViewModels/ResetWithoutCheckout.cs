using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace SourceGit.ViewModels
{
    public class ResetWithoutCheckout : Popup
    {
        public Repository Repo => _repo;
        public Models.Branch Target { get; }
        public object To { get; }

        public bool IsLoadingPreflight
        {
            get => _isLoadingPreflight;
            private set
            {
                if (SetProperty(ref _isLoadingPreflight, value))
                {
                    OnPropertyChanged(nameof(ShowConfigurationForm));
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
                    OnPropertyChanged(nameof(ShowConfigurationForm));
                    NotifyPopupButtonsChanged();
                }
            }
        }

        public bool CanExecute => _preflight is { CanExecute: true };
        public bool IsFastForward => _preflight is { IsFastForward: true };
        public bool IsProtectionEffectivelyLocked => _preflight is { BlockedByProtection: true };
        public bool HasBlockReason => !string.IsNullOrWhiteSpace(_blockReason);

        public string BlockReason
        {
            get => _blockReason;
            private set
            {
                if (SetProperty(ref _blockReason, value))
                    OnPropertyChanged(nameof(HasBlockReason));
            }
        }

        public IReadOnlyList<Models.ResetCommitSummary> CommitsToRemove
        {
            get => _commitsToRemove;
            private set
            {
                if (SetProperty(ref _commitsToRemove, value))
                {
                    OnPropertyChanged(nameof(HasCommitsToRemove));
                    OnPropertyChanged(nameof(CommitsPreviewHeader));
                }
            }
        }

        public bool HasCommitsToRemove => _commitsToRemove.Count > 0;
        public string CommitsPreviewHeader => App.Text("ResetWithoutCheckout.CommitsPreview", _totalCommitsToRemove);

        public bool ShowConfigurationForm => !HasResultView && (HasPreflight || !IsLoadingPreflight);

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

        public string ResultSummary
        {
            get => _resultSummary;
            private set => SetProperty(ref _resultSummary, value);
        }

        public string ResultDetail
        {
            get => _resultDetail;
            private set
            {
                if (SetProperty(ref _resultDetail, value))
                    OnPropertyChanged(nameof(HasResultDetail));
            }
        }

        public bool HasResultDetail => !string.IsNullOrWhiteSpace(_resultDetail);

        public string ResultBackupRef
        {
            get => _resultBackupRef;
            private set
            {
                if (SetProperty(ref _resultBackupRef, value))
                    OnPropertyChanged(nameof(HasResultBackupRef));
            }
        }

        public bool HasResultBackupRef => !string.IsNullOrWhiteSpace(_resultBackupRef);

        public Models.HistoryResetOutcome LastOutcome { get; private set; }

        public override bool ShowConfirmButton => !HasResultView;
        public override string ConfirmButtonText => App.Text("ResetWithoutCheckout.Confirm");
        public override string CancelButtonText => HasResultView ? App.Text("Revert.CloseResult") : App.Text("Cancel");
        public override bool CanConfirm => !HasResultView && !IsLoadingPreflight && HasPreflight && CanExecute;

        public ResetWithoutCheckout(Repository repo, Models.Branch target, Models.Branch to, Models.OperationJournalStore journalStore = null)
        {
            _repo = repo;
            _revision = to.Head;
            Target = target;
            To = to;
            _journalStore = journalStore;
            _preflightTask = StartPreflightAsync();
        }

        public ResetWithoutCheckout(Repository repo, Models.Branch target, Models.Commit to, Models.OperationJournalStore journalStore = null)
        {
            _repo = repo;
            _revision = to.SHA;
            Target = target;
            To = to;
            _journalStore = journalStore;
            _preflightTask = StartPreflightAsync();
        }

        public Task EnsureLoadedAsync()
        {
            return _preflightTask ?? Task.CompletedTask;
        }

        public override async Task<bool> Sure()
        {
            await EnsureLoadedAsync().ConfigureAwait(true);

            if (_preflight == null || !_preflight.CanExecute)
            {
                ShowFailure(string.IsNullOrWhiteSpace(_blockReason)
                    ? App.Text("ResetWithoutCheckout.ResultTitle")
                    : _blockReason,
                    string.Empty,
                    string.Empty);
                return false;
            }

            using var lockWatcher = _repo.LockWatcher();
            ProgressDescription = $"Move {Target.Name} to {_revision} ...";

            var log = _repo.CreateLog($"Move '{Target.Name}' to '{_revision}'");
            Use(log);

            var outcome = await Commands.HistoryResetService.ExecuteWithoutCheckoutResetAsync(
                _repo.FullPath,
                new Models.ResetWithoutCheckoutOptions { Preflight = _preflight },
                _journalStore,
                log).ConfigureAwait(true);

            LastOutcome = outcome;
            log.Complete();
            _repo.MarkBranchesDirtyManually();
            await _repo.RefreshRecoveryStatusAsync(_journalStore).ConfigureAwait(true);

            if (outcome.Succeeded)
                return true;

            ShowFailure(outcome.Summary, outcome.DetailMessage, outcome.BackupRefName);
            return false;
        }

        private Task StartPreflightAsync()
        {
            var cts = new CancellationTokenSource();
            _preflightCts = cts;
            return LoadPreflightCoreAsync(cts);
        }

        private async Task LoadPreflightCoreAsync(CancellationTokenSource cts)
        {
            IsLoadingPreflight = true;
            BlockReason = string.Empty;

            try
            {
                var preflight = await Commands.HistoryResetService
                    .InspectWithoutCheckoutResetAsync(_repo.FullPath, Target.Name, _revision, cts.Token)
                    .ConfigureAwait(true);

                if (cts.IsCancellationRequested)
                    return;

                _preflight = preflight;
                _totalCommitsToRemove = preflight.CommitsToRemove.Count;
                CommitsToRemove = preflight.CommitsToRemove.Take(12).ToList();
                BlockReason = preflight.BlockReason ?? string.Empty;
                HasPreflight = true;

                OnPropertyChanged(nameof(CanExecute));
                OnPropertyChanged(nameof(IsFastForward));
                OnPropertyChanged(nameof(IsProtectionEffectivelyLocked));
                NotifyPopupButtonsChanged();
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                if (!cts.IsCancellationRequested)
                {
                    _preflight = null;
                    HasPreflight = false;
                    BlockReason = Models.GitConfigSnapshot.RedactText(ex.Message);
                }
            }
            finally
            {
                if (ReferenceEquals(_preflightCts, cts))
                    _preflightCts = null;
                cts.Dispose();
                IsLoadingPreflight = false;
                NotifyPopupButtonsChanged();
            }
        }

        private void ShowFailure(string summary, string detail, string backupRef)
        {
            ResultSummary = summary ?? string.Empty;
            ResultDetail = detail ?? string.Empty;
            ResultBackupRef = backupRef ?? string.Empty;
            HasResultView = true;
        }

        private readonly Repository _repo;
        private readonly string _revision;
        private readonly Models.OperationJournalStore _journalStore;
        private readonly Task _preflightTask;

        private CancellationTokenSource _preflightCts;
        private Models.ResetWithoutCheckoutPreflight _preflight;
        private int _totalCommitsToRemove;
        private bool _isLoadingPreflight = true;
        private bool _hasPreflight;
        private bool _hasResultView;
        private string _blockReason = string.Empty;
        private string _resultSummary = string.Empty;
        private string _resultDetail = string.Empty;
        private string _resultBackupRef = string.Empty;
        private IReadOnlyList<Models.ResetCommitSummary> _commitsToRemove = [];
    }
}
