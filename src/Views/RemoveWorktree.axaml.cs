using Avalonia.Controls;
using Avalonia.Interactivity;

namespace SourceGit.Views
{
    public partial class RemoveWorktree : UserControl
    {
        public RemoveWorktree()
        {
            InitializeComponent();
        }

        private async void OnRefreshPreflight(object sender, RoutedEventArgs e)
        {
            if (DataContext is ViewModels.RemoveWorktree vm)
                await vm.RefreshPreflightAsync();

            e.Handled = true;
        }

        private async void OnRefreshRemoteProbe(object sender, RoutedEventArgs e)
        {
            if (DataContext is ViewModels.RemoveWorktree vm)
                await vm.RefreshRemoteProbeAsync();

            e.Handled = true;
        }

        private async void OnUnlockAndReinspect(object sender, RoutedEventArgs e)
        {
            if (DataContext is ViewModels.RemoveWorktree vm)
                await vm.UnlockAndReinspectAsync();

            e.Handled = true;
        }

        private void OnOpenPrunePreview(object sender, RoutedEventArgs e)
        {
            if (DataContext is ViewModels.RemoveWorktree vm)
                vm.OpenPrunePreviewDialog();

            e.Handled = true;
        }

        private async void OnForceDeleteRemainingBranch(object sender, RoutedEventArgs e)
        {
            if (DataContext is ViewModels.RemoveWorktree vm)
                await vm.ForceDeleteRemainingLocalBranchAsync();

            e.Handled = true;
        }

        private async void OnRestoreBackupBranch(object sender, RoutedEventArgs e)
        {
            if (DataContext is ViewModels.RemoveWorktree vm)
                await vm.RestoreBackupBranchAsync();

            e.Handled = true;
        }
    }
}
