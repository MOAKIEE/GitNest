using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Collections;
using Avalonia.Threading;

namespace SourceGit.ViewModels
{
    public sealed class IgnorePresetItem
    {
        public Models.IgnoreRulePresetKind Kind { get; init; }
        public string Pattern { get; init; } = string.Empty;
        public string DisplayLabel { get; init; } = string.Empty;
        public bool IsCustom { get; init; }

        public override string ToString() => DisplayLabel;
    }

    public sealed class IgnoreTargetItem
    {
        public Models.IgnoreRuleTargetOption Option { get; init; }
        public string DisplayText => Option?.DisplayName ?? string.Empty;

        public override string ToString() => DisplayText;
    }

    public class AddToIgnore : Popup
    {
        public AvaloniaList<IgnoreTargetItem> SupportedTargets { get; } = [];
        public AvaloniaList<IgnorePresetItem> Presets { get; } = [];
        public AvaloniaList<string> MatchedUntrackedPaths { get; } = [];
        public AvaloniaList<string> MatchedTrackedPaths { get; } = [];
        public AvaloniaList<string> Warnings { get; } = [];

        public bool HasPresets => Presets.Count > 1;

        public IgnoreTargetItem SelectedTarget
        {
            get => _selectedTarget;
            set
            {
                if (SetProperty(ref _selectedTarget, value))
                {
                    RebuildPresetsForSelectedTarget(keepCustomTextIfEditing: _isCustomMode);
                    _ = InspectPatternAsync();
                }
            }
        }

        public IgnorePresetItem SelectedPreset
        {
            get => _selectedPreset;
            set
            {
                if (SetProperty(ref _selectedPreset, value) && value != null)
                {
                    _isCustomMode = value.IsCustom;
                    if (!value.IsCustom)
                    {
                        _suppressCustomReset = true;
                        Pattern = value.Pattern;
                        _suppressCustomReset = false;
                    }
                    OnPropertyChanged(nameof(IsCustomPatternMode));
                }
            }
        }

        public bool IsCustomPatternMode => _isCustomMode || Presets.Count <= 1;

        public string Pattern
        {
            get => _pattern;
            set
            {
                if (SetProperty(ref _pattern, value))
                {
                    if (!_suppressCustomReset &&
                        _selectedPreset is { IsCustom: false } &&
                        !string.Equals(_selectedPreset.Pattern, value, StringComparison.Ordinal))
                    {
                        var custom = Presets.FirstOrDefault(p => p.IsCustom);
                        if (custom != null)
                        {
                            _isCustomMode = true;
                            _selectedPreset = custom;
                            OnPropertyChanged(nameof(SelectedPreset));
                            OnPropertyChanged(nameof(IsCustomPatternMode));
                        }
                    }

                    NotifyPopupButtonsChanged();
                    _ = InspectPatternAsync();
                }
            }
        }

        public bool IsLoadingInspection
        {
            get => _isLoadingInspection;
            private set
            {
                if (SetProperty(ref _isLoadingInspection, value))
                    NotifyPopupButtonsChanged();
            }
        }

        public bool HasInspection => _inspection != null;
        public string EffectivePattern => _inspection?.EffectivePattern ?? string.Empty;
        public string ValidationError => _inspection?.ValidationError ?? string.Empty;
        public bool HasValidationError => _inspection is { IsValid: false } && !string.IsNullOrWhiteSpace(_inspection.ValidationError);
        public bool AlreadyExists => _inspection is { AlreadyExists: true };
        public bool IsLocalTargetSelected => _selectedTarget?.Option?.IsLocalOnly == true;

        public string TargetScopeHint => IsLocalTargetSelected
            ? App.Text("AddToIgnore.ScopeLocalHint")
            : App.Text("AddToIgnore.ScopeRepoHint");

        public bool HasMatchedUntracked => MatchedUntrackedPaths.Count > 0;
        public string MatchedUntrackedTitle => App.Text("AddToIgnore.MatchedUntrackedTitle", MatchedUntrackedPaths.Count);

        public bool HasMatchedTracked => MatchedTrackedPaths.Count > 0;
        public string MatchedTrackedTitle => App.Text("AddToIgnore.MatchedTrackedTitle", MatchedTrackedPaths.Count);

        public bool HasWarnings => Warnings.Count > 0;

        public bool HasResultView
        {
            get => _hasResultView;
            private set
            {
                if (SetProperty(ref _hasResultView, value))
                    NotifyPopupButtonsChanged();
            }
        }

        public string ResultSummary
        {
            get => _resultSummary;
            private set => SetProperty(ref _resultSummary, value);
        }

        public override bool ShowConfirmButton => !HasResultView;

        public override bool CanConfirm =>
            !HasResultView &&
            !IsLoadingInspection &&
            _selectedTarget?.Option != null &&
            !string.IsNullOrWhiteSpace(_pattern) &&
            _inspection is { IsValid: true, AlreadyExists: false };

