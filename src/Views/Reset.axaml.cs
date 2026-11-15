using Avalonia.Controls;
using Avalonia.Interactivity;

namespace SourceGit.Views
{
    public partial class Reset : UserControl
    {
        public Reset()
        {
            InitializeComponent();
        }

        private async void OnOpenRecoveryLogsClicked(object sender, RoutedEventArgs e)
        {
            if (DataContext is ViewModels.Reset vm)
            {
                await this.ShowDialogAsync(new ViewModels.ViewLogs(vm.Repo, focusRecoveryTab: true));
                e.Handled = true;
            }
        }

        private async void OnRefreshFromResultClicked(object sender, RoutedEventArgs e)
        {
            if (DataContext is ViewModels.Reset vm)
            {
                await vm.RefreshFromResultAsync();
                e.Handled = true;
            }
        }
    }
}
