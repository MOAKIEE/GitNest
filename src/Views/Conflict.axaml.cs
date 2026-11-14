using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace SourceGit.Views
{
    public partial class Conflict : UserControl
    {
        public Conflict()
        {
            InitializeComponent();
        }

        private async void OnRefreshStatus(object sender, RoutedEventArgs e)
        {
            if (DataContext is ViewModels.Conflict vm)
                await vm.RefreshStatusAsync();

            e.Handled = true;
        }

        private void OnPressedSHA(object sender, PointerPressedEventArgs e)
        {
            if (DataContext is ViewModels.Conflict vm && sender is TextBlock block && block.Text is { } sha)
                vm.NavigateTo(sha);

            e.Handled = true;
        }
    }
}
