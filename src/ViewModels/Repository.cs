using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Avalonia.Collections;
using Avalonia.Threading;

using CommunityToolkit.Mvvm.ComponentModel;

namespace SourceGit.ViewModels
{
    public partial class Repository : ObservableObject, Models.IRepository
    {
        public bool IsBare
        {
            get;
        }

        public string FullPath
        {
            get;
        }

        public string GitDir
        {
            get;
        }

        public string GitCommonDir => _gitCommonDir;

        public Models.RepositorySettings Settings
        {
            get => _settings;
        }

        public Models.RepositoryUIStates UIStates
        {
            get => _uiStates;
        }

        public bool FocusOnCurrentBranch
        {
            get => _uiStates.FocusOnCurrentBranch;
            set
            {
                if (value != _uiStates.FocusOnCurrentBranch)
                {
                    _uiStates.FocusOnCurrentBranch = value;
                    OnPropertyChanged();

                    if (value && _historyFilterMode != Models.FilterMode.None)
                        ClearHistoryFilters();
                    else
                        RefreshCommits();
                }
            }
        }

        public Models.FilterMode HistoryFilterMode
        {
            get => _historyFilterMode;
            private set => SetProperty(ref _historyFilterMode, value);
        }

        public bool IsHistoryFiltersCollapsed
        {
            get => _uiStates.IsHistoryFiltersCollapsed;
            set
            {
                if (value != _uiStates.IsHistoryFiltersCollapsed)
                {
                    _uiStates.IsHistoryFiltersCollapsed = value;
                    OnPropertyChanged();
                }
            }
        }

        public bool HasAllowedSignersFile
        {
            get => _hasAllowedSignersFile;
        }

        public int SelectedViewIndex
        {
            get => _selectedViewIndex;
            set
            {
                if (SetProperty(ref _selectedViewIndex, value))
                {
                    OnPropertyChanged(nameof(IsDashboardVisible));
                    OnPropertyChanged(nameof(IsWorkingCopyVisible));
                    OnPropertyChanged(nameof(IsStashesVisible));
                    OnPropertyChanged(nameof(IsTaskComparisonVisible));
                    OnPropertyChanged(nameof(CanViewConflictsFromBanner));
                    if (value == 3) EnsureTaskComparison();
                }
            }
        }

        public Histories Histories
        {
            get => _histories;
        }

        public WorkingCopy WorkingCopy
        {
            get => _workingCopy;
        }

        public StashesPage StashesPage
        {
            get => _stashesPage;
        }

        public bool IsDashboardVisible
        {
            get => SelectedViewIndex == 0;
        }

        public bool IsWorkingCopyVisible
        {
            get => SelectedViewIndex == 1;
        }

        public bool IsStashesVisible
        {
            get => SelectedViewIndex == 2;
        }

        public string Filter
        {
            get => _filter;
            set
            {
                if (SetProperty(ref _filter, value))
                {
                    var builder = BuildBranchTree(_branches, _remotes);
                    LocalBranchTrees = builder.Locals;
                    RemoteBranchTrees = builder.Remotes;
                    VisibleTags = BuildVisibleTags();
                    VisibleSubmodules = BuildVisibleSubmodules();
                }
            }
        }

        public List<Models.Remote> Remotes
        {
            get => _remotes;
            private set
            {
                if (SetProperty(ref _remotes, value))
                {
                    if (_histories != null)
                        _histories.HasSingleRemote = value != null && value.Count == 1;
                }
            }
        }

        public List<Models.Branch> Branches
        {
            get => _branches;
            private set => SetProperty(ref _branches, value);
        }

        public Models.Branch CurrentBranch
        {
            get => _currentBranch;
            private set
            {
                var oldHead = _currentBranch?.Head;
                if (SetProperty(ref _currentBranch, value))
                {
                    if (_histories != null)
                        _histories.CurrentBranch = value;

                    if (value != null && !value.Head.Equals(oldHead, StringComparison.Ordinal) && _workingCopy is { UseAmend: true })
                        _workingCopy.UseAmend = false;
                }
            }
        }

        public List<BranchTreeNode> LocalBranchTrees
        {
            get => _localBranchTrees;
            private set => SetProperty(ref _localBranchTrees, value);
        }

        public List<BranchTreeNode> RemoteBranchTrees
        {
            get => _remoteBranchTrees;
            private set => SetProperty(ref _remoteBranchTrees, value);
        }

        public List<Worktree> Worktrees
        {
            get => _worktrees;
            private set => SetProperty(ref _worktrees, value);
        }

        public List<Models.Tag> Tags
        {
            get => _tags;
            private set => SetProperty(ref _tags, value);
        }

        public bool ShowTagsAsTree
        {
            get => _uiStates.ShowTagsAsTree;
            set
            {
                if (value != _uiStates.ShowTagsAsTree)
                {
                    _uiStates.ShowTagsAsTree = value;
                    VisibleTags = BuildVisibleTags();
                    OnPropertyChanged();
                }
            }
        }

        public object VisibleTags
        {
            get => _visibleTags;
            private set => SetProperty(ref _visibleTags, value);
        }

        public List<Models.Submodule> Submodules
        {
            get => _submodules;
            private set => SetProperty(ref _submodules, value);
        }

        public bool ShowSubmodulesAsTree
        {
            get => _uiStates.ShowSubmodulesAsTree;
            set
            {
                if (value != _uiStates.ShowSubmodulesAsTree)
                {
                    _uiStates.ShowSubmodulesAsTree = value;
                    VisibleSubmodules = BuildVisibleSubmodules();
                    OnPropertyChanged();
                }
            }
        }

        public object VisibleSubmodules
        {
            get => _visibleSubmodules;
            private set => SetProperty(ref _visibleSubmodules, value);
        }

        public int LocalChangesCount
        {
            get => _localChangesCount;
            private set => SetProperty(ref _localChangesCount, value);
        }

        public int StashesCount
        {
            get => _stashesCount;
            private set => SetProperty(ref _stashesCount, value);
        }

        public int LocalBranchesCount
        {
            get => _localBranchesCount;
            private set => SetProperty(ref _localBranchesCount, value);
        }

        public bool IncludeUntracked
        {
            get => _uiStates.IncludeUntrackedInLocalChanges;
            set
            {
                if (value != _uiStates.IncludeUntrackedInLocalChanges)
                {
                    _uiStates.IncludeUntrackedInLocalChanges = value;
                    OnPropertyChanged();
                    RefreshWorkingCopyChanges();
                }
            }
        }

        public bool IsLocalBranchGroupExpanded
        {
            get => _uiStates.IsLocalBranchesExpandedInSideBar;
            set
            {
                if (value != _uiStates.IsLocalBranchesExpandedInSideBar)
                {
                    _uiStates.IsLocalBranchesExpandedInSideBar = value;
                    OnPropertyChanged();
                }
            }
        }

        public bool IsRemoteGroupExpanded
        {
            get => _uiStates.IsRemotesExpandedInSideBar;
            set
            {
                if (value != _uiStates.IsRemotesExpandedInSideBar)
                {
                    _uiStates.IsRemotesExpandedInSideBar = value;
                    OnPropertyChanged();
                }
            }
        }

        public bool IsTagGroupExpanded
        {
            get => _uiStates.IsTagsExpandedInSideBar;
            set
            {
                if (value != _uiStates.IsTagsExpandedInSideBar)
                {
                    _uiStates.IsTagsExpandedInSideBar = value;
                    OnPropertyChanged();
                }
            }
        }

        public bool IsSubmoduleGroupExpanded
        {
            get => _uiStates.IsSubmodulesExpandedInSideBar;
            set
            {
                if (value != _uiStates.IsSubmodulesExpandedInSideBar)
                {
                    _uiStates.IsSubmodulesExpandedInSideBar = value;
                    OnPropertyChanged();
                }
            }
        }

        public bool IsWorktreeGroupExpanded
        {
            get => _uiStates.IsWorktreeExpandedInSideBar;
            set
            {
                if (value != _uiStates.IsWorktreeExpandedInSideBar)
                {
                    _uiStates.IsWorktreeExpandedInSideBar = value;
                    OnPropertyChanged();
                }
            }
        }

