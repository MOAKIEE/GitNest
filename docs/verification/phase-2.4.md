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
