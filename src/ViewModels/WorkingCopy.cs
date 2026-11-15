using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;

namespace SourceGit.ViewModels
{
    public partial class WorkingCopy : ObservableObject
    {
        public Repository Repository
        {
            get => _repo;
        }

        public bool IncludeUntracked
        {
            get => _repo.IncludeUntracked;
            set
            {
                if (_repo.IncludeUntracked != value)
                {
                    _repo.IncludeUntracked = value;
                    OnPropertyChanged();
                }
            }
        }

        public bool HasRemotes
        {
            get => _hasRemotes;
            set => SetProperty(ref _hasRemotes, value);
        }

        public bool HasUnsolvedConflicts
        {
            get => _hasUnsolvedConflicts;
            set
            {
                if (SetProperty(ref _hasUnsolvedConflicts, value))
                {
                    OnPropertyChanged(nameof(HasInProgressBanner));
                    OnPropertyChanged(nameof(CanContinueInProgress));
                    OnPropertyChanged(nameof(ShowExternalInProgressFooter));
                    OnPropertyChanged(nameof(HasInlineNoticeArea));
                    OnPropertyChanged(nameof(CanOpenAmendLastCommit));
                }
            }
        }

        public bool CanSwitchBranchDirectly
        {
            get;
            set;
        } = true;

        public InProgressContext InProgressContext
        {
            get => _inProgressContext;
            private set => SetProperty(ref _inProgressContext, value);
        }

        public Models.RepositoryInProgressInspection InProgressInspection
        {
            get => _inProgressInspection;
            private set
            {
                if (SetProperty(ref _inProgressInspection, value))
                    NotifyInProgressOwnershipChanged();
            }
        }

        public bool HasInProgressBanner => _inProgressContext != null || _hasUnsolvedConflicts;

        public string InProgressOriginLabel
        {
            get
            {
                if (!HasInProgressBanner)
                    return string.Empty;

                if (_inProgressInspection == null)
                    return App.Text("WorkingCopy.Conflicts.Origin.External");

                if (_inProgressInspection.IsGitNestDontCommitMerge)
                    return App.Text("WorkingCopy.InProgress.DontCommitOrigin");

                return _inProgressInspection.Origin switch
                {
                    Models.ConflictOwnershipOrigin.GitNestOwnedClean or
                    Models.ConflictOwnershipOrigin.GitNestOwnedWithExternalIntervention
                        => App.Text("WorkingCopy.Conflicts.Origin.GitNest"),
                    Models.ConflictOwnershipOrigin.ExternalOrUnknown
                        => App.Text("WorkingCopy.Conflicts.Origin.External"),
                    _ => string.Empty,
                };
            }
        }

        public string InProgressConflictBadgeText
        {
            get
            {
                var count = ConflictedChanges.Count;
                if (count == 0 && _inProgressInspection is { ConflictedPaths.Count: > 0 })
                    count = _inProgressInspection.ConflictedPaths.Count;

                return count > 0 ? App.Text("WorkingCopy.Conflicts.FilesHeader", count) : string.Empty;
            }
        }

        public string InProgressBannerTooltip
        {
            get
            {
                if (!HasInProgressBanner)
                    return string.Empty;

                if (_inProgressInspection == null)
                    return App.Text("WorkingCopy.Conflicts.ReadOnlyNotice");

                return string.IsNullOrWhiteSpace(_inProgressInspection.Detail)
                    ? _inProgressInspection.Headline
                    : $"{_inProgressInspection.Headline}\n{_inProgressInspection.Detail}";
            }
        }

        public bool CanAbortInProgressFromBanner =>
            _inProgressContext != null &&
            _inProgressInspection is { CanOfferSafeAbort: true };

        public bool CanContinueInProgress =>
            _inProgressContext is MergeInProgress &&
            !_hasUnsolvedConflicts &&
            _inProgressInspection is { IsGitNestDontCommitMerge: true };

        public bool ShowExternalInProgressFooter =>
            HasInProgressBanner && !CanContinueInProgress;

        public List<Models.Change> ConflictedChanges =>
            _cached.FindAll(static x => x.IsConflicted);

        public bool IsStaging
        {
            get => _isStaging;
            private set => SetProperty(ref _isStaging, value);
        }

        public bool IsUnstaging
        {
            get => _isUnstaging;
            private set => SetProperty(ref _isUnstaging, value);
        }

        public bool IsCommitting
        {
            get => _isCommitting;
            private set
            {
                if (SetProperty(ref _isCommitting, value))
                {
                    _repo.NotifyIsSkippingOrAbortingMergeChanged();
                    OnPropertyChanged(nameof(CanOpenAmendLastCommit));
                }
            }
        }

        public bool EnableSignOff
        {
            get => _repo.UIStates.EnableSignOffForCommit;
            set => _repo.UIStates.EnableSignOffForCommit = value;
        }

