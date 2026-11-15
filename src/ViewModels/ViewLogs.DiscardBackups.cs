using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Collections;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;

namespace SourceGit.ViewModels
{
    public sealed class DiscardBackupFileRow
    {
        public string RelativePath { get; init; } = string.Empty;
        public string StateLabel { get; init; } = string.Empty;
        public string PayloadSummary { get; init; } = string.Empty;
        public bool HasSubsequentModification { get; init; }
        public string ConflictBadge => HasSubsequentModification ? App.Text("ViewLogs.DiscardBackups.ModifiedBadge") : string.Empty;
    }

    public sealed class DiscardBackupItemViewModel : ObservableObject
    {
        public Models.DiscardBackupEntry Entry { get; }

        public string BackupId => Entry.BackupId;
        public string ShortId => Entry.BackupId.Length > 8 ? Entry.BackupId[..8] : Entry.BackupId;
        public Models.DiscardBaselineKind Baseline => Entry.Baseline;

        public string BaselineLabel => Entry.Baseline switch
        {
            Models.DiscardBaselineKind.RestoreToHead => App.Text("Discard.BaselineLabel.ToHead"),
            Models.DiscardBaselineKind.MoveUntrackedToRecycleBin => App.Text("Discard.BaselineLabel.Recycle"),
            _ => App.Text("Discard.BaselineLabel.ToIndex"),
        };

        public string TimeDisplay => Entry.CreatedAtUtc.ToLocalTime().ToString("MM-dd HH:mm");
        public string FullTimeDisplay => Entry.CreatedAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
        public string ExpiresDisplay => Entry.ExpiresAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
        public string HeadShortSha => string.IsNullOrEmpty(Entry.HeadSha)
            ? "-"
            : (Entry.HeadSha.Length > 8 ? Entry.HeadSha[..8] : Entry.HeadSha);

        public string Summary => string.IsNullOrWhiteSpace(Entry.Summary)
            ? BaselineLabel
            : Entry.Summary;

        public string FileCountText => App.Text("ViewLogs.DiscardBackups.FileCount", Entry.Files.Count);
        public bool IsRestored => Entry.RestoredAtUtc.HasValue;
        public string RestoredDisplay => Entry.RestoredAtUtc.HasValue
            ? App.Text("ViewLogs.DiscardBackups.RestoredAt", Entry.RestoredAtUtc.Value.ToLocalTime().ToString("MM-dd HH:mm"))
            : string.Empty;

        public bool IsExpired { get; }
        public string ExpiryBadgeText { get; }

        public DiscardBackupItemViewModel(Models.DiscardBackupEntry entry, DateTime nowUtc)
        {
            Entry = entry;
            IsExpired = nowUtc > entry.ExpiresAtUtc;
            if (IsExpired)
            {
                ExpiryBadgeText = App.Text("ViewLogs.DiscardBackups.Expired");
            }
            else
            {
                var remaining = entry.ExpiresAtUtc - nowUtc;
                var days = Math.Max(1, (int)Math.Ceiling(remaining.TotalDays));
                ExpiryBadgeText = App.Text("ViewLogs.DiscardBackups.DaysLeft", days);
            }
        }
    }

    public partial class ViewLogs
    {
        public AvaloniaList<DiscardBackupItemViewModel> DiscardBackups { get; } = [];
        public AvaloniaList<DiscardBackupFileRow> SelectedDiscardBackupFiles { get; } = [];
        public AvaloniaList<string> SelectedDiscardConflictDetails { get; } = [];

        public DiscardBackupItemViewModel SelectedDiscardBackup
        {
            get => _selectedDiscardBackup;
            set
            {
                if (SetProperty(ref _selectedDiscardBackup, value))
                {
                    AllowOverwriteSubsequentEdits = false;
                    OnPropertyChanged(nameof(HasSelectedDiscardBackup));
                    OnPropertyChanged(nameof(SelectedDiscardBaselineLabel));
                    OnPropertyChanged(nameof(SelectedDiscardMetaLine));
                    NotifyDiscardBackupActionStates();
                    _ = InspectSelectedDiscardBackupAsync();
                }
            }
        }

