using Avalonia.Controls;
using Avalonia.VisualTree;
using SourceGit;
using SourceGit.Models;
using VM = SourceGit.ViewModels;

internal static partial class Program
{
    static partial void RunWorktreeDelete()
    {
        var path = CreateRepository("worktree-delete");
        CommitFile(path, ".gitignore", "*.ignored\n", "ignore artifacts");
        var dirtyPath = Path.Combine(_root, "wt-dirty");
        var lockedPath = Path.Combine(_root, "wt-locked");
        var missingPath = Path.Combine(_root, "wt-missing");
        Git(path, "worktree", "add", "-b", "feat/dirty", dirtyPath);
        Git(path, "worktree", "add", "-b", "feat/locked", lockedPath);
        Git(path, "worktree", "lock", "--reason", "probe lock", lockedPath);
        Git(path, "worktree", "add", "-b", "feat/missing", missingPath);
        Directory.Move(missingPath, missingPath + "-moved");
        File.AppendAllText(Path.Combine(dirtyPath, "file.txt"), "staged\n");
        Git(dirtyPath, "add", "file.txt");
        File.AppendAllText(Path.Combine(dirtyPath, "file.txt"), "unstaged\n");
        File.WriteAllText(Path.Combine(dirtyPath, "new.txt"), "untracked\n");
        File.WriteAllText(Path.Combine(dirtyPath, "local.ignored"), "ignored\n");
        var (repo, page, window) = OpenRepository(path);
        Await(() => repo.Worktrees.Count == 4);
        var main = new VM.RemoveWorktree(repo, repo.Worktrees.First(w => w.IsMain));
        page.Popup = main;
        Await(() => !main.IsLoadingPreflight);
        Check(!main.CanConfirm && !main.CanShowForceOption && window.GetVisualDescendants().OfType<Button>()
                .Any(b => Equals(ToolTip.GetTip(b), App.Text("PruneWorktrees"))),
            "Main worktree is blocked and the repository exposes Prune preview");
        ClickButtonByText(window, App.Text("Cancel"));
        Await(() => page.Popup == null);

        _launcher.AddNewTab();
        var dirtyPage = _launcher.ActivePage;
        _launcher.OpenRepositoryInTab(dirtyPath, dirtyPage);
        Await(() => dirtyPage.Data is VM.Repository r && r.Worktrees.Any(w => w.IsCurrent));
        var dirtyRepo = (VM.Repository)dirtyPage.Data;
        var dirty = new VM.RemoveWorktree(dirtyRepo, dirtyRepo.Worktrees.First(w => w.IsCurrent));
        dirtyPage.Popup = dirty;
        Await(() => !dirty.IsLoadingPreflight);
        Check(dirty.IsTargetOpenInGitNest && !dirty.CanConfirm && dirty.CanShowForceOption &&
              dirty.StagedCount == 1 && dirty.UnstagedCount == 1 && dirty.UntrackedCount == 1 && dirty.IgnoredCount == 1,
            "Open dirty worktree renders separate change counts and requires force confirmation");
        dirty.Force = true;
        Await(() => dirty.ShowForceLossWarning && dirty.CanConfirm);
        Check(dirty.ConfirmButtonText == App.Text("RemoveWorktree.BtnForceDelete"), "Force opt-in changes the confirmation action");
        Screenshot(window, "worktree-force-confirm");
        ClickButtonByText(window, App.Text("RemoveWorktree.BtnForceDelete"));
        Await(() => !Directory.Exists(dirtyPath) && _launcher.ActivePage == page && page.Popup == null);
        Check(Git(path, "branch", "--list", "feat/dirty").Length > 0,
            "Deleting an open worktree switches to the main tab and preserves its branch");

        var locked = new VM.RemoveWorktree(repo, repo.Worktrees.First(w => WorktreeTaskStore.PathsEqual(w.FullPath, lockedPath)));
        page.Popup = locked;
        Await(() => !locked.IsLoadingPreflight);
        Check(locked.IsLocked && !locked.CanConfirm && !locked.CanShowForceOption && locked.LockReasonDisplay.Contains("probe lock", StringComparison.Ordinal),
            "Locked worktree renders its lock reason and disables removal");
        locked.UnlockAcknowledged = true;
        Pump();
        ClickButtonByText(window, App.Text("RemoveWorktree.UnlockAndReinspectBtn"));
        Await(() => !locked.IsLocked && locked.CanConfirm);
        Check(locked.CanConfirm, "Unlock and reinspect enables safe removal");
        ClickButtonByText(window, App.Text("Cancel"));
        Await(() => page.Popup == null);

        var missing = new VM.RemoveWorktree(repo, repo.Worktrees.First(w => WorktreeTaskStore.PathsEqual(w.FullPath, missingPath)));
        page.Popup = missing;
        Await(() => !missing.IsLoadingPreflight);
        ClickButtonByText(window, App.Text("RemoveWorktree.OpenPrunePreviewBtn"));
        Await(() => page.Popup is VM.PruneWorktrees p && !p.IsLoadingPreview);
        var prune = (VM.PruneWorktrees)page.Popup;
        Check(prune.Candidates.Count == 1 && prune.CanConfirm, "Missing worktree opens the Prune candidate preview");
        Screenshot(window, "worktree-prune-preview");
        ClickButtonByText(window, App.Text("PruneWorktrees.ConfirmBtn"));
        Await(() => page.Popup == null);
        Check(!Git(path, "worktree", "list", "--porcelain").Contains(missingPath.Replace('\\', '/'), StringComparison.Ordinal),
            "Prune confirmation removes the missing worktree registration");
    }
}
