# Phase 2.5 Verification Record（提交快照、获取、快进拉取、单分支普通推送）

## Batch 2.5a — 核心同步引擎、快照守卫、并发写锁与真实 Git 集成测试

### 范围与设计对齐
- 对齐 `docs/design.md` §3、§4、§5.1、§5.2、§7.2、§8.1：
  - 新增 `src/Models/StagedSnapshot.cs` 与 `src/Commands/QueryStagedSnapshot.cs`：只读计算 `HEAD`、`symbolic-ref`、`git ls-files --stage -z` 与 `git status --porcelain=v1 -z` 的组合指纹与部分暂存状态；未跟踪条目 `??` 严格归入未暂存区。
  - 新增 `src/Models/RepositoryWriteGuard.cs`：按规范 `git-common-dir` 对主仓与 linked worktree 进行进程内写操作串行化，并检查 `index.lock`、`HEAD.lock`、`packed-refs.lock`、`shallow.lock` 与目标分支 `.lock`（严格尊重且绝不自动删除 Git 原生锁文件）。
  - 新增 `src/Models/CommitSyncModels.cs` 与 `src/Commands/GitSyncService.cs`：
    - 使用 UTF-8 `\0` 分隔路径规范与 `--literal-pathspecs` 处理暂存/取消暂存（覆盖空仓库 `git rm --cached`、普通 `git reset` 与 `--amend` 下 `git update-index -z --index-info`），处理中文、空格、`[...]`、`&`、`#` 等路径而不丢失未暂存修改。
    - 提交前检查提交身份（`user.name` / `user.email`）、提交前复核暂存区快照、移除默认隐式 `--allow-empty`（仅在明确允许空提交时使用），准确分类 hook 拒绝 / 签名失败 / 锁冲突。
    - 获取（`FetchAsync`）记录实际获取时间与失败分类，并结合 `RepositorySyncOverviewBuilder` 区分“未获取”“未设置上游”“上游已删除”与已知缓存计数，不将未知写为零。
    - 拉取（`PullFastForwardAsync`）通过 `-c pull.rebase=false pull --ff-only` 强约束仅快进拉取，不读取或篡改用户全局/本地 `pull.rebase` 配置；分叉时精确统计领先/落后提交数并标记需要进入明确合并流程；存在本地未提交文件冲突时中止并保留工作区。
    - 单分支普通推送（`PushSingleBranchAsync`）固定显式 `refs/heads/<local>:refs/heads/<remote>` refspec，不附带全局 `--tags`、其他分支或强推参数；无上游设置跟踪时预检 `ls-remote` 目标分支防误覆盖。
    - 两步“提交并推送”会话（`CommitAndPushSession` + `RetryPushSessionAsync`）：第 2 步推送失败时完整保留第 1 步本地提交，仅重试推送而不重复创建提交。
  - 更新 `src/Commands/Add.cs`、`src/Commands/Reset.cs`、`src/Commands/Commit.cs`、`src/Commands/Pull.cs`、`src/Commands/Push.cs`、`src/Commands/UpdateIndexInfo.cs` 与 `src/Commands/CommandArgumentHelper.cs`。

### 验证命令与结果（Batch 2.5a）
- `.\build\scripts\build-gitnest.ps1`：Release 构建通过，`0` 个警告、`0` 个错误。
- `.\build\scripts\test-gitnest.ps1`：`tests/GitNest.IntegrationTests` 全量通过（**155 / 155** 项检查通过，含本次新增 `tests/GitNest.IntegrationTests/CommitSyncTests.cs` 35 项真实 Git 场景检查：unborn 暂存/取消暂存特殊路径、缺少提交身份拦截与本地身份设置、部分暂存保留工作区修改与覆盖预警、外部修改暂存区导致提交快照失效中止、原生 `index.lock` 尊重、pre-commit 钩子报错透传、主仓与 worktree 共享 `common-dir` 写串行化、状态摘要未知/零值区分、单分支不带标签推送、无上游远端碰撞拦截、两步提交推送失败后仅重试推送不重复提交、快进拉取保留 `pull.rebase=true`、脏文件冲突保护、分支分叉计数与不静默变基、游离 HEAD 防护与远端缺失/断连分类）。

## Batch 2.5b — 原生风格 UI 集成、多语言与 UI 探针（待进行）
- 待在下一批次完成 ViewModel/View 接入、`en_US`/`zh_CN` 本地化对称校验及 UI 探针截图检查。
