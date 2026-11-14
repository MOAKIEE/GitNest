# Phase 3.2 Verification Record（本地／远端分支删除、显式 lease、恢复与部分失败）

## Batch 3.2a — 分支删除安全引擎、真实 upstream 解析、顺序工作树清理与显式 lease

### 范围与设计对齐
- 对齐 `docs/design.md` §5.3、§8、§10 与 `docs/implementation-plan.md` 批次 3.2：
  - 新增 `src/Models/BranchDeletionModels.cs` 与 `src/Commands/BranchDeletionService.cs`，并加固 `src/Commands/DoesBranchExistOnRemote.cs`（使用 `ls-remote --heads -- <remote> refs/heads/<branch>` 精确锚定）及 `src/Commands/BackupRefs.cs`（抽离 `DeleteBackupRefAsync` 供已持锁写流程复用，避免与 `PruneExpiredAsync` 重复 `update-ref -d` 逻辑）：
    - **真实 upstream 解析与多远端隔离**：从 Git 配置 `branch.<name>.remote` 与 `branch.<name>.merge` 读取实际跟踪目标，不以同名猜测；支持本地与远端异名映射（如 `local/mapped -> fork/server/topic-9`）并在多远端下精确匹配，忽略 `"."` 本地伪远端。
    - **当前分支与工作树保护及顺序清理**：拒绝直接删除当前检出分支或被其他工作树占用的分支；确认“先移除工作树，再删除分支”时，显式优先移除关联工作树（默认不用 `--force`，遇脏目录/锁定立即止步，不继续删除本地或远端分支）。
    - **常规 `-d` 与未合并说明 / 恢复引用保护**：默认走 Git `branch -d -- <name>` 已合并检查；被拒时返回准确未合并提交列表/数量与差异文件数供进入单独“仍然删除（`-D`）”确认，不遗留冗余无效备份；实际删除本地分支前在 `RepositoryWriteGuard` 内创建本地 `refs/gitnest/backups/<op-id>` 并支持同名覆盖拒绝与改名/原名精确恢复。
    - **远端精确探测与冻结 `--force-with-lease`**：删除前经 `RemoteGitCommand`（复用 `Command.CreateGitStartInfo` 支持 SSH Key 与 askpass 并全量脱敏 URL 凭证）调用 `ls-remote` 区分“远端存在（获取实时 `LiveTipSha`）”“服务器已缺失（须显式确认仅清理本地跟踪缓存，绝不冒充服务器删除）”“网络/认证失败（立即报错并保留本地跟踪缓存）”。联动或独立远端删除均采用确认阶段冻结的预期哈希 `push --force-with-lease=refs/heads/<remoteBranch>:<expectedTipSha> -- <remote> :refs/heads/<remoteBranch>`；确认后即便发生后台 `fetch`，也不会篡改已确认的 lease 预期值，协作者期间新 push 将触发 `stale info` 拒绝并保留服务端提交。
    - **部分失败与分步结果**：联动删远端失败（如 lease 拒绝或断网）时，`OperationJournalStore` 诚实记录 `GitOperationStage.PartialSuccess`（`“本地已删除，远程未删除：...”`），保留本地恢复引用与原提交指纹，支持直接从备份恢复本地分支并自动核销待处理提醒；不自动重建远端分支。
    - **批量删除安全封装**：`DeleteMultipleBranchesAsync` 复用单分支保护、备份与显式 lease 逻辑并汇总逐项结果。

### 验证命令与结果（Batch 3.2a）
- `.\build\scripts\build-gitnest.ps1`：Release 编译通过，`0` 个警告、`0` 个错误。
- `.\build\scripts\test-gitnest.ps1`：`tests/GitNest.IntegrationTests` 全量 **188 / 188** 项真实 Git 检查通过（含本次新增 `tests/GitNest.IntegrationTests/BranchDeleteTests.cs` 13 项检查，覆盖双克隆裸仓协作冲突、后台 fetch 后 lease 冻结、脏工作树阻断、`-d` 未合并阻止与 `-D` 强删备份恢复、异名多远端、断网脱敏与假删缓存隔离）。

## Batch 3.2b — 原生分支删除确认/分步结果弹窗、菜单文案更新与 `-BranchDelete` UI 探针（待进行）
- 下一批完成 `BranchTree` / `Histories` 左右分支独立删除菜单文案、`DeleteBranch` / `DeleteMultipleBranches` 原生风格多步骤确认与部分失败恢复界面、`en_US`/`zh_CN` 词条与 `-BranchDelete` UI 探针截图验收。
