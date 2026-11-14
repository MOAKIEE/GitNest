# Phase 3.5 Verification Record（stash、丢弃前备份、文件回收与忽略规则）

## Batch 3.5a — 事务级 Stash、丢弃双基线与 30 天本地恢复库、系统回收站及忽略规则核心引擎

### 范围与设计对齐
- 对齐 `docs/design.md` §5.5、§5.6、§5.7、§8.1、§8.2、§10 与 `docs/implementation-plan.md` 批次 3.5：
  - **跨平台系统回收站后端（`src/Native/OS.cs`、`Windows.cs`、`MacOS.cs`、`Linux.cs`）**：
    - 在 `OS.IBackend` 新增 `MoveToRecycleBin(string path, out string error)`。Windows 后端经 `SHFileOperationW`（`FO_DELETE` + `FOF_ALLOWUNDO | FOF_SILENT | FOF_NOCONFIRMATION | FOF_NOERRORUI`）发送至系统回收站并二次校验磁盘路径移除；如发生占用/权限失败则明确返回错误并保留原文件，**绝不降级到 `File.Delete` / `Directory.Delete`**。
  - **统一模型、命令加固与 30 天本地恢复库（`src/Models/Stash.cs`、`WorkingCopySafetyModels.cs`、`DiscardBackupStore.cs`、`OperationSafetyModels.cs`、`App.JsonCodeGen.cs`、`Commands/Command.cs`、`CommandArgumentHelper.cs`、`Stash.cs`、`QueryStashes.cs`、`Restore.cs`、`Discard.cs`、`MergeRevertService.cs`）**：
    - `Models.Stash` 解析 `WIP on <branch>:` / `On <branch>:` 源分支并保持无前缀外部 stash 兼容显示。
    - `Command.RunGitDirectWithInputAsync` 与 `CommandArgumentHelper.BuildNulPathspecBytes` 为 pathspecs、blob 写入与索引恢复提供 NUL/stdin 安全管道；`Commands.Stash`、`Restore`、`QueryStashes` 与 `Discard` 完成参数非选项校验（`ValidateNonOptionToken`）、`--literal-pathspecs` 隔离与兼容外包；`MergeRevertService` 在 `GitInProgressKind.None && conflictedPaths.Count > 0` 时识别 `GitOperationKind.Stash` 的 `PendingVerification` 记录，标识「恢复未完成，保存项仍保留」且不提供危险自动中止。
    - 新增 `DiscardBackupStore`：将磁盘内容及暂存区 blob 原封写入应用本地缓存 `discard-backups/`（与 `refs/gitnest/backups/*` 完全隔离），逐条校对 SHA-256 与长度并留存 `PostDiscard` 状态用于后续篡改检出；提供 30 天保留过期拦截与 `PruneExpired` 清理。
  - **Stash 与分支切换事务核心（`src/Commands/StashWorkflowService.cs`）**：
    - 推入前拦截 unborn HEAD 与未解决冲突，预检待暂存文件及默认未纳入的被忽略文件清单；经 `--literal-pathspecs` + `--pathspec-from-file=- --pathspec-file-nul` 执行局部暂存与 `keepAll` / `--staged` / `--keep-index`。
    - 恢复前强制验证目标工作区/暂存区干净状态，并显式比对 `stash^1..stash` / `stash^1..stash^2` 与 `stash^3` 受影响路径 against 当前工作区未跟踪与 `.gitignore` 忽略文件，阻止潜在静默覆盖。
    - 按精确 commit SHA（防外部并发 push/drop 造成的 `stash@{k}` 下标错位）执行 `stash apply [--index]`；一旦出现冲突即保留现场及原 stash（绝不 drop 或 `--abort`/`reset --hard`）并写 `PendingVerification` 流水记录；仅在确认干净应用后再精确匹配 `SHA` 删除对应 `stash@{k}`。
    - `SwitchSafelyAsync` 禁止分叉切换自动丢弃工作区改动（拒绝 `DealWithLocalChanges.Discard`），选择 `StashAndReapply` 时含未跟踪文件暂存并于切换后按 SHA 安全重放及冲突保留。
  - **丢弃双基线隔离与安全回收服务（`src/Commands/DiscardBackupService.cs`）**：
    - **基线 1（`RestoreWorktreeToIndex`）**：先完整写入 30 天本地恢复库，随后仅执行 `git restore --worktree`（不传 `--staged`），严格保全已有暂存区切片。
    - **基线 2（`RestoreToHead`）**：拦截 unborn HEAD，区分 HEAD 中已跟踪文件（与重命名源）执行 `git restore --staged --worktree --source=HEAD`，对仅新添加到暂存区且不在 HEAD 的文件先完成 30 天备份后 `reset` 并交由回收站安全移出；若备份捕获失败立即在改动工作区前中止。
    - **基线 3（`MoveUntrackedToRecycleBin`）**：显式清单经系统回收站移除未跟踪文件/目录；遗留 `Discard.AllAsync` 封装亦屏蔽 `git clean -fdx` 默认清空忽略文件。
    - `InspectBackupRestoreAsync` / `RestoreBackupAsync` 检出磁盘或暂存区后续新修改并默认拒绝静默覆盖；支持显式恢复磁盘字节及通过 `hash-object` + `update-index` 精准还原暂存区 blob。
  - **忽略规则检查与写入引擎（`src/Commands/IgnoreRuleService.cs`）**：
    - 支持 `.gitignore`（仓根及就近父目录）与经 `git rev-parse --path-format=absolute --git-path info/exclude` 精确解析的本机排除文件（兼容主仓与 linked worktree）。
    - 提供路径分段与首尾特殊字符转义（`\`、`*`、`?`、`[`、`]`、首 `#`/`!`/空格及尾空格）、现有规则和注释行留存与精确去重、`!` 否定规则冲突识别，以及直接调用 Git `ls-files --ignored --exclude-from` 实时预览未跟踪匹配结果与已跟踪文件警告（绝不自动运行 `git rm --cached`）。

### 验证命令与结果（Batch 3.5a 末次编辑后实测）
- `.\build\scripts\build-gitnest.ps1`：Release 编译通过，`0` 个警告、`0` 个错误。
- `.\build\scripts\test-gitnest.ps1`：`tests/GitNest.IntegrationTests` 全量 **274 / 274** 项真实 Git 检查全部通过（新增 `tests/GitNest.IntegrationTests/StashDiscardIgnoreTests.cs` 共 41 项检查，覆盖 unborn HEAD 拦截、WIP/On/外部 stash 解析、中文及 `[1]`/空格路径 NUL pathspecs 暂存、未跟踪/被忽略文件冲突检出与多父 stash 路径差解析、并发 stash 下标错位按 SHA 应用丢弃、冲突后保留 stash 与 `PendingVerification` 只读横幅识别、分支切换禁止 `Discard` 与安全 `StashAndReapply`、恢复库写入失败回滚、双基线隔离恢复、后续编辑防静默覆盖、30 天过期拦截及清理、真实 Windows `SHFileOperationW` 回收站删除与文件句柄锁定拒绝降级删除、linked worktree 下 `info/exclude` 特殊字符规则实效、去重、否定规则警告与已跟踪文件安全保留）。

## Batch 3.5b — 原生 UI 对话框、30 天本地恢复列表交互、双语词条与探针截图验收（待续）
- 下一步实施。