        public string DiscardBackupsSummary
        {
            get => _discardBackupsSummary;
            private set => SetProperty(ref _discardBackupsSummary, value);
        }

        public bool IsLoadingDiscardRestoreCheck
        {
            get => _isLoadingDiscardRestoreCheck;
            private set
            {
                if (SetProperty(ref _isLoadingDiscardRestoreCheck, value))
                    NotifyDiscardBackupActionStates();
            }
        }

        public bool HasSelectedDiscardBackup => _selectedDiscardBackup != null;

        public string SelectedDiscardBaselineLabel => _selectedDiscardBackup?.BaselineLabel ?? string.Empty;

        public string SelectedDiscardMetaLine => _selectedDiscardBackup == null
            ? string.Empty
            : $"{_selectedDiscardBackup.FullTimeDisplay} · HEAD {_selectedDiscardBackup.HeadShortSha} · ID {_selectedDiscardBackup.BackupId}";

        public bool HasSelectedDiscardConflicts
        {
            get => _hasSelectedDiscardConflicts;
            private set => SetProperty(ref _hasSelectedDiscardConflicts, value);
        }

        public bool HasSelectedDiscardBlockingError
        {
            get => _hasSelectedDiscardBlockingError;
            private set => SetProperty(ref _hasSelectedDiscardBlockingError, value);
        }

        public string SelectedDiscardBlockingReason
        {
            get => _selectedDiscardBlockingReason;
            private set => SetProperty(ref _selectedDiscardBlockingReason, value);
        }

        public bool AllowOverwriteSubsequentEdits
        {
            get => _allowOverwriteSubsequentEdits;
            set
            {
                if (SetProperty(ref _allowOverwriteSubsequentEdits, value))
                    NotifyDiscardBackupActionStates();
            }
        }

        public bool CanRestoreSelectedDiscardBackup
        {
            get
            {
                if (_selectedDiscardBackup == null || _isBusy || _isLoadingDiscardRestoreCheck)
                    return false;
                if (_selectedDiscardRestoreCheck == null)
                    return false;
                if (!_selectedDiscardRestoreCheck.Found ||
                    _selectedDiscardRestoreCheck.IsExpired ||
                    _selectedDiscardRestoreCheck.HasPayloadIntegrityError)
                    return false;
                if (_selectedDiscardRestoreCheck.HasSubsequentModifications && !_allowOverwriteSubsequentEdits)
                    return false;
                return true;
            }
        }

        public bool CanDeleteSelectedDiscardBackup => _selectedDiscardBackup != null && !_isBusy;
        public bool HasExpiredDiscardBackups => _expiredDiscardBackupsCount > 0;
        public bool CanPruneExpiredDiscardBackups => _expiredDiscardBackupsCount > 0 && !_isBusy;

        public int TabCount => 3;

        partial void OnSelectedTabChanged(int index)
        {
            if (index == 2 && DiscardBackups.Count == 0 && !IsBusy)
                _ = RefreshDiscardBackupsAsync();
        }

