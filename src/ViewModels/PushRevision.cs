using System;
using System.Threading;
using System.Threading.Tasks;

namespace SourceGit.ViewModels
{
    public class PushRevision : Popup
    {
        public Models.Commit Revision { get; }
        public Models.Branch RemoteBranch { get; }

        public bool Force
        {
            get => _force;
            set
            {
                if (SetProperty(ref _force, value))
                {
                    OnPropertyChanged(nameof(ShowForceDetails));
                    OnPropertyChanged(nameof(ForceBlockMessage));
                    OnPropertyChanged(nameof(HasForceBlockMessage));
                    NotifyPopupButtonsChanged();

                    if (value)
                        _ = EnsureForceGuardsLoadedAsync();
                }
            }
        }

        public bool ShowForceDetails => _force;
        public bool IsCheckingForceGuards
        {
            get => _isCheckingForceGuards;
            private set
            {
                if (SetProperty(ref _isCheckingForceGuards, value))
                {
                    OnPropertyChanged(nameof(ForceBlockMessage));
                    OnPropertyChanged(nameof(HasForceBlockMessage));
                    NotifyPopupButtonsChanged();
                }
            }
        }

        public string RemoteTipDisplay
        {
            get
            {
                if (_isCheckingForceGuards)
                    return App.Text("RewriteRemote.Loading");
                if (_probe == null)
                    return string.Empty;
                if (!_probe.QuerySucceeded)
                    return _probe.ErrorSummary;
                if (!_probe.ExistsOnServer)
                    return App.Text("DeleteBranch.RemoteAbsentOnServer");
                var shortSha = _probe.LiveTipSha.Length > 10 ? _probe.LiveTipSha[..10] : _probe.LiveTipSha;
                return App.Text("Push.Revision.RemoteTipFormat", shortSha, RemoteBranch.FriendlyName);
            }
        }

        public bool HasRemoteTipDisplay => _force && !string.IsNullOrWhiteSpace(RemoteTipDisplay);

        public string ForceBlockMessage
        {
            get
            {
                if (!_force || _isCheckingForceGuards)
                    return string.Empty;

                if (_protection is { IsEffectivelyProtected: true })
                    return App.Text("Push.Revision.BlockedByProtection");

                if (_probe != null && !_probe.QuerySucceeded)
                    return App.Text("Push.Revision.OfflineForceBlocked");

                return string.Empty;
            }
        }

        public bool HasForceBlockMessage => !string.IsNullOrWhiteSpace(ForceBlockMessage);

        public override bool CanConfirm
        {
            get
            {
                if (!_force)
                    return true;

                if (_isCheckingForceGuards)
                    return false;

                if (_protection == null || _protection.IsEffectivelyProtected)
                    return false;

                if (_probe == null || !_probe.QuerySucceeded)
                    return false;

                return true;
            }
        }

        public PushRevision(Repository repo, Models.Commit revision, Models.Branch remoteBranch)
        {
            _repo = repo;
            Revision = revision;
            RemoteBranch = remoteBranch;
            _force = false;
            CanTerminate = true;
            _protectionTask = LoadProtectionAsync();
        }

        public Task EnsureForceGuardsLoadedAsync()
        {
            _forceGuardTask ??= LoadForceGuardsAsync();
            return _forceGuardTask;
        }