        public bool IsSortingLocalBranchByName
        {
            get => _uiStates.LocalBranchSortMode == Models.BranchSortMode.Name;
            set
            {
                _uiStates.LocalBranchSortMode = value ? Models.BranchSortMode.Name : Models.BranchSortMode.CommitterDate;
                OnPropertyChanged();

                var builder = BuildBranchTree(_branches, _remotes);
                LocalBranchTrees = builder.Locals;
                RemoteBranchTrees = builder.Remotes;
            }
        }

        public bool IsSortingRemoteBranchByName
        {
            get => _uiStates.RemoteBranchSortMode == Models.BranchSortMode.Name;
            set
            {
                _uiStates.RemoteBranchSortMode = value ? Models.BranchSortMode.Name : Models.BranchSortMode.CommitterDate;
                OnPropertyChanged();

                var builder = BuildBranchTree(_branches, _remotes);
                LocalBranchTrees = builder.Locals;
                RemoteBranchTrees = builder.Remotes;
            }
        }

        public bool IsSortingTagsByName
        {
            get => _uiStates.TagSortMode == Models.TagSortMode.Name;
            set
            {
                _uiStates.TagSortMode = value ? Models.TagSortMode.Name : Models.TagSortMode.CreatorDate;
                OnPropertyChanged();
                VisibleTags = BuildVisibleTags();
            }
        }

        public InProgressContext InProgressContext
        {
            get => _workingCopy?.InProgressContext;
        }

        public bool HasInProgressBanner => _workingCopy is { HasInProgressBanner: true };

        public bool HasInProgressOriginLabel => !string.IsNullOrWhiteSpace(InProgressOriginLabel);

        public string InProgressOriginLabel => _workingCopy?.InProgressOriginLabel ?? string.Empty;

        public string InProgressConflictBadgeText => _workingCopy?.InProgressConflictBadgeText ?? string.Empty;

        public bool HasInProgressConflictBadge => !string.IsNullOrWhiteSpace(InProgressConflictBadgeText);

        public string InProgressBannerTooltip => _workingCopy?.InProgressBannerTooltip ?? string.Empty;

        public bool HasInProgressHeadlineFallback => _workingCopy is { InProgressContext: null, HasInProgressBanner: true };

        public string InProgressHeadlineFallback =>
            _workingCopy?.InProgressInspection?.Headline ?? App.Text("WorkingCopy.Conflicts");

        public bool CanAbortInProgressFromBanner => _workingCopy is { CanAbortInProgressFromBanner: true };

        public bool CanViewConflictsFromBanner =>
            _workingCopy != null && (!IsWorkingCopyVisible || _workingCopy.HasUnsolvedConflicts);

        public bool IsSkippingOrAbortingMerge
        {
            get => _workingCopy is { InProgressContext: { }, IsCommitting: true };
        }

        public bool IsAutoFetching
        {
            get => _isAutoFetching;
            private set => SetProperty(ref _isAutoFetching, value);
        }

        public AvaloniaList<Models.IssueTracker> IssueTrackers
        {
            get;
        } = [];

        public AvaloniaList<CommandLog> Logs
        {
            get;
        } = [];

        public Repository(bool isBare, string path, string gitDir)
        {
            IsBare = isBare;
            FullPath = path.Replace('\\', '/').TrimEnd('/');
            GitDir = gitDir.Replace('\\', '/').TrimEnd('/');

            var commonDirFile = Path.Combine(GitDir, "commondir");
            var isWorktree = GitDir.IndexOf("/worktrees/", StringComparison.Ordinal) > 0 &&
                          File.Exists(commonDirFile);

            if (isWorktree)
            {
                var commonDir = File.ReadAllText(commonDirFile).Trim();
                if (Path.IsPathRooted(commonDir))
                    commonDir = new DirectoryInfo(commonDir).FullName;
                else
                    commonDir = new DirectoryInfo(Path.Combine(GitDir, commonDir)).FullName;

                _gitCommonDir = commonDir.Replace('\\', '/').TrimEnd('/');
            }
            else
            {
                _gitCommonDir = GitDir;
            }

            _settings = Models.RepositorySettings.Get(_gitCommonDir);
            _uiStates = Models.RepositoryUIStates.Load(GitDir);
        }

        public void Open()
        {
            try
            {
                _watcher = new Models.Watcher(this, FullPath, _gitCommonDir);
            }
            catch (Exception ex)
            {
                SendNotification($"Failed to start watcher for repository: '{FullPath}'. You may need to press 'F5' to refresh repository manually!\n\nReason: {ex.Message}", true);
            }

            _historyFilterMode = _uiStates.GetHistoryFilterMode();
            _histories = new Histories(this);
            _workingCopy = new WorkingCopy(this) { CommitMessage = _uiStates.LastCommitMessage };
            _stashesPage = new StashesPage(this);
            _selectedViewIndex = Preferences.Instance.ShowLocalChangesByDefault ? 1 : 0;
            _autoFetchAnchorTime = DateTime.Now;
            _autoFetchTimer = new Timer(AutoFetchByTimer, null, 5000, 5000);
            RefreshAll();
            _ = RefreshRecoveryStatusAsync();
        }

        public void Close()
        {
            _isClosed = true;
            _taskComparison?.Dispose();
            _isTaskOverviewClosed = true;
            Interlocked.Increment(ref _worktreeListGeneration);
            Interlocked.Increment(ref _branchesGeneration);
            Interlocked.Increment(ref _tagsGeneration);
            Interlocked.Increment(ref _commitsGeneration);
            Interlocked.Increment(ref _submodulesGeneration);
            Interlocked.Increment(ref _workingCopyGeneration);
            Interlocked.Increment(ref _stashesGeneration);
            Interlocked.Increment(ref _configStateGeneration);
            if (_workingCopy != null)
            {
                var commitMessage = _workingCopy.CommitMessage;
                if (!string.IsNullOrEmpty(commitMessage) && _workingCopy.InProgressContext != null)
                {
                    File.WriteAllText(Path.Combine(GitDir, "MERGE_MSG"), commitMessage);
                    commitMessage = string.Empty; // Clears this from ui-state, since it is already saved in MERGE_MSG file.
                }

                if (_uiStates != null)
                    _uiStates.LastCommitMessage = commitMessage;
            }

            _uiStates?.Save();

            if (_cancellationRefreshBranches is { IsCancellationRequested: false })
                _cancellationRefreshBranches.Cancel();
            if (_cancellationRefreshTags is { IsCancellationRequested: false })
                _cancellationRefreshTags.Cancel();
            if (_cancellationRefreshWorkingCopyChanges is { IsCancellationRequested: false })
                _cancellationRefreshWorkingCopyChanges.Cancel();
            if (_cancellationRefreshCommits is { IsCancellationRequested: false })
                _cancellationRefreshCommits.Cancel();
            if (_cancellationRefreshStashes is { IsCancellationRequested: false })
                _cancellationRefreshStashes.Cancel();
            if (_cancellationRefreshSubmodules is { IsCancellationRequested: false })
                _cancellationRefreshSubmodules.Cancel();
            if (_cancellationRefreshConfiguration is { IsCancellationRequested: false })
                _cancellationRefreshConfiguration.Cancel();

            _watcher?.Dispose();
            _autoFetchTimer?.Dispose();
        }

        public void SendNotification(string message, bool isError = false)
        {
            Models.Notification.Send(FullPath, message, isError);
        }

        public bool CanCreatePopup()
        {
            var page = GetOwnerPage();
            if (page == null)
                return false;

            return !_isAutoFetching && page.CanCreatePopup();
        }

        public void ShowPopup(Popup popup)
        {
            var page = GetOwnerPage();
            if (page != null)
                page.Popup = popup;
        }

        public void DismissPopup()
        {
            var page = GetOwnerPage();
            if (page != null)
                page.CancelPopup();
        }