        public bool NoVerifyOnCommit
        {
            get => _repo.UIStates.NoVerifyOnCommit;
            set => _repo.UIStates.NoVerifyOnCommit = value;
        }

        public bool CanOpenAmendLastCommit => !HasInProgressBanner && !_isCommitting;

        public void OpenAmendLastCommit()
        {
            if (!CanOpenAmendLastCommit || !_repo.CanCreatePopup())
                return;

            _repo.ShowPopup(new AmendLastCommit(_repo, this));
        }

        public string Filter
        {
            get => _filter;
            set
            {
                if (SetProperty(ref _filter, value))
                {
                    if (_isLoadingData)
                        return;

                    VisibleUnstaged = GetVisibleChanges(_unstaged);
                    VisibleStaged = GetVisibleChanges(_staged);
                    SelectedUnstaged = new(null);
                    SelectedStaged = new(null);
                }
            }
        }

        public List<Models.Change> Unstaged
        {
            get => _unstaged;
            private set
            {
                if (SetProperty(ref _unstaged, value))
                    OnPropertyChanged(nameof(UnstagedCountInfo));
            }
        }

        public List<Models.Change> VisibleUnstaged
        {
            get => _visibleUnstaged;
            private set
            {
                if (SetProperty(ref _visibleUnstaged, value))
                    OnPropertyChanged(nameof(UnstagedCountInfo));
            }
        }

        public List<Models.Change> Staged
        {
            get => _staged;
            private set
            {
                if (SetProperty(ref _staged, value))
                    OnPropertyChanged(nameof(StagedCountInfo));
            }
        }

        public List<Models.Change> VisibleStaged
        {
            get => _visibleStaged;
            private set
            {
                if (SetProperty(ref _visibleStaged, value))
                    OnPropertyChanged(nameof(StagedCountInfo));
            }
        }

        public string UnstagedCountInfo
        {
            get => string.IsNullOrEmpty(_filter) ? $"({_unstaged.Count})" : $"({_visibleUnstaged.Count}/{_unstaged.Count})";
        }

        public string StagedCountInfo
        {
            get => string.IsNullOrEmpty(_filter) ? $"({_staged.Count})" : $"({_visibleStaged.Count}/{_staged.Count})";
        }

        public ChangeSelection SelectedUnstaged
        {
            get => _selectedUnstaged;
            set
            {
                if (SetProperty(ref _selectedUnstaged, value))
                {
                    if (value is { Count: > 0 })
                    {
                        _isLoadingData = true;
                        if (_selectedStaged is { Count: > 0 })
                            SelectedStaged = new(null);
                        _isLoadingData = false;
                    }

                    if (!_isLoadingData)
                        UpdateDetail();
                }
            }
        }

        public ChangeSelection SelectedStaged
        {
            get => _selectedStaged;
            set
            {
                if (SetProperty(ref _selectedStaged, value))
                {
                    if (value is { Count: > 0 })
                    {
                        _isLoadingData = true;
                        if (_selectedUnstaged is { Count: > 0 })
                            SelectedUnstaged = new(null);
                        _isLoadingData = false;
                    }

                    if (!_isLoadingData)
                        UpdateDetail();
                }
            }
        }

        public object DetailContext
        {
            get => _detailContext;
            private set => SetProperty(ref _detailContext, value);
        }

        public string CommitMessage
        {
            get => _commitMessage;
            set => SetProperty(ref _commitMessage, value);
        }

        public WorkingCopy(Repository repo)
        {
            _repo = repo;
        }

        public void SetData(List<Models.Change> changes)
        {
            if (!IsChanged(_cached, changes))
            {
                HasUnsolvedConflicts = _cached.Find(x => x.IsConflicted) != null;
                UpdateInProgressState();
                UpdateDetail();
                return;
            }

            var lastSelectedUnstaged = new HashSet<string>();
            if (_selectedUnstaged is { Count: > 0 })
            {
                foreach (var c in _selectedUnstaged.Changes)
                    lastSelectedUnstaged.Add(c.Path);
            }

            var unstaged = new List<Models.Change>();
            var visibleUnstaged = new List<Models.Change>();
            var selectedUnstaged = new List<Models.Change>();
            var noFilter = string.IsNullOrEmpty(_filter);
            var conflicts = new List<Models.Change>();
            var canSwitchDirectly = true;
            foreach (var c in changes)
            {
                if (c.WorkTree != Models.ChangeState.None)
                {
                    unstaged.Add(c);

                    if (c.IsConflicted)
                        conflicts.Add(c);

                    if (noFilter || c.Path.Contains(_filter, StringComparison.OrdinalIgnoreCase))
                    {
                        visibleUnstaged.Add(c);
                        if (lastSelectedUnstaged.Contains(c.Path))
                            selectedUnstaged.Add(c);
                    }
                }

                if (!canSwitchDirectly)
                    continue;

                if (c.WorkTree == Models.ChangeState.Untracked || c.Index == Models.ChangeState.Added)
                    continue;

                canSwitchDirectly = false;
            }

            var staged = GetStagedChanges(changes);
            var visibleStaged = GetVisibleChanges(staged);
            var selectedStaged = new List<Models.Change>();
            if (_selectedStaged is { Count: > 0 })
            {
                var set = new HashSet<string>();
                foreach (var c in _selectedStaged.Changes)
                    set.Add(c.Path);

                foreach (var c in visibleStaged)
                {
                    if (set.Contains(c.Path))
                        selectedStaged.Add(c);
                }
            }

            if (selectedUnstaged.Count == 0 && selectedStaged.Count == 0 && conflicts.Count > 0)
            {
                var firstConflict = FindFirstConflict(conflicts);
                selectedUnstaged.Add(firstConflict);
            }

            _isLoadingData = true;
            _cached = changes;
            HasUnsolvedConflicts = conflicts.Count > 0;
            CanSwitchBranchDirectly = canSwitchDirectly;
            VisibleUnstaged = visibleUnstaged;
            VisibleStaged = visibleStaged;
            Unstaged = unstaged;
            Staged = staged;
            SelectedUnstaged = new(selectedUnstaged);
            SelectedStaged = new(selectedStaged);
            _isLoadingData = false;

            UpdateInProgressState();
            UpdateDetail();
            UpdatePartialStagingFlag();
            _ = RefreshStagedSnapshotAsync();
        }

