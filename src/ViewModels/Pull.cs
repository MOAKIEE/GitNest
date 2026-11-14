using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Threading;
using System.Threading.Tasks;

namespace SourceGit.ViewModels
{
    public class Pull : Popup
    {
        public List<Models.Remote> Remotes => _repo.Remotes;
        public Models.Branch Current { get; }

        public bool HasSpecifiedRemoteBranch
        {
            get;
            private set;
        }

        public Models.Remote SelectedRemote
        {
            get => _selectedRemote;
            set
            {
                if (SetProperty(ref _selectedRemote, value))
                {
                    ResetFeedbackBanner();
                    PostRemoteSelected();
                }
            }
        }

        public List<Models.Branch> RemoteBranches
        {
            get => _remoteBranches;
            private set => SetProperty(ref _remoteBranches, value);
        }

        [Required(ErrorMessage = "Remote branch to pull is required!!!")]
        public Models.Branch SelectedBranch
        {
            get => _selectedBranch;
            set
            {
                if (SetProperty(ref _selectedBranch, value, true))
                    ResetFeedbackBanner();
            }
        }

        public Pull(Repository repo, Models.Branch specifiedRemoteBranch)
        {
            _repo = repo;
            Current = repo.CurrentBranch;
            CanTerminate = true;

            if (specifiedRemoteBranch != null)
            {
                _selectedRemote = repo.Remotes.Find(x => x.Name == specifiedRemoteBranch.Remote);
                _selectedBranch = specifiedRemoteBranch;

                var branches = new List<Models.Branch>();
                foreach (var branch in _repo.Branches)
                {
                    if (branch.Remote == specifiedRemoteBranch.Remote)
                        branches.Add(branch);
                }

                _remoteBranches = branches;
                HasSpecifiedRemoteBranch = true;
            }
            else
            {
                Models.Remote autoSelectedRemote = null;
                if (!string.IsNullOrEmpty(Current.Upstream))
                {
                    var remoteNameEndIdx = Current.Upstream.IndexOf('/', 13);
                    if (remoteNameEndIdx > 0)
                    {
                        var remoteName = Current.Upstream.Substring(13, remoteNameEndIdx - 13);
                        autoSelectedRemote = _repo.Remotes.Find(x => x.Name == remoteName);
                    }
                }

                if (autoSelectedRemote == null)
                {
                    Models.Remote remote = null;
                    if (!string.IsNullOrEmpty(_repo.Settings.DefaultRemote))
                        remote = _repo.Remotes.Find(x => x.Name == _repo.Settings.DefaultRemote);
                    _selectedRemote = remote ?? _repo.Remotes[0];
                }
                else
                {
                    _selectedRemote = autoSelectedRemote;
                }

                PostRemoteSelected();
                HasSpecifiedRemoteBranch = false;
            }
        }

        public override async Task<bool> Sure()
        {
            using var lockWatcher = _repo.LockWatcher();

            var log = _repo.CreateLog("Pull");
            Use(log);

            _cancellation = new CancellationTokenSource();
            var token = _cancellation.Token;

            HasFeedbackBanner = false;
            IsDiverged = false;
            DivergedCountsText = string.Empty;
            FeedbackSummary = string.Empty;
            BlockingPathsText = string.Empty;

            if (Current == null || _selectedRemote == null || _selectedBranch == null)
            {
                HasFeedbackBanner = true;
                FeedbackSummary = "请选择有效的远端和目标分支。";
                log.Complete();
                _cancellation = null;
                return false;
            }

            var res = await Commands.GitSyncService.PullFastForwardAsync(
                _repo.FullPath,
                _selectedRemote,
                _selectedBranch.Name,
                log,
                token,
                Models.OperationJournalStore.Instance);

            log.Complete();
            _cancellation = null;

            if (res.Succeeded)
            {
                _repo.MarkFetched();
                if (_repo.SelectedViewIndex == 0 && !string.IsNullOrEmpty(res.AfterHead))
                    _repo.NavigateToCommit(res.AfterHead, true);

                _repo.SendNotification(res.Summary);
                return true;
            }

            if (res.Outcome == Models.PullOutcomeKind.Canceled)
                return false;

            HasFeedbackBanner = true;
            if (res.Outcome == Models.PullOutcomeKind.Diverged)
            {
                IsDiverged = true;
                DivergedCountsText = App.Text("Pull.DivergedCounts", res.LocalAheadCount, res.RemoteBehindCount);
                FeedbackSummary = App.Text("Pull.DivergedBanner");
            }
            else if (res.Outcome == Models.PullOutcomeKind.LocalChangesConflict)
            {
                IsDiverged = false;
                FeedbackSummary = App.Text("Pull.LocalConflictBanner");
                BlockingPathsText = res.BlockingPaths.Count > 0
                    ? string.Join(Environment.NewLine, res.BlockingPaths)
                    : res.DiagnosticDetail;
            }
            else
            {
                IsDiverged = false;
                FeedbackSummary = res.Summary;
            }

            return false;
        }

