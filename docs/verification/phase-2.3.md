# 批次 2.3 验证记录

环境：Windows x64、Git `2.45.1.windows.1`、.NET SDK `10.0.404`。开始日期：2026-11-13。

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

接续：2.3b 接入显式目标选择、累计差异页面、快照失效提示，并以界面探针截图验收。