        public async Task StageChangesAsync(List<Models.Change> changes, Models.Change next, bool skipPartialStageConfirm = false)
        {
            var canStaged = await GetCanStageChangesAsync(changes);
            var count = canStaged.Count;
            if (count == 0)
                return;

            if (!skipPartialStageConfirm)
            {
                var partialPaths = FindPartiallyStagedSelectionPaths(canStaged);
                if (partialPaths.Count > 0)
                {
                    var preview = partialPaths.Count <= 3
                        ? string.Join(", ", partialPaths)
                        : $"{string.Join(", ", partialPaths.GetRange(0, 3))} (+{partialPaths.Count - 3})";
                    var confirm = await App.AskConfirmAsync(App.Text("WorkingCopy.PartialFullStageConfirm", preview));
                    if (!confirm)
                        return;
                }
            }

            IsStaging = true;
            _selectedUnstaged = new(next != null ? new List<Models.Change> { next } : null);

            using var lockWatcher = _repo.LockWatcher();

            var log = _repo.CreateLog("Stage");
            var paths = canStaged.ConvertAll(c => c.Path);
            var (ok, lockStatus, diag) = await Commands.GitSyncService.StagePathsAsync(_repo.FullPath, paths, log, force: false);
            log.Complete();

            if (!ok)
            {
                var msg = lockStatus.IsLocked ? lockStatus.Summary : (string.IsNullOrWhiteSpace(diag) ? "暂存失败。" : diag);
                SetCommitAlert(msg);
                _repo.SendNotification(msg, true);
            }
            else
            {
                await RefreshStagedSnapshotAsync();
            }

            _repo.MarkWorkingCopyDirtyManually();
            IsStaging = false;
        }

        public async Task UnstageChangesAsync(List<Models.Change> changes, Models.Change next)
        {
            var count = changes.Count;
            if (count == 0)
                return;

            IsUnstaging = true;
            _selectedStaged = new(next != null ? new List<Models.Change> { next } : null);

            using var lockWatcher = _repo.LockWatcher();

            var log = _repo.CreateLog("Unstage");
            var paths = new List<string>();
            foreach (var c in changes)
            {
                paths.Add(c.Path);
                if (c.Index == Models.ChangeState.Renamed && !string.IsNullOrEmpty(c.OriginalPath))
                    paths.Add(c.OriginalPath);
            }

            var (ok, lockStatus, diag) = await Commands.GitSyncService.UnstagePathsAsync(
                _repo.FullPath,
                paths,
                log: log);
            log.Complete();

            if (!ok)
            {
                var msg = lockStatus.IsLocked ? lockStatus.Summary : (string.IsNullOrWhiteSpace(diag) ? "取消暂存失败。" : diag);
                SetCommitAlert(msg);
                _repo.SendNotification(msg, true);
            }
            else
            {
                await RefreshStagedSnapshotAsync();
            }

            _repo.MarkWorkingCopyDirtyManually();
            IsUnstaging = false;
        }

        public void DiscardAllChanges()
        {
            if (_repo.CanCreatePopup())
                _repo.ShowPopup(new Discard(_repo));
        }

        public void Discard(List<Models.Change> changes, Models.Change next)
        {
            if (_repo.CanCreatePopup())
                _repo.ShowPopup(new Discard(_repo, changes, next));
        }

        public void DiscardWithBaseline(List<Models.Change> changes, Models.Change next, Models.DiscardBaselineKind baseline)
        {
            if (_repo.CanCreatePopup())
                _repo.ShowPopup(new Discard(_repo, changes, next, baseline));
        }

