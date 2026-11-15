using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Avalonia;
using Avalonia.Collections;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;

namespace SourceGit.ViewModels
{
    public class RepositoryConfigure : ObservableObject
    {
        public RepositoryConfigure(Repository repo)
        {
            _repo = repo;

            UserNameField = new ConfigFieldItem("user.name");
            UserEmailField = new ConfigFieldItem("user.email");
            HttpProxyField = new ConfigFieldItem("http.proxy");
            HttpsProxyField = new ConfigFieldItem("https.proxy");
            SigningFormatField = new ConfigFieldItem("gpg.format");
            SigningKeyField = new ConfigFieldItem("user.signingkey");
            SshAllowedSignersField = new ConfigFieldItem("gpg.ssh.allowedsignersfile");
            CommitSignField = new ConfigFieldItem("commit.gpgsign", isBoolean: true);
            TagSignField = new ConfigFieldItem("tag.gpgsign", isBoolean: true);
            AutoCrlfField = new ConfigFieldItem("core.autocrlf");
            FetchPruneField = new ConfigFieldItem("fetch.prune", isBoolean: true);

            _fields =
            [
                UserNameField,
                UserEmailField,
                HttpProxyField,
                HttpsProxyField,
                SigningFormatField,
                SigningKeyField,
                SshAllowedSignersField,
                CommitSignField,
                TagSignField,
                AutoCrlfField,
                FetchPruneField,
            ];

            foreach (var field in _fields)
                field.StateChanged += OnAnyDraftStateChanged;

            foreach (var remote in _repo.Remotes)
                Remotes.Add(remote.Name);

            foreach (var rule in _repo.IssueTrackers)
            {
                IssueTrackers.Add(new Models.IssueTracker
                {
                    IsShared = rule.IsShared,
                    Name = rule.Name,
                    RegexString = rule.RegexString,
                    URLTemplate = rule.URLTemplate,
                });
            }

            RebuildScopeOptions(worktreeEnabled: false);
            RebuildSigningFormatOptions(currentFormat: "openpgp");
            RebuildAutoCrlfOptions(currentValue: string.Empty);

            _loadTask = ReloadAsync();
        }

        public ConfigFieldItem UserNameField { get; }
        public ConfigFieldItem UserEmailField { get; }
        public ConfigFieldItem HttpProxyField { get; }
        public ConfigFieldItem HttpsProxyField { get; }
        public ConfigFieldItem SigningFormatField { get; }
        public ConfigFieldItem SigningKeyField { get; }
        public ConfigFieldItem SshAllowedSignersField { get; }
        public ConfigFieldItem CommitSignField { get; }
        public ConfigFieldItem TagSignField { get; }
        public ConfigFieldItem AutoCrlfField { get; }
        public ConfigFieldItem FetchPruneField { get; }

        public string UserName
        {
            get => UserNameField.EditText;
            set
            {
                if (!string.Equals(UserNameField.EditText, value ?? string.Empty, StringComparison.Ordinal))
                {
                    UserNameField.EditText = value ?? string.Empty;
                    OnPropertyChanged();
                }
            }
        }

        public string UserEmail
        {
            get => UserEmailField.EditText;
            set
            {
                if (!string.Equals(UserEmailField.EditText, value ?? string.Empty, StringComparison.Ordinal))
                {
                    UserEmailField.EditText = value ?? string.Empty;
                    OnPropertyChanged();
                }
            }
        }

        public string HttpProxy
        {
            get => HttpProxyField.EditText;
            set
            {
                if (!string.Equals(HttpProxyField.EditText, value ?? string.Empty, StringComparison.Ordinal))
                {
                    HttpProxyField.EditText = value ?? string.Empty;
                    OnPropertyChanged();
                }
            }
        }

        public bool GPGCommitSigningEnabled
        {
            get => CommitSignField.EditBool;
            set
            {
                if (CommitSignField.EditBool != value)
                {
                    CommitSignField.EditBool = value;
                    OnPropertyChanged();
                }
            }
        }

        public bool GPGTagSigningEnabled
        {
            get => TagSignField.EditBool;
            set
            {
                if (TagSignField.EditBool != value)
                {
                    TagSignField.EditBool = value;
                    OnPropertyChanged();
                }
            }
        }

        public string GPGUserSigningKey
        {
            get => SigningKeyField.EditText;
            set
            {
                if (!string.Equals(SigningKeyField.EditText, value ?? string.Empty, StringComparison.Ordinal))
                {
                    SigningKeyField.EditText = value ?? string.Empty;
                    OnPropertyChanged();
                }
            }
        }

        public bool EnablePruneOnFetch
        {
            get => FetchPruneField.EditBool;
            set
            {
                if (FetchPruneField.EditBool != value)
                {
                    FetchPruneField.EditBool = value;
                    OnPropertyChanged();
                }
            }
        }

        public AvaloniaList<string> Remotes { get; } = [];

        public string DefaultRemote
        {
            get => _repo.Settings.DefaultRemote;
            set
            {
                if (_repo.Settings.DefaultRemote != value)
                {
                    _repo.Settings.DefaultRemote = value;
                    OnPropertyChanged();
                }
            }
        }

        public int PreferredMergeMode
        {
            get => _repo.Settings.PreferredMergeMode;
            set
            {
                if (_repo.Settings.PreferredMergeMode != value)
                {
                    _repo.Settings.PreferredMergeMode = value;
                    OnPropertyChanged();
                }
            }
        }

        public string ConventionalTypesOverride
        {
            get => _repo.Settings.ConventionalTypesOverride;
            set
            {
                if (_repo.Settings.ConventionalTypesOverride != value)
                {
                    _repo.Settings.ConventionalTypesOverride = value;
                    OnPropertyChanged();
                }
            }
        }

        public bool AskBeforeAutoUpdatingSubmodules
        {
            get => _repo.Settings.AskBeforeAutoUpdatingSubmodules;
            set
            {
                if (_repo.Settings.AskBeforeAutoUpdatingSubmodules != value)
                {
                    _repo.Settings.AskBeforeAutoUpdatingSubmodules = value;
                    OnPropertyChanged();
                }
            }
        }

        public bool EnableRecursiveWhenAutoUpdatingSubmodules
        {
            get => _repo.Settings.EnableRecursiveWhenAutoUpdatingSubmodules;
            set
            {
                if (_repo.Settings.EnableRecursiveWhenAutoUpdatingSubmodules != value)
                {
                    _repo.Settings.EnableRecursiveWhenAutoUpdatingSubmodules = value;
                    OnPropertyChanged();
                }
            }
        }

        public AvaloniaList<Models.CommitTemplate> CommitTemplates => _repo.Settings.CommitTemplates;

        public Models.CommitTemplate SelectedCommitTemplate
        {
            get => _selectedCommitTemplate;
            set => SetProperty(ref _selectedCommitTemplate, value);
        }

        public AvaloniaList<Models.IssueTracker> IssueTrackers { get; } = [];

        public Models.IssueTracker SelectedIssueTracker
        {
            get => _selectedIssueTracker;
            set => SetProperty(ref _selectedIssueTracker, value);
        }

        public AvaloniaList<ConfigScopeOption> AvailableWriteScopes { get; } = [];

        public ConfigScopeOption SelectedScopeOption
        {
            get => AvailableWriteScopes.FirstOrDefault(x => x.Scope == _targetScope);
            set
            {
                if (value != null)
                    TargetScope = value.Scope;
            }
        }

        public Models.GitConfigWriteScope TargetScope
        {
            get => _targetScope;
            set
            {
                if (value == Models.GitConfigWriteScope.Worktree && !WorktreeConfigEnabled)
                    return;

                if (SetProperty(ref _targetScope, value))
                {
                    OnPropertyChanged(nameof(SelectedScopeOption));
                    OnPropertyChanged(nameof(ShowGlobalScopeWarning));
                    OnPropertyChanged(nameof(ScopeDescription));

                    if (_snapshot != null)
                    {
                        foreach (var item in _fields)
                            item.RefreshFromSnapshot(_snapshot, _targetScope);
                        SyncOptionSelectionsFromFields();
                    }

                    OnPropertyChanged(nameof(GlobalScopeTargetHint));

                    UpdateSigningPreview();
                    OnAnyDraftStateChanged();
                }
            }
        }

        public bool WorktreeConfigEnabled
        {
            get => _worktreeConfigEnabled;
            private set => SetProperty(ref _worktreeConfigEnabled, value);
        }

        public bool ShowWorktreeDisabledHint => !WorktreeConfigEnabled;
        public bool ShowGlobalScopeWarning => TargetScope == Models.GitConfigWriteScope.Global;
        public string ScopeDescription => SelectedScopeOption?.Description ?? string.Empty;

        public string GlobalScopeTargetHint
        {
            get
            {
                var envGlobal = Environment.GetEnvironmentVariable("GIT_CONFIG_GLOBAL");
                string targetPath;
                if (!string.IsNullOrWhiteSpace(envGlobal))
                {
                    targetPath = envGlobal.Trim();
                }
                else
                {
                    var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                    targetPath = string.IsNullOrWhiteSpace(profile)
                        ? "~/.gitconfig"
                        : Path.Combine(profile, ".gitconfig");
                }

                return ConfigureLocalizer.Text(
                    "Configure.Scope.GlobalTargetFormat",
                    "写入目标：{0}（将影响当前用户账户下的所有 Git 仓库）",
                    targetPath);
            }
        }

        public bool HasExternalConfigChanges
        {
            get => _hasExternalConfigChanges;
            private set => SetProperty(ref _hasExternalConfigChanges, value);
        }

        public string ExternalChangeHeadline
        {
            get => _externalChangeHeadline;
            private set => SetProperty(ref _externalChangeHeadline, value ?? string.Empty);
        }

        public string ExternalChangeDetail
        {
            get => _externalChangeDetail;
            private set => SetProperty(ref _externalChangeDetail, value ?? string.Empty);
        }

        public AvaloniaList<ExternalConfigDiffItem> ExternalConfigDiffItems { get; } = [];

        public bool IsLoading
        {
            get => _isLoading;
            private set => SetProperty(ref _isLoading, value);
        }

        public bool IsSaving
        {
            get => _isSaving;
            private set => SetProperty(ref _isSaving, value);
        }

        public bool SnapshotReadFailed
        {
            get => _snapshotReadFailed;
            private set => SetProperty(ref _snapshotReadFailed, value);
        }

        public string SnapshotError
        {
            get => _snapshotError;
            private set => SetProperty(ref _snapshotError, value);
        }

        public AvaloniaList<ConfigChoiceOption> SigningFormatOptions { get; } = [];
        public ConfigChoiceOption SelectedSigningFormatOption
        {
            get
            {
                var current = string.IsNullOrWhiteSpace(SigningFormatField.EditText) ? "openpgp" : SigningFormatField.EditText.Trim().ToLowerInvariant();
                return SigningFormatOptions.FirstOrDefault(x => string.Equals(x.Value, current, StringComparison.OrdinalIgnoreCase));
            }
            set
            {
                if (value != null && !_syncingOptions)
                {
                    SigningFormatField.EditText = value.Value;
                    OnPropertyChanged();
                }
            }
        }

        public bool ShowSshSigningOptions
        {
            get
            {
                var fmt = SigningFormatField.EditText.Trim();
                return fmt.Equals("ssh", StringComparison.OrdinalIgnoreCase) || SshAllowedSignersField.HasEffectiveValue || SshAllowedSignersField.HasTargetScopeEntry;
            }
        }

        public AvaloniaList<ConfigChoiceOption> AutoCrlfOptions { get; } = [];
        public ConfigChoiceOption SelectedAutoCrlfOption
        {
            get
            {
                if (AutoCrlfField.PendingUnset)
                    return AutoCrlfOptions.FirstOrDefault(x => x.Value.Length == 0);

                var current = AutoCrlfField.EditText.Trim();
                if (current.Length == 0 && !AutoCrlfField.HasEffectiveValue && !AutoCrlfField.PendingExplicitEmpty)
                    return AutoCrlfOptions.FirstOrDefault(x => x.Value.Length == 0);

                return AutoCrlfOptions.FirstOrDefault(x => string.Equals(x.Value, current, StringComparison.OrdinalIgnoreCase))
                       ?? AutoCrlfOptions.FirstOrDefault(x => x.Value.Length == 0);
            }
            set
            {
                if (value == null || _syncingOptions)
                    return;

                if (value.Value.Length == 0)
                {
                    if (AutoCrlfField.HasTargetScopeEntry)
                        AutoCrlfField.MarkUnsetLayer();
                    else
                        AutoCrlfField.Revert();
                }
                else
                {
                    AutoCrlfField.EditText = value.Value;
                }

                OnPropertyChanged();
            }
        }