        public async Task RefreshDiscardBackupsAsync()
        {
            var prevId = _selectedDiscardBackup?.BackupId;
            var store = ResolveDiscardBackupStore();
            var now = _utcNowOverride ?? DateTime.UtcNow;

            var entries = await Task.Run(() =>
                store.ListForRepository(_repo.FullPath, _repo.GitCommonDir, now, includeExpired: true))
                .ConfigureAwait(false);

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                DiscardBackups.Clear();
                foreach (var e in entries)
                    DiscardBackups.Add(new DiscardBackupItemViewModel(e, now));

                var expiredCount = DiscardBackups.Count(b => b.IsExpired);
                _expiredDiscardBackupsCount = expiredCount;
                OnPropertyChanged(nameof(HasExpiredDiscardBackups));
                OnPropertyChanged(nameof(CanPruneExpiredDiscardBackups));

                DiscardBackupsSummary = expiredCount > 0
                    ? App.Text("ViewLogs.DiscardBackups.SummaryWithExpired", DiscardBackups.Count - expiredCount, expiredCount)
                    : App.Text("ViewLogs.DiscardBackups.Summary", DiscardBackups.Count);

                DiscardBackupItemViewModel toSelect = null;
                if (!string.IsNullOrEmpty(prevId))
                    toSelect = DiscardBackups.FirstOrDefault(b => b.BackupId.Equals(prevId, StringComparison.Ordinal));
                toSelect ??= DiscardBackups.FirstOrDefault();

                if (!ReferenceEquals(toSelect, _selectedDiscardBackup))
                    SelectedDiscardBackup = toSelect;
                else
                    _ = InspectSelectedDiscardBackupAsync();

                NotifyDiscardBackupActionStates();
            });
        }

        public async Task InspectSelectedDiscardBackupAsync()
        {
            var seq = Interlocked.Increment(ref _discardInspectSeq);
            var item = _selectedDiscardBackup;

            if (item == null)
            {
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    if (seq != Volatile.Read(ref _discardInspectSeq))
                        return;

                    _selectedDiscardRestoreCheck = null;
                    SelectedDiscardBackupFiles.Clear();
                    SelectedDiscardConflictDetails.Clear();
                    HasSelectedDiscardConflicts = false;
                    HasSelectedDiscardBlockingError = false;
                    SelectedDiscardBlockingReason = string.Empty;
                    NotifyDiscardBackupActionStates();
                });
                return;
            }

            IsLoadingDiscardRestoreCheck = true;
            try
            {
                var check = await Commands.DiscardBackupService.InspectBackupRestoreAsync(
                    _repo.FullPath,
                    item.BackupId,
                    ResolveDiscardBackupStore(),
                    _utcNowOverride).ConfigureAwait(false);

                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    if (seq != Volatile.Read(ref _discardInspectSeq))
                        return;

                    _selectedDiscardRestoreCheck = check;
                    PopulateDiscardInspectDetails(item, check);
                });
            }
            catch (Exception ex)
            {
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    if (seq != Volatile.Read(ref _discardInspectSeq))
                        return;

                    _selectedDiscardRestoreCheck = null;
                    HasSelectedDiscardBlockingError = true;
                    SelectedDiscardBlockingReason = ex.Message;
                    NotifyDiscardBackupActionStates();
                });
            }
            finally
            {
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    if (seq == Volatile.Read(ref _discardInspectSeq))
                    {
                        IsLoadingDiscardRestoreCheck = false;
                        NotifyDiscardBackupActionStates();
                    }
                });
            }
        }

        public async Task RestoreSelectedDiscardBackupAsync()
        {
            var item = _selectedDiscardBackup;
            if (item == null || IsBusy || !CanRestoreSelectedDiscardBackup)
                return;

            IsBusy = true;
            NotifyDiscardBackupActionStates();
            try
            {
                var res = await Commands.DiscardBackupService.RestoreBackupAsync(
                    _repo.FullPath,
                    item.BackupId,
                    allowOverwriteSubsequentEdits: AllowOverwriteSubsequentEdits,
                    backupStore: ResolveDiscardBackupStore(),
                    journalStore: _journalStore,
                    utcNowOverride: _utcNowOverride).ConfigureAwait(false);

                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    FeedbackMessage = res.Summary;
                    IsFeedbackError = !res.Success;
                    if (res.Success)
                        _repo.MarkWorkingCopyDirtyManually();
                });

                await RefreshDiscardBackupsAsync().ConfigureAwait(false);
                await RefreshOperationsAsync(runReconciliation: false).ConfigureAwait(false);
            }
            finally
            {
                IsBusy = false;
                NotifyDiscardBackupActionStates();
            }
        }

        public async Task DeleteSelectedDiscardBackupAsync()
        {
            var item = _selectedDiscardBackup;
            if (item == null || IsBusy)
                return;

            IsBusy = true;
            NotifyDiscardBackupActionStates();
            try
            {
                var deleted = await Task.Run(() => ResolveDiscardBackupStore().DeleteBackup(item.BackupId)).ConfigureAwait(false);
                FeedbackMessage = deleted
                    ? App.Text("ViewLogs.DiscardBackups.Deleted", item.ShortId)
                    : App.Text("ViewLogs.DiscardBackups.DeleteFailed", item.ShortId);
                IsFeedbackError = !deleted;
                await RefreshDiscardBackupsAsync().ConfigureAwait(false);
            }
            finally
            {
                IsBusy = false;
                NotifyDiscardBackupActionStates();
            }
        }

        public async Task PruneExpiredDiscardBackupsAsync()
        {
            if (IsBusy)
                return;

            IsBusy = true;
            NotifyDiscardBackupActionStates();
            try
            {
                var pruneRes = await Task.Run(() => ResolveDiscardBackupStore().PruneExpired(_utcNowOverride)).ConfigureAwait(false);
                FeedbackMessage = pruneRes.Summary;
                IsFeedbackError = false;
                await RefreshDiscardBackupsAsync().ConfigureAwait(false);
            }
            finally
            {
                IsBusy = false;
                NotifyDiscardBackupActionStates();
            }
        }

        private void PopulateDiscardInspectDetails(DiscardBackupItemViewModel item, Models.DiscardBackupRestoreCheck check)
        {
            SelectedDiscardBackupFiles.Clear();
            SelectedDiscardConflictDetails.Clear();

            var conflictingSet = new HashSet<string>(
                check.ConflictingPaths ?? [],
                OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

            foreach (var f in item.Entry.Files)
            {
                var wtInfo = f.ExistsInWorktree
                    ? (f.IsSymlink ? "工作区(符号链接)" : $"工作区({f.WorktreeByteLength} B)")
                    : "工作区(无文件)";
                var idxInfo = f.ExistsInIndex
                    ? "含暂存区快照"
                    : "无暂存条目";
                var stateLabel = f.Baseline == Models.DiscardBaselineKind.MoveUntrackedToRecycleBin
                    ? "回收站还原"
                    : f.Baseline == Models.DiscardBaselineKind.RestoreToHead
                        ? "工作区 + 暂存区"
                        : "仅工作区";

                SelectedDiscardBackupFiles.Add(new DiscardBackupFileRow
                {
                    RelativePath = f.RelativePath,
                    StateLabel = stateLabel,
                    PayloadSummary = $"{wtInfo} · {idxInfo}",
                    HasSubsequentModification = conflictingSet.Contains(f.RelativePath),
                });
            }

            foreach (var detail in check.ConflictDetails ?? [])
                SelectedDiscardConflictDetails.Add(detail);

            HasSelectedDiscardConflicts = check.HasSubsequentModifications;

            var blocking = check.IsExpired || check.HasPayloadIntegrityError || !check.Found;
            HasSelectedDiscardBlockingError = blocking;
            SelectedDiscardBlockingReason = blocking ? check.BlockingReason : string.Empty;

            NotifyDiscardBackupActionStates();
        }

        private Models.DiscardBackupStore ResolveDiscardBackupStore()
        {
            return _discardBackupStore ?? Models.DiscardBackupStore.Instance;
        }

        private void NotifyDiscardBackupActionStates()
        {
            OnPropertyChanged(nameof(CanRestoreSelectedDiscardBackup));
            OnPropertyChanged(nameof(CanDeleteSelectedDiscardBackup));
            OnPropertyChanged(nameof(CanPruneExpiredDiscardBackups));
        }

        private readonly Models.DiscardBackupStore _discardBackupStore;
        private DiscardBackupItemViewModel _selectedDiscardBackup = null;
        private Models.DiscardBackupRestoreCheck _selectedDiscardRestoreCheck = null;
        private string _discardBackupsSummary = string.Empty;
        private bool _isLoadingDiscardRestoreCheck = false;
        private bool _hasSelectedDiscardConflicts = false;
        private bool _hasSelectedDiscardBlockingError = false;
        private string _selectedDiscardBlockingReason = string.Empty;
        private bool _allowOverwriteSubsequentEdits = false;
        private int _expiredDiscardBackupsCount = 0;
        private int _discardInspectSeq = 0;
    }
}