        public void ClearFilter()
        {
            Filter = string.Empty;
        }

        public async Task UseTheirsAsync(List<Models.Change> changes)
        {
            using var lockWatcher = _repo.LockWatcher();

            var files = new List<string>();
            var needStage = new List<string>();
            var log = _repo.CreateLog("Use Theirs");

            foreach (var change in changes)
            {
                if (!change.IsConflicted)
                    continue;

                if (change.ConflictReason is Models.ConflictReason.BothDeleted or Models.ConflictReason.DeletedByThem or Models.ConflictReason.AddedByUs)
                {
                    var fullpath = Path.Combine(_repo.FullPath, change.Path);
                    if (File.Exists(fullpath))
                        File.Delete(fullpath);

                    needStage.Add(change.Path);
                }
                else
                {
                    files.Add(change.Path);
                }
            }

            if (files.Count > 0)
            {
                var succ = await new Commands.Checkout(_repo.FullPath).Use(log).UseTheirsAsync(files);
                if (succ)
                    needStage.AddRange(files);
            }

            if (needStage.Count > 0)
            {
                var pathSpecFile = Path.GetTempFileName();
                await File.WriteAllLinesAsync(pathSpecFile, needStage);
                await new Commands.Add(_repo.FullPath, pathSpecFile).Use(log).ExecAsync();
                File.Delete(pathSpecFile);
            }

            log.Complete();
            _repo.MarkWorkingCopyDirtyManually();
        }

        public async Task UseMineAsync(List<Models.Change> changes)
        {
            using var lockWatcher = _repo.LockWatcher();

            var files = new List<string>();
            var needStage = new List<string>();
            var log = _repo.CreateLog("Use Mine");

            foreach (var change in changes)
            {
                if (!change.IsConflicted)
                    continue;

                if (change.ConflictReason is Models.ConflictReason.BothDeleted or Models.ConflictReason.DeletedByUs or Models.ConflictReason.AddedByThem)
                {
                    var fullpath = Path.Combine(_repo.FullPath, change.Path);
                    if (File.Exists(fullpath))
                        File.Delete(fullpath);

                    needStage.Add(change.Path);
                }
                else
                {
                    files.Add(change.Path);
                }
            }

            if (files.Count > 0)
            {
                var succ = await new Commands.Checkout(_repo.FullPath).Use(log).UseMineAsync(files);
                if (succ)
                    needStage.AddRange(files);
            }

            if (needStage.Count > 0)
            {
                var pathSpecFile = Path.GetTempFileName();
                await File.WriteAllLinesAsync(pathSpecFile, needStage);
                await new Commands.Add(_repo.FullPath, pathSpecFile).Use(log).ExecAsync();
                File.Delete(pathSpecFile);
            }

            log.Complete();
            _repo.MarkWorkingCopyDirtyManually();
        }

        public async Task<bool> UseExternalMergeToolAsync(Models.Change change)
        {
            return await new Commands.MergeTool(_repo.FullPath, change?.Path).OpenAsync();
        }

        public void UseExternalDiffTool(Models.Change change, bool isUnstaged)
        {
            new Commands.DiffTool(_repo.FullPath, new Models.DiffOption(change, isUnstaged)).Open();
        }

        public async Task ContinueMergeAsync()
        {
            if (_inProgressContext != null)
            {
                if (!CanContinueInProgress)
                {
                    _repo.SendNotification(App.Text("WorkingCopy.Conflicts.ExternalAbortDisabled"), true);
                    return;
                }

                using var lockWatcher = _repo.LockWatcher();
                IsCommitting = true;

                var mergeMsgFile = Path.Combine(_repo.GitDir, "MERGE_MSG");
                if (File.Exists(mergeMsgFile) && !string.IsNullOrWhiteSpace(_commitMessage))
                    await File.WriteAllTextAsync(mergeMsgFile, _commitMessage);

                var log = _repo.CreateLog($"Continue {_inProgressContext.Name}");
                await _inProgressContext.ContinueAsync(log);
                log.Complete();

                CommitMessage = string.Empty;
                IsCommitting = false;
            }
            else
            {
                _repo.MarkWorkingCopyDirtyManually();
            }
        }

        public async Task SkipMergeAsync()
        {
            if (_inProgressContext != null)
            {
                _repo.SendNotification(App.Text("WorkingCopy.Conflicts.ExternalAbortDisabled"), true);
                return;
            }
            else
            {
                _repo.MarkWorkingCopyDirtyManually();
            }
        }

        public void FocusFirstConflict()
        {
            var conflict = _cached.Find(static x => x.IsConflicted);
            if (conflict == null)
                return;

            SelectedUnstaged = new ChangeSelection(new List<Models.Change> { conflict });
            SetDetail(conflict, true);
        }

        public async Task RefreshInProgressStateAsync()
        {
            _repo.MarkWorkingCopyDirtyManually();
            await RefreshInProgressInspectionAsync();
        }