        public AvaloniaList<RemoteConfigDraft> RemotesDrafts { get; } = [];
        public RemoteConfigDraft SelectedRemoteDraft
        {
            get => _selectedRemoteDraft;
            set
            {
                if (SetProperty(ref _selectedRemoteDraft, value))
                    OnPropertyChanged(nameof(HasSelectedRemote));
            }
        }

        public bool HasSelectedRemote => SelectedRemoteDraft != null;
        public bool HasNoRemotes => RemotesDrafts.Count == 0;

        public string NewRemoteName
        {
            get => _newRemoteName;
            set => SetProperty(ref _newRemoteName, value ?? string.Empty);
        }

        public string NewRemoteUrl
        {
            get => _newRemoteUrl;
            set => SetProperty(ref _newRemoteUrl, value ?? string.Empty);
        }

        public string RemoteStagingError
        {
            get => _remoteStagingError;
            private set
            {
                if (SetProperty(ref _remoteStagingError, value ?? string.Empty))
                    OnPropertyChanged(nameof(HasRemoteStagingError));
            }
        }

        public bool HasRemoteStagingError => !string.IsNullOrEmpty(RemoteStagingError);

        public string CurrentBranchName
        {
            get => _currentBranchName;
            private set
            {
                if (SetProperty(ref _currentBranchName, value ?? string.Empty))
                    OnPropertyChanged(nameof(IsDetachedHead));
            }
        }

        public bool IsDetachedHead => string.IsNullOrEmpty(CurrentBranchName);

        public string EffectiveUpstreamSummary
        {
            get => _effectiveUpstreamSummary;
            private set => SetProperty(ref _effectiveUpstreamSummary, value ?? string.Empty);
        }

        public bool IsUpstreamUnfetched
        {
            get => _isUpstreamUnfetched;
            private set => SetProperty(ref _isUpstreamUnfetched, value);
        }

        public AvaloniaList<string> UpstreamRemoteOptions { get; } = [];

        public string SelectedUpstreamRemote
        {
            get => _selectedUpstreamRemote;
            set
            {
                var next = value ?? string.Empty;
                if (SetProperty(ref _selectedUpstreamRemote, next))
                {
                    RebuildUpstreamBranchOptions();
                    OnAnyDraftStateChanged();
                }
            }
        }

        public AvaloniaList<string> UpstreamBranchOptions { get; } = [];
        public bool HasUpstreamBranchOptions => UpstreamBranchOptions.Count > 0;

        public string UpstreamTargetBranch
        {
            get => _upstreamTargetBranch;
            set
            {
                var next = value ?? string.Empty;
                if (SetProperty(ref _upstreamTargetBranch, next))
                    OnAnyDraftStateChanged();
            }
        }

        public AvaloniaList<ConfigReadOnlyEntry> UrlProxyOverrides { get; } = [];
        public bool HasUrlProxyOverrides => UrlProxyOverrides.Count > 0;

        public AvaloniaList<ConfigReadOnlyEntry> CredentialHelpers { get; } = [];
        public bool HasCredentialHelpers => CredentialHelpers.Count > 0;

        public AvaloniaList<ConfigReadOnlyEntry> SshAndRuntimeEntries { get; } = [];
        public bool HasSshAndRuntimeEntries => SshAndRuntimeEntries.Count > 0;

        public string SigningProgramReadOnly
        {
            get => _signingProgramReadOnly;
            private set => SetProperty(ref _signingProgramReadOnly, value);
        }

        public string CoreEolReadOnly
        {
            get => _coreEolReadOnly;
            private set => SetProperty(ref _coreEolReadOnly, value);
        }

        public bool SigningStatusHasIssue
        {
            get => _signingStatusHasIssue;
            private set => SetProperty(ref _signingStatusHasIssue, value);
        }

        public bool SigningStatusIsReady
        {
            get => _signingStatusIsReady;
            private set => SetProperty(ref _signingStatusIsReady, value);
        }

        public bool SigningStatusIsDisabled
        {
            get => _signingStatusIsDisabled;
            private set => SetProperty(ref _signingStatusIsDisabled, value);
        }

        public string SigningStatusMessage
        {
            get => _signingStatusMessage;
            private set => SetProperty(ref _signingStatusMessage, value);
        }

        public string SigningStatusSecondaryNote
        {
            get => _signingStatusSecondaryNote;
            private set
            {
                if (SetProperty(ref _signingStatusSecondaryNote, value ?? string.Empty))
                    OnPropertyChanged(nameof(HasSigningStatusSecondaryNote));
            }
        }

        public bool HasSigningStatusSecondaryNote => !string.IsNullOrEmpty(SigningStatusSecondaryNote);

        public string SanitizedDiagnostics
        {
            get => _sanitizedDiagnostics;
            private set => SetProperty(ref _sanitizedDiagnostics, value ?? string.Empty);
        }

        public AvaloniaList<BranchProtectionOptionItem> ProtectedBranches { get; } = [];

        public bool HasProtectedBranches => ProtectedBranches.Count > 0;

        public int PendingChangesCount
        {
            get => _pendingChangesCount;
            private set
            {
                if (SetProperty(ref _pendingChangesCount, value))
                    OnPropertyChanged(nameof(PendingChangesText));
            }
        }

        public string PendingChangesText => PendingChangesCount > 0
            ? ConfigureLocalizer.Text("Configure.Actions.PendingCount", "有 {0} 项待保存变更", PendingChangesCount)
            : ConfigureLocalizer.Text("Configure.Actions.NoChanges", "暂无未保存修改");

        public ConfigSaveSummary SaveFeedback
        {
            get => _saveFeedback;
            private set => SetProperty(ref _saveFeedback, value);
        }

        public Task EnsureLoadedAsync() => _loadTask ?? Task.CompletedTask;

        public void AttachExternalChangeMonitor()
        {
            if (_externalMonitorAttached)
                return;

            _externalMonitorAttached = true;
            _repo.ConfigurationStateChanged += OnRepoConfigurationStateChanged;
        }

        public void DetachExternalChangeMonitor()
        {
            if (!_externalMonitorAttached)
                return;

            _externalMonitorAttached = false;
            _repo.ConfigurationStateChanged -= OnRepoConfigurationStateChanged;
        }

        public async Task ManualRefreshAsync()
        {
            if (PendingChangesCount == 0)
            {
                await ReloadAsync();
                return;
            }

            await CheckExternalChangesAsync();
        }

        public async Task ReloadAsync()
        {
            var generation = Interlocked.Increment(ref _loadGeneration);
            IsLoading = true;
            try
            {
                var snapshot = await new Commands.Config(_repo.FullPath).ReadSnapshotAsync();
                if (generation != Interlocked.Read(ref _loadGeneration))
                    return;

                var branches = snapshot.IsReadable
                    ? await new Commands.QueryBranches(_repo.FullPath).GetResultAsync()
                    : [];
                if (generation != Interlocked.Read(ref _loadGeneration))
                    return;

                var protectionCandidates = snapshot.IsReadable
                    ? await Commands.BranchProtectionPolicy.InspectProtectedCandidatesAsync(
                        _repo.FullPath,
                        branches.Where(b => b.IsLocal).Select(b => b.Name))
                    : [];
                if (generation != Interlocked.Read(ref _loadGeneration))
                    return;

                _unreviewedConflictKeys.Clear();
                ClearExternalChangeNotice();
                ApplySnapshot(snapshot, branches);
                ApplyProtectedBranches(protectionCandidates);
            }
            finally
            {
                if (generation == Interlocked.Read(ref _loadGeneration))
                    IsLoading = false;
            }
        }

        public async Task CheckExternalChangesAsync()
        {
            if (_snapshot == null || IsLoading || IsSaving)
                return;

            var baselineGen = Interlocked.Read(ref _loadGeneration);
            var snapshot = await new Commands.Config(_repo.FullPath).ReadSnapshotAsync();
            if (IsLoading || IsSaving || baselineGen != Interlocked.Read(ref _loadGeneration))
                return;

            if (!snapshot.IsReadable)
            {
                if (PendingChangesCount == 0)
                {
                    _unreviewedConflictKeys.Clear();
                    ClearExternalChangeNotice();
                    ApplySnapshot(snapshot, []);
                }
                else
                {
                    _pendingExternalSnapshot = snapshot;
                    _pendingExternalBranches = [];
                    _pendingExternalProtection = [];
                    ExternalConfigDiffItems.Clear();
                    ExternalChangeHeadline = ConfigureLocalizer.Text("Configure.External.BannerHeadline", "检测到外部配置或当前分支（includeIf）已变化，且当前存在未保存草稿");
                    ExternalChangeDetail = string.IsNullOrWhiteSpace(snapshot.Error)
                        ? "外部配置文件当前无法读取；你的草稿已完整保留。"
                        : $"外部配置文件当前无法读取（{snapshot.Error}）；你的草稿已完整保留。";
                    HasExternalConfigChanges = true;
                }

                return;
            }

            var branches = await new Commands.QueryBranches(_repo.FullPath).GetResultAsync();
            if (IsLoading || IsSaving || baselineGen != Interlocked.Read(ref _loadGeneration))
                return;

            var protectionCandidates = await Commands.BranchProtectionPolicy.InspectProtectedCandidatesAsync(
                _repo.FullPath,
                branches.Where(b => b.IsLocal).Select(b => b.Name));
            if (IsLoading || IsSaving || baselineGen != Interlocked.Read(ref _loadGeneration))
                return;

            if (!HaveSnapshotsChanged(_snapshot, snapshot))
            {
                if (_unreviewedConflictKeys.Count == 0)
                    ClearExternalChangeNotice();
                return;
            }

            if (PendingChangesCount == 0)
            {
                _unreviewedConflictKeys.Clear();
                ClearExternalChangeNotice();
                ApplySnapshot(snapshot, branches);
                ApplyProtectedBranches(protectionCandidates);
                return;
            }

            PresentExternalChangeDiffBanner(_snapshot, snapshot, branches, protectionCandidates);
        }

        public async Task AcceptExternalReloadAsync()
        {
            if (IsLoading || IsSaving)
                return;

            _unreviewedConflictKeys.Clear();
            var cachedSnapshot = _pendingExternalSnapshot;
            var cachedBranches = _pendingExternalBranches;
            var cachedProtection = _pendingExternalProtection;
            ClearExternalChangeNotice();

            if (cachedSnapshot != null && cachedSnapshot.IsReadable)
            {
                Interlocked.Increment(ref _loadGeneration);
                ApplySnapshot(cachedSnapshot, cachedBranches ?? []);
                ApplyProtectedBranches(cachedProtection ?? []);
                return;
            }

            await ReloadAsync();
        }

        public async Task KeepDraftsWithExternalBaselineAsync()
        {
            if (IsLoading || IsSaving)
                return;

            var captured = CaptureAllPendingDrafts();
            var liveSnapshot = _pendingExternalSnapshot is { IsReadable: true }
                ? _pendingExternalSnapshot
                : await new Commands.Config(_repo.FullPath).ReadSnapshotAsync();
            if (!liveSnapshot.IsReadable)
                return;

            var liveBranches = _pendingExternalBranches ?? await new Commands.QueryBranches(_repo.FullPath).GetResultAsync();
            var liveProtection = _pendingExternalProtection
                ?? await Commands.BranchProtectionPolicy.InspectProtectedCandidatesAsync(
                    _repo.FullPath,
                    liveBranches.Where(b => b.IsLocal).Select(b => b.Name));

            Interlocked.Increment(ref _loadGeneration);
            _unreviewedConflictKeys.Clear();
            ClearExternalChangeNotice();

            ApplySnapshot(liveSnapshot, liveBranches);
            ApplyProtectedBranches(liveProtection);
            RestoreCapturedDrafts(captured, restoreAll: true, failedOrConflictTargets: null);
        }

