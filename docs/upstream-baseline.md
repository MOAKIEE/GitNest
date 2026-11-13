# SourceGit 构建基线

## 固定来源

| 项目 | 固定值 |
| --- | --- |
| 上游 | https://github.com/sourcegit-scm/sourcegit |
| 稳定发行版 | [v2026.24](https://github.com/sourcegit-scm/sourcegit/releases/tag/v2026.24)，2026-11-09 发布 |
| 完整提交 | `2735a8ad80c2443bc736b7e6e501f1052b61d940` |
| AvaloniaEdit 子模块 | `https://github.com/love-linger/AvaloniaEdit.git` |
| 子模块提交 | `ddf00a91dc0da29b85c79b85c9583860213a99ff` |
| 目标框架 | `net10.0` |
| 构建 SDK | `10.0.404`，Windows x64 |
| Avalonia | `11.3.22`；DataGrid `11.3.13` |
| 子模块框架依赖 | Avalonia `11.3.18`；主项目最终解析 `11.3.22` |
| TextMateSharp | `2.0.4` |
| 许可 | SourceGit MIT、AvaloniaEdit MIT；[完整依赖清单](dependency-inventory.md) |

通过本地 Git 合并导入上游，保留固定版本作为提交父节点，原始设计文档仍在同一仓库中。`upstream` 远端只用于读取底座来源。

## 环境与复现

本次环境为 Windows x64（系统版本 `10.0.26300`）、Git `2.45.1.windows.1`。.NET SDK 安装在工作区 `.tools/dotnet/`，没有修改系统 PATH。

SDK 来自微软官方 [10.0 发行元数据](https://builds.dotnet.microsoft.com/dotnet/release-metadata/10.0/releases.json)中的 `10.0.404` Windows x64 ZIP。解压前核对 SHA-512：

```text
330ddd2665b43cd2e428216a0d8ea1ac7bb19b5434fdc530e62010dc3b52823a620bf95489d628e1ed6f5122401793ee4887cca4a019122361a1231497f632ee
```

在批次 1.2 的导入状态执行：

```powershell
git submodule update --init --recursive --depth=1
& ./.tools/dotnet/dotnet.exe build SourceGit.slnx -c Release --nologo
```

结果：Release 构建成功，**0 个警告、0 个错误**，输出 `src/bin/Release/net10.0/SourceGit.dll`。完整日志保存在本地 `artifacts/logs/1.2-upstream-build.log`。

上游构建的版本元数据使用 `git describe`；合并提交建立之前，原文档提交不含上游标签，该命令输出无法描述提交的提示，MSBuild 按上游的 `IgnoreExitCode=true` 继续完成构建。GitNest 品牌批次将改用自己的版本元数据。

## 许可与版本核对

- 保留根目录 `LICENSE`、`THIRD-PARTY-LICENSES.md` 和子模块 `LICENSE`。
- 上游许可清单中的 Avalonia、DataGrid、AvaloniaEdit、TextMateSharp、StbImageSharp 版本与当前源码不一致；GitNest 后续清单按本次实际还原结果校正，并补充 CommunityToolkit.Mvvm。
- 本批次未改动上游应用源码；NuGet 的实际解析版本与内容哈希已记录在 [依赖快照](dependency-snapshot.json)。

## Windows 支持与发布方式

阶段一验证当前 Windows x64 环境。Windows 10/11 和高 DPI 验收仍按设计安排在阶段四。

GitNest 基线采用自包含的普通 .NET Windows x64 发布，避免要求开发机安装 Native AOT 所需的 C++ 工具链。用户运行发布目录中的程序无需另装 .NET；Git for Windows 由用户独立安装。安装器方案在阶段四确定。
