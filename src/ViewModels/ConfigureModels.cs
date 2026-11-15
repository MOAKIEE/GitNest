using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

using Avalonia;
using Avalonia.Collections;
using CommunityToolkit.Mvvm.ComponentModel;

namespace SourceGit.ViewModels
{
    internal static class ConfigureLocalizer
    {
        public static string Text(string key, string fallback, params object[] args)
        {
            if (Application.Current != null)
            {
                var localized = App.Text(key, args);
                if (!string.IsNullOrEmpty(localized) && !localized.Equals($"Text.{key}", StringComparison.Ordinal))
                    return localized;
            }

            if (args == null || args.Length == 0)
                return fallback;

            return string.Format(CultureInfo.CurrentCulture, fallback, args);
        }

        public static string FormatScopeBadge(Models.GitConfigScope scope, bool isIncluded)
        {
            var baseLabel = scope switch
            {
                Models.GitConfigScope.Local => "Local",
                Models.GitConfigScope.Worktree => "Worktree",
                Models.GitConfigScope.Global => "Global",
                Models.GitConfigScope.System => "System",
                Models.GitConfigScope.Command => "Runtime",
                Models.GitConfigScope.Submodule => "Submodule",
                _ => Text("Configure.Scope.Unset", "未设置"),
            };

            return isIncluded ? $"Included · {baseLabel}" : baseLabel;
        }

        public static Models.GitConfigScope ToConfigScope(Models.GitConfigWriteScope writeScope)
        {
            return writeScope switch
            {
                Models.GitConfigWriteScope.Local => Models.GitConfigScope.Local,
                Models.GitConfigWriteScope.Worktree => Models.GitConfigScope.Worktree,
                Models.GitConfigWriteScope.Global => Models.GitConfigScope.Global,
                _ => Models.GitConfigScope.Unknown,
            };
        }

        public static int GetScopeRank(Models.GitConfigScope scope)
        {
            return scope switch
            {
                Models.GitConfigScope.System => 1,
                Models.GitConfigScope.Global => 2,
                Models.GitConfigScope.Local => 3,
                Models.GitConfigScope.Submodule => 3,
                Models.GitConfigScope.Worktree => 4,
                Models.GitConfigScope.Command => 5,
                _ => 0,
            };
        }

        public static int GetWriteScopeRank(Models.GitConfigWriteScope scope) => GetScopeRank(ToConfigScope(scope));
    }

    public sealed class ConfigScopeOption
    {
        public Models.GitConfigWriteScope Scope { get; init; }
        public string Title { get; init; } = string.Empty;
        public string Description { get; init; } = string.Empty;
    }

    public sealed class ConfigChoiceOption
    {
        public string Value { get; init; } = string.Empty;
        public string Label { get; init; } = string.Empty;
    }

    public enum ConfigPendingOperationKind
    {
        None = 0,
        WriteValue = 1,
        Unset = 2,
    }

    public sealed class ConfigPendingOperation
    {
        public ConfigPendingOperationKind Kind { get; init; } = ConfigPendingOperationKind.None;
        public string Value { get; init; } = string.Empty;
        public bool AllowEmpty { get; init; } = false;
    }

    /// <summary>
    /// Tracks effective source, inheritance, override, explicit empty vs unset operations, and secret safety for a single Git config key.
    /// </summary>
    public sealed class ConfigFieldItem : ObservableObject
    {
        public ConfigFieldItem(string key, bool isBoolean = false)
        {
            Key = Models.GitConfigSnapshot.NormalizeKey(key);
            IsBoolean = isBoolean;
        }

        public event Action StateChanged;

        public string Key { get; }
        public bool IsBoolean { get; }

        public string EditText
        {
            get => _editText;
            set
            {
                var next = value ?? string.Empty;
                if (SetProperty(ref _editText, next))
                {
                    if (!_isUpdatingFromModel)
                    {
                        _pendingUnset = false;
                        _pendingExplicitEmpty = false;
                    }

                    NotifyStateChanged();
                }
            }
        }

