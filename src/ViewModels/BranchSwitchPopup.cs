using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Collections;
using Avalonia.Threading;

namespace SourceGit.ViewModels
{
    public abstract class BranchSwitchPopup : Popup
    {
        public bool HasLocalChanges => _repo.LocalChangesCount > 0;

        public Models.DealWithLocalChanges DealWithLocalChanges
        {
            get => _dealWithLocalChanges;
            set
            {
                var safe = value == Models.DealWithLocalChanges.Discard
                    ? Models.DealWithLocalChanges.DoNothing
                    : value;
                SetProperty(ref _dealWithLocalChanges, safe);
            }
        }

        public bool HasSubmodules
        {
            get => _hasSubmodules;
            protected set
            {
                if (SetProperty(ref _hasSubmodules, value))
                    OnPropertyChanged(nameof(ShowSubmoduleUpdateOption));
            }
        }

        public virtual bool ShowSubmoduleUpdateOption => ShowSwitchForm && _hasSubmodules;

        public bool UpdateSubmodulesAfterSwitch
        {
            get => _updateSubmodulesAfterSwitch;
            set => SetProperty(ref _updateSubmodulesAfterSwitch, value);
        }

        public bool RecursiveSubmoduleUpdate
        {
            get => _recursiveSubmoduleUpdate;
            set => SetProperty(ref _recursiveSubmoduleUpdate, value);
        }

        public bool IsAwaitingSubmoduleConfirm
        {
            get => _isAwaitingSubmoduleConfirm;
            protected set
            {
                if (SetProperty(ref _isAwaitingSubmoduleConfirm, value))
                {
                    OnPropertyChanged(nameof(ShowSwitchForm));
                    OnPropertyChanged(nameof(ShowSecondaryPanel));
                    OnPropertyChanged(nameof(ShowSubmoduleUpdateOption));
                    NotifyPopupButtonsChanged();
                }
            }
        }

        public bool HasUninitializedSubmodules
        {
            get => _hasUninitializedSubmodules;
            private set => SetProperty(ref _hasUninitializedSubmodules, value);
        }

        public string SubmoduleConfirmSummary
        {
            get => _submoduleConfirmSummary;
            private set => SetProperty(ref _submoduleConfirmSummary, value);
        }

        public AvaloniaList<string> AffectedSubmodules { get; } = [];

        public bool HasResultView
        {
            get => _hasResultView;
            protected set
            {
                if (SetProperty(ref _hasResultView, value))
                {
                    OnPropertyChanged(nameof(ShowSwitchForm));
                    OnPropertyChanged(nameof(ShowSecondaryPanel));
                    OnPropertyChanged(nameof(ShowSubmoduleUpdateOption));
                    OnPropertyChanged(nameof(IsSubmoduleFailureResult));
                    OnPropertyChanged(nameof(IsGeneralSwitchFailure));
                    NotifyPopupButtonsChanged();
                }
            }
        }

        public bool IsPartialSubmoduleFailure
        {
            get => _isPartialSubmoduleFailure;
            private set
            {
                if (SetProperty(ref _isPartialSubmoduleFailure, value))
                {
                    OnPropertyChanged(nameof(IsSubmoduleFailureResult));
                    OnPropertyChanged(nameof(IsGeneralSwitchFailure));
                }
            }
        }

        public bool ShowSwitchForm => !HasResultView && !IsAwaitingSubmoduleConfirm;
        public bool ShowSecondaryPanel => HasResultView || IsAwaitingSubmoduleConfirm;
        public bool IsSubmoduleFailureResult => HasResultView && IsPartialSubmoduleFailure;
        public bool IsGeneralSwitchFailure => HasResultView && !IsPartialSubmoduleFailure;

        public bool IsResultConflictRetained
        {
            get => _isResultConflictRetained;
            private set => SetProperty(ref _isResultConflictRetained, value);
        }

        public string ResultTitle
        {
            get => _resultTitle;
            private set => SetProperty(ref _resultTitle, value);
        }

        public string ResultSummary
        {
            get => _resultSummary;
            private set => SetProperty(ref _resultSummary, value);
        }

        public string MainBranchOutcomeText
        {
            get => _mainBranchOutcomeText;
            private set => SetProperty(ref _mainBranchOutcomeText, value);
        }

        public string SubmoduleFailureText
        {
            get => _submoduleFailureText;
            private set => SetProperty(ref _submoduleFailureText, value);
        }

        public AvaloniaList<string> ResultLines { get; } = [];

        public override bool ShowConfirmButton => !HasResultView;
        public override bool CanConfirm => !HasResultView;

        public override string ConfirmButtonText => IsAwaitingSubmoduleConfirm
            ? FormatText("Checkout.Submodules.ConfirmUpdate", "Update Submodules")
            : FormatText("Sure", "Sure");

        public override string CancelButtonText => IsAwaitingSubmoduleConfirm
            ? FormatText("Checkout.Submodules.SkipAndClose", "Skip Update")
            : (HasResultView ? FormatText("Close", "Close") : FormatText("Cancel", "Cancel"));

