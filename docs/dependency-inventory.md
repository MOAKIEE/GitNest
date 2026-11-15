# 构建依赖清单

来源：`.NET 10`（`global.json` 指定 SDK `10.0.404`）下执行自包含 Release 发布（`-p:DisableAOT=true`）所解析的 NuGet 依赖及子模块声明。源码子模块 `AvaloniaEdit` 固定在 `ddf00a91dc0da29b85c79b85c9583860213a99ff`（MIT）；嵌入字体与语法资源详见 [THIRD-PARTY-LICENSES.md](../THIRD-PARTY-LICENSES.md)。

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
| BitMiracle.LibTiff.NET | 2.4.660 | 见包内许可 / 上游许可清单 |
| CommunityToolkit.Mvvm | 8.4.2 | MIT |
| HarfBuzzSharp | 8.3.1.1 | MIT |
| HarfBuzzSharp.NativeAssets.Linux | 8.3.1.1 | MIT |
| HarfBuzzSharp.NativeAssets.macOS | 8.3.1.1 | MIT |
| HarfBuzzSharp.NativeAssets.WebAssembly | 8.3.1.1 | MIT |
| HarfBuzzSharp.NativeAssets.Win32 | 8.3.1.1 | MIT |
| MicroCom.Runtime | 0.11.0 | MIT |
| Onigwrap | 1.0.11 | MIT |
| Pfim | 0.11.4 | 见包内许可 / 上游许可清单 |
| SkiaSharp | 2.88.9 | MIT |
| SkiaSharp.NativeAssets.Linux | 2.88.9 | MIT |
| SkiaSharp.NativeAssets.macOS | 2.88.9 | MIT |
| SkiaSharp.NativeAssets.WebAssembly | 2.88.9 | MIT |
| SkiaSharp.NativeAssets.Win32 | 2.88.9 | MIT |
| StbImageSharp | 2.30.16 | Unlicense OR MIT |
| TextMateSharp | 2.0.4 | MIT |
| TextMateSharp.Grammars | 2.0.4 | MIT |
| Tmds.DBus.Protocol | 0.21.3 | MIT |

共 32 个 NuGet 包，内容哈希快照见 [dependency-snapshot.json](dependency-snapshot.json)。