        public bool EditBool
        {
            get => _editBool;
            set
            {
                if (SetProperty(ref _editBool, value))
                {
                    if (!_isUpdatingFromModel)
                    {
                        _pendingUnset = false;
                        _pendingExplicitEmpty = false;
                    }

                    NotifyStateChanged();
                }
            }
        }

        public bool PendingUnset
        {
            get => _pendingUnset;
            private set => SetProperty(ref _pendingUnset, value);
        }

        public bool PendingExplicitEmpty
        {
            get => _pendingExplicitEmpty;
            private set => SetProperty(ref _pendingExplicitEmpty, value);
        }

        public bool PendingForceOverride
        {
            get => _pendingForceOverride;
            private set => SetProperty(ref _pendingForceOverride, value);
        }

        public bool HasEffectiveValue { get; private set; }
        public string EffectiveDisplayValue { get; private set; } = string.Empty;
        public string EffectiveRawValue { get; private set; } = string.Empty;
        public Models.GitConfigScope EffectiveScope { get; private set; } = Models.GitConfigScope.Unknown;
        public bool EffectiveIsIncluded { get; private set; }

        public string SourceBadge { get; private set; } = ConfigureLocalizer.Text("Configure.Scope.Unset", "未设置");
        public string SourceOrigin { get; private set; } = string.Empty;

        public bool HasTargetScopeEntry { get; private set; }
        public string TargetScopeRawValue { get; private set; } = string.Empty;
        public bool HasMultipleTargetValues { get; private set; }
        public bool IsReadOnly => HasMultipleTargetValues;
        public bool IsSecret { get; private set; }
        public bool IsMaskingSecret { get; private set; }

        public bool IsInherited => !HasTargetScopeEntry && HasEffectiveValue;
        public bool IsOverriddenByHigherScope { get; private set; }
        public string HigherOverrideBadge { get; private set; } = string.Empty;

        public bool HasPendingChange => ComputePendingOperation().Kind != ConfigPendingOperationKind.None;

        public bool ShowOverrideHereButton => IsInherited && !IsReadOnly && !PendingForceOverride && !PendingExplicitEmpty && !HasPendingChange;
        public bool ShowUnsetLayerButton => HasTargetScopeEntry && !IsReadOnly && !PendingUnset;
        public bool ShowSetEmptyButton => !IsBoolean && !IsReadOnly && !PendingExplicitEmpty && (HasTargetScopeEntry || HasEffectiveValue);
        public bool ShowRevertButton => HasPendingChange || PendingForceOverride || PendingExplicitEmpty || PendingUnset;

        public bool HasStatusBanner => !string.IsNullOrEmpty(StatusMessage);
        public bool HasEffectiveSummary => HasEffectiveValue && (!HasTargetScopeEntry || IsOverriddenByHigherScope || HasMultipleTargetValues);

        public string EffectiveSummaryText
        {
            get
            {
                if (!HasEffectiveValue)
                    return string.Empty;

                var val = EffectiveRawValue.Length == 0 && EffectiveIsExplicitEntry
                    ? "\"\""
                    : EffectiveDisplayValue;
                return $"{ConfigureLocalizer.Text("Configure.Field.EffectiveValuePrefix", "当前生效：")}{val}";
            }
        }

        public bool EffectiveIsExplicitEntry { get; private set; }

        public string StatusMessage
        {
            get
            {
                if (HasMultipleTargetValues)
                    return ConfigureLocalizer.Text("Configure.Field.MultiValueReadOnly", "目标作用域存在多条定义，为保护多值配置结构已设为只读。");
                if (PendingUnset)
                    return ConfigureLocalizer.Text("Configure.Field.PendingUnsetHint", "待执行：保存时从目标作用域移除本层设置，恢复使用上层继承值。");
                if (PendingExplicitEmpty)
                    return ConfigureLocalizer.Text("Configure.Field.PendingEmptyHint", "待执行：保存时显式写入空字符串 \"\"，以屏蔽上层继承配置。");
                if (IsOverriddenByHigherScope)
                    return ConfigureLocalizer.Text("Configure.Field.HigherOverrideHint", "当前生效值由更高优先级（{0}）决定；写入目标作用域后仍会被上层覆盖。", HigherOverrideBadge);
                if (IsInherited && !PendingForceOverride)
                    return ConfigureLocalizer.Text("Configure.Field.InheritedHint", "当前使用上层继承值；修改内容或点击“为当前层覆盖”将写入目标作用域。");
                return string.Empty;
            }
        }

