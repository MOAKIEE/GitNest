using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media;

namespace SourceGit.ViewModels
{
    public enum MergeTestingState
    {
        Testing,
        NoConflicts,
        HasConflicts,
        Error,
    }

    public sealed class MergeModeOption
    {
        public Models.MergeMode Mode { get; }
        public string Name { get; }
        public string Desc { get; }

        public MergeModeOption(Models.MergeMode mode)
        {
            Mode = mode;
            if (mode == Models.MergeMode.FastForward)
            {
                Name = ResolveText("Merge.Mode.FastForwardOnly.Title", mode.Name);
                Desc = ResolveText("Merge.Mode.FastForwardOnly.Desc", mode.Desc);
            }
            else if (mode == Models.MergeMode.NoFastForward)
            {
                Name = ResolveText("Merge.Mode.NoFastForward.Title", mode.Name);
                Desc = ResolveText("Merge.Mode.NoFastForward.Desc", mode.Desc);
            }
            else if (mode == Models.MergeMode.Squash)
            {
                Name = ResolveText("Merge.Mode.Squash.Title", mode.Name);
                Desc = ResolveText("Merge.Mode.Squash.Desc", mode.Desc);
            }
            else if (mode == Models.MergeMode.DontCommit)
            {
                Name = ResolveText("Merge.Mode.DontCommit.Title", mode.Name);
                Desc = ResolveText("Merge.Mode.DontCommit.Desc", mode.Desc);
            }
            else
            {
                Name = ResolveText("Merge.Mode.Default.Title", mode.Name);
                Desc = ResolveText("Merge.Mode.Default.Desc", mode.Desc);
            }
        }

        private static string ResolveText(string key, string fallback)
        {
            try
            {
                var value = App.Text(key);
                return string.IsNullOrWhiteSpace(value) ? fallback : value;
            }
            catch
            {
                return fallback;
            }
        }
    }

    public sealed class MergeCommitPreviewItem
    {
        public string ShortSha { get; init; } = string.Empty;
        public string Subject { get; init; } = string.Empty;
    }

    public sealed class MergeFilePreviewItem
    {
        public string StatusCode { get; init; } = string.Empty;
        public string Path { get; init; } = string.Empty;
        public IBrush StatusBrush { get; init; } = Brushes.Gray;

        public static MergeFilePreviewItem FromChange(Models.Change change)
        {
            var code = change.Index switch
            {
                Models.ChangeState.Added => "A",
                Models.ChangeState.Deleted => "D",
                Models.ChangeState.Renamed => "R",
                Models.ChangeState.Copied => "C",
                Models.ChangeState.TypeChanged => "T",
                Models.ChangeState.Modified => "M",
                _ => "~",
            };

            IBrush brush = change.Index switch
            {
                Models.ChangeState.Added => Brushes.ForestGreen,
                Models.ChangeState.Deleted => Brushes.OrangeRed,
                Models.ChangeState.Renamed => Brushes.MediumPurple,
                _ => Brushes.DodgerBlue,
            };

            return new MergeFilePreviewItem
            {
                StatusCode = code,
                Path = change.Path ?? string.Empty,
                StatusBrush = brush,
            };
        }
    }

    public class Merge : Popup
    {
        public object Source { get; }

        public string SourceDisplayName { get; }

        public string SourceKindLabel { get; }

        public IReadOnlyList<string> TargetBranches { get; }

        public string Into
        {
            get => _into;
            set
            {
                var next = value ?? string.Empty;
                if (SetProperty(ref _into, next))
                {
                    OnPropertyChanged(nameof(DirectionSummary));
                    OnPropertyChanged(nameof(StayLocalHint));
                    ClearResultView();
                    StartPreflightRefresh();
                }
            }
        }

        public string DirectionSummary => App.Text("Merge.DirectionSummary", SourceDisplayName, _into);

        public string StayLocalHint => App.Text("Merge.StayLocalHint", _into);

        public IReadOnlyList<MergeModeOption> ModeOptions { get; }

