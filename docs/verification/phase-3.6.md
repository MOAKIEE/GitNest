# Phase 3.6 Verification Record (History Reset, Lease Remote Rewrite & Recovery)

## Batch 3.6a Scope (Core Services, Protection Policy & Multi-Clone Integration Verification)

### Implemented Components
- `src/Models/RepositorySettings.cs`: Added repository-common-dir `UnprotectedBranches` storage plus `IsBranchProtectionDisabled(...)` / `SetBranchProtectionDisabled(...)` helpers.
- `src/Models/OperationSafetyModels.cs`: Extended `GitBackupRestoreResult` with `BlockedByDirtyState`, `BlockedByNewerCommits`, `BlockedBySnapshot`, `UpdatedExistingBranch`, and `CheckedOutWorktreeUpdated`.
- `src/Models/HistoryResetModels.cs`: Added structured preflight/execution/recovery models (`ResetCommitMode`, `BranchProtectionStatus`, `HistoryResetPreflight`, `RemoteRewritePreflight`, `ResetWithoutCheckoutPreflight`, `HistoryResetOutcome`, `BackupResetRestorePreflight`, `RemoteRewriteJournalVerificationResult`).
- `src/Commands/BranchProtectionPolicy.cs`: Added default branch protection inspection across `main`, `master`, `develop`, and all `<remote>/HEAD` default targets, with explicit single-repo per-branch opt-out.
- `src/Commands/HistoryResetService.cs`: Implemented:
  - First-parent ancestor commit reset inspection (`--mixed` / `--hard` only; blocks unborn/detached HEAD, in-progress states, root-commit empty-repo resets, merged side-branch commits off `--first-parent`, dirty worktree/index, and untracked/`.gitignore`-ignored file/dir overwrite collisions without calling `git clean`).
  - Full topological removed-commit calculation across merge nodes (`<target>..HEAD`) and multi-author attribution (`LocalAuthors`, `HasOtherAuthorsInLocal`).
  - Live `ls-remote` precheck separating local vs remote removed commits, explicit multi-remote unpushed disambiguation, `"远端状态未知"` offline guarding, and blocking unintegrated/diverged remote tips.
  - Guarded execution creating `refs/gitnest/backups/<opId>` before resetting, explicit `--force-with-lease=refs/heads/<remoteBranch>:<expectedOid>` with pinned confirmation tip immunity to background fetch, collaborator push preservation (`"本地已撤回，远端未更新"` + `PartialSuccess`), and single-attempt ambiguous timeout verification via `ls-remote` (`PendingVerification` on unreachable probe).
  - Non-checkout branch pointer reset guards (`InspectWithoutCheckoutResetAsync` / `ExecuteWithoutCheckoutResetAsync`) blocking current branch, linked-worktree occupied branches, and protected branch backward moves.
  - Checked reset-backup recovery (`InspectBackupRestoreForResetAsync` / `RestoreFromBackupAsync`) refusing direct branch overwrite when worktree is dirty, collisions exist, or subsequent commits appeared, while offering unique `recover/...` new-branch recovery; plus explicit live verification (`VerifyRemoteRewriteJournalRecordAsync`).
- `src/Commands/OperationJournalService.cs`: Added `Reset` / `RewriteRemoteHistory` startup reconciliation notes comparing live local branch tips and tracking cache against recorded `OriginalTipSha`, `TargetTipSha`, and `ExpectedRemoteTipSha`.

### Automated Verification Executed After Final Batch 3.6a Edit
- `.\build\scripts\build-gitnest.ps1`: Release build succeeded with **0 warnings, 0 errors**.
- `.\build\scripts\test-gitnest.ps1`: **302 / 302** integration checks passed (covering 28 new Batch 3.6a multi-clone bare-origin / collaborator / worktree / backup-recovery / ambiguous-timeout checks in `tests/GitNest.IntegrationTests/HistoryResetRewriteTests.cs`).