        public void RefreshFromSnapshot(Models.GitConfigSnapshot snapshot, Models.GitConfigWriteScope targetScope)
        {
            _targetScope = targetScope;
            _pendingUnset = false;
            _pendingExplicitEmpty = false;
            _pendingForceOverride = false;

            var effective = snapshot?.GetEffectiveEntry(Key);
            var targetEntries = snapshot?.GetEntriesInScope(Key, ConfigureLocalizer.ToConfigScope(targetScope), includeIncluded: false) ?? [];

            HasEffectiveValue = effective != null;
            EffectiveIsExplicitEntry = effective != null;
            EffectiveRawValue = effective?.Value ?? string.Empty;
            EffectiveDisplayValue = effective?.DisplayValue ?? string.Empty;
            EffectiveScope = effective?.Scope ?? Models.GitConfigScope.Unknown;
            EffectiveIsIncluded = effective?.IsIncluded ?? false;

            SourceBadge = effective == null
                ? ConfigureLocalizer.Text("Configure.Scope.Unset", "未设置")
                : ConfigureLocalizer.FormatScopeBadge(effective.Scope, effective.IsIncluded);
            SourceOrigin = effective?.Origin ?? string.Empty;

            HasTargetScopeEntry = targetEntries.Count > 0;
            HasMultipleTargetValues = targetEntries.Count > 1;
            var targetSingle = targetEntries.Count == 1 ? targetEntries[0] : null;
            TargetScopeRawValue = targetSingle?.Value ?? string.Empty;

            var targetRank = ConfigureLocalizer.GetWriteScopeRank(targetScope);
            var effectiveRank = ConfigureLocalizer.GetScopeRank(EffectiveScope);
            var overriddenByOrder = effective != null &&
                                    (effectiveRank > targetRank ||
                                     (effectiveRank == targetRank && effective.IsIncluded && targetSingle != null));
            IsOverriddenByHigherScope = overriddenByOrder;
            HigherOverrideBadge = overriddenByOrder
                ? ConfigureLocalizer.FormatScopeBadge(effective.Scope, effective.IsIncluded)
                : string.Empty;

            IsSecret = (effective?.IsSensitive ?? false) || targetEntries.Any(x => x.IsSensitive);
            var baselineEntry = targetSingle ?? effective;
            IsMaskingSecret = baselineEntry?.IsSensitive ?? false;

            if (IsBoolean)
            {
                _baselineBool = targetSingle != null
                    ? snapshot.GetEffectiveBoolInEntries(targetEntries)
                    : (snapshot?.GetEffectiveBool(Key) ?? false);
                _baselineText = _baselineBool ? "true" : "false";
                _maskedBaseline = string.Empty;
            }
            else
            {
                if (targetSingle != null)
                {
                    _baselineText = targetSingle.Value ?? string.Empty;
                    _maskedBaseline = targetSingle.DisplayValue ?? string.Empty;
                }
                else if (effective != null)
                {
                    _baselineText = effective.Value ?? string.Empty;
                    _maskedBaseline = effective.DisplayValue ?? string.Empty;
                }
                else
                {
                    _baselineText = string.Empty;
                    _maskedBaseline = string.Empty;
                }
            }

            _isUpdatingFromModel = true;
            try
            {
                if (IsBoolean)
                {
                    EditBool = _baselineBool;
                    EditText = _baselineText;
                }
                else
                {
                    EditText = IsMaskingSecret ? _maskedBaseline : _baselineText;
                    EditBool = false;
                }
            }
            finally
            {
                _isUpdatingFromModel = false;
            }

            NotifyDerivedProperties();
        }

