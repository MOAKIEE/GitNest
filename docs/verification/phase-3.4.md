# Phase 3.4 Verification Record（定向合并、revert 及冲突归属检查）

## Batch 3.4a — 定向合并引擎、单提交 revert、冲突归属守卫与 hook/签名失败分离

### 范围与设计对齐
- 对齐 `docs/design.md` §5.4、§6.4、§8.1、§8.2、§10 与 `docs/implementation-plan.md` 批次 3.4：
  - **命令行安全与隐式行为清理**：
    - 在 `src/Commands/CommandArgumentHelper.cs` 提取 `ValidateNonOptionToken` 公共防注入校验，在 `src/Commands/Merge.cs`、`src/Commands/MergeTree.cs`、`src/Commands/Revert.cs` 对来源分支/提交引用做非选项前缀核验与参数引述。
    - 彻底移除 `src/Commands/Revert.cs` 硬编码的 `revert -m 1`，回归仅对单一普通提交执行 `revert --no-edit [--no-commit]`；补充 `Command.RunGitDirectAsync` 带超时的直连 `ArgumentList` 重载及 `GIT_TERMINAL_PROMPT=0` 非交互保护。
    - 在 `src/Models/OperationSafetyModels.cs`、`src/Models/OperationJournalStore.cs` 与 `src/Commands/OperationJournalService.cs` 增补 `ExpectedPartnerSha` 字段（用于持久化记录 `MERGE_HEAD` / `REVERT_HEAD` 伙伴提交 SHA）。
  - **定向合并、单提交 revert 与冲突归属保护核心（`src/Models/MergeRevertModels.cs`、`src/Commands/MergeRevertService.cs`）**：
    - **定向合并与无副作用预检**：`InspectMergeAsync` 与 `ExecuteMergeAsync` 明确区分「将【来源】的改动合并到【目标】」，支持本地分支、**显式已抓取的远端分支本地快照**（拒绝猜测默认上游或自动推远端）、标签与提交作为来源，支持指定目标本地分支（含跨分支检出切换，以及检出于其他 worktree 时的跨工作树修改阻断）；通过 `git merge-tree --write-tree` 无副作用预检预期冲突文件列表与无共同祖先拦截，展示拟并入提交与文件列表，操作前置确认工作区与暂存区干净、`InProgressKind == None`，在 `OperationJournalService` 写锁内重新校验 `WatchedRefs` 确认快照后执行，成功后停留在目标分支且不自动推送。
    - **仅限单次普通提交的 revert**：`InspectRevertAsync` 与 `ExecuteRevertAsync` 通过提交与受影响文件预览确认前置干净状态，严格阻止多父节点合并提交（`Parents.Count > 1` -> `MergeCommitNotSupported`，提示交由外部工具处理而非猜 `-m 1`）。
    - **严格的冲突归属确认与外部/AI 介入止损**：发生文本/合并冲突后，立即记录含 `OperationId`、`ExpectedHeadSha`、`ExpectedPartnerSha`、`MERGE_HEAD`/`REVERT_HEAD` 原始内容 SHA-256、所有进行中标记、未合并暂存条目签名（`ls-files -u -z`）、`IndexFingerprint` 与工作区差异/冲突/未跟踪文件磁盘 SHA-256 清单在内的基线；通过 `VerifyConflictOwnershipForAbortAsync` 逐项复核仅由 GitNest 发起且无任何外部或 AI 编辑/解决/`git add`/新增文件介入后方才自动执行 `--abort`，并在中止后二次校验 `InProgressKind == None`、工作区干净与 `HEAD` 还原；只要任意一项归属或内容校验不一致，立即停止自动中止，完整保留外部修改成果并持续报告冲突与介入原因。
    - **外部进行中状态只读识别与 hook/签名失败区分**：`InspectRepositoryInProgressAsync` 结合磁盘 `MERGE_HEAD`/`REVERT_HEAD` 创建修改时间（`LastWriteTimeUtc <= record.UpdatedAtUtc`）与流水记录精准区分 GitNest 保留态与外部命令行/工具新建操作，绝不在打开或刷新时自动 `--abort`；同步在合并与 revert 退出非零分支中将未产生冲突的 Git 钩子拒签（`HookRejected`：如 `pre-merge-commit`、`prepare-commit-msg`、`commit-msg`）与 GPG/SSH 签名失败（`SigningFailed`）清晰分离并保留当前真实仓库状态与错误输出。

### 验证命令与结果（Batch 3.4a）
- `.\build\scripts\build-gitnest.ps1`：Release 编译通过，`0` 个警告、`0` 个错误。
- `.\build\scripts\test-gitnest.ps1`：`tests/GitNest.IntegrationTests` 全量 **233 / 233** 项真实 Git 检查通过（含本次新增 `tests/GitNest.IntegrationTests/MergeRevertTests.cs` 27 项检查，覆盖中文/带空格路径 FF 合并与停留在目标分支未推送、跨分支分叉三方合并、显式远端分支本地快照合并、其他 worktree 检出目标拦截、脏工作区与过期确认快照拦截、`merge-tree --write-tree` 预检、GitNest 冲突自动 `--abort` 与二次干净校验、冲突后 AI 改写文件与新增未跟踪文件的防自动中止拦截、外部命令行冲突只读识别与多次刷新绝不自动中止、单提交 revert 成功与合并提交屏蔽、revert 冲突归属护卫，以及基于真实 `prepare-commit-msg` / `pre-merge-commit` 钩子和 `gpg.program` 失败桩的 `HookRejected` / `SigningFailed` 分类校验）。
