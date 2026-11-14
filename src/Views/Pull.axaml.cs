using Avalonia.Controls;
using Avalonia.Interactivity;

namespace SourceGit.Views
{
    public partial class Pull : UserControl
    {
        public Pull()
        {
            InitializeComponent();
        }

        private void OnOpenMergeFlow(object _, RoutedEventArgs e)
        {
            if (DataContext is ViewModels.Pull vm)
                vm.OpenMergeFlow();

            e.Handled = true;
        }

        private void OnCloseForExternalTool(object _, RoutedEventArgs e)
        {
            if (DataContext is ViewModels.Pull vm)
                vm.CloseForExternalTool();

            e.Handled = true;
        }
    }
}