        public void ReplacePopup(Popup popup)
        {
            var page = GetOwnerPage();
            if (page == null)
                return;

            page.CancelPopup();
            page.Popup = popup;
        }

        public async Task ShowAndStartPopupAsync(Popup popup)
        {
            var page = GetOwnerPage();
            page.Popup = popup;

            if (popup.CanStartDirectly())
                await page.ProcessPopupAsync();
        }

        public bool IsLFSEnabled()
        {
            var path = Path.Combine(GitDir, "hooks", "pre-push");
            if (!File.Exists(path))
                return false;

            try
            {
                var content = File.ReadAllText(path);
                return content.Contains("git lfs pre-push");
            }
            catch
            {
                return false;
            }
        }

        public async Task InstallLFSAsync()
        {
            var log = CreateLog("Install LFS");
            var succ = await new Commands.LFS(FullPath).Use(log).InstallAsync();
            if (succ)
                SendNotification("LFS enabled successfully!");

            log.Complete();
        }

        public async Task<bool> TrackLFSFileAsync(string pattern, bool isFilenameMode)
        {
            var log = CreateLog("Track LFS");
            var succ = await new Commands.LFS(FullPath)
                .Use(log)
                .TrackAsync(pattern, isFilenameMode);

            if (succ)
                SendNotification($"Tracking successfully! Pattern: {pattern}");

            log.Complete();
            return succ;
        }

        public CommandLog CreateLog(string name)
        {
            var log = new CommandLog(name);
            Logs.Insert(0, log);
            return log;
        }

        public string GetRecommandedWorktreeDir()
        {
            var commonDirFile = Path.Combine(GitDir, "commondir");
            var isWorktree = GitDir.IndexOf("/worktrees/", StringComparison.Ordinal) > 0 && File.Exists(commonDirFile);
            var parentFolder = Path.GetFullPath(Path.Combine(FullPath, ".."));
            if (isWorktree)
                return parentFolder;

            var dirName = $"{Path.GetFileName(FullPath)}-worktrees";
            return Path.Combine(parentFolder, dirName);
        }

        public event Action ConfigurationStateChanged;

        public void RefreshAll()
        {
            if (_isClosed)
                return;

            RefreshCommits();
            RefreshBranches();
            RefreshTags();
            RefreshSubmodules();
            RefreshWorktrees();
            RefreshWorkingCopyChanges();
            RefreshStashes();
            RefreshConfigurationState();
        }

        public void RefreshConfigurationState()
        {
            if (_isClosed)
                return;

            if (_cancellationRefreshConfiguration is { IsCancellationRequested: false })
                _cancellationRefreshConfiguration.Cancel();

            _cancellationRefreshConfiguration = new CancellationTokenSource();
            var token = _cancellationRefreshConfiguration.Token;
            var generation = Interlocked.Increment(ref _configStateGeneration);

            Task.Run(async () =>
            {
                var issuetrackers = new List<Models.IssueTracker>();
                await new Commands.IssueTracker(FullPath, true).ReadAllAsync(issuetrackers, true).ConfigureAwait(false);
                if (_isClosed || token.IsCancellationRequested || generation != Interlocked.Read(ref _configStateGeneration))
                    return;

                await new Commands.IssueTracker(FullPath, false).ReadAllAsync(issuetrackers, false).ConfigureAwait(false);
                if (_isClosed || token.IsCancellationRequested || generation != Interlocked.Read(ref _configStateGeneration))
                    return;

                var config = await new Commands.Config(FullPath).ReadAllAsync().ConfigureAwait(false);
                if (_isClosed || token.IsCancellationRequested || generation != Interlocked.Read(ref _configStateGeneration))
                    return;

                Dispatcher.UIThread.Post(() =>
                {
                    if (_isClosed || token.IsCancellationRequested || generation != Interlocked.Read(ref _configStateGeneration))
                        return;

                    IssueTrackers.Clear();
                    IssueTrackers.AddRange(issuetrackers);
                    _hasAllowedSignersFile = config.TryGetValue("gpg.ssh.allowedsignersfile", out var allowedSignersFile) && !string.IsNullOrEmpty(allowedSignersFile);
                    ConfigurationStateChanged?.Invoke();
                });
            }, token);
        }

        public async Task FetchAsync(bool autoStart)
        {
            if (!CanCreatePopup())
                return;

            if (_remotes.Count == 0)
            {
                SendNotification("No remotes added to this repository!!!", true);
                return;
            }

            if (autoStart)
                await ShowAndStartPopupAsync(new Fetch(this));
            else
                ShowPopup(new Fetch(this));
        }

        public async Task PullAsync(bool autoStart)
        {
            if (IsBare || !CanCreatePopup())
                return;

            if (_remotes.Count == 0)
            {
                SendNotification("No remotes added to this repository!!!", true);
                return;
            }

            if (_currentBranch == null)
            {
                SendNotification("Can NOT find current branch!!!", true);
                return;
            }

            var pull = new Pull(this, null);
            if (autoStart && pull.SelectedBranch != null)
                await ShowAndStartPopupAsync(pull);
            else
                ShowPopup(pull);
        }

        public async Task PushAsync(bool autoStart)
        {
            if (!CanCreatePopup())
                return;

            if (_remotes.Count == 0)
            {
                SendNotification("No remotes added to this repository!!!", true);
                return;
            }

            if (_currentBranch == null)
            {
                SendNotification("Can NOT find current branch!!!", true);
                return;
            }

            if (autoStart)
                await ShowAndStartPopupAsync(new Push(this, null));
            else
                ShowPopup(new Push(this, null));
        }

        public async Task CleanupAsync()
        {
            if (CanCreatePopup())
                await ShowAndStartPopupAsync(new Cleanup(this));
        }

        public void ClearFilter()
        {
            Filter = string.Empty;
        }

        public IDisposable LockWatcher()
        {
            return _watcher?.Lock();
        }

        public void RefreshAfterCreateBranch(Models.Branch created, bool checkout)
        {
            _watcher?.MarkBranchUpdated();

            _branches.RemoveAll(b => b.IsLocal && b.Name.Equals(created.Name, StringComparison.Ordinal));
            _branches.Add(created);

            if (checkout)
            {
                if (_currentBranch.IsDetachedHead)
                {
                    _branches.Remove(_currentBranch);
                }
                else
                {
                    _currentBranch.IsCurrent = false;
                    _currentBranch.WorktreePath = null;
                }

                created.IsCurrent = true;
                created.WorktreePath = FullPath;

                var folderEndIdx = created.FullName.LastIndexOf('/');
                if (folderEndIdx > 10)
                    _uiStates.ExpandedBranchNodesInSideBar.Add(created.FullName.Substring(0, folderEndIdx));

                if (_historyFilterMode == Models.FilterMode.Included &&
                    !_uiStates.IsCoveredByFolderFilter(created.FullName, Models.FilterType.LocalBranchFolder))
                    SetBranchFilterMode(created, Models.FilterMode.Included, false, false);

                CurrentBranch = created;
            }

            var locals = new List<Models.Branch>();
            var count = 0;
            foreach (var b in _branches)
            {
                if (b.IsLocal)
                {
                    locals.Add(b);
                    if (!b.IsDetachedHead)
                        count++;
                }
            }

            var builder = BuildBranchTree(locals, [], false);
            LocalBranchTrees = builder.Locals;
            LocalBranchesCount = count;

            RefreshCommits();
            RefreshWorkingCopyChanges();
            RefreshWorktrees();
        }

        public void RefreshAfterCheckoutBranch(Models.Branch checkouted)
        {
            _watcher?.MarkBranchUpdated();

            if (_currentBranch != null)
            {
                if (_currentBranch.IsDetachedHead)
                {
                    _branches.Remove(_currentBranch);
                }
                else
                {
                    _currentBranch.IsCurrent = false;
                    _currentBranch.WorktreePath = null;
                }
            }

            checkouted.IsCurrent = true;
            checkouted.WorktreePath = FullPath;
            if (_historyFilterMode == Models.FilterMode.Included &&
                !_uiStates.IsCoveredByFolderFilter(checkouted.FullName, Models.FilterType.LocalBranchFolder))
                SetBranchFilterMode(checkouted, Models.FilterMode.Included, false, false);

            List<Models.Branch> locals = [];
            foreach (var b in _branches)
            {
                if (b.IsLocal)
                    locals.Add(b);
            }

            var builder = BuildBranchTree(locals, [], false);
            LocalBranchTrees = builder.Locals;
            CurrentBranch = checkouted;
            GetOwnerPage()?.ChangeDirtyState(Models.DirtyState.HasPendingPullOrPush, !checkouted.IsTrackStatusVisible);

            RefreshCommits();
            RefreshWorkingCopyChanges();
            RefreshWorktrees();
        }

