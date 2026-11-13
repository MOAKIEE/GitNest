# 批次 2.3 验证记录

环境：Windows x64、Git `2.45.1.windows.1`、.NET SDK `10.0.404`。开始日期：2026-11-13。

## 2.3b · 累计差异页面与快照失效

导航新增“累计差异”，工作树菜单提供“查看本任务改动”；菜单可直接审阅其他工作树，比较操作保持双方分支原位。用户选择完整本地／远程跟踪引用后按工作树保存目标，重新打开恢复选择。

默认展示共同祖先到任务 HEAD 的已提交成果，另可切换目标当前版本到任务 HEAD 的两端差异。页面显示双方完整引用、哈希、比较基准、文件数、文本增删行数、二进制数，以及仅任务侧可达的提交列表；文件查看复用现有 `ChangeCollectionView`、`DiffContext` 和 `DiffView`。未提交数量独立刷新，“查看修改”打开对应工作树的修改页面。远程目标明确标注本地跟踪缓存。

当前活动页面每两秒核对源分支、双方哈希和仓库身份；变化后快照及页面一并标记过期，保留原哈希供审阅，点击“刷新比较”才读取新快照。快速切换目标会取消上一代读取，关闭页面后丢弃未完成结果。只读状态进程同步接收取消信号。

验证命令：

```powershell
./build/scripts/test-gitnest.ps1
./build/scripts/probe-gitnest.ps1 -Comparison
./build/scripts/build-gitnest.ps1 -Publish
```

结果：75 项真实 Git／进程集成检查、30 项交互探针检查全部通过；Release 构建 0 警告、0 错误，Windows x64 自包含发布成功。日志分别为 `artifacts/logs/2.3b-integration-tests.log`、`2.3b-ui-probe.log`、`2.3b-build-publish.log`。

30 项探针包含既有 11 项工作树检查和新增 19 项累计差异检查，覆盖导航、显式目标、无目标时未提交数量、目标保存与重载、模式切换、真实文本／二进制视图、两个工作树 HEAD 保持不变、未提交修改独立统计、后台过期及提示可见性、按钮刷新、远程缓存提示、快速选择竞态、目标删除、打开对应修改页和关闭后丢弃结果。

截图与结果保存在 `artifacts/probes/phase-2.3/`，11 张 PNG 已逐一核对：

| 截图 | 核对内容 |
| --- | --- |
| `overview-*.png`（4 张） | 别名编辑、浅／深色及窄窗口、工作树打开 |
| `comparison-choose-target.png` | 显式选择提示与独立未提交数量 |
| `comparison-merge-base-light.png` | 浅色、共同祖先、已提交成果及文本差异 |
| `comparison-binary.png` | 二进制数量与原生大小展示 |
| `comparison-two-tips-dark-narrow.png` | 深色 `1060×700`、两端比较和目标独有文件 |
| `comparison-stale.png` | 快照过期提示、固定哈希及未提交数量 |
| `comparison-remote-cache.png` | 完整远程引用与缓存提示 |
| `comparison-missing-target.png` | 目标删除提示及旧内容清除 |

探针复核中修复了下拉框重新绑定时空值反写导致保存目标丢失、过期提示的主题资源引用，以及未选目标时未提交数量停留在读取态的问题；探针清理在关闭前取走引用，避免失败路径重复关闭仓库。临时夹具及失败运行的残留目录均移至回收站。

## 2.3a · 累计差异读取基础

运行 `./build/scripts/test-gitnest.ps1`，75 项检查通过，其中累计差异新增 22 项。日志：`artifacts/logs/2.3a-integration-tests.log`。

`./build/scripts/build-gitnest.ps1` Release 构建通过，0 警告、0 错误；日志：`artifacts/logs/2.3a-build.log`。

| 检查 | 结果 |
| --- | --- |
| AI 连续两次提交且目录干净 | 共同祖先比较保留完整任务成果，排除目标独有提交的文件 |
| 两端版本比较 | 基准为目标当前提交，目标独有文件按真实方向显示为删除 |
| 选择目标与推送上游不同 | 比较固定 `refs/heads/main`，不采用任务的 `publish/task` 上游 |
| 路径与统计 | 中文、空格、`&`、`#` 重命名路径完整；文本增删行数准确，二进制独立计数 |
| 未提交修改 | 已提交差异不变，暂存／工作区／未跟踪数量单独读取 |
| 只读与失效 | 配置和索引 SHA-256 不变；目标更新、任务提交及同哈希切分支使快照失效 |
| 异常历史 | 初始仓库、detached HEAD、无共同祖先、多个共同祖先、目标删除均有明确结果 |
| 仓库归属与参数 | 校验 common directory，选项形式输入被拒绝；Git 参数通过 `ArgumentList` 传入 |

每个比较进程有 10 秒上限，取消／超时会终止子进程。文件列表与统计使用 NUL 分隔输出，内容读取固定对象哈希，关闭外部 diff 和 textconv。任务独有提交总数单独统计，列表最多显示最近 500 条。

首次运行中，测试远端缺少 `remote.*.fetch`，Git 未报告预期上游；补齐夹具的实际远端配置后 75 项检查通过。

命令依据：[git diff](https://git-scm.com/docs/git-diff)、[git merge-base](https://git-scm.com/docs/git-merge-base)、[git rev-parse](https://git-scm.com/docs/git-rev-parse)。

2.3a 读取基础提交：`496fca47`。2.3b 页面与探针结果见上。接续批次 2.4：配置来源、多值和常用设置编辑。
