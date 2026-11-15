# GitNest 构建与打包说明

GitNest 基于 `.NET 10`（见根目录 `global.json`）与 Avalonia UI，支持 Windows、macOS 与 Linux 构建。

## 快速构建（Windows）

在项目根目录的 PowerShell 中执行：

```powershell
# 可选：若本机未安装 global.json 对应的 .NET SDK，可下载到工作区 .tools/dotnet/
./build/scripts/bootstrap-dotnet.ps1

# 编译 Release 并发布自包含程序至 artifacts/windows-x64/
./build/scripts/build-gitnest.ps1 -Publish
```

构建 Windows x64 单用户安装包（需安装 [Inno Setup 6](https://jrsoftware.org/isinfo.php)）：

```powershell
./build/scripts/build-installer.ps1
```

安装包输出至 `artifacts/installer/GitNest-Setup-<VERSION>-win-x64.exe`。

## 通用跨平台发布命令

任意已安装 .NET 10 SDK 的系统均可使用以下命令发布对应平台自包含目录（以 `linux-x64` 为例，支持 `win-x64`、`win-arm64`、`osx-x64`、`osx-arm64`、`linux-x64`、`linux-arm64`）：

```bash
git submodule update --init --recursive
dotnet publish src/SourceGit.csproj -c Release -r linux-x64 --self-contained true -p:DisableAOT=true -o build/GitNest
```

## 各平台打包脚本

- **Windows**：`./build/scripts/package.win.ps1` 生成便携 ZIP；`./build/scripts/build-installer.ps1` 生成安装包。
- **macOS**：`./build/scripts/package.osx-app.sh` 生成 `GitNest.app` 压缩包（含图标生成、编译 `tools/setsid-macos/setsid.c` 与 ad-hoc 签名）。
- **Linux**：`./build/scripts/package.linux.sh` 生成 `.tar.gz`、`.AppImage`、`.deb` 与 `.rpm` 包。
- **应用图标**：矢量源文件为 `src/Assets/gitnest.svg`，安装 Python `Pillow` 后可通过 `python build/scripts/generate-icon.py` 重新导出 `src/App.ico` 与跨平台 PNG 资源。
