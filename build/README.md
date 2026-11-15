# GitNest 构建

在项目根目录的 PowerShell 中执行：

```powershell
./build/scripts/bootstrap-dotnet.ps1
./build/scripts/build-gitnest.ps1 -Publish
```

`bootstrap-dotnet.ps1` 按 `global.json` 从微软发行元数据找到 Windows x64 SDK ZIP，核对 SHA-512 后解压到 `.tools/dotnet/`。

`build-gitnest.ps1` 优先使用工作区 SDK，也支持系统中符合 `global.json` 的 SDK；初始化固定子模块后构建 Release。`-Publish` 生成自包含的普通 .NET 程序及许可证，输出到 `artifacts/windows-x64/`。

本阶段使用以下发布命令：

```powershell
./.tools/dotnet/dotnet.exe publish src/SourceGit.csproj -c Release -r win-x64 --self-contained true -p:DisableAOT=true -o artifacts/windows-x64
```

图标源为 `src/Assets/gitnest.svg`。安装 Pillow 后可用 `python build/scripts/generate-icon.py` 重建 `src/App.ico`，日常构建直接使用已提交的 ICO 文件。

安装器与验证脚本：

- Windows x64 单用户安装包由 [gitnest.iss](installer/gitnest.iss) 通过 Inno Setup 6（`ISCC.exe`）构建，使用当前用户权限安装至 `%LOCALAPPDATA%\Programs\GitNest`，用户配置与数据写入 `%APPDATA%\GitNest`，不随包分发 Git，不修改 `PATH`。
- `./build/scripts/build-installer.ps1`：自动执行（或配合 `-SkipPublish` 跳过）发布后校验许可证齐备与未随包携带 `git.exe`，输出安装包到 `artifacts/installer/GitNest-Setup-<VERSION>-win-x64.exe` 并打印 SHA-256。
- `./build/scripts/verify-installer.ps1`：执行全新静默安装、随包许可证与 `PerMonitorV2` 清单校验、冷启动冒烟、高版本覆盖升级以及完整卸载验证（确认保留 `%APPDATA%\GitNest` 与用户 Git 仓库，并将测试残留移至回收站）。
- `./build/scripts/test-gitnest.ps1` 与 `./build/scripts/probe-gitnest.ps1`：运行真实 Git 集成测试及包括 `-TrimSurface`、`-HighDpi` 在内的 UI 探针，实施记录见 [实施计划](../docs/implementation-plan.md)。