        public async Task AbortMergeAsync()
        {
            if (_inProgressContext != null)
            {
                if (!CanAbortInProgressFromBanner)
                {
                    _repo.SendNotification(App.Text("WorkingCopy.Conflicts.ExternalAbortDisabled"), true);
                    return;
                }

                using var lockWatcher = _repo.LockWatcher();
                IsCommitting = true;

                var log = _repo.CreateLog($"Abort {_inProgressContext.Name}");
                await _inProgressContext.AbortAsync(log);
                log.Complete();

                CommitMessage = string.Empty;
                IsCommitting = false;
            }
            else
            {
                _repo.MarkWorkingCopyDirtyManually();
            }
        }

        public async Task ClearCommitMessageHistoryAsync()
        {
            var sure = await App.AskConfirmAsync(App.Text("WorkingCopy.ClearCommitHistories.Confirm"));
            if (sure)
                _repo.UIStates.RecentCommitMessages.Clear();
        }

        public async Task CommitAsync(bool autoStage, bool autoPush)
        {
            if (string.IsNullOrWhiteSpace(_commitMessage))
                return;

            if (!_repo.CanCreatePopup())
            {
                _repo.SendNotification("Repository has an unfinished job! Please wait!", true);
                return;
            }

            if (autoStage && HasUnsolvedConflicts)
            {
                _repo.SendNotification("Repository has unsolved conflict(s). Auto-stage and commit is disabled!", true);
                return;
            }

            if (_inProgressContext != null ||
                Commands.CaptureOperationSnapshot.DetectInProgressKind(_repo.GitDir) != Models.GitInProgressKind.None)
            {
                SetCommitAlert(App.Text("WorkingCopy.ExternalInProgressBlocked"));
                return;
            }

            DismissCommitAlert();
            DismissTwoStepBanner();

            var baselineSnapshot = _displayedSnapshot ?? await Commands.QueryStagedSnapshot.CaptureAsync(_repo.FullPath);

            var identity = await new Commands.Config(_repo.FullPath).ReadCommitIdentityAsync();
            if (!identity.IsComplete)
            {
                IdentityAuthorName = identity.Name;
                IdentityAuthorEmail = identity.Email;
                _pendingIdentityCommit = true;
                _pendingIdentityAutoStage = autoStage;
                _pendingIdentityAutoPush = autoPush;
                ShowIdentityPrompt = true;
                NotifyInlineNoticeVisibility();
                return;
            }

            if (_repo.CurrentBranch is { IsDetachedHead: true })
            {
                var msg = App.Text("WorkingCopy.ConfirmCommitWithDetachedHead");
                var sure = await App.AskConfirmAsync(msg);
                if (!sure)
                    return;
            }

            if (!string.IsNullOrEmpty(_filter) && _staged.Count > _visibleStaged.Count)
            {
                var msg = App.Text("WorkingCopy.ConfirmCommitWithFilter", _staged.Count, _visibleStaged.Count, _staged.Count - _visibleStaged.Count);
                var sure = await App.AskConfirmAsync(msg);
                if (!sure)
                    return;
            }

            var allowEmpty = false;
            if ((!autoStage && _staged.Count == 0) || (autoStage && _cached.Count == 0))
            {
                var rs = await App.AskConfirmEmptyCommitAsync(_cached.Count > 0, _selectedUnstaged is { Count: > 0 });
                if (rs == Models.ConfirmEmptyCommitResult.Cancel)
                    return;

                if (rs == Models.ConfirmEmptyCommitResult.StageAllAndCommit)
                {
                    autoStage = true;
                }
                else if (rs == Models.ConfirmEmptyCommitResult.StageSelectedAndCommit)
                {
                    await StageChangesAsync(_selectedUnstaged.Changes, null, skipPartialStageConfirm: true);
                    baselineSnapshot = await Commands.QueryStagedSnapshot.CaptureAsync(_repo.FullPath);
                }
                else if (rs == Models.ConfirmEmptyCommitResult.CreateEmptyCommit)
                {
                    allowEmpty = true;
                }
            }

            using var lockWatcher = _repo.LockWatcher();
            IsCommitting = true;
            _repo.UIStates.AddRecentCommitMessage(_commitMessage);

            try
            {
                if (autoStage && _unstaged.Count > 0)
                {
                    await StageChangesAsync(_unstaged, null, skipPartialStageConfirm: false);
                    baselineSnapshot = await Commands.QueryStagedSnapshot.CaptureAsync(_repo.FullPath);
                }

                var log = _repo.CreateLog("Commit");
                var commitResult = await Commands.GitSyncService.CommitAsync(
                    _repo.FullPath,
                    new Models.CommitExecutionOptions
                    {
                        Message = _commitMessage,
                        SignOff = EnableSignOff,
                        NoVerify = NoVerifyOnCommit,
                        Amend = false,
                        ResetAuthor = false,
                        AllowEmpty = allowEmpty,
                        CheckIdentity = true,
                        ExpectedSnapshot = baselineSnapshot,
                    },
                    log,
                    journalStore: Models.OperationJournalStore.Instance);
                log.Complete();

                if (!commitResult.Succeeded)
                {
                    if (commitResult.FailureKind == Models.CommitFailureKind.MissingCommitIdentity)
                    {
                        IdentityAuthorName = commitResult.Identity?.Name ?? string.Empty;
                        IdentityAuthorEmail = commitResult.Identity?.Email ?? string.Empty;
                        _pendingIdentityCommit = true;
                        _pendingIdentityAutoStage = autoStage;
                        _pendingIdentityAutoPush = autoPush;
                        ShowIdentityPrompt = true;
                        NotifyInlineNoticeVisibility();
                    }
                    else if (commitResult.FailureKind == Models.CommitFailureKind.StagedSnapshotChanged)
                    {
                        _displayedSnapshot = commitResult.CurrentSnapshot;
                        SetCommitAlert(App.Text("WorkingCopy.SnapshotStaleBanner"));
                        _repo.MarkWorkingCopyDirtyManually();
                    }
                    else
                    {
                        SetCommitAlert(commitResult.Summary);
                    }

                    return;
                }

                CommitMessage = string.Empty;
                _displayedSnapshot = commitResult.CurrentSnapshot;

                if (autoPush && _repo.Remotes.Count > 0)
                {
                    var pushOpts = TryResolveUpstreamPushOptions();
                    if (pushOpts != null)
                    {
                        var pushLog = _repo.CreateLog("Push");
                        var session = new Models.CommitAndPushSession
                        {
                            RepoPath = _repo.FullPath,
                            CommitResult = commitResult,
                            PushAttempted = false,
                            PendingPushOptions = pushOpts,
                        };

                        var afterPush = await Commands.GitSyncService.RetryPushSessionAsync(
                            session,
                            pushOpts,
                            pushLog,
                            journalStore: Models.OperationJournalStore.Instance);
                        pushLog.Complete();
                        ApplyTwoStepSessionState(afterPush);

                        if (afterPush.IsFullySucceeded)
                            _repo.SendNotification(afterPush.PushResult.Summary);
                        else
                            _repo.SendNotification(afterPush.Summary, true);
                    }
                    else
                    {
                        ApplyTwoStepSessionState(new Models.CommitAndPushSession
                        {
                            RepoPath = _repo.FullPath,
                            CommitResult = commitResult,
                            PushAttempted = false,
                        });
                        OpenPushForPendingSession();
                    }
                }
                else
                {
                    _repo.SendNotification(commitResult.Summary);
                }

                _repo.MarkBranchesDirtyManually();
                _repo.RefreshSubmodules();
            }
            finally
            {
                IsCommitting = false;
            }
        }

