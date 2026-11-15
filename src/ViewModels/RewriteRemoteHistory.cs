using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace SourceGit.ViewModels
{
    public class RewriteRemoteHistory : Popup
    {
        public Repository Repo => _repo;
        public Models.Branch LocalBranch { get; }
        public string TargetCommitSha { get; }
        public string TargetCommitSubject { get; private set; }

        public string TargetTipShortSha =>
            !string.IsNullOrEmpty(TargetCommitSha) && TargetCommitSha.Length > 10
                ? TargetCommitSha[..10]
                : (TargetCommitSha ?? string.Empty);

        public string LiveRemoteTipDisplay
        {
            get
            {
                if (_preflight == null)
                    return "-";
                if (_preflight.RemoteStateUnknown)
                    return App.Text("Reset.RemoteProbeOffline");
                if (!_preflight.ExistsOnServer)
                    return App.Text("DeleteBranch.RemoteAbsentOnServer");
                return _preflight.LiveRemoteTipSha.Length > 10
                    ? _preflight.LiveRemoteTipSha[..10]
                    : _preflight.LiveRemoteTipSha;
            }
        }

        public string RemoteTargetDisplay => _preflight == null
            ? (_remoteName is { Length: > 0 } r && _remoteBranchName is { Length: > 0 } b ? $"{r}/{b}" : "-")
            : (string.IsNullOrEmpty(_preflight.TrackingRefName)
                ? $"{_preflight.RemoteName}/{_preflight.RemoteBranchName}"
                : _preflight.TrackingRefName);

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

        public bool CanRewriteRemote => _preflight is { CanRewriteRemote: true };
        public bool BlockedByProtection => _preflight is { BlockedByProtection: true };
        public bool IsRemoteStateUnknown => _preflight is { RemoteStateUnknown: true };

        public string BlockReason
        {
            get => _blockReason;
            private set
            {
                if (SetProperty(ref _blockReason, value))
                    OnPropertyChanged(nameof(HasBlockReason));
            }
        }

        public bool HasBlockReason => !string.IsNullOrWhiteSpace(_blockReason);

        public string PinnedLeaseNote => _preflight is { ExistsOnServer: true, LiveRemoteTipSha.Length: > 0 }
            ? App.Text("RewriteRemote.PinnedLeaseNote", _preflight.RemoteBranchName, ShortSha(_preflight.LiveRemoteTipSha))
            : string.Empty;

        public bool HasPinnedLeaseNote => CanRewriteRemote && !string.IsNullOrEmpty(PinnedLeaseNote);

        public IReadOnlyList<Models.ResetCommitSummary> CommitsToRemoveOnRemote
        {
            get => _commitsToRemoveOnRemote;
            private set
            {
                if (SetProperty(ref _commitsToRemoveOnRemote, value))
                {
                    OnPropertyChanged(nameof(HasCommitsToRemove));
                    OnPropertyChanged(nameof(CommitsToRemoveHeader));
                }
            }
        }

        public bool HasCommitsToRemove => _commitsToRemoveOnRemote.Count > 0;
        public string CommitsToRemoveHeader => App.Text("RewriteRemote.CommitsToRemove", _totalCommitsToRemove);

        public bool HasOtherAuthorsOnRemote => _preflight is { HasOtherAuthorsOnRemote: true };
        public string RemoteAuthorsSummary => _preflight == null
            ? string.Empty
            : string.Join("、", _preflight.RemoteAuthors.Select(a => a.DisplayText));

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

        public bool IsResultError
        {
            get => _isResultError;
            private set => SetProperty(ref _isResultError, value);
        }

        public bool IsResultWarning
        {
            get => _isResultWarning;
            private set => SetProperty(ref _isResultWarning, value);
        }

        public bool CollaboratorCommitsPreserved
        {
            get => _collaboratorCommitsPreserved;
            private set => SetProperty(ref _collaboratorCommitsPreserved, value);
        }

        public Models.HistoryResetOutcome LastOutcome { get; private set; }

        public override bool ShowConfirmButton => !HasResultView;
        public override string ConfirmButtonText => App.Text("RewriteRemote.Confirm");
        public override string CancelButtonText => HasResultView ? App.Text("Revert.CloseResult") : App.Text("Cancel");
        public override bool CanConfirm => !HasResultView && !IsLoadingPreflight && HasPreflight && CanRewriteRemote;

        public RewriteRemoteHistory(
            Repository repo,
            Models.Branch localBranch,
            Models.Commit targetCommit = null,
            string remoteName = null,
            string remoteBranchName = null,
            Models.OperationJournalStore journalStore = null)
        {
            _repo = repo;
            LocalBranch = localBranch ?? throw new ArgumentNullException(nameof(localBranch));
            TargetCommitSha = targetCommit?.SHA ?? localBranch.Head;
            TargetCommitSubject = targetCommit?.Subject ?? string.Empty;
            _remoteName = remoteName;
            _remoteBranchName = remoteBranchName;
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

            if (_preflight == null || !_preflight.CanRewriteRemote)
            {
                ShowOutcome(new Models.HistoryResetOutcome
                {
                    Succeeded = false,
                    Summary = string.IsNullOrWhiteSpace(_blockReason)
                        ? App.Text("RewriteRemote.ProtectionHint")
                        : _blockReason,
                });
                return false;
            }

            using var lockWatcher = _repo.LockWatcher();
            ProgressDescription = $"Rewrite remote branch {RemoteTargetDisplay} -> {TargetTipShortSha} ...";

            var log = _repo.CreateLog($"Rewrite remote history ({RemoteTargetDisplay})");
            Use(log);

            var outcome = await Commands.HistoryResetService.ExecuteRemoteRewriteOnlyAsync(
                _repo.FullPath,
                new Models.RemoteRewriteOptions { Preflight = _preflight },
                _journalStore,
                log).ConfigureAwait(true);

            LastOutcome = outcome;
            log.Complete();
            await _repo.RefreshRecoveryStatusAsync(_journalStore).ConfigureAwait(true);

            if (outcome.Succeeded && !outcome.RemotePushOutcomeUncertain)
                return true;

            ShowOutcome(outcome);
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
                    .InspectRemoteRewriteForTargetAsync(
                        _repo.FullPath,
                        LocalBranch.Name,
                        TargetCommitSha,
                        _remoteName,
                        _remoteBranchName,
                        cts.Token)
                    .ConfigureAwait(true);

                if (cts.IsCancellationRequested)
                    return;

                _preflight = preflight;
                if (string.IsNullOrEmpty(TargetCommitSubject) && !string.IsNullOrEmpty(preflight.TargetCommitSubject))
                {
                    TargetCommitSubject = preflight.TargetCommitSubject;
                    OnPropertyChanged(nameof(TargetCommitSubject));
                }

                _totalCommitsToRemove = preflight.CommitsToRemoveOnRemote.Count;
                CommitsToRemoveOnRemote = preflight.CommitsToRemoveOnRemote.Take(12).ToList();
                BlockReason = preflight.BlockReason ?? string.Empty;
                HasPreflight = true;

                OnPropertyChanged(nameof(RemoteTargetDisplay));
                OnPropertyChanged(nameof(LiveRemoteTipDisplay));
                OnPropertyChanged(nameof(CanRewriteRemote));
                OnPropertyChanged(nameof(BlockedByProtection));
                OnPropertyChanged(nameof(IsRemoteStateUnknown));
                OnPropertyChanged(nameof(PinnedLeaseNote));
                OnPropertyChanged(nameof(HasPinnedLeaseNote));
                OnPropertyChanged(nameof(HasOtherAuthorsOnRemote));
                OnPropertyChanged(nameof(RemoteAuthorsSummary));
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

        private void ShowOutcome(Models.HistoryResetOutcome outcome)
        {
            IsResultWarning = outcome.RemotePushOutcomeUncertain || outcome.RejectedByRemoteLease;
            IsResultError = !outcome.Succeeded && !IsResultWarning;
            CollaboratorCommitsPreserved = outcome.CollaboratorCommitsPreserved;
            ResultSummary = outcome.Summary ?? string.Empty;
            ResultDetail = outcome.DetailMessage ?? string.Empty;
            HasResultView = true;
        }

        private static string ShortSha(string sha)
        {
            if (string.IsNullOrEmpty(sha))
                return "-";
            return sha.Length > 10 ? sha[..10] : sha;
        }

        private readonly Repository _repo;
        private readonly string _remoteName;
        private readonly string _remoteBranchName;
        private readonly Models.OperationJournalStore _journalStore;
        private readonly Task _preflightTask;

        private CancellationTokenSource _preflightCts;
        private Models.RemoteRewritePreflight _preflight;
        private int _totalCommitsToRemove;
        private bool _isLoadingPreflight = true;
        private bool _hasPreflight;
        private bool _hasResultView;
        private bool _isResultError;
        private bool _isResultWarning;
        private bool _collaboratorCommitsPreserved;
        private string _blockReason = string.Empty;
        private string _resultSummary = string.Empty;
        private string _resultDetail = string.Empty;
        private IReadOnlyList<Models.ResetCommitSummary> _commitsToRemoveOnRemote = [];
    }
}
