# 上游基线与依赖说明

## 固定基线

| 项目 | 值 |
| --- | --- |
| 上游项目 | [sourcegit-scm/sourcegit](https://github.com/sourcegit-scm/sourcegit) |
| 上游稳定版本 | [v2026.24](https://github.com/sourcegit-scm/sourcegit/releases/tag/v2026.24)（`2735a8ad80c2443bc736b7e6e501f1052b61d940`） |
| AvaloniaEdit 子模块 | [love-linger/AvaloniaEdit](https://github.com/love-linger/AvaloniaEdit)（`ddf00a91dc0da29b85c79b85c9583860213a99ff`） |
| 目标框架 | `net10.0`（SDK 版本见 [global.json](../global.json)） |
| UI 框架 | Avalonia `11.3.22`、Avalonia.Controls.DataGrid `11.3.13` |
| 文本高亮 | TextMateSharp `2.0.4` |
| 开源许可 | SourceGit MIT、AvaloniaEdit MIT；完整清单见 [dependency-inventory.md](dependency-inventory.md) |

## 构建与发布约定

- 克隆源码时需同步初始化子模块（`git submodule update --init --recursive`）。
- 发行包采用自包含（self-contained）非 Native AOT 模式构建（`DisableAOT=true`），不捆绑 Git 二进制文件，且随包附带 `LICENSE`、`NOTICE.md`、`THIRD-PARTY-LICENSES.md` 与 `licenses/AvaloniaEdit-LICENSE.txt`。
