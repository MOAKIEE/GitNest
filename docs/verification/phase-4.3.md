# Phase 4.3 Verification Record (Section 10 Full Real-Git Acceptance Audit & Release Sign-Off)

## 1. Added Edge-Case Real-Git Checks (`tests/GitNest.IntegrationTests/Phase43AcceptanceTests.cs`)
Wired `RunPhase43AcceptanceTestsAsync` into `tests/GitNest.IntegrationTests/Program.cs` to close remaining explicit edge-case verifications from `docs/design.md` §10:
- **Missing / Invalid Git Inspection & Recovery (`GitInstallation.InspectAsync` / `Native.OS.UseGitInstallation`)**: Verifies nonexistent Git path returns `Missing` (`IsReady = false`), corrupt PE stub and non-Git system executable (`where.exe`) fail cleanly without corrupting readiness, and selecting valid Git for Windows immediately restores `Ready` state with `Version >= GitVersions.MINIMAL`.
- **Empty (Unborn) Repository Safe Discovery**: Verifies freshly initialized empty repo (`git init -b main` with zero commits) returns 0 commits, 0 branches, 0 tags, and 0 stashes without throwing.
- **Large History (650 Commits) & Search**: Generates 650 consecutive commits via real `git fast-import`, verifies bounded history query (`-200`), full query (`-1000` -> 650 entries, newest commit first), and message search (`CommitSearchMethod.ByMessage`) locating specific entries accurately.
- **Unescaped CJK/Special-Character Paths & Binary Diffs**: Verifies `CompareRevisions`, `Diff`, and `IsBinary` on `"文档/中文 配置 [v1]&说明.txt"` and `"assets/图标 [测试].bin"` (containing NUL bytes): text diff parses UTF-8 added lines (`TextDiff.Lines`) while binary files report `IsBinary = true` and `TextDiff = null` cleanly without text corruption.
- **Authentication Cancel, Local HTTP 401 Failure & Credential Redaction**:
  - Real per-repo `core.sshCommand` simulating `ASKPASS_CANCELLED` triggers `PushOutcomeKind.AuthCanceled` with explicit cancellation feedback.
  - Local loopback `TcpListener` returning genuine `HTTP/1.1 401 Unauthorized` triggers explicit authentication failure on push, leaving repository `HEAD`, local commits, and clean working tree intact.
  - Local commit uses `user.name`/`user.email` independent of credentialed remote URLs; `FetchExecutionResult.DiagnosticDetail`, `PushExecutionResult.DiagnosticDetail`/`Summary`, and persisted `OperationJournalStore` JSON never expose embedded remote tokens or `http.extraheader` secrets.

## 2. Design §10 Acceptance Coverage Matrix
| Section 10 Requirement Area | Verified Coverage Suites |
| --- | --- |
| Staging, partial staging, unborn safety, commit-without-repeat on push fail | `CommitSyncTests.cs`, `Probe --commit-sync` (11/11) |
| Local vs remote branch deletion, lease check, worktree cleanup ordering & partial results | `BranchDeleteTests.cs`, `WorktreeDeleteTests.cs`, `Probe --branch-delete` (13/13), `Probe --worktree-delete` (20/20) |
| Collaborator concurrent push lease rejection, backup ref recovery | `BranchDeleteTests.cs`, `HistoryResetRewriteTests.cs`, `OperationSafetyTests.cs` |
| Directed Merge (`src -> target`), abort cleanup & AI-intervention stop; external conflict preservation | `MergeRevertTests.cs`, `Probe --merge-revert` (11/11) |
| Forward-only / diverged Pull, missing/deleted upstream, multi-remote isolation | `CommitSyncTests.cs`, `HistoryResetRewriteTests.cs`, `Probe --commit-sync` (11/11) |
| History Reset (`--mixed`/`--hard`), root/merge/collision checks, `--force-with-lease`, restart recovery | `HistoryResetRewriteTests.cs`, `OperationSafetyTests.cs`, `Probe --reset-rewrite` (14/14), `Probe --operation-recovery` (13/13) |
| Stash lifecycle, conflict retention, Discard dual baselines, 30-day recovery store, Recycle Bin & Ignore rules | `StashDiscardIgnoreTests.cs`, `Probe --stash-discard-ignore` (15/15) |
| Config inheritance, `includeIf`, multi-value helpers, worktree config scope, lock protection & external diff banner | `Program.cs`, `ConfigTests.cs`, `Phase37SyncTests.cs`, `Probe --config` (13/13), `Probe --config-refresh` (4/4) |
| Task overview aliases, worktree path resolution, accumulated task comparison & stale-snapshot invalidation | `WorktreeTests.cs`, `ComparisonTests.cs`, `Probe --comparison` (30/30) |
| CJK/special paths, binary diffs, unborn repo, large history (650 commits), missing Git inspection & auth feedback/redaction | `Phase43AcceptanceTests.cs`, `CommitSyncTests.cs`, `ComparisonTests.cs` |
| v1 surface trimming, 32-package NuGet/license parity, High-DPI `PerMonitorV2` & x64 user installer/upgrade/uninstall | `Phase41TrimTests.cs`, `Probe --trim-surface` (24/24), `Probe --high-dpi` (7/7), `verify-installer.ps1` (38/38) |

## 3. Final Clean Build, Test, Probe & Installer Verification Results
All runs executed sequentially after the final code edit cleanly with zero leftover temporary files, branches, or test directories:
- `.\build\scripts\build-gitnest.ps1 -Publish`: **0 warnings, 0 errors** -> `artifacts/windows-x64/GitNest.exe`.
- `.\build\scripts\test-gitnest.ps1`: **388 / 388** real-Git integration checks passed.
- **All 12 UIProbe suites passed**:
  - `-Comparison` (incl. Overview): **30 / 30**
  - `-Config`: **13 / 13**
  - `-ConfigRefresh`: **4 / 4**
  - `-CommitSync`: **11 / 11**
  - `-OperationRecovery`: **13 / 13**
  - `-BranchDelete`: **13 / 13**
  - `-WorktreeDelete`: **20 / 20**
  - `-MergeRevert`: **11 / 11**
  - `-StashDiscardIgnore`: **15 / 15**
  - `-ResetRewrite`: **14 / 14**
  - `-TrimSurface`: **24 / 24**
  - `-HighDpi`: **7 / 7**
- `.\build\scripts\build-installer.ps1 -SkipPublish`: Built `artifacts/installer/GitNest-Setup-0.1.0-dev-win-x64.exe` (**35.95 MB**, SHA-256 `2072815A74402B908A2340AAD605F115B93E4081E5CCD39F236DD41D8B53BEDF`).
- `.\build\scripts\verify-installer.ps1`: **38 / 38** clean-install, upgrade (`0.1.1-test`), uninstall, preservation, PE manifest, and exclusion checks passed.
- Cleaned temporary test artifacts (`%TEMP%\*GitNest*`, `artifacts/smoke/`) to Windows Recycle Bin (`0` remaining test items, single local `main` branch, single repository worktree).
