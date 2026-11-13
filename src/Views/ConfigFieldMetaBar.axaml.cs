using Avalonia.Controls;
using Avalonia.Interactivity;

namespace SourceGit.Views
{
    public partial class ConfigFieldMetaBar : UserControl
    {
        public ConfigFieldMetaBar()
        {
            InitializeComponent();
        }

        private void OnOverrideHereClicked(object sender, RoutedEventArgs e)
        {
            if (DataContext is ViewModels.ConfigFieldItem field)
                field.OverrideHere();
            e.Handled = true;
        }

        private void OnUnsetLayerClicked(object sender, RoutedEventArgs e)
        {
            if (DataContext is ViewModels.ConfigFieldItem field)
                field.MarkUnsetLayer();
            e.Handled = true;
        }

        private void OnSetEmptyClicked(object sender, RoutedEventArgs e)
        {
            if (DataContext is ViewModels.ConfigFieldItem field)
                field.MarkExplicitEmpty();
            e.Handled = true;
        }

        private void OnRevertClicked(object sender, RoutedEventArgs e)
        {
            if (DataContext is ViewModels.ConfigFieldItem field)
                field.Revert();
            e.Handled = true;
        }
    }
}
