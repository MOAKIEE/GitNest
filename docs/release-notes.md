# GitNest 发行记录（Windows x64 首版基线）

- **产品名称**：GitNest
- **版本号**：`0.1.0-dev`（见根目录 [VERSION](../VERSION)）
- **目标平台**：Windows 10 / Windows 11 x64
- **基准代码**：基于 MIT 许可的 SourceGit `v2026.24`，固定提交 `2735a8ad80c2443bc736b7e6e501f1052b61d940`，保留上游许可证与署名（[LICENSE](../LICENSE)、[NOTICE.md](../NOTICE.md)、[THIRD-PARTY-LICENSES.md](../THIRD-PARTY-LICENSES.md)）。

## 交付产物与校验信息

| 产物 | 路径 | 说明与校验摘要 |
| --- | --- | --- |
| Windows x64 安装包 | `artifacts/installer/GitNest-Setup-0.1.0-dev-win-x64.exe` | `35.95 MB`，SHA-256 `12A34A2FFAD8EA8FE5C14F086B0FB9EF6A950EFE88B3191D4455C66348254707`，基于 Inno Setup 6.7.3 构建 |
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

## 2026-11-15 审查修复

- 重命名原路径存在新文件时阻止丢弃；恢复完整保留已删除文件、暂存删除与仅索引删除状态。
- 分支删除、批量删除和工作树联动清理统一执行默认分支保护。
- 远端预检与执行使用同一实际推送地址，多推送地址先阻断；获取／推送地址分离时保留获取端跟踪缓存。
- 单分支和指定修订推送固定来源提交，显式禁用自动附带标签；指定修订的强制模式统一使用带恢复引用的历史改写流程，重启对账核对远端地址指纹。

## 最终验证依据

环境：Windows 11 x64（build 26300）、Git `2.45.1.windows.1`、.NET SDK `10.0.404`。

- 发布构建：`.\build\scripts\build-gitnest.ps1 -Publish`（0 警告 0 错误）
- 真实 Git 集成测试：`.\build\scripts\test-gitnest.ps1`（424 / 424 通过）
- 全量 12 组 UI 探针共 183 项：`-Comparison`（30/30）、`-Config`（13/13）、`-ConfigRefresh`（4/4）、`-CommitSync`（11/11）、`-OperationRecovery`（13/13）、`-BranchDelete`（16/16）、`-WorktreeDelete`（20/20）、`-MergeRevert`（11/11）、`-StashDiscardIgnore`（15/15）、`-ResetRewrite`（19/19）、`-TrimSurface`（24/24）、`-HighDpi`（7/7，PE 清单及 150%／200% 离屏渲染），均通过
- 安装包生命周期：`.\build\scripts\verify-installer.ps1`（38 / 38 全流程静默安装、升级覆盖、冷启动、数据留存与卸载验证通过）
- 详细验收条目核对见 [phase-4.3.md](verification/phase-4.3.md)。
- 2.2 原生窗口操作与完整进程重启验收因桌面操作被 Esc 中止而待补，具体条目见 [phase-2.2.md](verification/phase-2.2.md)。