        public void StageAddRemote()
        {
            RemoteStagingError = string.Empty;
            var name = NewRemoteName.Trim();
            var url = NewRemoteUrl.Trim();

            if (string.IsNullOrWhiteSpace(name) || name.StartsWith('-'))
            {
                RemoteStagingError = "请输入有效的远端名称（不能以 '-' 开头）。";
                return;
            }

            if (string.IsNullOrWhiteSpace(url) || url.StartsWith('-'))
            {
                RemoteStagingError = "请输入有效的远端 URL（不能以 '-' 开头）。";
                return;
            }

            if (RemotesDrafts.Any(x => !x.IsMarkedForRemoval && string.Equals(x.Name.Trim(), name, StringComparison.Ordinal)))
            {
                RemoteStagingError = $"远端 '{name}' 已存在。";
                return;
            }

            var draft = RemoteConfigDraft.CreateNew(name, url);
            draft.StateChanged += OnAnyDraftStateChanged;
            RemotesDrafts.Add(draft);
            SelectedRemoteDraft = draft;
            NewRemoteName = string.Empty;
            NewRemoteUrl = string.Empty;
            OnPropertyChanged(nameof(HasNoRemotes));
            RebuildUpstreamRemoteOptions();
            OnAnyDraftStateChanged();
        }

        public void ToggleRemoveSelectedRemote()
        {
            if (SelectedRemoteDraft == null)
                return;

            RemoteStagingError = string.Empty;
            if (SelectedRemoteDraft.IsNew)
            {
                SelectedRemoteDraft.StateChanged -= OnAnyDraftStateChanged;
                RemotesDrafts.Remove(SelectedRemoteDraft);
                SelectedRemoteDraft = RemotesDrafts.FirstOrDefault();
                OnPropertyChanged(nameof(HasNoRemotes));
                RebuildUpstreamRemoteOptions();
                OnAnyDraftStateChanged();
                return;
            }

            SelectedRemoteDraft.IsMarkedForRemoval = !SelectedRemoteDraft.IsMarkedForRemoval;
            RebuildUpstreamRemoteOptions();
        }

        public void ClearUpstream()
        {
            SelectedUpstreamRemote = string.Empty;
            UpstreamTargetBranch = string.Empty;
        }

        public void ClearHttpProxy()
        {
            HttpProxyField.MarkUnsetLayer();
            if (!HttpProxyField.HasTargetScopeEntry)
                HttpProxyField.EditText = string.Empty;
            OnPropertyChanged(nameof(HttpProxy));
        }

        public void AddCommitTemplate()
        {
            var template = new Models.CommitTemplate { Name = "New Template" };
            _repo.Settings.CommitTemplates.Add(template);
            SelectedCommitTemplate = template;
        }

        public void RemoveSelectedCommitTemplate()
        {
            if (_selectedCommitTemplate != null)
                _repo.Settings.CommitTemplates.Remove(_selectedCommitTemplate);
            SelectedCommitTemplate = null;
        }

        public List<string> GetRemoteVisitUrls()
        {
            var outs = new List<string>();
            foreach (var remote in _repo.Remotes)
            {
                if (remote.TryGetVisitURL(out var url))
                    outs.Add(url);
            }
            return outs;
        }

        public void AddIssueTracker(string name, string regex, string url)
        {
            var rule = new Models.IssueTracker
            {
                IsShared = false,
                Name = name,
                RegexString = regex,
                URLTemplate = url,
            };

            IssueTrackers.Add(rule);
            SelectedIssueTracker = rule;
        }

        public void RemoveIssueTracker()
        {
            if (_selectedIssueTracker is { } rule)
                IssueTrackers.Remove(rule);

            SelectedIssueTracker = null;
        }

        public async Task<ConfigSaveSummary> SaveAsync()
        {
            if (IsSaving)
                return SaveFeedback;

            IsSaving = true;
            try
            {
                _repo.Settings.Save();
                await ApplyIssueTrackerChangesAsync();

                var configCmd = new Commands.Config(_repo.FullPath);
                var preSaveSnapshot = await configCmd.ReadSnapshotAsync();
                if (!preSaveSnapshot.IsReadable)
                {
                    var unreadable = new ConfigSaveSummary
                    {
                        Severity = ConfigFeedbackSeverity.Error,
                        Headline = ConfigureLocalizer.Text("Configure.Save.AllFailed", "保存失败（共 {0} 项未能写入）：", 1),
                        FailedCount = 1,
                    };
                    unreadable.FailureAndConflictLines.Add(string.IsNullOrEmpty(preSaveSnapshot.Error) ? "Unable to read repository config." : preSaveSnapshot.Error);
                    SaveFeedback = unreadable;
                    return unreadable;
                }

                if (TargetScope == Models.GitConfigWriteScope.Worktree && !preSaveSnapshot.WorktreeConfigEnabled)
                {
                    var notEnabled = new ConfigSaveSummary
                    {
                        Severity = ConfigFeedbackSeverity.Error,
                        Headline = ConfigureLocalizer.Text("Configure.Save.AllFailed", "保存失败（共 {0} 项未能写入）：", 1),
                        FailedCount = 1,
                    };
                    notEnabled.FailureAndConflictLines.Add("extensions.worktreeConfig is not enabled for this repository.");
                    SaveFeedback = notEnabled;
                    return notEnabled;
                }

                var hasFieldWrites = _fields.Any(f => f.ComputePendingOperation().Kind != ConfigPendingOperationKind.None);
                var hasGlobalWrites = TargetScope == Models.GitConfigWriteScope.Global && hasFieldWrites;
                var hasRepoConfigWrites =
                    (TargetScope != Models.GitConfigWriteScope.Global && hasFieldWrites) ||
                    RemotesDrafts.Any(r => r.HasPendingChange) ||
                    HasUpstreamPendingChange;

                if (hasGlobalWrites)
                {
                    var globalLock = Models.RepositoryWriteGuard.InspectGlobalConfigLock();
                    if (globalLock.IsLocked)
                    {
                        var lockedSummary = new ConfigSaveSummary
                        {
                            Severity = ConfigFeedbackSeverity.Error,
                            Headline = ConfigureLocalizer.Text("Configure.Save.LockBlocked", "检测到 Git 配置锁文件占用，已中止写入（绝不自动删除锁文件）："),
                            FailedCount = 1,
                        };
                        lockedSummary.FailureAndConflictLines.Add(globalLock.Summary);
                        SaveFeedback = lockedSummary;
                        return lockedSummary;
                    }
                }

                await using var repoWriteGuard = hasRepoConfigWrites
                    ? await Models.RepositoryWriteGuard.TryEnterAsync(
                        _repo.FullPath,
                        "保存仓库配置",
                        checkIndexLock: false,
                        checkConfigLock: true)
                    : null;

                if (repoWriteGuard != null && !repoWriteGuard.Acquired)
                {
                    var lockedSummary = new ConfigSaveSummary
                    {
                        Severity = ConfigFeedbackSeverity.Error,
                        Headline = ConfigureLocalizer.Text("Configure.Save.LockBlocked", "检测到 Git 配置锁文件占用，已中止写入（绝不自动删除锁文件）："),
                        FailedCount = 1,
                    };
                    lockedSummary.FailureAndConflictLines.Add(repoWriteGuard.LockDetection.Summary);
                    SaveFeedback = lockedSummary;
                    return lockedSummary;
                }

                var preSaveBaseSnapshot = _snapshot;
                var capturedDraftsBeforeSave = CaptureAllPendingDrafts();

                var results = new List<ConfigSaveItemResult>();
                var attemptedKeys = new List<(string Key, string IntendedValue, bool WasUnset)>();

                await SaveBranchProtectionOverridesAsync(results);
                await SaveScalarFieldsAsync(preSaveSnapshot, preSaveBaseSnapshot, configCmd, results, attemptedKeys);
                await SaveRemoteDraftsAsync(preSaveSnapshot, preSaveBaseSnapshot, results);
                await SaveUpstreamAsync(preSaveSnapshot, preSaveBaseSnapshot, results);

                var refreshedSnapshot = await configCmd.ReadSnapshotAsync();
                var refreshedBranches = refreshedSnapshot.IsReadable
                    ? await new Commands.QueryBranches(_repo.FullPath).GetResultAsync()
                    : [];
                var refreshedProtection = refreshedSnapshot.IsReadable
                    ? await Commands.BranchProtectionPolicy.InspectProtectedCandidatesAsync(
                        _repo.FullPath,
                        refreshedBranches.Where(b => b.IsLocal).Select(b => b.Name))
                    : [];

                if (refreshedSnapshot.IsReadable)
                {
                    Interlocked.Increment(ref _loadGeneration);
                    ApplySnapshot(refreshedSnapshot, refreshedBranches);
                    ApplyProtectedBranches(refreshedProtection);
                    RefreshRepoRemotesAndBranches(refreshedSnapshot);

                    var failedOrConflictTargets = results
                        .Where(r => !r.Succeeded)
                        .Select(r => r.TargetName)
                        .Where(t => !string.IsNullOrWhiteSpace(t))
                        .ToHashSet(StringComparer.OrdinalIgnoreCase);

                    if (failedOrConflictTargets.Count > 0)
                    {
                        RestoreCapturedDrafts(capturedDraftsBeforeSave, restoreAll: false, failedOrConflictTargets);
                    }

                    var conflictTargets = results
                        .Where(r => r.SkippedConflict && !string.IsNullOrWhiteSpace(r.TargetName))
                        .Select(r => r.TargetName)
                        .ToList();

                    foreach (var target in conflictTargets)
                        _unreviewedConflictKeys.Add(target);

                    if (conflictTargets.Count > 0 && preSaveBaseSnapshot != null)
                    {
                        PresentExternalChangeDiffBanner(preSaveBaseSnapshot, refreshedSnapshot, refreshedBranches, refreshedProtection);
                    }
                    else if (_unreviewedConflictKeys.Count == 0)
                    {
                        ClearExternalChangeNotice();
                    }
                }

                var summary = BuildSaveSummary(refreshedSnapshot, results, attemptedKeys);
                SaveFeedback = summary;
                return summary;
            }
            finally
            {
                IsSaving = false;
            }
        }

        private async Task SaveScalarFieldsAsync(
            Models.GitConfigSnapshot preSaveSnapshot,
            Models.GitConfigSnapshot baseSnapshot,
            Commands.Config configCmd,
            List<ConfigSaveItemResult> results,
            List<(string Key, string IntendedValue, bool WasUnset)> attempted)
        {
            foreach (var field in _fields)
            {
                var op = field.ComputePendingOperation();
                if (op.Kind == ConfigPendingOperationKind.None)
                    continue;

                var preScopeEntries = preSaveSnapshot.GetEntriesInScope(
                    field.Key,
                    ConfigureLocalizer.ToConfigScope(TargetScope),
                    includeIncluded: false);
                if (preScopeEntries.Count > 1)
                {
                    results.Add(new ConfigSaveItemResult
                    {
                        TargetName = field.Key,
                        Succeeded = false,
                        Detail = ConfigureLocalizer.Text("Configure.Field.MultiValueReadOnly", "目标作用域存在多条定义，为保护多值配置结构已设为只读。"),
                    });
                    continue;
                }

                if (_unreviewedConflictKeys.Contains(field.Key))
                {
                    results.Add(new ConfigSaveItemResult
                    {
                        TargetName = field.Key,
                        Succeeded = false,
                        SkippedConflict = true,
                        Detail = "存在尚未复核的外部变更冲突，请先在上方差异提示区确认保留草稿或重新读取后再保存。",
                    });
                    continue;
                }

                if (HasTargetScopeConflict(baseSnapshot, preSaveSnapshot, field.Key, TargetScope, out var conflictDetail))
                {
                    results.Add(new ConfigSaveItemResult
                    {
                        TargetName = field.Key,
                        Succeeded = false,
                        SkippedConflict = true,
                        Detail = conflictDetail,
                    });
                    continue;
                }

                var writeRes = await configCmd.SetScopedAsync(
                    TargetScope,
                    field.Key,
                    op.Value,
                    unset: op.Kind == ConfigPendingOperationKind.Unset,
                    allowEmpty: op.AllowEmpty,
                    acquireWriteGuard: false);

                if (writeRes.IsSuccess)
                {
                    results.Add(new ConfigSaveItemResult
                    {
                        TargetName = field.Key,
                        Succeeded = true,
                    });
                    attempted.Add((field.Key, op.Value, op.Kind == ConfigPendingOperationKind.Unset));
                }
                else
                {
                    results.Add(new ConfigSaveItemResult
                    {
                        TargetName = field.Key,
                        Succeeded = false,
                        Detail = FormatCommandFailure(writeRes),
                    });
                }
            }
        }

