using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Collections;
using Avalonia.Media;

namespace SourceGit.ViewModels
{
    public sealed class WorktreeFilePreviewItem
    {
        public string CategoryBadge { get; init; } = string.Empty;
        public string RelativePath { get; init; } = string.Empty;
        public IBrush BadgeBackground { get; init; }
    }

    public sealed class WorktreeDeleteStepDisplayItem
    {
        public WorktreeDeleteStepDisplayItem(Models.WorktreeDeleteStepResult step)
        {
            Label = step.Label;
            Detail = step.Detail;
            HasDetail = !string.IsNullOrWhiteSpace(step.Detail);

            if (step.Succeeded && !step.Skipped)
            {
                IconKey = "Icons.CheckCircled";
                IconBrush = Brushes.ForestGreen;
            }
            else if (step.Skipped)
            {
                IconKey = "Icons.Info";
                IconBrush = Brushes.SteelBlue;
            }
            else
            {
                IconKey = "Icons.Error";
                IconBrush = Brushes.OrangeRed;
            }
        }

        public WorktreeDeleteStepDisplayItem(Models.BranchDeleteStepResult step)
            : this(new Models.WorktreeDeleteStepResult
            {
                Succeeded = step.Succeeded,
                Skipped = step.Skipped,
                Label = step.Label,
                Detail = step.Detail,
            })
        {
        }

        public string Label { get; }
        public string Detail { get; }
        public bool HasDetail { get; }
        public string IconKey { get; }
        public IBrush IconBrush { get; }
    }

    public class RemoveWorktree : Popup
    {
        public Worktree Target { get; }
        public string TargetFullPath => Target.FullPath;

