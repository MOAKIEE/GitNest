using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia.Collections;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;

namespace SourceGit.ViewModels
{
    public sealed class BranchDeleteStepDisplayItem
    {
        public BranchDeleteStepDisplayItem(Models.BranchDeleteStepResult step)
        {
            Label = step.Label;
            Detail = step.Detail;
            HasDetail = !string.IsNullOrWhiteSpace(step.Detail);
            IsSuccess = step.Succeeded && !step.Skipped;
            IsSkipped = step.Skipped;
            IsFailure = !step.Succeeded && !step.Skipped;
            IconKey = IsSuccess ? "Icons.CheckCircled" : (IsSkipped ? "Icons.Info" : "Icons.Error");
            IconBrush = IsSuccess
                ? Brushes.ForestGreen
                : (IsSkipped ? Brushes.DodgerBlue : Brushes.OrangeRed);
        }

        public string Label { get; }
        public string Detail { get; }
        public bool HasDetail { get; }
        public bool IsSuccess { get; }
        public bool IsSkipped { get; }
        public bool IsFailure { get; }
        public string IconKey { get; }
        public IBrush IconBrush { get; }
    }

    public class DeleteBranch : Popup
    {
        public Models.Branch Target { get; }

        public bool IsLocal => Target.IsLocal;
        public bool IsRemote => !Target.IsLocal;
        public bool IsCurrent => Target.IsCurrent || (_preflight?.IsCurrent ?? false);

        public string DialogTitle => IsLocal
            ? App.Text("DeleteBranch.TitleLocal")
            : App.Text("DeleteBranch.TitleRemote");