        private async Task SaveRemoteDraftsAsync(
            Models.GitConfigSnapshot preSaveSnapshot,
            Models.GitConfigSnapshot baseSnapshot,
            List<ConfigSaveItemResult> results)
        {
            var remoteCmd = new Commands.Remote(_repo.FullPath);
            var baseRemotes = baseSnapshot?.GetRemotes().ToDictionary(x => x.Name, StringComparer.Ordinal)
                              ?? new Dictionary<string, Models.GitConfigRemote>(StringComparer.Ordinal);
            var liveRemotes = preSaveSnapshot.GetRemotes().ToDictionary(x => x.Name, StringComparer.Ordinal);

            foreach (var draft in RemotesDrafts)
            {
                if (!draft.HasPendingChange)
                    continue;

                if (draft.IsNew)
                {
                    var addName = draft.Name.Trim();
                    var addUrl = draft.PrimaryFetchUrl.Trim();
                    if (liveRemotes.ContainsKey(addName))
                    {
                        results.Add(new ConfigSaveItemResult
                        {
                            TargetName = $"remote.{addName}",
                            Succeeded = false,
                            SkippedConflict = true,
                            Detail = $"远端 '{addName}' 已存在于仓库配置中。",
                        });
                        continue;
                    }

                    var addRes = await remoteCmd.AddTargetedAsync(addName, addUrl, acquireWriteGuard: false);
                    results.Add(new ConfigSaveItemResult
                    {
                        TargetName = $"remote.{addName}",
                        Succeeded = addRes.IsSuccess,
                        Detail = addRes.IsSuccess ? string.Empty : FormatCommandFailure(addRes),
                    });

                    if (addRes.IsSuccess && !string.IsNullOrWhiteSpace(draft.PrimaryPushUrl))
                    {
                        var pushRes = await remoteCmd.SetUrlTargetedAsync(addName, draft.PrimaryPushUrl.Trim(), null, isPush: true, isDelete: false, acquireWriteGuard: false);
                        results.Add(new ConfigSaveItemResult
                        {
                            TargetName = $"remote.{addName}.pushurl",
                            Succeeded = pushRes.IsSuccess,
                            Detail = pushRes.IsSuccess ? string.Empty : FormatCommandFailure(pushRes),
                        });
                    }
                    continue;
                }

                if (!liveRemotes.TryGetValue(draft.OriginalName, out var liveRemote))
                {
                    results.Add(new ConfigSaveItemResult
                    {
                        TargetName = $"remote.{draft.OriginalName}",
                        Succeeded = false,
                        SkippedConflict = true,
                        Detail = "远端已被外部操作移除或重命名，跳过写入。",
                    });
                    continue;
                }

                if (draft.IsMarkedForRemoval)
                {
                    var delRes = await remoteCmd.DeleteTargetedAsync(draft.OriginalName, acquireWriteGuard: false);
                    results.Add(new ConfigSaveItemResult
                    {
                        TargetName = $"remote.{draft.OriginalName}",
                        Succeeded = delRes.IsSuccess,
                        Detail = delRes.IsSuccess ? string.Empty : FormatCommandFailure(delRes),
                    });
                    continue;
                }

                var activeName = draft.OriginalName;
                if (draft.IsRenamed)
                {
                    var nextName = draft.Name.Trim();
                    var renameRes = await remoteCmd.RenameTargetedAsync(draft.OriginalName, nextName, acquireWriteGuard: false);
                    results.Add(new ConfigSaveItemResult
                    {
                        TargetName = $"remote.{draft.OriginalName} -> {nextName}",
                        Succeeded = renameRes.IsSuccess,
                        Detail = renameRes.IsSuccess ? string.Empty : FormatCommandFailure(renameRes),
                    });

                    if (!renameRes.IsSuccess)
                        continue;

                    activeName = nextName;
                }

                if (draft.IsFetchUrlEdited)
                {
                    baseRemotes.TryGetValue(draft.OriginalName, out var baseRemote);
                    var baseFirstFetch = baseRemote?.FetchUrls.FirstOrDefault()?.Value ?? string.Empty;
                    var liveFirstFetch = liveRemote.FetchUrls.FirstOrDefault()?.Value ?? string.Empty;
                    if (!string.Equals(baseFirstFetch, liveFirstFetch, StringComparison.Ordinal))
                    {
                        results.Add(new ConfigSaveItemResult
                        {
                            TargetName = $"remote.{activeName}.url",
                            Succeeded = false,
                            SkippedConflict = true,
                            Detail = "保存前核对发现主获取 URL 已被外部修改，跳过覆盖。",
                        });
                    }
                    else
                    {
                        var fetchRes = await remoteCmd.SetUrlTargetedAsync(
                            activeName,
                            draft.PrimaryFetchUrl.Trim(),
                            draft.OriginalPrimaryFetchUrl,
                            isPush: false,
                            isDelete: false,
                            acquireWriteGuard: false);
                        results.Add(new ConfigSaveItemResult
                        {
                            TargetName = $"remote.{activeName}.url",
                            Succeeded = fetchRes.IsSuccess,
                            Detail = fetchRes.IsSuccess ? string.Empty : FormatCommandFailure(fetchRes),
                        });
                    }
                }

                if (draft.IsPushUrlEdited)
                {
                    baseRemotes.TryGetValue(draft.OriginalName, out var baseRemote);
                    var baseFirstPush = baseRemote?.PushUrls.FirstOrDefault()?.Value ?? string.Empty;
                    var liveFirstPush = liveRemote.PushUrls.FirstOrDefault()?.Value ?? string.Empty;
                    if (!string.Equals(baseFirstPush, liveFirstPush, StringComparison.Ordinal))
                    {
                        results.Add(new ConfigSaveItemResult
                        {
                            TargetName = $"remote.{activeName}.pushurl",
                            Succeeded = false,
                            SkippedConflict = true,
                            Detail = "保存前核对发现主推送 URL 已被外部修改，跳过覆盖。",
                        });
                    }
                    else
                    {
                        var nextPush = draft.PrimaryPushUrl.Trim();
                        var hasOriginalPush = !string.IsNullOrEmpty(draft.OriginalPrimaryPushUrl);
                        Commands.Command.Result pushRes;

                        if (nextPush.Length == 0 && hasOriginalPush)
                            pushRes = await remoteCmd.SetUrlTargetedAsync(activeName, null, draft.OriginalPrimaryPushUrl, isPush: true, isDelete: true, acquireWriteGuard: false);
                        else
                            pushRes = await remoteCmd.SetUrlTargetedAsync(activeName, nextPush, hasOriginalPush ? draft.OriginalPrimaryPushUrl : null, isPush: true, isDelete: false, acquireWriteGuard: false);

                        results.Add(new ConfigSaveItemResult
                        {
                            TargetName = $"remote.{activeName}.pushurl",
                            Succeeded = pushRes.IsSuccess,
                            Detail = pushRes.IsSuccess ? string.Empty : FormatCommandFailure(pushRes),
                        });
                    }
                }
            }
        }

        private async Task SaveUpstreamAsync(
            Models.GitConfigSnapshot preSaveSnapshot,
            Models.GitConfigSnapshot baseSnapshot,
            List<ConfigSaveItemResult> results)
        {
            if (IsDetachedHead || !HasUpstreamPendingChange)
                return;

            var remoteKey = $"branch.{CurrentBranchName}.remote";
            var mergeKey = $"branch.{CurrentBranchName}.merge";
            if (_unreviewedConflictKeys.Contains($"branch.{CurrentBranchName}.upstream") ||
                !string.Equals(preSaveSnapshot.CurrentBranch, CurrentBranchName, StringComparison.Ordinal) ||
                HasEffectiveValueChanged(baseSnapshot, preSaveSnapshot, remoteKey) ||
                HasEffectiveValueChanged(baseSnapshot, preSaveSnapshot, mergeKey))
            {
                results.Add(new ConfigSaveItemResult
                {
                    TargetName = $"branch.{CurrentBranchName}.upstream",
                    Succeeded = false,
                    SkippedConflict = true,
                    Detail = "当前分支或其上游配置已在外部变更，跳过覆盖，请确认最新状态。",
                });
                return;
            }

            var targetRemote = SelectedUpstreamRemote.Trim();
            var targetBranch = UpstreamTargetBranch.Trim();
            var branchCmd = new Commands.Branch(_repo.FullPath, CurrentBranchName);

            if (targetRemote.Length == 0 && targetBranch.Length == 0)
            {
                var unsetRes = await branchCmd.UnsetUpstreamTargetedAsync(acquireWriteGuard: false);
                results.Add(new ConfigSaveItemResult
                {
                    TargetName = $"branch.{CurrentBranchName}.upstream",
                    Succeeded = unsetRes.IsSuccess,
                    Detail = unsetRes.IsSuccess ? string.Empty : FormatCommandFailure(unsetRes),
                });
                return;
            }

            if (targetRemote.Length == 0 || targetBranch.Length == 0)
            {
                results.Add(new ConfigSaveItemResult
                {
                    TargetName = $"branch.{CurrentBranchName}.upstream",
                    Succeeded = false,
                    Detail = "设置上游时请同时指定目标远端与目标分支。",
                });
                return;
            }

            var setRes = await branchCmd.SetUpstreamTargetedAsync(targetRemote, targetBranch, acquireWriteGuard: false);
            results.Add(new ConfigSaveItemResult
            {
                TargetName = $"branch.{CurrentBranchName}.upstream",
                Succeeded = setRes.IsSuccess,
                Detail = setRes.IsSuccess ? string.Empty : FormatCommandFailure(setRes),
            });
        }

