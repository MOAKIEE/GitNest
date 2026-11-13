# GitNest 集成检查

在项目根目录运行：

```powershell
./build/scripts/test-gitnest.ps1
```

需要 Windows x64、项目 SDK 和 Git for Windows。脚本构建并运行 `GitNest.IntegrationTests` 控制台程序；检查失败时返回非零退出码，成功时打印通过数量。

版本探测使用该程序自己的 apphost 作为可执行夹具，检查真实进程退出、输出管道和超时行为。配置读取调用真实 Git，使用独立临时全局配置和临时仓库，并核对配置文件内容哈希。

临时路径固定在系统临时目录下的 `GitNest-tests-<随机标识>/`，清理前验证路径；测试进程退出前恢复覆盖的环境变量。

## 界面探针

```powershell
./build/scripts/probe-gitnest.ps1
```

在交互式 Windows 会话运行。探针加载实际应用资源、控件和 ViewModel，使用隔离配置及真实 Git 工作树，触发控件绑定和双击路由事件，等待文件监听刷新，并以 `RenderTargetBitmap` 保存截图。结果与 PNG 位于 `artifacts/probes/phase-2.2/`；临时仓库完成后移至回收站。编辑器和终端使用探针进程记录实际收到的路径及工作目录。原生文件选择器与资源管理器窗口需要另行桌面验收。

运行 `./build/scripts/probe-gitnest.ps1 -Comparison` 另加累计差异页面检查，包含目标持久化、两种比较方式、文本／二进制查看、未提交修改单独展示、定时过期检查、刷新、目标删除、快速切换目标以及关闭后丢弃旧请求。结果与截图写入 `artifacts/probes/phase-2.3/`。
