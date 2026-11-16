# GitNest 测试与界面探针说明

本项目测试分为两部分：面向逻辑与真实 Git 行为的 **集成检查**（`GitNest.IntegrationTests`），以及面向 Avalonia 视图、交互与截图回归的 **界面探针**（`GitNest.UIProbe`）。两个项目均按**功能模块**拆分，改某个功能时只需运行对应的小测试。

## 1. 集成检查（`GitNest.IntegrationTests`）

需要 Windows x64、项目 SDK（见根目录 `global.json`）和 Git for Windows。临时测试目录统一创建在系统临时目录下的 `GitNest-tests-<随机标识>/`，测试结束后恢复环境变量并自动清理。

### 常用运行方式

```powershell
# 查看全部功能 suite 及说明
./build/scripts/test-gitnest.ps1 -List

# 仅跑当前工作区改动对应的相关小测试（按 git status / git diff 自动映射）
./build/scripts/test-gitnest.ps1 -Changed
./build/scripts/test-gitnest.ps1 -Changed -DryRun

# 仅跑指定的一个或多个功能 suite（加 -NoBuild 可跳过重复编译）
./build/scripts/test-gitnest.ps1 -Suite merge -NoBuild
./build/scripts/test-gitnest.ps1 -Suite commit,sync -NoBuild

# 运行完整套件（构建一次后多进程并行执行，也可通过 -Jobs 1 强制串行）
./build/scripts/test-gitnest.ps1
```

若需指定特定版本的 `git.exe`，可设置环境变量 `GITNEST_TEST_GIT_EXECUTABLE` 为目标 `git.exe` 的绝对路径后再运行脚本。

### 功能 Suite 一览

| Suite | 对应测试文件 | 覆盖功能 |
| --- | --- | --- |
| `core` | `CoreTests.cs` | Git 可执行探测与超时、提交身份读取、空仓只读查询、资源键一致性、统计/归档/图像解码 |
| `update` | `UpdateTests.cs` | SemVer 版本号比较、异常标签拒绝、`UpdateChecker` GitHub 发布检查 |
| `worktree` | `WorktreeTests.cs` | 工作树状态读取、任务元数据存储、编辑器参数、工作树监听与关闭安全 |
| `comparison` | `ComparisonTests.cs` | 任务累计差异（`MergeBase`/`TwoTips`）、重命名、二进制统计、快照失效与防注入 |
| `history-diff` | `HistoryDiffTests.cs` | 提交历史分页与搜索、CJK/特殊路径 `CompareRevisions`、`Diff` 与 `IsBinary` |
| `config` | `ConfigTests.cs` | 分层配置快照与脱敏、作用域写入、锁保护、外部变更刷新、`RepositoryConfigure`、旧配置迁移、`IssueTracker` |
| `operation-safety` | `OperationSafetyTests.cs` | 操作快照校验、跨工作树写锁、`BackupRefs`、操作日志脱敏与崩溃对账、外部 `rebase`/`bisect` 只读保护、`AssumeUnchanged` |
| `commit` | `CommitTests.cs` | 暂存/取消暂存、局部暂存保留、缺身份拦截、快照篡改拦截、`index.lock`、`pre-commit`、`commit.template` 保留 |
| `sync` | `SyncTests.cs` | 同步概览、`Fetch`/`Pull(--ff-only)`/单分支与指定修订 `Push`、分离与多推送地址、二步重试、认证失败脱敏 |
| `amend` | `AmendTests.cs` | 独立 `AmendLastCommit`（本地增补、未知/未拉取/保护分支阻断、opt-in lease、并发竞争与重试） |
| `branch-delete` | `BranchDeleteTests.cs` | 本地/远端删分支、多远端 upstream、`-d`/`-D` 备份恢复、分离/多推送地址、lease 部分失败、默认分支保护 |
| `worktree-delete` | `WorktreeDeleteTests.cs` | 工作树删除预检、ignored/锁定/嵌套仓库/子模块阻断、detached 备份、顺序联动清理与干跑 `prune` |
| `merge` | `MergeTests.cs` | 定向合并、`DontCommit` 暂停、远端快照、冲突自动中止与外部介入止损、外部冲突只读、hook/签名错误区分 |
| `revert` | `RevertTests.cs` | 单提交 Revert、多父合并提交拦截、冲突自动中止与外部介入止损、hook/签名错误区分 |
| `discard` | `DiscardTests.cs` | 双基线丢弃（`RestoreWorktreeToIndex`/`RestoreToHead`）、30 天恢复与过期清理、队列快照校验、重命名冲突与系统回收站 |
| `stash-ignore` | `StashIgnoreTests.cs` | `Stash` 推入/碰撞检查/按 SHA 应用/冲突保留、切分支 `StashAndReapply`、`IgnoreRuleService` 规则与冲突警告 |
| `submodule-lfs` | `SubmoduleLfsTests.cs` | 标签/子模块只读查询、克隆子模块初始化、Git LFS 追踪与 Diff、切换分支子模块显式二次确认与部分失败 |
| `history-reset` | `HistoryResetTests.cs` | 本地撤回（unborn/根/detached/非第一父拦截、碰撞、`--mixed`/`--hard`、备份恢复、`ResetWithoutCheckout`） |
| `remote-rewrite` | `RemoteRewriteTests.cs` | 远端历史改写（默认分支保护与局部解锁、离线/分叉阻断、分离与多推送地址、lease 竞争、超时核对与日志对账） |

## 2. 界面探针（`GitNest.UIProbe`）

在交互式 Windows 会话运行，使用真实 Avalonia 视图、隔离配置与临时 Git 仓库，并将截图与 `results.txt` 写入 `artifacts/probes/<探针名>/`；临时仓库结束后移至回收站。

```powershell
# 查看全部探针
./build/scripts/probe-gitnest.ps1 -List

# 运行指定功能探针（加 -NoBuild 复用编译产物）
./build/scripts/probe-gitnest.ps1 -Probe amend -NoBuild
./build/scripts/probe-gitnest.ps1 -Probe config
```

内置探针包括：`overview`、`comparison`、`config`、`commit-sync`、`amend`、`operation-recovery`、`branch-delete`、`worktree-delete`、`merge-revert`、`stash-discard`、`history-reset`、`submodule-lfs`、`surface`、`high-dpi`（依赖 `artifacts/windows-x64/GitNest.exe`，需先执行 `./build/scripts/build-gitnest.ps1 -Publish`）。
