using Avalonia.Controls;
using Avalonia.Interactivity;

namespace SourceGit.Views
{
    public partial class RewriteRemoteHistory : UserControl
    {
        public RewriteRemoteHistory()
        {
            InitializeComponent();
        }

        private async void OnOpenRecoveryLogsClicked(object sender, RoutedEventArgs e)
        {
            if (DataContext is ViewModels.RewriteRemoteHistory vm)
            {
                await this.ShowDialogAsync(new ViewModels.ViewLogs(vm.Repo, focusRecoveryTab: true));
                e.Handled = true;
            }
        }
    }
}
