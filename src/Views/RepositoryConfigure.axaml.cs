using System;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;

namespace SourceGit.Views
{
    public partial class RepositoryConfigure : ChromelessWindow
    {
        public RepositoryConfigure()
        {
            CloseOnESC = true;
            InitializeComponent();
        }

        protected override async void OnOpened(EventArgs e)
        {
            base.OnOpened(e);

            if (!Design.IsDesignMode && DataContext is ViewModels.RepositoryConfigure configure)
            {
                configure.AttachExternalChangeMonitor();
                Activated += OnConfigureWindowActivated;
                await configure.EnsureLoadedAsync();
            }
        }

        protected override void OnClosed(EventArgs e)
        {
            Activated -= OnConfigureWindowActivated;
            if (DataContext is ViewModels.RepositoryConfigure configure)
                configure.DetachExternalChangeMonitor();

            base.OnClosed(e);
        }

        private async void OnConfigureWindowActivated(object sender, EventArgs e)
        {
            if (DataContext is ViewModels.RepositoryConfigure vm)
                await vm.CheckExternalChangesAsync();
        }

        private async void OnReloadClicked(object sender, RoutedEventArgs e)
        {
            if (DataContext is ViewModels.RepositoryConfigure vm)
                await vm.ManualRefreshAsync();
            e.Handled = true;
        }

        private async void OnAcceptExternalReloadClicked(object sender, RoutedEventArgs e)
        {
            if (DataContext is ViewModels.RepositoryConfigure vm)
                await vm.AcceptExternalReloadAsync();
            e.Handled = true;
        }

        private async void OnKeepDraftsWithExternalBaselineClicked(object sender, RoutedEventArgs e)
        {
            if (DataContext is ViewModels.RepositoryConfigure vm)
                await vm.KeepDraftsWithExternalBaselineAsync();
            e.Handled = true;
        }

        private async void OnSaveClicked(object sender, RoutedEventArgs e)
        {
            if (DataContext is ViewModels.RepositoryConfigure vm)
                await vm.SaveAsync();
            e.Handled = true;
        }

        private void OnCloseClicked(object sender, RoutedEventArgs e)
        {
            Close();
            e.Handled = true;
        }

        private void OnStageAddRemoteClicked(object sender, RoutedEventArgs e)
        {
            if (DataContext is ViewModels.RepositoryConfigure vm)
                vm.StageAddRemote();
            e.Handled = true;
        }

        private void OnToggleRemoveRemoteClicked(object sender, RoutedEventArgs e)
        {
            if (DataContext is ViewModels.RepositoryConfigure vm)
                vm.ToggleRemoveSelectedRemote();
            e.Handled = true;
        }

        private void OnClearUpstreamClicked(object sender, RoutedEventArgs e)
        {
            if (DataContext is ViewModels.RepositoryConfigure vm)
                vm.ClearUpstream();
            e.Handled = true;
        }

        private void OnPickUpstreamSuggestionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (sender is ComboBox { SelectedItem: string branch } &&
                !string.IsNullOrEmpty(branch) &&
                DataContext is ViewModels.RepositoryConfigure vm)
            {
                vm.UpstreamTargetBranch = branch;
            }
        }

        private async void OnCopyDiagnosticsClicked(object sender, RoutedEventArgs e)
        {
            if (DataContext is ViewModels.RepositoryConfigure vm && !string.IsNullOrEmpty(vm.SanitizedDiagnostics))
                await this.CopyTextAsync(vm.SanitizedDiagnostics);
            e.Handled = true;
        }

        private async void SelectConventionalTypesFile(object sender, RoutedEventArgs e)
        {
            var options = new FilePickerOpenOptions()
            {
                FileTypeFilter = [new FilePickerFileType("Conventional Commit Types") { Patterns = ["*.json"] }],
                AllowMultiple = false,
            };

            var selected = await StorageProvider.OpenFilePickerAsync(options);
            if (selected.Count == 1 && DataContext is ViewModels.RepositoryConfigure vm)
                vm.ConventionalTypesOverride = selected[0].Path.LocalPath;

            e.Handled = true;
        }

        private void OnNewCustomIssueTracker(object sender, RoutedEventArgs e)
        {
            if (DataContext is ViewModels.RepositoryConfigure vm)
                vm.AddIssueTracker("New Issue Tracker", @"#(\d+)", "https://xxx/$1");

            e.Handled = true;
        }

