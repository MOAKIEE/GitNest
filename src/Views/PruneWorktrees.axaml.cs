using Avalonia.Controls;
using Avalonia.Interactivity;

namespace SourceGit.Views
{
    public partial class PruneWorktrees : UserControl
    {
        public PruneWorktrees()
        {
            InitializeComponent();
        }

        private async void OnRefreshPreview(object sender, RoutedEventArgs e)
        {
            if (DataContext is ViewModels.PruneWorktrees vm)
                await vm.RefreshPreviewAsync();

            e.Handled = true;
        }
    }
}