        private List<Models.Change> GetVisibleChanges(List<Models.Change> changes)
        {
            if (string.IsNullOrEmpty(_filter))
                return changes;

            var visible = new List<Models.Change>();

            foreach (var c in changes)
            {
                if (c.Path.Contains(_filter, StringComparison.OrdinalIgnoreCase))
                    visible.Add(c);
            }

            return visible;
        }

        private async Task<List<Models.Change>> GetCanStageChangesAsync(List<Models.Change> changes)
        {
            if (!HasUnsolvedConflicts)
                return changes;

            var outs = new List<Models.Change>();
            foreach (var c in changes)
            {
                if (c.IsConflicted)
                {
                    if (c.ConflictReason is Models.ConflictReason.BothAdded or Models.ConflictReason.BothModified)
                    {
                        var state = await new Commands.QueryConflictFileState(_repo.FullPath, c).GetResultAsync();
                        if (state != Models.ConflictFileState.Resolved)
                            continue;
                    }
                    else
                    {
                        continue;
                    }
                }

                outs.Add(c);
            }

            return outs;
        }

        private List<Models.Change> GetStagedChanges(List<Models.Change> cached)
        {
            var rs = new List<Models.Change>();
            foreach (var c in cached)
            {
                if (c.Index != Models.ChangeState.None)
                    rs.Add(c);
            }
            return rs;
        }

        private void UpdateDetail()
        {
            if (_selectedUnstaged is { Count: 1, HasFolder: false })
                SetDetail(_selectedUnstaged.Changes[0], true);
            else if (_selectedStaged is { Count: 1, HasFolder: false })
                SetDetail(_selectedStaged.Changes[0], false);
            else
                SetDetail(null, false);
        }

