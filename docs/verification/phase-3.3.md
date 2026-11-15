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

## Batch 3.3b — 原生 `RemoveWorktree`/`PruneWorktrees` 弹窗、打开视图切换、`DeleteBranch` 协同与 UI 探针验收

### 变更范围与实现要点
- **正在打开的工作树视图自动切换与安全收尾**：
  - 在 `src/ViewModels/Launcher.cs` 补全 `IsWorktreeOpenInTabs`、`SwitchAwayAndCloseWorktreeTabs`（`UIThread` 调度，若主仓未打开则先开启主仓标签页、激活主仓标签并在弹窗迁移时保留当前进度，关闭匹配工作树路径的所有标签页）与 `src/ViewModels/LauncherPage.cs` 弹窗转移保护、`src/ViewModels/Repository.cs` 暴露 `internal GetOwnerPage()`。
- **原生 Worktree 移除、预览清理弹窗与侧栏入口对齐**：
  - 在 `src/Views/Repository.axaml` 左侧工作树分组标题区接入与刷新按钮并列的清理预览图标按钮（`Icons.Clean` / `OnPruneWorktrees`），并将分组标题与计数改为 `Grid ColumnDefinitions="*,Auto"` + `TextTrimming="CharacterEllipsis"` 避免窄侧栏文字重叠；在 `src/Views/Repository.axaml.cs` 对 `!worktree.IsMain` 开放移除菜单（支持在当前已打开的关联工作树页签内发起删除），主工作树不显示锁定与移除菜单。
  - 重构 `src/ViewModels/RemoveWorktree.cs`、`src/Views/RemoveWorktree.axaml`、`.axaml.cs`：
    - 展示目标路径、同步状态、打开标签页自动切换提示与 detached HEAD 删前恢复引用提示；在 `IsLoadingPreflight` setter 同步刷新 `CanUnlockWorktree`、`CanShowForceOption`、`ShowForceLossWarning`、`CanOfferLocalBranchDelete`、`ShowCleanupBreakdown`、`ShowRemoteTargetRow` 等状态绑定。
    - 分类展示已暂存 / 未暂存 / 冲突 / 未跟踪 / `.gitignore` 忽略文件明细及彩色标签（默认拦截，必须勾选显式 `--force` 风险确认并显示包含目录路径与文件数的永久丢失红框提示）；缺失目录拦截常规移除并引导一键跳转到清理预览；锁定状态阻断常规及强删并展示完整锁原因，仅在勾选知情后允许“解锁并重新检查”；嵌套 `.git` 仓库或子模块彻底禁止删除（含强删）。
    - 可选勾选“工作树移除后删除本地分支（默认不勾选）”及真实 configured upstream“远端分支（默认不勾选，显式异名跟踪标签及服务端 tip SHA 探测）”；当步骤 1 移除工作树后 `-d` 因未合并提交止步时保留弹窗并展示未合并提交列表与“仍然强制删除剩余分支（`-D`）”；若步骤 3 远端 lease 拒绝则保留部分结果与一步本地备份恢复输入框。
  - 重构 `src/ViewModels/PruneWorktrees.cs`、`src/Views/PruneWorktrees.axaml`、`.axaml.cs`：从直接静默清理改为 `PreviewPruneWorktreesAsync` 干跑候选清单预览，并发展示“将被清理的失效工作树记录”与“已锁定并自动保留的记录”，仅在点击“确认清理失效记录”后执行真实 prune。
  - 更新 `src/ViewModels/DeleteBranch.cs` 与 `src/Views/DeleteBranch.axaml` 共享 `AttachedWorktreePreflight` 检查详情（锁定原因、缺失目录、嵌套仓库阻断、暂存/未暂存/未跟踪/忽略文件统计）与 `SwitchAwayAndCloseWorktreeTabs` 回调，并在工作树移除后刷新任务概览；更新 `src/Resources/Locales/en_US.axaml` 与 `zh_CN.axaml` 保持中英词条严格一致（`en=1401 zh=1401 diff=0`）。
- **Avalonia UI 探针接入**：
  - 在 `tests/GitNest.UIProbe/WorktreeDeleteProbe.cs`、`tests/GitNest.UIProbe/Program.cs` 与 `build/scripts/probe-gitnest.ps1` 接入 `-WorktreeDelete` / `--worktree-delete` 探针流程。

### 验证命令、结果与截图检查（末次代码改动后）
1. **中英资源完整性与 XML 校验**：
   - `en_US.axaml` 与 `zh_CN.axaml` UTF-8 XML 解析通过，`en=1401 zh=1401`，`diffEnOnly` / `diffZhOnly` / `missingEn` / `missingZh` 均为空。
2. **Release 编译与发布**：
   - 执行 `.\build\scripts\build-gitnest.ps1 -Publish`：`0` 个警告、`0` 个错误，发布生成 `C:\Project\GitNest\artifacts\windows-x64\GitNest.exe`。
3. **全量实仓集成测试**：
   - 执行 `.\build\scripts\test-gitnest.ps1`：`Passed 206 checks.`（全部 206 项通过，含 `BranchDeleteTests` 与 `WorktreeDeleteTests` 15 项实仓测试）。
4. **分支删除 UI 探针回归**：
   - 执行 `.\build\scripts\probe-gitnest.ps1 -BranchDelete`：`Passed 13 probe checks.`（全部 13 项通过）。
5. **工作树删除与清理 UI 探针**：
   - 执行 `.\build\scripts\probe-gitnest.ps1 -WorktreeDelete`：`Passed 20 probe checks.`（覆盖主工作树保护、脏/未跟踪/`.gitignore` 文件分项拦截与显式 `--force` `IsEffectivelyVisible` 红色警告、锁定解锁重检、嵌套仓库双向阻断、当前打开工作树标签页自动切换回主仓并关闭原页签、`worktree -> -d 未合并止步 -> -D -> 冻结 lease 拒绝 -> 备份恢复`、detached HEAD 备份恢复、缺失目录转清理预览保留锁定记录）。
6. **视觉截图检查（`artifacts/probes/phase-3.3/`）**：
   - [worktree-remove-dirty-force-light.png](../../artifacts/probes/phase-3.3/worktree-remove-dirty-force-light.png)（浅色 `1120x760`）：路径、文件分布、5 项彩色明细、强删勾选、红框不可逆损失警告、可选本地分支复选框及操作按钮完整可见，无滚动遮挡，左侧栏“工作树”标题与图标无重叠。
   - [worktree-remove-follow-up-unmerged-light.png](../../artifacts/probes/phase-3.3/worktree-remove-follow-up-unmerged-light.png)（浅色 `1120x760`）：切回主仓关闭工作树页签后的分步结果、2 个未合并提交列表及“仍然强制删除剩余分支（-D）”按钮完整无截断。
   - [worktree-remove-detached-dark-narrow.png](../../artifacts/probes/phase-3.3/worktree-remove-detached-dark-narrow.png)（暗黑窄窗 `780x700`）：detached HEAD 徽标与 30 天备份说明折行正常、暗黑对比清晰。
   - [worktree-prune-preview-dark-narrow.png](../../artifacts/probes/phase-3.3/worktree-prune-preview-dark-narrow.png)（暗黑窄窗 `780x700`）：2 条待清理失效记录与 1 条锁定保留记录及原因一屏完整展示，无文本与按钮裁切。
