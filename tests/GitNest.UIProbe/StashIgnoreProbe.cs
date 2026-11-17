using SourceGit;
using SourceGit.Models;
using VM = SourceGit.ViewModels;

internal static partial class Program
{
    static partial void RunStashIgnore()
    {
        var path = CreateRepository("stash-ignore");
        File.AppendAllText(Path.Combine(path, "file.txt"), "stashed\n");
        File.WriteAllText(Path.Combine(path, "note.log"), "untracked\n");
        var (repo, page, window) = OpenRepository(path);
        var stash = new VM.StashChanges(repo, null) { Message = "probe stash", IncludeUntracked = true };
        page.Popup = stash;
        Await(() => !stash.IsLoadingPreflight && stash.HasPreflight && stash.CanConfirm);
        Check(stash.CandidateFiles.Any(f => f.Path == "note.log"), "Stash popup previews included untracked files");
        Screenshot(window, "stash-preview");
        ClickButtonByText(window, App.Text("Stash"));
        Await(() => page.Popup == null);
        repo.RefreshStashes();
        Await(() => repo.StashesPage?.Stashes.Count == 1);
        var saved = repo.StashesPage.Stashes[0];
        var apply = new VM.ApplyStash(repo, saved) { DropAfterApply = false };
        page.Popup = apply;
        Await(() => !apply.IsLoadingPreflight && apply.CanConfirm);
        ClickButtonByText(window, App.Text("ApplyStash.Confirm"));
        Await(() => page.Popup == null);
        Check(File.Exists(Path.Combine(path, "note.log")) && File.ReadAllText(Path.Combine(path, "file.txt")).Contains("stashed", StringComparison.Ordinal),
            "Apply Stash button restores the saved changes");
        page.Popup = new VM.DropStash(repo, saved);
        Pump();
        ClickButtonByText(window, App.Text("DropStash.Confirm"));
        Await(() => page.Popup == null);
        Check(string.IsNullOrWhiteSpace(Git(path, "stash", "list")), "Drop Stash button removes the selected stash");

        var ignore = new VM.AddToIgnore(repo, "note.log", isDirectory: false);
        page.Popup = ignore;
        Await(() => !ignore.IsLoadingInspection && ignore.Presets.Count >= 2);
        ignore.SelectedPreset = ignore.Presets.First(p => p.Kind == IgnoreRulePresetKind.ByExtension);
        ignore.SelectedTarget = ignore.SupportedTargets.First(t => t.Option.IsLocalOnly);
        Await(() => !ignore.IsLoadingInspection && ignore.IsLocalTargetSelected && ignore.CanConfirm);
        Screenshot(window, "ignore-local");
        ClickButtonByText(window, App.Text("AddToIgnore.Confirm"));
        Await(() => page.Popup == null);
        Check(File.ReadAllText(Path.Combine(path, ".git", "info", "exclude")).Contains("*.log", StringComparison.Ordinal),
            "AddToIgnore confirmation writes the selected local rule");
    }
}