        private ConfigSaveSummary BuildSaveSummary(
            Models.GitConfigSnapshot refreshed,
            IReadOnlyList<ConfigSaveItemResult> results,
            IReadOnlyList<(string Key, string IntendedValue, bool WasUnset)> attemptedKeys)
        {
            var succeeded = results.Count(x => x.Succeeded);
            var failed = results.Count(x => !x.Succeeded && !x.SkippedConflict);
            var conflicts = results.Count(x => x.SkippedConflict);

            var failureLines = new List<string>();
            foreach (var r in results.Where(x => !x.Succeeded))
                failureLines.Add(r.DisplayLine);

            var shadowedLines = new List<string>();
            if (refreshed.IsReadable)
            {
                var targetRank = ConfigureLocalizer.GetWriteScopeRank(TargetScope);
                foreach (var (key, intended, wasUnset) in attemptedKeys)
                {
                    var eff = refreshed.GetEffectiveEntry(key);
                    if (eff == null)
                        continue;

                    var effRank = ConfigureLocalizer.GetScopeRank(eff.Scope);
                    var isShadowed = !wasUnset &&
                                     (effRank > targetRank ||
                                      (effRank == targetRank && eff.IsIncluded) ||
                                      !string.Equals(eff.Value, intended, StringComparison.Ordinal));

                    if (isShadowed)
                    {
                        var scopeLabel = ConfigureLocalizer.FormatScopeBadge(eff.Scope, eff.IsIncluded);
                        shadowedLines.Add($"{key}: 有效值仍为 \"{eff.DisplayValue}\" ({scopeLabel}, {eff.Origin})");
                    }
                }
            }

            ConfigFeedbackSeverity severity;
            string headline;

            if (results.Count == 0)
            {
                severity = ConfigFeedbackSeverity.Info;
                headline = ConfigureLocalizer.Text("Configure.Save.NothingToSave", "未检测到需要写入 Git 配置的更改（仓库本地偏好已同步）。");
            }
            else if (failed > 0 && succeeded == 0)
            {
                severity = ConfigFeedbackSeverity.Error;
                headline = ConfigureLocalizer.Text("Configure.Save.AllFailed", "保存失败（共 {0} 项未能写入）：", failed + conflicts);
            }
            else if (failed > 0 || conflicts > 0)
            {
                severity = conflicts > 0 && failed == 0 && succeeded == 0
                    ? ConfigFeedbackSeverity.Warning
                    : ConfigFeedbackSeverity.Error;
                headline = conflicts > 0 && failed == 0
                    ? ConfigureLocalizer.Text("Configure.Save.ExternalConflict", "保存前核对发现外部配置已变更，以下项目已跳过覆盖（请重新读取后再确认）：")
                    : ConfigureLocalizer.Text("Configure.Save.PartialFailure", "部分设置未保存成功：成功 {0} 项，失败 {1} 项。逐项详情：", succeeded, failed + conflicts);
            }
            else if (shadowedLines.Count > 0)
            {
                severity = ConfigFeedbackSeverity.Warning;
                headline = ConfigureLocalizer.Text("Configure.Save.HigherOverrideWarning", "请注意：以下项目已写入目标作用域，但仍被更高优先级配置覆盖，暂未作为有效值生效：");
            }
            else
            {
                severity = ConfigFeedbackSeverity.Success;
                headline = ConfigureLocalizer.Text("Configure.Save.Success", "已成功保存 {0} 项设置并重新读取有效配置与来源。", succeeded);
            }

            var summary = new ConfigSaveSummary
            {
                Severity = severity,
                Headline = headline,
                SucceededCount = succeeded,
                FailedCount = failed,
                ConflictCount = conflicts,
                ShadowedCount = shadowedLines.Count,
            };

            foreach (var line in failureLines)
                summary.FailureAndConflictLines.Add(line);
            foreach (var line in shadowedLines)
                summary.ShadowedLines.Add(line);
            foreach (var item in results)
                summary.ItemResults.Add(item);

            return summary;
        }

        private void RefreshRepoRemotesAndBranches(Models.GitConfigSnapshot snapshot)
        {
            var remoteNames = snapshot.GetRemotes().Select(x => x.Name).ToList();
            Remotes.Clear();
            foreach (var name in remoteNames)
                Remotes.Add(name);

            if (!string.IsNullOrEmpty(DefaultRemote) && !remoteNames.Contains(DefaultRemote, StringComparer.Ordinal))
                DefaultRemote = remoteNames.FirstOrDefault() ?? string.Empty;

            if (Application.Current != null)
                Dispatcher.UIThread.Post(_repo.RefreshBranches);
        }

        private void ApplySnapshot(Models.GitConfigSnapshot snapshot, List<Models.Branch> branches)
        {
            _snapshot = snapshot;
            _queriedBranches = branches ?? [];

            SnapshotReadFailed = !snapshot.IsReadable;
            SnapshotError = snapshot.Error ?? string.Empty;

            WorktreeConfigEnabled = snapshot.WorktreeConfigEnabled;
            OnPropertyChanged(nameof(ShowWorktreeDisabledHint));
            RebuildScopeOptions(WorktreeConfigEnabled);

            if (TargetScope == Models.GitConfigWriteScope.Worktree && !WorktreeConfigEnabled)
                _targetScope = Models.GitConfigWriteScope.Local;

            OnPropertyChanged(nameof(TargetScope));
            OnPropertyChanged(nameof(SelectedScopeOption));
            OnPropertyChanged(nameof(ShowGlobalScopeWarning));
            OnPropertyChanged(nameof(ScopeDescription));

            foreach (var field in _fields)
                field.RefreshFromSnapshot(snapshot, TargetScope);

            RebuildSigningFormatOptions(SigningFormatField.EditText);
            RebuildAutoCrlfOptions(AutoCrlfField.EditText);
            SyncOptionSelectionsFromFields();

            RebuildRemotesDrafts(snapshot);
            RebuildUpstreamState(snapshot);
            RebuildReadOnlyCollections(snapshot);

            SanitizedDiagnostics = snapshot.BuildSanitizedDiagnostics();
            UpdateSigningPreview();
            NotifyAllCompatibilityProperties();
            UpdatePendingChangesCount();
        }

        private void RebuildScopeOptions(bool worktreeEnabled)
        {
            AvailableWriteScopes.Clear();
            AvailableWriteScopes.Add(new ConfigScopeOption
            {
                Scope = Models.GitConfigWriteScope.Local,
                Title = ConfigureLocalizer.Text("Configure.Scope.Local", "当前仓库 (--local)"),
                Description = ConfigureLocalizer.Text("Configure.Scope.LocalHint", "写入当前仓库配置文件；同一仓库的多个工作树可能共享此配置。"),
            });

            if (worktreeEnabled)
            {
                AvailableWriteScopes.Add(new ConfigScopeOption
                {
                    Scope = Models.GitConfigWriteScope.Worktree,
                    Title = ConfigureLocalizer.Text("Configure.Scope.Worktree", "当前工作树 (--worktree)"),
                    Description = ConfigureLocalizer.Text("Configure.Scope.WorktreeHint", "已启用 extensions.worktreeConfig，保存将写入当前工作树独立配置。"),
                });
            }

            AvailableWriteScopes.Add(new ConfigScopeOption
            {
                Scope = Models.GitConfigWriteScope.Global,
                Title = ConfigureLocalizer.Text("Configure.Scope.Global", "此用户的全局配置 (--global)"),
                Description = ConfigureLocalizer.Text("Configure.Scope.GlobalWarning", "注意：当前选择写入“此用户的全局配置 (--global)”，保存将影响此用户下的所有 Git 仓库。"),
            });
        }

        private void RebuildSigningFormatOptions(string currentFormat)
        {
            SigningFormatOptions.Clear();
            SigningFormatOptions.Add(new ConfigChoiceOption { Value = "openpgp", Label = "OpenPGP (gpg)" });
            SigningFormatOptions.Add(new ConfigChoiceOption { Value = "ssh", Label = "SSH (ssh-keygen)" });
            SigningFormatOptions.Add(new ConfigChoiceOption { Value = "x509", Label = "X.509 / S/MIME (gpgsm)" });

            var trimmed = (currentFormat ?? string.Empty).Trim().ToLowerInvariant();
            if (!string.IsNullOrEmpty(trimmed) && SigningFormatOptions.All(x => !x.Value.Equals(trimmed, StringComparison.OrdinalIgnoreCase)))
                SigningFormatOptions.Add(new ConfigChoiceOption { Value = trimmed, Label = trimmed });
        }

        private void RebuildAutoCrlfOptions(string currentValue)
        {
            AutoCrlfOptions.Clear();
            AutoCrlfOptions.Add(new ConfigChoiceOption
            {
                Value = string.Empty,
                Label = ConfigureLocalizer.Text("Configure.Scope.Unset", "未设置"),
            });
            AutoCrlfOptions.Add(new ConfigChoiceOption { Value = "true", Label = "true" });
            AutoCrlfOptions.Add(new ConfigChoiceOption { Value = "input", Label = "input" });
            AutoCrlfOptions.Add(new ConfigChoiceOption { Value = "false", Label = "false" });

            var trimmed = (currentValue ?? string.Empty).Trim();
            if (!string.IsNullOrEmpty(trimmed) && AutoCrlfOptions.All(x => !x.Value.Equals(trimmed, StringComparison.OrdinalIgnoreCase)))
                AutoCrlfOptions.Add(new ConfigChoiceOption { Value = trimmed, Label = trimmed });
        }

        private void RebuildRemotesDrafts(Models.GitConfigSnapshot snapshot)
        {
            var previousSelection = SelectedRemoteDraft?.OriginalName;
            foreach (var old in RemotesDrafts)
                old.StateChanged -= OnAnyDraftStateChanged;
            RemotesDrafts.Clear();

            var remoteModels = snapshot.GetRemotes();
            Remotes.Clear();
            foreach (var r in remoteModels)
            {
                Remotes.Add(r.Name);
                var draft = RemoteConfigDraft.FromModel(r);
                draft.StateChanged += OnAnyDraftStateChanged;
                RemotesDrafts.Add(draft);
            }

            SelectedRemoteDraft = !string.IsNullOrEmpty(previousSelection)
                ? RemotesDrafts.FirstOrDefault(x => string.Equals(x.OriginalName, previousSelection, StringComparison.Ordinal)) ?? RemotesDrafts.FirstOrDefault()
                : RemotesDrafts.FirstOrDefault();

            OnPropertyChanged(nameof(HasNoRemotes));
        }

        private void RebuildUpstreamState(Models.GitConfigSnapshot snapshot)
        {
            CurrentBranchName = snapshot.CurrentBranch ?? string.Empty;
            RebuildUpstreamRemoteOptions();

            if (IsDetachedHead)
            {
                _originalUpstreamRemote = string.Empty;
                _originalUpstreamBranch = string.Empty;
                _selectedUpstreamRemote = string.Empty;
                _upstreamTargetBranch = string.Empty;
                EffectiveUpstreamSummary = ConfigureLocalizer.Text("Configure.Upstream.DetachedHead", "当前处于游离 HEAD 状态，无可配置的分支上游。");
                IsUpstreamUnfetched = false;
                UpstreamBranchOptions.Clear();
                OnPropertyChanged(nameof(SelectedUpstreamRemote));
                OnPropertyChanged(nameof(UpstreamTargetBranch));
                return;
            }

            var remoteVal = snapshot.GetEffectiveValue($"branch.{CurrentBranchName}.remote").Trim();
            var mergeVal = snapshot.GetEffectiveValue($"branch.{CurrentBranchName}.merge").Trim();
            var shortMerge = mergeVal.StartsWith("refs/heads/", StringComparison.Ordinal)
                ? mergeVal.Substring("refs/heads/".Length)
                : mergeVal;

            _originalUpstreamRemote = remoteVal;
            _originalUpstreamBranch = shortMerge;
            _selectedUpstreamRemote = remoteVal;
            _upstreamTargetBranch = shortMerge;

            if (string.IsNullOrEmpty(remoteVal) || string.IsNullOrEmpty(shortMerge))
            {
                EffectiveUpstreamSummary = ConfigureLocalizer.Text("Configure.Upstream.NotSet", "未设置上游分支");
                IsUpstreamUnfetched = false;
            }
            else
            {
                var trackingFull = remoteVal == "."
                    ? $"refs/heads/{shortMerge}"
                    : $"refs/remotes/{remoteVal}/{shortMerge}";
                var trackingDisplay = remoteVal == "." ? shortMerge : $"{remoteVal}/{shortMerge}";
                var hasRef = _queriedBranches.Any(b => string.Equals(b.FullName, trackingFull, StringComparison.Ordinal));
                EffectiveUpstreamSummary = $"{CurrentBranchName} -> {trackingDisplay} ({mergeVal})";
                IsUpstreamUnfetched = !hasRef;
            }

            RebuildUpstreamBranchOptions();
            OnPropertyChanged(nameof(SelectedUpstreamRemote));
            OnPropertyChanged(nameof(UpstreamTargetBranch));
        }

        private void RebuildUpstreamRemoteOptions()
        {
            UpstreamRemoteOptions.Clear();
            UpstreamRemoteOptions.Add(string.Empty);

            foreach (var r in RemotesDrafts.Where(x => !x.IsMarkedForRemoval))
            {
                var n = r.Name.Trim();
                if (!string.IsNullOrEmpty(n) && !UpstreamRemoteOptions.Contains(n))
                    UpstreamRemoteOptions.Add(n);
            }

            if (!string.IsNullOrEmpty(_selectedUpstreamRemote) && !UpstreamRemoteOptions.Contains(_selectedUpstreamRemote))
                UpstreamRemoteOptions.Add(_selectedUpstreamRemote);
        }

