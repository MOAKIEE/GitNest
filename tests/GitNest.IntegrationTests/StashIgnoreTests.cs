using SourceGit.Commands;
using SourceGit.Models;
using StashModel = SourceGit.Models.Stash;

internal static partial class Program
{
    private static async Task RunStashIgnoreTestsAsync(string realGit, string suiteRoot)
    {
        var phaseRoot = Path.Combine(suiteRoot, "stash-ignore-suite");
        Directory.CreateDirectory(phaseRoot);
        var journal = new OperationJournalStore(Path.Combine(phaseRoot, "journal.json"));

        // 1. Unborn HEAD blocks stash push
        var unbornRepo = Path.Combine(phaseRoot, "unborn-repo");
        await InitRepoAsync(realGit, unbornRepo, "main", "Alice", "alice@example.invalid");
        await File.WriteAllTextAsync(Path.Combine(unbornRepo, "draft.txt"), "unborn draft\n", Utf8NoBom);
        await GitInAsync(realGit, unbornRepo, "add", "draft.txt");

        var unbornStashPre = await StashWorkflowService.InspectPushAsync(unbornRepo);
        var unbornStashExec = await StashWorkflowService.ExecutePushAsync(unbornRepo, "should block", journalStore: journal);
        Check(!unbornStashPre.CanProceed && unbornStashPre.IsUnbornHead &&
              !unbornStashExec.Success && !string.IsNullOrEmpty(unbornStashExec.Message),
              "Unborn HEAD repo blocks stash push without corrupting index");

        // 2. Main repo setup + Stash model prefix parsing
        var repo = Path.Combine(phaseRoot, "main-repo");
        await InitRepoAsync(realGit, repo, "main", "Alice", "alice@example.invalid");

        var docRel = "docs [1]/中文 说明.txt";
        var baseRel = "app.txt";
        var mergeRel = "shared.txt";
        Directory.CreateDirectory(Path.Combine(repo, "docs [1]"));
        await File.WriteAllTextAsync(Path.Combine(repo, ".gitignore"), "*.ignored\n", Utf8NoBom);
        await File.WriteAllTextAsync(Path.Combine(repo, docRel), "doc-v1\n", Utf8NoBom);
        await File.WriteAllTextAsync(Path.Combine(repo, baseRel), "line1\nline2\nline3\nline4\n", Utf8NoBom);
        await File.WriteAllTextAsync(Path.Combine(repo, mergeRel), "base-alpha\n", Utf8NoBom);
        await GitInAsync(realGit, repo, "add", ".gitignore", docRel, baseRel, mergeRel);
        await GitInAsync(realGit, repo, "commit", "-m", "initial commit");
        var headMainSha = (await GitInAsync(realGit, repo, "rev-parse", "HEAD")).Trim();

        var sWip = new StashModel { Name = "stash@{0}", SHA = headMainSha, Message = "WIP on feat/ui: abcdef1 fix" };
        var sOn = new StashModel { Name = "stash@{1}", SHA = headMainSha, Message = "On main: custom msg" };
        var sExt = new StashModel { Name = "stash@{2}", SHA = headMainSha, Message = "external tool raw commit title" };
        Check(sWip.SourceBranch == "feat/ui" && sWip.HasStandardPrefix &&
              sOn.SourceBranch == "main" && sOn.DisplaySubject == "custom msg" &&
              string.IsNullOrEmpty(sExt.SourceBranch) && !sExt.HasStandardPrefix && sExt.DisplaySubject == "external tool raw commit title",
              "Stash model parses WIP/On prefixes and degrades gracefully for external raw titles");

        // 3. Stash push with special characters, untracked inclusion, ignored file exclusion
        await File.WriteAllTextAsync(Path.Combine(repo, docRel), "doc-v2-edited\n", Utf8NoBom);
        await File.WriteAllTextAsync(Path.Combine(repo, "untracked 中文.txt"), "new-untracked\n", Utf8NoBom);
        await File.WriteAllTextAsync(Path.Combine(repo, "secret.ignored"), "keep-ignored\n", Utf8NoBom);

        var localChanges = await new QueryLocalChanges(repo).GetResultAsync();
        var docChange = localChanges.Single(c => c.Path == docRel);
        var untrackedChange = localChanges.Single(c => c.Path == "untracked 中文.txt");

        var pushPre = await StashWorkflowService.InspectPushAsync(repo, [docChange, untrackedChange], includeUntracked: true, stagedOnly: false);
        Check(pushPre.CanProceed && pushPre.IncludedCount == 2 && pushPre.UntrackedIncludedCount == 1 && pushPre.IgnoredFileCount == 1,
              "Stash push preflight counts included tracked/untracked files and warns about excluded ignored files");

        var pushRes = await StashWorkflowService.ExecutePushAsync(
            repo, "partial special chars", [docChange, untrackedChange], includeUntracked: true, journalStore: journal);
        var specialStashSha = pushRes.CreatedStashSha;
        Check(pushRes.Success && !string.IsNullOrEmpty(specialStashSha) &&
              await File.ReadAllTextAsync(Path.Combine(repo, docRel), Utf8NoBom) == "doc-v1\n" &&
              !File.Exists(Path.Combine(repo, "untracked 中文.txt")) &&
              File.Exists(Path.Combine(repo, "secret.ignored")),
              "Stash push uses literal NUL pathspecs on special filenames and leaves ignored files untouched");

        // 4. Stash Apply collision checks (dirty, untracked, ignored) + index shift by SHA
        await File.WriteAllTextAsync(Path.Combine(repo, baseRel), "dirty-local\n", Utf8NoBom);
        var dirtyApplyPre = await StashWorkflowService.InspectApplyAsync(repo, new StashModel { Name = "stash@{0}", SHA = specialStashSha });
        var dirtyApplyRes = await StashWorkflowService.ExecuteApplyAsync(repo, "stash@{0}", specialStashSha, journalStore: journal);
        Check(!dirtyApplyPre.CanProceed && !dirtyApplyPre.IsWorkingTreeClean &&
              dirtyApplyRes.Status == StashApplyStatus.Blocked && dirtyApplyRes.StashRetained,
              "Stash apply blocks dirty working tree without applying or dropping stash");
        await GitInAsync(realGit, repo, "checkout", "--", baseRel);

        await File.WriteAllTextAsync(Path.Combine(repo, "untracked 中文.txt"), "newer-untracked-collision\n", Utf8NoBom);
        var untrackedColPre = await StashWorkflowService.InspectApplyAsync(repo, new StashModel { Name = "stash@{0}", SHA = specialStashSha });
        Check(!untrackedColPre.CanProceed && untrackedColPre.CollidingUntrackedPaths.Contains("untracked 中文.txt"),
              "Stash apply preflight catches untracked path collision against stash^3");
        File.Delete(Path.Combine(repo, "untracked 中文.txt"));

        await File.WriteAllTextAsync(Path.Combine(repo, ".gitignore"), "*.ignored\ndocs \\[1\\]/中文 说明.txt\n", Utf8NoBom);
        await GitInAsync(realGit, repo, "add", ".gitignore");
        await GitInAsync(realGit, repo, "commit", "-m", "ignore doc");
        await GitInAsync(realGit, repo, "rm", "--cached", docRel);
        await GitInAsync(realGit, repo, "commit", "-m", "untrack doc");
        await File.WriteAllTextAsync(Path.Combine(repo, docRel), "ignored-file-on-disk\n", Utf8NoBom);
        var ignoredColPre = await StashWorkflowService.InspectApplyAsync(repo, new StashModel { Name = "stash@{0}", SHA = specialStashSha });
        Check(!ignoredColPre.CanProceed && ignoredColPre.CollidingIgnoredPaths.Contains(docRel),
              "Stash apply preflight catches ignored path collision so apply will not silently fail or overwrite");
        await GitInAsync(realGit, repo, "reset", "--hard", headMainSha);
        File.Delete(Path.Combine(repo, "secret.ignored"));

        await File.WriteAllTextAsync(Path.Combine(repo, baseRel), "newer-unrelated-change\n", Utf8NoBom);
        var newerPush = await StashWorkflowService.ExecutePushAsync(repo, "newer shift stash", journalStore: journal);
        var shiftedApply = await StashWorkflowService.ExecuteApplyAsync(
            repo, "stash@{0}", specialStashSha, restoreIndex: true, dropAfterApply: true, journalStore: journal);
        var stashesAfterShiftDrop = await new QueryStashes(repo).GetResultAsync();
        Check(newerPush.Success && shiftedApply.Success && shiftedApply.StashSha == specialStashSha && shiftedApply.DropSucceeded &&
              await File.ReadAllTextAsync(Path.Combine(repo, docRel), Utf8NoBom) == "doc-v2-edited\n" &&
              File.Exists(Path.Combine(repo, "untracked 中文.txt")) &&
              stashesAfterShiftDrop.Count == 1 && stashesAfterShiftDrop[0].SHA == newerPush.CreatedStashSha,
              "ExecuteApplyAsync resolves shifted stash index by exact SHA, applies, and drops only the applied stash");

        await GitInAsync(realGit, repo, "checkout", "--", docRel);
        File.Delete(Path.Combine(repo, "untracked 中文.txt"));

        // 5. Stash apply conflict retains stash & is classified by InspectRepositoryInProgressAsync
        await File.WriteAllTextAsync(Path.Combine(repo, mergeRel), "stash-side-line\n", Utf8NoBom);
        var conflictStashRes = await StashWorkflowService.ExecutePushAsync(repo, "conflicting stash", journalStore: journal);
        var conflictHead = await CommitFileAsync(realGit, repo, mergeRel, "committed-branch-line\n", "diverge shared.txt");

        var conflictApply = await StashWorkflowService.ExecuteApplyAsync(
            repo, conflictStashRes.CreatedStashName, conflictStashRes.CreatedStashSha,
            restoreIndex: true, dropAfterApply: true, journalStore: journal);
        var stashesAfterConflict = await new QueryStashes(repo).GetResultAsync();
        var conflictInProgress = await MergeRevertService.InspectRepositoryInProgressAsync(repo, journal);
        Check(conflictStashRes.Success &&
              conflictApply.Status == StashApplyStatus.AppliedWithConflictsRetained &&
              !conflictApply.Success && conflictApply.StashRetained && !conflictApply.DropSucceeded &&
              conflictApply.ConflictedPaths.Contains(mergeRel) &&
              stashesAfterConflict.Any(s => s.SHA == conflictStashRes.CreatedStashSha) &&
              conflictInProgress.ConflictedPaths.Contains(mergeRel) &&
              conflictInProgress.Origin == ConflictOwnershipOrigin.GitNestOwnedWithExternalIntervention &&
              !conflictInProgress.CanOfferSafeAbort,
              "Conflicting stash apply retains stash, refuses drop, and reports read-only in-progress state");

        await GitInAsync(realGit, repo, "checkout", "--ours", mergeRel);
        await GitInAsync(realGit, repo, "add", mergeRel);
        await GitInAsync(realGit, repo, "reset", "--hard", conflictHead);

        // 6. Branch Switch Safety: blocks Discard; StashAndReapply preserves untracked & retains on conflict
        await GitInAsync(realGit, repo, "branch", "feat/switch-clean", headMainSha);
        await File.WriteAllTextAsync(Path.Combine(repo, baseRel), "local-edit-before-switch\n", Utf8NoBom);
        await File.WriteAllTextAsync(Path.Combine(repo, "untracked-switch.txt"), "carry-me\n", Utf8NoBom);

        var refusedSwitch = await StashWorkflowService.SwitchSafelyAsync(
            repo, ["checkout", "feat/switch-clean"], DealWithLocalChanges.Discard, "feat/switch-clean", journal);
        Check(!refusedSwitch.Success && (await GitInAsync(realGit, repo, "branch", "--show-current")).Trim() == "main",
              "Branch switch refuses DealWithLocalChanges.Discard so local work is never auto-discarded");

        var cleanSwitch = await StashWorkflowService.SwitchSafelyAsync(
            repo, ["checkout", "feat/switch-clean"], DealWithLocalChanges.StashAndReapply, "feat/switch-clean", journal);
        Check(cleanSwitch.Success && cleanSwitch.CheckoutSucceeded && cleanSwitch.ReapplySucceeded && !cleanSwitch.StashRetained &&
              (await GitInAsync(realGit, repo, "branch", "--show-current")).Trim() == "feat/switch-clean" &&
              await File.ReadAllTextAsync(Path.Combine(repo, baseRel), Utf8NoBom) == "local-edit-before-switch\n" &&
              File.Exists(Path.Combine(repo, "untracked-switch.txt")),
              "Branch switch StashAndReapply preserves untracked + tracked changes cleanly and drops temp stash");

        File.Delete(Path.Combine(repo, "untracked-switch.txt"));
        await GitInAsync(realGit, repo, "checkout", "--", baseRel);
        await File.WriteAllTextAsync(Path.Combine(repo, mergeRel), "switch-conflict-local\n", Utf8NoBom);
        var conflictSwitch = await StashWorkflowService.SwitchSafelyAsync(
            repo, ["checkout", "main"], DealWithLocalChanges.StashAndReapply, "main", journal);
        Check(!conflictSwitch.Success && conflictSwitch.CheckoutSucceeded && conflictSwitch.HasConflicts &&
              conflictSwitch.StashRetained && !string.IsNullOrEmpty(conflictSwitch.StashSha),
              "Branch switch preserves temporary stash when reapply hits conflicts on target branch");

        await GitInAsync(realGit, repo, "reset", "--hard", "HEAD");
        await GitInAsync(realGit, repo, "stash", "clear");

        // 7. Ignore rules (.gitignore + linked worktree info/exclude + escaping + dedupe + negation + tracked warn)
        var wtPath = Path.Combine(phaseRoot, "linked-wt");
        await GitInAsync(realGit, repo, "worktree", "add", wtPath, "-b", "wt/ignore-test");
        Directory.CreateDirectory(Path.Combine(wtPath, "dir [1]"));
        var specialIgnRel = "dir [1]/#draft!.tmp";
        await File.WriteAllTextAsync(Path.Combine(wtPath, specialIgnRel), "temp draft", Utf8NoBom);
        await File.WriteAllTextAsync(Path.Combine(wtPath, "already-tracked-check.tmp"), "untracked", Utf8NoBom);

        var targets = await IgnoreRuleService.GetSupportedTargetsAsync(wtPath, specialIgnRel);
        var rootTarget = targets.Single(t => t.Kind == IgnoreRuleTargetKind.RootGitIgnore);
        var subTarget = targets.Single(t => t.Kind == IgnoreRuleTargetKind.ParentGitIgnore);
        var localTarget = targets.Single(t => t.Kind == IgnoreRuleTargetKind.LocalExclude);

        var exactPreset = IgnoreRuleService.BuildPresetOptions(specialIgnRel, isDirectory: false, rootTarget.BaseRelativeDir)
            .Single(p => p.Kind == IgnoreRulePresetKind.ExactFile);
        Check(exactPreset.Pattern.Contains(@"\[1\]", StringComparison.Ordinal),
              "Ignore rule preset escapes bracket glob metacharacters cleanly");

        var wtExactApply = await IgnoreRuleService.ApplyAsync(wtPath, exactPreset.Pattern, localTarget, journal);
        var wtStatus = await GitInAsync(realGit, wtPath, "-c", "core.quotepath=off", "status", "-uall", "--porcelain", "--ignored");
        Check(wtExactApply.Success && wtExactApply.Inspection.IsLocalOnly &&
              wtStatus.Contains("dir [1]/#draft!.tmp", StringComparison.Ordinal) &&
              wtStatus.Contains("!! ", StringComparison.Ordinal),
              "Local info/exclude resolved in linked worktree ignores special filename under real Git");

        await File.WriteAllTextAsync(rootTarget.FullPath, "# keep comment\n*.ignored\n!important.txt\n", Utf8NoBom);
        var dupApply = await IgnoreRuleService.ApplyAsync(wtPath, "*.ignored", rootTarget, journal);
        Check(dupApply.Success && dupApply.SkippedDuplicate &&
              (await File.ReadAllTextAsync(rootTarget.FullPath, Utf8NoBom)).Split('\n').Count(l => l.Trim() == "*.ignored") == 1,
              "Ignore rule service preserves comments and deduplicates existing rule");

        var negInspect = await IgnoreRuleService.InspectAsync(wtPath, "*.txt", rootTarget);
        var appStillTracked = await GitInAsync(realGit, wtPath, "ls-files", "--", "app.txt");
        Check(negInspect.NegationConflicts.Contains("!important.txt") &&
              negInspect.MatchedTrackedPaths.Contains("app.txt") &&
              !string.IsNullOrWhiteSpace(appStillTracked),
              "Ignore inspection warns on conflicting negation and matched tracked files without untracking them");

        var subExt = IgnoreRuleService.BuildPresetOptions(specialIgnRel, isDirectory: false, subTarget.BaseRelativeDir)
            .Single(p => p.Kind == IgnoreRulePresetKind.ByExtension);
        var subApply = await IgnoreRuleService.ApplyAsync(wtPath, subExt.Pattern, subTarget, journal);
        Check(subApply.Success && (await File.ReadAllTextAsync(subTarget.FullPath, Utf8NoBom)).Contains("*.tmp", StringComparison.Ordinal),
              "Subdirectory .gitignore receives normalized relative rule cleanly");

        await GitInAsync(realGit, repo, "worktree", "remove", "--force", wtPath);
    }
}
