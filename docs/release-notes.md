# GitNest 发行说明

## Unreleased

- **可配置的文件删除方式与 30 天备份保障**：在“通用配置 → 文件删除方式”及丢弃确认弹窗中支持切换“移入系统回收站”（默认）与“直接删除”，覆盖未跟踪文件清理及 HEAD 基线下的新增已暂存文件；删除前先写入并校验 30 天本地恢复备份，自动跳过 `.git`、忽略文件、子模块与嵌套仓库，并在目录连接点场景下仅移除链接本身。

## v0.1.0-preview.1

首个跨平台预览版，基于 [SourceGit](https://github.com/sourcegit-scm/sourcegit) `v2026.24`（MIT）打造，面向使用 AI 编程工具的开发者提供清晰的成果审阅与安全 Git 操作体验。

### 主要特性

- **工作树任务概览与累计差异**：为工作树设置本地别名与完成标记，按共同祖先或双端点审阅 AI 的累计提交成果，独立查看未提交修改，并在引用变动时提醒快照过期。
- **清晰可靠的日常同步**：文件级暂存与部分暂存保留、独立安全弹窗的“补充到上一次提交”、`--ff-only` 快进拉取、明确目标的单分支推送（失败重试不重复提交）。
- **高安全边界操作**：
  - 本地保护 `main` / `master` / `develop` 及远端默认分支（可在仓库设置中显式解除）；
  - 远端删除与历史改写强制核对推送地址并使用显式 `--force-with-lease`；
  - 丢弃前保存 30 天本地恢复备份，未跟踪文件优先移入系统回收站；
  - 删除分支、detached 工作树与历史改写前自动生成本地恢复引用（`refs/gitnest/backups/*`）。
- **外部工具协同与只读保护**：外部命令行执行 Rebase / Cherry-Pick / Bisect 或产生冲突时显示状态提示并锁定冲突写入；合并或 `revert` 冲突在确认无外部介入时自动清理回滚。
- **Scoped Git 配置管理**：单页展示 Global / Local / Worktree 有效值、配置来源及 `includeIf` 覆盖关系，支持多值项保留与脱敏诊断。
- **跨平台支持与双语主题**：支持 Windows、macOS 与 Linux，内置简体中文与英文界面、浅色/深色及跟随系统主题。

### 安装与使用提示

- **系统 Git 要求**：发行包**不捆绑 Git**，请确保系统已安装 Git（建议 `2.31.0` 及以上）；首次启动会自动检测并引导配置。
- **数据目录与便携模式**：默认使用系统用户目录保存配置（Windows 为 `%APPDATA%\GitNest`）；在程序同级目录创建 `gitnest-data/` 即可启用便携模式。
- **预览版说明**：当前版本为预览版（Pre-release），内置“检查更新”默认查询 GitHub 最新正式发行版（`/releases/latest`），预览版更新请关注 [GitHub Releases](https://github.com/MOAKIEE/GitNest/releases)。

### 开源许可与致谢

本项目基于 MIT 许可开源，保留 SourceGit 上游版权与全部第三方开源声明，详见 [LICENSE](../LICENSE)、[NOTICE.md](../NOTICE.md) 与 [THIRD-PARTY-LICENSES.md](../THIRD-PARTY-LICENSES.md)。