        protected BranchSwitchPopup(Repository repo, string targetRevisionForSubmoduleCheck = null)
        {
            _repo = repo;
            _dealWithLocalChanges = Preferences.Instance.UseStashAndReapplyByDefault
                ? Models.DealWithLocalChanges.StashAndReapply
                : Models.DealWithLocalChanges.DoNothing;

            _hasSubmodules = _repo.Submodules.Count > 0 || File.Exists(Path.Combine(_repo.FullPath, ".gitmodules"));
            if (!_hasSubmodules && !string.IsNullOrWhiteSpace(targetRevisionForSubmoduleCheck))
                _ = ProbeTargetSubmodulePresenceAsync(targetRevisionForSubmoduleCheck);
        }

        protected async Task<bool> ExecuteSafeSwitchAsync(
            string logTitle,
            string progressDescription,
            string targetDescription,
            string[] checkoutGitArgs,
            Func<Task> onCheckoutSucceededAsync,
            bool skipDetachedHeadCheck = false)
        {
            if (IsAwaitingSubmoduleConfirm)
                return await ExecutePendingSubmoduleUpdateAsync().ConfigureAwait(false);

            using var lockWatcher = _repo.LockWatcher();
            ProgressDescription = progressDescription;

            var log = _repo.CreateLog(logTitle);
            Use(log);

            if (!skipDetachedHeadCheck && _repo.CurrentBranch is { IsDetachedHead: true } detached)
            {
                var refs = await new Commands.QueryRefsContainsCommit(_repo.FullPath, detached.Head)
                    .GetResultAsync()
                    .ConfigureAwait(false);
                if (refs.Count == 0)
                {
                    var shouldContinue = await App.AskConfirmAsync(App.Text("Checkout.WarnLostCommits"));
                    if (!shouldContinue)
                    {
                        log.Complete();
                        return true;
                    }
                }
            }

            var res = await Commands.StashWorkflowService.SwitchSafelyAsync(
                _repo.FullPath,
                checkoutGitArgs,
                _dealWithLocalChanges,
                targetDescription,
                journalStore: Models.OperationJournalStore.Instance,
                outerLog: log).ConfigureAwait(false);

            if (res.CheckoutSucceeded)
            {
                if (onCheckoutSucceededAsync != null)
                    await onCheckoutSucceededAsync().ConfigureAwait(false);

                _repo.MarkSubmodulesDirtyManually();
            }

            log.Complete();

            _repo.MarkWorkingCopyDirtyManually();
            if (res.StashCreated || res.ReapplyAttempted)
                _repo.RefreshStashes();
            _ = _repo.RefreshInProgressStateAsync();
            _ = _repo.RefreshRecoveryStatusAsync();

            if (res.Success)
            {
                if (UpdateSubmodulesAfterSwitch)
                {
                    var initedUpdatable = await new Commands.QueryUpdatableSubmodules(_repo.FullPath, false)
                        .GetResultAsync()
                        .ConfigureAwait(false);
                    var targets = initedUpdatable;
                    var needsInit = false;

                    if (targets.Count == 0)
                    {
                        var allPending = await new Commands.QueryUpdatableSubmodules(_repo.FullPath, true)
                            .GetResultAsync()
                            .ConfigureAwait(false);
                        if (allPending.Count > 0)
                        {
                            targets = allPending;
                            needsInit = true;
                        }
                    }

                    if (targets.Count > 0)
                    {
                        _pendingSubmoduleTargets = targets;
                        _pendingSubmodulesNeedInit = needsInit;
                        _switchedTargetDescription = targetDescription;

                        await RunOnUIThreadAsync(() =>
                        {
                            AffectedSubmodules.Clear();
                            foreach (var path in targets)
                                AffectedSubmodules.Add(path);

                            HasUninitializedSubmodules = needsInit;
                            SubmoduleConfirmSummary = FormatText(
                                "Checkout.Submodules.ConfirmSummary",
                                "Main branch switched to '{0}'. Review the affected submodules below and confirm before updating:",
                                targetDescription);
                            IsAwaitingSubmoduleConfirm = true;
                        }).ConfigureAwait(false);

                        return false;
                    }
                }

                if (res.StashCreated && res.ReapplySucceeded)
                    PostNotification(res.Summary, false);
                return true;
            }

            await RunOnUIThreadAsync(() =>
            {
                ResultLines.Clear();
                IsPartialSubmoduleFailure = false;
                IsResultConflictRetained = res.StashRetained || res.HasConflicts;
                ResultTitle = res.StashRetained
                    ? FormatText("Checkout.Result.StashRetainedTitle", "Branch Switched With Conflicts — Auto-Stash Retained")
                    : FormatText("Checkout.Result.FailedTitle", "Branch Switch Failed");
                ResultSummary = string.IsNullOrWhiteSpace(res.Summary)
                    ? ResultTitle
                    : res.Summary;

                foreach (var p in res.ConflictedPaths)
                    ResultLines.Add($"[冲突] {p}");
                if (res.StashRetained && !string.IsNullOrEmpty(res.StashSha))
                {
                    var shortSha = res.StashSha.Length > 8 ? res.StashSha[..8] : res.StashSha;
                    ResultLines.Add(FormatText(
                        "Checkout.Result.RetainedStashSha",
                        "Retained Stash SHA: {0}",
                        shortSha));
                }

                HasResultView = true;
            }).ConfigureAwait(false);

            return false;
        }