        public void OverrideHere()
        {
            if (IsReadOnly || HasTargetScopeEntry || !HasEffectiveValue)
                return;

            _pendingUnset = false;
            _pendingExplicitEmpty = false;
            _pendingForceOverride = true;
            NotifyStateChanged();
        }

        public void MarkUnsetLayer()
        {
            if (IsReadOnly || !HasTargetScopeEntry)
                return;

            _pendingUnset = true;
            _pendingExplicitEmpty = false;
            _pendingForceOverride = false;
            NotifyStateChanged();
        }

        public void MarkExplicitEmpty()
        {
            if (IsBoolean || IsReadOnly)
                return;

            _pendingUnset = false;
            _pendingExplicitEmpty = true;
            _pendingForceOverride = false;
            _isUpdatingFromModel = true;
            try
            {
                EditText = string.Empty;
            }
            finally
            {
                _isUpdatingFromModel = false;
            }

            NotifyStateChanged();
        }

        public void Revert()
        {
            _pendingUnset = false;
            _pendingExplicitEmpty = false;
            _pendingForceOverride = false;

            _isUpdatingFromModel = true;
            try
            {
                if (IsBoolean)
                {
                    EditBool = _baselineBool;
                }
                else
                {
                    EditText = IsMaskingSecret ? _maskedBaseline : _baselineText;
                }
            }
            finally
            {
                _isUpdatingFromModel = false;
            }

            NotifyStateChanged();
        }

        public ConfigPendingOperation ComputePendingOperation()
        {
            if (HasMultipleTargetValues)
                return new ConfigPendingOperation();

            if (_pendingUnset)
            {
                return HasTargetScopeEntry
                    ? new ConfigPendingOperation { Kind = ConfigPendingOperationKind.Unset }
                    : new ConfigPendingOperation();
            }

            if (_pendingExplicitEmpty)
            {
                if (HasTargetScopeEntry && TargetScopeRawValue.Length == 0)
                    return new ConfigPendingOperation();

                return new ConfigPendingOperation
                {
                    Kind = ConfigPendingOperationKind.WriteValue,
                    Value = string.Empty,
                    AllowEmpty = true,
                };
            }

            if (_pendingForceOverride)
            {
                if (IsBoolean)
                {
                    return new ConfigPendingOperation
                    {
                        Kind = ConfigPendingOperationKind.WriteValue,
                        Value = EditBool ? "true" : "false",
                    };
                }

                var valueToWrite = IsMaskingSecret && string.Equals(EditText, _maskedBaseline, StringComparison.Ordinal)
                    ? _baselineText
                    : EditText;
                return new ConfigPendingOperation
                {
                    Kind = ConfigPendingOperationKind.WriteValue,
                    Value = valueToWrite,
                    AllowEmpty = valueToWrite.Length == 0,
                };
            }

            if (IsBoolean)
            {
                if (EditBool != _baselineBool)
                {
                    return new ConfigPendingOperation
                    {
                        Kind = ConfigPendingOperationKind.WriteValue,
                        Value = EditBool ? "true" : "false",
                    };
                }

                return new ConfigPendingOperation();
            }

            if (IsMaskingSecret && string.Equals(EditText, _maskedBaseline, StringComparison.Ordinal))
                return new ConfigPendingOperation();

            var compareBase = IsMaskingSecret ? _maskedBaseline : _baselineText;
            if (string.Equals(EditText, compareBase, StringComparison.Ordinal))
                return new ConfigPendingOperation();

            if (string.IsNullOrWhiteSpace(EditText))
            {
                return HasTargetScopeEntry
                    ? new ConfigPendingOperation { Kind = ConfigPendingOperationKind.Unset }
                    : new ConfigPendingOperation();
            }

            return new ConfigPendingOperation
            {
                Kind = ConfigPendingOperationKind.WriteValue,
                Value = EditText,
                AllowEmpty = false,
            };
        }

        public ConfigFieldDraftMemento CaptureDraftState()
        {
            return new ConfigFieldDraftMemento(
                IsBoolean,
                _editText,
                _editBool,
                _pendingUnset,
                _pendingExplicitEmpty,
                _pendingForceOverride);
        }

