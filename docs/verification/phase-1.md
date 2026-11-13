# 阶段一验证记录

日期：2026-11-13。环境：Windows x64 `10.0.26300`、Git for Windows `2.45.1.windows.1`、.NET SDK `10.0.404`。

## 构建及发布

| 检查 | 执行与结果 |
| --- | --- |
| 上游原始源码 | `./.tools/dotnet/dotnet.exe build SourceGit.slnx -c Release --nologo`：成功，0 警告、0 错误 |
| 工作区 SDK 选择 | `./build/scripts/bootstrap-dotnet.ps1`：识别已安装的 `10.0.404`；构建脚本使用该 SDK |
| GitNest 构建 | `./build/scripts/build-gitnest.ps1 -Publish`：Release 构建成功，0 警告、0 错误 |
| Windows x64 发布 | 同一脚本完成 `win-x64` 自包含发布，命令退出码为 0，生成 `artifacts/windows-x64/GitNest.exe` 与 `coreclr.dll` |
| 产品信息 | Windows 文件属性显示 ProductName 为 `GitNest`，ProductVersion 为 `0.1.0-dev`，附带构建时 Git 提交信息 |
| 发布许可 | 输出包含 `LICENSE`、`NOTICE.md`、`THIRD-PARTY-LICENSES.md`、`licenses/AvaloniaEdit-LICENSE.txt` |
| 子模块 | `git submodule status` 显示固定提交 `ddf00a91dc0da29b85c79b85c9583860213a99ff`，无源码修改 |
| 变更格式 | `git diff --check` 对本批修改通过 |

完整日志位于本机忽略目录 `artifacts/logs/`：`1.2-upstream-build.log`、`gitnest-build.log`、`gitnest-publish.log`、`1.3-final-build.log`。

## 真实桌面启动

在 `artifacts/smoke/中文 仓库 #1/` 创建真实 Git 仓库，提交 `你好.txt` 后追加一行未提交修改。以该路径启动发布的 `GitNest.exe`。

- 主窗口标题为 `GitNest · 中文 仓库 #1 - Default`，正确打开含中文、空格与 `#` 的仓库路径。
- 界面默认使用简体中文，历史中可见真实初始提交与 `main` 分支；修改入口显示 1 个文件。
- 关于页面显示独立图标、`GitNest`、`v0.1.0-dev`、中文产品说明和 `基于 SourceGit v2026.24 · MIT 许可`，上游链接有明确来源标识。
- 应用菜单中没有上游更新入口。
- 实际生成 `%APPDATA%/GitNest/preference.json`，其中 `Locale=zh_CN`、`Theme=Default`、`Check4UpdatesOnStartup=false`，Git 路径自动检测为 `C:/Program Files/Git/bin/git.exe`。
- 仓库 UI 状态写入 `.git/gitnest.uistates`。
- 启动前后比对 `%APPDATA%/SourceGit/` 的文件清单与内容哈希，结果一致；本次测试中该目录没有文件。

桌面检查使用 computer-use 技能。用户按物理 Esc 停止桌面检查后，停止全部界面操作；上述结果为停止前的实际观察及随后对已生成文件的读取。

## 阶段接续

阶段二从 Git 检测／首次引导及核心页面改造开始。产品级提交、同步和删除等流程仍按实施计划的对应批次进行 Git 集成验收。