        protected async Task<bool> ExecutePendingSubmoduleUpdateAsync()
        {
            using var lockWatcher = _repo.LockWatcher();
            ProgressDescription = $"Update submodules ({_pendingSubmoduleTargets.Count}) ...";

            var log = _repo.CreateLog("Update Submodules");
            Use(log);

            var (succ, errMsg) = await new Commands.Submodule(_repo.FullPath)
                .Use(log)
                .UpdateDetailedAsync(
                    [.. _pendingSubmoduleTargets],
                    _pendingSubmodulesNeedInit,
                    RecursiveSubmoduleUpdate,
                    false)
                .ConfigureAwait(false);

            log.Complete();

            _repo.MarkSubmodulesDirtyManually();
            _repo.MarkWorkingCopyDirtyManually();

            if (succ)
            {
                PostNotification(
                    $"Switched to '{_switchedTargetDescription}' and updated {_pendingSubmoduleTargets.Count} submodule(s).",
                    false);
                return true;
            }

            await RunOnUIThreadAsync(() =>
            {
                IsAwaitingSubmoduleConfirm = false;
                IsPartialSubmoduleFailure = true;
                IsResultConflictRetained = false;
                ResultTitle = FormatText(
                    "Checkout.Result.SubmoduleUpdateFailedTitle",
                    "Branch Switched — Submodule Update Failed");
                ResultSummary = FormatText(
                    "Checkout.Result.SubmoduleUpdateFailedSummary",
                    "Main branch '{0}' switched cleanly, but updating submodules failed. Actual repository and submodule states have been refreshed.",
                    _switchedTargetDescription);
                MainBranchOutcomeText = FormatText(
                    "Checkout.Result.MainBranchSucceededLabel",
                    "Main branch: Switched cleanly to '{0}'.",
                    _switchedTargetDescription);
                SubmoduleFailureText = FormatText(
                    "Checkout.Result.SubmoduleFailedLabel",
                    "Submodule update: Failed — {0}",
                    string.IsNullOrWhiteSpace(errMsg) ? "git submodule update failed" : errMsg);

                ResultLines.Clear();
                foreach (var path in _pendingSubmoduleTargets)
                    ResultLines.Add(path);

                HasResultView = true;
            }).ConfigureAwait(false);

            return false;
        }

        private async Task ProbeTargetSubmodulePresenceAsync(string targetRevision)
        {
            var rs = await Commands.Command.RunGitDirectAsync(
                _repo.FullPath,
                TimeSpan.FromSeconds(5),
                CancellationToken.None,
                "cat-file",
                "-e",
                $"{targetRevision}:.gitmodules").ConfigureAwait(false);

            if (rs.IsSuccess)
            {
                await RunOnUIThreadAsync(() =>
                {
                    if (!_hasSubmodules)
                        HasSubmodules = true;
                }).ConfigureAwait(false);
            }
        }

        private void PostNotification(string message, bool isError)
        {
            if (Application.Current == null || Dispatcher.UIThread.CheckAccess())
            {
                if (Application.Current != null)
                    _repo.SendNotification(message, isError);
                return;
            }

            Dispatcher.UIThread.Post(() => _repo.SendNotification(message, isError));
        }

        private static async Task RunOnUIThreadAsync(Action action)
        {
            if (Dispatcher.UIThread.CheckAccess() || Application.Current == null)
            {
                action();
                return;
            }

            await Dispatcher.UIThread.InvokeAsync(action);
        }

        private static string FormatText(string key, string fallbackFormat, params object[] args)
        {
            if (Application.Current != null)
                return App.Text(key, args);

            return args == null || args.Length == 0
                ? fallbackFormat
                : string.Format(fallbackFormat, args);
        }

        protected readonly Repository _repo;
        private Models.DealWithLocalChanges _dealWithLocalChanges;
        private bool _hasSubmodules = false;
        private bool _updateSubmodulesAfterSwitch = false;
        private bool _recursiveSubmoduleUpdate = false;
        private bool _isAwaitingSubmoduleConfirm = false;
        private bool _hasUninitializedSubmodules = false;
        private string _submoduleConfirmSummary = string.Empty;
        private bool _hasResultView = false;
        private bool _isPartialSubmoduleFailure = false;
        private bool _isResultConflictRetained = false;
        private string _resultTitle = string.Empty;
        private string _resultSummary = string.Empty;
        private string _mainBranchOutcomeText = string.Empty;
        private string _submoduleFailureText = string.Empty;
        private List<string> _pendingSubmoduleTargets = [];
        private bool _pendingSubmodulesNeedInit = false;
        private string _switchedTargetDescription = string.Empty;
    }
}