        public void RestoreDraftState(ConfigFieldDraftMemento state)
        {
            if (state == null)
                return;

            _isUpdatingFromModel = true;
            try
            {
                _pendingUnset = state.PendingUnset;
                _pendingExplicitEmpty = state.PendingExplicitEmpty;
                _pendingForceOverride = state.PendingForceOverride;

                if (IsBoolean)
                    SetProperty(ref _editBool, state.EditBool, nameof(EditBool));
                else
                    SetProperty(ref _editText, state.EditText ?? string.Empty, nameof(EditText));
            }
            finally
            {
                _isUpdatingFromModel = false;
            }

            NotifyStateChanged();
        }

        public string DescribeCurrentDraft()
        {
            if (_pendingUnset)
                return ConfigureLocalizer.Text("Configure.External.DraftUnset", "移除此作用域条目");
            if (_pendingExplicitEmpty)
                return ConfigureLocalizer.Text("Configure.External.DraftExplicitEmpty", "\"\"（显式空值）");
            if (IsBoolean)
                return EditBool ? "true" : "false";
            if (string.IsNullOrEmpty(EditText))
                return ConfigureLocalizer.Text("Configure.External.DraftEmpty", "（清空）");
            return IsSecret && !string.Equals(EditText, _maskedBaseline, StringComparison.Ordinal)
                ? Models.GitConfigSnapshot.RedactText(EditText)
                : EditText;
        }

        private void NotifyStateChanged()
        {
            NotifyDerivedProperties();
            StateChanged?.Invoke();
        }

        private void NotifyDerivedProperties()
        {
            OnPropertyChanged(nameof(PendingUnset));
            OnPropertyChanged(nameof(PendingExplicitEmpty));
            OnPropertyChanged(nameof(PendingForceOverride));
            OnPropertyChanged(nameof(HasEffectiveValue));
            OnPropertyChanged(nameof(EffectiveDisplayValue));
            OnPropertyChanged(nameof(EffectiveRawValue));
            OnPropertyChanged(nameof(SourceBadge));
            OnPropertyChanged(nameof(SourceOrigin));
            OnPropertyChanged(nameof(HasTargetScopeEntry));
            OnPropertyChanged(nameof(HasMultipleTargetValues));
            OnPropertyChanged(nameof(IsReadOnly));
            OnPropertyChanged(nameof(IsSecret));
            OnPropertyChanged(nameof(IsInherited));
            OnPropertyChanged(nameof(IsOverriddenByHigherScope));
            OnPropertyChanged(nameof(HigherOverrideBadge));
            OnPropertyChanged(nameof(HasPendingChange));
            OnPropertyChanged(nameof(ShowOverrideHereButton));
            OnPropertyChanged(nameof(ShowUnsetLayerButton));
            OnPropertyChanged(nameof(ShowSetEmptyButton));
            OnPropertyChanged(nameof(ShowRevertButton));
            OnPropertyChanged(nameof(HasStatusBanner));
            OnPropertyChanged(nameof(HasEffectiveSummary));
            OnPropertyChanged(nameof(EffectiveSummaryText));
            OnPropertyChanged(nameof(StatusMessage));
        }

        private Models.GitConfigWriteScope _targetScope = Models.GitConfigWriteScope.Local;
        private string _editText = string.Empty;
        private bool _editBool;
        private string _baselineText = string.Empty;
        private string _maskedBaseline = string.Empty;
        private bool _baselineBool;
        private bool _pendingUnset;
        private bool _pendingExplicitEmpty;
        private bool _pendingForceOverride;
        private bool _isUpdatingFromModel;
    }

    internal static class GitConfigSnapshotExtension
    {
        public static bool GetEffectiveBoolInEntries(this Models.GitConfigSnapshot _, IReadOnlyList<Models.GitConfigEntry> entries)
        {
            if (entries == null || entries.Count == 0)
                return false;

            var last = entries[^1];
            if (!last.HasValue)
                return true;

            var trimmed = last.Value.Trim().ToLowerInvariant();
            return trimmed is "true" or "yes" or "on" or "1";
        }
    }