        public bool HasFeedbackBanner
        {
            get => _hasFeedbackBanner;
            private set => SetProperty(ref _hasFeedbackBanner, value);
        }

        public bool IsDiverged
        {
            get => _isDiverged;
            private set => SetProperty(ref _isDiverged, value);
        }

        public string DivergedCountsText
        {
            get => _divergedCountsText;
            private set => SetProperty(ref _divergedCountsText, value);
        }

        public string FeedbackSummary
        {
            get => _feedbackSummary;
            private set => SetProperty(ref _feedbackSummary, value);
        }

        public string BlockingPathsText
        {
            get => _blockingPathsText;
            private set => SetProperty(ref _blockingPathsText, value);
        }

        public bool HasBlockingPaths => !string.IsNullOrWhiteSpace(_blockingPathsText);

        public void OpenMergeFlow()
        {
            if (Current == null)
                return;

            var sourceBranch = _selectedBranch;
            if (sourceBranch == null && _selectedRemote != null)
            {
                sourceBranch = _repo.Branches.Find(b =>
                    !b.IsLocal &&
                    b.Remote == _selectedRemote.Name &&
                    b.Name == Current.Name);
            }

            if (sourceBranch == null)
            {
                _repo.DismissPopup();
                return;
            }

            _repo.ReplacePopup(new Merge(_repo, sourceBranch, Current.Name, forceFastForward: false));
        }

        public void CloseForExternalTool()
        {
            _repo.DismissPopup();
        }

        private void ResetFeedbackBanner()
        {
            HasFeedbackBanner = false;
            IsDiverged = false;
            DivergedCountsText = string.Empty;
            FeedbackSummary = string.Empty;
            BlockingPathsText = string.Empty;
        }

        private bool _hasFeedbackBanner = false;
        private bool _isDiverged = false;
        private string _divergedCountsText = string.Empty;
        private string _feedbackSummary = string.Empty;
        private string _blockingPathsText = string.Empty;

        public override void Terminate()
        {
            // Just fire cancel event and UI will auto wait the `Sure` complete
            var _ = _cancellation?.CancelAsync();
        }

        private void PostRemoteSelected()
        {
            var remoteName = _selectedRemote.Name;
            var branches = new List<Models.Branch>();
            foreach (var branch in _repo.Branches)
            {
                if (!branch.IsLocal && branch.Remote.Equals(remoteName, StringComparison.Ordinal))
                    branches.Add(branch);
            }

            Models.Branch selected = null;
            if (!string.IsNullOrEmpty(Current.Upstream))
                selected = branches.Find(x => x.FullName.Equals(Current.Upstream, StringComparison.Ordinal));

            if (selected == null)
                selected = branches.Find(x => x.Name.Equals(Current.Name, StringComparison.Ordinal));

            RemoteBranches = branches;
            SelectedBranch = selected;
        }

        private readonly Repository _repo = null;
        private Models.Remote _selectedRemote = null;
        private List<Models.Branch> _remoteBranches = null;
        private Models.Branch _selectedBranch = null;
        private CancellationTokenSource _cancellation = null;
    }
}
