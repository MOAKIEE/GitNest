using System;
using System.Threading.Tasks;

using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;

namespace SourceGit.ViewModels
{
    public sealed class GitSetup : ObservableObject
    {
        public static GitSetup Instance { get; } = new();

        public bool IsChecking
        {
            get => _isChecking;
            private set => SetProperty(ref _isChecking, value);
        }

        public bool IsReady => Native.OS.GitInstallation.IsReady;
        public string Executable => Native.OS.GitInstallation.Executable;
        public string Version => Native.OS.GitInstallation.VersionText;
        public bool HasVersion => !string.IsNullOrEmpty(Version);

        public string StatusMessage => App.Text(Native.OS.GitInstallation.Status switch
        {
            Models.GitInstallationStatus.Ready => "GitSetup.Ready",
            Models.GitInstallationStatus.Unsupported => "GitSetup.Unsupported",
            Models.GitInstallationStatus.TimedOut => "GitSetup.TimedOut",
            Models.GitInstallationStatus.InvalidExecutable => "GitSetup.InvalidExecutable",
            Models.GitInstallationStatus.InvalidVersion => "GitSetup.InvalidVersion",
            _ => "GitSetup.Missing",
        }, Models.GitVersions.MINIMAL);

        public string IdentityMessage
        {
            get => _identityMessage;
            private set => SetProperty(ref _identityMessage, value);
        }

        private GitSetup()
        {
            Native.OS.GitInstallationChanged += () => Dispatcher.UIThread.Post(() =>
            {
                OnPropertyChanged(nameof(IsReady));
                OnPropertyChanged(nameof(Executable));
                OnPropertyChanged(nameof(Version));
                OnPropertyChanged(nameof(HasVersion));
                OnPropertyChanged(nameof(StatusMessage));
                _ = RefreshIdentityAsync();
            });
        }

        public async Task DetectAsync()
        {
            if (IsChecking)
                return;

            IsChecking = true;
            try
            {
                var previous = Native.OS.GitExecutable;
                var executable = await Task.Run(Native.OS.FindGitExecutable);
                var installation = await Models.GitInstallation.InspectAsync(executable ?? previous);
                if (Native.OS.GitExecutable == previous)
                {
                    Preferences.Instance.UseGitInstallation(installation);
                    Preferences.Instance.Save();
                }
            }
            catch (Exception)
            {
                Models.Notification.Send(null, App.Text("GitSetup.DetectFailed"), true);
            }
            finally
            {
                IsChecking = false;
            }
        }

        public async Task SelectAsync(string executable)
        {
            if (IsChecking)
                return;

            IsChecking = true;
            try
            {
                var previous = Native.OS.GitExecutable;
                var installation = await Models.GitInstallation.InspectAsync(executable);
                if (Native.OS.GitExecutable == previous)
                {
                    Preferences.Instance.UseGitInstallation(installation);
                    Preferences.Instance.Save();
                }
            }
            finally
            {
                IsChecking = false;
            }
        }

        public async Task RefreshIdentityAsync()
        {
            var generation = ++_identityGeneration;
            var installation = Native.OS.GitInstallation;
            if (!installation.IsReady)
            {
                IdentityMessage = App.Text("GitSetup.IdentityAfterGit");
                return;
            }

            IdentityMessage = App.Text("GitSetup.ReadingIdentity");
            var identity = await new Commands.Config(null).ReadCommitIdentityAsync();
            if (generation != _identityGeneration || installation != Native.OS.GitInstallation)
                return;

            IdentityMessage = !identity.IsReadable ? App.Text("GitSetup.IdentityReadFailed") :
                identity.IsComplete ? App.Text("GitSetup.Identity", identity.Name, identity.Email) :
                App.Text("GitSetup.IdentityMissing");
        }

        private bool _isChecking = false;
        private string _identityMessage = string.Empty;
        private int _identityGeneration = 0;
    }
}