        public void RefreshAfterRenameBranch(Models.Branch b, string newName)
        {
            _watcher?.MarkBranchUpdated();

            var newFullName = $"refs/heads/{newName}";
            _uiStates.RenameBranchFilter(b.FullName, newFullName);

            var renamed = new Models.Branch
            {
                Name = newName,
                FullName = newFullName,
                CommitterDate = b.CommitterDate,
                Head = b.Head,
                IsLocal = b.IsLocal,
                IsCurrent = b.IsCurrent,
                IsDetachedHead = b.IsDetachedHead,
                Upstream = b.Upstream,
                Ahead = b.Ahead,
                Behind = b.Behind,
                Remote = b.Remote,
                IsUpstreamGone = b.IsUpstreamGone,
                WorktreePath = b.WorktreePath,
            };

            _branches.Remove(b);
            _branches.Add(renamed);

            if (b.IsCurrent)
                CurrentBranch = renamed;

            List<Models.Branch> locals = [];
            foreach (var branch in _branches)
            {
                if (branch.IsLocal)
                    locals.Add(branch);
            }

            var builder = BuildBranchTree(locals, [], false);
            LocalBranchTrees = builder.Locals;

            RefreshCommits();
            RefreshWorktrees();
        }

        public void MarkBranchesDirtyManually()
        {
            _watcher?.MarkBranchUpdated();
            RefreshBranches();
            RefreshCommits();
            RefreshWorkingCopyChanges();
            RefreshWorktrees();
        }

        public void MarkTagsDirtyManually()
        {
            _watcher?.MarkTagUpdated();
            RefreshTags();
            RefreshCommits();
        }

        public void MarkWorkingCopyDirtyManually()
        {
            _watcher?.MarkWorkingCopyUpdated();
            RefreshWorkingCopyChanges();
        }

        public void MarkStashesDirtyManually()
        {
            _watcher?.MarkStashUpdated();
            RefreshStashes();
        }

        public void MarkSubmodulesDirtyManually()
        {
            _watcher?.MarkSubmodulesUpdated();
            RefreshSubmodules();
        }

        public bool LastFetchFailed
        {
            get => _lastFetchFailed;
            private set => SetProperty(ref _lastFetchFailed, value);
        }

        public string LastFetchError
        {
            get => _lastFetchError;
            private set => SetProperty(ref _lastFetchError, value);
        }

        public string LastFetchDisplayTime =>
            _currentTaskStatus?.LastFetchAt is { } taskFetchTime
                ? taskFetchTime.ToString("yyyy-MM-dd HH:mm:ss")
                : (_lastFetchTime != DateTime.MinValue ? _lastFetchTime.ToString("yyyy-MM-dd HH:mm:ss") : string.Empty);

        public void MarkFetched()
        {
            var now = DateTime.Now;
            _lastFetchTime = now;
            _autoFetchAnchorTime = now;
            LastFetchFailed = false;
            LastFetchError = string.Empty;
            OnPropertyChanged(nameof(LastFetchDisplayTime));
            OnPropertyChanged(nameof(StatusSummary));
            OnPropertyChanged(nameof(StatusSummaryDetail));
        }

        public void MarkFetchResult(Models.FetchExecutionResult result)
        {
            if (result == null)
                return;

            _autoFetchAnchorTime = DateTime.Now;
            if (result.Succeeded)
            {
                _lastFetchTime = result.FetchedAtUtc?.LocalDateTime ?? DateTime.Now;
                LastFetchFailed = false;
                LastFetchError = string.Empty;
            }
            else if (result.Outcome != Models.FetchOutcomeKind.Canceled)
            {
                LastFetchFailed = true;
                LastFetchError = result.Summary;
            }

            OnPropertyChanged(nameof(LastFetchDisplayTime));
            OnPropertyChanged(nameof(StatusSummary));
            OnPropertyChanged(nameof(StatusSummaryDetail));
        }

        public Models.OperationReconciliationReport PendingReconciliation
        {
            get => _pendingReconciliation;
            private set
            {
                if (SetProperty(ref _pendingReconciliation, value))
                {
                    var key = BuildRecoveryDismissKey(value);
                    if (string.IsNullOrEmpty(key) || !string.Equals(key, _dismissedRecoveryKey, StringComparison.Ordinal))
                        _dismissedRecoveryKey = string.Empty;

                    OnPropertyChanged(nameof(HasPendingRecoveryOperations));
                    OnPropertyChanged(nameof(ShowPendingRecoveryBanner));
                    OnPropertyChanged(nameof(PendingRecoverySummary));
                }
            }
        }

        public bool HasPendingRecoveryOperations => _pendingReconciliation is { HasPendingAttention: true };

        public bool ShowPendingRecoveryBanner =>
            HasPendingRecoveryOperations &&
            !string.Equals(BuildRecoveryDismissKey(_pendingReconciliation), _dismissedRecoveryKey, StringComparison.Ordinal);

        public string PendingRecoverySummary => _pendingReconciliation?.Summary ?? string.Empty;

        public void DismissPendingRecoveryBanner()
        {
            _dismissedRecoveryKey = BuildRecoveryDismissKey(_pendingReconciliation);
            OnPropertyChanged(nameof(ShowPendingRecoveryBanner));
        }

        private static string BuildRecoveryDismissKey(Models.OperationReconciliationReport report)
        {
            if (report is not { HasPendingAttention: true })
                return string.Empty;

            return string.Join("|", report.UnfinishedRecords.Select(r => $"{r.OperationId}:{r.Stage}"));
        }

        public async Task RefreshRecoveryStatusAsync(Models.OperationJournalStore journalStore = null)
        {
            try
            {
                var report = await Commands.OperationJournalService
                    .ReconcileOnOpenAsync(FullPath, journalStore)
                    .ConfigureAwait(false);
                Dispatcher.UIThread.Post(() =>
                {
                    if (!_isTaskOverviewClosed)
                        PendingReconciliation = report;
                });
            }
            catch
            {
                // Keep opening resilient if journal inspection fails.
            }
        }

        public void RefreshOnWindowActivated()
        {
            if (_isClosed)
                return;

            var now = DateTime.UtcNow;
            if ((now - _lastWindowActivationRefreshUtc).TotalSeconds < 1.5)
                return;

            _lastWindowActivationRefreshUtc = now;
            RefreshBranches();
            RefreshCommits();
            RefreshWorkingCopyChanges();
            RefreshWorktrees();
            RefreshStashes();
            RefreshConfigurationState();
            _ = RefreshRecoveryStatusAsync();
        }

        public void NavigateToCommit(string sha, bool isDelayMode = false)
        {
            if (isDelayMode)
            {
                _navigateToCommitDelayed = sha;
            }
            else
            {
                SelectedViewIndex = 0;
                _histories?.NavigateTo(sha);
            }
        }

        public void SetCommitMessage(string message)
        {
            if (_workingCopy is not null)
                _workingCopy.CommitMessage = message;
        }

        public void ClearCommitMessage()
        {
            if (_workingCopy is not null)
                _workingCopy.CommitMessage = string.Empty;
        }

        public Models.Commit GetSelectedCommitInHistory()
        {
            return (_histories?.DetailContext as CommitDetail)?.Commit;
        }

        public void ClearHistoryFilters()
        {
            _uiStates.HistoryFilters.Clear();
            HistoryFilterMode = Models.FilterMode.None;

            ResetBranchTreeFilterMode(LocalBranchTrees);
            ResetBranchTreeFilterMode(RemoteBranchTrees);
            ResetTagFilterMode();
            RefreshCommits();
        }

