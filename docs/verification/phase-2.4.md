# 批次 2.4 验证记录

环境：Windows x64、Git `2.45.1.windows.1`、.NET SDK `10.0.404`。开始日期：2026-11-13。

## 2.4a · 配置来源、多值读取与脱敏基础

新增 `GitConfigSnapshot`、`GitConfigEntry` 等配置模型，并在 `Commands.Config` 中扩展 `ReadSnapshotAsync`、`SetScopedAsync` 与 `InspectSigningStatus`：

- 在目标目录直接以 `ArgumentList` 执行 `git config --list --includes --show-origin --show-scope --null`，不附带 UI 内部 `-c` 覆写，完整保留系统、全局、仓库、工作树、`include`/`includeIf` 以及外部环境变量运行时来源。
- 通过 `git rev-parse --path-format=absolute` 定位裸仓库／工作树／子目录对应的 `.git/config` 与 `config.worktree`，修正子目录打开时相对 `file:.git/config` 来源的解析。
- 保留多值列表顺序；`credential.helper` 按 Git 规则在遇到空字符串时清空此前累积链并继续累加；`remote.*.url`/`pushurl`/`fetch` 与 `http.<url>.proxy` 各自独立归集。
- 对 `http.extraheader`、敏感配置键及 URL 内嵌 `user:secret@` 执行脱敏展示，诊断输出不留存明文密码或令牌，保留普通 SSH 地址。
- `SetScopedAsync` 支持显式空字符串与 `--unset` 区分操作；未启用 `extensions.worktreeConfig` 时拒绝写入 `--worktree`，且单值写入不破坏既有本地多值记录。
- `InspectSigningStatus` 检查 `openpgp`／`x509`／`ssh` 格式、密钥配置、工具路径及 SSH 签名所需的 Git `2.34.0` 门槛。

验证命令：

```powershell
./build/scripts/build-gitnest.ps1
./build/scripts/test-gitnest.ps1
```

结果：Release 构建 **0 警告、0 错误**；**102 项集成检查全部通过**（既有 75 项，新增 27 项配置检查）。日志：`artifacts/logs/2.4a-integration-tests.log`、`artifacts/logs/2.4a-build.log`。

| 检查范围 | 实际结果 |
| --- | --- |
| 继承与包含 | 全局继承、本地覆盖、`include.path` 与 `includeIf "onbranch:..."` 条件包含及来源文件均正确识别 |
| 多值与 URL 覆盖 | `credential.helper` 跨层累积及空值重置、`remote.*` 多地址与 refspec、`http.<url>.proxy` 独立解析正确 |
| 子目录与裸仓库 | 从子目录读取准确定位仓库配置与来源；裸仓库无工作区时不触发 `--show-toplevel` 报错 |
| 运行时与只读 | `GIT_CONFIG_COUNT` 环境覆写识别为 `Command` 运行时来源；读取前后全局、包含及本地配置 SHA-256 一致 |
| 脱敏与异常 | URL 凭据、`http.extraheader` 与代理密码均被脱敏，SSH 地址保留；损坏配置文件返回明确读取失败 |
| 作用域写入 | 显式空值覆盖继承与 `--unset` 恢复继承区分正确；未开启 `extensions.worktreeConfig` 拒绝写入，开启后仅影响目标工作树；拒绝覆盖本地多值 |
| 签名依赖检查 | 缺失密钥、缺失签名程序与旧版 Git 下 SSH 签名均准确报告原因 |

接续批次 2.4b：重构 `RepositoryConfigure` 常用设置编辑界面（来源标签、多值展示、远端与当前分支上游、代理、签名检查、高级换行与作用域保存），并补齐 UI 探针与截图核对。

## 2.4b · 配置来源 UI 与常用设置编辑（2026-11-14）

在 2.4a 读取与脱敏基础上，补齐 `Remote`／`Branch` 定向写操作与共享直连 Git 执行器，按 SourceGit 原生扁平分组表单风格重构“仓库配置 → GIT配置”页，并补充 `RepositoryConfigure` 工作流集成测试与 `-Config` 交互探针：

