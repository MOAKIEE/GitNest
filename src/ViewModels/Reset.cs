using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace SourceGit.ViewModels
{
    public class Reset : Popup
    {
        public Repository Repo => _repo;
        public Models.Branch Current { get; }
        public Models.Commit To { get; }

        public string TargetCommitShortSha =>
            !string.IsNullOrEmpty(To?.SHA) && To.SHA.Length > 10 ? To.SHA[..10] : (To?.SHA ?? string.Empty);

        public string CurrentBranchDisplay => string.IsNullOrWhiteSpace(_currentBranchDisplay)
            ? (Current?.Name ?? "HEAD")
            : _currentBranchDisplay;

        public Models.ResetCommitMode SelectedMode
        {
            get => _selectedMode;
            set
            {
                if (SetProperty(ref _selectedMode, value))
                {
                    OnPropertyChanged(nameof(IsMixedMode));
                    OnPropertyChanged(nameof(IsHardMode));
                    NotifyPopupButtonsChanged();
                }
            }
        }

        public bool IsMixedMode
        {
            get => _selectedMode == Models.ResetCommitMode.KeepCodeMixed;
            set
            {
                if (value)
                    SelectedMode = Models.ResetCommitMode.KeepCodeMixed;
            }
        }

        public bool IsHardMode
        {
            get => _selectedMode == Models.ResetCommitMode.DiscardCodeHard;
            set
            {
                if (value)
                    SelectedMode = Models.ResetCommitMode.DiscardCodeHard;
            }
        }

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

        public bool CanResetLocally => _preflight is { CanResetLocally: true };
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

        public IReadOnlyList<string> CollisionPaths
        {
            get => _collisionPaths;
            private set
            {
                if (SetProperty(ref _collisionPaths, value))
                    OnPropertyChanged(nameof(HasCollisionPaths));
            }
        }

        public bool HasCollisionPaths => _collisionPaths.Count > 0;

        public IReadOnlyList<Models.ResetCommitSummary> LocalCommitsToRemove
        {
            get => _localCommitsToRemove;
            private set
            {
                if (SetProperty(ref _localCommitsToRemove, value))
                {
                    OnPropertyChanged(nameof(HasLocalCommitsToRemove));
                    OnPropertyChanged(nameof(CommitsToRemoveHeader));
                }
            }
        }

        public bool HasLocalCommitsToRemove => _localCommitsToRemove.Count > 0;
        public string CommitsToRemoveHeader => App.Text("Reset.CommitsToRemove", _totalLocalCommitsToRemove);

        public bool HasCrossMergeNotice => _preflight is { MergedSideCommitsToRemoveCount: > 0 };
        public string CrossMergeNoticeText => _preflight == null
            ? string.Empty
            : App.Text("Reset.CrossMergeNotice", _preflight.FirstParentCommitsToRemoveCount, _preflight.MergedSideCommitsToRemoveCount);

        public bool HasOtherAuthorsInLocal => _preflight is { HasOtherAuthorsInLocal: true };
        public string LocalAuthorsSummary => _preflight == null
            ? string.Empty
            : string.Join("、", _preflight.LocalAuthors.Select(a => a.DisplayText));

        public bool IsProtectedByDefault => _preflight?.Protection is { IsProtectedByDefault: true };
        public bool IsProtectionEffectivelyLocked => _preflight?.Protection is { IsEffectivelyProtected: true };
        public bool IsProtectionLocallyUnlocked => _preflight?.Protection is { IsProtectedByDefault: true, IsEffectivelyProtected: false };
        public string ProtectionSummary => _preflight?.Protection?.Summary ?? string.Empty;

        public bool HasConfiguredUpstream => _preflight?.ConfiguredUpstream != null;
        public Models.ResetUpstreamRemoteStatus ConfiguredUpstream => _preflight?.ConfiguredUpstream;
        public bool HasDistinctConfiguredUpstreamDetail
        {
            get
            {
                var u = ConfiguredUpstream;
                if (u == null || string.IsNullOrWhiteSpace(u.Detail))
                    return false;

                var redundant = $"{u.RemoteName}/{u.RemoteBranchName}: {u.RemoteStatusLabel}";
                return !string.Equals(u.Detail.Trim(), redundant, StringComparison.Ordinal) &&
                       !string.Equals(u.Detail.Trim(), u.RemoteStatusLabel?.Trim(), StringComparison.Ordinal);
            }
        }
        public bool IsRemoteStateUnknown => _preflight?.ConfiguredUpstream is { RemoteStateUnknown: true };
        public bool HasAdditionalTrackedRemotes => _additionalTrackedRemotes.Count > 0;
        public IReadOnlyList<Models.ResetUpstreamRemoteStatus> AdditionalTrackedRemotes
        {
            get => _additionalTrackedRemotes;
            private set
            {
                if (SetProperty(ref _additionalTrackedRemotes, value))
                    OnPropertyChanged(nameof(HasAdditionalTrackedRemotes));
            }
        }

        public bool CanEnableAlsoRewriteRemote =>
            !HasResultView &&
            !IsLoadingPreflight &&
            CanResetLocally &&
            HasConfiguredUpstream &&
            _remoteRewritePreflight is { CanRewriteRemote: true };

        public bool AlsoRewriteRemote
        {
            get => _alsoRewriteRemote;
            set
            {
                var next = value && CanEnableAlsoRewriteRemote;
                if (SetProperty(ref _alsoRewriteRemote, next))
                    NotifyPopupButtonsChanged();
            }
        }

        public string AlsoRewriteRemoteLabel
        {
            get
            {
                var sha = _remoteRewritePreflight?.LiveRemoteTipSha;
                var shortSha = !string.IsNullOrEmpty(sha) && sha.Length > 10 ? sha[..10] : (sha ?? "...");
                return App.Text("Reset.AlsoRewriteRemote", shortSha);
            }
        }

        public string RemoteRewriteHintText
        {
            get => _remoteRewriteHintText;
            private set
            {
                if (SetProperty(ref _remoteRewriteHintText, value))
                    OnPropertyChanged(nameof(HasRemoteRewriteHint));
            }
        }

        public bool HasRemoteRewriteHint => !string.IsNullOrWhiteSpace(_remoteRewriteHintText);

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

        public IReadOnlyList<Models.HistoryResetStepResult> ResultSteps
        {
            get => _resultSteps;
            private set
            {
                if (SetProperty(ref _resultSteps, value))
                    OnPropertyChanged(nameof(HasResultSteps));
            }
        }

        public bool HasResultSteps => _resultSteps.Count > 0;

        public override bool ShowConfirmButton => !HasResultView;
        public override string ConfirmButtonText => AlsoRewriteRemote ? App.Text("Reset.ConfirmWithRemote") : App.Text("Reset.Confirm");
        public override string CancelButtonText => HasResultView ? App.Text("Revert.CloseResult") : App.Text("Cancel");
        public override bool CanConfirm =>
            !HasResultView &&
            !IsLoadingPreflight &&
            HasPreflight &&
            CanResetLocally &&
            (!AlsoRewriteRemote || (_remoteRewritePreflight?.CanRewriteRemote ?? false));

        public Models.HistoryResetOutcome LastOutcome { get; private set; }

        public Reset(
            Repository repo,
            Models.Branch current,
            Models.Commit to,
            Models.OperationJournalStore journalStore = null)
        {
            _repo = repo;
            Current = current;
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

            if (_preflight == null || !_preflight.CanResetLocally)
            {
                ShowOutcomeCard(new Models.HistoryResetOutcome
                {
                    Succeeded = false,
                    Summary = string.IsNullOrWhiteSpace(_blockReason)
                        ? App.Text("Reset.Result.FailedTitle")
                        : _blockReason,
                });
                return false;
            }

            if (AlsoRewriteRemote && _remoteRewritePreflight is not { CanRewriteRemote: true })
            {
                ShowOutcomeCard(new Models.HistoryResetOutcome
                {
                    Succeeded = false,
                    Summary = _remoteRewritePreflight?.BlockReason ?? App.Text("Reset.Result.FailedTitle"),
                });
                return false;
            }

            using var lockWatcher = _repo.LockWatcher();
            ProgressDescription = $"Return current branch to {TargetCommitShortSha} ...";

            var log = _repo.CreateLog($"Return HEAD to '{TargetCommitShortSha}'");
            Use(log);

            var outcome = await Commands.HistoryResetService.ExecuteCommitResetAsync(
                _repo.FullPath,
                new Models.HistoryResetOptions
                {
                    Mode = SelectedMode,
                    Preflight = _preflight,
                    AlsoRewriteRemote = AlsoRewriteRemote,
                    RemoteRewritePreflight = AlsoRewriteRemote ? _remoteRewritePreflight : null,
                },
                _journalStore,
                log).ConfigureAwait(true);

            LastOutcome = outcome;

            if (outcome.LocalBranchReset)
                await _repo.AutoUpdateSubmodulesAsync(log).ConfigureAwait(true);

            log.Complete();
            await _repo.RefreshRecoveryStatusAsync(_journalStore).ConfigureAwait(true);

            if (outcome.Succeeded && !outcome.IsPartialSuccess && !outcome.RemotePushOutcomeUncertain)
                return true;

            ShowOutcomeCard(outcome);
            return false;
        }

        private Task StartPreflightAsync()
        {
            CancelPreflight();
            var version = Interlocked.Increment(ref _preflightVersion);
            var cts = new CancellationTokenSource();
            _preflightCts = cts;
            return LoadPreflightCoreAsync(version, cts);
        }

        private async Task LoadPreflightCoreAsync(int version, CancellationTokenSource cts)
        {
            IsLoadingPreflight = true;
            BlockReason = string.Empty;

            try
            {
                var preflight = await Commands.HistoryResetService
                    .InspectCommitResetAsync(_repo.FullPath, To.SHA, probeRemote: true, cts.Token)
                    .ConfigureAwait(true);

                if (version != _preflightVersion || cts.IsCancellationRequested)
                    return;

                Models.RemoteRewritePreflight remoteRewrite = null;
                if (preflight.CanResetLocally && preflight.ConfiguredUpstream != null && !string.IsNullOrWhiteSpace(preflight.LocalBranchName))
                {
                    remoteRewrite = await Commands.HistoryResetService
                        .InspectRemoteRewriteForTargetAsync(
                            _repo.FullPath,
                            preflight.LocalBranchName,
                            To.SHA,
                            preflight.ConfiguredUpstream.RemoteName,
                            preflight.ConfiguredUpstream.RemoteBranchName,
                            cts.Token)
                        .ConfigureAwait(true);
                }

                if (version != _preflightVersion || cts.IsCancellationRequested)
                    return;

                ApplyPreflight(preflight, remoteRewrite);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                if (version != _preflightVersion || cts.IsCancellationRequested)
                    return;

                _preflight = null;
                _remoteRewritePreflight = null;
                HasPreflight = false;
                BlockReason = Models.GitConfigSnapshot.RedactText(ex.Message);
            }
            finally
            {
                if (ReferenceEquals(_preflightCts, cts))
                    _preflightCts = null;
                cts.Dispose();

                if (version == _preflightVersion)
                {
                    IsLoadingPreflight = false;
                    NotifyPopupButtonsChanged();
                }
            }
        }

        private void ApplyPreflight(Models.HistoryResetPreflight preflight, Models.RemoteRewritePreflight remoteRewrite)
        {
            _preflight = preflight;
            _remoteRewritePreflight = remoteRewrite;
            _currentBranchDisplay = preflight.IsDetachedHead
                ? $"HEAD ({ShortSha(preflight.CurrentHeadSha)})"
                : (string.IsNullOrWhiteSpace(preflight.LocalBranchName) ? Current?.Name ?? "HEAD" : preflight.LocalBranchName);

            _totalLocalCommitsToRemove = preflight.LocalCommitsToRemove.Count;
            LocalCommitsToRemove = preflight.LocalCommitsToRemove.Take(12).ToList();
            CollisionPaths = preflight.CollisionPaths.Take(20).ToList();
            AdditionalTrackedRemotes = preflight.AdditionalTrackedRemotes;
            BlockReason = preflight.BlockReason ?? string.Empty;

            if (preflight.ConfiguredUpstream == null)
            {
                RemoteRewriteHintText = App.Text("Reset.NoUpstreamRemote");
            }
            else if (remoteRewrite is { CanRewriteRemote: false } && !string.IsNullOrWhiteSpace(remoteRewrite.BlockReason))
            {
                RemoteRewriteHintText = remoteRewrite.BlockReason;
            }
            else if (preflight.ConfiguredUpstream.RemoteStateUnknown)
            {
                RemoteRewriteHintText = App.Text("Reset.RemoteProbeOffline");
            }
            else
            {
                RemoteRewriteHintText = string.Empty;
            }

            if (_alsoRewriteRemote && !CanEnableAlsoRewriteRemote)
                _alsoRewriteRemote = false;

            HasPreflight = true;

            OnPropertyChanged(nameof(CurrentBranchDisplay));
            OnPropertyChanged(nameof(CanResetLocally));
            OnPropertyChanged(nameof(HasCrossMergeNotice));
            OnPropertyChanged(nameof(CrossMergeNoticeText));
            OnPropertyChanged(nameof(HasOtherAuthorsInLocal));
            OnPropertyChanged(nameof(LocalAuthorsSummary));
            OnPropertyChanged(nameof(IsProtectedByDefault));
            OnPropertyChanged(nameof(IsProtectionEffectivelyLocked));
            OnPropertyChanged(nameof(IsProtectionLocallyUnlocked));
            OnPropertyChanged(nameof(ProtectionSummary));
            OnPropertyChanged(nameof(HasConfiguredUpstream));
            OnPropertyChanged(nameof(ConfiguredUpstream));
            OnPropertyChanged(nameof(HasDistinctConfiguredUpstreamDetail));
            OnPropertyChanged(nameof(IsRemoteStateUnknown));
            OnPropertyChanged(nameof(CanEnableAlsoRewriteRemote));
            OnPropertyChanged(nameof(AlsoRewriteRemote));
            OnPropertyChanged(nameof(AlsoRewriteRemoteLabel));
            NotifyPopupButtonsChanged();
        }

        private void ShowOutcomeCard(Models.HistoryResetOutcome outcome)
        {
            if (outcome.IsPartialSuccess)
            {
                ResultTitle = App.Text("Reset.Result.PartialTitle");
                IsResultWarning = true;
                IsResultError = false;
            }
            else if (outcome.RemotePushOutcomeUncertain)
            {
                ResultTitle = App.Text("Reset.Result.UncertainTitle");
                IsResultWarning = true;
                IsResultError = false;
            }
            else if (outcome.Succeeded)
            {
                ResultTitle = App.Text("Reset.Result.SuccessTitle");
                IsResultWarning = false;
                IsResultError = false;
            }
            else
            {
                ResultTitle = App.Text("Reset.Result.FailedTitle");
                IsResultWarning = false;
                IsResultError = true;
            }

            ResultSummary = outcome.Summary ?? string.Empty;
            var steps = outcome.StepResults ?? [];
            var detail = outcome.DetailMessage ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(detail) &&
                steps.Any(s => !string.IsNullOrWhiteSpace(s.Detail) &&
                               s.Detail.Contains(detail.Trim(), StringComparison.Ordinal)))
            {
                detail = string.Empty;
            }

            ResultDetail = detail;
            CollaboratorCommitsPreserved = outcome.CollaboratorCommitsPreserved;
            ResultBackupRef = outcome.BackupRefName ?? string.Empty;
            ResultSteps = steps;
            HasResultView = true;
        }

        private void CancelPreflight()
        {
            try
            {
                _preflightCts?.Cancel();
            }
            catch
            {
            }
        }

        private static string ShortSha(string sha)
        {
            if (string.IsNullOrEmpty(sha))
                return "-";
            return sha.Length > 10 ? sha[..10] : sha;
        }

        private readonly Repository _repo;
        private readonly Models.OperationJournalStore _journalStore;
        private readonly Task _preflightTask;

        private Models.ResetCommitMode _selectedMode = Models.ResetCommitMode.KeepCodeMixed;
        private Models.HistoryResetPreflight _preflight;
        private Models.RemoteRewritePreflight _remoteRewritePreflight;
        private CancellationTokenSource _preflightCts;
        private int _preflightVersion;
        private int _totalLocalCommitsToRemove;

        private bool _isLoadingPreflight = true;
        private bool _hasPreflight;
        private bool _alsoRewriteRemote;
        private bool _hasResultView;
        private bool _isResultError;
        private bool _isResultWarning;
        private bool _collaboratorCommitsPreserved;

        private string _currentBranchDisplay = string.Empty;
        private string _blockReason = string.Empty;
        private string _remoteRewriteHintText = string.Empty;
        private string _resultTitle = string.Empty;
        private string _resultSummary = string.Empty;
        private string _resultDetail = string.Empty;
        private string _resultBackupRef = string.Empty;
        private IReadOnlyList<string> _collisionPaths = [];
        private IReadOnlyList<Models.ResetCommitSummary> _localCommitsToRemove = [];
        private IReadOnlyList<Models.ResetUpstreamRemoteStatus> _additionalTrackedRemotes = [];
        private IReadOnlyList<Models.HistoryResetStepResult> _resultSteps = [];
    }
}
