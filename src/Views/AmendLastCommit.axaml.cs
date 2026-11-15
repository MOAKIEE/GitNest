using Avalonia.Controls;
using Avalonia.Interactivity;

namespace SourceGit.Views
{
    public partial class AmendLastCommit : UserControl
    {
        public AmendLastCommit()
        {
            InitializeComponent();
        }

        private void OnRestoreMessageClicked(object sender, RoutedEventArgs e)
        {
            if (DataContext is ViewModels.AmendLastCommit vm)
                vm.RestoreOriginalMessage();
            e.Handled = true;
        }

        private void OnCreateNewCommitInsteadClicked(object sender, RoutedEventArgs e)
        {
            if (DataContext is ViewModels.AmendLastCommit vm)
                vm.SwitchToCreateNewCommit();
            e.Handled = true;
        }

        private async void OnFetchAndReinspectClicked(object sender, RoutedEventArgs e)
        {
            if (DataContext is ViewModels.AmendLastCommit vm)
                await vm.FetchAndReinspectAsync();
            e.Handled = true;
        }

        private async void OnOpenRecoveryLogsClicked(object sender, RoutedEventArgs e)
        {
            if (DataContext is ViewModels.AmendLastCommit vm)
                await this.ShowDialogAsync(new ViewModels.ViewLogs(vm.Repo, focusRecoveryTab: true));
            e.Handled = true;
        }
    }
}
