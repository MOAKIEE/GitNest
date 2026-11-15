# GitNest 发行记录（Windows x64 首版基线）

- **产品名称**：GitNest
- **版本号**：`0.1.0-dev`（见根目录 [VERSION](../VERSION)）
- **目标平台**：Windows 10 / Windows 11 x64
- **基准代码**：基于 MIT 许可的 SourceGit `v2026.24`，固定提交 `2735a8ad80c2443bc736b7e6e501f1052b61d940`，保留上游许可证与署名（[LICENSE](../LICENSE)、[NOTICE.md](../NOTICE.md)、[THIRD-PARTY-LICENSES.md](../THIRD-PARTY-LICENSES.md)）。

## 交付产物与校验信息

| 产物 | 路径 | 说明与校验摘要 |
| --- | --- | --- |
| Windows x64 安装包 | `artifacts/installer/GitNest-Setup-0.1.0-dev-win-x64.exe` | `35.81 MB`，SHA-256 `B78DA5A1D3FB70CC75D7FF670345DF605F091C405F61EC0AC5BFB48302BE4E16`，基于 Inno Setup 6.7.3 构建 |
| Windows x64 自包含程序 | `artifacts/windows-x64/GitNest.exe` | `net10.0` 自包含发布，内嵌 `PerMonitorV2` 与 `true/pm` DPI 清单 |
| 依赖快照与清单 | [dependency-inventory.md](dependency-inventory.md) / [dependency-snapshot.json](dependency-snapshot.json) | 32 个发布 NuGet 包与源码固定子模块 `AvaloniaEdit`（MIT） |

## 核心功能与安全边界摘要

1. **只读总览、任务对比与本地配置管理**：工作区状态、工作树任务别名及本地目录直达；以共同祖先或两分支端点的任务累计差异与快照失效重检；按 Global / Local / Worktree 作用域显式读取与保存 Git 配置（保留 `includeIf`、多值凭据/代理规则、高优先级覆盖提醒与外部变更复核）。
2. **文件级提交、同步与操作记录**：分步暂存/取消暂存、局部仓内身份补齐、提交失败或推送失败互不重复、`--ff-only` 拉取与明确的合并交接、单分支推送、最近操作日志与崩溃恢复核对（所有诊断与落盘记录均脱敏敏感 URL 凭据与认证头）。
3. **高安全边界操作**：
   - 分支删除与工作树删除显式区分本地/远端及删前 `refs/gitnest/backups/*` 本地恢复引用，支持显式 `--force-with-lease`、分步失败保留现场与联动清理顺序执行；
   - 定向合并与单提交 `revert` 预检、磁盘/暂存 SHA-256 冲突归属守卫（外部 AI 已介入时自动停止回滚，绝不强行覆盖外部或历史冲突状态）；
   - `stash` 精确 SHA 绑定与冲突保留；丢弃操作区分暂存区/HEAD 双基准并前置 30 天本地恢复备份，未跟踪文件优先送入系统回收站；
   - `reset` 限定 `--mixed`/`--hard`、默认分支本地保护、基于实查远端摘要的租约改写及重启后对账；
   - 首版已移除 AI 辅助提交、Custom Actions、Git-Flow、Bisect、Interactive Rebase、Cherry-Pick、内部冲突解决器及补丁/标签/子模块/工作树创建写入口（保留只读子模块、标签与 LFS 状态展示）。

## 安装与运行时说明

- **独立 Git 要求**：安装包与绿色目录**不捆绑 Git**；未安装 Git 或版本低于要求时由首次引导界面检测并提示安装系统 Git for Windows 后即可直接复用。
- **安装目录与数据隔离**：使用当前用户权限默认安装到 `%LOCALAPPDATA%\Programs\GitNest`，不修改系统或用户 `PATH` 环境变量；用户配置、状态与恢复数据保存于 `%APPDATA%\GitNest`，升级安装与卸载均完整保留用户配置及磁盘 Git 仓库。
- **分发与升级**：当前交付为未签名的本地安装包；运行新安装包可覆盖升级。

## 2026-11-15 清理与更新检查

