# GitNest

面向 AI 辅助编程工作流的跨平台 Git 桌面客户端，基于 [SourceGit](https://github.com/sourcegit-scm/sourcegit) 改造。

GitNest 帮助你通过直观的图形界面审阅 AI 在不同工作树中的累计改动与未提交修改，安全完成提交、同步、合并、回退与分支/工作树清理，并将复杂的底层历史操作留给外部工具处理。

## 核心特性

- **工作树任务概览与累计差异**
  - 在统一列表中查看主仓库与所有关联工作树的状态、未提交变更计数与同步进度，支持为工作树设置本地别名与完成状态。
  - 支持按「共同祖先 → 任务 HEAD」或「双版本端点」审阅任务累计提交成果，独立查看未提交修改，并在引用变更时提醒快照过期。
- **清晰可靠的提交与同步**
  - 文件级暂存与取消暂存，完整保留已有的部分暂存状态。
  - 「补充到上一次提交（Amend）」独立于常规提交区，提交与推送分步反馈，推送失败可仅重试推送而不重复提交。
  - 默认采用快进拉取（`--ff-only`）与明确的单分支推送，避免隐式附带标签或覆盖跟踪缓存。
- **面向安全的操作边界**
  - 默认在本地保护 `main`、`master`、`develop` 与远端默认分支，防止误删分支或误改写共享历史。
  - 远端删除与历史改写前核对真实推送地址，并使用显式 `--force-with-lease`。
  - 丢弃或删除文件前自动保存本地恢复备份（默认保留 30 天），支持系统回收站（默认）与直接删除；删除分支或改写历史前自动创建本地恢复引用。
- **外部工具协同与只读保护**
  - 实时监听工作目录与 Git 元数据变化；遇到外部命令行正在进行的 Rebase、Cherry-Pick、Bisect 或冲突状态时只读提示并阻断冲突写入，不覆盖外部流程。
- **Scoped Git 配置管理**
  - 单页查看 `Global` / `Local` / `Worktree` 作用域的有效配置、来源文件与 `includeIf` 条件包含关系，保留多值配置并在保存时检测外部改动。
- **跨平台与界面体验**
  - 支持 **Windows**、**macOS** 与 **Linux**。
  - 提供**简体中文**与**英文**界面，支持浅色、深色及跟随系统主题，支持高 DPI 缩放。

## 下载与安装

请前往 [GitHub Releases](https://github.com/MOAKIEE/GitNest/releases) 下载对应平台的发行包：

- **Windows**：提供 x64 安装包（`GitNest-Setup-*-win-x64.exe`，默认安装至 `%LOCALAPPDATA%\Programs\GitNest`，不修改系统 `PATH`）及 x64 / ARM64 便携压缩包。
- **macOS**：提供 Intel（`osx-x64`）与 Apple Silicon（`osx-arm64`）`.app` 压缩包。
- **Linux**：提供 x64 与 ARM64 的 `.tar.gz`、`.AppImage`、`.deb` 及 `.rpm` 安装包。

> **前置依赖**：GitNest 发行包**不捆绑 Git**。使用前请先在系统中安装 Git（建议 `2.31.0` 及以上版本），首次启动会自动检测并引导配置。

## 本地数据与便携模式

- **常规模式**：配置、任务别名、恢复备份与缓存默认保存在系统用户配置目录（Windows 为 `%APPDATA%\GitNest`）。卸载或覆盖升级不会删除用户配置和 Git 仓库。
- **便携模式**：在 `GitNest` 可执行文件同级目录创建一个名为 `gitnest-data` 的空文件夹，应用数据即会读写至该目录。

## 从源码构建

构建环境需要 `.NET 10` SDK（详见 [`global.json`](global.json)）与 Git。

```bash
git clone --recurse-submodules https://github.com/MOAKIEE/GitNest.git
cd GitNest
```

Windows PowerShell 快速发布：

```powershell
# 若系统未安装对应 .NET 10 SDK，可先下载到工作区 .tools/dotnet/
./build/scripts/bootstrap-dotnet.ps1

# 编译 Release 并输出自包含程序至 artifacts/windows-x64/
./build/scripts/build-gitnest.ps1 -Publish
```

跨平台与安装包打包详细说明请参阅 [构建文档](build/README.md)。

## 相关文档与开源许可

- [产品与技术设计](docs/design.md)
- [发行说明](docs/release-notes.md)
- [上游基线与构建说明](docs/upstream-baseline.md)
- [构建依赖清单](docs/dependency-inventory.md)
- [仓库协作与自动化工作流](docs/repository-workflow.md)
- 基于 MIT 许可开源，详见 [LICENSE](LICENSE)、[NOTICE.md](NOTICE.md) 与 [THIRD-PARTY-LICENSES.md](THIRD-PARTY-LICENSES.md)
