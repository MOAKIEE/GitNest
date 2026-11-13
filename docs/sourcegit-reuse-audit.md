# SourceGit 源码复用核对

核对日期：2026-11-12  
核对版本：[2735a8ad80c2443bc736b7e6e501f1052b61d940](https://github.com/sourcegit-scm/sourcegit/tree/2735a8ad80c2443bc736b7e6e501f1052b61d940)（当日 master）  
核对方式：读取该提交的命令、ViewModel 和视图源码；尚未运行上游构建与交互测试。实施选定稳定基线后复核差异。

## 改造原则

已有能力优先保留，中文命名、主题和入口调整归为界面改造；只有缺失的数据和流程才新增实现。下面的“补充”是相对已核对路径的差距，不表示已排查上游全部代码。

## 能力与改造清单

| 能力 | 已核对的上游实现 | GitNest 处理 |
| --- | --- | --- |
| 本地／远程分支显示 | `Views/Repository.axaml` 已有 `LocalBranchTree`、`RemoteBranchTree`；`Repository` 分别提供两组树数据 | 直接沿用分组、组件和筛选，不另写独立列表 |
| 删除分支及联动 | `ViewModels/DeleteBranch.cs` 已区分本地与远端，支持 `DeleteUpstream` 和工作树相关选项 | 保留独立删除入口与可选联动，保留并明确工作树删除选项；补齐预期 tip、恢复与分步失败展示 |
| 工作树清理 | `Commands/Worktree.cs` 已有 list、remove、prune、lock／unlock；`ViewModels/RemoveWorktree.cs` 已有默认关闭的 Force 选项与执行结果返回 | 保留列表与删除，补目标目录检查、未提交／忽略文件提示、detached HEAD 恢复引用及联动分步结果 |
| stash | `Commands/Stash.cs` 已有 push、apply、pop、drop；`ApplyStash` 在 apply 成功后才按选项 drop | 复用保存、列表与恢复页面，改成易懂文案；补充干净状态检查和恢复冲突提示 |
| 丢弃修改 | `Commands/Discard.cs` 支持全部及选定变更，包含 clean、hard reset 及直接删除文件的路径 | 复用界面和受控恢复能力；不直接沿用永久删除路径，增加内容备份与新文件回收 |
| 忽略规则 | `ViewModels/AddToIgnore.cs` 生成／追加规则，`Models/GitIgnoreFile.cs` 提供存储候选 | 复用文件选择和写入流程，补预设、去重、匹配预览及已跟踪提示 |
| 保留历史的撤销 | `Commands/Revert.cs`、`ViewModels/Revert.cs` 已有命令与确认入口 | 复用页面和执行机制，补成功判断及冲突取消；普通提交首版不携带合并主线选择 |
| 仓库配置读取 | `Commands/Config.cs` 的 `ReadAll`／`ReadAllAsync` 在仓库目录执行 `config -l` | 复用入口，扩展来源、scope、包含文件、多值及可靠输出解析 |
| 常用配置编辑 | `RepositoryConfigure` 已读写姓名、邮箱、签名、代理、fetch prune；`Config.SetAsync` 根据上下文写 local 或 global | 简化页面并补作用域选择、继承／覆盖、外部变动检测及保存后验证 |
| 状态基础数据 | `Repository.cs` 已有修改刷新、分支刷新、stash 数量和 ahead／behind 使用 | 复用数据和刷新生命周期，新增面向用户的摘要及下一步提示 |
| Git 探测 | `Native/Windows.cs` 已有 `FindGitExecutable` | 复用探测，补首次引导、版本校验、路径选择与重新检测 |
| 历史文件导出 | `CommitDetail.SaveRevisionFileAsync` 调用 `Commands.SaveRevisionFile` | 后续开放原有入口，保持原实现，不重写导出 |
| 推送与远端删除 | `Commands/Push.cs` 已构建明确分支 refspec，强推使用裸 `--force-with-lease`，删除使用 `--delete` | 复用执行通道，补显式分支与预期 OID 的 lease；简化标签、跳过 hooks 等选项 |

## 已发现的具体差异

### 1. 配置读取不是从零实现

`RepositoryConfigure` 创建时即调用 `Commands.Config(repo.FullPath).ReadAll()`，已有配置会被读取。`SaveAsync` 比较缓存后写变化项，默认是仓库 local 范围。

现有读取返回单值字典：重复键覆盖早先值，按换行拆分，也不携带来源和作用域。为准确显示条件包含、继承和多值配置，应扩展现有模型与解析，不新建平行配置服务。写配置仍走 Git，保留未编辑项。

### 2. 分支显示已有分组，联动删除保留

上游已有本地和远程树，符合用户最新选择。独立删除入口基于当前对象类型区分，联动作为本地删除弹窗选项保留，默认关闭。

上游联动显示条件包含“上游分支与本地同名”。GitNest 根据实际 upstream 映射展示，异名时明确列出完整远端分支，避免使用者误认为同名就是关联。

上游删除流程可能先完成本地再删除远端，因此需要新增分步结果和本地恢复入口；不把一次弹窗确认理解为两侧原子操作。

### 3. 强推已有 lease，需补充明确的预期值

上游强推并非裸 `--force`，已有 `--force-with-lease`。GitNest 增加确认时固定的远端 OID，使用 `--force-with-lease=<ref>:<expected>`，避免后台 fetch 改变隐式预期。远端删除也采用显式预期值约束。

### 4. 丢弃实现需要按产品语义收窄

上游 `Discard.AllAsync` 能清理未跟踪／忽略文件；`ChangesAsync` 能直接执行文件和目录删除，并以 restore 处理其他文件。GitNest 的“新文件移至回收站”和“覆盖前保留可恢复内容”需要适配，不能仅改按钮文字后直接调用原永久删除流程。

### 5. revert 已有，但不能照搬所有行为

上游命令构造使用固定 `-m 1`，允许自动提交；对应 ViewModel 调用执行后结束日志并返回。GitNest 首版仅开放普通提交撤销，明确检查实际执行结果，冲突时取消本程序发起的操作，并区分钩子／签名失败。

### 6. 恢复 stash 沿用成功后删除的逻辑

上游 `ApplyStash` 已实现 apply 成功后可选 drop，直接保留。需要补充的主要是用户文案、前置状态检查和失败结果；stash 冲突不能沿用 merge abort。

### 7. 工作树删除直接复用现有能力

新增读取核对 `Commands/Worktree.cs` 与 `ViewModels/RemoveWorktree.cs`。上游通过 `worktree list --porcelain` 列出工作树、通过 `worktree remove`（可选 `-f`）删除，并已有 prune、lock、unlock 命令。`RemoveWorktree` 返回命令执行结果；`DeleteBranch` 也已有先删除关联工作树再删除分支的流程。

保留这些入口与实现，补齐 GitNest 的路径、主工作树、忽略文件和联动失败检查。上游 `PruneAsync` 使用 `worktree prune -v`；失效记录清理需补 dry-run 预览。列表模型若未保留锁定原因和 prunable 信息，应扩展解析字段，不重写工作树管理器。

## 新增工作的实际范围

- 状态摘要、工作树任务概览与首次使用的引导编排。
- 任务累计差异入口及现有删除流程的任务清理编排。
- 配置来源、多值和作用域模型，以及保存后有效值反馈。
- 改写远端历史和远端删除的显式预期 OID。
- 本地恢复引用、丢弃前内容备份与分步失败恢复。
- 外部 AI 修改后的刷新、确认失效，以及合并／revert 取消前后的状态核验。
- 简化界面、文案、主题、安装包与验收用例。

合并预览应先接入上游已有历史和 revision compare 能力，复用其数据与差异组件；实施时核对具体调用入口后再接入。历史图、diff 渲染、stash 引擎、Git 配置写入和分支树不另行开发。

## AI 编程流程的复用约束

- 暂存区：此前已读取 `ViewModels/WorkingCopy.cs`，其数据区分工作区与索引状态。保留此模型与原有暂存操作，部分暂存不能通过自动暂存整个文件被覆盖。具体文件级操作调用在实施接入时复核。
- 任务概览：复用已核对的工作树列表和仓库刷新数据；新增本地别名、比较目标与跨工作树状态摘要，不增加 Agent 运行管理。
- 累计差异：`CommitDetail` 已使用 `Commands.CompareRevisions`；复用差异渲染，新增明确的比较基准选择和累计变化入口。共同祖先选择及分支比较入口的具体实现仍需在稳定基线核对后接入。
- 快捷打开：此前读取的 `Native/Windows.cs` 包含外部终端启动处理；资源管理器、编辑器和路径复制的具体 UI 调用需在实施时补查，优先复用，不在本次核对中认定全部入口已验证。
- 自动刷新：`Repository` 已有分支、工作区、工作树刷新方法和取消过期读取的机制；监听覆盖范围及跨工作树失效规则在实施时验证，按缺口补强。
- 任务清理：组合已核对的 `RemoveWorktree`、`DeleteBranch` 与 `Push`，保持各步骤独立结果。祖先检查只说明提交关系，压缩合并后需要呈现差异，不自动判定代码丢失。

## 源码入口

以下路径均固定到本次核对提交：

- [仓库视图](https://github.com/sourcegit-scm/sourcegit/blob/2735a8ad80c2443bc736b7e6e501f1052b61d940/src/Views/Repository.axaml)
- [仓库数据与刷新](https://github.com/sourcegit-scm/sourcegit/blob/2735a8ad80c2443bc736b7e6e501f1052b61d940/src/ViewModels/Repository.cs)
- [分支删除](https://github.com/sourcegit-scm/sourcegit/blob/2735a8ad80c2443bc736b7e6e501f1052b61d940/src/ViewModels/DeleteBranch.cs)
- [配置读写](https://github.com/sourcegit-scm/sourcegit/blob/2735a8ad80c2443bc736b7e6e501f1052b61d940/src/Commands/Config.cs)
- [仓库设置](https://github.com/sourcegit-scm/sourcegit/blob/2735a8ad80c2443bc736b7e6e501f1052b61d940/src/ViewModels/RepositoryConfigure.cs)
- [stash 命令](https://github.com/sourcegit-scm/sourcegit/blob/2735a8ad80c2443bc736b7e6e501f1052b61d940/src/Commands/Stash.cs)
- [stash 恢复](https://github.com/sourcegit-scm/sourcegit/blob/2735a8ad80c2443bc736b7e6e501f1052b61d940/src/ViewModels/ApplyStash.cs)
- [丢弃修改](https://github.com/sourcegit-scm/sourcegit/blob/2735a8ad80c2443bc736b7e6e501f1052b61d940/src/Commands/Discard.cs)
- [忽略规则](https://github.com/sourcegit-scm/sourcegit/blob/2735a8ad80c2443bc736b7e6e501f1052b61d940/src/ViewModels/AddToIgnore.cs)
- [revert 命令](https://github.com/sourcegit-scm/sourcegit/blob/2735a8ad80c2443bc736b7e6e501f1052b61d940/src/Commands/Revert.cs)
- [revert 流程](https://github.com/sourcegit-scm/sourcegit/blob/2735a8ad80c2443bc736b7e6e501f1052b61d940/src/ViewModels/Revert.cs)
- [推送命令](https://github.com/sourcegit-scm/sourcegit/blob/2735a8ad80c2443bc736b7e6e501f1052b61d940/src/Commands/Push.cs)
- [历史文件导出](https://github.com/sourcegit-scm/sourcegit/blob/2735a8ad80c2443bc736b7e6e501f1052b61d940/src/ViewModels/CommitDetail.cs)
- [Windows Git 检测](https://github.com/sourcegit-scm/sourcegit/blob/2735a8ad80c2443bc736b7e6e501f1052b61d940/src/Native/Windows.cs)

- [工作树命令](https://github.com/sourcegit-scm/sourcegit/blob/2735a8ad80c2443bc736b7e6e501f1052b61d940/src/Commands/Worktree.cs)
- [工作树删除流程](https://github.com/sourcegit-scm/sourcegit/blob/2735a8ad80c2443bc736b7e6e501f1052b61d940/src/ViewModels/RemoveWorktree.cs)
