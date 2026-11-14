using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using Avalonia.Collections;
using Avalonia.Threading;

using CommunityToolkit.Mvvm.ComponentModel;

namespace SourceGit.ViewModels
{
    public sealed class OperationJournalEntryItem : ObservableObject
    {
        public Models.GitOperationRecord Record { get; }

        public string OperationId => Record.OperationId;
        public string Title => string.IsNullOrWhiteSpace(Record.Title) ? Record.Kind.ToString() : Record.Title;
        public string KindText => Record.Kind.ToString();
        public bool RequiresAttention => !Record.IsAcknowledged && (Models.OperationJournalStore.RequiresAttentionStage(Record.Stage) || Record.IsUnfinishedOnStartup);
        public bool IsWarningIcon => RequiresAttention || (Record.IsAcknowledged && Record.Stage == Models.GitOperationStage.PartialSuccess);
        public bool IsSuccess => Record.Stage == Models.GitOperationStage.Succeeded && !Record.IsUnfinishedOnStartup;
        public bool IsFailure => Record.Stage == Models.GitOperationStage.Failed;
        public bool HasBackupRef => !string.IsNullOrWhiteSpace(Record.BackupRefName);
        public bool HasReconciliationNote => !string.IsNullOrWhiteSpace(ReconciliationNote);
        public bool HasDiagnostics => !string.IsNullOrWhiteSpace(DiagnosticText);

        public string StageText => Record.Stage switch
        {
            Models.GitOperationStage.Preparing => App.Text("OperationRecovery.Stage.Preparing"),
            Models.GitOperationStage.AwaitingConfirmation => App.Text("OperationRecovery.Stage.AwaitingConfirmation"),
            Models.GitOperationStage.Executing => App.Text("OperationRecovery.Stage.Executing"),
            Models.GitOperationStage.PendingVerification => App.Text("OperationRecovery.Stage.PendingVerification"),
            Models.GitOperationStage.CanceledUncertain => App.Text("OperationRecovery.Stage.CanceledUncertain"),
            Models.GitOperationStage.PartialSuccess => App.Text("OperationRecovery.Stage.PartialSuccess"),
            Models.GitOperationStage.Failed => App.Text("OperationRecovery.Stage.Failed"),
            _ => App.Text("OperationRecovery.Stage.Succeeded"),
        };

        public string TimeDisplay => Record.CreatedAtUtc.ToLocalTime().ToString("MM-dd HH:mm");
        public string FullTimeDisplay => Record.CreatedAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
        public string BranchDisplay => string.IsNullOrWhiteSpace(Record.BranchName) ? "-" : Record.BranchName;
        public string RemoteDisplay => string.IsNullOrWhiteSpace(Record.RemoteName)
            ? "-"
            : (string.IsNullOrWhiteSpace(Record.RemoteBranchName)
                ? Record.RemoteName
                : $"{Record.RemoteName}/{Record.RemoteBranchName}");

        public string OriginalTipDisplay => FormatSha(Record.OriginalTipSha);
        public string TargetTipDisplay => FormatSha(Record.TargetTipSha);
        public string ExpectedRemoteTipDisplay => FormatSha(Record.ExpectedRemoteTipSha);
        public string BackupRefDisplay => string.IsNullOrWhiteSpace(Record.BackupRefName) ? "-" : Record.BackupRefName;
        public string StepSummary => Models.GitConfigSnapshot.RedactText(Record.StepSummary ?? string.Empty);
        public string ReconciliationNote => Models.GitConfigSnapshot.RedactText(Record.ReconciliationNote ?? string.Empty);
        public string DiagnosticText { get; }

        public OperationJournalEntryItem(Models.GitOperationRecord record)
        {
            Record = record ?? throw new ArgumentNullException(nameof(record));
            DiagnosticText = BuildDiagnosticText(record);
        }

        private static string FormatSha(string sha)
        {
            if (string.IsNullOrWhiteSpace(sha))
                return "-";

            var trimmed = sha.Trim();
            return trimmed.Length > 12 ? trimmed[..12] : trimmed;
        }

        private static string BuildDiagnosticText(Models.GitOperationRecord record)
        {
            var sb = new StringBuilder();
            if (!string.IsNullOrWhiteSpace(record.CommandSummary))
                sb.AppendLine(Models.GitConfigSnapshot.RedactText(record.CommandSummary.Trim()));

            if (!string.IsNullOrWhiteSpace(record.DiagnosticDetail))
            {
                if (sb.Length > 0)
                    sb.AppendLine();
                sb.AppendLine(Models.GitConfigSnapshot.RedactText(record.DiagnosticDetail.Trim()));
            }

            return sb.ToString().Trim();
        }
    }

