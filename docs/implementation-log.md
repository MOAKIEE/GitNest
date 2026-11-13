# GitNest 实施记录

## 2026-11-13 · 批次 1.1：建立实施基线

- 工作区初始内容为 `design.md`、`sourcegit-reuse-audit.md`、`oldcoffee-gitnest-review.md` 三份文档，尚无源码和 Git 仓库。
- 根据设计第 10 节拆分四阶段和可独立验证的提交批次，建立 [实施计划](implementation-plan.md)。
- 检查环境：Windows x64，Git `2.45.1.windows.1`，已安装 .NET SDK `8.0.201`，另有 .NET 10 运行时但没有 .NET 10 SDK。
- 上游最新稳定发行版为 `v2026.24`，目标框架为 `net10.0`，Avalonia 主版本依赖为 `11.3.22`。下一批固定完整提交和子模块版本，在工作区安装隔离的 .NET 10 SDK 后验证构建。
- Git 提交使用本机已有身份；按批次提交到本地仓库。

验证：已读取三份原始文档，检查工作区文件、Git 版本、SDK 列表与现有提交身份；上游版本及项目配置通过 GitHub 官方仓库核对。

## 2026-11-13 · 批次 1.2：导入并构建上游

- 前一批提交：`5549094`（建立实施计划与原始文档基线）。
- 从官方仓库导入稳定版 `v2026.24`，完整提交 `2735a8ad80c2443bc736b7e6e501f1052b61d940`；初始化固定的 AvaloniaEdit 子模块 `ddf00a91dc0da29b85c79b85c9583860213a99ff`。
- 将 .NET SDK `10.0.404` 安装至工作区 `.tools/dotnet/`，核对官方 SHA-512；系统 PATH 保持原值。
- `dot.net` 安装脚本下载遇到 TLS EOF，改用微软发行元数据中的 SDK ZIP 地址并校验后解压，工具准备完成。
- 对未修改的应用源码执行 `dotnet build SourceGit.slnx -c Release --nologo`，构建成功，0 个警告、0 个错误。
- 记录 [固定上游与构建基线](upstream-baseline.md)、49 个 NuGet 包的实际解析版本及内容哈希；保留上游和子模块许可证。
- 确认上游许可清单有部分版本过时，下一批按依赖快照更新。

接续：批次 1.3 完成 GitNest 品牌、独立配置、可复现构建脚本、自包含发布及桌面启动验证。