        private void UpdateInProgressState()
        {
            var oldType = _inProgressContext != null ? _inProgressContext.GetType() : null;

            if (File.Exists(Path.Combine(_repo.GitDir, "CHERRY_PICK_HEAD")))
                InProgressContext = new CherryPickInProgress(_repo);
            else if (Directory.Exists(Path.Combine(_repo.GitDir, "rebase-merge")))
                InProgressContext = new RebaseInProgress(_repo);
            else if (Directory.Exists(Path.Combine(_repo.GitDir, "rebase-apply")))
                InProgressContext = File.Exists(Path.Combine(_repo.GitDir, "rebase-apply", "applying"))
                    ? new AmInProgress(_repo)
                    : new RebaseInProgress(_repo);
            else if (File.Exists(Path.Combine(_repo.GitDir, "REVERT_HEAD")))
                InProgressContext = new RevertInProgress(_repo);
            else if (File.Exists(Path.Combine(_repo.GitDir, "MERGE_HEAD")))
                InProgressContext = new MergeInProgress(_repo);
            else if (File.Exists(Path.Combine(_repo.GitDir, "BISECT_LOG")))
                InProgressContext = new BisectInProgress();
            else
                InProgressContext = null;

            UpdateInProgressOwnershipInspection();

            if (_inProgressContext == null)
            {
                if (oldType != null && IsAutoLoadedCommitMessage())
                    CommitMessage = string.Empty;
                else if (string.IsNullOrEmpty(_commitMessage))
                    LoadCommitMessageFromFile(Path.Combine(_repo.GitDir, "MERGE_MSG"));

                return;
            }

            if (_inProgressContext.GetType() == oldType && !string.IsNullOrEmpty(_commitMessage))
                return;

            if (LoadCommitMessageFromFile(Path.Combine(_repo.GitDir, "MERGE_MSG")))
                return;

            if (_inProgressContext is RebaseInProgress rebasing)
            {
                if (!LoadCommitMessageFromFile(Path.Combine(_repo.GitDir, "rebase-merge", "message")) &&
                    !LoadCommitMessageFromFile(Path.Combine(_repo.GitDir, "rebase-apply", "msg")) &&
                    rebasing.StoppedAt != null &&
                    !string.IsNullOrEmpty(rebasing.StoppedAt.SHA))
                {
                    var message = new Commands.QueryCommitFullMessage(_repo.FullPath, rebasing.StoppedAt.SHA).GetResult();
                    SetAutoLoadedCommitMessage(message);
                }
            }
            else if (_inProgressContext is AmInProgress)
            {
                LoadCommitMessageFromFile(Path.Combine(_repo.GitDir, "rebase-apply", "final-commit"));
            }
        }

        private bool LoadCommitMessageFromFile(string file)
        {
            if (!File.Exists(file))
                return false;

            var msg = File.ReadAllText(file).Trim();
            if (string.IsNullOrEmpty(msg))
                return false;

            SetAutoLoadedCommitMessage(msg);
            return true;
        }

        private void SetAutoLoadedCommitMessage(string msg)
        {
            _autoLoadedCommitMessage = msg;
            CommitMessage = msg;
        }

        private bool IsAutoLoadedCommitMessage()
        {
            return !string.IsNullOrEmpty(_autoLoadedCommitMessage) &&
                string.Equals(_autoLoadedCommitMessage, _commitMessage, StringComparison.Ordinal);
        }

        private void SetDetail(Models.Change change, bool isUnstaged)
        {
            if (_isLoadingData)
                return;

            if (change == null)
                DetailContext = null;
            else if (change.IsConflicted)
                DetailContext = new Conflict(_repo, this, change);
            else
                DetailContext = new DiffContext(_repo.FullPath, new Models.DiffOption(change, isUnstaged), _detailContext as DiffContext);
        }

        private bool IsChanged(List<Models.Change> old, List<Models.Change> cur)
        {
            if (old.Count != cur.Count)
                return true;

            for (int idx = 0; idx < old.Count; idx++)
            {
                var o = old[idx];
                var c = cur[idx];
                if (!o.Path.Equals(c.Path, StringComparison.Ordinal) || o.Index != c.Index || o.WorkTree != c.WorkTree)
                    return true;
            }

            return false;
        }

        private Models.Change FindFirstConflict(List<Models.Change> conflicts)
        {
            if (conflicts.Count == 1 || Preferences.Instance.ChangeViewMode != Models.ChangeViewMode.Tree)
                return conflicts[0];

            var finder = (ChangeInTree l, ChangeInTree r) =>
            {
                var lDirs = l.PathSegments.Length - 1;
                var rDirs = r.PathSegments.Length - 1;
                if (lDirs == 0)
                {
                    if (rDirs == 0)
                        return Models.NumericSort.Compare(l.PathSegments[0], r.PathSegments[0]) <= 0 ? l : r;
                    else
                        return r;
                }
                else if (rDirs == 0)
                {
                    return l;
                }

                var min = Math.Min(lDirs, rDirs);
                for (var idx = 0; idx < min; idx++)
                {
                    var cmp = Models.NumericSort.Compare(l.PathSegments[idx], r.PathSegments[idx]);
                    if (cmp < 0)
                        return l;
                    else if (cmp > 0)
                        return r;
                }

                if (lDirs == rDirs)
                    return Models.NumericSort.Compare(l.PathSegments[^1], r.PathSegments[^1]) <= 0 ? l : r;

                return lDirs < rDirs ? r : l;
            };

            var collection = new List<ChangeInTree>();
            foreach (var c in conflicts)
                collection.Add(new ChangeInTree(c));

            var first = collection[0];
            for (var idx = 1; idx < collection.Count; idx++)
                first = finder(first, collection[idx]);

            return first.Change;
        }

