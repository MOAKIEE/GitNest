using Avalonia.Controls;
using Avalonia.Interactivity;

namespace SourceGit.Views
{
    public partial class ResetWithoutCheckout : UserControl
    {
        public ResetWithoutCheckout()
        {
            InitializeComponent();
        }

        private async void OnOpenRecoveryLogsClicked(object sender, RoutedEventArgs e)
        {
            if (DataContext is ViewModels.ResetWithoutCheckout vm)
            {
                await this.ShowDialogAsync(new ViewModels.ViewLogs(vm.Repo, focusRecoveryTab: true));
                e.Handled = true;
            }
        }
    }
}
