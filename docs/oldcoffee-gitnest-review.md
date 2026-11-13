# OldCoffee/GitNest 参考评估

日期：2026-11-13

## 结论

继续以 SourceGit 为开发底座。OldCoffee/GitNest 可提供局部交互、刷新和验证方法的参考，目前没有需要替换底座或直接引入代码的理由。

本项目名称已确定为 GitNest，目录为 `C:\Project\GitNest`。OldCoffee/GitNest 是另一个同名项目；后续仓库介绍、发行包和关于页面使用自己的作者信息，准确标注基于 SourceGit，避免与该项目混淆。

## 核对范围

读取 [OldCoffee/GitNest](https://github.com/OldCoffee/GitNest) 的 README、许可证、依赖、设计规范，以及下列实现。核对提交为 `351921ba39f339c2104c95c0e5455e41fa16a357`。本次为源码和文档评估，未安装运行或验证其 Windows 发布包。

该项目使用 Rust、Tauri 2 和 React，Git 核心调用 Git CLI。除常用 Git 操作外，还包括文件编辑、预览、多会话终端及可选 Java LSP，整体偏向 IDE 工作台。其 LICENSE 为 MIT，署名 OldCoffee；若后续实际采用代码或资产，应保留对应许可与署名。本次仅记录设计参考，未复制实现或视觉资产。

## 值得参考的做法

| 做法 | 证据 | 本项目如何采用 |
| --- | --- | --- |
| 按仓库和状态类别刷新 | `queryInvalidation.ts` 按 rootPath 限定部分事件刷新，区分 Git 状态、目录、日志、stash 与 worktree | 在 SourceGit 刷新机制上补齐按工作树失效和操作类型刷新，减少 AI 连续写文件时的重复读取 |
| 汇总文件事件 | `watcher.rs` 按根目录汇总路径、去抖，并携带 generation | 借鉴事件合并与代次标识；继续执行读取结果版本校验，过期结果不得覆盖当前状态 |
| 紧凑工作树信息 | `WorktreesTab.tsx` 显示路径、分支和短哈希 | 沿用 SourceGit 工作树组件，补必要状态；路径和分支清楚展示，不堆叠大量卡片 |
| 统一视觉基础 | `design-system/gitnest/MASTER.md` 统一明暗语义色、按钮、列表、焦点和状态组件 | 采用统一样式的原则，基于 Avalonia 资源实现；不照搬其品牌色、图标或高密度 IDE 布局 |
| UI 与真实 Git 结果分别验证 | `e2e/README.md` 区分 mock UI 流程和实际桌面 IPC 流程，并用磁盘上的 git log 校验提交结果 | 将界面测试与真实临时仓库验证结合，关键操作检查提交、引用、索引和文件的实际结果 |

## 不直接沿用的细节

- IDE 扩展：编辑器、Java LSP、PR/MR 工作台和多终端不纳入本项目首版，快捷打开已有外部工具即可。
- 字号密度：其设计规范偏高密度、较小字号；本项目面向初学者，优先清晰阅读和点击操作。
- 工作树删除：已读取的 `WorktreesTab` 中删除按钮直接调用非强制 remove，没有本项目需要的影响预览与联动步骤；后端工作树解析也未在该文件中保留锁定原因等字段。继续采用本项目既定检查流程。
- 文件监听：已读取的 watcher 过滤 `.git` 和 `.gitignore`，不能直接作为本项目外部 Agent 状态检测方案。本项目需要覆盖 HEAD、索引、引用和忽略规则变化，并定位关联工作树的 gitdir/common directory。其他模块是否提供补偿不在本次核对结论内。
- 确认策略：`useDiscardConfirm` 允许通过设置跳过丢弃确认；本项目对实际删除／覆盖文件继续展示具体影响和恢复条件。
- 技术栈：其 React 与 Rust 实现不能直接放入 C#／Avalonia 项目，参考流程即可，避免额外维护两套 Git 执行与状态模型。

## 对本项目设计的落实

1. 在自动刷新部分明确仓库范围、事件合并、代次校验及 Git 元数据监听。
2. 在验收部分要求关键 GUI 操作以实际 Git 状态验证，不能只用 mock 成功作为交付依据。
3. SourceGit 原有分支树、工作树和差异组件继续优先复用。

## 已读取的源码与文档

- [许可证](https://github.com/OldCoffee/GitNest/blob/351921ba39f339c2104c95c0e5455e41fa16a357/LICENSE)
- [依赖与构建脚本](https://github.com/OldCoffee/GitNest/blob/351921ba39f339c2104c95c0e5455e41fa16a357/package.json)
- [设计规范](https://github.com/OldCoffee/GitNest/blob/351921ba39f339c2104c95c0e5455e41fa16a357/design-system/gitnest/MASTER.md)
- [按仓库失效刷新](https://github.com/OldCoffee/GitNest/blob/351921ba39f339c2104c95c0e5455e41fa16a357/src/lib/queryInvalidation.ts)
- [文件监听](https://github.com/OldCoffee/GitNest/blob/351921ba39f339c2104c95c0e5455e41fa16a357/src-tauri/src/watcher.rs)
- [工作树界面](https://github.com/OldCoffee/GitNest/blob/351921ba39f339c2104c95c0e5455e41fa16a357/src/components/commit/WorktreesTab.tsx)
- [工作树命令](https://github.com/OldCoffee/GitNest/blob/351921ba39f339c2104c95c0e5455e41fa16a357/crates/rebased-core/src/worktree.rs)
- [丢弃确认](https://github.com/OldCoffee/GitNest/blob/351921ba39f339c2104c95c0e5455e41fa16a357/src/hooks/useDiscardConfirm.ts)
- [端到端验证说明](https://github.com/OldCoffee/GitNest/blob/351921ba39f339c2104c95c0e5455e41fa16a357/e2e/README.md)