- **共享直连与定向写入**：在 `Commands.Command` 提取共享 `RunGitDirectAsync` 与 `RedactResult`（`Commands.Config` 复用并移除重复实现）；在 `Commands.Remote` 新增 `AddTargetedAsync`、`DeleteTargetedAsync`、`RenameTargetedAsync`、`SetUrlTargetedAsync`（精确 POSIX ERE `^...$` 匹配主获取／推送 URL，保留附加 URL 与自定义 fetch refspec）；在 `Commands.Branch` 新增 `SetUpstreamTargetedAsync` 与 `UnsetUpstreamTargetedAsync`（兼容未拉取远程引用和 unborn 分支）。
- **ViewModel 与状态交互**：新增 `ConfigScopeOption`、`ConfigChoiceOption`、`ConfigFieldItem`、`RemoteConfigDraft`、`ConfigReadOnlyEntry`、`ConfigSaveSummary`（`src/ViewModels/ConfigureModels.cs`）并更新 `RepositoryConfigure.cs`；`extensions.worktreeConfig` 未开启时隐藏工作树作用域；区分继承态、`为当前层覆盖`、`设为空值 ""`、`移除本层` 与单字段还原；完整展示 `credential.helper` 有序链（含 `""` 重置条目）；保存前核对目标作用域外部变更防止误覆盖，保存后重新读取快照并报告部分失败或被更高优先级作用域覆盖提示。
- **SourceGit 原生风格界面对齐与视觉修正**：新增 `ConfigFieldMetaBar` 并重构 `RepositoryConfigure.axaml`（保留提交信息模板／ISSUE追踪／自定义操作／AI 既有页签兼容）。取消外层重卡片边框，统一采用两列右对齐标签（`144,*`）、`group_header_label` + `1px` 分割线、`small` 辅助字号与 `28px` 控件高度；将高级设置区从默认圆角 `Expander` 改为原生 `ToggleButton.group_expander` 平铺折叠面板；将提示横幅由横向 `StackPanel` 改为 `Grid` 修复窄窗口右裁切，折行左侧长标签，启用 `SanitizedDiagnostics` 自动换行消除水平滚动条遮挡，并消除高优先级覆盖保存警告标题重复输出；同步扩展 `en_US.axaml`／`zh_CN.axaml` 对应词条。

验证命令：

```powershell
./build/scripts/build-gitnest.ps1 -Publish
./build/scripts/test-gitnest.ps1
./build/scripts/probe-gitnest.ps1 -Config
```

结果：
- Release 构建与 Windows x64 自包含发布 **0 警告、0 错误**（输出 `artifacts/windows-x64/GitNest.exe`，日志 `artifacts/logs/2.4b-build.log`）。
- 双语资源键对齐核对（PowerShell XML 比对 `en_US.axaml` 与 `zh_CN.axaml`）：**1254** 条键完全对称（注：`localization-check.js` 因本机未安装 npm `fs-extra` 模块改由 PowerShell 直接比对）。
- **120 项集成检查全部通过**（既有 102 项 + 2.4b 新增 18 项定向远端/上游及保存冲突/部分失败检查，日志 `artifacts/logs/2.4b-integration-tests.log`）。
- **13 项 UI 探针检查全部通过**（`artifacts/probes/phase-2.4/results.txt`、`artifacts/logs/2.4b-probe.log`），以下 6 张实机截图已完成目视核对：

| 场景与截图文件 | 验证要点与目视结论 |
| --- | --- |
| `artifacts/probes/phase-2.4/config-light-overview.png` | 浅色模式 `820×660`：作用域选择、工作树禁用说明、`Global`/`Local` 来源徽章、完整“远端名 (如 upstream)”输入框、保留附加 URL/refspec 提示及当前分支上游映射排版清晰 |
| `artifacts/probes/phase-2.4/config-light-signing-auth.png` | 浅色模式：URL 特定代理脱敏展示、`未找到签名程序` 警告横幅与 `#1 "" 重置`/`#2 manager` 只读认证助手链折行整齐 |
| `artifacts/probes/phase-2.4/config-light-signing-advanced.png` | 浅色模式：原生 `ToggleButton.group_expander` 折叠区块、`core.autocrlf` 不自动重规范化提示、`core.eol` 只读值与折行脱敏诊断摘要无裁切 |
| `artifacts/probes/phase-2.4/config-dark-narrow-shadow-warning.png` | 深色模式 `680×560` 窄窗口：选择 Global 写入时黄色警告与“当前生效值由更高优先级 Local 决定”提示换行完整，底部阴影覆盖警告单行无重复 |
| `artifacts/probes/phase-2.4/config-dark-partial-failure.png` | 深色模式：部分保存成功场景将合法字段 `user.name` 写入 `.git/config`，并在底部错误栏逐项列出未指定远端分支的上游保存失败明细 |
| `artifacts/probes/phase-2.4/config-worktree-scope.png` | 启用 `extensions.worktreeConfig` 时显示 `--worktree` 作用域，并通过点击“设为空值 ""”独立覆写目标工作树 `http.proxy`（不影响主工作树与全局） |
