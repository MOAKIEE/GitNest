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

            _cancellation = new CancellationTokenSource();
            var token = _cancellation.Token;

            var remote = _repo.Remotes.Find(x => x.Name.Equals(RemoteBranch.Remote, StringComparison.Ordinal));
            string pinnedLeaseSha = null;
            if (Force && _probe is { QuerySucceeded: true })
                pinnedLeaseSha = _probe.ExistsOnServer ? _probe.LiveTipSha : string.Empty;

            var succ = await new Commands.Push(
                _repo.FullPath,
                Revision,
                remote,
                RemoteBranch,
                Force,
                pinnedLeaseSha).WithCancellation(token).Use(log).ExecAsync();

            log.Complete();

            _cancellation = null;
            return succ;
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
