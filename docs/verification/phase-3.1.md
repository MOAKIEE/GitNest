# Phase 3.1 Verification Record（写操作串行化、确认快照、操作记录与恢复基础）

## Batch 3.1a — 统一确认快照、恢复引用生命周期、脱敏日志持久化与重启核查

### 范围与设计对齐
- 对齐 `docs/design.md` §4、§6.3、§7.2、§8、§8.1、§8.2 与 `docs/implementation-plan.md` 批次 3.1：
  - 新增 `src/Models/OperationSafetyModels.cs` 与 `src/Commands/CaptureOperationSnapshot.cs`：
    - 捕获仓库规范 `CanonicalGitDir` / `CanonicalCommonDir`、`HEAD`（OID、分支或 detached HEAD）、进行中状态（`Merge`/`Rebase`/`CherryPick`/`Revert`/`Am`/`Bisect`）、暂存区指纹、工作区未暂存/未跟踪/冲突标识、目标引用 OID 与目标文件磁盘 SHA-256 + 索引/HEAD blob OID；
    - 执行前在 `RepositoryWriteGuard` 锁内再次核对，发现目标文件篡改、工作区变脏、HEAD 移动、关注引用变更、产生冲突状态或存在 Git 原生 `.lock` 文件时立即拦截并提示“仓库状态已变化，请重新查看，原确认已失效”，绝不删除锁文件。
  - 新增 `src/Commands/BackupRefs.cs`：
    - 统一管理本地恢复引用 `refs/gitnest/backups/<operation-id>`（创建、`ExistsAsync`、`ListAsync`、同名占用保护恢复、30 天保留窗口预览与基于 `<expectedOid>` 条件删除）；
    - 验证 `refs/gitnest/backups/*` 彻底与 `refs/heads/*`、`refs/remotes/*`、`QueryBranches` 与默认分支列表隔离；
    - 计算恢复引用年龄优先依据恢复引用自身创建/reflog 时间而非所指提交本身日期，防止刚备份旧提交时误判过期；
    - 恢复分支时显式使用 `check-ref-format`、`show-ref` 与 `update-ref ... ""` 拒绝覆盖已被重新创建的同名分支。
  - 新增 `src/Models/OperationJournalStore.cs`（注册于 `src/App.JsonCodeGen.cs`）与 `src/Commands/OperationJournalService.cs`：
    - 原子写入（`.tmp` + `File.Move`）与线程锁保存脱敏操作记录（区分 `Preparing`、`AwaitingConfirmation`、`Executing`、`Succeeded`、`Failed`、`PartialSuccess`、`PendingVerification`、`CanceledUncertain`）；
    - 对标题、摘要、命令明细和异常详情统一经过 `GitConfigSnapshot.RedactText` 强脱敏，不留存明文密码或 URL userinfo 令牌；
    - 实现 `ReconcileOnOpenAsync` / `VerifyRecordStateAsync`，在进程异常中断重启重开仓库时只读核查真实 `HEAD`、分支 tip、远端跟踪引用、进行中冲突与恢复引用，给出真实核查说明，不自动覆盖后续外部新提交或冲突状态。
  - 完善 `src/Models/Watcher.cs` 与 `src/ViewModels/Repository.cs`：
    - `Watcher.IsOperationStateFile` 扩展主仓与 linked worktree 下的 `MERGE_HEAD`、`CHERRY_PICK_HEAD`、`REVERT_HEAD`、`REBASE_HEAD`、`rebase-merge/`、`rebase-apply/`、`BISECT_LOG` 状态变更触发刷新；
    - 将 `Repository` 的 `_autoFetchAnchorTime` 与真实 `_lastFetchTime` 拆离，确保从未执行过 fetch 的仓库 `LastFetchDisplayTime` 保持为空而不虚报开仓时间；新增 `PendingReconciliation` 与 `RefreshOnWindowActivated()` 底座。

### 验证命令与结果（Batch 3.1a）
- `.\build\scripts\build-gitnest.ps1`：Release 构建通过，`0` 个警告、`0` 个错误。
- `.\build\scripts\test-gitnest.ps1`：`tests/GitNest.IntegrationTests` 全量通过（**173 / 173** 项检查通过，含本次新增 `tests/GitNest.IntegrationTests/OperationSafetyTests.cs` 18 项真实 Git 检查）。

## Batch 3.1b — 最近操作与恢复 UI、同步日志接线、窗口激活刷新与 UI 探针

### 范围与设计对齐
- 前一批提交：`9753b6a8`（Batch 3.1a：写串行化、确认快照、备份引用与恢复日志引擎）。
- **同步操作日志接入**：
  - `src/Commands/GitSyncService.cs` 与 `src/Commands/OperationJournalService.cs`：将 `CommitAsync`、`FetchAsync`、`PullFastForwardAsync`、`PushSingleBranchAsync`、`CommitAndPushAsync` 与 `RetryPushSessionAsync` 接入显式可选的 `OperationJournalStore`（当 UI 路径传入 `OperationJournalStore.Instance` 时记录脱敏命令摘要、操作前/目标/远端预期 commit OID 与诊断信息；后台 Auto-Fetch 与未显式传参路径不写入全局持久化日志）；
  - `src/ViewModels/Fetch.cs`、`src/ViewModels/Pull.cs`、`src/ViewModels/Push.cs`、`src/ViewModels/WorkingCopy.cs`、`src/ViewModels/WorkingCopy.CommitSync.cs`：在用户主动发起的提交与同步入口传入 `OperationJournalStore.Instance`。
