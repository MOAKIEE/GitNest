# Phase 3.3 Verification Record（工作树检查、删除与任务清理编排）

## Batch 3.3a — 工作树多态预检引擎、锁定/嵌套隔离、detached 恢复、统一编排与 dry-run 清理

### 范围与设计对齐
- 对齐 `docs/design.md` §5.8、§10 与 `docs/implementation-plan.md` 批次 3.3：
  - 新增 `src/Models/WorktreeDeletionModels.cs` 与 `src/Commands/WorktreeDeletionService.cs`，并在 `src/Models/BranchDeletionModels.cs` 与 `src/Commands/BranchDeletionService.cs` 完成单一共享编排统一：
    - **全量目标状态检查与 `.gitignore` 保护**：在目标工作树路径执行 `--no-optional-locks status --porcelain=v2 --branch --ahead-behind -z --untracked-files=all --ignored=matching --ignore-submodules=none` 并结合 `rev-parse --path-format=absolute` 校验仓库归属（`show-toplevel` 与 `git-common-dir`）及每工作树锁状态；不仅阻止存在暂存/未暂存/冲突/未跟踪文件时的常规删除，也在存在**仅被 `.gitignore` 忽略的文件**时默认阻断删除（避免原生 `git worktree remove` 静默永久删除本地配置/产物的风险）。
    - **主工作树与锁定 / 嵌套仓库保护**：主工作树及裸仓库严格禁止删除；锁定工作树展示完整锁原因，在 `ForceRemoveWorktree` 为 `true` 时仍坚决拒绝（绝不自动叠加 `-f -f` 绕过锁），提供显式 `UnlockWorktreeAsync` 先解锁并自动重检；通过 `git ls-files --stage`（`160000` gitlink）及符号链接安全的树扫描检测任意注册子模块或未跟踪/已忽略嵌套 `.git` 仓库，在首版严格阻止包括强制删除在内的一切移除。
    - **确认状态指纹防漂移与打开视图切换**：生成覆盖注册态、锁态、HEAD SHA 及全部暂存/未暂存/冲突/未跟踪/忽略/嵌套清单的 `StateDigest`；确认后目录内任何新增未跟踪或忽略文件会立即导致校验失效阻断执行；始终在主工作树目录执行 `git worktree remove` 并于移除前回调切换/关闭已打开的工作树标签页。
    - **detached HEAD 恢复保护与统一清理流水线**：detached HEAD 工作树在 `RepositoryWriteGuard` 锁内移除前先为 HEAD 创建 `refs/gitnest/backups/<op-id>` 恢复引用并支持恢复为分支（明确说明不含未提交文件）；`BranchDeletionService` 与 `WorktreeDeletionService` 共用同一份工作树检查/移除流水线（`RemoveWorktreeInLockAsync`）及本地/远端分支删除流水线（`ExecuteLocalBranchAndLinkedRemoteInLockAsync`），按“工作树 -> 本地分支 -> 远端分支”严格串联，任一步拒绝（如未合并 `-d` 或协作者新提交触发 `--force-with-lease`）立即止步保留后续对象且不重建已删除目录。
    - **dry-run 失效记录预览清理**：新增 `PreviewPruneWorktreesAsync` 与 `PruneWorktreesAsync`，联合 `git worktree prune --dry-run -v` 与 `gitdir` 反查列出准确候选路径与原因，同时显式保留并说明处于锁定或未过期的缺失/离线工作树记录。

### 验证命令与结果（Batch 3.3a）
- `.\build\scripts\build-gitnest.ps1`：Release 编译通过，`0` 个警告、`0` 个错误。
- `.\build\scripts\test-gitnest.ps1`：`tests/GitNest.IntegrationTests` 全量 **206 / 206** 项真实 Git 检查通过（含本次新增 `tests/GitNest.IntegrationTests/WorktreeDeleteTests.cs` 15 项检查，覆盖主工作树阻断、打开目录切回主仓、纯 `.gitignore` 文件保护与过期确认拦截、锁定阻断与解锁重检、嵌套仓库与子模块强删阻断、detached HEAD 备份恢复、三步有序清理在未合并 `-d` 与协作者 lease 拒绝下的诚实部分状态，以及锁定/离线记录在 dry-run 与真实 prune 中的保留行为）。

## Batch 3.3b — 原生 `RemoveWorktree`/`PruneWorktrees` 弹窗、打开视图切换与 `-WorktreeDelete` UI 探针（待进行）
- 下一批完成打开工作树自动切回主仓关闭标签、重构 `RemoveWorktree` / `PruneWorktrees` 原生弹窗、整合 `DeleteBranch` 关联工作树预检细节、完善双语词条与 `-WorktreeDelete` UI 探针截图验收。