        private void RebuildUpstreamBranchOptions()
        {
            UpstreamBranchOptions.Clear();
            if (string.IsNullOrWhiteSpace(_selectedUpstreamRemote))
            {
                OnPropertyChanged(nameof(HasUpstreamBranchOptions));
                return;
            }

            var remoteName = _selectedUpstreamRemote.Trim();
            var names = _queriedBranches
                .Where(b => remoteName == "."
                    ? b.IsLocal && !b.IsDetachedHead
                    : !b.IsLocal && string.Equals(b.Remote, remoteName, StringComparison.Ordinal))
                .Select(b => b.Name)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase);

            foreach (var n in names)
                UpstreamBranchOptions.Add(n);

            OnPropertyChanged(nameof(HasUpstreamBranchOptions));
        }

        private void RebuildReadOnlyCollections(Models.GitConfigSnapshot snapshot)
        {
            UrlProxyOverrides.Clear();
            foreach (var item in snapshot.GetUrlProxyOverrides())
            {
                UrlProxyOverrides.Add(new ConfigReadOnlyEntry
                {
                    Label = item.Pattern,
                    Value = item.Entry.DisplayValue,
                    SourceBadge = ConfigureLocalizer.FormatScopeBadge(item.Entry.Scope, item.Entry.IsIncluded),
                    Origin = item.Entry.Origin,
                });
            }
            OnPropertyChanged(nameof(HasUrlProxyOverrides));

            CredentialHelpers.Clear();
            var helpers = snapshot.GetEntries("credential.helper");
            var lastResetIndex = -1;
            for (var i = helpers.Count - 1; i >= 0; i--)
            {
                if (helpers[i].HasValue && helpers[i].Value.Length == 0)
                {
                    lastResetIndex = i;
                    break;
                }
            }

            for (var i = 0; i < helpers.Count; i++)
            {
                var h = helpers[i];
                string displayVal;
                if (h.HasValue && h.Value.Length == 0)
                    displayVal = ConfigureLocalizer.Text("Configure.AuthSsh.EmptyHelperReset", "\"\"（显式重置前序助手链）");
                else if (i < lastResetIndex)
                    displayVal = $"{h.DisplayValue} (已被后续 \"\" 重置)";
                else
                    displayVal = h.DisplayValue;

                CredentialHelpers.Add(new ConfigReadOnlyEntry
                {
                    Label = $"#{i + 1}",
                    Value = displayVal,
                    SourceBadge = ConfigureLocalizer.FormatScopeBadge(h.Scope, h.IsIncluded),
                    Origin = h.Origin,
                });
            }
            OnPropertyChanged(nameof(HasCredentialHelpers));

            SshAndRuntimeEntries.Clear();
            var sshEntry = snapshot.GetEffectiveEntry("core.sshcommand");
            if (sshEntry != null)
            {
                SshAndRuntimeEntries.Add(new ConfigReadOnlyEntry
                {
                    Label = "core.sshCommand",
                    Value = sshEntry.DisplayValue,
                    SourceBadge = ConfigureLocalizer.FormatScopeBadge(sshEntry.Scope, sshEntry.IsIncluded),
                    Origin = sshEntry.Origin,
                });
            }

            foreach (var remote in snapshot.GetRemotes().Where(x => x.SshKey != null))
            {
                SshAndRuntimeEntries.Add(new ConfigReadOnlyEntry
                {
                    Label = $"remote.{remote.Name}.sshkey",
                    Value = remote.SshKey.DisplayValue,
                    SourceBadge = ConfigureLocalizer.FormatScopeBadge(remote.SshKey.Scope, remote.SshKey.IsIncluded),
                    Origin = remote.SshKey.Origin,
                });
            }

            foreach (var rt in snapshot.Entries.Where(x => x.IsRuntime))
            {
                SshAndRuntimeEntries.Add(new ConfigReadOnlyEntry
                {
                    Label = rt.Key,
                    Value = rt.DisplayValue,
                    SourceBadge = ConfigureLocalizer.FormatScopeBadge(rt.Scope, rt.IsIncluded),
                    Origin = rt.Origin,
                });
            }
            OnPropertyChanged(nameof(HasSshAndRuntimeEntries));

            var fmt = string.IsNullOrWhiteSpace(SigningFormatField.EffectiveRawValue)
                ? "openpgp"
                : SigningFormatField.EffectiveRawValue.Trim().ToLowerInvariant();
            var progEntry = fmt switch
            {
                "ssh" => snapshot.GetEffectiveEntry("gpg.ssh.program"),
                "x509" => snapshot.GetEffectiveEntry("gpg.x509.program"),
                _ => snapshot.GetEffectiveEntry("gpg.openpgp.program") ?? snapshot.GetEffectiveEntry("gpg.program"),
            };
            SigningProgramReadOnly = progEntry != null
                ? $"{progEntry.DisplayValue} ({ConfigureLocalizer.FormatScopeBadge(progEntry.Scope, progEntry.IsIncluded)})"
                : "PATH 默认程序";

            var eolEntry = snapshot.GetEffectiveEntry("core.eol");
            CoreEolReadOnly = eolEntry != null
                ? $"{eolEntry.DisplayValue} ({ConfigureLocalizer.FormatScopeBadge(eolEntry.Scope, eolEntry.IsIncluded)})"
                : "native (默认)";
        }

        private void UpdateSigningPreview()
        {
            if (_snapshot == null || !_snapshot.IsReadable)
            {
                SigningStatusIsDisabled = true;
                SigningStatusIsReady = false;
                SigningStatusHasIssue = false;
                SigningStatusMessage = ConfigureLocalizer.Text("Configure.Signing.Disabled", "未启用提交或标签自动签名。");
                SigningStatusSecondaryNote = string.Empty;
                return;
            }

            var preview = new Models.GitConfigSnapshot { IsReadable = true };
            preview.Entries.AddRange(_snapshot.Entries);

            AppendPreviewEntry(preview, CommitSignField, CommitSignField.EditBool ? "true" : "false");
            AppendPreviewEntry(preview, TagSignField, TagSignField.EditBool ? "true" : "false");
            AppendPreviewEntry(preview, SigningFormatField, SigningFormatField.EditText);
            AppendPreviewEntry(preview, SigningKeyField, SigningKeyField.EditText);
            AppendPreviewEntry(preview, SshAllowedSignersField, SshAllowedSignersField.EditText);

            var status = Commands.Config.InspectSigningStatus(preview, Native.OS.GitInstallation);
            var enabled = status.CommitSigningEnabled || status.TagSigningEnabled;

            if (!enabled)
            {
                SigningStatusIsDisabled = true;
                SigningStatusIsReady = false;
                SigningStatusHasIssue = false;
                SigningStatusMessage = ConfigureLocalizer.Text("Configure.Signing.Disabled", "未启用提交或标签自动签名。");
                SigningStatusSecondaryNote = !status.IsReady && !string.IsNullOrWhiteSpace(status.SigningKey)
                    ? FormatSigningIssue(status)
                    : string.Empty;
                return;
            }

            SigningStatusIsDisabled = false;
            if (status.IsReady)
            {
                SigningStatusIsReady = true;
                SigningStatusHasIssue = false;
                SigningStatusMessage = ConfigureLocalizer.Text("Configure.Signing.Ready", "签名配置与本地依赖检查通过，环境已就绪。");
                SigningStatusSecondaryNote = status.Format == "ssh" && string.IsNullOrWhiteSpace(status.AllowedSignersFile)
                    ? ConfigureLocalizer.Text("Configure.Signing.Issue.MissingAllowedSigners", "提示：未设置 gpg.ssh.allowedSignersFile，签名可正常生成，但本地验签需要该文件。")
                    : string.Empty;
            }
            else
            {
                SigningStatusIsReady = false;
                SigningStatusHasIssue = true;
                SigningStatusMessage = FormatSigningIssue(status);
                SigningStatusSecondaryNote = string.Empty;
            }
        }

        private static string FormatSigningIssue(Models.GitConfigSigningStatus status)
        {
            var fallback = status.IssueKey switch
            {
                "Configure.Signing.UnsupportedFormat" => $"不支持的签名格式 ({status.IssueDetail})，请使用 openpgp、x509 或 ssh。",
                "Configure.Signing.SshRequiresNewerGit" => $"SSH 签名需要 Git 2.34 及以上版本（当前版本：{status.IssueDetail}）。",
                "Configure.Signing.MissingKey" => "已启用签名，但尚未配置签名密钥 (user.signingkey)。",
                "Configure.Signing.MissingProgram" => $"未找到签名程序 ({status.IssueDetail})，执行提交或标签签名将失败。",
                _ => status.IssueKey,
            };

            return ConfigureLocalizer.Text(status.IssueKey, fallback, status.IssueDetail);
        }

        private static void AppendPreviewEntry(Models.GitConfigSnapshot preview, ConfigFieldItem field, string value)
        {
            if (!field.HasPendingChange && !field.HasTargetScopeEntry)
                return;

            var op = field.ComputePendingOperation();
            if (op.Kind == ConfigPendingOperationKind.Unset)
            {
                preview.Entries.RemoveAll(e => e.NormalizedKey == field.Key && e.Scope == Models.GitConfigScope.Local);
                return;
            }

            preview.Entries.Add(new Models.GitConfigEntry
            {
                Key = field.Key,
                NormalizedKey = field.Key,
                Value = value ?? string.Empty,
                HasValue = true,
                Scope = Models.GitConfigScope.Command,
            });
        }

        private void OnAnyDraftStateChanged()
        {
            SyncOptionSelectionsFromFields();
            OnPropertyChanged(nameof(ShowSshSigningOptions));
            UpdateSigningPreview();
            NotifyAllCompatibilityProperties();
            UpdatePendingChangesCount();
        }

        private void SyncOptionSelectionsFromFields()
        {
            _syncingOptions = true;
            try
            {
                OnPropertyChanged(nameof(SelectedSigningFormatOption));
                OnPropertyChanged(nameof(SelectedAutoCrlfOption));
            }
            finally
            {
                _syncingOptions = false;
            }
        }

        private void NotifyAllCompatibilityProperties()
        {
            OnPropertyChanged(nameof(UserName));
            OnPropertyChanged(nameof(UserEmail));
            OnPropertyChanged(nameof(HttpProxy));
            OnPropertyChanged(nameof(GPGCommitSigningEnabled));
            OnPropertyChanged(nameof(GPGTagSigningEnabled));
            OnPropertyChanged(nameof(GPGUserSigningKey));
            OnPropertyChanged(nameof(EnablePruneOnFetch));
        }

        private bool HasUpstreamPendingChange =>
            !IsDetachedHead &&
            (!string.Equals(SelectedUpstreamRemote.Trim(), _originalUpstreamRemote, StringComparison.Ordinal) ||
             !string.Equals(UpstreamTargetBranch.Trim(), _originalUpstreamBranch, StringComparison.Ordinal));

        private void UpdatePendingChangesCount()
        {
            var count = 0;
            foreach (var f in _fields)
            {
                if (f.HasPendingChange)
                    count++;
            }

            foreach (var r in RemotesDrafts)
            {
                if (r.HasPendingChange)
                    count++;
            }

            if (HasUpstreamPendingChange)
                count++;

            foreach (var p in ProtectedBranches)
            {
                if (p.HasPendingChange)
                    count++;
            }

            PendingChangesCount = count;
        }

        private static bool HasTargetScopeConflict(
            Models.GitConfigSnapshot baseSnapshot,
            Models.GitConfigSnapshot preSaveSnapshot,
            string key,
            Models.GitConfigWriteScope scope,
            out string detail)
        {
            detail = string.Empty;
            if (baseSnapshot == null)
                return false;

            var configScope = ConfigureLocalizer.ToConfigScope(scope);
            var baseList = baseSnapshot.GetEntriesInScope(key, configScope, includeIncluded: false);
            var liveList = preSaveSnapshot.GetEntriesInScope(key, configScope, includeIncluded: false);
            if (baseList.Count != liveList.Count)
            {
                detail = "保存前核对发现该作用域中的条目数量已在外部发生改变，已跳过覆盖。";
                return true;
            }

            if (baseList.Count == 1 && liveList.Count == 1 &&
                (!string.Equals(baseList[0].Value, liveList[0].Value, StringComparison.Ordinal) ||
                 baseList[0].HasValue != liveList[0].HasValue))
            {
                detail = $"保存前核对发现外部值已从 \"{baseList[0].DisplayValue}\" 变更为 \"{liveList[0].DisplayValue}\"，已跳过覆盖。";
                return true;
            }

            return false;
        }

