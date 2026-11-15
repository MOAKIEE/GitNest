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

### Remaining in Batch 3.6b
- Native SourceGit UI wiring (`Reset`, `ResetWithoutCheckout`, `Histories`, `BranchTree`, `Push`, `RepositoryConfigure`, `ViewLogs` operations reset/rewrite recovery), localization keys (`en_US`/`zh_CN`), UIProbe `-ResetRewrite`, Light/Dark visual screenshot verification, and final `gitnest.exe` publish.