        public string TargetTipShortSha
        {
            get
            {
                var sha = !string.IsNullOrEmpty(_preflight?.TargetTipSha)
                    ? _preflight.TargetTipSha
                    : Target.Head;
                return sha?.Length > 8 ? sha[..8] : (sha ?? string.Empty);
            }
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

        public bool IsProbingRemote
        {
            get => _isProbingRemote;
            private set
            {
                if (SetProperty(ref _isProbingRemote, value))
                {
                    OnPropertyChanged(nameof(RemoteLiveStatusText));
                    NotifyPopupButtonsChanged();
                }
            }
        }

        public bool IsCheckedOutInOtherWorktree => _preflight?.IsCheckedOutInOtherWorktree ?? Target.HasWorktree;
        public string AttachedWorktreePath => _preflight?.AttachedWorktreePath ?? Target.WorktreePath ?? string.Empty;
        public bool AttachedWorktreeIsLocked =>
            _preflight?.AttachedWorktreePreflight?.IsLocked ?? (_preflight?.AttachedWorktreeLocked ?? false);
        public bool AttachedWorktreeHasNestedRepo =>
            _preflight?.AttachedWorktreePreflight?.HasNestedRepoOrSubmodule ?? false;
        public bool AttachedWorktreeIsMissing =>
            _preflight?.AttachedWorktreePreflight?.IsDirectoryMissing ?? false;
        public bool AttachedWorktreeRequiresForce =>
            _preflight?.AttachedWorktreePreflight?.RequiresForceToDeleteFiles ?? (_preflight?.AttachedWorktreeHasChanges ?? false);
        public bool CanCheckDeleteAttachedWorktree =>
            !AttachedWorktreeIsLocked && !AttachedWorktreeHasNestedRepo && !AttachedWorktreeIsMissing;
        public string AttachedWorktreeLockReasonDisplay =>
            string.IsNullOrWhiteSpace(_preflight?.AttachedWorktreePreflight?.LockReason)
                ? App.Text("RemoveWorktree.LockReasonEmpty")
                : _preflight.AttachedWorktreePreflight.LockReason;
        public string AttachedWorktreeNestedPathsDisplay =>
            _preflight?.AttachedWorktreePreflight is { NestedRepoOrSubmodulePaths.Count: > 0 } aw
                ? string.Join(Environment.NewLine, aw.NestedRepoOrSubmodulePaths)
                : string.Empty;
        public string AttachedWorktreeDetail
        {
            get
            {
                var aw = _preflight?.AttachedWorktreePreflight;
                if (aw == null)
                    return string.Empty;
                if (aw.IsLocked)
                    return App.Text("DeleteBranch.WorktreeLockedBlock", AttachedWorktreeLockReasonDisplay);
                if (aw.HasNestedRepoOrSubmodule)
                    return App.Text("DeleteBranch.WorktreeNestedBlock", AttachedWorktreeNestedPathsDisplay);
                if (aw.IsDirectoryMissing)
                    return App.Text("DeleteBranch.WorktreeMissingBlock", AttachedWorktreePath);
                if (aw.RequiresForceToDeleteFiles)
                {
                    return App.Text(
                        "RemoveWorktree.FileBreakdownSummary",
                        aw.StagedFiles.Count,
                        aw.UnstagedFiles.Count,
                        aw.ConflictedFiles.Count,
                        aw.UntrackedFiles.Count,
                        aw.IgnoredFiles.Count);
                }

                return string.Empty;
            }
        }
        public bool HasAttachedWorktreeDetail => !string.IsNullOrEmpty(AttachedWorktreeDetail);
        public bool AttachedWorktreeNeedsForceOption =>
            DeleteWorktree &&
            CanCheckDeleteAttachedWorktree &&
            AttachedWorktreeRequiresForce;
        public string WorktreeBlockMessage => IsCheckedOutInOtherWorktree
            ? App.Text("DeleteBranch.WorktreeBlocked", Target.Name, AttachedWorktreePath)
            : string.Empty;
        public string CurrentBlockMessage => IsCurrent
            ? App.Text("DeleteBranch.CurrentBlocked", Target.Name)
            : string.Empty;

        public bool DeleteWorktree
        {
            get => _deleteWorktree;
            set
            {
                if (SetProperty(ref _deleteWorktree, value))
                {
                    OnPropertyChanged(nameof(AttachedWorktreeNeedsForceOption));
                    NotifyPopupButtonsChanged();
                }
            }
        }

        public bool ForceRemoveWorktree
        {
            get => _forceRemoveWorktree;
            set
            {
                if (SetProperty(ref _forceRemoveWorktree, value))
                    NotifyPopupButtonsChanged();
            }
        }

        public string WorktreeRemovedNotice
        {
            get => _worktreeRemovedNotice;
            private set
            {
                if (SetProperty(ref _worktreeRemovedNotice, value))
                    OnPropertyChanged(nameof(HasWorktreeRemovedNotice));
            }
        }
        public bool HasWorktreeRemovedNotice => !string.IsNullOrEmpty(_worktreeRemovedNotice);

        public bool HasConfiguredUpstream => IsLocal && (_preflight?.HasConfiguredUpstream ?? false);
        public string ConfiguredUpstreamFriendly => HasConfiguredUpstream
            ? $"{_preflight.RemoteName}/{_preflight.RemoteBranchName}"
            : string.Empty;
        public string DeleteUpstreamTip => HasConfiguredUpstream
            ? App.Text("DeleteBranch.WithTrackingRemoteActual", ConfiguredUpstreamFriendly)
            : string.Empty;
        public bool IsUpstreamNameDifferent => HasConfiguredUpstream && (_preflight?.IsUpstreamNameDifferent ?? false);

        public bool DeleteUpstream
        {
            get => _deleteUpstream;
            set
            {
                if (SetProperty(ref _deleteUpstream, value))
                {
                    if (_inForceLocalConfirmStep)
                        InForceLocalConfirmStep = false;

                    OnPropertyChanged(nameof(ShowLinkedTargetsBreakdown));
                    NotifyPopupButtonsChanged();

                    if (value && HasConfiguredUpstream && _remoteProbe == null)
                        _ = EnsureRemoteProbeLoadedAsync(forceRefresh: false);
                }
            }
        }

        public bool ShowLinkedTargetsBreakdown => IsLocal && HasConfiguredUpstream && DeleteUpstream && !HasResultView && !IsCurrent;

        public string RemoteNameDisplay => !string.IsNullOrEmpty(_preflight?.RemoteName)
            ? _preflight.RemoteName
            : (Target.Remote ?? string.Empty);

        public string RemoteBranchFullDisplay => !string.IsNullOrEmpty(_preflight?.RemoteBranchName)
            ? $"refs/heads/{_preflight.RemoteBranchName}"
            : $"refs/heads/{Target.Name}";

        public string RedactedRemoteUrl => _remoteProbe?.RedactedRemoteUrl is { Length: > 0 } u
            ? u
            : (_preflight?.RedactedRemoteUrl ?? string.Empty);

        public string RemoteLiveStatusText
        {
            get
            {
                if (_isProbingRemote)
                    return App.Text("DeleteBranch.ProbingRemote");
                if (_remoteProbe == null)
                    return string.Empty;
                if (!_remoteProbe.QuerySucceeded)
                    return _remoteProbe.ErrorSummary;
                if (!_remoteProbe.ExistsOnServer)
                    return App.Text("DeleteBranch.RemoteAbsentOnServer");
                return _remoteProbe.LiveTipSha.Length > 10
                    ? _remoteProbe.LiveTipSha[..10]
                    : _remoteProbe.LiveTipSha;
            }
        }

        public bool RemoteQuerySucceeded => _remoteProbe?.QuerySucceeded ?? false;
        public bool RemoteExistsOnServer => _remoteProbe?.ExistsOnServer ?? false;
        public bool ShowCachePruneNotice =>
            (IsRemote && RemoteQuerySucceeded && !RemoteExistsOnServer && !HasResultView) || _inCachePruneConfirmStep;
        public string CachePruneConfirmMessage => App.Text("DeleteBranch.CachePruneConfirmNote", RemoteNameDisplay);

        public bool InForceLocalConfirmStep
        {
            get => _inForceLocalConfirmStep;
            private set
            {
                if (SetProperty(ref _inForceLocalConfirmStep, value))
                {
                    OnPropertyChanged(nameof(ShowUnmergedSection));
                    OnPropertyChanged(nameof(UnmergedSectionHeader));
                    NotifyPopupButtonsChanged();
                }
            }
        }

        public bool InCachePruneConfirmStep
        {
            get => _inCachePruneConfirmStep;
            private set
            {
                if (SetProperty(ref _inCachePruneConfirmStep, value))
                {
                    OnPropertyChanged(nameof(ShowCachePruneNotice));
                    NotifyPopupButtonsChanged();
                }
            }
        }

        public bool ShowUnmergedSection =>
            IsLocal &&
            !HasResultView &&
            !IsCurrent &&
            (_inForceLocalConfirmStep || ((_preflight?.HasUnmergedCommits ?? false) && UnmergedCommits.Count > 0));

        public string UnmergedSectionHeader => _inForceLocalConfirmStep
            ? App.Text("DeleteBranch.ForceRequiredHeader", Target.Name, _unmergedCommitCount, _unmergedChangedFilesCount)
            : App.Text("DeleteBranch.UnmergedPreviewHeader", _unmergedCommitCount, _unmergedChangedFilesCount);

        public AvaloniaList<Models.UnmergedCommitSummary> UnmergedCommits { get; } = [];

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

        public bool HasResultView
        {
            get => _hasResultView;
            private set
            {
                if (SetProperty(ref _hasResultView, value))
                {
                    OnPropertyChanged(nameof(ShowConfigurationForm));
                    OnPropertyChanged(nameof(ShowLinkedTargetsBreakdown));
                    OnPropertyChanged(nameof(ShowUnmergedSection));
                    OnPropertyChanged(nameof(ShowCachePruneNotice));
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

        public AvaloniaList<BranchDeleteStepDisplayItem> StepResults { get; } = [];

        public bool CanRestoreLocalFromBackup
        {
            get => _canRestoreLocalFromBackup;
            private set => SetProperty(ref _canRestoreLocalFromBackup, value);
        }

        public string RestorableBranchName
        {
            get => _restorableBranchName;
            set => SetProperty(ref _restorableBranchName, value);
        }

        public string RestorableBackupRef
        {
            get => _restorableBackupRef;
            private set => SetProperty(ref _restorableBackupRef, value);
        }

        public string RestoreFeedbackMessage
        {
            get => _restoreFeedbackMessage;
            private set
            {
                if (SetProperty(ref _restoreFeedbackMessage, value))
                    OnPropertyChanged(nameof(HasRestoreFeedback));
            }
        }
        public bool HasRestoreFeedback => !string.IsNullOrEmpty(_restoreFeedbackMessage);

        public bool HasRestoreError
        {
            get => _hasRestoreError;
            private set => SetProperty(ref _hasRestoreError, value);
        }

        public bool IsRestoringBackup
        {
            get => _isRestoringBackup;
            private set => SetProperty(ref _isRestoringBackup, value);
        }

        public override string ConfirmButtonText
        {
            get
            {
                if (_inForceLocalConfirmStep)
                    return App.Text("DeleteBranch.BtnForceDelete");
                if (_inCachePruneConfirmStep || (IsRemote && RemoteQuerySucceeded && !RemoteExistsOnServer))
                    return App.Text("DeleteBranch.BtnPruneCacheOnly");
                if (IsRemote)
                    return App.Text("DeleteBranch.BtnDeleteRemote");
                if (DeleteUpstream && HasConfiguredUpstream)
                    return App.Text("DeleteBranch.BtnDeleteLocalAndRemote");
                return App.Text("DeleteBranch.BtnDeleteLocal");
            }
        }

        public override string CancelButtonText => HasResultView ? App.Text("Close") : App.Text("Cancel");
        public override bool ShowConfirmButton => !HasResultView;

        public override bool CanConfirm
        {
            get
            {
                if (HasResultView || IsLoadingPreflight || IsProbingRemote)
                    return false;
                if (_preflight?.Protection.IsEffectivelyProtected == true)
                    return false;
                if (IsLocal && IsCurrent)
                    return false;
                if (IsLocal && IsCheckedOutInOtherWorktree)
                {
                    if (!DeleteWorktree || !CanCheckDeleteAttachedWorktree)
                        return false;
                    if (AttachedWorktreeRequiresForce && !ForceRemoveWorktree)
                        return false;
                }
                if (IsLocal && DeleteUpstream && (_remoteProbe == null || !_remoteProbe.QuerySucceeded))
                    return false;
                if (IsRemote && (_remoteProbe == null || !_remoteProbe.QuerySucceeded))
                    return false;
                return true;
            }
        }

        public Task PreflightTask => _preflightTask;

        public DeleteBranch(Repository repo, Models.Branch branch, Models.OperationJournalStore journalStore = null)
        {
            _repo = repo;
            Target = branch;
            _journalStore = journalStore ?? Models.OperationJournalStore.Instance;
            _restorableBranchName = branch.Name;
            _preflightTask = LoadPreflightAsync(probeRemote: !branch.IsLocal, preserveRemoteProbe: false);
        }

        public async Task RefreshRemoteProbeAsync()
        {
            await EnsureRemoteProbeLoadedAsync(forceRefresh: true);
        }

        public override async Task<bool> Sure()
        {
            await _preflightTask;
            if (!CanConfirm)
                return false;

            using var lockWatcher = _repo.LockWatcher();
            ProgressDescription = IsLocal ? "正在删除本地分支..." : "正在删除远程分支...";
            InlineErrorMessage = string.Empty;

            var log = _repo.CreateLog("Delete Branch");
            Use(log);

            try
            {
                Models.BranchDeleteOutcome outcome;
                if (Target.IsLocal)
                {
                    var options = new Models.BranchDeleteOptions
                    {
                        Preflight = _preflight,
                        DeleteAttachedWorktree = DeleteWorktree,
                        ForceRemoveWorktree = ForceRemoveWorktree,
                        ForceDeleteLocal = _inForceLocalConfirmStep,
                        DeleteLinkedUpstreamRemote = DeleteUpstream && HasConfiguredUpstream,
                        PruneStaleTrackingCacheWhenRemoteAbsent = DeleteUpstream && HasConfiguredUpstream && _remoteProbe is { QuerySucceeded: true, ExistsOnServer: false },
                        ConfirmedExpectedRemoteTipSha = _remoteProbe?.LiveTipSha,
                        ConfirmedPushUrl = _remoteProbe?.PushUrl,
                        ConfirmedRemoteExistsOnServer = _remoteProbe?.ExistsOnServer,
                        BeforeWorktreeRemoveAsync = (mainRoot, wtPath, _) =>
                        {
                            var switched = App.GetLauncher()?.SwitchAwayAndCloseWorktreeTabs(mainRoot, wtPath, this);
                            if (switched is { MainRepo: not null } sw)
                            {
                                _repo = sw.MainRepo;
                                _switchedHostPage = sw.MainPage;
                            }

                            return Task.CompletedTask;
                        },
                    };

                    outcome = await Commands.BranchDeletionService.DeleteLocalBranchAsync(
                        _repo.FullPath,
                        Target.Name,
                        options,
                        _journalStore,
                        log).ConfigureAwait(true);
                }
                else
                {
                    var options = new Models.BranchDeleteOptions
                    {
                        Preflight = _preflight,
                        PruneStaleTrackingCacheWhenRemoteAbsent = _inCachePruneConfirmStep || (_remoteProbe is { QuerySucceeded: true, ExistsOnServer: false }),
                        ConfirmedExpectedRemoteTipSha = _remoteProbe?.LiveTipSha,
                        ConfirmedPushUrl = _remoteProbe?.PushUrl,
                        ConfirmedRemoteExistsOnServer = _remoteProbe?.ExistsOnServer,
                    };

                    outcome = await Commands.BranchDeletionService.DeleteRemoteBranchAsync(
                        _repo.FullPath,
                        Target.Remote,
                        Target.Name,
                        options,
                        _journalStore,
                        log).ConfigureAwait(true);
                }

                if (outcome.WorktreeRemoved)
                {
                    _repo.RefreshTaskOverview();
                    _repo.MarkBranchesDirtyManually();
                }

                if (outcome.WorktreeRemoved && !outcome.LocalBranchDeleted)
                {
                    WorktreeRemovedNotice = App.Text("DeleteBranch.WorktreeRemovedNote", AttachedWorktreePath);
                }

                if (outcome.RequiresForceLocalConfirmation)
                {
                    _unmergedCommitCount = outcome.UnmergedCommitCount;
                    _unmergedChangedFilesCount = outcome.UnmergedChangedFilesCount;
                    UpdateUnmergedCommits(outcome.UnmergedCommits);
                    await LoadPreflightAsync(probeRemote: false, preserveRemoteProbe: true);
                    InForceLocalConfirmStep = true;
                    return false;
                }

                if (outcome.RequiresRemoteCachePruneConfirmation)
                {
                    InCachePruneConfirmStep = true;
                    return false;
                }

                if (outcome.IsPartialSuccess)
                {
                    _repo.UIStates.RemoveHistoryFilter(Target.FullName, Models.FilterType.LocalBranch);
                    _repo.MarkBranchesDirtyManually();

                    _completedOperationId = outcome.OperationId;
                    RestorableBranchName = outcome.BranchName;
                    RestorableBackupRef = outcome.BackupRefName;
                    CanRestoreLocalFromBackup = outcome.LocalBranchDeleted && !string.IsNullOrEmpty(outcome.BackupRefName);
                    ResultSummary = outcome.Summary;
                    StepResults.Clear();
                    foreach (var s in outcome.StepResults)
                        StepResults.Add(new BranchDeleteStepDisplayItem(s));
                    HasResultView = true;
                    return false;
                }

                if (!outcome.Succeeded)
                {
                    InlineErrorMessage = string.IsNullOrWhiteSpace(outcome.DetailMessage) || outcome.DetailMessage == outcome.Summary
                        ? outcome.Summary
                        : $"{outcome.Summary}{Environment.NewLine}{outcome.DetailMessage}";

                    if (outcome.BlockedBySnapshot || outcome.RejectedByRemoteLease)
                        await LoadPreflightAsync(probeRemote: DeleteUpstream || IsRemote, preserveRemoteProbe: false);

                    return false;
                }

                if (outcome.LocalBranchDeleted)
                    _repo.UIStates.RemoveHistoryFilter(Target.FullName, Models.FilterType.LocalBranch);
                if (outcome.RemoteBranchDeleted)
                {
                    var remoteFullName = IsRemote
                        ? Target.FullName
                        : $"refs/remotes/{outcome.RemoteName}/{outcome.RemoteBranchName}";
                    _repo.UIStates.RemoveHistoryFilter(remoteFullName, Models.FilterType.RemoteBranch);
                }

                _repo.MarkBranchesDirtyManually();
                _repo.SendNotification(outcome.Summary);
                if (_switchedHostPage != null && _switchedHostPage.Popup == this)
                    _switchedHostPage.Popup = null;
                return true;
            }
            finally
            {
                log.Complete();
            }
        }

        public async Task RestoreLocalBranchFromBackupAsync()
        {
            if (IsRestoringBackup || string.IsNullOrEmpty(RestorableBackupRef))
                return;

            IsRestoringBackup = true;
            RestoreFeedbackMessage = string.Empty;
            HasRestoreError = false;

            var log = _repo.CreateLog("Restore Branch");
            try
            {
                var restore = await Commands.BranchDeletionService.RestoreDeletedLocalBranchAsync(
                    _repo.FullPath,
                    RestorableBranchName?.Trim() ?? string.Empty,
                    RestorableBackupRef,
                    _completedOperationId,
                    _journalStore,
                    log).ConfigureAwait(true);

                RestoreFeedbackMessage = restore.Summary;
                HasRestoreError = !restore.Succeeded;

                if (restore.Succeeded)
                {
                    CanRestoreLocalFromBackup = false;
                    _repo.MarkBranchesDirtyManually();
                }
            }
            finally
            {
                log.Complete();
                IsRestoringBackup = false;
            }
        }

        private async Task LoadPreflightAsync(bool probeRemote, bool preserveRemoteProbe)
        {
            IsLoadingPreflight = true;
            try
            {
                var existingRemoteProbe = preserveRemoteProbe ? _remoteProbe : null;
                _preflight = Target.IsLocal
                    ? await Commands.BranchDeletionService.InspectLocalBranchAsync(_repo.FullPath, Target.Name, probeLinkedRemote: probeRemote)
                        .ConfigureAwait(true)
                    : await Commands.BranchDeletionService.InspectRemoteBranchAsync(_repo.FullPath, Target.Remote, Target.Name, queryServer: true)
                        .ConfigureAwait(true);

                _remoteProbe = preserveRemoteProbe && existingRemoteProbe != null
                    ? existingRemoteProbe
                    : _preflight.RemoteProbe;

                if (!_inForceLocalConfirmStep)
                {
                    _unmergedCommitCount = _preflight.UnmergedCommitCount;
                    _unmergedChangedFilesCount = _preflight.UnmergedChangedFilesCount;
                    UpdateUnmergedCommits(_preflight.UnmergedCommits);
                }

                if (_remoteProbe is { QuerySucceeded: false })
                    InlineErrorMessage = _remoteProbe.ErrorSummary;
                if (_preflight.Protection.IsEffectivelyProtected)
                    InlineErrorMessage = _preflight.Protection.Summary;

                OnPropertyChanged(nameof(IsCurrent));
                OnPropertyChanged(nameof(TargetTipShortSha));
                OnPropertyChanged(nameof(IsCheckedOutInOtherWorktree));
                OnPropertyChanged(nameof(AttachedWorktreePath));
                OnPropertyChanged(nameof(AttachedWorktreeIsLocked));
                OnPropertyChanged(nameof(AttachedWorktreeHasNestedRepo));
                OnPropertyChanged(nameof(AttachedWorktreeIsMissing));
                OnPropertyChanged(nameof(AttachedWorktreeRequiresForce));
                OnPropertyChanged(nameof(CanCheckDeleteAttachedWorktree));
                OnPropertyChanged(nameof(AttachedWorktreeLockReasonDisplay));
                OnPropertyChanged(nameof(AttachedWorktreeNestedPathsDisplay));
                OnPropertyChanged(nameof(AttachedWorktreeDetail));
                OnPropertyChanged(nameof(HasAttachedWorktreeDetail));
                OnPropertyChanged(nameof(AttachedWorktreeNeedsForceOption));
                OnPropertyChanged(nameof(WorktreeBlockMessage));
                OnPropertyChanged(nameof(CurrentBlockMessage));
                OnPropertyChanged(nameof(HasConfiguredUpstream));
                OnPropertyChanged(nameof(ConfiguredUpstreamFriendly));
                OnPropertyChanged(nameof(DeleteUpstreamTip));
                OnPropertyChanged(nameof(IsUpstreamNameDifferent));
                OnPropertyChanged(nameof(ShowLinkedTargetsBreakdown));
                OnPropertyChanged(nameof(RemoteNameDisplay));
                OnPropertyChanged(nameof(RemoteBranchFullDisplay));
                OnPropertyChanged(nameof(RedactedRemoteUrl));
                OnPropertyChanged(nameof(RemoteLiveStatusText));
                OnPropertyChanged(nameof(RemoteQuerySucceeded));
                OnPropertyChanged(nameof(RemoteExistsOnServer));
                OnPropertyChanged(nameof(ShowCachePruneNotice));
                OnPropertyChanged(nameof(CachePruneConfirmMessage));
                OnPropertyChanged(nameof(ShowUnmergedSection));
                OnPropertyChanged(nameof(UnmergedSectionHeader));
                NotifyPopupButtonsChanged();
            }
            catch (Exception ex)
            {
                InlineErrorMessage = Models.GitConfigSnapshot.RedactText(ex.Message);
            }
            finally
            {
                IsLoadingPreflight = false;
            }
        }

        private async Task EnsureRemoteProbeLoadedAsync(bool forceRefresh)
        {
            if (_preflight == null)
                await _preflightTask;

            if (!forceRefresh && _remoteProbe != null)
                return;

            var remoteName = RemoteNameDisplay;
            var remoteBranchName = !string.IsNullOrEmpty(_preflight?.RemoteBranchName)
                ? _preflight.RemoteBranchName
                : Target.Name;

            if (string.IsNullOrEmpty(remoteName) || string.IsNullOrEmpty(remoteBranchName))
                return;

            IsProbingRemote = true;
            try
            {
                _remoteProbe = await Commands.BranchDeletionService.ProbeRemoteBranchAsync(
                    _repo.FullPath,
                    remoteName,
                    remoteBranchName).ConfigureAwait(true);

                if (_remoteProbe is { QuerySucceeded: false })
                    InlineErrorMessage = _remoteProbe.ErrorSummary;
                else if (InlineErrorMessage != null && (_remoteProbe?.QuerySucceeded ?? false))
                    InlineErrorMessage = string.Empty;
                if (_preflight?.Protection.IsEffectivelyProtected == true)
                    InlineErrorMessage = _preflight.Protection.Summary;

                OnPropertyChanged(nameof(RedactedRemoteUrl));
                OnPropertyChanged(nameof(RemoteLiveStatusText));
                OnPropertyChanged(nameof(RemoteQuerySucceeded));
                OnPropertyChanged(nameof(RemoteExistsOnServer));
                OnPropertyChanged(nameof(ShowCachePruneNotice));
                NotifyPopupButtonsChanged();
            }
            finally
            {
                IsProbingRemote = false;
            }
        }

        private void UpdateUnmergedCommits(IReadOnlyList<Models.UnmergedCommitSummary> commits)
        {
            UnmergedCommits.Clear();
            if (commits == null)
                return;

            var limit = Math.Min(commits.Count, 6);
            for (var i = 0; i < limit; i++)
                UnmergedCommits.Add(commits[i]);

            OnPropertyChanged(nameof(ShowUnmergedSection));
            OnPropertyChanged(nameof(UnmergedSectionHeader));
        }

        private Repository _repo;
        private LauncherPage _switchedHostPage;
        private readonly Models.OperationJournalStore _journalStore;
        private Task _preflightTask = Task.CompletedTask;
        private Models.BranchDeletePreflight _preflight;
        private Models.RemoteBranchProbeResult _remoteProbe;

        private bool _isLoadingPreflight = true;
        private bool _isProbingRemote;
        private bool _deleteWorktree;
        private bool _forceRemoveWorktree;
        private bool _deleteUpstream;
        private bool _inForceLocalConfirmStep;
        private bool _inCachePruneConfirmStep;
        private bool _hasResultView;
        private bool _canRestoreLocalFromBackup;
        private bool _hasRestoreError;
        private bool _isRestoringBackup;

        private int _unmergedCommitCount;
        private int _unmergedChangedFilesCount;
        private string _worktreeRemovedNotice = string.Empty;
        private string _inlineErrorMessage = string.Empty;
        private string _resultSummary = string.Empty;
        private string _restorableBranchName = string.Empty;
        private string _restorableBackupRef = string.Empty;
        private string _restoreFeedbackMessage = string.Empty;
        private string _completedOperationId = string.Empty;
    }
}
