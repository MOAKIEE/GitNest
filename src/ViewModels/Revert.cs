using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace SourceGit.ViewModels
{
    public class Revert : Popup
    {
        public Models.Commit Target { get; }

        public bool AutoCommit
        {
            get => _autoCommit;
            set => SetProperty(ref _autoCommit, value);
        }

        public bool IsLoadingPreflight
        {
            get => _isLoadingPreflight;
            private set
            {
                if (SetProperty(ref _isLoadingPreflight, value))
                    NotifyPopupButtonsChanged();
            }
        }

        public bool HasPreflight
        {
            get => _hasPreflight;
            private set
            {
                if (SetProperty(ref _hasPreflight, value))
                    OnPropertyChanged(nameof(HasNormalFilesPreview));
            }
        }

        public string CurrentBranchDisplay
        {
            get => _currentBranchDisplay;
            private set => SetProperty(ref _currentBranchDisplay, value);
        }

        public string ParentSummary
        {
            get => _parentSummary;
            private set => SetProperty(ref _parentSummary, value);
        }

        public bool IsMergeCommitBlock
        {
            get => _isMergeCommitBlock;
            private set
            {
                if (SetProperty(ref _isMergeCommitBlock, value))
                {
                    OnPropertyChanged(nameof(ShowNormalPreviewContent));
                    OnPropertyChanged(nameof(HasNormalFilesPreview));
                    NotifyPopupButtonsChanged();
                }
            }
        }

        public bool HasOtherBlockingReason => !string.IsNullOrWhiteSpace(_blockingReason) && !_isMergeCommitBlock;

        public string BlockingReason
        {
            get => _blockingReason;
            private set
            {
                if (SetProperty(ref _blockingReason, value))
                    OnPropertyChanged(nameof(HasOtherBlockingReason));
            }
        }

        public string AffectedFilesHeader => App.Text("Revert.AffectedFiles", _affectedFiles.Count);

        public IReadOnlyList<MergeFilePreviewItem> AffectedFiles
        {
            get => _affectedFiles;
            private set
            {
                if (SetProperty(ref _affectedFiles, value))
                {
                    OnPropertyChanged(nameof(HasAffectedFiles));
                    OnPropertyChanged(nameof(HasNoAffectedFiles));
                    OnPropertyChanged(nameof(AffectedFilesHeader));
                }
            }
        }

        public bool HasAffectedFiles => _affectedFiles.Count > 0;

        public bool HasNoAffectedFiles => _hasPreflight && _affectedFiles.Count == 0;

        public bool HasResultView
        {
            get => _hasResultView;
            private set
            {
                if (SetProperty(ref _hasResultView, value))
                {
                    OnPropertyChanged(nameof(ShowEditorContent));
                    NotifyPopupButtonsChanged();
                }
            }
        }

        public bool ShowEditorContent => !_hasResultView;

        public bool ShowNormalPreviewContent => !_hasResultView && !_isMergeCommitBlock;

        public bool HasNormalFilesPreview => ShowNormalPreviewContent && _hasPreflight;

        public string ResultSummary
        {
            get => _resultSummary;
            private set => SetProperty(ref _resultSummary, value);
        }

        public string ResultDetail
        {
            get => _resultDetail;
            private set
            {
                if (SetProperty(ref _resultDetail, value))
                    OnPropertyChanged(nameof(HasResultDetail));
            }
        }

        public bool HasResultDetail => !string.IsNullOrWhiteSpace(_resultDetail);

        public IReadOnlyList<string> ResultLines
        {
            get => _resultLines;
            private set
            {
                if (SetProperty(ref _resultLines, value))
                    OnPropertyChanged(nameof(HasResultLines));
            }
        }

        public bool HasResultLines => _resultLines.Count > 0;

        public bool IsResultError
        {
            get => _isResultError;
            private set => SetProperty(ref _isResultError, value);
        }

        public bool IsResultWarning
        {
            get => _isResultWarning;
            private set => SetProperty(ref _isResultWarning, value);
        }

        public bool CanRefreshFromResult
        {
            get => _canRefreshFromResult;
            private set => SetProperty(ref _canRefreshFromResult, value);
        }

        public override bool ShowConfirmButton => !_hasResultView && !_isMergeCommitBlock;

        public override bool CanConfirm => !_hasResultView && !_isMergeCommitBlock && !_isLoadingPreflight && _preflight is { IsReady: true };

        public override string CancelButtonText => (_hasResultView || _isMergeCommitBlock) ? App.Text("Revert.CloseResult") : App.Text("Cancel");

        public Revert(Repository repo, Models.Commit target)
        {
            _repo = repo;
            Target = target;
            _autoCommit = true;
            _isMergeCommitBlock = target.Parents.Count > 1;
            _currentBranchDisplay = repo.CurrentBranch?.Name ?? "HEAD";
            _parentSummary = BuildParentSummary(target);

            StartPreflightRefresh();
        }

        public Task RefreshPreflightAsync()
        {
            ClearResultView();
            return StartPreflightRefresh();
        }

        public override async Task<bool> Sure()
        {
            if (_preflightTask != null)
            {
                try
                {
                    await _preflightTask;
                }
                catch
                {
                    // Handled inside RefreshPreflightCoreAsync
                }
            }

            if (_preflight == null)
                await StartPreflightRefresh();

            if (_preflight is not { IsReady: true })
            {
                ShowResultCard(
                    summary: string.IsNullOrWhiteSpace(_blockingReason) ? "当前不满足撤销提交前置条件，未执行操作。" : _blockingReason,
                    detail: string.Empty,
                    lines: [],
                    isError: true,
                    isWarning: false,
                    canRefresh: !IsMergeCommitBlock);
                return false;
            }

            using var lockWatcher = _repo.LockWatcher();
            _repo.ClearCommitMessage();
            ProgressDescription = $"Reverting commit '{Target.SHA}' ...";

            var log = _repo.CreateLog($"Revert '{Target.SHA}'");
            Use(log);

            var result = await Commands.MergeRevertService.ExecuteRevertAsync(
                _repo.FullPath,
                new Models.RevertExecutionOptions
                {
                    CommitSha = Target.SHA,
                    AutoCommit = _autoCommit,
                    Preflight = _preflight,
                },
                outerLog: log);

            log.Complete();
            _repo.MarkBranchesDirtyManually();
            _repo.MarkWorkingCopyDirtyManually();

            if (result.Succeeded)
            {
                if (!string.IsNullOrWhiteSpace(result.Summary))
                    _repo.SendNotification(result.Summary, false);
                return true;
            }

            var detailLines = new List<string>();
            if (result.ConflictedPaths.Count > 0)
                detailLines.AddRange(result.ConflictedPaths);
            if (result.InterventionReasons.Count > 0)
                detailLines.AddRange(result.InterventionReasons);

            var isWarningOnly = result.ConflictAbortedCleanly || result.BlockedBySnapshot;
            ShowResultCard(
                summary: string.IsNullOrWhiteSpace(result.Summary) ? "撤销提交未完成。" : result.Summary,
                detail: result.DiagnosticDetail ?? string.Empty,
                lines: detailLines,
                isError: !isWarningOnly,
                isWarning: isWarningOnly,
                canRefresh: result.BlockedBySnapshot || result.ConflictAbortedCleanly);
            return false;
        }

        public override void Cleanup()
        {
            Interlocked.Increment(ref _preflightVersion);
            CancelPreflight();
            base.Cleanup();
        }

        private void CancelPreflight()
        {
            try
            {
                _preflightCts?.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        }

        private Task StartPreflightRefresh()
        {
            var version = Interlocked.Increment(ref _preflightVersion);
            CancelPreflight();
            var cts = new CancellationTokenSource();
            _preflightCts = cts;
            var task = RefreshPreflightCoreAsync(version, cts);
            _preflightTask = task;
            return task;
        }

        private async Task RefreshPreflightCoreAsync(int version, CancellationTokenSource cts)
        {
            IsLoadingPreflight = true;
            BlockingReason = string.Empty;

            try
            {
                var preflight = await Commands.MergeRevertService.InspectRevertAsync(_repo.FullPath, Target.SHA, cts.Token);
                if (version != _preflightVersion || cts.IsCancellationRequested)
                    return;

                _preflight = preflight;
                HasPreflight = true;
                IsMergeCommitBlock = preflight.IsMergeCommit;
                CurrentBranchDisplay = preflight.IsDetachedHead
                    ? $"HEAD ({ShortSha(preflight.HeadSha)})"
                    : preflight.CurrentBranchName;
                ParentSummary = BuildParentSummary(preflight.TargetCommit ?? Target);
                BlockingReason = preflight.BlockingReason ?? string.Empty;
                AffectedFiles = preflight.AffectedChanges
                    .Select(MergeFilePreviewItem.FromChange)
                    .ToList();
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                if (version != _preflightVersion || cts.IsCancellationRequested)
                    return;

                _preflight = null;
                HasPreflight = false;
                BlockingReason = ex.Message;
            }
            finally
            {
                if (ReferenceEquals(_preflightCts, cts))
                    _preflightCts = null;
                cts.Dispose();

                if (version == _preflightVersion)
                {
                    IsLoadingPreflight = false;
                    NotifyPopupButtonsChanged();
                }
            }
        }

        private void ShowResultCard(
            string summary,
            string detail,
            IReadOnlyList<string> lines,
            bool isError,
            bool isWarning,
            bool canRefresh)
        {
            ResultSummary = summary;
            ResultDetail = detail;
            ResultLines = lines ?? [];
            IsResultError = isError;
            IsResultWarning = isWarning;
            CanRefreshFromResult = canRefresh;
            HasResultView = true;
        }

        private void ClearResultView()
        {
            HasResultView = false;
            ResultSummary = string.Empty;
            ResultDetail = string.Empty;
            ResultLines = [];
            IsResultError = false;
            IsResultWarning = false;
            CanRefreshFromResult = false;
        }

        private static string BuildParentSummary(Models.Commit commit)
        {
            if (commit == null || commit.Parents.Count == 0)
                return "(root commit)";
            if (commit.Parents.Count > 1)
                return $"{commit.Parents.Count} parents ({string.Join(", ", commit.Parents.Select(ShortSha))})";
            return ShortSha(commit.Parents[0]);
        }

        private static string ShortSha(string sha)
        {
            if (string.IsNullOrWhiteSpace(sha))
                return "-------";
            return sha.Length <= 8 ? sha : sha.Substring(0, 8);
        }

        private readonly Repository _repo;
        private bool _autoCommit;
        private int _preflightVersion;
        private CancellationTokenSource _preflightCts;
        private Task _preflightTask;
        private Models.RevertPreflight _preflight;
        private bool _isLoadingPreflight;
        private bool _hasPreflight;
        private string _currentBranchDisplay = string.Empty;
        private string _parentSummary = string.Empty;
        private bool _isMergeCommitBlock;
        private string _blockingReason = string.Empty;
        private IReadOnlyList<MergeFilePreviewItem> _affectedFiles = [];

        private bool _hasResultView;
        private string _resultSummary = string.Empty;
        private string _resultDetail = string.Empty;
        private IReadOnlyList<string> _resultLines = [];
        private bool _isResultError;
        private bool _isResultWarning;
        private bool _canRefreshFromResult;
    }
}
