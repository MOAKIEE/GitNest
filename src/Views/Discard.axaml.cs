using Avalonia.Controls;
using Avalonia.Interactivity;

namespace SourceGit.Views
{
    public partial class Discard : UserControl
    {
        public Discard()
        {
            InitializeComponent();
        }

        private async void OnOpenDiscardBackups(object _1, RoutedEventArgs e)
        {
            if (DataContext is ViewModels.Discard vm)
            {
                await this.ShowDialogAsync(new ViewModels.ViewLogs(vm.Repo, focusDiscardBackupsTab: true));
                e.Handled = true;
            }
        }
    }
}