        private static bool HasEffectiveValueChanged(Models.GitConfigSnapshot baseSnapshot, Models.GitConfigSnapshot preSaveSnapshot, string key)
        {
            if (baseSnapshot == null)
                return false;

            var b = baseSnapshot.GetEffectiveValue(key);
            var p = preSaveSnapshot.GetEffectiveValue(key);
            return !string.Equals(b, p, StringComparison.Ordinal);
        }

        private static string FormatCommandFailure(Commands.Command.Result result)
        {
            var err = result?.StdErr?.Trim();
            if (!string.IsNullOrEmpty(err))
                return err;

            var outMsg = result?.StdOut?.Trim();
            if (!string.IsNullOrEmpty(outMsg))
                return outMsg;

            return $"Git 命令失败（退出码 {result?.ExitCode ?? -1}）。";
        }

        private async Task ApplyIssueTrackerChangesAsync()
        {
            var changed = false;
            var oldRules = new Dictionary<string, Models.IssueTracker>();
            foreach (var rule in _repo.IssueTrackers)
                oldRules.Add(rule.Name, rule);

            foreach (var rule in IssueTrackers)
            {
                if (oldRules.TryGetValue(rule.Name, out var old))
                {
                    if (old.IsShared != rule.IsShared)
                    {
                        changed = true;
                        await new Commands.IssueTracker(_repo.FullPath, old.IsShared).RemoveAsync(old.Name);
                        await new Commands.IssueTracker(_repo.FullPath, rule.IsShared).AddAsync(rule);
                    }
                    else
                    {
                        if (!old.RegexString.Equals(rule.RegexString, StringComparison.Ordinal))
                        {
                            changed = true;
                            await new Commands.IssueTracker(_repo.FullPath, old.IsShared).UpdateRegexAsync(rule);
                        }

                        if (!old.URLTemplate.Equals(rule.URLTemplate, StringComparison.Ordinal))
                        {
                            changed = true;
                            await new Commands.IssueTracker(_repo.FullPath, old.IsShared).UpdateURLTemplateAsync(rule);
                        }
                    }

                    oldRules.Remove(rule.Name);
                }
                else
                {
                    changed = true;
                    await new Commands.IssueTracker(_repo.FullPath, rule.IsShared).AddAsync(rule);
                }
            }

            if (oldRules.Count > 0)
            {
                changed = true;
                foreach (var kv in oldRules)
                    await new Commands.IssueTracker(_repo.FullPath, kv.Value.IsShared).RemoveAsync(kv.Key);
            }

            if (changed)
            {
                _repo.IssueTrackers.Clear();
                _repo.IssueTrackers.AddRange(IssueTrackers);
            }
        }

        private readonly Repository _repo;
        private readonly ConfigFieldItem[] _fields;
        private readonly Task _loadTask;
        private Models.GitConfigSnapshot _snapshot;
        private List<Models.Branch> _queriedBranches = [];
        private Models.GitConfigWriteScope _targetScope = Models.GitConfigWriteScope.Local;
        private bool _worktreeConfigEnabled;
        private bool _isLoading;
        private bool _isSaving;
        private bool _snapshotReadFailed;
        private string _snapshotError = string.Empty;
        private RemoteConfigDraft _selectedRemoteDraft;
        private string _newRemoteName = string.Empty;
        private string _newRemoteUrl = string.Empty;
        private string _remoteStagingError = string.Empty;
        private string _currentBranchName = string.Empty;
        private string _originalUpstreamRemote = string.Empty;
        private string _originalUpstreamBranch = string.Empty;
        private string _selectedUpstreamRemote = string.Empty;
        private string _upstreamTargetBranch = string.Empty;
        private string _effectiveUpstreamSummary = string.Empty;
        private bool _isUpstreamUnfetched;
        private string _signingProgramReadOnly = string.Empty;
        private string _coreEolReadOnly = string.Empty;
        private bool _signingStatusHasIssue;
        private bool _signingStatusIsReady;
        private bool _signingStatusIsDisabled = true;
        private string _signingStatusMessage = string.Empty;
        private string _signingStatusSecondaryNote = string.Empty;
        private string _sanitizedDiagnostics = string.Empty;
        private int _pendingChangesCount;
        private ConfigSaveSummary _saveFeedback = new();
        private bool _syncingOptions;
        private Models.CommitTemplate _selectedCommitTemplate = null;
        private Models.IssueTracker _selectedIssueTracker = null;
        private void ApplyProtectedBranches(IReadOnlyList<Models.BranchProtectionStatus> candidates)
        {
            ProtectedBranches.Clear();
            if (candidates != null)
            {
                foreach (var status in candidates)
                {
                    var item = new BranchProtectionOptionItem(
                        status.BranchName,
                        status.Reasons.Count > 0 ? string.Join(" · ", status.Reasons) : App.Text("Configure.Git.BranchProtectionReasonDefault"),
                        status.IsLocallyUnlocked,
                        UpdatePendingChangesCount);
                    ProtectedBranches.Add(item);
                }
            }

            OnPropertyChanged(nameof(HasProtectedBranches));
            UpdatePendingChangesCount();
        }

        private async Task SaveBranchProtectionOverridesAsync(List<ConfigSaveItemResult> results)
        {
            foreach (var item in ProtectedBranches)
            {
                if (!item.HasPendingChange)
                    continue;

                _repo.Settings.SetBranchProtectionDisabled(item.BranchName, item.AllowDestructiveHistoryRewrite);
                await Commands.BranchProtectionPolicy.SetLocalOverrideAsync(
                    _repo.FullPath,
                    item.BranchName,
                    item.AllowDestructiveHistoryRewrite);
                item.CommitSavedState();
                results.Add(new ConfigSaveItemResult
                {
                    TargetName = $"branch-protection.{item.BranchName}",
                    Succeeded = true,
                    Detail = App.Text("Configure.Git.BranchProtectionSaved"),
                });
            }
        }

        private void OnRepoConfigurationStateChanged()
        {
            Dispatcher.UIThread.Post(() => _ = CheckExternalChangesAsync());
        }

        private void ClearExternalChangeNotice()
        {
            _pendingExternalSnapshot = null;
            _pendingExternalBranches = null;
            _pendingExternalProtection = null;
            ExternalConfigDiffItems.Clear();
            ExternalChangeHeadline = string.Empty;
            ExternalChangeDetail = string.Empty;
            HasExternalConfigChanges = false;
        }

        private void PresentExternalChangeDiffBanner(
            Models.GitConfigSnapshot baselineSnapshot,
            Models.GitConfigSnapshot liveSnapshot,
            List<Models.Branch> liveBranches,
            IReadOnlyList<Models.BranchProtectionStatus> liveProtection)
        {
            _pendingExternalSnapshot = liveSnapshot;
            _pendingExternalBranches = liveBranches;
            _pendingExternalProtection = liveProtection;

            var diffs = BuildExternalDiffItems(baselineSnapshot, liveSnapshot);
            ExternalConfigDiffItems.Clear();
            foreach (var item in diffs)
                ExternalConfigDiffItems.Add(item);

            var changedFiles = GetChangedFingerprintNames(baselineSnapshot, liveSnapshot);
            var branchNote = !string.Equals(baselineSnapshot.CurrentBranch, liveSnapshot.CurrentBranch, StringComparison.Ordinal)
                ? $"当前分支由 {FormatBranchLabel(baselineSnapshot.CurrentBranch)} 切换为 {FormatBranchLabel(liveSnapshot.CurrentBranch)}（includeIf 条件生效项已刷新）"
                : string.Empty;
            var fileNote = changedFiles.Count > 0
                ? $"变更文件：{string.Join("、", changedFiles)}"
                : string.Empty;

            ExternalChangeHeadline = ConfigureLocalizer.Text(
                "Configure.External.BannerHeadline",
                "检测到外部配置或当前分支（includeIf）已变化，且当前存在未保存草稿");
            ExternalChangeDetail = string.Join("；", new[] { branchNote, fileNote }.Where(s => !string.IsNullOrEmpty(s)));
            HasExternalConfigChanges = true;
        }

        private List<ExternalConfigDiffItem> BuildExternalDiffItems(
            Models.GitConfigSnapshot baseSnapshot,
            Models.GitConfigSnapshot liveSnapshot)
        {
            var items = new List<ExternalConfigDiffItem>();
            var unsetLabel = ConfigureLocalizer.Text("Configure.External.UnsetValue", "(未设置)");

            if (!string.Equals(baseSnapshot.CurrentBranch, liveSnapshot.CurrentBranch, StringComparison.Ordinal))
            {
                items.Add(new ExternalConfigDiffItem
                {
                    TargetName = "HEAD (includeIf)",
                    ScopeLabel = "Branch",
                    PreviousDisplay = FormatBranchLabel(baseSnapshot.CurrentBranch),
                    ExternalDisplay = FormatBranchLabel(liveSnapshot.CurrentBranch),
                    HasUserConflict = HasUpstreamPendingChange,
                    DraftDisplay = HasUpstreamPendingChange ? $"{SelectedUpstreamRemote}/{UpstreamTargetBranch}".Trim('/') : string.Empty,
                });
            }

            if (baseSnapshot.WorktreeConfigEnabled != liveSnapshot.WorktreeConfigEnabled)
            {
                items.Add(new ExternalConfigDiffItem
                {
                    TargetName = "extensions.worktreeConfig",
                    ScopeLabel = "Local",
                    PreviousDisplay = baseSnapshot.WorktreeConfigEnabled ? "true" : "false",
                    ExternalDisplay = liveSnapshot.WorktreeConfigEnabled ? "true" : "false",
                    HasUserConflict = TargetScope == Models.GitConfigWriteScope.Worktree && _fields.Any(f => f.HasPendingChange),
                    DraftDisplay = TargetScope == Models.GitConfigWriteScope.Worktree ? "当前目标为 --worktree" : string.Empty,
                });
            }

            var targetConfigScope = ConfigureLocalizer.ToConfigScope(TargetScope);
            foreach (var field in _fields)
            {
                var baseEffective = baseSnapshot.GetEffectiveEntry(field.Key);
                var liveEffective = liveSnapshot.GetEffectiveEntry(field.Key);
                var baseTargetEntries = baseSnapshot.GetEntriesInScope(field.Key, targetConfigScope, includeIncluded: false);
                var liveTargetEntries = liveSnapshot.GetEntriesInScope(field.Key, targetConfigScope, includeIncluded: false);

                var effectiveChanged = !AreEntriesEquivalent(baseEffective, liveEffective);
                var targetChanged = !AreEntryListsEquivalent(baseTargetEntries, liveTargetEntries);
                var isConflict = _unreviewedConflictKeys.Contains(field.Key) || (field.HasPendingChange && (effectiveChanged || targetChanged));

                if (!effectiveChanged && !targetChanged && !isConflict)
                    continue;

                var prevDisplay = baseEffective?.DisplayValue;
                if (string.IsNullOrEmpty(prevDisplay) && baseEffective == null)
                    prevDisplay = unsetLabel;
                else if (baseEffective is { HasValue: true, Value.Length: 0 })
                    prevDisplay = "\"\"";

                var extDisplay = liveEffective?.DisplayValue;
                if (string.IsNullOrEmpty(extDisplay) && liveEffective == null)
                    extDisplay = unsetLabel;
                else if (liveEffective is { HasValue: true, Value.Length: 0 })
                    extDisplay = "\"\"";

                if (!effectiveChanged && targetChanged)
                {
                    prevDisplay = baseTargetEntries.Count == 0 ? unsetLabel : string.Join(", ", baseTargetEntries.Select(e => e.DisplayValue));
                    extDisplay = liveTargetEntries.Count == 0 ? unsetLabel : string.Join(", ", liveTargetEntries.Select(e => e.DisplayValue));
                }

                items.Add(new ExternalConfigDiffItem
                {
                    TargetName = field.Key,
                    ScopeLabel = liveEffective != null && effectiveChanged
                        ? liveEffective.Scope.ToString()
                        : TargetScope.ToString(),
                    PreviousDisplay = prevDisplay ?? unsetLabel,
                    ExternalDisplay = extDisplay ?? unsetLabel,
                    ExternalOrigin = liveEffective?.Origin ?? string.Empty,
                    HasUserConflict = isConflict,
                    DraftDisplay = isConflict ? field.DescribeCurrentDraft() : string.Empty,
                });
            }

            var baseRemotes = baseSnapshot.GetRemotes().ToDictionary(r => r.Name, StringComparer.Ordinal);
            var liveRemotes = liveSnapshot.GetRemotes().ToDictionary(r => r.Name, StringComparer.Ordinal);
            var allRemoteNames = baseRemotes.Keys.Union(liveRemotes.Keys, StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal);
            foreach (var rName in allRemoteNames)
            {
                baseRemotes.TryGetValue(rName, out var bRemote);
                liveRemotes.TryGetValue(rName, out var lRemote);

                var bFetch = bRemote?.FetchUrls.FirstOrDefault()?.DisplayValue ?? unsetLabel;
                var lFetch = lRemote?.FetchUrls.FirstOrDefault()?.DisplayValue ?? unsetLabel;
                var bPush = bRemote?.PushUrls.FirstOrDefault()?.DisplayValue ?? unsetLabel;
                var lPush = lRemote?.PushUrls.FirstOrDefault()?.DisplayValue ?? unsetLabel;
                if ((bRemote == null) == (lRemote == null) &&
                    string.Equals(bFetch, lFetch, StringComparison.Ordinal) &&
                    string.Equals(bPush, lPush, StringComparison.Ordinal))
                    continue;

                var draft = RemotesDrafts.FirstOrDefault(r =>
                    (!r.IsNew && string.Equals(r.OriginalName, rName, StringComparison.Ordinal)) ||
                    string.Equals(r.Name.Trim(), rName, StringComparison.Ordinal));
                var hasDraftConflict = draft != null && draft.HasPendingChange;
                items.Add(new ExternalConfigDiffItem
                {
                    TargetName = $"remote.{rName}",
                    ScopeLabel = "Local",
                    PreviousDisplay = bRemote == null ? unsetLabel : bFetch,
                    ExternalDisplay = lRemote == null ? unsetLabel : lFetch,
                    HasUserConflict = hasDraftConflict,
                    DraftDisplay = hasDraftConflict
                        ? (draft!.IsMarkedForRemoval ? "(待删除)" : draft.PrimaryFetchUrl)
                        : string.Empty,
                });
            }

            return items;
        }

