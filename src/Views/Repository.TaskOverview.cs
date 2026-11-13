using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

using Avalonia.Controls;
using Avalonia.Platform.Storage;

namespace SourceGit.Views
{
    public partial class Repository
    {
        private void AddWorktreeTaskMenuItems(ContextMenu menu, ViewModels.Repository repo, ViewModels.Worktree worktree)
        {
            var edit = new MenuItem() { Header = App.Text("Worktree.Task.Edit") };
            edit.Click += (_, e) =>
            {
                if (repo.CanCreatePopup())
                    repo.ShowPopup(new ViewModels.EditWorktreeTask(repo, worktree));
                e.Handled = true;
            };
            var refresh = new MenuItem() { Header = App.Text("Worktree.Task.Refresh") };
            refresh.Click += (_, e) => { repo.RefreshTaskOverview(); e.Handled = true; };
            menu.Items.Add(edit);
            menu.Items.Add(refresh);

            var exists = Directory.Exists(worktree.FullPath) && !worktree.Backend.IsBare;
            var explore = new MenuItem() { Header = App.Text("Repository.Explore"), IsEnabled = exists };
            explore.Click += (_, e) =>
            {
                TryOpenWorktreeTool(repo, () => Native.OS.OpenInFileManager(worktree.FullPath));
                e.Handled = true;
            };
            var terminal = new MenuItem() { Header = App.Text("Repository.Terminal"), IsEnabled = exists };
            terminal.Click += async (_, e) =>
            {
                if (!File.Exists(Native.OS.ShellOrTerminal))
                {
                    var path = await SelectWorktreeToolAsync("Repository.Terminal");
                    if (path == null)
                        return;
                    ViewModels.Preferences.Instance.ShellOrTerminalType = -1;
                    ViewModels.Preferences.Instance.ShellOrTerminalPath = path;
                    ViewModels.Preferences.Instance.ShellOrTerminalArgs = string.Empty;
                    ViewModels.Preferences.Instance.Save();
                }
                TryOpenWorktreeTool(repo, () => Native.OS.OpenTerminal(worktree.FullPath));
                e.Handled = true;
            };
            var editors = new MenuItem() { Header = App.Text("Worktree.Task.OpenEditor"), IsEnabled = exists };
            foreach (var tool in Native.OS.ExternalTools.Where(t => t.SupportOpenFolder && File.Exists(t.ExecFile)))
            {
                var item = new MenuItem() { Header = tool.Name };
                item.Click += (_, e) =>
                {
                    TryOpenWorktreeTool(repo, () => tool.Launch(worktree.FullPath.Quoted(), worktree.FullPath));
                    e.Handled = true;
                };
                editors.Items.Add(item);
            }
            var customPath = ViewModels.Preferences.Instance.WorktreeEditorPath;
            if (File.Exists(customPath))
            {
                var custom = new MenuItem() { Header = Path.GetFileNameWithoutExtension(customPath) };
                custom.Click += (_, e) =>
                {
                    TryOpenWorktreeTool(repo, () => Process.Start(Models.WorktreeToolLauncher.CreateEditorStartInfo(customPath, worktree.FullPath)));
                    e.Handled = true;
                };
                editors.Items.Add(custom);
            }
            var choose = new MenuItem() { Header = App.Text("Worktree.Task.SelectEditor") };
            choose.Click += async (_, e) =>
            {
                var path = await SelectWorktreeToolAsync("Worktree.Task.SelectEditor");
                if (path != null)
                {
                    ViewModels.Preferences.Instance.WorktreeEditorPath = path;
                    ViewModels.Preferences.Instance.Save();
                    TryOpenWorktreeTool(repo, () => Process.Start(Models.WorktreeToolLauncher.CreateEditorStartInfo(path, worktree.FullPath)));
                }
                e.Handled = true;
            };
            editors.Items.Add(choose);
            menu.Items.Add(explore);
            menu.Items.Add(editors);
            menu.Items.Add(terminal);
            menu.Items.Add(new MenuItem() { Header = "-" });
        }

        private async Task<string> SelectWorktreeToolAsync(string title)
        {
            var topLevel = TopLevel.GetTopLevel(this);
            if (topLevel == null)
                return null;
            var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions()
            {
                Title = App.Text(title),
                AllowMultiple = false,
                FileTypeFilter = [new FilePickerFileType("Executable") { Patterns = OperatingSystem.IsWindows() ? ["*.exe"] : ["*"] }],
            });
            return files.Count == 1 ? files[0].TryGetLocalPath() : null;
        }

        private static void TryOpenWorktreeTool(ViewModels.Repository repo, Action open)
        {
            try { open(); }
            catch (Exception ex) { repo.SendNotification(App.Text("Worktree.Task.OpenFailed", ex.Message), true); }
        }
    }
}