        public bool IsLoadingPreflight
        {
            get => _isLoadingPreflight;
            private set
            {
                if (SetProperty(ref _isLoadingPreflight, value))
                {
                    OnPropertyChanged(nameof(CanUnlockWorktree));
                    OnPropertyChanged(nameof(CanShowForceOption));
                    OnPropertyChanged(nameof(ShowForceLossWarning));
                    OnPropertyChanged(nameof(CanOfferLocalBranchDelete));
                    OnPropertyChanged(nameof(ShowCleanupBreakdown));
                    OnPropertyChanged(nameof(ShowRemoteTargetRow));
                    NotifyPopupButtonsChanged();
                }
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

        public bool IsUnlockingWorktree
        {
            get => _isUnlockingWorktree;
            private set => SetProperty(ref _isUnlockingWorktree, value);
        }

        public bool IsExecutingFollowUp
        {
            get => _isExecutingFollowUp;
            private set => SetProperty(ref _isExecutingFollowUp, value);
        }

        public bool IsDetached => _preflight?.IsDetached ?? Target.Backend.IsDetached;
        public string BranchDisplay => IsDetached
            ? "detached HEAD"
            : (!string.IsNullOrWhiteSpace(_preflight?.BranchName) ? _preflight.BranchName : Target.Branch);
        public string HeadShortSha
        {
            get
            {
                var sha = !string.IsNullOrWhiteSpace(_preflight?.HeadSha) ? _preflight.HeadSha : Target.Head;
                return sha.Length > 8 ? sha[..8] : sha;
            }
        }

        public string SyncSummaryText
        {
            get
            {
                if (_preflight == null)
                    return Target.SyncDescription;

                return Worktree.DescribeSync(new Models.WorktreeStatus
                {
                    IsReadable = !_preflight.IsDirectoryMissing && _preflight.IdentityValid,
                    SyncState = _preflight.SyncState,
                    Upstream = _preflight.SyncUpstream,
                    Ahead = _preflight.SyncAhead,
                    Behind = _preflight.SyncBehind,
                    LastFetchAt = _preflight.LastFetchAt,
                    Head = _preflight.HeadSha,
                    Branch = _preflight.BranchName,
                    Error = _preflight.IsDirectoryMissing ? "Worktree.Status.Missing" : "Worktree.Status.IdentityChanged",
                });
            }
        }

        public bool IsTargetOpenInGitNest => App.GetLauncher()?.IsWorktreeOpenInTabs(Target.FullPath) ?? false;
        public bool IsDirectoryMissing => _preflight?.IsDirectoryMissing ?? false;
        public bool IsLocked => _preflight?.IsLocked ?? Target.IsLocked;
        public string LockReasonDisplay => string.IsNullOrWhiteSpace(_preflight?.LockReason)
            ? App.Text("RemoveWorktree.LockReasonEmpty")
            : _preflight.LockReason;

        public bool UnlockAcknowledged
        {
            get => _unlockAcknowledged;
            set
            {
                if (SetProperty(ref _unlockAcknowledged, value))
                    OnPropertyChanged(nameof(CanUnlockWorktree));
            }
        }
        public bool CanUnlockWorktree => IsLocked && UnlockAcknowledged && !IsUnlockingWorktree && !IsLoadingPreflight;

        public bool HasNestedRepoOrSubmodule => _preflight?.HasNestedRepoOrSubmodule ?? false;
        public string NestedPathsDisplay => _preflight != null
            ? string.Join(Environment.NewLine, _preflight.NestedRepoOrSubmodulePaths)
            : string.Empty;

        public int StagedCount => _preflight?.StagedFiles.Count ?? 0;
        public int UnstagedCount => _preflight?.UnstagedFiles.Count ?? 0;
        public int ConflictedCount => _preflight?.ConflictedFiles.Count ?? 0;
        public int UntrackedCount => _preflight?.UntrackedFiles.Count ?? 0;
        public int IgnoredCount => _preflight?.IgnoredFiles.Count ?? 0;
        public int TotalBlockingFiles => _preflight?.TotalBlockingFiles ?? 0;
        public bool HasInspectableFiles => !HasResultView && !IsDirectoryMissing && TotalBlockingFiles > 0;
        public string FileBreakdownSummary => App.Text(
            "RemoveWorktree.FileBreakdownSummary",
            StagedCount,
            UnstagedCount,
            ConflictedCount,
            UntrackedCount,
            IgnoredCount);

        public AvaloniaList<WorktreeFilePreviewItem> BlockingFiles { get; } = [];

        public bool CanShowForceOption =>
            !HasResultView &&
            !IsLoadingPreflight &&
            !IsDirectoryMissing &&
            !(_preflight?.IsMainWorktree ?? Target.IsMain) &&
            !IsLocked &&
            !HasNestedRepoOrSubmodule &&
            (_preflight?.RequiresForceToDeleteFiles ?? false);

        public bool Force
        {
            get => _force;
            set
            {
                if (SetProperty(ref _force, value))
                {
                    OnPropertyChanged(nameof(ShowForceLossWarning));
                    NotifyPopupButtonsChanged();
                }
            }
        }

        public bool ShowForceLossWarning => CanShowForceOption && Force;
        public string ForceLossWarningText => App.Text("RemoveWorktree.ForceLossWarning", Target.FullPath, TotalBlockingFiles);

        public bool CanOfferLocalBranchDelete =>
            !HasResultView &&
            !IsLoadingPreflight &&
            !IsDirectoryMissing &&
            !(_preflight?.IsMainWorktree ?? Target.IsMain) &&
            !IsLocked &&
            !HasNestedRepoOrSubmodule &&
            !IsDetached &&
            _preflight?.AttachedBranchPreflight != null;

        public bool DeleteLocalBranch
        {
            get => _deleteLocalBranch;
            set
            {
                if (SetProperty(ref _deleteLocalBranch, value))
                {
                    if (!value && DeleteRemoteBranch)
                        _deleteRemoteBranch = false;
                    OnPropertyChanged(nameof(DeleteRemoteBranch));
                    OnPropertyChanged(nameof(ShowRemoteBranchOption));
                    OnPropertyChanged(nameof(ShowCleanupBreakdown));
                    NotifyPopupButtonsChanged();
                }
            }
        }

        public bool HasConfiguredUpstream => _preflight?.AttachedBranchPreflight?.HasConfiguredUpstream ?? false;
        public bool ShowRemoteBranchOption => DeleteLocalBranch && HasConfiguredUpstream;
        public bool IsUpstreamNameDifferent => _preflight?.AttachedBranchPreflight?.IsUpstreamNameDifferent ?? false;
        public string ConfiguredUpstreamFriendly => HasConfiguredUpstream
            ? $"{_preflight.AttachedBranchPreflight.RemoteName}/{_preflight.AttachedBranchPreflight.RemoteBranchName}"
            : string.Empty;
        public string DeleteLocalBranchOptionText => App.Text("RemoveWorktree.AlsoDeleteLocalBranch", BranchDisplay);
        public string DeleteRemoteBranchOptionText => App.Text("RemoveWorktree.AlsoDeleteRemoteBranch", ConfiguredUpstreamFriendly);

        public bool DeleteRemoteBranch
        {
            get => _deleteRemoteBranch;
            set
            {
                if (SetProperty(ref _deleteRemoteBranch, value))
                {
                    OnPropertyChanged(nameof(ShowRemoteTargetRow));
                    NotifyPopupButtonsChanged();
                    if (value && _preflight?.AttachedBranchPreflight != null && _remoteProbe == null)
                        _ = EnsureRemoteProbeLoadedAsync(forceRefresh: false);
                }
            }
        }

        public bool ShowCleanupBreakdown => CanOfferLocalBranchDelete && DeleteLocalBranch;
        public bool ShowRemoteTargetRow => ShowCleanupBreakdown && DeleteRemoteBranch && HasConfiguredUpstream;

        public string RedactedRemoteUrl => !string.IsNullOrWhiteSpace(_remoteProbe?.RedactedRemoteUrl)
            ? _remoteProbe.RedactedRemoteUrl
            : (_preflight?.AttachedBranchPreflight?.RedactedRemoteUrl ?? string.Empty);

        public string RemoteLiveStatusText
        {
            get
            {
                if (IsProbingRemote)
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
                    OnPropertyChanged(nameof(HasInspectableFiles));
                    OnPropertyChanged(nameof(CanShowForceOption));
                    OnPropertyChanged(nameof(ShowForceLossWarning));
                    OnPropertyChanged(nameof(CanOfferLocalBranchDelete));
                    OnPropertyChanged(nameof(ShowCleanupBreakdown));
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

        public AvaloniaList<WorktreeDeleteStepDisplayItem> StepResults { get; } = [];
        public AvaloniaList<Models.UnmergedCommitSummary> UnmergedCommits { get; } = [];

        public bool RequiresFollowUpForceLocalBranch
        {
            get => _requiresFollowUpForceLocalBranch;
            private set => SetProperty(ref _requiresFollowUpForceLocalBranch, value);
        }

        public string FollowUpUnmergedHeader => App.Text(
            "RemoveWorktree.FollowUpUnmergedHeader",
            _remainingLocalBranchName,
            _followUpUnmergedCommitCount);

        public bool CanRestoreBackupBranch
        {
            get => _canRestoreBackupBranch;
            private set => SetProperty(ref _canRestoreBackupBranch, value);
        }

        public string RestorableBranchName
        {
            get => _restorableBranchName;
            set => SetProperty(ref _restorableBranchName, value);
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
                if (CanShowForceOption && Force)
                    return App.Text("RemoveWorktree.BtnForceDelete");
                if (DeleteLocalBranch && DeleteRemoteBranch && HasConfiguredUpstream)
                    return App.Text("RemoveWorktree.BtnDeleteAll");
                if (DeleteLocalBranch)
                    return App.Text("RemoveWorktree.BtnDeleteWithLocal");
                return App.Text("RemoveWorktree.BtnDeleteOnly");
            }
        }

        public override string CancelButtonText => HasResultView ? App.Text("Close") : App.Text("Cancel");
        public override bool ShowConfirmButton => !HasResultView;

        public override bool CanConfirm
        {
            get
            {
                if (HasResultView || IsLoadingPreflight || IsProbingRemote || IsUnlockingWorktree || IsExecutingFollowUp)
                    return false;
                if (_preflight == null)
                    return false;
                if (_preflight.IsMainWorktree || _preflight.IsBare || _preflight.IsDirectoryMissing || !_preflight.IdentityValid)
                    return false;
                if (_preflight.IsLocked || _preflight.HasNestedRepoOrSubmodule)
                    return false;
                if (_preflight.RequiresForceToDeleteFiles && !Force)
                    return false;
                if (DeleteLocalBranch && DeleteRemoteBranch && (_remoteProbe == null || !_remoteProbe.QuerySucceeded))
                    return false;
                return true;
            }
        }

        public Task PreflightTask => _preflightTask;

        public RemoveWorktree(Repository repo, Worktree target, Models.OperationJournalStore journalStore = null)
        {
            _repo = repo;
            _hostPage = repo.GetOwnerPage();
            Target = target;
            _journalStore = journalStore ?? Models.OperationJournalStore.Instance;
            _preflightTask = LoadPreflightAsync(probeRemote: false);
        }

        public Task RefreshPreflightAsync() => LoadPreflightAsync(probeRemote: DeleteLocalBranch && DeleteRemoteBranch);

        public Task RefreshRemoteProbeAsync() => EnsureRemoteProbeLoadedAsync(forceRefresh: true);

        public async Task UnlockAndReinspectAsync()
        {
            if (!CanUnlockWorktree)
                return;

            IsUnlockingWorktree = true;
            InlineErrorMessage = string.Empty;
            var log = _repo.CreateLog("Unlock Worktree");
            try
            {
                var res = await Commands.WorktreeDeletionService.UnlockWorktreeAsync(
                    _repo.FullPath,
                    Target.FullPath,
                    log).ConfigureAwait(true);

                _preflight = res.Preflight;
                Target.IsLocked = _preflight?.IsLocked ?? false;
                UnlockAcknowledged = false;
                _repo.RefreshTaskOverview();
                ApplyPreflightBindings();

                if (!res.Succeeded)
                    InlineErrorMessage = res.Summary;
            }
            catch (Exception ex)
            {
                InlineErrorMessage = Models.GitConfigSnapshot.RedactText(ex.Message);
            }
            finally
            {
                log.Complete();
                IsUnlockingWorktree = false;
                OnPropertyChanged(nameof(CanUnlockWorktree));
            }
        }

        public void OpenPrunePreviewDialog()
        {
            if (InProgress)
                return;

            var repo = _repo;
            var host = _hostPage ?? repo.GetOwnerPage();
            var journal = _journalStore;
            Cleanup();
            if (host != null)
                host.Popup = new PruneWorktrees(repo, journal);
            else
                repo.ShowPopup(new PruneWorktrees(repo, journal));
        }

        public override async Task<bool> Sure()
        {
            await _preflightTask;
            if (!CanConfirm)
                return false;

            using var lockWatcher = _repo.LockWatcher();
            ProgressDescription = "正在安全移除工作树...";
            InlineErrorMessage = string.Empty;

            var log = _repo.CreateLog("Remove Worktree");
            Use(log);

            try
            {
                var outcome = await Commands.WorktreeDeletionService.DeleteWorktreeAsync(
                    _repo.FullPath,
                    Target.FullPath,
                    new Models.WorktreeDeleteOptions
                    {
                        Preflight = _preflight,
                        ForceRemoveWorktree = Force,
                        DeleteLocalBranch = DeleteLocalBranch,
                        ForceDeleteLocalBranch = false,
                        DeleteLinkedUpstreamRemote = DeleteLocalBranch && DeleteRemoteBranch,
                        PruneStaleTrackingCacheWhenRemoteAbsent = DeleteLocalBranch && DeleteRemoteBranch && _remoteProbe is { QuerySucceeded: true, ExistsOnServer: false },
                        ConfirmedExpectedRemoteTipSha = _remoteProbe?.LiveTipSha,
                        ConfirmedPushUrl = _remoteProbe?.PushUrl,
                        ConfirmedRemoteExistsOnServer = _remoteProbe?.ExistsOnServer,
                        BeforeWorktreeRemoveAsync = (mainRoot, targetPath, _) =>
                        {
                            var switched = App.GetLauncher()?.SwitchAwayAndCloseWorktreeTabs(mainRoot, targetPath, this);
                            if (switched is { MainRepo: not null } sw)
                            {
                                _repo = sw.MainRepo;
                                _switchedHostPage = sw.MainPage;
                            }

                            return Task.CompletedTask;
                        },
                    },
                    _journalStore,
                    log).ConfigureAwait(true);

                if (outcome.WorktreeRemoved)
                {
                    _repo.RefreshTaskOverview();
                    _repo.MarkBranchesDirtyManually();
                }

                if (outcome.IsPartialSuccess || outcome.RequiresForceLocalBranchConfirmation)
                {
                    _remainingLocalBranchName = outcome.BranchName;
                    _followUpUnmergedCommitCount = outcome.UnmergedCommitCount;
                    _followUpRemoteLeaseSha = _remoteProbe?.LiveTipSha ?? outcome.ExpectedRemoteTipSha;
                    _followUpPushUrl = _remoteProbe?.PushUrl;
                    _followUpRemoteExists = _remoteProbe?.ExistsOnServer;
                    _followUpDeleteRemote = DeleteLocalBranch && DeleteRemoteBranch;
                    _completedOperationId = outcome.OperationId;
                    _restorableBackupRef = outcome.LocalBranchBackupRefName;
                    RestorableBranchName = outcome.BranchName;

                    UnmergedCommits.Clear();
                    foreach (var c in outcome.UnmergedCommits)
                        UnmergedCommits.Add(c);

                    RequiresFollowUpForceLocalBranch = outcome.WorktreeRemoved &&
                        !outcome.LocalBranchDeleted &&
                        outcome.RequiresForceLocalBranchConfirmation;
                    CanRestoreBackupBranch = outcome.LocalBranchDeleted &&
                        !string.IsNullOrEmpty(outcome.LocalBranchBackupRefName);

                    ResultSummary = outcome.Summary;
                    StepResults.Clear();
                    foreach (var s in outcome.StepResults)
                        StepResults.Add(new WorktreeDeleteStepDisplayItem(s));

                    OnPropertyChanged(nameof(FollowUpUnmergedHeader));
                    HasResultView = true;
                    return false;
                }

                if (!outcome.Succeeded)
                {
                    InlineErrorMessage = string.IsNullOrWhiteSpace(outcome.DetailMessage) || outcome.DetailMessage == outcome.Summary
                        ? outcome.Summary
                        : $"{outcome.Summary}{Environment.NewLine}{outcome.DetailMessage}";

                    if (outcome.BlockedBySnapshot || outcome.PreflightAfterFailure != null)
                        await LoadPreflightAsync(probeRemote: DeleteLocalBranch && DeleteRemoteBranch).ConfigureAwait(true);

                    return false;
                }

                if (outcome.LocalBranchDeleted)
                    _repo.UIStates.RemoveHistoryFilter($"refs/heads/{outcome.BranchName}", Models.FilterType.LocalBranch);
                if (outcome.RemoteBranchDeleted)
                    _repo.UIStates.RemoveHistoryFilter($"refs/remotes/{outcome.RemoteName}/{outcome.RemoteBranchName}", Models.FilterType.RemoteBranch);

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

        public async Task ForceDeleteRemainingLocalBranchAsync()
        {
            if (IsExecutingFollowUp || string.IsNullOrWhiteSpace(_remainingLocalBranchName))
                return;

            IsExecutingFollowUp = true;
            RestoreFeedbackMessage = string.Empty;
            HasRestoreError = false;
            var log = _repo.CreateLog("Force Delete Branch");

            try
            {
                var preflight = await Commands.BranchDeletionService.InspectLocalBranchAsync(
                    _repo.FullPath,
                    _remainingLocalBranchName,
                    probeLinkedRemote: false).ConfigureAwait(true);

                var res = await Commands.BranchDeletionService.DeleteLocalBranchAsync(
                    _repo.FullPath,
                    _remainingLocalBranchName,
                    new Models.BranchDeleteOptions
                    {
                        Preflight = preflight,
                        ForceDeleteLocal = true,
                        DeleteLinkedUpstreamRemote = _followUpDeleteRemote && preflight.HasConfiguredUpstream,
                        PruneStaleTrackingCacheWhenRemoteAbsent = _followUpDeleteRemote && _followUpRemoteExists == false,
                        ConfirmedExpectedRemoteTipSha = _followUpRemoteLeaseSha,
                        ConfirmedPushUrl = _followUpPushUrl,
                        ConfirmedRemoteExistsOnServer = _followUpRemoteExists,
                    },
                    _journalStore,
                    log).ConfigureAwait(true);

                _repo.MarkBranchesDirtyManually();

                if (res.LocalBranchDeleted)
                    _repo.UIStates.RemoveHistoryFilter($"refs/heads/{res.BranchName}", Models.FilterType.LocalBranch);
                if (res.RemoteBranchDeleted)
                    _repo.UIStates.RemoveHistoryFilter($"refs/remotes/{res.RemoteName}/{res.RemoteBranchName}", Models.FilterType.RemoteBranch);

                RequiresFollowUpForceLocalBranch = false;
                ResultSummary = res.Summary;

                foreach (var s in res.StepResults)
                    StepResults.Add(new WorktreeDeleteStepDisplayItem(s));

                if (res.IsPartialSuccess)
                {
                    _completedOperationId = res.OperationId;
                    _restorableBackupRef = res.BackupRefName;
                    RestorableBranchName = res.BranchName;
                    CanRestoreBackupBranch = res.LocalBranchDeleted && !string.IsNullOrEmpty(res.BackupRefName);
                }
                else
                {
                    HasRestoreError = !res.Succeeded;
                    RestoreFeedbackMessage = res.Summary;
                }
            }
            finally
            {
                log.Complete();
                IsExecutingFollowUp = false;
            }
        }

        public async Task RestoreBackupBranchAsync()
        {
            if (IsRestoringBackup || string.IsNullOrWhiteSpace(_restorableBackupRef))
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
                    _restorableBackupRef,
                    _completedOperationId,
                    _journalStore,
                    log).ConfigureAwait(true);

                HasRestoreError = !restore.Succeeded;
                RestoreFeedbackMessage = restore.Summary;
                if (restore.Succeeded)
                {
                    CanRestoreBackupBranch = false;
                    _repo.MarkBranchesDirtyManually();
                }
            }
            finally
            {
                log.Complete();
                IsRestoringBackup = false;
            }
        }

        private async Task LoadPreflightAsync(bool probeRemote)
        {
            IsLoadingPreflight = true;
            try
            {
                _preflight = await Commands.WorktreeDeletionService.InspectWorktreeAsync(
                    _repo.FullPath,
                    Target.FullPath,
                    probeUpstreamRemote: probeRemote,
                    includeAttachedBranchPreflight: true).ConfigureAwait(true);

                if (probeRemote)
                    _remoteProbe = _preflight?.AttachedBranchPreflight?.RemoteProbe;

                ApplyPreflightBindings();
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

            var branchPre = _preflight?.AttachedBranchPreflight;
            if (branchPre == null || !branchPre.HasConfiguredUpstream)
                return;

            if (!forceRefresh && _remoteProbe != null)
                return;

            IsProbingRemote = true;
            try
            {
                _remoteProbe = await Commands.BranchDeletionService.ProbeRemoteBranchAsync(
                    _preflight.MainRepoPath,
                    branchPre.RemoteName,
                    branchPre.RemoteBranchName).ConfigureAwait(true);

                if (_remoteProbe is { QuerySucceeded: false })
                    InlineErrorMessage = _remoteProbe.ErrorSummary;
                else if (_remoteProbe is { QuerySucceeded: true })
                    InlineErrorMessage = string.Empty;

                OnPropertyChanged(nameof(RedactedRemoteUrl));
                OnPropertyChanged(nameof(RemoteLiveStatusText));
                NotifyPopupButtonsChanged();
            }
            finally
            {
                IsProbingRemote = false;
            }
        }

        private void ApplyPreflightBindings()
        {
            BlockingFiles.Clear();
            if (_preflight != null)
            {
                AddFileGroup(_preflight.StagedFiles, "暂存", Brushes.SeaGreen);
                AddFileGroup(_preflight.UnstagedFiles, "未暂存", Brushes.Goldenrod);
                AddFileGroup(_preflight.ConflictedFiles, "冲突", Brushes.IndianRed);
                AddFileGroup(_preflight.UntrackedFiles, "未跟踪", Brushes.SteelBlue);
                AddFileGroup(_preflight.IgnoredFiles, "已忽略", Brushes.SlateGray);
            }

            if (_preflight is { RequiresForceToDeleteFiles: false } && _force)
                _force = false;

            OnPropertyChanged(nameof(Force));
            OnPropertyChanged(nameof(IsDetached));
            OnPropertyChanged(nameof(BranchDisplay));
            OnPropertyChanged(nameof(HeadShortSha));
            OnPropertyChanged(nameof(SyncSummaryText));
            OnPropertyChanged(nameof(IsTargetOpenInGitNest));
            OnPropertyChanged(nameof(IsDirectoryMissing));
            OnPropertyChanged(nameof(IsLocked));
            OnPropertyChanged(nameof(LockReasonDisplay));
            OnPropertyChanged(nameof(CanUnlockWorktree));
            OnPropertyChanged(nameof(HasNestedRepoOrSubmodule));
            OnPropertyChanged(nameof(NestedPathsDisplay));
            OnPropertyChanged(nameof(StagedCount));
            OnPropertyChanged(nameof(UnstagedCount));
            OnPropertyChanged(nameof(ConflictedCount));
            OnPropertyChanged(nameof(UntrackedCount));
            OnPropertyChanged(nameof(IgnoredCount));
            OnPropertyChanged(nameof(TotalBlockingFiles));
            OnPropertyChanged(nameof(HasInspectableFiles));
            OnPropertyChanged(nameof(FileBreakdownSummary));
            OnPropertyChanged(nameof(CanShowForceOption));
            OnPropertyChanged(nameof(ShowForceLossWarning));
            OnPropertyChanged(nameof(ForceLossWarningText));
            OnPropertyChanged(nameof(CanOfferLocalBranchDelete));
            OnPropertyChanged(nameof(HasConfiguredUpstream));
            OnPropertyChanged(nameof(ShowRemoteBranchOption));
            OnPropertyChanged(nameof(IsUpstreamNameDifferent));
            OnPropertyChanged(nameof(ConfiguredUpstreamFriendly));
            OnPropertyChanged(nameof(DeleteLocalBranchOptionText));
            OnPropertyChanged(nameof(DeleteRemoteBranchOptionText));
            OnPropertyChanged(nameof(ShowCleanupBreakdown));
            OnPropertyChanged(nameof(ShowRemoteTargetRow));
            OnPropertyChanged(nameof(RedactedRemoteUrl));
            OnPropertyChanged(nameof(RemoteLiveStatusText));
            NotifyPopupButtonsChanged();
        }

        private void AddFileGroup(IReadOnlyList<string> paths, string badge, IBrush brush)
        {
            foreach (var p in paths)
            {
                BlockingFiles.Add(new WorktreeFilePreviewItem
                {
                    CategoryBadge = badge,
                    RelativePath = p,
                    BadgeBackground = brush,
                });
            }
        }

        private Repository _repo;
        private readonly LauncherPage _hostPage;
        private LauncherPage _switchedHostPage;
        private readonly Models.OperationJournalStore _journalStore;
        private readonly Task _preflightTask;
        private Models.WorktreeInspectPreflight _preflight;
        private Models.RemoteBranchProbeResult _remoteProbe;

        private bool _isLoadingPreflight;
        private bool _isProbingRemote;
        private bool _isUnlockingWorktree;
        private bool _isExecutingFollowUp;
        private bool _unlockAcknowledged;
        private bool _force;
        private bool _deleteLocalBranch;
        private bool _deleteRemoteBranch;
        private string _inlineErrorMessage = string.Empty;

        private bool _hasResultView;
        private string _resultSummary = string.Empty;
        private bool _requiresFollowUpForceLocalBranch;
        private string _remainingLocalBranchName = string.Empty;
        private int _followUpUnmergedCommitCount;
        private string _followUpRemoteLeaseSha = string.Empty;
        private string _followUpPushUrl;
        private bool? _followUpRemoteExists;
        private bool _followUpDeleteRemote;

        private string _completedOperationId = string.Empty;
        private string _restorableBackupRef = string.Empty;
        private bool _canRestoreBackupBranch;
        private string _restorableBranchName = string.Empty;
        private string _restoreFeedbackMessage = string.Empty;
        private bool _hasRestoreError;
        private bool _isRestoringBackup;
    }
}