        public MergeModeOption SelectedModeOption
        {
            get => _selectedModeOption;
            set
            {
                if (value == null || value == _selectedModeOption)
                    return;

                SetProperty(ref _selectedModeOption, value);
                Mode = value.Mode;
            }
        }

        public Models.MergeMode Mode
        {
            get => _mode;
            set
            {
                if (SetProperty(ref _mode, value))
                {
                    CanEditMessage = _mode != Models.MergeMode.Squash && _mode != Models.MergeMode.DontCommit;
                    var matched = ModeOptions.FirstOrDefault(x => x.Mode == value);
                    if (matched != null && matched != _selectedModeOption)
                        SetProperty(ref _selectedModeOption, matched, nameof(SelectedModeOption));
                    NotifyPopupButtonsChanged();
                }
            }
        }

        public bool CanEditMessage
        {
            get => _canEditMessage;
            private set => SetProperty(ref _canEditMessage, value);
        }

        public bool Edit { get; set; }

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
            private set => SetProperty(ref _hasPreflight, value);
        }

        public string SourceShortSha
        {
            get => _sourceShortSha;
            private set => SetProperty(ref _sourceShortSha, value);
        }

        public string TargetShortSha
        {
            get => _targetShortSha;
            private set => SetProperty(ref _targetShortSha, value);
        }

        public string RelationText
        {
            get => _relationText;
            private set => SetProperty(ref _relationText, value);
        }

        public IBrush RelationBrush
        {
            get => _relationBrush;
            private set => SetProperty(ref _relationBrush, value);
        }

        public bool HasBlockingReason => !string.IsNullOrWhiteSpace(_blockingReason);

        public string BlockingReason
        {
            get => _blockingReason;
            private set
            {
                if (SetProperty(ref _blockingReason, value))
                    OnPropertyChanged(nameof(HasBlockingReason));
            }
        }

        public bool NeedsCheckoutNotice
        {
            get => _needsCheckoutNotice;
            private set => SetProperty(ref _needsCheckoutNotice, value);
        }

        public string CheckoutTargetNoticeText
        {
            get => _checkoutTargetNoticeText;
            private set => SetProperty(ref _checkoutTargetNoticeText, value);
        }

        public string IncomingSummaryText
        {
            get => _incomingSummaryText;
            private set => SetProperty(ref _incomingSummaryText, value);
        }

        public IReadOnlyList<MergeCommitPreviewItem> IncomingCommits
        {
            get => _incomingCommits;
            private set
            {
                if (SetProperty(ref _incomingCommits, value))
                    OnPropertyChanged(nameof(HasIncomingCommits));
            }
        }

        public bool HasIncomingCommits => _incomingCommits.Count > 0;

        public IReadOnlyList<MergeFilePreviewItem> IncomingChanges
        {
            get => _incomingChanges;
            private set
            {
                if (SetProperty(ref _incomingChanges, value))
                    OnPropertyChanged(nameof(HasIncomingChanges));
            }
        }

        public bool HasIncomingChanges => _incomingChanges.Count > 0;

        public bool HasNoIncomingChanges => _hasPreflight && _incomingCommits.Count == 0 && _incomingChanges.Count == 0;

        public MergeTestingState TestingState
        {
            get => _testingState;
            private set => SetProperty(ref _testingState, value);
        }

        public string ConflictPredictionDetail
        {
            get => _conflictPredictionDetail;
            private set
            {
                if (SetProperty(ref _conflictPredictionDetail, value))
                    OnPropertyChanged(nameof(HasConflictPredictionDetail));
            }
        }

        public bool HasConflictPredictionDetail => !string.IsNullOrWhiteSpace(_conflictPredictionDetail);

        public string PredictedConflictsHeader => App.Text("Merge.ConflictPreviewFiles", _predictedConflictPaths.Count);