        private CapturedConfigDrafts CaptureAllPendingDrafts()
        {
            var captured = new CapturedConfigDrafts();
            foreach (var field in _fields)
            {
                if (field.HasPendingChange)
                    captured.Fields[field.Key] = field.CaptureDraftState();
            }

            foreach (var draft in RemotesDrafts)
            {
                if (draft.HasPendingChange)
                    captured.Remotes.Add(draft.CaptureDraftState());
            }

            if (HasUpstreamPendingChange && !IsDetachedHead)
            {
                captured.HasUpstreamDraft = true;
                captured.UpstreamRemote = SelectedUpstreamRemote;
                captured.UpstreamBranch = UpstreamTargetBranch;
            }

            foreach (var prot in ProtectedBranches)
            {
                if (prot.HasPendingChange)
                    captured.BranchProtectionDrafts[prot.BranchName] = prot.AllowDestructiveHistoryRewrite;
            }

            return captured;
        }

        private void RestoreCapturedDrafts(
            CapturedConfigDrafts captured,
            bool restoreAll,
            HashSet<string> failedOrConflictTargets)
        {
            if (captured == null)
                return;

            foreach (var field in _fields)
            {
                if (!captured.Fields.TryGetValue(field.Key, out var memento))
                    continue;

                if (restoreAll || (failedOrConflictTargets != null && failedOrConflictTargets.Contains(field.Key)))
                    field.RestoreDraftState(memento);
            }

            foreach (var remMemento in captured.Remotes)
            {
                var shouldRestore = restoreAll || IsRemoteDraftMatched(remMemento, failedOrConflictTargets);
                if (!shouldRestore)
                    continue;

                if (remMemento.IsNew)
                {
                    var newDraft = RemoteConfigDraft.CreateNew(remMemento.Name, remMemento.PrimaryFetchUrl);
                    newDraft.StateChanged += OnAnyDraftStateChanged;
                    newDraft.RestoreDraftState(remMemento);
                    RemotesDrafts.Add(newDraft);
                    SelectedRemoteDraft = newDraft;
                }
                else
                {
                    var existing = RemotesDrafts.FirstOrDefault(r =>
                        !r.IsNew && string.Equals(r.OriginalName, remMemento.OriginalName, StringComparison.Ordinal));
                    if (existing != null)
                        existing.RestoreDraftState(remMemento);
                }
            }

            if (captured.HasUpstreamDraft && !IsDetachedHead &&
                (restoreAll || (failedOrConflictTargets != null && failedOrConflictTargets.Contains($"branch.{CurrentBranchName}.upstream"))))
            {
                SelectedUpstreamRemote = captured.UpstreamRemote ?? string.Empty;
                UpstreamTargetBranch = captured.UpstreamBranch ?? string.Empty;
            }

            if (restoreAll)
            {
                foreach (var prot in ProtectedBranches)
                {
                    if (captured.BranchProtectionDrafts.TryGetValue(prot.BranchName, out var allow))
                        prot.AllowDestructiveHistoryRewrite = allow;
                }
            }

            OnPropertyChanged(nameof(HasNoRemotes));
            UpdatePendingChangesCount();
        }

        private static bool IsRemoteDraftMatched(RemoteConfigDraftMemento rem, HashSet<string> targets)
        {
            if (targets == null || targets.Count == 0)
                return false;

            var candidates = new[]
            {
                $"remote.{rem.OriginalName}",
                $"remote.{rem.OriginalName}.url",
                $"remote.{rem.OriginalName}.pushurl",
                $"remote.{rem.Name?.Trim()}",
                $"remote.{rem.Name?.Trim()}.url",
                $"remote.{rem.Name?.Trim()}.pushurl",
                $"remote.{rem.OriginalName} -> {rem.Name?.Trim()}",
            };

            return candidates.Any(targets.Contains);
        }

        private static bool HaveSnapshotsChanged(Models.GitConfigSnapshot a, Models.GitConfigSnapshot b)
        {
            if (a == null || b == null)
                return a != b;

            if (!string.Equals(a.CurrentBranch, b.CurrentBranch, StringComparison.Ordinal) ||
                a.WorktreeConfigEnabled != b.WorktreeConfigEnabled ||
                a.FileFingerprints.Count != b.FileFingerprints.Count ||
                a.Entries.Count != b.Entries.Count)
                return true;

            foreach (var kv in a.FileFingerprints)
            {
                if (!b.FileFingerprints.TryGetValue(kv.Key, out var bVal) ||
                    !string.Equals(kv.Value, bVal, StringComparison.Ordinal))
                    return true;
            }

            for (var i = 0; i < a.Entries.Count; i++)
            {
                var ae = a.Entries[i];
                var be = b.Entries[i];
                if (!string.Equals(ae.NormalizedKey, be.NormalizedKey, StringComparison.Ordinal) ||
                    !string.Equals(ae.Value, be.Value, StringComparison.Ordinal) ||
                    ae.HasValue != be.HasValue ||
                    ae.Scope != be.Scope ||
                    ae.IsIncluded != be.IsIncluded ||
                    !string.Equals(ae.OriginPath, be.OriginPath, StringComparison.Ordinal))
                    return true;
            }

            return false;
        }

        private static List<string> GetChangedFingerprintNames(Models.GitConfigSnapshot a, Models.GitConfigSnapshot b)
        {
            var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
            var allPaths = new HashSet<string>(a.FileFingerprints.Keys, comparer);
            allPaths.UnionWith(b.FileFingerprints.Keys);

            var changed = new List<string>();
            foreach (var path in allPaths)
            {
                a.FileFingerprints.TryGetValue(path, out var av);
                b.FileFingerprints.TryGetValue(path, out var bv);
                if (!string.Equals(av, bv, StringComparison.Ordinal))
                    changed.Add(Path.GetFileName(path));
            }

            return changed.Distinct(comparer).ToList();
        }

        private static bool AreEntriesEquivalent(Models.GitConfigEntry a, Models.GitConfigEntry b)
        {
            if (a == null && b == null)
                return true;
            if (a == null || b == null)
                return false;

            return a.Scope == b.Scope &&
                   a.HasValue == b.HasValue &&
                   string.Equals(a.Value, b.Value, StringComparison.Ordinal) &&
                   string.Equals(a.OriginPath, b.OriginPath, StringComparison.Ordinal);
        }

        private static bool AreEntryListsEquivalent(IReadOnlyList<Models.GitConfigEntry> a, IReadOnlyList<Models.GitConfigEntry> b)
        {
            if (a.Count != b.Count)
                return false;

            for (var i = 0; i < a.Count; i++)
            {
                if (!AreEntriesEquivalent(a[i], b[i]))
                    return false;
            }

            return true;
        }

        private static string FormatBranchLabel(string branch)
            => string.IsNullOrWhiteSpace(branch) ? "(HEAD detached / unborn)" : branch;

        private sealed class CapturedConfigDrafts
        {
            public Dictionary<string, ConfigFieldDraftMemento> Fields { get; } = new(StringComparer.Ordinal);
            public List<RemoteConfigDraftMemento> Remotes { get; } = [];
            public bool HasUpstreamDraft { get; set; }
            public string UpstreamRemote { get; set; } = string.Empty;
            public string UpstreamBranch { get; set; } = string.Empty;
            public Dictionary<string, bool> BranchProtectionDrafts { get; } = new(StringComparer.Ordinal);
        }

        private long _loadGeneration;
        private bool _externalMonitorAttached;
        private bool _hasExternalConfigChanges;
        private string _externalChangeHeadline = string.Empty;
        private string _externalChangeDetail = string.Empty;
        private Models.GitConfigSnapshot _pendingExternalSnapshot;
        private List<Models.Branch> _pendingExternalBranches;
        private IReadOnlyList<Models.BranchProtectionStatus> _pendingExternalProtection;
        private readonly HashSet<string> _unreviewedConflictKeys = new(StringComparer.OrdinalIgnoreCase);
    }

    public sealed class BranchProtectionOptionItem : ObservableObject
    {
        public string BranchName { get; }
        public string ReasonsDisplay { get; }

        public bool AllowDestructiveHistoryRewrite
        {
            get => _allowDestructiveHistoryRewrite;
            set
            {
                if (SetProperty(ref _allowDestructiveHistoryRewrite, value))
                {
                    OnPropertyChanged(nameof(HasPendingChange));
                    _onChanged?.Invoke();
                }
            }
        }

        public bool HasPendingChange => _allowDestructiveHistoryRewrite != _initialAllowDestructiveHistoryRewrite;

        public BranchProtectionOptionItem(
            string branchName,
            string reasonsDisplay,
            bool initialAllowed,
            Action onChanged)
        {
            BranchName = branchName;
            ReasonsDisplay = reasonsDisplay;
            _initialAllowDestructiveHistoryRewrite = initialAllowed;
            _allowDestructiveHistoryRewrite = initialAllowed;
            _onChanged = onChanged;
        }

        public void CommitSavedState()
        {
            _initialAllowDestructiveHistoryRewrite = _allowDestructiveHistoryRewrite;
            OnPropertyChanged(nameof(HasPendingChange));
        }

        private readonly Action _onChanged;
        private bool _initialAllowDestructiveHistoryRewrite;
        private bool _allowDestructiveHistoryRewrite;
    }
}