        private void OnAddGitHubIssueTracker(object sender, RoutedEventArgs e)
        {
            if (DataContext is ViewModels.RepositoryConfigure vm)
            {
                var link = "https://github.com/username/repository/issues/$1";
                var remotes = vm.GetRemoteVisitUrls();
                foreach (var remote in remotes)
                {
                    if (remote.Contains("github.com", StringComparison.Ordinal))
                    {
                        link = $"{remote}/issues/$1";
                        break;
                    }
                }

                vm.AddIssueTracker("GitHub Issue", @"#(\d+)", link);
            }

            e.Handled = true;
        }

        private void OnAddJiraIssueTracker(object sender, RoutedEventArgs e)
        {
            if (DataContext is ViewModels.RepositoryConfigure vm)
            {
                vm.AddIssueTracker(
                    "Jira Tracker",
                    @"PROJ-(\d+)",
                    "https://jira.yourcompany.com/browse/PROJ-$1");
            }

            e.Handled = true;
        }

        private void OnAddAzureWorkItemTracker(object sender, RoutedEventArgs e)
        {
            if (DataContext is ViewModels.RepositoryConfigure vm)
            {
                vm.AddIssueTracker(
                    "Azure DevOps Tracker",
                    @"#(\d+)",
                    "https://dev.azure.com/yourcompany/workspace/_workitems/edit/$1");
            }

            e.Handled = true;
        }

        private void OnAddGitLabIssueTracker(object sender, RoutedEventArgs e)
        {
            if (DataContext is ViewModels.RepositoryConfigure vm)
            {
                var link = "https://gitlab.com/username/repository/-/issues/$1";
                var remotes = vm.GetRemoteVisitUrls();
                foreach (var remote in remotes)
                {
                    link = $"{remote}/-/issues/$1";
                    break;
                }

                vm.AddIssueTracker("GitLab Issue", @"#(\d+)", link);
            }

            e.Handled = true;
        }

        private void OnAddGitLabMergeRequestTracker(object sender, RoutedEventArgs e)
        {
            if (DataContext is ViewModels.RepositoryConfigure vm)
            {
                var link = "https://gitlab.com/username/repository/-/merge_requests/$1";
                var remotes = vm.GetRemoteVisitUrls();
                foreach (var remote in remotes)
                {
                    link = $"{remote}/-/merge_requests/$1";
                    break;
                }

                vm.AddIssueTracker("GitLab MR", @"!(\d+)", link);
            }

            e.Handled = true;
        }

        private void OnAddGiteeIssueTracker(object sender, RoutedEventArgs e)
        {
            if (DataContext is ViewModels.RepositoryConfigure vm)
            {
                var link = "https://gitee.com/username/repository/issues/$1";
                var remotes = vm.GetRemoteVisitUrls();
                foreach (var remote in remotes)
                {
                    if (remote.Contains("gitee.com", StringComparison.Ordinal))
                    {
                        link = $"{remote}/issues/$1";
                        break;
                    }
                }

                vm.AddIssueTracker("Gitee Issue", @"#([0-9A-Z]{6,10})", link);
            }

            e.Handled = true;
        }

        private void OnAddGiteePullRequestTracker(object sender, RoutedEventArgs e)
        {
            if (DataContext is ViewModels.RepositoryConfigure vm)
            {
                var link = "https://gitee.com/username/repository/pulls/$1";
                var remotes = vm.GetRemoteVisitUrls();
                foreach (var remote in remotes)
                {
                    if (remote.Contains("gitee.com", StringComparison.Ordinal))
                    {
                        link = $"{remote}/pulls/$1";
                        break;
                    }
                }

                vm.AddIssueTracker("Gitee Pull Request", @"!(\d+)", link);
            }

            e.Handled = true;
        }

        private void OnAddGerritChangeIdTracker(object sender, RoutedEventArgs e)
        {
            if (DataContext is ViewModels.RepositoryConfigure vm)
            {
                vm.AddIssueTracker(
                    "Gerrit Change-Id",
                    @"(I[A-Za-z0-9]{40})",
                    "https://gerrit.yourcompany.com/q/$1");
            }

            e.Handled = true;
        }

        private void OnRemoveIssueTracker(object sender, RoutedEventArgs e)
        {
            if (DataContext is ViewModels.RepositoryConfigure vm)
                vm.RemoveIssueTracker();

            e.Handled = true;
        }
    }
}