- **主窗口未完成告警、工具栏角标与窗口激活刷新**：
  - `src/ViewModels/Repository.cs` 与 `src/Views/Launcher.axaml.cs`：暴露 `GitCommonDir`、`ShowPendingRecoveryBanner` 与 `DismissPendingRecoveryBanner()`（若出现新的待核实操作键值则重新显现），并在主窗口激活时调用 `activeRepo.RefreshOnWindowActivated()` 发现外部 CLI 变更与刷新状态；
  - `src/Views/Repository.axaml` / `Repository.axaml.cs`：在状态摘要下方增加可暂不提醒的紧凑橙色提醒条（`查看与恢复` / `暂不提醒`）；
  - `src/Views/RepositoryToolbar.axaml` / `RepositoryToolbar.axaml.cs`：为日志按钮增加未处理/待核实操作提醒圆点角标，存在待核实记录时默认打开恢复页签。
- **SourceGit 原生“最近操作与恢复”双页签窗口（`ViewLogs`）**：
  - `src/ViewModels/ViewLogs.cs` 与 `src/Views/ViewLogs.axaml` / `ViewLogs.axaml.cs`：将原生 `ViewLogs` 扩展为 920×600（最小 740×480）“最近操作与恢复 / 原始命令日志”双页签界面，展示操作编号、UTC 转本地时间、分支/远端与前后 SHA、实测磁盘核对结论（`ReconciliationNote`）、单条/全量状态核查、`确认已完成/消除提醒`、`从备份恢复分支`（支持显式修改目标分支名并拒绝覆盖既有分支）、可折叠脱敏诊断输出与 30 天过期备份引用预览/确认清理区；
  - `src/Resources/Locales/en_US.axaml` 与 `src/Resources/Locales/zh_CN.axaml`：补充 39 组 `ViewLogs.*` / `OperationRecovery.*` 词条，XML `x:Key` 对称性校验 `en=1325 zh=1325 diff=0`。
- **端到端测试与探针暴露并修复的 3 处真实缺陷**：
  1. `src/Models/OperationJournalStore.cs`：`App.JsonCodeGen.DateTimeConverter` 将磁盘 JSON 时间读回为 `DateTimeKind.Local`（UTC+8 下与内存 `DateTime.UtcNow` 排序错位 8 小时），已增加 `EnsureUtc` 统一归一化并保持同秒倒序；
  2. `src/Models/OperationSafetyModels.cs`、`OperationJournalStore.cs`、`OperationJournalService.cs`、`ViewLogs.cs`：为 `GitOperationRecord` 增加 `IsAcknowledged` 标记，使得无结果 commit 的中断操作在用户确认已完成转为 `PartialSuccess` 后仍诚实保留阶段记录，同时解除主仓待核实告警循环；
  3. `src/Commands/BackupRefs.cs`：`TryReadReflogTimestampAsync` 原命令 `git log -g -n 1 --reverse --format=%ct` 触发 Git `fatal: options '--reverse' and '--walk-reflogs' cannot be used together` 且 `--format=%ct` 误取提交时间而非 reflog 时间；已改为 `git log -g --date=unix --format=%gd` 取最末条目 `@{<unix>}`，并在集成测试中以无日志仓库 + 旧提交日期校验。

### 末次编辑后全量验证结果（Batch 3.1b）
- `.\build\scripts\build-gitnest.ps1 -Publish`：Release 构建 `0` 个警告、`0` 个错误，成功发布自包含 `artifacts/windows-x64/GitNest.exe`。
- `.\build\scripts\test-gitnest.ps1`：`tests/GitNest.IntegrationTests` 全量 **175 / 175** 项通过（含 `GitSyncService` 注入日志校验与旧提交日期 reflog 保留检验）。
- `.\build\scripts\probe-gitnest.ps1 -OperationRecovery`：`tests/GitNest.UIProbe/OperationRecoveryProbe.cs` 全部 **13 / 13** 项端到端 UI 探针校验通过：
  - 未获取仓库诚实显示未获取且无虚假告警；
  - 窗口激活自动发现外部 CLI 新建分支；
  - 主仓横幅 + 工具栏角标显示、横幅暂不提醒后工具栏仍保留角标；
  - 双页签自动聚焦“最近操作与恢复”，实测磁盘核对与 URL token 强脱敏（界面不泄露明文密码/token）；
  - 同名分支恢复防覆盖、改名恢复到精确 commit、确认已完成消除主仓告警；
  - 暗黑主题 760×500 窄窗下 30 天过期备份引用预览与确认清理（保留未过期恢复引用），以及“原始命令日志”页签切换。
- 截图输出（已逐张人工/视觉核对 `artifacts/probes/phase-3.1/`）：
  - `repo-recovery-banner-light.png`（主仓未完成横幅、诚实状态摘要、工具栏橙色提醒角标）
  - `viewlogs-recovery-light.png`（浅色双页签、操作元数据、实测磁盘核对、改名恢复区与全宽诊断折叠面板）
  - `viewlogs-recovery-prune-dark-narrow.png`（暗黑主题 760×500 窄窗下的 30 天过期备份清理预览与确认卡片）
  - `viewlogs-rawlogs-dark.png`（暗黑主题下“原始命令日志”页签原生视图）
