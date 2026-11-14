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
- 注：提交 `0288b0cd` 后修正 `BranchDeleteTests.cs:328` 将 `show-ref --verify` 改为 `rev-parse --verify` 以匹配纯哈希比对，实际 `tests/GitNest.IntegrationTests` 全量为 **191 / 191** 项真实 Git 检查通过（其中 `tests/GitNest.IntegrationTests/BranchDeleteTests.cs` 共 16 项，覆盖双克隆裸仓协作冲突、后台 fetch 后 lease 冻结、脏工作树阻断、`-d` 未合并阻止与 `-D` 强删备份恢复、异名多远端、断网脱敏与假删缓存隔离）。

## Batch 3.2b — 原生分支删除确认/分步结果弹窗、菜单文案更新与 `-BranchDelete` UI 探针（已完成）

### 范围与实现要点
- **菜单入口与可用性区分**：更新 `src/Views/BranchTree.axaml.cs` 与 `src/Views/Histories.axaml.cs` 的右键删除菜单文案为独立本地/远端条目（`Text.BranchCM.DeleteLocal` / `Text.BranchCM.DeleteRemote`），并在历史提交右键菜单对当前检出分支禁用本地删除入口。
- **动态按钮与弹窗守卫**：为 `src/ViewModels/Popup.cs` 新增可覆写的 `ConfirmButtonText`、`CancelButtonText`、`ShowConfirmButton`、`CanConfirm` 及 `NotifyPopupButtonsChanged()`，在 `src/ViewModels/LauncherPage.cs` 阻止已完成/被阻塞弹窗被回车快捷键误提交，并在 `src/Views/LauncherPage.axaml` 将确认按钮从固定 `Width="100"` 调整为 `MinWidth="100"` + `Padding="14,0"` 以完整容纳明确目标动作按钮文字（如`删除本地及关联远程分支`）。
- **单分支删除多步骤确认与就地恢复**：重写 `src/ViewModels/DeleteBranch.cs`、`src/Views/DeleteBranch.axaml` 与 `.axaml.cs`：
  - 显示当前检出分支阻止提示、其他工作树占用警告及按顺序“先移除工作树，再删除分支”显式勾选项。
  - 仅在存在真实 Git 配置 `upstream` 时显示关联远端删改选项，勾选后明确列出本地引用、异名远端目标、脱敏 URL 与实时服务端提交探测（支持手动刷新），绝不以同名猜测。
  - 常规 `-d` 拒绝未合并分支时保持弹窗开启，列出未合并提交摘要与差异文件数，并切换主按钮为独立的`仍然强制删除`。
  - 联动远端删除被显式 `--force-with-lease` 拒绝或断网失败时保持弹窗停留在分步结果页（`本地已删除，远程未删除`），保留协作者提交并在弹窗内提供“恢复分支名 + 从备份恢复本地分支”一键恢复。
  - 独立远端删除展示服务器实时状态、缺失时区分要求显式确认仅清理本地跟踪缓存（`仅清理本地缓存`），保留本地同名分支不动。
- **批量删除逐项结果收敛**：重构 `src/ViewModels/DeleteMultipleBranches.cs`、`src/Views/DeleteMultipleBranches.axaml` 及 `BranchDeletionService.DeleteMultipleBranchesAsync` 的跳过计数归类（将受保护、需强制确认与需清缓存均计入跳过项），发生受保护或部分跳过时保留弹窗展示逐项结果与原因说明。
- **文本折行修复**：将 `DeleteBranch.axaml` 与 `DeleteMultipleBranches.axaml` 中横向排列图标与换行的 `StackPanel Orientation="Horizontal"` 替换为 `Grid ColumnDefinitions="Auto,*"`（及状态行 `*,Auto`），解决长提示在 `620px`/`780px` 窄窗下右边缘裁剪问题。
- **双语词条与 UI 探针**：补齐 `src/Resources/Locales/en_US.axaml` 与 `zh_CN.axaml` 各 36 组 `Text.BranchCM.*` / `Text.DeleteBranch.*` / `Text.DeleteMultiBranch.*` 词条，并新增 `tests/GitNest.UIProbe/BranchDeleteProbe.cs`（接入 `Program.cs` 与 `build/scripts/probe-gitnest.ps1 -BranchDelete`）。

### 末次代码改动后验证结果
- 双语资源键一致性校验：`en=1361 zh=1361 diff=0`。
- `.\build\scripts\build-gitnest.ps1 -Publish`：`0` 个警告、`0` 个错误，发布生成 `artifacts/windows-x64/GitNest.exe`。
- `.\build\scripts\test-gitnest.ps1`：**191 / 191** 项真实 Git 集成检查通过（含 `BranchDeleteTests` 16 项）。
- `.\build\scripts\probe-gitnest.ps1 -BranchDelete`：**13 / 13** 项 Avalonia UI 探针断言全部通过。
- 逐张视觉检查 `artifacts/probes/phase-3.2/` 下 4 张截图均通过，浅色/暗黑主题及 `780×700` 窄窗下无截断或错位：
  - `delete-local-unmerged-force-step-light.png`（浅色模式：未合并分支常规 `-d` 阻止并进入`仍然强制删除`二级确认态）
  - `delete-linked-partial-recovery-light.png`（浅色模式：异名多远端协作者抢先 push 触发显式 lease 拒绝，展示“已保存备份/已删本地/未删远端”分步结果与一键恢复区）
  - `delete-remote-dark-narrow.png`（暗黑 `780×700` 窄窗：独立远端删除展示脱敏 URL、服务端实时提交与显式 lease 说明）
  - `delete-multiple-result-dark-narrow.png`（暗黑 `780×700` 窄窗：批量删除逐项展示受保护 `main`、已清理 `feat/merged-clean` 与待强删跳过 `feat/remote-only`）
