# GitNest

面向使用 AI Agent 编程的 Git 初学者的中文 Windows 桌面客户端，基于 [SourceGit](https://github.com/sourcegit-scm/sourcegit) 改造。

GitNest 的目标是通过图形界面审阅 AI 的累计成果和未提交修改，选择提交、同步与合并，并清理任务遗留的工作树和分支。产品需求和操作约定见 [产品与技术设计](docs/design.md)。

阶段一构建基线和阶段二批次 2.1 已完成，可构建并运行 GitNest `0.1.0-dev`。欢迎页提供 Git 检测、已有全局提交身份展示、打开／新建和克隆入口；仓库导航显示“提交历史／修改／暂存的工作”。底座为 SourceGit 稳定版 `v2026.24`，后续产品流程按 [实施计划](docs/implementation-plan.md) 逐批推进。[实施记录](docs/implementation-log.md) 保存每批变更与验证结果。

## 开发构建

环境：Windows x64、Git for Windows 2.31.0 或更高版本、.NET 10 SDK。`global.json` 指定 SDK `10.0.404`，允许同一功能带的后续补丁。

首次准备工具（SDK 安装在工作区 `.tools/dotnet/`）：

```powershell
./build/scripts/bootstrap-dotnet.ps1
```

构建 Release：

```powershell
./build/scripts/build-gitnest.ps1
```

运行 Git 检测与真实仓库集成检查：

```powershell
./build/scripts/test-gitnest.ps1
```

发布可直接运行的 Windows x64 目录：

```powershell
./build/scripts/build-gitnest.ps1 -Publish
./artifacts/windows-x64/GitNest.exe
```

发布目录包含 .NET 运行依赖；Git for Windows 由用户单独安装。构建日志位于 `artifacts/logs/`，输出位于 `artifacts/windows-x64/`。源码沿用上游的 `SourceGit.slnx`、`src/SourceGit.csproj` 和内部命名空间，程序名为 `GitNest.exe`。

## 本机数据

默认使用 `%APPDATA%/GitNest/` 保存设置和缓存。便携使用时，在 `GitNest.exe` 同级创建 `gitnest-data/` 目录，数据即存放于该目录。

首次启动默认使用简体中文，保留浅色、深色及跟随系统主题。更新采用手动下载方式，发行渠道在安装交付阶段确定。

## 文档与许可

- [实施计划](docs/implementation-plan.md)与[实施记录](docs/implementation-log.md)
- [上游版本、SDK 与构建验证](docs/upstream-baseline.md)
- [SourceGit 复用核对](docs/sourcegit-reuse-audit.md)
- [依赖清单](docs/dependency-inventory.md)
- [MIT 许可证](LICENSE)、[署名与来源](NOTICE.md)、[第三方许可](THIRD-PARTY-LICENSES.md)
