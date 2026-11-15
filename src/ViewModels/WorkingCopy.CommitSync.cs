using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;

namespace SourceGit.ViewModels
{
    public partial class WorkingCopy
    {
        public bool HasPartialStagedFiles
        {
            get => _hasPartialStagedFiles;
            private set => SetProperty(ref _hasPartialStagedFiles, value);
        }

        public bool HasCommitAlert
        {
            get => _hasCommitAlert;
            private set => SetProperty(ref _hasCommitAlert, value);
        }

        public string CommitAlertMessage
        {
            get => _commitAlertMessage;
            private set => SetProperty(ref _commitAlertMessage, value);
        }

        public bool ShowIdentityPrompt
        {
            get => _showIdentityPrompt;
            private set => SetProperty(ref _showIdentityPrompt, value);
        }

        public string IdentityAuthorName
        {
            get => _identityAuthorName;
            set => SetProperty(ref _identityAuthorName, value);
        }

        public string IdentityAuthorEmail
        {
            get => _identityAuthorEmail;
            set => SetProperty(ref _identityAuthorEmail, value);
        }

        public bool IsSavingIdentity
        {
            get => _isSavingIdentity;
            private set => SetProperty(ref _isSavingIdentity, value);
        }

        public bool HasTwoStepStatus
        {
            get => _hasTwoStepStatus;
            private set => SetProperty(ref _hasTwoStepStatus, value);
        }

        public bool TwoStepPushSucceeded
        {
            get => _twoStepPushSucceeded;
            private set => SetProperty(ref _twoStepPushSucceeded, value);
        }

        public bool CanRetryPushOnly
        {
            get => _canRetryPushOnly;
            private set => SetProperty(ref _canRetryPushOnly, value);
        }

        public string TwoStepCommitText
        {
            get => _twoStepCommitText;
            private set => SetProperty(ref _twoStepCommitText, value);
        }

        public string TwoStepPushText
        {
            get => _twoStepPushText;
            private set => SetProperty(ref _twoStepPushText, value);
        }

        public Models.StagedSnapshot LastCapturedSnapshot => _displayedSnapshot;
        public Models.CommitAndPushSession ActiveTwoStepSession => _activeTwoStepSession;

        public bool HasInlineNoticeArea =>
            HasPartialStagedFiles || HasCommitAlert || ShowIdentityPrompt || HasTwoStepStatus || ShowExternalInProgressFooter;

        public async Task RefreshStagedSnapshotAsync(CancellationToken cancellationToken = default)
        {
            var seq = Interlocked.Increment(ref _snapshotRefreshSeq);
            try
            {
                var snap = await Commands.QueryStagedSnapshot
                    .CaptureAsync(_repo.FullPath, cancellationToken)
                    .ConfigureAwait(false);

                if (seq == Interlocked.Read(ref _snapshotRefreshSeq))
                    _displayedSnapshot = snap;
            }
            catch
            {
                // Ignore best-effort background snapshot refreshes when repository closes.
            }
        }

        public void DismissCommitAlert()
        {
            HasCommitAlert = false;
            CommitAlertMessage = string.Empty;
            NotifyInlineNoticeVisibility();
        }

        public void CancelIdentityPrompt()
        {
            ShowIdentityPrompt = false;
            _pendingIdentityCommit = false;
            NotifyInlineNoticeVisibility();
        }

        public void DismissTwoStepBanner()
        {
            HasTwoStepStatus = false;
            TwoStepPushSucceeded = false;
            CanRetryPushOnly = false;
            TwoStepCommitText = string.Empty;
            TwoStepPushText = string.Empty;
            _activeTwoStepSession = null;
            NotifyInlineNoticeVisibility();
        }

        public async Task SaveLocalIdentityAndContinueAsync()
        {
            if (IsSavingIdentity)
                return;

            var name = _identityAuthorName?.Trim() ?? string.Empty;
            var email = _identityAuthorEmail?.Trim() ?? string.Empty;
            if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(email))
            {
                SetCommitAlert("提交者姓名与邮箱均不能为空，请补充后保存。");
                return;
            }

