using System;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;

namespace SourceGit.Views
{
    public partial class GitSetup : UserControl
    {
        public GitSetup()
        {
            InitializeComponent();
        }

        protected override async void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);
            if (DataContext is ViewModels.GitSetup setup)
                await setup.RefreshIdentityAsync();
        }

        private async void OnDetect(object sender, RoutedEventArgs e)
        {
            if (DataContext is ViewModels.GitSetup setup)
                await setup.DetectAsync();
            e.Handled = true;
        }

        private async void OnSelect(object sender, RoutedEventArgs e)
        {
            if (TopLevel.GetTopLevel(this) is not { } top || DataContext is not ViewModels.GitSetup setup)
                return;

            try
            {
                var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
                {
                    Title = App.Text("GitSetup.Select"),
                    AllowMultiple = false,
                    FileTypeFilter = [new FilePickerFileType("Git") { Patterns = [OperatingSystem.IsWindows() ? "git.exe" : "git"] }],
                });
                if (files.Count == 1 && files[0].TryGetLocalPath() is { } path)
                    await setup.SelectAsync(path);
            }
            catch (Exception)
            {
                Models.Notification.Send(null, App.Text("GitSetup.SelectFailed"), true);
            }
            e.Handled = true;
        }

        private void OnDownload(object sender, RoutedEventArgs e)
        {
            Native.OS.OpenBrowser(OperatingSystem.IsWindows() ? "https://gitforwindows.org/" : "https://git-scm.com/downloads");
            e.Handled = true;
        }
    }
}