    public class ViewLogs : ObservableObject
    {
        public AvaloniaList<CommandLog> Logs => _repo.Logs;

        public CommandLog SelectedLog
        {
            get => _selectedLog;
            set => SetProperty(ref _selectedLog, value);
        }

        public AvaloniaList<OperationJournalEntryItem> Operations { get; } = [];

        public OperationJournalEntryItem SelectedOperation
        {
            get => _selectedOperation;
            set
            {
                if (SetProperty(ref _selectedOperation, value))
                {
                    RestoreTargetBranch = value?.Record.BranchName ?? string.Empty;
                    NotifyOperationSelectionChanged();
                }
            }
        }

        public int SelectedTabIndex
        {
            get => _selectedTabIndex;
            set
            {
                if (SetProperty(ref _selectedTabIndex, value))
                {
                    OnPropertyChanged(nameof(IsOperationsTabActive));
                    OnPropertyChanged(nameof(IsRawLogsTabActive));
                }
            }
        }

        public bool IsOperationsTabActive => _selectedTabIndex == 0;
        public bool IsRawLogsTabActive => _selectedTabIndex == 1;

        public string RepoStateSummary
        {
            get => _repoStateSummary;
            private set => SetProperty(ref _repoStateSummary, value);
        }

        public string ActiveBackupsSummary
        {
            get => _activeBackupsSummary;
            private set => SetProperty(ref _activeBackupsSummary, value);
        }

        public string RestoreTargetBranch
        {
            get => _restoreTargetBranch;
            set => SetProperty(ref _restoreTargetBranch, value);
        }

        public bool IsBusy
        {
            get => _isBusy;
            private set
            {
                if (SetProperty(ref _isBusy, value))
                {
                    OnPropertyChanged(nameof(CanAcknowledgeSelectedOperation));
                    OnPropertyChanged(nameof(CanRestoreSelectedBackup));
                }
            }
        }

        public bool HasFeedbackMessage => !string.IsNullOrWhiteSpace(_feedbackMessage);

        public string FeedbackMessage
        {
            get => _feedbackMessage;
            private set
            {
                if (SetProperty(ref _feedbackMessage, value))
                    OnPropertyChanged(nameof(HasFeedbackMessage));
            }
        }

        public bool IsFeedbackError
        {
            get => _isFeedbackError;
            private set => SetProperty(ref _isFeedbackError, value);
        }

        public Models.GitBackupPrunePreview PrunePreview
        {
            get => _prunePreview;
            private set
            {
                if (SetProperty(ref _prunePreview, value))
                {
                    OnPropertyChanged(nameof(HasPrunePreview));
                    OnPropertyChanged(nameof(HasExpiredBackupsToPrune));
                    OnPropertyChanged(nameof(PrunePreviewSummary));
                    OnPropertyChanged(nameof(PruneExpiredListText));
                }
            }
        }

        public bool HasPrunePreview => _prunePreview != null;
        public bool HasExpiredBackupsToPrune => _prunePreview is { ExpiredBackups.Count: > 0 };
        public string PrunePreviewSummary => _prunePreview?.Summary ?? string.Empty;
        public string PruneExpiredListText => _prunePreview is { ExpiredBackups.Count: > 0 }
            ? string.Join(Environment.NewLine, _prunePreview.ExpiredBackups.Select(FormatBackupLine))
            : string.Empty;

        public bool HasSelectedOperation => _selectedOperation != null;
        public bool HasSelectedReconciliationNote => _selectedOperation is { HasReconciliationNote: true };
        public bool HasSelectedDiagnostics => _selectedOperation is { HasDiagnostics: true };
        public bool CanAcknowledgeSelectedOperation => _selectedOperation is { RequiresAttention: true } && !_isBusy;
        public bool CanRestoreSelectedBackup => _selectedOperation is { HasBackupRef: true } && !_isBusy;
        public string SelectedOperationDiagnosticText => _selectedOperation?.DiagnosticText ?? string.Empty;

        public ViewLogs(
            Repository repo,
            bool focusRecoveryTab = false,
            Models.OperationJournalStore journalStore = null,
            DateTime? utcNowOverride = null)
        {
            _repo = repo ?? throw new ArgumentNullException(nameof(repo));
            _journalStore = journalStore ?? Models.OperationJournalStore.Instance;
            _utcNowOverride = utcNowOverride;

            if (focusRecoveryTab || repo.HasPendingRecoveryOperations || repo.Logs.Count == 0)
                _selectedTabIndex = 0;
            else
                _selectedTabIndex = 1;
        }

