# Phase 3.7 Verification Record (Scoped Config Writes, External Review & Refresh Safety)

## Implemented Components

### 1. Scoped Config Lock Awareness (`config.lock`, `config.worktree.lock`, Global `.lock`)
- `src/Models/RepositoryWriteGuard.cs`: Added `checkConfigLock` inspection across `InspectLocksFromDirectories`, `InspectLocksAsync`, and `TryEnterAsync` checking `<gitDir>/config.worktree.lock` and `<commonDir>/config.lock` without deleting external lock files, plus `InspectGlobalConfigLock(...)` covering `GIT_CONFIG_GLOBAL`, `~/.gitconfig.lock`, and `$XDG_CONFIG_HOME` / `~/.config/git/config.lock`.
- `src/Commands/Config.cs` (`SetScopedAsync`), `src/Commands/Remote.cs` (`AddTargetedAsync`, `DeleteTargetedAsync`, `RenameTargetedAsync`, `SetUrlTargetedAsync`), `src/Commands/Branch.cs` (`SetUpstreamTargetedAsync`, `UnsetUpstreamTargetedAsync`), and `src/ViewModels/RepositoryConfigure.cs` (`SaveAsync`): Enforce write-guard/lock checks (`acquireWriteGuard: false` for inner batched calls) and report clear lock-block errors while preserving lock files and unsaved drafts.

### 2. External Config / `includeIf` Change Detection & Safe Draft Reconciliation
- `src/ViewModels/ConfigureModels.cs`: Added `ConfigFieldDraftMemento`, `RemoteConfigDraftMemento`, draft capture/restore/describe helpers, and `ExternalConfigDiffItem`.
- `src/ViewModels/RepositoryConfigure.cs`, `src/Views/RepositoryConfigure.axaml`, `src/Views/RepositoryConfigure.axaml.cs`:
  - Added `GlobalScopeTargetHint` surfaced in the Global scope warning banner so the exact destination `.gitconfig` path is visible prior to saving.
  - Added `_loadGeneration` concurrency guard in `ReloadAsync()`, `AttachExternalChangeMonitor` / `DetachExternalChangeMonitor` wired to window lifecycle (`Opened`, `Activated`, `Closed`) and `Repository.ConfigurationStateChanged`, plus `ManualRefreshAsync()`.
  - `CheckExternalChangesAsync()` auto-reloads silently when clean; when dirty drafts exist, retains all drafts and populates `ExternalConfigChangeBanner` (`ExternalConfigDiffItems`) with explicit `"丢弃冲突草稿并重新加载"` (`AcceptExternalReloadBtn`) and `"保留我的草稿（按最新外部状态重设基线）"` (`KeepDraftsExternalBtn`) actions.
  - `SaveAsync()` retains drafts and marks `_unreviewedConflictKeys` when individual items are skipped or fail due to external race.

### 3. Confirmation Snapshot Invalidation & Async Generation / Close Safety
- `src/Models/WorkingCopySafetyModels.cs`, `src/Commands/DiscardBackupService.cs`, `src/ViewModels/Discard.cs`: `InspectDiscardAsync` captures `OperationConfirmationSnapshot` via `CaptureOperationSnapshot.CaptureAsync`; `ExecuteDiscardAsync(..., expectedPreflight)` rejects execution (`BlockedBySnapshot = true`) when target files, HEAD, staged state, or repository identity drift while confirmation dialog is open, triggering reload in `Discard.Sure()`. Fixed `CaptureOperationSnapshot.CaptureDiskFileAsync` to read with `FileShare.ReadWrite | FileShare.Delete` and degrade gracefully on locked files rather than throwing `IOException`.
- `src/ViewModels/Reset.cs`, `src/Views/Reset.axaml`, `src/Views/Reset.axaml.cs`: Added `CanRefreshFromResult` and `RefreshFromResultAsync()` (`RefreshSnapshot` button) on non-mutating blocked/stale result cards.
- `src/Models/IRepository.cs`, `src/Models/Watcher.cs`, `src/ViewModels/Repository.cs`: Added config/worktree-admin watcher scheduling (`config`, `config.worktree`, `HEAD`, `IsWorktreeAdminChange`), `RefreshConfigurationState()` + `ConfigurationStateChanged`, generation tokens + `_isClosed` guards across all `Refresh*` UI dispatches, `Close()` null-safe guards, and `RefreshCommits()` + `RefreshConfigurationState()` on window activation.
- `src/Resources/Locales/en_US.axaml`, `src/Resources/Locales/zh_CN.axaml`: Expanded symmetrically to **1634** keys on both sides (`diff=0`).

## Automated Verification Executed After Last Code Edit
- `.\build\scripts\build-gitnest.ps1 -Publish`: Release build & Windows x64 publish succeeded with **0 warnings, 0 errors** (`artifacts/windows-x64/GitNest.exe`).
- `.\build\scripts\test-gitnest.ps1`: **320 / 320** integration checks passed (including 18 new checks in `tests/GitNest.IntegrationTests/Phase37SyncTests.cs` covering repo/worktree/global `.lock` blocking, clean auto-reload vs dirty `includeIf "onbranch:..."` external diff banner, keep-drafts vs accept-reload flows, stale `Discard` snapshot blocking, `Watcher.IsWorktreeAdminChange`, and `Repository.Close` cancellation).
- `.\build\scripts\probe-gitnest.ps1 -ConfigRefresh`: **4 / 4** probe checks passed; all 3 screenshots under `artifacts/probes/phase-3.7/` (`config-global-target-hint.png`, `config-external-diff-banner.png`, `config-keep-drafts-saved.png`) inspected cleanly with zero layout clipping or overlap.
- `.\build\scripts\probe-gitnest.ps1 -Config`: **13 / 13** regression probe checks passed cleanly.
