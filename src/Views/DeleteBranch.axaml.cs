using System;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Interactivity;
using Avalonia.Media;

namespace SourceGit.Views
{
    public partial class DeleteBranch : UserControl
    {
        public static readonly IValueConverter StepIconConverter =
            new FuncValueConverter<string, StreamGeometry>(key =>
            {
                if (!string.IsNullOrEmpty(key) &&
                    Application.Current != null &&
                    Application.Current.TryFindResource(key, out var res) &&
                    res is StreamGeometry geo)
                {
                    return geo;
                }

                return null;
            });

        public DeleteBranch()
        {
            InitializeComponent();
        }

        private async void OnRefreshRemoteProbe(object sender, RoutedEventArgs e)
        {
            e.Handled = true;
            if (DataContext is ViewModels.DeleteBranch vm)
                await vm.RefreshRemoteProbeAsync();
        }

        private async void OnRestoreLocalFromBackup(object sender, RoutedEventArgs e)
        {
            e.Handled = true;
            if (DataContext is ViewModels.DeleteBranch vm)
                await vm.RestoreLocalBranchFromBackupAsync();
        }
    }
}
