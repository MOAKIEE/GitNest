using Avalonia.Controls;
using Avalonia.Interactivity;

namespace SourceGit.Views
{
    public partial class Merge : UserControl
    {
        public Merge()
        {
            InitializeComponent();
        }

        private async void OnRefreshSnapshot(object sender, RoutedEventArgs e)
        {
            if (DataContext is ViewModels.Merge vm)
                await vm.RefreshPreflightAsync();

            e.Handled = true;
        }
    }
}