        public override async Task<bool> Sure()
        {
            if (Force)
            {
                await EnsureForceGuardsLoadedAsync().ConfigureAwait(true);
                if (!CanConfirm)
                    return false;
            }

            using var lockWatcher = _repo.LockWatcher();
            ProgressDescription = $"Push {Revision.SHA.AsSpan(0, 10)} -> {RemoteBranch.FriendlyName} ...";

            var log = _repo.CreateLog("Push Revision");
            Use(log);

            using var cancellation = new CancellationTokenSource();
            _cancellation = cancellation;
            try
            {
                var token = cancellation.Token;
                var localBranch = await Commands.BranchDeletionService.ReadCurrentBranchNameAsync(_repo.FullPath, token).ConfigureAwait(true);
                if (Force)
                {
                    var preflight = await Commands.HistoryResetService.InspectRemoteRewriteForTargetAsync(
                        _repo.FullPath, localBranch, Revision.SHA, RemoteBranch.Remote, RemoteBranch.Name, token).ConfigureAwait(true);
                    if (!preflight.CanRewriteRemote || _probe == null ||
                        !string.Equals(preflight.RemoteName, _probe.RemoteName, StringComparison.Ordinal) ||
                        !string.Equals(preflight.RemoteBranchName, _probe.RemoteBranchName, StringComparison.Ordinal) ||
                        !string.Equals(preflight.PushUrl, _probe.PushUrl, StringComparison.Ordinal) ||
                        !string.Equals(preflight.LiveRemoteTipSha, _probe.LiveTipSha, StringComparison.Ordinal))
                    {
                        Models.Notification.Send(_repo.FullPath, preflight.CanRewriteRemote
                            ? "远端状态在确认后已变化，请关闭弹窗并重新确认。"
                            : preflight.BlockReason, true);
                        return false;
                    }

                    var outcome = await Commands.HistoryResetService.ExecuteRemoteRewriteOnlyAsync(
                        _repo.FullPath, new Models.RemoteRewriteOptions { Preflight = preflight },
                        Models.OperationJournalStore.Instance, log, token).ConfigureAwait(true);
                    await _repo.RefreshRecoveryStatusAsync(Models.OperationJournalStore.Instance).ConfigureAwait(true);
                    if (!outcome.Succeeded)
                        Models.Notification.Send(_repo.FullPath, outcome.Summary, true);
                    return outcome.Succeeded;
                }

                var remote = _repo.Remotes.Find(x => x.Name.Equals(RemoteBranch.Remote, StringComparison.Ordinal));
                var result = await Commands.GitSyncService.PushSingleBranchAsync(_repo.FullPath,
                    new Models.PushExecutionOptions
                    {
                        LocalBranch = localBranch,
                        SourceRevisionSha = Revision.SHA,
                        Remote = remote,
                        RemoteBranch = RemoteBranch.Name,
                    }, log, token, Models.OperationJournalStore.Instance).ConfigureAwait(true);
                if (!result.Succeeded)
                    Models.Notification.Send(_repo.FullPath, result.Summary, true);
                return result.Succeeded;
            }
            finally
            {
                log.Complete();
                _cancellation = null;
            }
        }

        public override void Terminate()
        {
            var _ = _cancellation?.CancelAsync();
        }

        private async Task LoadProtectionAsync()
        {
            try
            {
                _protection = await Commands.BranchProtectionPolicy
                    .InspectAsync(_repo.FullPath, RemoteBranch.Name)
                    .ConfigureAwait(true);
            }
            catch
            {
                _protection = new Models.BranchProtectionStatus
                {
                    BranchName = RemoteBranch.Name,
                    IsDefaultProtectedName = true,
                };
            }
        }

        private async Task LoadForceGuardsAsync()
        {
            IsCheckingForceGuards = true;
            try
            {
                await _protectionTask.ConfigureAwait(true);
                _probe = await Commands.BranchDeletionService
                    .ProbeRemoteBranchAsync(_repo.FullPath, RemoteBranch.Remote, RemoteBranch.Name)
                    .ConfigureAwait(true);

                OnPropertyChanged(nameof(RemoteTipDisplay));
                OnPropertyChanged(nameof(HasRemoteTipDisplay));
                OnPropertyChanged(nameof(ForceBlockMessage));
                OnPropertyChanged(nameof(HasForceBlockMessage));
                NotifyPopupButtonsChanged();
            }
            finally
            {
                IsCheckingForceGuards = false;
                OnPropertyChanged(nameof(RemoteTipDisplay));
                OnPropertyChanged(nameof(HasRemoteTipDisplay));
                OnPropertyChanged(nameof(ForceBlockMessage));
                OnPropertyChanged(nameof(HasForceBlockMessage));
                NotifyPopupButtonsChanged();
            }
        }

        private readonly Repository _repo;
        private readonly Task _protectionTask;
        private Task _forceGuardTask;
        private Models.BranchProtectionStatus _protection;
        private Models.RemoteBranchProbeResult _probe;
        private bool _force;
        private bool _isCheckingForceGuards;
        private CancellationTokenSource _cancellation;
    }
}