### Completed in Batch 3.6b
- **Native UI & Workflows**:
  - `src/ViewModels/Reset.cs`, `src/Views/Reset.axaml`, `src/Views/Reset.axaml.cs`: Refactored to strictly 2 reset modes (`KeepCodeMixed` / `--mixed`, `DiscardCodeHard` / `--hard`), preflight collision blocking (untracked/ignored without `git clean`), full `target..HEAD` cross-merge & multi-author attribution cards, distinct multi-remote unpushed summary, guarded pinned `--force-with-lease` remote rewrite option, and non-auto-closing partial failure / pending verification card (`BackupRef` chip + `打开操作与恢复日志` action).
  - `src/ViewModels/RewriteRemoteHistory.cs`, `src/Views/RewriteRemoteHistory.axaml`, `src/Views/RewriteRemoteHistory.axaml.cs`: Added standalone `"改写远程分支历史..."` dialog with live `ls-remote` precheck, offline `"远端状态未知"` / diverged guards, `BranchProtectionPolicy` enforcement, explicit pinned lease preview, and recovery journal shortcut.
  - `src/ViewModels/ResetWithoutCheckout.cs`, `src/Views/ResetWithoutCheckout.axaml`, `src/Views/ResetWithoutCheckout.axaml.cs`: Wired `InspectWithoutCheckoutResetAsync` / `ExecuteWithoutCheckoutResetAsync` blocking active branch, other-worktree checked-out branches, and protected branch backward moves while creating safety backup refs.
  - `src/Commands/Push.cs`, `src/ViewModels/PushRevision.cs`, `src/Views/PushRevision.axaml`: Added explicit `expectedLeaseTipSha` constructor overload in `Commands.Push`; `PushRevision` validates live `ls-remote` state & `BranchProtectionPolicy` whenever force push is enabled.
  - `src/Commands/BranchProtectionPolicy.cs`, `src/ViewModels/RepositoryConfigure.cs`, `src/Views/RepositoryConfigure.axaml`: Added fast protection lookup (`InspectProtectedCandidatesAsync` / `IsBranchEffectivelyProtectedFast`) and per-repository explicit branch-protection override toggles in `仓库配置 -> Git` (`保存设置` persists `UnprotectedBranches` via `_repo.Settings`).
  - `src/Views/ControlExtensions.cs`, `src/Views/Histories.axaml.cs`, `src/Views/BranchTree.axaml.cs`: Added shared context-menu factory (`CreateRewriteRemoteMenuItem`) and gated `"改写远程分支历史..."` / reset items cleanly.
  - `src/ViewModels/ViewLogs.cs`, `src/Views/ViewLogs.axaml`, `src/Views/ViewLogs.axaml.cs`, `src/Commands/OperationJournalService.cs`: Added `"联网核对远端状态"` verification button, safe reset backup restoration card (`RestoreModeNewBranch` default + guarded `RestoreModeExistingBranch` with newer-commit warning), selection-refresh race fix, and punctuation cleanup.
- **Localization (`src/Resources/Locales/en_US.axaml`, `zh_CN.axaml`)**: Expanded symmetrically to **1624** keys on both sides (`diff=0`).
- **UIProbe & Visual Fixes (`tests/GitNest.UIProbe/ResetRewriteProbe.cs`, `Program.cs`, `build/scripts/probe-gitnest.ps1`)**:
  - Added `-ResetRewrite` (`phase-3.6`) covering 14 interactive assertions and 14 screenshots (`Light`, `Dark`, and narrow width).
  - Visual screenshot inspection identified and resolved 4 UI issues prior to closeout: missing warning glyph (`Icons.Warning` -> `Icons.Error` warning triangle), narrow-window partial failure scroll clipping (promoted `BackupRef` & recovery shortcut above stacked steps and deduplicated stderr), oversized commit list pushing remote lock notice offscreen in `Reset` (`MaxHeight="132"` scroller), and initial `ViewLogs` scroll position hiding bottom restore card + minor `。；` punctuation.

### Final Verification Executed After Last Batch 3.6b Code Edit
- `.\build\scripts\probe-gitnest.ps1 -ResetRewrite`: **14 / 14** probe checks passed; all 14 regenerated screenshots in `artifacts/probes/phase-3.6/` inspected cleanly.
- `.\build\scripts\test-gitnest.ps1`: **302 / 302** integration checks passed.
- `.\build\scripts\build-gitnest.ps1 -Publish`: Release build & Windows x64 publish succeeded with **0 warnings, 0 errors** (`artifacts/windows-x64/GitNest.exe`).
