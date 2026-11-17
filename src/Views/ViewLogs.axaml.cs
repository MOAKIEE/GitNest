using System;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace SourceGit.Views
{
    public partial class ViewLogs : ChromelessWindow
    {
        public ViewLogs()
        {
            CloseOnESC = true;
            InitializeComponent();

            var restoreGroup = $"GitNestBackupRestoreMode_{Guid.NewGuid():N}";
            RestoreModeNewBranchRadio.GroupName = restoreGroup;
            RestoreModeExistingBranchRadio.GroupName = restoreGroup;
        }

        protected override void OnClosed(EventArgs e)
        {
            DataContext = null;
            base.OnClosed(e);
        }

        protected override async void OnOpened(EventArgs e)
        {
            base.OnOpened(e);

            if (DataContext is ViewModels.ViewLogs vm)
            {
                if (vm.Logs.Count > 0)
                    vm.SelectedLog = vm.Logs[0];

                await vm.RefreshOperationsAsync(runReconciliation: false);
                await vm.RefreshDiscardBackupsAsync();
            }
        }

        private void OnSelectOperationsTab(object sender, RoutedEventArgs e)
        {
            if (DataContext is ViewModels.ViewLogs vm)
            {
                vm.SelectTab(0);
                e.Handled = true;
            }
        }

        private void OnSelectRawLogsTab(object sender, RoutedEventArgs e)
        {
            if (DataContext is ViewModels.ViewLogs vm)
            {
                vm.SelectTab(1);
                e.Handled = true;
            }
        }

        private void OnSelectDiscardBackupsTab(object sender, RoutedEventArgs e)
        {
            if (DataContext is ViewModels.ViewLogs vm)
            {
                vm.SelectTab(2);
                e.Handled = true;
            }
        }

        private async void OnRefreshDiscardBackups(object sender, RoutedEventArgs e)
        {
            if (DataContext is ViewModels.ViewLogs vm)
            {
                await vm.RefreshDiscardBackupsAsync();
                e.Handled = true;
            }
        }

        private async void OnPruneExpiredDiscardBackups(object sender, RoutedEventArgs e)
        {
            if (DataContext is ViewModels.ViewLogs vm)
            {
                await vm.PruneExpiredDiscardBackupsAsync();
                e.Handled = true;
            }
        }

        private async void OnRestoreSelectedDiscardBackup(object sender, RoutedEventArgs e)
        {
            if (DataContext is ViewModels.ViewLogs vm)
            {
                await vm.RestoreSelectedDiscardBackupAsync();
                e.Handled = true;
            }
        }

        private async void OnDeleteSelectedDiscardBackup(object sender, RoutedEventArgs e)
        {
            if (DataContext is ViewModels.ViewLogs vm)
            {
                await vm.DeleteSelectedDiscardBackupAsync();
                e.Handled = true;
            }
        }

        private async void OnReconcileAllOperations(object sender, RoutedEventArgs e)
        {
            if (DataContext is ViewModels.ViewLogs vm)
            {
                await vm.RefreshOperationsAsync(runReconciliation: true);
                e.Handled = true;
            }
        }

        private async void OnPreviewExpiredBackups(object sender, RoutedEventArgs e)
        {
            if (DataContext is ViewModels.ViewLogs vm)
            {
                await vm.PreviewExpiredBackupsAsync();
                e.Handled = true;
            }
        }

        private async void OnConfirmPruneExpiredBackups(object sender, RoutedEventArgs e)
        {
            if (DataContext is ViewModels.ViewLogs vm)
            {
                await vm.ConfirmPruneExpiredBackupsAsync();
                e.Handled = true;
            }
        }

        private void OnCancelPrunePreview(object sender, RoutedEventArgs e)
        {
            if (DataContext is ViewModels.ViewLogs vm)
            {
                vm.CancelPrunePreview();
                e.Handled = true;
            }
        }

        private async void OnVerifySelectedOperation(object sender, RoutedEventArgs e)
        {
            if (DataContext is ViewModels.ViewLogs vm)
            {
                await vm.VerifySelectedOperationAsync();
                e.Handled = true;
            }
        }

        private async void OnVerifyRemoteSelectedOperation(object sender, RoutedEventArgs e)
        {
            if (DataContext is ViewModels.ViewLogs vm)
            {
                await vm.VerifyRemoteSelectedOperationAsync();
                e.Handled = true;
            }
        }

        private async void OnAcknowledgeSelectedOperation(object sender, RoutedEventArgs e)
        {
            if (DataContext is ViewModels.ViewLogs vm)
            {
                await vm.AcknowledgeSelectedOperationAsync();
                e.Handled = true;
            }
        }

        private async void OnRestoreSelectedBackup(object sender, RoutedEventArgs e)
        {
            if (DataContext is ViewModels.ViewLogs vm)
            {
                await vm.RestoreSelectedBackupAsync();
                e.Handled = true;
            }
        }

        private async void OnCopySelectedDiagnostics(object sender, RoutedEventArgs e)
        {
            if (DataContext is ViewModels.ViewLogs vm && !string.IsNullOrEmpty(vm.SelectedOperationDiagnosticText))
            {
                await this.CopyTextAsync(vm.SelectedOperationDiagnosticText);
                e.Handled = true;
            }
        }

        private void OnDismissFeedback(object sender, RoutedEventArgs e)
        {
            if (DataContext is ViewModels.ViewLogs vm)
            {
                vm.DismissFeedback();
                e.Handled = true;
            }
        }

        private void OnClearAllLogs(object sender, RoutedEventArgs e)
        {
            if (DataContext is ViewModels.ViewLogs vm)
            {
                vm.ClearAll();
                e.Handled = true;
            }
        }

        private void OnLogContextRequested(object sender, ContextRequestedEventArgs e)
        {
            if (sender is not Grid { DataContext: ViewModels.CommandLog log } grid || DataContext is not ViewModels.ViewLogs vm)
                return;

            var copy = new MenuItem();
            copy.Header = App.Text("ViewLogs.CopyLog");
            copy.Icon = this.CreateMenuIcon("Icons.Copy");
            copy.Click += async (_, ev) =>
            {
                await this.CopyTextAsync(log.Content);
                ev.Handled = true;
            };

            var rm = new MenuItem();
            rm.Header = App.Text("ViewLogs.Delete");
            rm.Icon = this.CreateMenuIcon("Icons.Clear");
            rm.Click += (_, ev) =>
            {
                vm.Logs.Remove(log);
                ev.Handled = true;
            };

            var menu = new ContextMenu();
            menu.Items.Add(copy);
            menu.Items.Add(rm);
            menu.Open(grid);

            e.Handled = true;
        }

        private void OnLogKeyDown(object _, KeyEventArgs e)
        {
            if (e.Key is not (Key.Delete or Key.Back))
                return;

            if (DataContext is ViewModels.ViewLogs { SelectedLog: { } log } vm)
                vm.Logs.Remove(log);

            e.Handled = true;
        }
    }
}