        public override string ConfirmButtonText => App.Text("AddToIgnore.Confirm");
        public override string CancelButtonText => HasResultView ? App.Text("Close") : App.Text("Cancel");

        public AddToIgnore(Repository repo, string pattern)
            : this(
                repo,
                DetectSelectedRelativePath(repo, pattern, out var detectedIsDir, out var isGlob),
                detectedIsDir,
                isGlob ? pattern : null)
        {
        }

        public AddToIgnore(
            Repository repo,
            string selectedRelativePath,
            bool isDirectory,
            string initialCustomPattern = null)
        {
            _repo = repo;
            _selectedRelativePath = (selectedRelativePath ?? string.Empty).Trim().Replace('\\', '/').TrimEnd('/');
            _isDirectory = isDirectory;
            _initialCustomPattern = initialCustomPattern ?? string.Empty;
            _isCustomMode = !string.IsNullOrEmpty(_initialCustomPattern);

            _ = LoadTargetsAndInspectAsync();
        }

        public override async Task<bool> Sure()
        {
            if (HasResultView || _selectedTarget?.Option == null)
                return false;

            using var lockWatcher = _repo.LockWatcher();
            ProgressDescription = App.Text("AddToIgnore.Applying");

            var log = _repo.CreateLog("Add To Ignore");
            Use(log);

            var res = await Commands.IgnoreRuleService.ApplyAsync(
                _repo.FullPath,
                _pattern,
                _selectedTarget.Option,
                journalStore: Models.OperationJournalStore.Instance,
                outerLog: log).ConfigureAwait(false);

            log.Complete();
            _repo.MarkWorkingCopyDirtyManually();
            _ = _repo.RefreshRecoveryStatusAsync();

            if (res.Success && !res.SkippedDuplicate)
            {
                Dispatcher.UIThread.Post(() => _repo.SendNotification(res.Summary, false));
                return true;
            }

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                ResultSummary = string.IsNullOrWhiteSpace(res.Summary)
                    ? App.Text("AddToIgnore.FailedFallback")
                    : res.Summary;
                HasResultView = true;
            });
            return false;
        }

        private async Task LoadTargetsAndInspectAsync()
        {
            try
            {
                var targets = await Commands.IgnoreRuleService.GetSupportedTargetsAsync(
                    _repo.FullPath,
                    string.IsNullOrEmpty(_selectedRelativePath) ? null : _selectedRelativePath,
                    _isDirectory).ConfigureAwait(false);

                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    SupportedTargets.Clear();
                    foreach (var t in targets)
                        SupportedTargets.Add(new IgnoreTargetItem { Option = t });

                    _selectedTarget = SupportedTargets.FirstOrDefault();
                    OnPropertyChanged(nameof(SelectedTarget));
                    OnPropertyChanged(nameof(IsLocalTargetSelected));
                    OnPropertyChanged(nameof(TargetScopeHint));

                    RebuildPresetsForSelectedTarget(keepCustomTextIfEditing: _isCustomMode);
                });

                await InspectPatternAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    Warnings.Clear();
                    Warnings.Add(ex.Message);
                    OnPropertyChanged(nameof(HasWarnings));
                });
            }
        }

        private void RebuildPresetsForSelectedTarget(bool keepCustomTextIfEditing)
        {
            var baseDir = _selectedTarget?.Option?.BaseRelativeDir ?? string.Empty;
            var prevPresetKind = _selectedPreset?.Kind;

            Presets.Clear();

            if (!string.IsNullOrEmpty(_selectedRelativePath))
            {
                var built = Commands.IgnoreRuleService.BuildPresetOptions(_selectedRelativePath, _isDirectory, baseDir);
                foreach (var opt in built)
                {
                    Presets.Add(new IgnorePresetItem
                    {
                        Kind = opt.Kind,
                        Pattern = opt.Pattern,
                        DisplayLabel = opt.DisplayLabel,
                        IsCustom = false,
                    });
                }
            }

            Presets.Add(new IgnorePresetItem
            {
                Kind = Models.IgnoreRulePresetKind.ExactFile,
                Pattern = string.Empty,
                DisplayLabel = App.Text("AddToIgnore.Preset.Custom"),
                IsCustom = true,
            });

            OnPropertyChanged(nameof(HasPresets));

            if (!string.IsNullOrEmpty(_initialCustomPattern) && keepCustomTextIfEditing)
            {
                var custom = Presets.First(p => p.IsCustom);
                _selectedPreset = custom;
                _isCustomMode = true;
                _suppressCustomReset = true;
                _pattern = _initialCustomPattern;
                _suppressCustomReset = false;
                OnPropertyChanged(nameof(SelectedPreset));
                OnPropertyChanged(nameof(Pattern));
                OnPropertyChanged(nameof(IsCustomPatternMode));
                return;
            }

            if (keepCustomTextIfEditing)
            {
                _selectedPreset = Presets.First(p => p.IsCustom);
                OnPropertyChanged(nameof(SelectedPreset));
                OnPropertyChanged(nameof(IsCustomPatternMode));
                return;
            }

            var match = prevPresetKind.HasValue
                ? Presets.FirstOrDefault(p => !p.IsCustom && p.Kind == prevPresetKind.Value)
                : null;
            match ??= Presets.FirstOrDefault();

            _selectedPreset = match;
            _isCustomMode = match?.IsCustom ?? true;
            if (match != null && !match.IsCustom)
            {
                _suppressCustomReset = true;
                _pattern = match.Pattern;
                _suppressCustomReset = false;
            }

            OnPropertyChanged(nameof(SelectedPreset));
            OnPropertyChanged(nameof(Pattern));
            OnPropertyChanged(nameof(IsCustomPatternMode));
        }

        private async Task InspectPatternAsync()
        {
            var seq = Interlocked.Increment(ref _inspectSeq);
            var target = _selectedTarget?.Option;
            var rawPattern = _pattern;

            if (target == null)
                return;

            IsLoadingInspection = true;
            try
            {
                var inspection = await Commands.IgnoreRuleService.InspectAsync(
                    _repo.FullPath,
                    rawPattern,
                    target).ConfigureAwait(false);

                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    if (seq != Volatile.Read(ref _inspectSeq))
                        return;

                    ApplyInspection(inspection);
                });
            }
            catch (Exception ex)
            {
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    if (seq != Volatile.Read(ref _inspectSeq))
                        return;

                    _inspection = null;
                    Warnings.Clear();
                    Warnings.Add(ex.Message);
                    OnPropertyChanged(nameof(HasWarnings));
                    OnPropertyChanged(nameof(HasInspection));
                    NotifyPopupButtonsChanged();
                });
            }
            finally
            {
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    if (seq == Volatile.Read(ref _inspectSeq))
                    {
                        IsLoadingInspection = false;
                        NotifyPopupButtonsChanged();
                    }
                });
            }
        }

        private void ApplyInspection(Models.IgnoreRuleInspection inspection)
        {
            _inspection = inspection;

            MatchedUntrackedPaths.Clear();
            foreach (var p in inspection.MatchedUntrackedPaths)
                MatchedUntrackedPaths.Add(p);

            MatchedTrackedPaths.Clear();
            foreach (var p in inspection.MatchedTrackedPaths)
                MatchedTrackedPaths.Add(p);

            Warnings.Clear();
            foreach (var conflict in inspection.NegationConflicts)
                Warnings.Add($"[反向忽略 ! 冲突] {conflict}");
            foreach (var w in inspection.Warnings)
            {
                if (MatchedTrackedPaths.Count > 0 && w.Contains("git rm --cached", StringComparison.OrdinalIgnoreCase))
                    continue;
                Warnings.Add(w);
            }

            OnPropertyChanged(nameof(HasInspection));
            OnPropertyChanged(nameof(EffectivePattern));
            OnPropertyChanged(nameof(ValidationError));
            OnPropertyChanged(nameof(HasValidationError));
            OnPropertyChanged(nameof(AlreadyExists));
            OnPropertyChanged(nameof(HasMatchedUntracked));
            OnPropertyChanged(nameof(MatchedUntrackedTitle));
            OnPropertyChanged(nameof(HasMatchedTracked));
            OnPropertyChanged(nameof(MatchedTrackedTitle));
            OnPropertyChanged(nameof(HasWarnings));
            OnPropertyChanged(nameof(IsLocalTargetSelected));
            OnPropertyChanged(nameof(TargetScopeHint));
            NotifyPopupButtonsChanged();
        }

        private static string DetectSelectedRelativePath(Repository repo, string raw, out bool isDirectory, out bool isGlobPattern)
        {
            isDirectory = false;
            isGlobPattern = false;
            if (string.IsNullOrWhiteSpace(raw))
                return string.Empty;

            var normalized = raw.Trim().Replace('\\', '/');
            isDirectory = normalized.EndsWith('/');
            var trimmed = normalized.TrimEnd('/');
            if (repo != null && !string.IsNullOrEmpty(trimmed))
            {
                var full = Native.OS.GetAbsPath(repo.FullPath, trimmed);
                if (Directory.Exists(full))
                {
                    isDirectory = true;
                    return trimmed;
                }

                if (File.Exists(full))
                {
                    isDirectory = false;
                    return trimmed;
                }
            }

            if (raw.Contains('*') || raw.Contains('?') || raw.StartsWith('!'))
            {
                isGlobPattern = true;
                return string.Empty;
            }

            return trimmed;
        }

        private readonly Repository _repo;
        private readonly string _selectedRelativePath;
        private readonly bool _isDirectory;
        private readonly string _initialCustomPattern;

        private IgnoreTargetItem _selectedTarget;
        private IgnorePresetItem _selectedPreset;
        private Models.IgnoreRuleInspection _inspection;
        private string _pattern = string.Empty;
        private bool _isCustomMode = false;
        private bool _suppressCustomReset = false;
        private bool _isLoadingInspection = false;
        private bool _hasResultView = false;
        private string _resultSummary = string.Empty;
        private int _inspectSeq = 0;
    }
}
