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

## Batch 3.1b — 最近操作与恢复 UI、窗口激活刷新与 UI 探针（待进行）
- 待下一批次完成“最近操作与恢复”原生视图、`GitSyncService` 日志接线、主仓未完成提示横幅、窗口激活焦点刷新、`en_US`/`zh_CN` 词条与 UI 探针截图检查。
