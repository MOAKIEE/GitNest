using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace SourceGit.ViewModels
{
    public class AmendLastCommit : Popup
    {
        public Repository Repo => _repo;
        public Models.AmendLastCommitPreflight Preflight => _preflight;
        public Models.AmendLastCommitOutcome LastOutcome => _lastOutcome;
        public Models.CommitAndPushSession RetrySession => _retrySession;

        public bool IsLoadingPreflight
        {
            get => _isLoadingPreflight;
            private set
            {
                if (SetProperty(ref _isLoadingPreflight, value))
                    NotifyStatePropertiesChanged();
            }
        }

        public bool HasPreflight
        {
            get => _hasPreflight;
            private set
            {
                if (SetProperty(ref _hasPreflight, value))
                    NotifyStatePropertiesChanged();
            }
        }

        public bool IsFetching
        {
            get => _isFetching;
            private set
            {
                if (SetProperty(ref _isFetching, value))
                {
                    OnPropertyChanged(nameof(CanFetchAndReinspect));
                    OnPropertyChanged(nameof(FetchButtonText));
                }
            }
        }

        public string CommitMessage
        {
            get => _commitMessage;
            set
            {
                if (SetProperty(ref _commitMessage, value))
                {
                    OnPropertyChanged(nameof(CanRestoreOriginalMessage));
                    NotifyPopupButtonsChanged();
                }
            }
        }

        public bool SignOff
        {
            get => _signOff;
            set => SetProperty(ref _signOff, value);
        }

        public bool NoVerify
        {
            get => _noVerify;
            set => SetProperty(ref _noVerify, value);
        }

        public bool ResetAuthor
        {
            get => _resetAuthor;
            set => SetProperty(ref _resetAuthor, value);
        }

        public bool ConfirmRewritePushedWithLease
        {
            get => _confirmRewritePushedWithLease;
            set
            {
                if (SetProperty(ref _confirmRewritePushedWithLease, value))
                {
                    OnPropertyChanged(nameof(GuideNewCommitAsPrimary));
                    OnPropertyChanged(nameof(GuideNewCommitAsSecondary));
                    NotifyPopupButtonsChanged();
                }
            }
        }

        public bool HasResultView
        {
            get => _hasResultView;
            private set
            {
                if (SetProperty(ref _hasResultView, value))
                    NotifyStatePropertiesChanged();
            }
        }

        public bool IsPartialFailureView
        {
            get => _isPartialFailureView;
            private set
            {
                if (SetProperty(ref _isPartialFailureView, value))
                    NotifyStatePropertiesChanged();
            }
        }

        public bool HasHardBlocker => !IsLoadingPreflight && HasPreflight && _preflight is { HasHardBlocker: true };
        public bool ShowConfigurationForm => !IsLoadingPreflight && HasPreflight && !HasHardBlocker && !HasResultView;
        public bool ShowSummaryCard => !IsLoadingPreflight && HasPreflight && !HasResultView;
        public bool IsErrorResultView => HasResultView && !IsPartialFailureView;
        public bool IsPushRetryOnlyState => HasResultView && IsPartialFailureView && _retrySession is { CanRetryPushOnly: true };

        public string BlockReason => _preflight?.BlockReason ?? string.Empty;

        public string TargetBranchDisplay
        {
            get
            {
                if (_preflight == null)
                    return "-";
                if (_preflight.IsDetachedHead)
                    return App.Text("AmendLastCommit.DetachedBranch");
                return string.IsNullOrEmpty(_preflight.CurrentBranchName) ? "-" : _preflight.CurrentBranchName;
            }
        }

        public string HeadShortSha => _preflight?.HeadShortSha ?? "-";
        public string HeadSubject => _preflight?.HeadSubject ?? string.Empty;
        public string HeadAuthorDisplay => _preflight?.HeadAuthorDisplay ?? string.Empty;
        public bool HasHeadAuthor => !string.IsNullOrWhiteSpace(HeadAuthorDisplay);

        public bool HasRemoteEndpoint => _preflight is { RemoteName.Length: > 0, RemoteBranchName.Length: > 0 };
        public string RemoteTargetDisplay => HasRemoteEndpoint
            ? $"{_preflight.RemoteName}/{_preflight.RemoteBranchName}"
            : "-";

        public string LiveRemoteTipDisplay
        {
            get
            {
                if (_preflight == null)
                    return "-";
                if (_preflight.RemotePublishState == Models.AmendRemotePublishState.UnknownRemoteState)
                    return App.Text("Reset.RemoteProbeOffline");
                if (string.IsNullOrEmpty(_preflight.LiveRemoteTipSha))
                    return App.Text("DeleteBranch.RemoteAbsentOnServer");
                return ShortSha(_preflight.LiveRemoteTipSha);
            }
        }

        public bool IsUnpushedLocalOnly => _preflight?.RemotePublishState == Models.AmendRemotePublishState.UnpushedLocalOnly;
        public bool IsAlreadyPushed => _preflight?.RemotePublishState == Models.AmendRemotePublishState.AlreadyPushed;
        public bool IsCautionRemoteBanner => _preflight != null && !IsUnpushedLocalOnly && !IsBlockedRemoteDanger;
        public bool IsBlockedRemoteDanger => _preflight != null && (
            _preflight.BlockedByBranchProtection ||
            _preflight.RemoteMovedAheadOfHead ||
            _preflight.RemotePublishState is Models.AmendRemotePublishState.UnknownRemoteState
                or Models.AmendRemotePublishState.UnfetchedOrStaleRemote);

        public bool RequiresRemoteWarningCard => _preflight != null && !IsUnpushedLocalOnly;
        public bool BlockedByBranchProtection => _preflight is { BlockedByBranchProtection: true };
        public bool HasOtherAuthorsOnRemote => _preflight is { HasOtherAuthorsOnRemote: true };
        public string RemoteAuthorsSummary => _preflight?.RemoteAuthorsSummary ?? string.Empty;
        public string RemotePublishSummary => _preflight?.RemotePublishSummary ?? string.Empty;
        public string RemotePublishDetail => _preflight?.RemotePublishDetail ?? string.Empty;
        public bool HasRemotePublishDetail => !string.IsNullOrWhiteSpace(RemotePublishDetail);

        public bool CanOptInPushedRewrite => _preflight is { CanOptInPushedRewrite: true };
        public bool GuideNewCommitAsPrimary => !CanOptInPushedRewrite || !ConfirmRewritePushedWithLease;
        public bool GuideNewCommitAsSecondary => CanOptInPushedRewrite && ConfirmRewritePushedWithLease;

        public string RewritePushedOptInText => _preflight is { CanOptInPushedRewrite: true }
            ? App.Text("AmendLastCommit.RewritePushedOptIn", RemoteTargetDisplay, _preflight.RemoteBranchName, ShortSha(_preflight.LiveRemoteTipSha))
            : string.Empty;

        public bool CanFetchAndReinspect => _preflight is { HasRemotes: true } &&
            _preflight.RemotePublishState is Models.AmendRemotePublishState.UnknownRemoteState
                or Models.AmendRemotePublishState.UnfetchedOrStaleRemote
                or Models.AmendRemotePublishState.AlreadyPushed;

        public string FetchButtonText => IsFetching
            ? App.Text("AmendLastCommit.Fetching")
            : App.Text("AmendLastCommit.FetchAndReinspect");

        public bool HasNewlyStagedChanges => _preflight?.NewlyStagedChanges is { Count: > 0 };
        public IReadOnlyList<Models.Change> VisibleNewlyStagedChanges => _visibleNewlyStagedChanges;
        public int MoreNewlyStagedCount => Math.Max(0, (_preflight?.NewlyStagedChanges.Count ?? 0) - _visibleNewlyStagedChanges.Count);
        public bool HasMoreNewlyStaged => MoreNewlyStagedCount > 0;
        public string MoreNewlyStagedText => HasMoreNewlyStaged
            ? App.Text("AmendLastCommit.MoreFiles", MoreNewlyStagedCount)
            : string.Empty;

        public string NewlyStagedHeader => _preflight == null
            ? string.Empty
            : App.Text("AmendLastCommit.NewlyStagedHeader", _preflight.NewlyStagedChanges.Count, _preflight.ResultingAmendChanges.Count);

        public string SigningSummary => _preflight?.SigningSummary ?? string.Empty;
        public string HookSummary => _preflight?.HookSummary ?? string.Empty;
        public bool IsSigningIssue => _preflight?.SigningStatus is { CommitSigningEnabled: true, IsReady: false };

        public bool CanRestoreOriginalMessage => !string.Equals(CommitMessage ?? string.Empty, _originalHeadFullMessage, StringComparison.Ordinal);

        public string PartialAmendDetail
        {
            get => _partialAmendDetail;
            private set => SetProperty(ref _partialAmendDetail, value);
        }

        public string PartialBackupRefDetail
        {
            get => _partialBackupRefDetail;
            private set => SetProperty(ref _partialBackupRefDetail, value);
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

        public override bool ShowConfirmButton
        {
            get
            {
                if (IsPushRetryOnlyState)
                    return true;

                if (!ShowConfigurationForm || _preflight == null)
                    return false;

                return _preflight.CanAmendDirectly || _preflight.CanOptInPushedRewrite;
            }
        }

        public override string ConfirmButtonText
        {
            get
            {
                if (IsPushRetryOnlyState)
                    return App.Text("AmendLastCommit.RetryPushOnly");

                if (_preflight?.CanOptInPushedRewrite == true)
                    return App.Text("AmendLastCommit.ConfirmRewriteWithLease");

                return App.Text("AmendLastCommit.ConfirmLocal");
            }
        }

        public override string CancelButtonText => HasResultView
            ? App.Text("AmendLastCommit.CloseResult")
            : App.Text("Cancel");

        public override bool CanConfirm
        {
            get
            {
                if (InProgress)
                    return false;

                if (IsPushRetryOnlyState)
                    return _retrySession is { CanRetryPushOnly: true };

                if (!ShowConfigurationForm || _preflight == null || string.IsNullOrWhiteSpace(CommitMessage))
                    return false;

                if (_preflight.CanAmendDirectly)
                    return true;

                if (_preflight.CanOptInPushedRewrite)
                    return ConfirmRewritePushedWithLease;

                return false;
            }
        }

        public AmendLastCommit(
            Repository repo,
            WorkingCopy workingCopy,
            Models.OperationJournalStore journalStore = null)
        {
            _repo = repo ?? throw new ArgumentNullException(nameof(repo));
            _workingCopy = workingCopy;
            _journalStore = journalStore ?? Models.OperationJournalStore.Instance;
            _signOff = workingCopy?.EnableSignOff ?? false;
            _noVerify = workingCopy?.NoVerifyOnCommit ?? false;
            _preflightTask = StartPreflightAsync(preserveUserMessage: false);
        }

        public Task EnsureLoadedAsync()
        {
            return _preflightTask ?? Task.CompletedTask;
        }

        public void RestoreOriginalMessage()
        {
            CommitMessage = _originalHeadFullMessage;
        }

        public void SwitchToCreateNewCommit()
        {
            if (_workingCopy != null)
            {
                _workingCopy.EnableSignOff = SignOff;
                _workingCopy.NoVerifyOnCommit = NoVerify;
                if (CanRestoreOriginalMessage || string.IsNullOrWhiteSpace(_workingCopy.CommitMessage))
                {
                    if (!string.Equals(CommitMessage, _originalHeadFullMessage, StringComparison.Ordinal))
                        _workingCopy.CommitMessage = CommitMessage ?? string.Empty;
                }
            }

            _repo.DismissPopup();
        }

        public async Task FetchAndReinspectAsync()
        {
            if (IsFetching || InProgress)
                return;

            IsFetching = true;
            try
            {
                var remotes = _repo.Remotes;
                var targetRemotes = new List<Models.Remote>();
                if (_preflight is { RemoteName.Length: > 0 })
                {
                    var matched = remotes.Find(r => string.Equals(r.Name, _preflight.RemoteName, StringComparison.Ordinal));
                    if (matched != null)
                        targetRemotes.Add(matched);
                }

                if (targetRemotes.Count == 0 && remotes.Count > 0)
                    targetRemotes.AddRange(remotes);

                if (targetRemotes.Count > 0)
                {
                    var log = _repo.CreateLog("Fetch before Amend");
                    await Commands.GitSyncService.FetchAsync(
                        _repo.FullPath,
                        targetRemotes,
                        log: log,
                        journalStore: _journalStore).ConfigureAwait(true);
                    log.Complete();
                    _repo.MarkBranchesDirtyManually();
                }

                await ReloadPreflightAsync(preserveUserMessage: true).ConfigureAwait(true);
            }
            finally
            {
                IsFetching = false;
            }
        }

        public Task ReloadPreflightAsync(bool preserveUserMessage = true)
        {
            HasResultView = false;
            IsPartialFailureView = false;
            ResultSummary = string.Empty;
            ResultDetail = string.Empty;
            _preflightTask = StartPreflightAsync(preserveUserMessage);
            return _preflightTask;
        }

        public override async Task<bool> Sure()
        {
            await EnsureLoadedAsync().ConfigureAwait(true);

            if (IsPushRetryOnlyState)
                return await ExecuteRetryPushOnlyAsync().ConfigureAwait(true);

            if (_preflight == null || _preflight.HasHardBlocker)
            {
                ShowFailureResult(
                    string.IsNullOrWhiteSpace(BlockReason) ? App.Text("AmendLastCommit.FailureTitle") : BlockReason,
                    string.Empty);
                return false;
            }

            var rewriteRemote = _preflight.RemotePublishState == Models.AmendRemotePublishState.AlreadyPushed &&
                                _preflight.CanOptInPushedRewrite &&
                                ConfirmRewritePushedWithLease;

            using var lockWatcher = _repo.LockWatcher();
            ProgressDescription = rewriteRemote
                ? $"Amend HEAD ({HeadShortSha}) and push with lease to {RemoteTargetDisplay} ..."
                : $"Amend HEAD ({HeadShortSha}) ...";

            var log = _repo.CreateLog("Amend Last Commit");
            Use(log);

            var outcome = await Commands.GitSyncService.ExecuteAmendLastCommitAsync(
                _repo.FullPath,
                new Models.AmendLastCommitExecutionOptions
                {
                    Preflight = _preflight,
                    Message = CommitMessage ?? string.Empty,
                    SignOff = SignOff,
                    NoVerify = NoVerify,
                    ResetAuthor = ResetAuthor,
                    RewritePushedRemoteWithLease = rewriteRemote,
                },
                log,
                CancellationToken.None,
                _journalStore).ConfigureAwait(true);

            _lastOutcome = outcome;
            log.Complete();

            if (outcome.AmendSucceeded)
            {
                if (_workingCopy != null)
                    await _workingCopy.RefreshStagedSnapshotAsync().ConfigureAwait(true);
                _repo.MarkBranchesDirtyManually();
                _repo.MarkWorkingCopyDirtyManually();
            }

            await _repo.RefreshRecoveryStatusAsync(_journalStore).ConfigureAwait(true);

            if (outcome.Succeeded)
            {
                if (_workingCopy != null && outcome.RetrySession is { IsFullySucceeded: true })
                    _workingCopy.ApplyExternalTwoStepSession(null);

                _repo.SendNotification(outcome.Summary);
                return true;
            }

            if (outcome.IsPartialSuccess && outcome.RetrySession is { CanRetryPushOnly: true })
            {
                _retrySession = outcome.RetrySession;
                _workingCopy?.ApplyExternalTwoStepSession(outcome.RetrySession);
                PartialAmendDetail = App.Text("AmendLastCommit.PartialAmendCardTitle") + $": {ShortSha(outcome.NewHeadSha)}";
                PartialBackupRefDetail = App.Text("AmendLastCommit.PartialBackupHint", outcome.BackupRefName);
                ResultSummary = outcome.Summary ?? string.Empty;
                ResultDetail = outcome.DetailMessage ?? string.Empty;
                IsPartialFailureView = true;
                HasResultView = true;
                return false;
            }

            ShowFailureResult(outcome.Summary, outcome.DetailMessage);
            return false;
        }

        private async Task<bool> ExecuteRetryPushOnlyAsync()
        {
            if (_retrySession is not { CanRetryPushOnly: true })
                return false;

            using var lockWatcher = _repo.LockWatcher();
            ProgressDescription = $"Retry lease push to {RemoteTargetDisplay} ...";

            var log = _repo.CreateLog("Retry Push After Amend");
            Use(log);

            var updated = await Commands.GitSyncService.RetryPushSessionAsync(
                _retrySession,
                null,
                log,
                CancellationToken.None,
                _journalStore).ConfigureAwait(true);

            log.Complete();
            await _repo.RefreshRecoveryStatusAsync(_journalStore).ConfigureAwait(true);
            _repo.MarkBranchesDirtyManually();

            if (updated.IsFullySucceeded)
            {
                _retrySession = updated;
                _workingCopy?.ApplyExternalTwoStepSession(null);
                var msg = updated.PushResult?.Summary ?? App.Text("AmendLastCommit.RetryPushSucceeded",
                    ShortSha(updated.CommitResult?.CommitSha), RemoteTargetDisplay);
                _repo.SendNotification(msg);
                return true;
            }

            _retrySession = updated;
            _workingCopy?.ApplyExternalTwoStepSession(updated);
            ResultSummary = updated.Summary ?? updated.PushResult?.Summary ?? string.Empty;
            ResultDetail = updated.PushResult?.DiagnosticDetail ?? string.Empty;
            NotifyPopupButtonsChanged();
            return false;
        }

        private void ShowFailureResult(string summary, string detail)
        {
            ResultSummary = summary ?? string.Empty;
            ResultDetail = detail ?? string.Empty;
            IsPartialFailureView = false;
            HasResultView = true;
        }

        private async Task StartPreflightAsync(bool preserveUserMessage)
        {
            IsLoadingPreflight = true;
            ConfirmRewritePushedWithLease = false;

            try
            {
                var preflight = await Commands.GitSyncService.InspectAmendLastCommitAsync(_repo.FullPath)
                    .ConfigureAwait(true);

                _preflight = preflight;
                _originalHeadFullMessage = preflight.HeadFullMessage ?? string.Empty;
                _visibleNewlyStagedChanges = preflight.NewlyStagedChanges.Take(10).ToList();

                if (!preserveUserMessage || string.IsNullOrEmpty(_commitMessage))
                    CommitMessage = _originalHeadFullMessage;

                HasPreflight = true;
                NotifyStatePropertiesChanged();
            }
            catch (Exception ex)
            {
                _preflight = new Models.AmendLastCommitPreflight
                {
                    RepoPath = _repo.FullPath,
                    BlockReason = Models.GitConfigSnapshot.RedactText(ex.Message),
                };
                _visibleNewlyStagedChanges = Array.Empty<Models.Change>();
                HasPreflight = true;
                NotifyStatePropertiesChanged();
            }
            finally
            {
                IsLoadingPreflight = false;
                NotifyStatePropertiesChanged();
            }
        }

        private void NotifyStatePropertiesChanged()
        {
            OnPropertyChanged(nameof(Preflight));
            OnPropertyChanged(nameof(HasHardBlocker));
            OnPropertyChanged(nameof(ShowConfigurationForm));
            OnPropertyChanged(nameof(ShowSummaryCard));
            OnPropertyChanged(nameof(IsErrorResultView));
            OnPropertyChanged(nameof(IsPushRetryOnlyState));
            OnPropertyChanged(nameof(BlockReason));
            OnPropertyChanged(nameof(TargetBranchDisplay));
            OnPropertyChanged(nameof(HeadShortSha));
            OnPropertyChanged(nameof(HeadSubject));
            OnPropertyChanged(nameof(HeadAuthorDisplay));
            OnPropertyChanged(nameof(HasHeadAuthor));
            OnPropertyChanged(nameof(HasRemoteEndpoint));
            OnPropertyChanged(nameof(RemoteTargetDisplay));
            OnPropertyChanged(nameof(LiveRemoteTipDisplay));
            OnPropertyChanged(nameof(IsUnpushedLocalOnly));
            OnPropertyChanged(nameof(IsAlreadyPushed));
            OnPropertyChanged(nameof(IsCautionRemoteBanner));
            OnPropertyChanged(nameof(IsBlockedRemoteDanger));
            OnPropertyChanged(nameof(RequiresRemoteWarningCard));
            OnPropertyChanged(nameof(BlockedByBranchProtection));
            OnPropertyChanged(nameof(HasOtherAuthorsOnRemote));
            OnPropertyChanged(nameof(RemoteAuthorsSummary));
            OnPropertyChanged(nameof(RemotePublishSummary));
            OnPropertyChanged(nameof(RemotePublishDetail));
            OnPropertyChanged(nameof(HasRemotePublishDetail));
            OnPropertyChanged(nameof(CanOptInPushedRewrite));
            OnPropertyChanged(nameof(GuideNewCommitAsPrimary));
            OnPropertyChanged(nameof(GuideNewCommitAsSecondary));
            OnPropertyChanged(nameof(RewritePushedOptInText));
            OnPropertyChanged(nameof(CanFetchAndReinspect));
            OnPropertyChanged(nameof(FetchButtonText));
            OnPropertyChanged(nameof(HasNewlyStagedChanges));
            OnPropertyChanged(nameof(VisibleNewlyStagedChanges));
            OnPropertyChanged(nameof(MoreNewlyStagedCount));
            OnPropertyChanged(nameof(HasMoreNewlyStaged));
            OnPropertyChanged(nameof(MoreNewlyStagedText));
            OnPropertyChanged(nameof(NewlyStagedHeader));
            OnPropertyChanged(nameof(SigningSummary));
            OnPropertyChanged(nameof(HookSummary));
            OnPropertyChanged(nameof(IsSigningIssue));
            OnPropertyChanged(nameof(CanRestoreOriginalMessage));
            NotifyPopupButtonsChanged();
        }

        private static string ShortSha(string sha)
        {
            if (string.IsNullOrEmpty(sha))
                return "-";
            return sha.Length > 10 ? sha[..10] : sha;
        }

        private readonly Repository _repo;
        private readonly WorkingCopy _workingCopy;
        private readonly Models.OperationJournalStore _journalStore;
        private Task _preflightTask;

        private Models.AmendLastCommitPreflight _preflight;
        private Models.AmendLastCommitOutcome _lastOutcome;
        private Models.CommitAndPushSession _retrySession;
        private IReadOnlyList<Models.Change> _visibleNewlyStagedChanges = Array.Empty<Models.Change>();
        private string _originalHeadFullMessage = string.Empty;
        private string _commitMessage = string.Empty;
        private bool _isLoadingPreflight = true;
        private bool _hasPreflight;
        private bool _isFetching;
        private bool _signOff;
        private bool _noVerify;
        private bool _resetAuthor;
        private bool _confirmRewritePushedWithLease;
        private bool _hasResultView;
        private bool _isPartialFailureView;
        private string _partialAmendDetail = string.Empty;
        private string _partialBackupRefDetail = string.Empty;
        private string _resultSummary = string.Empty;
        private string _resultDetail = string.Empty;
    }
}