    public sealed class RemoteConfigDraft : ObservableObject
    {
        public event Action StateChanged;

        public string OriginalName { get; init; } = string.Empty;
        public string OriginalPrimaryFetchUrl { get; init; } = string.Empty;
        public string MaskedPrimaryFetchUrl { get; init; } = string.Empty;
        public bool IsFetchUrlMasked { get; init; }

        public string OriginalPrimaryPushUrl { get; init; } = string.Empty;
        public string MaskedPrimaryPushUrl { get; init; } = string.Empty;
        public bool IsPushUrlMasked { get; init; }

        public int ExtraFetchUrlCount { get; init; }
        public int ExtraPushUrlCount { get; init; }
        public int FetchRefspecCount { get; init; }
        public string SshKeyDisplay { get; init; } = string.Empty;
        public bool DisableAutoFetch { get; init; }
        public bool IsNew { get; init; }

        public string Name
        {
            get => _name;
            set
            {
                if (SetProperty(ref _name, value ?? string.Empty))
                    NotifyChange();
            }
        }

        public string PrimaryFetchUrl
        {
            get => _primaryFetchUrl;
            set
            {
                if (SetProperty(ref _primaryFetchUrl, value ?? string.Empty))
                    NotifyChange();
            }
        }

        public string PrimaryPushUrl
        {
            get => _primaryPushUrl;
            set
            {
                if (SetProperty(ref _primaryPushUrl, value ?? string.Empty))
                    NotifyChange();
            }
        }

        public bool IsMarkedForRemoval
        {
            get => _isMarkedForRemoval;
            set
            {
                if (SetProperty(ref _isMarkedForRemoval, value))
                    NotifyChange();
            }
        }

        public bool HasSensitiveUrl => IsFetchUrlMasked || IsPushUrlMasked;
        public bool HasPreservedDetails => !IsNew && (ExtraFetchUrlCount > 0 || ExtraPushUrlCount > 0 || FetchRefspecCount > 0);
        public string PreservedDetailsText => ConfigureLocalizer.Text(
            "Configure.Remotes.ExtraUrlsHint",
            "编辑主 URL 时保留原有规则：附加获取 URL {0} 条、附加推送 URL {1} 条、fetch refspec {2} 条",
            ExtraFetchUrlCount,
            ExtraPushUrlCount,
            FetchRefspecCount);

        public bool HasSshKey => !string.IsNullOrEmpty(SshKeyDisplay);

        public string PendingBadgeText
        {
            get
            {
                if (IsMarkedForRemoval)
                    return ConfigureLocalizer.Text("Configure.Remotes.PendingRemoveBadge", "待删除");
                if (IsNew)
                    return ConfigureLocalizer.Text("Configure.Remotes.PendingAddBadge", "待添加");
                return string.Empty;
            }
        }

        public bool HasPendingBadge => IsMarkedForRemoval || IsNew;

        public string DisplayHeader
        {
            get
            {
                var currentName = string.IsNullOrWhiteSpace(Name) ? OriginalName : Name.Trim();
                return string.IsNullOrEmpty(PendingBadgeText) ? currentName : $"{currentName} [{PendingBadgeText}]";
            }
        }

        public string ToggleRemoveLabel => IsMarkedForRemoval
            ? ConfigureLocalizer.Text("Configure.Remotes.UndoRemove", "撤销删除")
            : ConfigureLocalizer.Text("Configure.Remotes.StageRemove", "标记删除远端");

        public bool IsRenamed => !IsNew && !IsMarkedForRemoval && !string.Equals(Name.Trim(), OriginalName, StringComparison.Ordinal);

        public bool IsFetchUrlEdited
        {
            get
            {
                if (IsNew || IsMarkedForRemoval)
                    return false;
                var current = PrimaryFetchUrl.Trim();
                if (IsFetchUrlMasked && string.Equals(current, MaskedPrimaryFetchUrl, StringComparison.Ordinal))
                    return false;
                return !string.Equals(current, OriginalPrimaryFetchUrl, StringComparison.Ordinal);
            }
        }

