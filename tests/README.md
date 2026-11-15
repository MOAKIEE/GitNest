# GitNest 集成检查

在项目根目录运行：

```powershell
./build/scripts/test-gitnest.ps1
```

需要 Windows x64、项目 SDK 和 Git for Windows。脚本构建并运行 `GitNest.IntegrationTests` 控制台程序；检查失败时返回非零退出码，成功时打印通过数量。

版本探测使用该程序自己的 apphost 作为可执行夹具，检查真实进程退出、输出管道和超时行为。配置读取调用真实 Git，使用独立临时全局配置和临时仓库，并核对配置文件内容哈希。

临时路径固定在系统临时目录下的 `GitNest-tests-<随机标识>/`，清理前验证路径；测试进程退出前恢复覆盖的环境变量。

仅运行丢弃与恢复审查回归（包括基础 Git 检查）：

```powershell
./.tools/dotnet/dotnet.exe run --project tests/GitNest.IntegrationTests -c Release -- --review-discard
```

将参数替换为 `--review-remote` 可定向检查远端地址、指定修订与标签推送范围、跟踪缓存、分支保护及历史改写回归。

验证其他 Git for Windows 版本时，可将 `GITNEST_TEST_GIT_EXECUTABLE` 环境变量设为该版本 `git.exe` 的绝对路径，再运行同一集成检查命令。未设置时自动检测本机 Git。

## 界面探针

```powershell
./build/scripts/probe-gitnest.ps1
```

在交互式 Windows 会话运行。探针加载实际应用资源、控件和 ViewModel，使用隔离配置及真实 Git 工作树，触发控件绑定和双击路由事件，等待文件监听刷新，并以 `RenderTargetBitmap` 保存截图。结果与 PNG 位于 `artifacts/probes/phase-2.2/`；临时仓库完成后移至回收站。编辑器和终端使用探针进程记录实际收到的路径及工作目录。原生文件选择器与资源管理器窗口需要另行桌面验收。

运行 `./build/scripts/probe-gitnest.ps1 -Comparison` 另加累计差异页面检查，包含目标持久化、两种比较方式、文本／二进制查看、未提交修改单独展示、定时过期检查、刷新、目标删除、快速切换目标以及关闭后丢弃旧请求。结果与截图写入 `artifacts/probes/phase-2.3/`。

运行 `./build/scripts/probe-gitnest.ps1 -Config` 检查仓库配置界面，包含 `Global`/`Local`/`Worktree` 来源标签、敏感凭据与诊断脱敏、URL 特定代理覆盖、`credential.helper` 有序重置链、签名依赖提示、浅色／深色窄窗口高优先级覆盖提示、保存前冲突保护与部分失败逐项汇报。结果与截图写入 `artifacts/probes/phase-2.4/`。

其他具名探针使用同一脚本，各参数单独运行：

| 参数 | 检查内容 | 输出目录（`artifacts/probes/` 下） |
| --- | --- | --- |
| `-ConfigRefresh` | 外部配置变更、草稿保留与刷新 | `phase-3.7` |
| `-CommitSync` | 提交快照、分步推送与同步反馈 | `phase-2.5` |
| `-OperationRecovery` | 操作记录、未完成状态及恢复 | `phase-3.1` |
| `-BranchDelete` | 保护分支、多地址阻断、删除与显式 lease | `phase-3.2` |
| `-WorktreeDelete` | 工作树预检、删除、联动清理及部分失败 | `phase-3.3` |
| `-MergeRevert` | 定向合并、撤销与冲突归属 | `phase-3.4` |
| `-StashDiscardIgnore` | 暂存工作、丢弃恢复、回收与忽略规则 | `phase-3.5` |
| `-ResetRewrite` | 本地撤回、远端改写、指定修订推送、保护与恢复 | `phase-3.6` |
| `-TrimSurface` | 首版入口和资源裁剪 | `phase-4.1` |
| `-HighDpi` | PE DPI 清单和 150%／200% 离屏布局渲染 | `phase-4.2` |