        public IReadOnlyList<string> PredictedConflictPaths
        {
            get => _predictedConflictPaths;
            private set
            {
                if (SetProperty(ref _predictedConflictPaths, value))
                {
                    OnPropertyChanged(nameof(HasPredictedConflictPaths));
                    OnPropertyChanged(nameof(PredictedConflictsHeader));
                }
            }
        }

        public bool HasPredictedConflictPaths => _predictedConflictPaths.Count > 0;

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

        public override bool ShowConfirmButton => !_hasResultView;

        public override bool CanConfirm
        {
            get
            {
                if (_hasResultView)
                    return false;

                if (_autoStartFastForward && !_hasAutoStarted)
                    return true;

                if (_isLoadingPreflight || _preflight is not { IsReady: true })
                    return false;

                if (_mode == Models.MergeMode.FastForward && _preflight.Relation != Models.MergeRelationKind.FastForward)
                    return false;

                return true;
            }
        }

        public override string CancelButtonText => _hasResultView ? App.Text("Merge.CloseResult") : App.Text("Cancel");

        public Merge(Repository repo, Models.Branch source, string into, bool forceFastForward)
            : this(repo, (object)source,
                source.IsLocal ? source.Name : source.FriendlyName,
                !string.IsNullOrWhiteSpace(source.FullName) ? source.FullName : (source.IsLocal ? $"refs/heads/{source.Name}" : source.FriendlyName),
                source.IsLocal ? Models.MergeSourceKind.LocalBranch : Models.MergeSourceKind.RemoteBranch,
                into,
                forceFastForward)
        {
        }

        public Merge(Repository repo, Models.Commit source, string into)
            : this(repo, (object)source,
                source.SHA.Length >= 8 ? source.SHA.Substring(0, 8) : source.SHA,
                source.SHA,
                Models.MergeSourceKind.Commit,
                into,
                false)
        {
        }

        public Merge(Repository repo, Models.Tag source, string into)
            : this(repo, (object)source,
                source.Name,
                $"refs/tags/{source.Name}",
                Models.MergeSourceKind.Tag,
                into,
                false)
        {
        }

        private Merge(
            Repository repo,
            object sourceObj,
            string sourceDisplayName,
            string sourceRefOrSha,
            Models.MergeSourceKind sourceKind,
            string into,
            bool forceFastForward)
        {
            _repo = repo;
            Source = sourceObj;
            SourceDisplayName = sourceDisplayName;
            _sourceRefOrSha = sourceRefOrSha;
            _sourceKind = sourceKind;
            _autoStartFastForward = forceFastForward;

            SourceKindLabel = sourceKind switch
            {
                Models.MergeSourceKind.LocalBranch => App.Text("Merge.SourceKind.LocalBranch"),
                Models.MergeSourceKind.RemoteBranch => App.Text("Merge.SourceKind.RemoteSnapshot"),
                Models.MergeSourceKind.Tag => App.Text("Merge.SourceKind.Tag"),
                _ => App.Text("Merge.SourceKind.Commit"),
            };

            TargetBranches = BuildTargetBranchList(repo, into);
            _into = !string.IsNullOrWhiteSpace(into)
                ? into
                : (repo.CurrentBranch is { IsDetachedHead: false } current ? current.Name : TargetBranches.FirstOrDefault() ?? string.Empty);

            ModeOptions = Models.MergeMode.Supported.Select(m => new MergeModeOption(m)).ToList();
            var initialMode = forceFastForward ? Models.MergeMode.FastForward : AutoSelectPreferredMode();
            _mode = initialMode;
            _selectedModeOption = ModeOptions.FirstOrDefault(x => x.Mode == initialMode) ?? ModeOptions[0];
            _canEditMessage = _mode != Models.MergeMode.Squash && _mode != Models.MergeMode.DontCommit;

            StartPreflightRefresh();
        }

        public override bool CanStartDirectly()
        {
            return _autoStartFastForward;
        }

        public Task RefreshPreflightAsync()
        {
            ClearResultView();
            return StartPreflightRefresh();
        }