        public bool IsPushUrlEdited
        {
            get
            {
                if (IsNew || IsMarkedForRemoval)
                    return false;
                var current = PrimaryPushUrl.Trim();
                if (IsPushUrlMasked && string.Equals(current, MaskedPrimaryPushUrl, StringComparison.Ordinal))
                    return false;
                return !string.Equals(current, OriginalPrimaryPushUrl, StringComparison.Ordinal);
            }
        }

        public bool HasPendingChange => IsNew
            ? !IsMarkedForRemoval
            : (IsMarkedForRemoval || IsRenamed || IsFetchUrlEdited || IsPushUrlEdited);

        public static RemoteConfigDraft FromModel(Models.GitConfigRemote remote)
        {
            var firstFetch = remote.FetchUrls.FirstOrDefault();
            var firstPush = remote.PushUrls.FirstOrDefault();

            var draft = new RemoteConfigDraft
            {
                OriginalName = remote.Name,
                OriginalPrimaryFetchUrl = firstFetch?.Value ?? string.Empty,
                MaskedPrimaryFetchUrl = firstFetch?.DisplayValue ?? string.Empty,
                IsFetchUrlMasked = firstFetch?.IsSensitive ?? false,
                OriginalPrimaryPushUrl = firstPush?.Value ?? string.Empty,
                MaskedPrimaryPushUrl = firstPush?.DisplayValue ?? string.Empty,
                IsPushUrlMasked = firstPush?.IsSensitive ?? false,
                ExtraFetchUrlCount = Math.Max(0, remote.FetchUrls.Count - 1),
                ExtraPushUrlCount = Math.Max(0, remote.PushUrls.Count - 1),
                FetchRefspecCount = remote.FetchRefspecs.Count,
                SshKeyDisplay = remote.SshKey?.DisplayValue ?? string.Empty,
                DisableAutoFetch = remote.DisableAutoFetch != null &&
                                   (!remote.DisableAutoFetch.HasValue ||
                                    remote.DisableAutoFetch.Value.Trim().Equals("true", StringComparison.OrdinalIgnoreCase)),
                IsNew = false,
            };

            draft._name = remote.Name;
            draft._primaryFetchUrl = draft.IsFetchUrlMasked ? draft.MaskedPrimaryFetchUrl : draft.OriginalPrimaryFetchUrl;
            draft._primaryPushUrl = draft.IsPushUrlMasked ? draft.MaskedPrimaryPushUrl : draft.OriginalPrimaryPushUrl;
            return draft;
        }

        public static RemoteConfigDraft CreateNew(string name, string fetchUrl)
        {
            var draft = new RemoteConfigDraft
            {
                OriginalName = name.Trim(),
                IsNew = true,
            };
            draft._name = name.Trim();
            draft._primaryFetchUrl = fetchUrl.Trim();
            return draft;
        }

        public RemoteConfigDraftMemento CaptureDraftState()
        {
            return new RemoteConfigDraftMemento(
                OriginalName,
                IsNew,
                _name,
                _primaryFetchUrl,
                _primaryPushUrl,
                _isMarkedForRemoval);
        }

        public void RestoreDraftState(RemoteConfigDraftMemento state)
        {
            if (state == null)
                return;

            _name = state.Name ?? string.Empty;
            _primaryFetchUrl = state.PrimaryFetchUrl ?? string.Empty;
            _primaryPushUrl = state.PrimaryPushUrl ?? string.Empty;
            _isMarkedForRemoval = state.IsMarkedForRemoval;

            OnPropertyChanged(nameof(Name));
            OnPropertyChanged(nameof(PrimaryFetchUrl));
            OnPropertyChanged(nameof(PrimaryPushUrl));
            OnPropertyChanged(nameof(IsMarkedForRemoval));
            NotifyChange();
        }

        private void NotifyChange()
        {
            OnPropertyChanged(nameof(PendingBadgeText));
            OnPropertyChanged(nameof(HasPendingBadge));
            OnPropertyChanged(nameof(DisplayHeader));
            OnPropertyChanged(nameof(ToggleRemoveLabel));
            OnPropertyChanged(nameof(HasPendingChange));
            StateChanged?.Invoke();
        }

