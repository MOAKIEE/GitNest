# 仓库协作与自动化工作流

仓库地址：[MOAKIEE/GitNest](https://github.com/MOAKIEE/GitNest)，主分支为 `main`。

## 克隆与子模块

```powershell
git clone --recurse-submodules https://github.com/MOAKIEE/GitNest.git
cd GitNest
```

`depends/AvaloniaEdit` 为固定提交子模块，首次构建前若未自动检出，可执行 `git submodule update --init --recursive`。

## 自动化工作流

### 持续集成（`.github/workflows/ci.yml`）

在 `main` 分支代码推送、Pull Request 或手动触发时运行（仅修改文档时自动跳过）：

1. 检出源码与固定子模块；
2. 按 `global.json` 安装 `.NET 10` SDK；
3. 执行 Release 构建与 Windows x64 自包含发布；
4. 运行集成检查并归档构建产物与日志。

### 多平台发布（`.github/workflows/release.yml`）

在推送 `v*` 标签或手动触发时运行，并行构建全平台产物并自动创建或更新 GitHub Release（版本号包含 `-` 时自动标记为 Pre-release）：

- **Windows**：`win-x64`（便携 ZIP 与 Inno Setup 安装包）、`win-arm64`（便携 ZIP）
- **macOS**：`osx-x64`、`osx-arm64`（`.app` 压缩包，含 `setsid` 辅助程序）
- **Linux**：`linux-x64`、`linux-arm64`（`.tar.gz`、`.AppImage`、`.deb`、`.rpm`）
