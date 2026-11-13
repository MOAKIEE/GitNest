# GitNest 集成检查

在项目根目录运行：

```powershell
./build/scripts/test-gitnest.ps1
```

需要 Windows x64、项目 SDK 和 Git for Windows。脚本构建并运行 `GitNest.IntegrationTests` 控制台程序；检查失败时返回非零退出码，成功时打印通过数量。

版本探测使用该程序自己的 apphost 作为可执行夹具，检查真实进程退出、输出管道和超时行为。配置读取调用真实 Git，使用独立临时全局配置和临时仓库，并核对配置文件内容哈希。

临时路径固定在系统临时目录下的 `GitNest-tests-<随机标识>/`，清理前验证路径；测试进程退出前恢复覆盖的环境变量。