        public void RemoveHistoryFilter(Models.HistoryFilter filter)
        {
            if (_uiStates.HistoryFilters.Remove(filter))
            {
                HistoryFilterMode = _uiStates.GetHistoryFilterMode();
                RefreshHistoryFilters(true);
            }
        }

        public void UpdateBranchNodeIsExpanded(BranchTreeNode node)
        {
            if (_uiStates == null || !string.IsNullOrWhiteSpace(_filter))
                return;

            if (node.IsExpanded)
            {
                if (!_uiStates.ExpandedBranchNodesInSideBar.Contains(node.Path))
                    _uiStates.ExpandedBranchNodesInSideBar.Add(node.Path);
            }
            else
            {
                _uiStates.ExpandedBranchNodesInSideBar.Remove(node.Path);
            }
        }

        public void SetTagFilterMode(Models.Tag tag, Models.FilterMode mode)
        {
            // Auto turn off `FocusOnCurrentBranch` when user set a tag filter manually.
            if (_uiStates.FocusOnCurrentBranch)
            {
                _uiStates.FocusOnCurrentBranch = false;
                OnPropertyChanged(nameof(FocusOnCurrentBranch));
            }

            var changed = _uiStates.UpdateHistoryFilters(tag.Name, Models.FilterType.Tag, mode);
            if (changed)
                RefreshHistoryFilters(true);
        }

        public void SetBranchFilterMode(Models.Branch branch, Models.FilterMode mode, bool clearExists, bool refresh)
        {
            var node = FindBranchNode(branch.IsLocal ? _localBranchTrees : _remoteBranchTrees, branch.FullName);
            if (node != null)
                SetBranchFilterMode(node, mode, clearExists, refresh);
        }

        public void SetBranchFilterMode(BranchTreeNode node, Models.FilterMode mode, bool clearExists, bool refresh)
        {
            // Auto turn off `FocusOnCurrentBranch` when user set a branch filter manually.
            if (_uiStates.FocusOnCurrentBranch)
            {
                _uiStates.FocusOnCurrentBranch = false;
                OnPropertyChanged(nameof(FocusOnCurrentBranch));
            }

            var isLocal = node.Path.StartsWith("refs/heads/", StringComparison.Ordinal);
            var tree = isLocal ? _localBranchTrees : _remoteBranchTrees;

            if (clearExists)
            {
                _uiStates.HistoryFilters.Clear();
                HistoryFilterMode = Models.FilterMode.None;
            }

            if (node.Backend is Models.Branch branch)
            {
                var type = isLocal ? Models.FilterType.LocalBranch : Models.FilterType.RemoteBranch;
                var changed = _uiStates.UpdateHistoryFilters(node.Path, type, mode);
                if (!changed)
                    return;

                if (isLocal && !string.IsNullOrEmpty(branch.Upstream) && !branch.IsUpstreamGone)
                    _uiStates.UpdateHistoryFilters(branch.Upstream, Models.FilterType.RemoteBranch, mode);
            }
            else
            {
                var type = isLocal ? Models.FilterType.LocalBranchFolder : Models.FilterType.RemoteBranchFolder;
                var changed = _uiStates.UpdateHistoryFilters(node.Path, type, mode);
                if (!changed)
                    return;

                _uiStates.RemoveBranchFiltersByPrefix(node.Path);
            }

            var parentType = isLocal ? Models.FilterType.LocalBranchFolder : Models.FilterType.RemoteBranchFolder;
            var cur = node;
            do
            {
                var lastSepIdx = cur.Path.LastIndexOf('/');
                if (lastSepIdx <= 0)
                    break;

                var parentPath = cur.Path.Substring(0, lastSepIdx);
                var parent = FindBranchNode(tree, parentPath);
                if (parent == null)
                    break;

                _uiStates.UpdateHistoryFilters(parent.Path, parentType, Models.FilterMode.None);
                cur = parent;
            } while (true);

            RefreshHistoryFilters(refresh);
        }

        public async Task StashAllAsync(bool autoStart)
        {
            if (!CanCreatePopup())
                return;

            var popup = new StashChanges(this, null);
            if (autoStart)
                await ShowAndStartPopupAsync(popup);
            else
                ShowPopup(popup);
        }

        public void NotifyIsSkippingOrAbortingMergeChanged()
        {
            OnPropertyChanged(nameof(IsSkippingOrAbortingMerge));
        }

        public void NotifyInProgressOwnershipChanged()
        {
            OnPropertyChanged(nameof(HasInProgressBanner));
            OnPropertyChanged(nameof(HasInProgressOriginLabel));
            OnPropertyChanged(nameof(InProgressOriginLabel));
            OnPropertyChanged(nameof(InProgressConflictBadgeText));
            OnPropertyChanged(nameof(HasInProgressConflictBadge));
            OnPropertyChanged(nameof(InProgressBannerTooltip));
            OnPropertyChanged(nameof(HasInProgressHeadlineFallback));
            OnPropertyChanged(nameof(InProgressHeadlineFallback));
            OnPropertyChanged(nameof(CanAbortInProgressFromBanner));
            OnPropertyChanged(nameof(CanViewConflictsFromBanner));
        }

        public void FocusFirstConflict()
        {
            SelectedViewIndex = 1;
            _workingCopy?.FocusFirstConflict();
        }

        public async Task RefreshInProgressStateAsync()
        {
            if (_workingCopy != null)
                await _workingCopy.RefreshInProgressStateAsync();
        }

        public async Task SkipMergeAsync()
        {
            if (_workingCopy is not { IsCommitting: false } wc)
                return;

            await wc.SkipMergeAsync();
        }

        public async Task AbortMergeAsync()
        {
            if (_workingCopy is not { IsCommitting: false } wc)
                return;

            await wc.AbortMergeAsync();
        }

        public bool MayHaveSubmodules()
        {
            var modulesFile = Path.Combine(FullPath, ".gitmodules");
            var info = new FileInfo(modulesFile);
            return info.Exists && info.Length > 20;
        }

        public void RefreshBranches()
        {
            if (_isClosed)
                return;

            if (_cancellationRefreshBranches is { IsCancellationRequested: false })
                _cancellationRefreshBranches.Cancel();

            _cancellationRefreshBranches = new CancellationTokenSource();
            var token = _cancellationRefreshBranches.Token;
            var generation = Interlocked.Increment(ref _branchesGeneration);

            Task.Run(async () =>
            {
                var branches = await new Commands.QueryBranches(FullPath).GetResultAsync().ConfigureAwait(false);
                if (_isClosed || token.IsCancellationRequested || generation != Interlocked.Read(ref _branchesGeneration))
                    return;

                var remotes = await new Commands.QueryRemotes(FullPath).GetResultAsync().ConfigureAwait(false);
                if (_isClosed || token.IsCancellationRequested || generation != Interlocked.Read(ref _branchesGeneration))
                    return;

                var builder = BuildBranchTree(branches, remotes);

                Dispatcher.UIThread.Invoke(() =>
                {
                    if (_isClosed || token.IsCancellationRequested || generation != Interlocked.Read(ref _branchesGeneration))
                        return;

                    Remotes = remotes;
                    Branches = branches;
                    CurrentBranch = branches.Find(x => x.IsCurrent);
                    LocalBranchTrees = builder.Locals;
                    RemoteBranchTrees = builder.Remotes;

                    ValidateHistoryFilters(true);

                    var localBranchesCount = 0;
                    foreach (var b in branches)
                    {
                        if (b.IsLocal && !b.IsDetachedHead)
                            localBranchesCount++;
                    }
                    LocalBranchesCount = localBranchesCount;

                    if (_workingCopy != null)
                        _workingCopy.HasRemotes = remotes.Count > 0;

                    var hasPendingPullOrPush = CurrentBranch?.IsTrackStatusVisible ?? false;
                    GetOwnerPage()?.ChangeDirtyState(Models.DirtyState.HasPendingPullOrPush, !hasPendingPullOrPush);
                });
            }, token);
        }