            IsSavingIdentity = true;
            try
            {
                var cfg = new Commands.Config(_repo.FullPath);
                var nameRes = await cfg.SetScopedAsync(Models.GitConfigWriteScope.Local, "user.name", name);
                var emailRes = await cfg.SetScopedAsync(Models.GitConfigWriteScope.Local, "user.email", email);

                if (!nameRes.IsSuccess || !emailRes.IsSuccess)
                {
                    var err = !nameRes.IsSuccess ? nameRes.StdErr : emailRes.StdErr;
                    SetCommitAlert(string.IsNullOrWhiteSpace(err) ? "保存仓库局部提交身份失败。" : err.Trim());
                    return;
                }

                ShowIdentityPrompt = false;
                DismissCommitAlert();
                _repo.SendNotification($"已保存当前仓库局部提交身份：{name} <{email}>");

                if (_pendingIdentityCommit)
                {
                    var autoStage = _pendingIdentityAutoStage;
                    var autoPush = _pendingIdentityAutoPush;
                    _pendingIdentityCommit = false;
                    await CommitAsync(autoStage, autoPush);
                }
            }
            finally
            {
                IsSavingIdentity = false;
                NotifyInlineNoticeVisibility();
            }
        }

        public async Task RetryLastPushOnlyAsync()
        {
            if (_activeTwoStepSession is not { CanRetryPushOnly: true } || IsCommitting)
                return;

            using var lockWatcher = _repo.LockWatcher();
            IsCommitting = true;
            try
            {
                var log = _repo.CreateLog("Retry Push");
                var updated = await Commands.GitSyncService.RetryPushSessionAsync(
                    _activeTwoStepSession,
                    null,
                    log,
                    journalStore: Models.OperationJournalStore.Instance);
                log.Complete();

                ApplyTwoStepSessionState(updated);
                _repo.MarkBranchesDirtyManually();

                if (updated.IsFullySucceeded)
                    _repo.SendNotification(updated.PushResult.Summary);
            }
            finally
            {
                IsCommitting = false;
            }
        }

        public void OpenPushForPendingSession()
        {
            if (!_repo.CanCreatePopup())
                return;

            var branch = _repo.CurrentBranch;
            var push = new Push(_repo, branch);
            push.PushFinishedCallback = result =>
            {
                Dispatcher.UIThread.Post(() =>
                {
                    if (_activeTwoStepSession != null && result != null)
                    {
                        var updated = new Models.CommitAndPushSession
                        {
                            RepoPath = _activeTwoStepSession.RepoPath,
                            CommitResult = _activeTwoStepSession.CommitResult,
                            PushAttempted = true,
                            PushResult = result,
                            PendingPushOptions = result.Succeeded
                                ? null
                                : new Models.PushExecutionOptions
                                {
                                    LocalBranch = result.LocalBranch,
                                    Remote = _repo.Remotes.Find(r => r.Name == result.RemoteName),
                                    RemoteBranch = result.RemoteBranch,
                                    SetUpstream = string.IsNullOrEmpty(_repo.CurrentBranch?.Upstream),
                                },
                        };

                        ApplyTwoStepSessionState(updated);
                    }
                });
            };

            _repo.ShowPopup(push);
        }

        private List<string> FindPartiallyStagedSelectionPaths(List<Models.Change> targets)
        {
            if (targets == null || targets.Count == 0 || _staged == null || _staged.Count == 0)
                return [];

            var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
            var stagedSet = new HashSet<string>(_staged.Select(s => s.Path), comparer);
            var matched = new List<string>();
            foreach (var c in targets)
            {
                if (c != null && stagedSet.Contains(c.Path))
                    matched.Add(c.Path);
            }

            return matched;
        }

        private void UpdatePartialStagingFlag()
        {
            if (_staged == null || _unstaged == null || _staged.Count == 0 || _unstaged.Count == 0)
            {
                HasPartialStagedFiles = false;
            }
            else
            {
                var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
                var stagedSet = new HashSet<string>(_staged.Select(s => s.Path), comparer);
                HasPartialStagedFiles = _unstaged.Any(u => stagedSet.Contains(u.Path));
            }

            NotifyInlineNoticeVisibility();
        }

        private void SetCommitAlert(string message)
        {
            HasCommitAlert = !string.IsNullOrWhiteSpace(message);
            CommitAlertMessage = message ?? string.Empty;
            NotifyInlineNoticeVisibility();
        }

