using Avalonia.Controls;
using Avalonia.Interactivity;

namespace SourceGit.Views
{
    public partial class Revert : UserControl
    {
        public Revert()
        {
            InitializeComponent();
        }

        private async void OnRefreshSnapshot(object sender, RoutedEventArgs e)
        {
            if (DataContext is ViewModels.Revert vm)
                await vm.RefreshPreflightAsync();

            e.Handled = true;
        }
    }
}