        public void SelectTab(int index)
        {
            SelectedTabIndex = index == 1 ? 1 : 0;
        }

        public void ClearAll()
        {
            SelectedLog = null;
            Logs.Clear();
        }

        public void DismissFeedback()
        {
            FeedbackMessage = string.Empty;
            IsFeedbackError = false;
        }

        public void CancelPrunePreview()
        {
            PrunePreview = null;
        }

        public async Task RefreshOperationsAsync(bool runReconciliation = false)
        {
            if (IsBusy)
                return;

            IsBusy = true;
            try
            {
                if (runReconciliation)
                    await _repo.RefreshRecoveryStatusAsync(_journalStore).ConfigureAwait(false);

                var records = await Task.Run(() =>
                {
                    var keyDir = string.IsNullOrWhiteSpace(_repo.GitCommonDir) ? _repo.GitDir : _repo.GitCommonDir;
                    return _journalStore.GetByRepository(keyDir, 100);
                }).ConfigureAwait(false);

                var backups = await Commands.BackupRefs
                    .ListAsync(_repo.FullPath, Commands.BackupRefs.DefaultRetentionDays, _journalStore, _utcNowOverride)
                    .ConfigureAwait(false);

                var branchText = _repo.CurrentBranch?.Name ?? "detached HEAD";
                var headSha = _repo.CurrentBranch?.Head ?? string.Empty;
                var shortHead = headSha.Length > 8 ? headSha[..8] : (string.IsNullOrEmpty(headSha) ? "-" : headSha);
                var items = records.Select(r => new OperationJournalEntryItem(r)).ToList();

                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    var previousId = _selectedOperation?.OperationId;
                    Operations.Clear();
                    foreach (var item in items)
                        Operations.Add(item);

                    RepoStateSummary = App.Text("OperationRecovery.CurrentRepoState", branchText, shortHead);
                    ActiveBackupsSummary = App.Text("OperationRecovery.ActiveBackupsCount", backups.Count);

                    if (Operations.Count == 0)
                    {
                        SelectedOperation = null;
                    }
                    else
                    {
                        var match = !string.IsNullOrEmpty(previousId)
                            ? Operations.FirstOrDefault(o => string.Equals(o.OperationId, previousId, StringComparison.Ordinal))
                            : null;
                        SelectedOperation = match
                            ?? Operations.FirstOrDefault(o => o.RequiresAttention)
                            ?? Operations[0];
                    }
                });
            }
            catch (Exception ex)
            {
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    IsFeedbackError = true;
                    FeedbackMessage = Models.GitConfigSnapshot.RedactText(ex.Message);
                });
            }
            finally
            {
                await Dispatcher.UIThread.InvokeAsync(() => IsBusy = false);
            }
        }

        public async Task VerifySelectedOperationAsync()
        {
            var current = SelectedOperation;
            if (current == null || IsBusy)
                return;

            IsBusy = true;
            try
            {
                await Commands.OperationJournalService
                    .VerifyRecordStateAsync(_repo.FullPath, current.OperationId, _journalStore, acknowledgeResolved: false)
                    .ConfigureAwait(false);

                await _repo.RefreshRecoveryStatusAsync(_journalStore).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    IsFeedbackError = true;
                    FeedbackMessage = Models.GitConfigSnapshot.RedactText(ex.Message);
                });
                await Dispatcher.UIThread.InvokeAsync(() => IsBusy = false);
                return;
            }

            await Dispatcher.UIThread.InvokeAsync(() => IsBusy = false);
            await RefreshOperationsAsync(runReconciliation: false);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                IsFeedbackError = false;
                FeedbackMessage = App.Text("OperationRecovery.VerifyCompleted");
            });
        }

        public async Task AcknowledgeSelectedOperationAsync()
        {
            var current = SelectedOperation;
            if (current == null || IsBusy)
                return;

            IsBusy = true;
            try
            {
                await Commands.OperationJournalService
                    .VerifyRecordStateAsync(_repo.FullPath, current.OperationId, _journalStore, acknowledgeResolved: true)
                    .ConfigureAwait(false);

                await _repo.RefreshRecoveryStatusAsync(_journalStore).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    IsFeedbackError = true;
                    FeedbackMessage = Models.GitConfigSnapshot.RedactText(ex.Message);
                });
                await Dispatcher.UIThread.InvokeAsync(() => IsBusy = false);
                return;
            }

            await Dispatcher.UIThread.InvokeAsync(() => IsBusy = false);
            await RefreshOperationsAsync(runReconciliation: false);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                IsFeedbackError = false;
                FeedbackMessage = App.Text("OperationRecovery.AcknowledgeCompleted");
            });
        }

        public async Task RestoreSelectedBackupAsync()
        {
            var current = SelectedOperation;
            if (current == null || !current.HasBackupRef || IsBusy)
                return;

            var targetBranch = (_restoreTargetBranch ?? string.Empty).Trim();
            IsBusy = true;
            Models.GitBackupRestoreResult res;
            try
            {
                var log = _repo.CreateLog("Restore Backup Branch");
                res = await Commands.BackupRefs
                    .RestoreBranchFromBackupAsync(_repo.FullPath, targetBranch, current.Record.BackupRefName, log)
                    .ConfigureAwait(false);
                log.Complete();

                if (res.Succeeded)
                    _repo.RefreshBranches();
            }
            catch (Exception ex)
            {
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    IsFeedbackError = true;
                    FeedbackMessage = Models.GitConfigSnapshot.RedactText(ex.Message);
                    IsBusy = false;
                });
                return;
            }

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                IsFeedbackError = !res.Succeeded;
                FeedbackMessage = res.Summary;
                IsBusy = false;
            });

            if (res.Succeeded)
                await RefreshOperationsAsync(runReconciliation: false);
        }

        public async Task PreviewExpiredBackupsAsync()
        {
            if (IsBusy)
                return;

            IsBusy = true;
            try
            {
                var preview = await Commands.BackupRefs
                    .PreviewExpiredAsync(_repo.FullPath, Commands.BackupRefs.DefaultRetentionDays, _journalStore, _utcNowOverride)
                    .ConfigureAwait(false);

                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    PrunePreview = preview;
                    if (preview.ExpiredBackups.Count == 0)
                    {
                        IsFeedbackError = false;
                        FeedbackMessage = App.Text("OperationRecovery.NoExpiredBackups");
                    }
                });
            }
            catch (Exception ex)
            {
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    IsFeedbackError = true;
                    FeedbackMessage = Models.GitConfigSnapshot.RedactText(ex.Message);
                });
            }
            finally
            {
                await Dispatcher.UIThread.InvokeAsync(() => IsBusy = false);
            }
        }

        public async Task ConfirmPruneExpiredBackupsAsync()
        {
            var preview = PrunePreview;
            if (preview == null || preview.ExpiredBackups.Count == 0 || IsBusy)
                return;

            IsBusy = true;
            Models.GitBackupPruneResult res;
            try
            {
                var log = _repo.CreateLog("Prune Expired Backup Refs");
                res = await Commands.BackupRefs
                    .PruneExpiredAsync(_repo.FullPath, preview.ExpiredBackups, log)
                    .ConfigureAwait(false);
                log.Complete();
            }
            catch (Exception ex)
            {
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    IsFeedbackError = true;
                    FeedbackMessage = Models.GitConfigSnapshot.RedactText(ex.Message);
                    IsBusy = false;
                });
                return;
            }

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                PrunePreview = null;
                IsFeedbackError = !res.Succeeded;
                FeedbackMessage = res.Summary;
                IsBusy = false;
            });

            await RefreshOperationsAsync(runReconciliation: false);
        }

        private void NotifyOperationSelectionChanged()
        {
            OnPropertyChanged(nameof(HasSelectedOperation));
            OnPropertyChanged(nameof(HasSelectedReconciliationNote));
            OnPropertyChanged(nameof(HasSelectedDiagnostics));
            OnPropertyChanged(nameof(CanAcknowledgeSelectedOperation));
            OnPropertyChanged(nameof(CanRestoreSelectedBackup));
            OnPropertyChanged(nameof(SelectedOperationDiagnosticText));
        }

        private static string FormatBackupLine(Models.GitBackupRefInfo info)
        {
            var shortSha = info.CommitSha.Length > 8 ? info.CommitSha[..8] : info.CommitSha;
            return $"{info.FullRefName} ({shortSha} · {info.AgeDays:F1}d)";
        }

        private readonly Repository _repo;
        private readonly Models.OperationJournalStore _journalStore;
        private readonly DateTime? _utcNowOverride;
        private CommandLog _selectedLog = null;
        private OperationJournalEntryItem _selectedOperation = null;
        private int _selectedTabIndex = 0;
        private string _repoStateSummary = string.Empty;
        private string _activeBackupsSummary = string.Empty;
        private string _restoreTargetBranch = string.Empty;
        private bool _isBusy = false;
        private string _feedbackMessage = string.Empty;
        private bool _isFeedbackError = false;
        private Models.GitBackupPrunePreview _prunePreview = null;
    }
}