        private void ApplyTwoStepSessionState(Models.CommitAndPushSession session)
        {
            _activeTwoStepSession = session;
            if (session == null || !session.CommitSucceeded)
            {
                DismissTwoStepBanner();
                return;
            }

            HasTwoStepStatus = true;
            TwoStepCommitText = App.Text("WorkingCopy.StepCommitSucceeded", session.CommitResult.ShortSha);

            if (session.PushAttempted && session.PushSucceeded)
            {
                TwoStepPushSucceeded = true;
                CanRetryPushOnly = false;
                TwoStepPushText = App.Text("WorkingCopy.StepPushSucceeded", $"{session.PushResult.RemoteName}/{session.PushResult.RemoteBranch}");
            }
            else if (session.PushAttempted)
            {
                TwoStepPushSucceeded = false;
                CanRetryPushOnly = session.CanRetryPushOnly;
                TwoStepPushText = App.Text("WorkingCopy.StepPushFailed", session.CommitResult.ShortSha) + " " + (session.PushResult?.Summary ?? string.Empty);
            }
            else
            {
                TwoStepPushSucceeded = false;
                CanRetryPushOnly = false;
                TwoStepPushText = "当前分支未设置上游，请确认推送远端与目标分支。";
            }

            NotifyInlineNoticeVisibility();
        }

        private Models.PushExecutionOptions TryResolveUpstreamPushOptions()
        {
            var current = _repo.CurrentBranch;
            if (current == null || string.IsNullOrWhiteSpace(current.Upstream) || _repo.Remotes.Count == 0)
                return null;

            var remoteBranchObj = _repo.Branches.Find(b => !b.IsLocal && string.Equals(b.FullName, current.Upstream, StringComparison.Ordinal));
            if (remoteBranchObj != null)
            {
                var remote = _repo.Remotes.Find(r => string.Equals(r.Name, remoteBranchObj.Remote, StringComparison.Ordinal));
                if (remote != null)
                {
                    return new Models.PushExecutionOptions
                    {
                        LocalBranch = current.Name,
                        Remote = remote,
                        RemoteBranch = remoteBranchObj.Name,
                        SetUpstream = false,
                    };
                }
            }

            const string remotesPrefix = "refs/remotes/";
            if (current.Upstream.StartsWith(remotesPrefix, StringComparison.Ordinal))
            {
                var remainder = current.Upstream.Substring(remotesPrefix.Length);
                foreach (var remote in _repo.Remotes.OrderByDescending(r => r.Name.Length))
                {
                    var prefix = remote.Name + "/";
                    if (remainder.StartsWith(prefix, StringComparison.Ordinal))
                    {
                        var remoteBranchName = remainder.Substring(prefix.Length);
                        if (!string.IsNullOrEmpty(remoteBranchName))
                        {
                            return new Models.PushExecutionOptions
                            {
                                LocalBranch = current.Name,
                                Remote = remote,
                                RemoteBranch = remoteBranchName,
                                SetUpstream = false,
                            };
                        }
                    }
                }
            }

            return null;
        }

        private void NotifyInlineNoticeVisibility()
        {
            OnPropertyChanged(nameof(HasInlineNoticeArea));
        }

        private bool _hasPartialStagedFiles = false;
        private bool _hasCommitAlert = false;
        private string _commitAlertMessage = string.Empty;
        private bool _showIdentityPrompt = false;
        private string _identityAuthorName = string.Empty;
        private string _identityAuthorEmail = string.Empty;
        private bool _isSavingIdentity = false;
        private bool _pendingIdentityCommit = false;
        private bool _pendingIdentityAutoStage = false;
        private bool _pendingIdentityAutoPush = false;
        private bool _hasTwoStepStatus = false;
        private bool _twoStepPushSucceeded = false;
        private bool _canRetryPushOnly = false;
        private string _twoStepCommitText = string.Empty;
        private string _twoStepPushText = string.Empty;
        private long _snapshotRefreshSeq = 0;
        private Models.StagedSnapshot _displayedSnapshot = null;
        private Models.CommitAndPushSession _activeTwoStepSession = null;
    }
}