        private class ChangeInTree
        {
            public Models.Change Change { get; set; }
            public string[] PathSegments { get; set; }

            public ChangeInTree(Models.Change change)
            {
                Change = change;
                PathSegments = change.Path.Split('/', StringSplitOptions.RemoveEmptyEntries);
            }
        }

        private Repository _repo = null;
        private bool _isLoadingData = false;
        private bool _isStaging = false;
        private bool _isUnstaging = false;
        private bool _isCommitting = false;
        private bool _hasRemotes = false;
        private List<Models.Change> _cached = [];
        private List<Models.Change> _unstaged = [];
        private List<Models.Change> _visibleUnstaged = [];
        private List<Models.Change> _staged = [];
        private List<Models.Change> _visibleStaged = [];
        private ChangeSelection _selectedUnstaged = new(null);
        private ChangeSelection _selectedStaged = new(null);
        private object _detailContext = null;
        private string _filter = string.Empty;
        private string _commitMessage = string.Empty;

        internal bool IsRefreshingInProgressOwnership => Volatile.Read(ref _inProgressOwnershipRefreshCount) > 0;

        private Models.GitInProgressKind GetExpectedInProgressKind()
        {
            return _inProgressContext switch
            {
                CherryPickInProgress => Models.GitInProgressKind.CherryPick,
                RebaseInProgress => Models.GitInProgressKind.Rebase,
                AmInProgress => Models.GitInProgressKind.Am,
                BisectInProgress => Models.GitInProgressKind.Bisect,
                RevertInProgress => Models.GitInProgressKind.Revert,
                MergeInProgress => Models.GitInProgressKind.Merge,
                _ => Models.GitInProgressKind.None,
            };
        }

        private void UpdateInProgressOwnershipInspection()
        {
            var expectedKind = GetExpectedInProgressKind();
            if (_inProgressContext == null && !_hasUnsolvedConflicts)
            {
                Interlocked.Increment(ref _inProgressInspectionVersion);
                if (_inProgressInspection != null)
                    InProgressInspection = null;
                else
                    NotifyInProgressOwnershipChanged();
                return;
            }

            if (_inProgressInspection != null &&
                expectedKind != Models.GitInProgressKind.None &&
                _inProgressInspection.InProgressKind != expectedKind)
            {
                InProgressInspection = null;
            }
            else
            {
                NotifyInProgressOwnershipChanged();
            }

            _ = RefreshInProgressInspectionAsync();
        }

        private async Task RefreshInProgressInspectionAsync()
        {
            var version = Interlocked.Increment(ref _inProgressInspectionVersion);
            Interlocked.Increment(ref _inProgressOwnershipRefreshCount);
            try
            {
                var inspection = await Commands.MergeRevertService
                    .InspectRepositoryInProgressAsync(_repo.FullPath);

                if (version != _inProgressInspectionVersion)
                    return;

                InProgressInspection = inspection;
                NotifyInProgressOwnershipChanged();
            }
            catch
            {
                if (version == _inProgressInspectionVersion)
                    NotifyInProgressOwnershipChanged();
            }
            finally
            {
                Interlocked.Decrement(ref _inProgressOwnershipRefreshCount);
            }
        }

        private void NotifyInProgressOwnershipChanged()
        {
            OnPropertyChanged(nameof(HasInProgressBanner));
            OnPropertyChanged(nameof(CanOpenAmendLastCommit));
            OnPropertyChanged(nameof(InProgressOriginLabel));
            OnPropertyChanged(nameof(InProgressConflictBadgeText));
            OnPropertyChanged(nameof(InProgressBannerTooltip));
            OnPropertyChanged(nameof(CanAbortInProgressFromBanner));
            OnPropertyChanged(nameof(CanContinueInProgress));
            OnPropertyChanged(nameof(ShowExternalInProgressFooter));
            OnPropertyChanged(nameof(HasInlineNoticeArea));
            OnPropertyChanged(nameof(ConflictedChanges));

            if (_detailContext is Conflict conflictVm)
                conflictVm.SyncState(_inProgressInspection, ConflictedChanges);

            _repo.NotifyInProgressOwnershipChanged();
        }

        private bool _hasUnsolvedConflicts = false;
        private InProgressContext _inProgressContext = null;
        private Models.RepositoryInProgressInspection _inProgressInspection = null;
        private int _inProgressInspectionVersion;
        private int _inProgressOwnershipRefreshCount;
        private string _autoLoadedCommitMessage = null;
    }
}