        private string _name = string.Empty;
        private string _primaryFetchUrl = string.Empty;
        private string _primaryPushUrl = string.Empty;
        private bool _isMarkedForRemoval;
    }

    public sealed class ConfigReadOnlyEntry
    {
        public string Label { get; init; } = string.Empty;
        public string Value { get; init; } = string.Empty;
        public string SourceBadge { get; init; } = string.Empty;
        public string Origin { get; init; } = string.Empty;
        public bool HasOrigin => !string.IsNullOrEmpty(Origin);
    }

    public enum ConfigFeedbackSeverity
    {
        None = 0,
        Info = 1,
        Success = 2,
        Warning = 3,
        Error = 4,
    }

    public sealed class ConfigSaveItemResult
    {
        public string TargetName { get; init; } = string.Empty;
        public bool Succeeded { get; init; }
        public bool SkippedConflict { get; init; }
        public bool ShadowedByHigherScope { get; init; }
        public string Detail { get; init; } = string.Empty;

        public string DisplayLine => Succeeded
            ? $"✓ {TargetName}{(string.IsNullOrEmpty(Detail) ? string.Empty : $" — {Detail}")}"
            : $"✗ {TargetName}: {Detail}";
    }

    public sealed class ConfigSaveSummary
    {
        public ConfigFeedbackSeverity Severity { get; init; } = ConfigFeedbackSeverity.None;
        public string Headline { get; init; } = string.Empty;
        public bool IsVisible => Severity != ConfigFeedbackSeverity.None && !string.IsNullOrEmpty(Headline);
        public bool IsWarning => Severity == ConfigFeedbackSeverity.Warning;
        public bool IsError => Severity == ConfigFeedbackSeverity.Error;
        public bool IsSuccess => Severity == ConfigFeedbackSeverity.Success;

        public int SucceededCount { get; init; }
        public int FailedCount { get; init; }
        public int ConflictCount { get; init; }
        public int ShadowedCount { get; init; }

        public AvaloniaList<string> FailureAndConflictLines { get; } = [];
        public AvaloniaList<string> ShadowedLines { get; } = [];
        public AvaloniaList<ConfigSaveItemResult> ItemResults { get; } = [];

        public bool HasFailureAndConflictLines => FailureAndConflictLines.Count > 0;
        public bool HasShadowedLines => ShadowedLines.Count > 0;
        public bool ShowSecondaryShadowedHeader => HasShadowedLines && (FailedCount > 0 || ConflictCount > 0);
    }

    public sealed record ConfigFieldDraftMemento(
        bool IsBoolean,
        string EditText,
        bool EditBool,
        bool PendingUnset,
        bool PendingExplicitEmpty,
        bool PendingForceOverride);

    public sealed record RemoteConfigDraftMemento(
        string OriginalName,
        bool IsNew,
        string Name,
        string PrimaryFetchUrl,
        string PrimaryPushUrl,
        bool IsMarkedForRemoval);

    public sealed class ExternalConfigDiffItem
    {
        public string TargetName { get; init; } = string.Empty;
        public string ScopeLabel { get; init; } = string.Empty;
        public string PreviousDisplay { get; init; } = string.Empty;
        public string ExternalDisplay { get; init; } = string.Empty;
        public string DraftDisplay { get; init; } = string.Empty;
        public string ExternalOrigin { get; init; } = string.Empty;
        public bool HasUserConflict { get; init; }

        public string SummaryLine
        {
            get
            {
                var originSuffix = string.IsNullOrWhiteSpace(ExternalOrigin) ? string.Empty : $" ({ExternalOrigin})";
                if (HasUserConflict)
                    return $"[草稿冲突] {TargetName} [{ScopeLabel}]: 原值 {PreviousDisplay} -> 外部 {ExternalDisplay}{originSuffix} | 草稿 {DraftDisplay}";

                return $"[外部变化] {TargetName} [{ScopeLabel}]: {PreviousDisplay} -> {ExternalDisplay}{originSuffix}";
            }
        }
    }
}