- 移除无调用的旧命令、模型及冲突编辑器残留类型，界面语言精简为简体中文和英文；旧配置使用已移除的语言时回退简体中文。
- 更新检查改为读取 `MOAKIEE/GitNest` 最新正式 GitHub Release，支持手动检查及可选的启动时检查，下载入口指向本项目发行页。
- 版本比较支持补丁号、预发行后缀与构建元数据，当前版本完整显示为 `v0.1.0-dev`；不可访问或无效的发行信息明确报告失败。
- 适配 Git 2.56.1 按 URL 推送时的跟踪引用更新行为：普通推送、远端删除和历史改写使用进程内临时远端，避免隐式覆盖获取端缓存，配置文件保持原样。

## 2026-11-15 审查修复

- 重命名原路径存在新文件时阻止丢弃；恢复完整保留已删除文件、暂存删除与仅索引删除状态。
- 分支删除、批量删除和工作树联动清理统一执行默认分支保护。
- 远端预检与执行使用同一实际推送地址，多推送地址先阻断；获取／推送地址分离时保留获取端跟踪缓存。
- 单分支和指定修订推送固定来源提交，显式禁用自动附带标签；指定修订的强制模式统一使用带恢复引用的历史改写流程，重启对账核对远端地址指纹。

## 2026-11-15 下一阶段精简与交互调整（批次 1–6）

- 清理未引用的旧命令与模型（`Commands.Am`、`Commands.Clean`、`Models.CleanMode`、未调用的 `Worktree` / `Submodule` 写入方法），移除 5 个未使用图标键和 44 个未使用多语言文案键，中英词条同步保持各 `1387` 项（差集为 `0`）。
- 精简 Rebase、MergeMultiple 与文件右键“假设未修改”写入入口：保留外部命令行 `.git/rebase-merge` / `.git/rebase-apply` / `BISECT_LOG` 只读横幅监测与写保护，保留已标记文件的只读查看及恢复跟踪入口。
- 彻底移除内置提交模板及 `commit.template` 自动读取，升级旧仓库设置前先自动备份 `commitTemplates` 配置，提交说明选择器保留近期提交记录与清空动作。
- 收口 Git LFS 与子模块设置：LFS 保留只读指针识别及状态/跟踪/安装高级子菜单（移除 Locks 与 Prune）；移除旧子模块自动更新设置，在切分支与新建分支弹窗提供默认关闭的按需两段显式确认（含失败后主仓库与子模块分离汇报）。
- 将仓库配置整合为单页（问题追踪规则折叠于高级设置），将 Amend 从常规提交区隔离至工作区卡片更多操作菜单的独立“补充到上一次提交...”弹窗：未推送提交允许就地修改，受保护分支阻止已推送改写并引导新建提交，非保护已推送分支须用户显式确认且严格使用 `--force-with-lease` + 本地恢复引用，推送失败支持不重复 Amend 的仅重试推送恢复。
- 抽检验证保留能力（仓库统计、HEAD 归档导出 ZIP、在线头像设置与缓存接口、以及 TGA/DDS/TIFF/PSD 特殊图像解码）均可正常工作。

## 最终验证依据

环境：Windows 11 x64（build 26300）、Git `2.45.1.windows.1`、.NET SDK `10.0.404`。（注：本轮本机仅安装 Git `2.45.1.windows.1`，无本地 `2.56.1` 二进制可复测。）

- 发布构建：`.\build\scripts\build-gitnest.ps1 -Publish`（0 警告 0 错误，输出 `artifacts/windows-x64/GitNest.exe`）
- 集成检查：`.\build\scripts\test-gitnest.ps1 -NoBuild`（**636 / 636** 通过，完整覆盖既有套件及 `next-stage-1` 至 `next-stage-6`）
- 全量 UI 探针：12 组依次执行合计 **228 / 228** 通过（`-Comparison` 30、`-Config` 13、`-ConfigRefresh` 4、`-CommitSync` 11、`-OperationRecovery` 13、`-BranchDelete` 16、`-WorktreeDelete` 20、`-MergeRevert` 19、`-StashDiscardIgnore` 15、`-ResetRewrite` 19、`-TrimSurface` 61、`-HighDpi` 7）
- 安装包生命周期：`.\build\scripts\verify-installer.ps1`（**38 / 38** 全流程静默安装、覆盖升级、冷启动、用户配置与仓库留存及卸载验证通过）
- 详细首版基线验收条目核对见 [phase-4.3.md](verification/phase-4.3.md)，后续精简变更记录见 [next-stage-plan.md](next-stage-plan.md) 与 [implementation-log.md](implementation-log.md)。
- 2.2 原生窗口操作与完整进程重启验收仍待补，具体条目见 [phase-2.2.md](verification/phase-2.2.md)。
