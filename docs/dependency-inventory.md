# 构建依赖清单

日期：2026-11-15（阶段四批次 4.1 依赖与入口裁剪后更新）。来源：Windows x64、.NET SDK `10.0.404` 下执行 Release 发布构建（`.\build\scripts\build-gitnest.ps1 -Publish`，`-p:DisableAOT=true`）后的 `src/obj/project.assets.json` 与本机 NuGet 包内许可声明。

说明：批次 4.1 移除 `Azure.AI.OpenAI` 与 `OpenAI` 直接依赖后，发布集合已同步剔除 `Azure.AI.OpenAI`、`OpenAI`、`Azure.Core`、`System.ClientModel`、`System.Memory.Data`、`Microsoft.Bcl.AsyncInterfaces` 等传递依赖（且未启用 AOT 发布的 `win-x64` 构建不再包含 AOT 编译器及 `Microsoft.Extensions.*` 构建期传递包）。源码子模块 AvaloniaEdit 仍固定在 `ddf00a91dc0da29b85c79b85c9583860213a99ff`（MIT）；嵌入字体和语法资源见根目录 `THIRD-PARTY-LICENSES.md`。

| NuGet 包 | 解析版本 | 包声明许可 |
| --- | --- | --- |
| Avalonia | 11.3.22 | MIT |
| Avalonia.Angle.Windows.Natives | 2.1.25547.20250602 | 包内文件：LICENSE |
| Avalonia.BuildServices | 11.3.2 | MIT |
| Avalonia.Controls.DataGrid | 11.3.13 | MIT |
| Avalonia.Desktop | 11.3.22 | MIT |
| Avalonia.Fonts.Inter | 11.3.22 | MIT |
| Avalonia.FreeDesktop | 11.3.22 | MIT |
| Avalonia.Native | 11.3.22 | MIT |
| Avalonia.Remote.Protocol | 11.3.22 | MIT |
| Avalonia.Skia | 11.3.22 | MIT |
| Avalonia.Themes.Fluent | 11.3.22 | MIT |
| Avalonia.Win32 | 11.3.22 | MIT |
| Avalonia.X11 | 11.3.22 | MIT |
| BitMiracle.LibTiff.NET | 2.4.660 | 见包内许可／上游许可清单 |
| CommunityToolkit.Mvvm | 8.4.2 | MIT |
| HarfBuzzSharp | 8.3.1.1 | MIT |
| HarfBuzzSharp.NativeAssets.Linux | 8.3.1.1 | MIT |
| HarfBuzzSharp.NativeAssets.macOS | 8.3.1.1 | MIT |
| HarfBuzzSharp.NativeAssets.WebAssembly | 8.3.1.1 | MIT |
| HarfBuzzSharp.NativeAssets.Win32 | 8.3.1.1 | MIT |
| MicroCom.Runtime | 0.11.0 | MIT |
| Onigwrap | 1.0.11 | MIT |
| Pfim | 0.11.4 | 见包内许可／上游许可清单 |
| SkiaSharp | 2.88.9 | MIT |
| SkiaSharp.NativeAssets.Linux | 2.88.9 | MIT |
| SkiaSharp.NativeAssets.macOS | 2.88.9 | MIT |
| SkiaSharp.NativeAssets.WebAssembly | 2.88.9 | MIT |
| SkiaSharp.NativeAssets.Win32 | 2.88.9 | MIT |
| StbImageSharp | 2.30.16 | Unlicense OR MIT |
| TextMateSharp | 2.0.4 | MIT |
| TextMateSharp.Grammars | 2.0.4 | MIT |
| Tmds.DBus.Protocol | 0.21.3 | MIT |

共 32 个 NuGet 包。完整内容哈希见 [依赖快照](dependency-snapshot.json)。

实际发布时的包集合以对应发布输出为准；阶段四随安装包收录依赖许可正文。