        public void RefreshWorktrees()
        {
            RefreshWorktreeList(false);
        }

        public void RefreshTags()
        {
            if (_isClosed)
                return;

            if (_cancellationRefreshTags is { IsCancellationRequested: false })
                _cancellationRefreshTags.Cancel();

            _cancellationRefreshTags = new CancellationTokenSource();
            var token = _cancellationRefreshTags.Token;
            var generation = Interlocked.Increment(ref _tagsGeneration);

            Task.Run(async () =>
            {
                var tags = await new Commands.QueryTags(FullPath).GetResultAsync().ConfigureAwait(false);
                if (_isClosed || token.IsCancellationRequested || generation != Interlocked.Read(ref _tagsGeneration))
                    return;

                Dispatcher.UIThread.Invoke(() =>
                {
                    if (_isClosed || token.IsCancellationRequested || generation != Interlocked.Read(ref _tagsGeneration))
                        return;

                    Tags = tags;
                    VisibleTags = BuildVisibleTags();
                    ValidateHistoryFilters(false);
                });
            }, token);
        }

        public void RefreshCommits()
        {
            if (_isClosed)
                return;

            if (_cancellationRefreshCommits is { IsCancellationRequested: false })
                _cancellationRefreshCommits.Cancel();

            _cancellationRefreshCommits = new CancellationTokenSource();
            var token = _cancellationRefreshCommits.Token;
            var generation = Interlocked.Increment(ref _commitsGeneration);

            Task.Run(async () =>
            {
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    if (!_isClosed && !token.IsCancellationRequested && generation == Interlocked.Read(ref _commitsGeneration))
                        _histories.IsLoading = true;
                });

                var builder = new StringBuilder();
                builder
                    .Append('-')
                    .Append(Preferences.Instance.MaxHistoryCommits)
                    .Append(' ');

                if (_uiStates.FocusOnCurrentBranch)
                {
                    var upstream = await new Commands.QueryUpstreamOfCurrentBranch(FullPath)
                        .GetResultAsync()
                        .ConfigureAwait(false);
                    builder.Append(_uiStates.BuildHistoryParams_FocusOnCurrentBranchMode(upstream));
                }
                else
                {
                    builder.Append(_uiStates.BuildHistoryParams_NormalMode(GitDir));
                }

                if (_isClosed || token.IsCancellationRequested || generation != Interlocked.Read(ref _commitsGeneration))
                    return;

                var commits = await new Commands.QueryCommits(FullPath, builder.ToString())
                    .GetResultAsync()
                    .ConfigureAwait(false);
                if (_isClosed || token.IsCancellationRequested || generation != Interlocked.Read(ref _commitsGeneration))
                    return;

                var merged = new HashSet<string>();
                foreach (var c in commits)
                {
                    if (merged.Remove(c.SHA))
                        c.IsMerged = true;

                    if (c.IsMerged)
                    {
                        foreach (var p in c.Parents)
                            merged.Add(p);
                    }
                }

                Dispatcher.UIThread.Invoke(() =>
                {
                    if (_isClosed || token.IsCancellationRequested || generation != Interlocked.Read(ref _commitsGeneration))
                        return;

                    if (_histories != null)
                    {
                        _histories.IsLoading = false;
                        _histories.Commits = commits;

                        if (!string.IsNullOrEmpty(_navigateToCommitDelayed))
                            NavigateToCommit(_navigateToCommitDelayed);
                    }

                    _navigateToCommitDelayed = string.Empty;
                });
            }, token);
        }

        public void RefreshSubmodules()
        {
            if (_isClosed)
                return;

            if (_cancellationRefreshSubmodules is { IsCancellationRequested: false })
                _cancellationRefreshSubmodules.Cancel();

            _cancellationRefreshSubmodules = new CancellationTokenSource();
            var token = _cancellationRefreshSubmodules.Token;
            var generation = Interlocked.Increment(ref _submodulesGeneration);

            if (!MayHaveSubmodules())
            {
                if (_submodules.Count > 0)
                {
                    Dispatcher.UIThread.Invoke(() =>
                    {
                        if (_isClosed || token.IsCancellationRequested || generation != Interlocked.Read(ref _submodulesGeneration))
                            return;

                        Submodules = [];
                        VisibleSubmodules = BuildVisibleSubmodules();
                    });
                }

                return;
            }

            Task.Run(async () =>
            {
                var submodules = await new Commands.QuerySubmodules(FullPath).GetResultAsync().ConfigureAwait(false);
                if (_isClosed || token.IsCancellationRequested || generation != Interlocked.Read(ref _submodulesGeneration))
                    return;

                Action applySubmodules = () =>
                {
                    if (_isClosed || token.IsCancellationRequested || generation != Interlocked.Read(ref _submodulesGeneration))
                        return;
                    bool hasChanged = _submodules.Count != submodules.Count;
                    if (!hasChanged)
                    {
                        var old = new Dictionary<string, Models.Submodule>();
                        foreach (var module in _submodules)
                            old.Add(module.Path, module);

                        foreach (var module in submodules)
                        {
                            if (!old.TryGetValue(module.Path, out var exist))
                            {
                                hasChanged = true;
                                break;
                            }

                            hasChanged = !exist.SHA.Equals(module.SHA, StringComparison.Ordinal) ||
                                         !exist.Branch.Equals(module.Branch, StringComparison.Ordinal) ||
                                         !exist.URL.Equals(module.URL, StringComparison.Ordinal) ||
                                         exist.Status != module.Status;

                            if (hasChanged)
                                break;
                        }
                    }

                    if (hasChanged)
                    {
                        Submodules = submodules;
                        VisibleSubmodules = BuildVisibleSubmodules();
                    }
                };

                if (Avalonia.Application.Current == null || Dispatcher.UIThread.CheckAccess())
                    applySubmodules();
                else
                    Dispatcher.UIThread.Invoke(applySubmodules);
            }, token);
        }

        public void RefreshWorkingCopyChanges()
        {
            if (_isClosed || IsBare)
                return;

            RefreshCurrentTaskStatus();

            if (_cancellationRefreshWorkingCopyChanges is { IsCancellationRequested: false })
                _cancellationRefreshWorkingCopyChanges.Cancel();

            _cancellationRefreshWorkingCopyChanges = new CancellationTokenSource();
            var token = _cancellationRefreshWorkingCopyChanges.Token;
            var generation = Interlocked.Increment(ref _workingCopyGeneration);
            var noOptionalLocks = Interlocked.Add(ref _queryLocalChangesTimes, 1) > 1;

            Task.Run(async () =>
            {
                var changes = await new Commands.QueryLocalChanges(FullPath, _uiStates.IncludeUntrackedInLocalChanges, noOptionalLocks)
                    .WithCancellation(token)
                    .GetResultAsync()
                    .ConfigureAwait(false);
                if (_isClosed || token.IsCancellationRequested || generation != Interlocked.Read(ref _workingCopyGeneration))
                    return;

                changes.Sort((l, r) => Models.NumericSort.Compare(l.Path, r.Path));

                Dispatcher.UIThread.Invoke(() =>
                {
                    if (_isClosed || token.IsCancellationRequested || generation != Interlocked.Read(ref _workingCopyGeneration))
                        return;

                    _workingCopy.SetData(changes);
                    LocalChangesCount = changes.Count;
                    OnPropertyChanged(nameof(InProgressContext));
                    NotifyInProgressOwnershipChanged();
                    GetOwnerPage()?.ChangeDirtyState(Models.DirtyState.HasLocalChanges, changes.Count == 0);
                });
            }, token);
        }

        public void RefreshStashes()
        {
            if (_isClosed || IsBare)
                return;

            if (_cancellationRefreshStashes is { IsCancellationRequested: false })
                _cancellationRefreshStashes.Cancel();

            _cancellationRefreshStashes = new CancellationTokenSource();
            var token = _cancellationRefreshStashes.Token;
            var generation = Interlocked.Increment(ref _stashesGeneration);

            Task.Run(async () =>
            {
                var stashes = await new Commands.QueryStashes(FullPath).GetResultAsync().ConfigureAwait(false);
                if (_isClosed || token.IsCancellationRequested || generation != Interlocked.Read(ref _stashesGeneration))
                    return;

                Dispatcher.UIThread.Invoke(() =>
                {
                    if (_isClosed || token.IsCancellationRequested || generation != Interlocked.Read(ref _stashesGeneration))
                        return;

                    if (_stashesPage != null)
                        _stashesPage.Stashes = stashes;

                    StashesCount = stashes.Count;
                });
            }, token);
        }

        public void CreateNewBranch()
        {
            if (_currentBranch == null)
            {
                SendNotification("Git cannot create a branch before your first commit.", true);
                return;
            }

            if (CanCreatePopup())
                ShowPopup(new CreateBranch(this, _currentBranch));
        }

        public async Task CheckoutBranchAsync(Models.Branch branch)
        {
            if (branch.IsLocal)
            {
                var worktree = _worktrees.Find(x => x.IsAttachedTo(branch));
                if (worktree != null)
                {
                    OpenWorktree(worktree);
                    return;
                }
            }

            if (IsBare)
                return;

            if (!CanCreatePopup())
                return;

            if (branch.IsLocal)
            {
                if (_workingCopy is { CanSwitchBranchDirectly: true })
                    await ShowAndStartPopupAsync(new Checkout(this, branch));
                else
                    ShowPopup(new Checkout(this, branch));
            }
            else
            {
                foreach (var b in _branches)
                {
                    if (b.IsLocal &&
                        !string.IsNullOrEmpty(b.Upstream) &&
                        b.Upstream.Equals(branch.FullName, StringComparison.Ordinal) &&
                        b.Ahead.Count == 0)
                    {
                        if (b.Behind.Count > 0)
                            ShowPopup(new CheckoutAndFastForward(this, b, branch));
                        else if (!b.IsCurrent)
                            await CheckoutBranchAsync(b);

                        return;
                    }
                }

                ShowPopup(new CreateBranch(this, branch));
            }
        }

        public async Task CheckoutTagAsync(Models.Tag tag)
        {
            var c = await new Commands.QuerySingleCommit(FullPath, tag.SHA).GetResultAsync();
            if (c != null && _histories != null)
                await _histories.CheckoutBranchByCommitAsync(c);
        }

        public void DeleteBranch(Models.Branch branch)
        {
            if (CanCreatePopup())
                ShowPopup(new DeleteBranch(this, branch));
        }

        public void DeleteMultipleBranches(List<Models.Branch> branches, bool isLocal)
        {
            if (CanCreatePopup())
                ShowPopup(new DeleteMultipleBranches(this, branches, isLocal));
        }

        public void AddRemote()
        {
            if (CanCreatePopup())
                ShowPopup(new AddRemote(this));
        }

        public void DeleteRemote(Models.Remote remote)
        {
            if (CanCreatePopup())
                ShowPopup(new DeleteRemote(this, remote));
        }

        public async Task ToggleAutoFetchOnRemoteAsync(Models.Remote remote)
        {
            var val = remote.DisableAutoFetch ? "false" : "true";
            var succ = await new Commands.Config(FullPath).SetAsync($"remote.{remote.Name.Quoted()}.disableautofetch", val);
            if (succ)
                remote.DisableAutoFetch = !remote.DisableAutoFetch;
        }

        public void MarkSubmodulesDirtyAfterResetOrSwitch()
        {
            MarkSubmodulesDirtyManually();
        }

        public void OpenSubmodule(string submodule)
        {
            var selfPage = GetOwnerPage();
            if (selfPage == null)
                return;

            var fullpath = Path.GetFullPath(Path.Combine(FullPath, submodule));
            App.GetLauncher().OpenSubRepository(selfPage, fullpath);
        }

        public Task PruneWorktreesAsync()
        {
            if (CanCreatePopup())
                ShowPopup(new PruneWorktrees(this));
            return Task.CompletedTask;
        }

        public void OpenWorktree(Worktree worktree)
        {
            if (worktree.IsCurrent)
                return;

            if (!Directory.Exists(worktree.FullPath))
            {
                SendNotification(App.Text("Worktree.Status.Missing"), true);
                return;
            }

            if (worktree.Status is { IsReadable: false } status)
            {
                SendNotification(App.Text(status.Error), true);
                return;
            }

            var normalizedPath = worktree.FullPath.Replace('\\', '/').TrimEnd('/');
            App.GetLauncher().OpenRepositoryInTab(normalizedPath, null);
        }

        public async Task LockWorktreeAsync(Worktree worktree)
        {
            using var lockWatcher = _watcher?.Lock();
            var log = CreateLog("Lock Worktree");
            var succ = await new Commands.Worktree(FullPath).Use(log).LockAsync(worktree.FullPath);
            if (succ)
                worktree.IsLocked = true;
            log.Complete();
        }

        public async Task UnlockWorktreeAsync(Worktree worktree)
        {
            using var lockWatcher = _watcher?.Lock();
            var log = CreateLog("Unlock Worktree");
            var succ = await new Commands.Worktree(FullPath).Use(log).UnlockAsync(worktree.FullPath);
            if (succ)
                worktree.IsLocked = false;
            log.Complete();
        }

        internal LauncherPage GetOwnerPage()
        {
            var launcher = App.GetLauncher();
            if (launcher == null)
                return null;

            foreach (var page in launcher.Pages)
            {
                if (page.Node.Id.Equals(FullPath) ||
                    Models.WorktreeTaskStore.PathsEqual(page.Node.Id, FullPath))
                    return page;
            }

            return null;
        }

        private BranchTreeNode.Builder BuildBranchTree(List<Models.Branch> branches, List<Models.Remote> remotes, bool validateExpandedNodes = true)
        {
            var builder = new BranchTreeNode.Builder(_uiStates.LocalBranchSortMode, _uiStates.RemoteBranchSortMode);
            if (string.IsNullOrEmpty(_filter))
            {
                builder.SetExpandedNodes(_uiStates.ExpandedBranchNodesInSideBar);
                builder.Run(branches, remotes, false);

                if (validateExpandedNodes)
                {
                    foreach (var invalid in builder.InvalidExpandedNodes)
                        _uiStates.ExpandedBranchNodesInSideBar.Remove(invalid);
                }
            }
            else
            {
                var visibles = new List<Models.Branch>();
                foreach (var b in branches)
                {
                    if (b.FullName.Contains(_filter, StringComparison.OrdinalIgnoreCase))
                        visibles.Add(b);
                }

                builder.Run(visibles, remotes, true);
            }

            var filterMap = _uiStates.GetHistoryFiltersMap();
            UpdateBranchTreeFilterMode(builder.Locals, filterMap);
            UpdateBranchTreeFilterMode(builder.Remotes, filterMap);
            return builder;
        }

        private object BuildVisibleTags()
        {
            switch (_uiStates.TagSortMode)
            {
                case Models.TagSortMode.CreatorDate:
                    _tags.Sort((l, r) => r.CreatorDate.CompareTo(l.CreatorDate));
                    break;
                default:
                    _tags.Sort((l, r) => Models.NumericSort.Compare(l.Name, r.Name));
                    break;
            }

            var visible = new List<Models.Tag>();
            if (string.IsNullOrEmpty(_filter))
            {
                visible.AddRange(_tags);
            }
            else
            {
                foreach (var t in _tags)
                {
                    if (t.Name.Contains(_filter, StringComparison.OrdinalIgnoreCase))
                        visible.Add(t);
                }
            }

            var filterMap = _uiStates.GetHistoryFiltersMap();
            UpdateTagFilterMode(filterMap);

            if (_uiStates.ShowTagsAsTree)
            {
                var tree = TagCollectionAsTree.Build(visible, _visibleTags as TagCollectionAsTree);
                foreach (var node in tree.Tree)
                    node.UpdateFilterMode(filterMap);
                return tree;
            }
            else
            {
                var list = new TagCollectionAsList(visible);
                foreach (var item in list.TagItems)
                    item.FilterMode = filterMap.GetValueOrDefault(item.Tag.Name, Models.FilterMode.None);
                return list;
            }
        }

        private object BuildVisibleSubmodules()
        {
            var visible = new List<Models.Submodule>();
            if (string.IsNullOrEmpty(_filter))
            {
                visible.AddRange(_submodules);
            }
            else
            {
                foreach (var s in _submodules)
                {
                    if (s.Path.Contains(_filter, StringComparison.OrdinalIgnoreCase))
                        visible.Add(s);
                }
            }

            if (_uiStates.ShowSubmodulesAsTree)
                return SubmoduleCollectionAsTree.Build(visible, _visibleSubmodules as SubmoduleCollectionAsTree);
            else
                return new SubmoduleCollectionAsList() { Submodules = visible };
        }

        private void RefreshHistoryFilters(bool refresh)
        {
            HistoryFilterMode = _uiStates.GetHistoryFilterMode();
            if (!refresh)
                return;

            var map = _uiStates.GetHistoryFiltersMap();
            UpdateBranchTreeFilterMode(LocalBranchTrees, map);
            UpdateBranchTreeFilterMode(RemoteBranchTrees, map);
            UpdateTagFilterMode(map);
            RefreshCommits();
        }

        private void UpdateBranchTreeFilterMode(List<BranchTreeNode> nodes, Dictionary<string, Models.FilterMode> map)
        {
            foreach (var node in nodes)
            {
                node.FilterMode = map.GetValueOrDefault(node.Path, Models.FilterMode.None);

                if (!node.IsBranch)
                    UpdateBranchTreeFilterMode(node.Children, map);
            }
        }

        private void UpdateTagFilterMode(Dictionary<string, Models.FilterMode> map)
        {
            if (VisibleTags is TagCollectionAsTree tree)
            {
                foreach (var node in tree.Tree)
                    node.UpdateFilterMode(map);
            }
            else if (VisibleTags is TagCollectionAsList list)
            {
                foreach (var item in list.TagItems)
                    item.FilterMode = map.GetValueOrDefault(item.Tag.Name, Models.FilterMode.None);
            }
        }

        private void ResetBranchTreeFilterMode(List<BranchTreeNode> nodes)
        {
            foreach (var node in nodes)
            {
                node.FilterMode = Models.FilterMode.None;
                if (!node.IsBranch)
                    ResetBranchTreeFilterMode(node.Children);
            }
        }

        private void ResetTagFilterMode()
        {
            if (VisibleTags is TagCollectionAsTree tree)
            {
                var filters = new Dictionary<string, Models.FilterMode>();
                foreach (var node in tree.Tree)
                    node.UpdateFilterMode(filters);
            }
            else if (VisibleTags is TagCollectionAsList list)
            {
                foreach (var item in list.TagItems)
                    item.FilterMode = Models.FilterMode.None;
            }
        }

        private void ValidateHistoryFilters(bool forBranch)
        {
            if (_historyFilterMode == Models.FilterMode.None)
                return;

            var set = new HashSet<string>();

            if (forBranch)
            {
                foreach (var b in _branches)
                    set.Add(b.FullName);

                foreach (var f in _uiStates.HistoryFilters)
                {
                    if (f.Type is Models.FilterType.LocalBranch or Models.FilterType.RemoteBranch)
                        f.IsValid = set.Contains(f.Pattern);
                }
            }
            else
            {
                foreach (var t in _tags)
                    set.Add(t.Name);

                foreach (var f in _uiStates.HistoryFilters)
                {
                    if (f.Type is Models.FilterType.Tag)
                        f.IsValid = set.Contains(f.Pattern);
                }
            }
        }

        private BranchTreeNode FindBranchNode(List<BranchTreeNode> nodes, string path)
        {
            if (string.IsNullOrEmpty(path))
                return null;

            foreach (var node in nodes)
            {
                if (node.Path.Equals(path, StringComparison.Ordinal))
                    return node;

                if (path.StartsWith(node.Path, StringComparison.Ordinal))
                {
                    var founded = FindBranchNode(node.Children, path);
                    if (founded != null)
                        return founded;
                }
            }

            return null;
        }

        private void AutoFetchByTimer(object sender)
        {
            try
            {
                Dispatcher.UIThread.Invoke(AutoFetchOnUIThread);
            }
            catch
            {
                // Ignore exception.
            }
        }

        private async Task AutoFetchOnUIThread()
        {
            if (IsAutoFetching)
                return;

            CommandLog log = null;

            try
            {
                if (!Preferences.Instance.EnableAutoFetch || !CanCreatePopup())
                {
                    _autoFetchAnchorTime = DateTime.Now;
                    return;
                }

                var lockFile = Path.Combine(GitDir, "index.lock");
                if (File.Exists(lockFile))
                    return;

                var now = DateTime.Now;
                var desire = _autoFetchAnchorTime.AddMinutes(Preferences.Instance.AutoFetchInterval);
                if (desire > now)
                    return;

                var remotes = new List<Models.Remote>();
                foreach (var r in _remotes)
                {
                    if (!r.DisableAutoFetch)
                        remotes.Add(r);
                }

                if (remotes.Count == 0)
                    return;

                IsAutoFetching = true;
                log = CreateLog("Auto-Fetch");

                var fetchResult = await Commands.GitSyncService.FetchAsync(FullPath, remotes, log: log);
                MarkFetchResult(fetchResult);
            }
            catch
            {
                // Ignore all exceptions.
            }

            IsAutoFetching = false;
            log?.Complete();
        }

        private readonly string _gitCommonDir = null;
        private Models.RepositorySettings _settings = null;
        private Models.RepositoryUIStates _uiStates = null;
        private Models.FilterMode _historyFilterMode = Models.FilterMode.None;
        private bool _hasAllowedSignersFile = false;
        private ulong _queryLocalChangesTimes = 0;

        private Models.Watcher _watcher = null;
        private Histories _histories = null;
        private WorkingCopy _workingCopy = null;
        private StashesPage _stashesPage = null;
        private int _selectedViewIndex = 0;

        private int _localBranchesCount = 0;
        private int _localChangesCount = 0;
        private int _stashesCount = 0;

        private string _filter = string.Empty;
        private List<Models.Remote> _remotes = [];
        private List<Models.Branch> _branches = [];
        private Models.Branch _currentBranch = null;
        private List<BranchTreeNode> _localBranchTrees = [];
        private List<BranchTreeNode> _remoteBranchTrees = [];
        private List<Worktree> _worktrees = [];
        private List<Models.Tag> _tags = [];
        private object _visibleTags = null;
        private List<Models.Submodule> _submodules = [];
        private object _visibleSubmodules = null;
        private string _navigateToCommitDelayed = string.Empty;

        private bool _isAutoFetching = false;
        private Timer _autoFetchTimer = null;
        private DateTime _autoFetchAnchorTime = DateTime.MinValue;
        private DateTime _lastFetchTime = DateTime.MinValue;
        private bool _lastFetchFailed = false;
        private string _lastFetchError = string.Empty;
        private DateTime _lastWindowActivationRefreshUtc = DateTime.MinValue;
        private Models.OperationReconciliationReport _pendingReconciliation = null;
        private string _dismissedRecoveryKey = string.Empty;

        private CancellationTokenSource _cancellationRefreshBranches = null;
        private CancellationTokenSource _cancellationRefreshTags = null;
        private CancellationTokenSource _cancellationRefreshWorkingCopyChanges = null;
        private CancellationTokenSource _cancellationRefreshCommits = null;
        private CancellationTokenSource _cancellationRefreshStashes = null;
        private CancellationTokenSource _cancellationRefreshSubmodules = null;
        private CancellationTokenSource _cancellationRefreshConfiguration = null;
        private volatile bool _isClosed;
        private long _branchesGeneration;
        private long _tagsGeneration;
        private long _commitsGeneration;
        private long _submodulesGeneration;
        private long _workingCopyGeneration;
        private long _stashesGeneration;
        private long _configStateGeneration;
    }
}
