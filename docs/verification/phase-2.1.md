# 批次 2.1 验证记录

日期：2026-11-13。环境：Windows x64、Git `2.45.1.windows.1`、.NET SDK `10.0.404`。

## 自动检查

```powershell
./build/scripts/test-gitnest.ps1
```

结果：**26 项检查全部通过**。检查程序使用实际进程作为版本探测夹具，身份查询使用已安装的真实 Git 和临时仓库。

| 检查范围 | 验证内容 |
| --- | --- |
| 程序路径 | 未设置、缺失、相对路径、存在但不可执行的文件 |
| 输出和退出码 | Git for Windows 后缀、Apple Git 格式、其他程序输出、超大数字、非零退出码 |
| 最低能力门槛 | 低于 `2.31.0` 时拒绝且保留检测到的版本；等于门槛时允许 |
| 进程生命周期 | 大量 stderr 不阻塞 stdout；超时后终止探测并及时返回 |
| 状态替换 | 有效 Git 更换为缺失路径后，旧版本归零、仓库入口判为不可用 |
| 实际 Git | 本机 Git for Windows 成功运行并通过探测 |
| 身份读取 | 全局身份、仓库覆盖及继承、include 文件、显式空值、缺失值、非法配置 |
| 特殊内容 | 中文、等号、换行保留完整；仓库路径含中文、空格与 `#` |
| 配置完整性 | 读取前后全局及仓库配置的 SHA-256 一致 |

测试通过 `GIT_CONFIG_GLOBAL` 和 `GIT_CONFIG_NOSYSTEM` 指向隔离配置，结束时恢复进程环境并清理专用临时目录。检查日志保存在 `artifacts/logs/2.1-integration-tests.log`。

## 构建和发布

```powershell
./build/scripts/build-gitnest.ps1 -Publish
```

结果：Release 构建 **0 警告、0 错误**，Windows x64 自包含发布成功。应用输出为 `artifacts/windows-x64/GitNest.exe`。

日志：`artifacts/logs/gitnest-build.log`、`artifacts/logs/gitnest-publish.log`。

## 桌面检查

使用 computer-use 技能操作隔离的便携实例，位置为 `artifacts/smoke/phase-2.1/app/`。配置和真实 Git 仓库均位于该测试目录，测试身份使用 `smoke@example.invalid`。

1. 将 Git 路径预设为一个能运行但输出无效 Git 版本的测试程序，再携带仓库路径启动应用。欢迎页显示具体失败状态和程序路径，打开／克隆按钮禁用，启动仓库保留待处理。
2. 点击“重新检测”。应用识别 `C:/Program Files/Git/bin/git.exe`，随后自动打开先前等待的 `中文 工作目录 #1` 仓库。
3. 检查侧栏：“提交历史／修改／暂存的工作”文字均显示，修改入口显示 1 个文件。打开 `你好.txt` 后，可见初始内容及新增行差异。
4. 打开新标签页。引导显示 `Git 已就绪`、完整 Git 路径、`2.45.1.windows.1` 和已有全局提交身份；打开与克隆按钮均可使用。
5. 分别点击打开和克隆按钮，确认进入对应的现有弹窗，然后取消。
6. 用本地裸仓库建立 `origin/main`。原有分支组件分别显示本地 `main`、远端 `origin` 及其 `main` 分支。
7. 比对引导前后测试全局配置 SHA-256，结果一致；实际 Git 路径已保存至 `gitnest-data/preference.json`。正常退出后，便携进程锁释放。

## Git 能力门槛

GitNest 当前门槛为 Git `2.31.0`，对应工作树锁定／prunable 元数据及 `rev-parse --path-format=absolute`。两项能力均记录于 [Git 2.31 官方发行说明](https://github.com/git/git/blob/v2.31.0/Documentation/RelNotes/2.31.0.txt)。

本批在真实 Git `2.45.1.windows.1` 上检查进程和配置行为，版本门槛通过进程夹具覆盖边界；Windows／Git 版本兼容矩阵继续安排在安装交付阶段。
