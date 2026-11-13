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

安装器与发行流水线在实施计划阶段四接入，记录见 [实施计划](../docs/implementation-plan.md)。