        public override async Task<bool> Sure()
        {
            _hasAutoStarted = true;
            NotifyPopupButtonsChanged();

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

            if (_preflight == null || !_preflight.TargetBranchName.Equals(_into, StringComparison.Ordinal))
                await StartPreflightRefresh();

            if (_preflight is not { IsReady: true })
            {
                ShowResultCard(
                    summary: string.IsNullOrWhiteSpace(_blockingReason) ? "当前不满足合并前置条件，未执行合并。" : _blockingReason,
                    detail: string.Empty,
                    lines: [],
                    isError: true,
                    isWarning: false,
                    canRefresh: true);
                return false;
            }

            if (_mode == Models.MergeMode.FastForward && _preflight.Relation != Models.MergeRelationKind.FastForward)
            {
                ShowResultCard(
                    summary: "当前处于“仅快进 (--ff-only)”模式，但目标与来源已分叉或无新提交，未执行合并。",
                    detail: RelationText,
                    lines: [],
                    isError: true,
                    isWarning: false,
                    canRefresh: true);
                return false;
            }

            using var lockWatcher = _repo.LockWatcher();
            _repo.ClearCommitMessage();
            ProgressDescription = $"Merging '{SourceDisplayName}' into '{_into}' ...";

            var log = _repo.CreateLog($"Merge '{SourceDisplayName}' into '{_into}'");
            Use(log);

            var result = await Commands.MergeRevertService.ExecuteMergeAsync(
                _repo.FullPath,
                new Models.MergeExecutionOptions
                {
                    SourceKind = _sourceKind,
                    SourceRefOrSha = _sourceRefOrSha,
                    SourceDisplayName = SourceDisplayName,
                    TargetBranchName = _into,
                    Mode = _mode,
                    EditCommitMessage = Edit,
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
                summary: string.IsNullOrWhiteSpace(result.Summary) ? "合并未完成。" : result.Summary,
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
            var targetSnapshot = _into;
            var task = RefreshPreflightCoreAsync(version, targetSnapshot, cts);
            _preflightTask = task;
            return task;
        }

        private async Task RefreshPreflightCoreAsync(int version, string targetBranch, CancellationTokenSource cts)
        {
            IsLoadingPreflight = true;
            TestingState = MergeTestingState.Testing;
            BlockingReason = string.Empty;

            try
            {
                var preflight = await Commands.MergeRevertService.InspectMergeAsync(
                    _repo.FullPath,
                    _sourceRefOrSha,
                    targetBranch,
                    _sourceKind,
                    SourceDisplayName,
                    cts.Token);

                if (version != _preflightVersion || cts.IsCancellationRequested)
                    return;

                _preflight = preflight;
                HasPreflight = true;
                SourceShortSha = ShortSha(preflight.SourceResolvedSha);
                TargetShortSha = ShortSha(preflight.TargetResolvedSha);

                UpdateRelationDisplay(preflight.Relation);

                NeedsCheckoutNotice = preflight.NeedsCheckoutTargetBranch;
                CheckoutTargetNoticeText = preflight.NeedsCheckoutTargetBranch
                    ? $"执行前将先切换到目标分支【{preflight.TargetBranchName}】。"
                    : string.Empty;

                BlockingReason = preflight.BlockingReason ?? string.Empty;

                IncomingCommits = preflight.IncomingCommits
                    .Select(c => new MergeCommitPreviewItem
                    {
                        ShortSha = ShortSha(c.SHA),
                        Subject = c.Subject ?? string.Empty,
                    })
                    .ToList();

                IncomingChanges = preflight.IncomingChanges
                    .Select(MergeFilePreviewItem.FromChange)
                    .ToList();

                IncomingSummaryText = App.Text("Merge.IncomingCounts", IncomingCommits.Count, IncomingChanges.Count);
                OnPropertyChanged(nameof(HasNoIncomingChanges));

                PredictedConflictPaths = preflight.PredictedConflictPaths ?? [];
                ConflictPredictionDetail = preflight.ConflictPredictionDetail ?? string.Empty;
                TestingState = preflight.ConflictPrediction switch
                {
                    Models.MergeConflictPrediction.Clean => MergeTestingState.NoConflicts,
                    Models.MergeConflictPrediction.HasConflicts => MergeTestingState.HasConflicts,
                    _ => MergeTestingState.Error,
                };
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
                TestingState = MergeTestingState.Error;
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

        private void UpdateRelationDisplay(Models.MergeRelationKind relation)
        {
            switch (relation)
            {
                case Models.MergeRelationKind.FastForward:
                    RelationText = App.Text("Merge.Relation.FastForward");
                    RelationBrush = Brushes.ForestGreen;
                    break;
                case Models.MergeRelationKind.Diverged:
                    RelationText = App.Text("Merge.Relation.Diverged");
                    RelationBrush = Brushes.DodgerBlue;
                    break;
                case Models.MergeRelationKind.AlreadyUpToDate:
                    RelationText = App.Text("Merge.Relation.UpToDate");
                    RelationBrush = Brushes.Gray;
                    break;
                default:
                    RelationText = App.Text("Merge.Relation.Unrelated");
                    RelationBrush = Brushes.OrangeRed;
                    break;
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

        private Models.MergeMode AutoSelectPreferredMode()
        {
            var preferredMergeModeIdx = _repo.Settings.PreferredMergeMode;
            if (preferredMergeModeIdx < 0 || preferredMergeModeIdx >= Models.MergeMode.Supported.Length)
                return Models.MergeMode.Default;

            return Models.MergeMode.Supported[preferredMergeModeIdx];
        }

        private static List<string> BuildTargetBranchList(Repository repo, string into)
        {
            var result = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);

            foreach (var b in repo.Branches)
            {
                if (b.IsLocal && !b.IsDetachedHead && seen.Add(b.Name))
                    result.Add(b.Name);
            }

            if (!string.IsNullOrWhiteSpace(into) && !into.Contains("HEAD detached", StringComparison.OrdinalIgnoreCase) && seen.Add(into))
                result.Add(into);

            result.Sort(Models.NumericSort.Compare);
            return result;
        }

        private static string ShortSha(string sha)
        {
            if (string.IsNullOrWhiteSpace(sha))
                return "-------";
            return sha.Length <= 8 ? sha : sha.Substring(0, 8);
        }

        private readonly Repository _repo;
        private readonly string _sourceRefOrSha;
        private readonly Models.MergeSourceKind _sourceKind;
        private readonly bool _autoStartFastForward;

        private string _into = string.Empty;
        private MergeModeOption _selectedModeOption;
        private Models.MergeMode _mode;
        private bool _canEditMessage;
        private bool _hasAutoStarted;

        private int _preflightVersion;
        private CancellationTokenSource _preflightCts;
        private Task _preflightTask;
        private Models.MergePreflight _preflight;
        private bool _isLoadingPreflight;
        private bool _hasPreflight;
        private string _sourceShortSha = "-------";
        private string _targetShortSha = "-------";
        private string _relationText = string.Empty;
        private IBrush _relationBrush = Brushes.Gray;
        private string _blockingReason = string.Empty;
        private bool _needsCheckoutNotice;
        private string _checkoutTargetNoticeText = string.Empty;
        private string _incomingSummaryText = string.Empty;
        private IReadOnlyList<MergeCommitPreviewItem> _incomingCommits = [];
        private IReadOnlyList<MergeFilePreviewItem> _incomingChanges = [];
        private MergeTestingState _testingState = MergeTestingState.Testing;
        private string _conflictPredictionDetail = string.Empty;
        private IReadOnlyList<string> _predictedConflictPaths = [];

        private bool _hasResultView;
        private string _resultSummary = string.Empty;
        private string _resultDetail = string.Empty;
        private IReadOnlyList<string> _resultLines = [];
        private bool _isResultError;
        private bool _isResultWarning;
        private bool _canRefreshFromResult;
    }
}
