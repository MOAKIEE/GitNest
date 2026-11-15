# GitNest 实施记录

## 2026-11-14 · 批次 2.4b：配置来源 UI 与常用设置编辑

- 前一批提交：`e630a315`（批次 2.4a 配置来源、多值读取与脱敏基础）。
- 抽出 `Commands.Command.RunGitDirectAsync` 与 `RedactResult` 消除 `Commands.Config` 重复逻辑；新增 `Commands.Remote` 定向 URL／增删改操作（精准替换主获取／推送 URL 并保留附加 URL 与 refspec）及 `Commands.Branch` 定向设定／取消上游命令（兼容未 fetch 引用与 unborn 分支）。
- 新增配置表单模型（`src/ViewModels/ConfigureModels.cs`）并升级 `RepositoryConfigure`，按 `extensions.worktreeConfig` 控制工作树作用域显示，区分继承、`为当前层覆盖`、`设为空值 ""`、`移除本层` 与高优先级覆盖警告；提供保存前外部变更冲突检测、部分失败逐项汇报及密码／令牌全程脱敏。
- 对照 SourceGit 原生样式重构 `RepositoryConfigure.axaml` 与 `ConfigFieldMetaBar.axaml`：取消重边框卡片改为两列右对齐扁平表单与 `group_header_label` 分割线，高级设置采用原生 `ToggleButton.group_expander` 平铺面板；修复探针截图暴露出的窄窗横幅裁切、诊断框水平滚动条压字、左列标签折行与高优先级警告标题重复打印问题，双语词条对齐 1254 项。
- 验证：Release 与 `-Publish` 构建 0 警告 0 错误（`artifacts/windows-x64/GitNest.exe`）；120 项集成检查全过；13 项 `-Config` UI 探针通过且 6 张浅色／深色／窄窗／工作树截图目视核对正常。详见 [2.4 验证记录](verification/phase-2.4.md)。

接续：2.5 提交快照、获取、快进拉取、单分支普通推送（本批完成后按用户要求暂停）。

## 2026-11-13 · 批次 2.4a：配置来源、多值读取与脱敏基础

- 前一批提交：`5cb3806e`。新增 `GitConfigSnapshot` 等配置模型，扩展 `Commands.Config` 的 `ReadSnapshotAsync`、`SetScopedAsync` 与 `InspectSigningStatus`。
- 直接以 `ArgumentList` 调用 `git config --list --includes --show-origin --show-scope --null`（不混入 UI 内部 `-c` 参数），保留系统、全局、仓库、工作树、`include`/`includeIf` 与外部环境变量运行时来源，并结合 `rev-parse` 修正子目录打开与裸仓库下的配置文件定位。
- 完整保留 `credential.helper` 多值顺序及空值重置、`remote.*` 多地址与 refspec、`http.<url>.proxy` URL 覆盖及无值布尔键；对 `http.extraheader`、敏感键和 URL 凭据做诊断脱敏。
- 作用域写入区分显式空值覆盖与 `--unset` 恢复继承；未启用 `extensions.worktreeConfig` 时拒绝写入 `--worktree`，拒绝覆盖已有本地多值项；签名检查识别缺失密钥、缺失程序及 SSH 签名的 Git `2.34.0` 门槛。
- Release 构建 0 警告、0 错误；102 项集成检查全部通过（新增 27 项配置检查）。详见 [2.4 验证记录](verification/phase-2.4.md)。

接续：2.4b 重构常用设置编辑界面（来源、多值、远端、当前分支上游、代理、签名、高级换行与作用域保存）及 UI 探针截图核对。

## 2026-11-13 · 批次 2.3b：累计差异页面与快照失效

- 前一批提交：`496fca47`。新增累计差异导航及工作树菜单入口，显式选择完整本地／远程引用，按工作树保存比较目标。
- 默认审阅共同祖先到任务 HEAD 的成果，提供两端版本比较；展示双方哈希、基准、文件数、文本行数、二进制数和任务独有提交，复用既有文件与差异控件。未提交修改单独统计并可打开对应工作树。
- 当前活动页面每两秒核对快照，外部引用或工作树身份变化后显示过期提示，刷新后读取新快照；取消旧代次读取并丢弃关闭后的结果。
- 修复探针发现的目标重载空值反写、过期提示主题资源和未选目标时状态停在读取态；清理探针中的重复关闭路径。
- 75 项集成检查、30 项交互探针通过，11 张浅色／深色及窄窗口截图核对完成；Release 构建 0 警告、0 错误，Windows x64 自包含发布成功。详见 [2.3 验证记录](verification/phase-2.3.md)。临时夹具及失败运行残留均已移至回收站。

接续：2.4 配置来源、多值与常用设置编辑。2.2 的原生文件选择器、资源管理器窗口及完整进程重启验收继续保留在原记录清单。

## 2026-11-13 · 批次 2.3a：固定快照的累计差异读取

- 前一批提交：`e6e06627`。增加只读累计比较查询，显式选择完整本地／远端引用，固定任务 HEAD、目标 HEAD 与基准哈希。
- 默认使用唯一共同祖先；两端模式直接比较目标版本到任务版本。无共同祖先和多个共同祖先独立报告，detached HEAD 可比较，初始仓库提示尚无提交。
- 使用 NUL 分隔输出保留重命名与特殊路径，汇总文本增删行及二进制文件数，任务独有提交列表最多 500 条并保留总数；未提交状态单独读取。
- 核对仓库身份、源分支与双方哈希，供后续页面标记过期。75 项真实 Git／进程集成检查通过，详细结果见 [2.3 验证记录](verification/phase-2.3.md)。

接续：2.3b 页面、目标存储、刷新失效与探针截图。

## 2026-11-13 · 批次 2.2e：交互探针与截图记录

- 前一批提交：`4c5f6a43`。新增 `GitNest.UIProbe` 和 `probe-gitnest.ps1`，用生产控件、绑定、双击路由事件及真实 Git 夹具补齐可自动重复的交互检查。
- 11 项探针检查通过，涵盖别名保存与重载、完成标记、查看修改、工作树打开、外部文件／索引监听、全工作树刷新以及编辑器／终端工作目录；53 项既有集成检查通过。
- 浅色、深色、窄窗口及编辑页面截图和结果保存于 `artifacts/probes/phase-2.2/`，复现命令与验收边界见 [2.2 验证记录](verification/phase-2.2.md)。临时夹具完成后移至回收站。

接续：2.3 累计差异读取基础与页面接入分批提交。原生文件选择器、资源管理器和完整进程重启留在桌面验收清单。

## 2026-11-13 · 批次 2.2d：按 SourceGit 原生风格优化界面

- 前一批提交：`f9459aa7`。对照 SourceGit 原生界面规范统一新增和改造页面的按钮、表单、徽章与列表排版。
- 欢迎页引导卡片（`GitSetup`）：统一使用 `flat` / `flat primary` 按钮，加入状态图标与就绪／版本胶囊；将详情区由 `StackPanel` 改为 `Grid` 布局，修复切换至 Git 缺失态时说明文本单行裁切未换行的问题，并改善深色主题下绿底状态徽章对比度。
- 仓库顶部状态摘要栏：补充分支图标，状态摘要改为单行省略显示（完整明细保留在悬浮提示），右侧“查看修改”改用 `flat` 按钮。
- 工作树任务概览列表：优化 `128px` 项内上下留白（`22,20,18,18,18,18`），统一第 1～5 行 `16px` 缩进，补齐刷新时间省略号与项间底部分隔线；将分支与手动完成状态拆为独立标签（`Worktree` 补充 `IsTaskCompleted` 通知），并将侧栏溢出最小高度钳制由 `120` 修正为 `128`。
- 任务编辑弹窗（`EditWorktreeTask`）：对齐 `EditRepositoryNode`／`AddWorktree` 的两列右对齐表单结构，新增 `Text.Worktree.Task.AliasLabel` 本地化词条，底部状态信息收入圆角边框卡片并补齐换行。

验证：Release 构建 0 警告、0 错误；53 项集成检查全部通过。使用独立 Avalonia 渲染探针加载 `artifacts/smoke/phase-2.2/` 真实夹具，在浅色／深色主题、`1280×900` 与 `1060×700` 下截图核对欢迎页（就绪／缺失态）、仓库主视图、工作树完成徽章与编辑弹窗，布局与对比度正常。详见 [批次 2.2 验证记录](verification/phase-2.2.md)。

接续：完成 2.2 剩余交互验收（别名保存、查看修改、双击打开、快捷工具及文件监听刷新），再进入 2.3。

## 2026-11-13 · 批次 2.2c：修复工作树信息重叠

- 前一批提交：`1a705c66`。用户提供的截图确认上一批局部行高覆盖没有生效，共享侧栏样式仍给工作树容器设置 24 像素行高，多行内容溢出并重叠。
- 为工作树列表增加独立样式类，在共享样式中明确排除普通 24 像素规则并应用 128 像素容器行高，移除原局部覆盖。
- Windows Release 构建 0 警告、0 错误，自包含发布成功；53 项集成检查全部通过。
- 实际桌面在 1280×900 下确认四种工作树逐行显示；1060×700 下确认纵向滚动、末尾条目、完整路径提示与整项选中背景，信息重叠消失。修复后的隔离实例保留打开。

详细结果与日志见 [批次 2.2 验证记录](verification/phase-2.2.md)。2.2 其余交互验收继续按该记录接续。

## 2026-11-13 · 批次 2.2b：任务概览界面与快捷打开

- 前一批提交：`6a838b17`（工作树读取与本机任务存储基础）。
- 仓库主体顶部显示当前分支、完整未提交文件数与同步状态，提供“查看修改”；摘要详情列出暂存／未暂存／未跟踪数量和本地获取记录时间。
- 扩展原工作树区域，只有主工作树时也显示；增加本机别名、手动完成状态、路径、分类数量、缓存同步状态、刷新时间及详细提示。干净状态不会自动标记任务完成。
- 新增别名及完成标记编辑弹窗，存储写入 GitNest 本机配置目录；保存失败反馈原因。比较目标字段随存储保留，目标选择界面接续 2.3。
- 工作树右键新增资源管理器、编辑器、终端及刷新入口，保留复制路径和工作树打开入口；编辑器复用已发现工具，也可选择程序并保存路径，终端缺失时提供程序选择。
- 工具启动使用目标工作树路径，新增编辑器路径通过进程参数列表传递，已发现编辑器额外设置准确工作目录。
- 状态读取限制每个页面最多两个并发进程，过期代次和已关闭页面的结果丢弃；普通文件事件刷新当前工作树，全量刷新由首次发现、刷新按钮、右键入口及 F5 触发，恢复焦点时补读当前状态。
- 补充关联工作树 index、FETCH_HEAD 及配置事件，保持已有去抖机制。任务设置中的非法路径记录按损坏配置处理并保留原文件。

验证：53 项集成检查全部通过；Release 构建 0 警告、0 错误，Windows x64 自包含发布成功。真实工具夹具确认中文／特殊字符目录以单个参数传入且工作目录正确。

桌面首轮确认数据绑定，并发现列表原 24 像素行高造成信息重叠，已设置工作树独立行高并重新发布。用户通过物理 Esc 停止检查，随后要求再次启动，最新隔离实例已重新打开；桌面工具仍返回停止状态，修复后的布局与交互验收留待接续。详细命令、结果及待检查步骤见 [批次 2.2 验证记录](verification/phase-2.2.md)。

接续：先补齐 2.2 桌面验收，再进入 2.3 累计差异、明确比较目标与快照失效。

## 2026-11-13 · 批次 2.2a：工作树状态读取与本机任务存储

- 前一批提交：`864a45a3`（首次推送及 CI 记录）。批次 2.2 分为读取／存储基础和界面／快捷操作两次提交。
- 新增工作树只读状态查询，在目标目录读取 porcelain v2、分支、HEAD、暂存／未暂存／未跟踪／冲突数量。部分暂存文件在两个分类中各计一次，总文件数去重；重命名按一项计算。
- 查询前核对工作目录根和 Git common directory，拒绝被替换为独立 clone 的目录；目录缺失、无上游、上游已删除、未获取、detached HEAD 和初始仓库分别建模。
- 同步数量来自 Git 本地引用；有 FETCH_HEAD 时保留获取时间，没有获取记录时数量保持未知。每个只读进程设 10 秒上限并在取消／超时后终止。
- 工作树列表优先使用 NUL 分隔，保留中文、空格、特殊字符路径和多行锁定原因；旧 Git 不支持 `list -z` 时回退原 porcelain 格式。
- 别名、手动完成标记和比较目标存储基础写入 GitNest 配置目录中的 `worktree-tasks.json`，按 common directory 与工作树绝对路径联合匹配；写入采用临时文件替换，损坏的原设置拒绝覆盖。

验证：`./build/scripts/test-gitnest.ps1` 的 50 项真实进程／Git 集成检查全部通过，包含既有 26 项及新增 24 项；构建 0 警告、0 错误。首次测试因 Git 对象只读属性导致临时目录清理失败，清理逻辑在核对专用临时目录后重置文件属性，重跑成功。日志：`artifacts/logs/2.2-foundation-tests.log`。

依据：[git-status porcelain v2](https://git-scm.com/docs/git-status)、[git-worktree porcelain 格式](https://git-scm.com/docs/git-worktree)。接续 2.2b 将读取结果接入原工作树区域、状态摘要、别名编辑与快捷打开，并做桌面验证。

## 2026-11-13 · 批次 1.1：建立实施基线

- 工作区初始内容为 `design.md`、`sourcegit-reuse-audit.md`、`oldcoffee-gitnest-review.md` 三份文档，尚无源码和 Git 仓库。
- 根据设计第 10 节拆分四阶段和可独立验证的提交批次，建立 [实施计划](implementation-plan.md)。
- 检查环境：Windows x64，Git `2.45.1.windows.1`，已安装 .NET SDK `8.0.201`，另有 .NET 10 运行时但没有 .NET 10 SDK。
- 上游最新稳定发行版为 `v2026.24`，目标框架为 `net10.0`，Avalonia 主版本依赖为 `11.3.22`。下一批固定完整提交和子模块版本，在工作区安装隔离的 .NET 10 SDK 后验证构建。
- Git 提交使用本机已有身份；按批次提交到本地仓库。

验证：已读取三份原始文档，检查工作区文件、Git 版本、SDK 列表与现有提交身份；上游版本及项目配置通过 GitHub 官方仓库核对。

## 2026-11-13 · 批次 1.2：导入并构建上游

- 前一批提交：`081d3ddd`（建立实施计划与原始文档基线）。
- 从官方仓库导入稳定版 `v2026.24`，完整提交 `2735a8ad80c2443bc736b7e6e501f1052b61d940`；初始化固定的 AvaloniaEdit 子模块 `ddf00a91dc0da29b85c79b85c9583860213a99ff`。
- 将 .NET SDK `10.0.404` 安装至工作区 `.tools/dotnet/`，核对官方 SHA-512；系统 PATH 保持原值。
- `dot.net` 安装脚本下载遇到 TLS EOF，改用微软发行元数据中的 SDK ZIP 地址并校验后解压，工具准备完成。
- 对未修改的应用源码执行 `dotnet build SourceGit.slnx -c Release --nologo`，构建成功，0 个警告、0 个错误。
- 记录 [固定上游与构建基线](upstream-baseline.md)、49 个 NuGet 包的实际解析版本及内容哈希；保留上游和子模块许可证。
- 确认上游许可清单有部分版本过时，下一批按依赖快照更新。

接续：批次 1.3 完成 GitNest 品牌、独立配置、可复现构建脚本、自包含发布及桌面启动验证。

## 2026-11-13 · 批次 1.3：GitNest 品牌与独立运行

- 前一批提交：`3d94cc76`（固定上游、完成原始源码 Release 构建）。
- 程序集和 Windows 产品名改为 GitNest，版本为 `0.1.0-dev`；保留上游源码项目路径和内部命名空间，更新 Avalonia 资源 URI 及嵌入字体引用。
- 添加 GitNest SVG 图标及多尺寸 ICO，更新窗口标题和关于页，保留 SourceGit 来源与版权署名；版本徽标显示本项目版本。
- 默认使用简体中文和跟随系统主题。关闭上游更新检测与入口，首版采用手动下载升级。
- 默认设置及缓存目录使用 `%APPDATA%/GitNest/`，便携目录使用 `gitnest-data/`；IPC、仓库 UI 状态／设置、临时交互文件和应用凭据标识改用 GitNest 名称，避免共享 SourceGit 的应用数据。
- 固定 SDK `10.0.404` 功能带，添加 SDK 准备和 Windows 构建／发布脚本；自包含发布采用普通 .NET，输出包含运行依赖及项目许可证。
- 校正上游许可清单的依赖版本，补充 CommunityToolkit.Mvvm，更新 README 与构建说明。

验证：上游与 GitNest Release 构建均为 0 警告、0 错误；Windows x64 自包含发布成功。真实桌面打开中文／空格／特殊字符路径仓库、默认语言、历史、关于页、Git 自动检测及独立配置落盘均通过。SourceGit 数据目录启动前后文件及哈希一致。详见 [阶段一验证记录](verification/phase-1.md)。

用户按物理 Esc 停止桌面检查后，停止界面操作，随后整理记录并完成本批本地提交。

阶段一完成。下一批为 2.1：Git 检测与首次引导、核心页面中文文案和入口调整；其余批次状态见实施计划。

## 2026-11-13 · 批次 2.1：核心导航与首次引导

- 前一批提交：`677b6750`（GitNest 品牌与 Windows 发布基线）。
- 仓库侧栏显示“提交历史／修改／暂存的工作”文字，沿用已暂存／未暂存列表、历史和本地／远端分支树；调整获取、推送和欢迎页入口的中文表述。
- 欢迎页新增 Git 状态、完整程序路径、版本、官网下载链接、重新检测和文件选择入口，直接复用打开／初始化与克隆弹窗。
- Git 检测实际执行 `--version`，区分文件缺失、程序执行失败、无效版本、版本过低、超时和可用状态；并行读取两个输出流，默认 3 秒超时。更换失败路径后清空旧版本，仓库入口使用验证结果。
- Windows 探测依次检查用户／系统 Git for Windows 注册记录与 PATH，跳过不存在的安装路径。欢迎页检测异步执行，成功后保存 GitNest 的程序路径设置。
- 启动时 Git 无效则保留待恢复标签与启动仓库路径，停留在引导页；检测成功后继续打开，避免将 Git 错误误判为需要初始化仓库。
- 在现有 `Commands.Config` 上增加只读身份查询。欢迎页读取已有全局姓名和邮箱，缺失时允许继续打开仓库；仓库查询按 Git 的实际继承关系读取。界面区分提交身份和远端认证。
- 当前 Git 能力门槛定为 `2.31.0`，依据为工作树锁定／prunable 元数据和绝对路径查询能力，来源见 [批次验证记录](verification/phase-2.1.md)。本次真实 Git 集成运行于 `2.45.1.windows.1`。
- 增加不依赖额外测试框架的集成检查程序和 `test-gitnest.ps1`，覆盖真实进程探测、版本边界、超时、旧状态清除及真实 Git 配置读取。

验证：26 项自动检查全部通过；Release 构建成功，0 警告、0 错误，Windows x64 自包含发布成功。桌面验证通过无效程序引导、重新检测后继续打开中文路径仓库、已有身份展示、导航与文件差异、打开／克隆弹窗和远端分支分组。引导前后全局配置哈希一致，便携配置正常保存，测试应用正常退出。

详细步骤、命令和日志位置见 [批次 2.1 验证记录](verification/phase-2.1.md)。

阶段二继续。下一批为 2.2：状态摘要、工作树任务概览、本地别名与快捷打开。

## 2026-11-13 · 仓库协作：建立远程与 Windows CI

- 前一批提交：`bb6e6080`（核心导航与首次引导）。
- 在当前登录的 MOAKIEE 账号创建私有仓库 [GitNest](https://github.com/MOAKIEE/GitNest)，添加 `origin`；本地默认推送远端设为 `origin`。
- 补齐原先浅克隆的上游历史，保留 SourceGit 导入提交与已有分批提交。
- 保留 AvaloniaEdit 的固定子模块版本及工作区原有显示方式。
- 将继承的上游构建、发布、Homebrew 通知及翻译改写工作流替换为 GitNest 的 Windows CI，检查构建、发布和集成测试，上传产物与日志。
- README 增加远程地址和递归克隆方法，项目元数据指向 GitNest 仓库；协作约定见 [远程仓库与协作](repository-workflow.md)。

本地验证：Windows Release 构建 0 警告、0 错误，自包含发布成功，26 项集成检查全部通过；工作流结构及解决方案中的工作流引用核对通过。

远程推送及 CI 结果见下一条记录。

## 2026-11-13 · 首次远程推送与提交邮箱

- 首次推送被 GitHub `GH007` 邮箱隐私保护拒绝。本仓库将提交邮箱改为账号对应的 GitHub noreply 地址，保留原提交链的本地备份后，重建 5 个尚未发布的 GitNest 提交。
- 核对各提交文件树一致，保留说明、作者名称、日期和提交图关系；上游提交及 AvaloniaEdit 固定版本保留。[提交映射](commit-email-migration.md) 记录新旧提交号，现行实施文档已更新引用。
- 调整后的仓库配置提交 `057f22ec` 已成功推送，GitHub 默认分支为 `main`，本地 `main` 跟踪 `origin/main`，远端与本地 HEAD 一致。
- 已撤回本次 VS Code 工作区设置调整，`.gitignore` 保持原配置。
- [首次 Windows CI](https://github.com/MOAKIEE/GitNest/actions/runs/43367433419) 全部通过，耗时 3 分 28 秒，完成远程检出、Windows 构建与发布、集成检查和产物上传。
- 已上传 `GitNest-verification-logs` 和 `GitNest-windows-x64` 两个产物；应用目录压缩产物为 53,925,679 字节，保留 7 天。
- 全局 Git 邮箱与调整前一致，本仓库使用 noreply 地址。后续仅文档更新按 CI 路径规则跳过重复构建。

## 2026-11-14 · 批次 2.5a：提交快照校验、并发写守卫与获取/快进拉取/单分支推送基础

- 新增 `src/Models/StagedSnapshot.cs`、`src/Models/RepositoryWriteGuard.cs`、`src/Models/CommitSyncModels.cs` 与 `src/Commands/CommandArgumentHelper.cs`、`src/Commands/QueryStagedSnapshot.cs`、`src/Commands/GitSyncService.cs`。
- 改进 `src/Commands/Add.cs`、`src/Commands/Reset.cs`、`src/Commands/Commit.cs`、`src/Commands/Pull.cs`、`src/Commands/Push.cs`、`src/Commands/UpdateIndexInfo.cs`，支持顶层 `--literal-pathspecs` + `\0` 分隔路径规范、提交默认取消隐式 `--allow-empty`、`-c pull.rebase=false pull --ff-only` 显式快进策略，以及单分支显式 refspec 普通推送。
- 新增真实 Git 集成测试 `tests/GitNest.IntegrationTests/CommitSyncTests.cs` 并接入 `Program.cs`，Release 构建 0 警告 0 错误，集成测试 **155** 项全通过；详细过程见 [phase-2.5.md](verification/phase-2.5.md)。

## 2026-11-14 · 批次 2.5b：原生风格提交与同步 UI 接入及探针验收

- 在 `WorkingCopy`、`WorkingCopy.CommitSync.cs`、`Fetch`、`Pull`、`Push` 及 `Repository.TaskOverview.cs` 完成快照守护、并发安全提示、部分暂存保护、本地提交身份内联补全、仅快进拉取（分叉可转入明确合并流程）、单分支显式 refspec 推送与两步“提交并推送”仅重试推送交互，整体保留 SourceGit 原生窗口与卡片样式。
- `en_US.axaml` 与 `zh_CN.axaml` 新增 36 组本地化词条，XML 键名校验 `en=1286 zh=1286 diff=0`。
- 新增 `tests/GitNest.UIProbe/CommitSyncProbe.cs`（`probe-gitnest.ps1 -CommitSync`），完成 11 项 UI 探针检查及 7 张浅色/深色/窄窗截图核对（过程与截图清单见 [phase-2.5.md](verification/phase-2.5.md)）。
- 末次代码修改后全量验证：`.\build\scripts\build-gitnest.ps1 -Publish`（0 警告 0 错误，输出 `artifacts/windows-x64/GitNest.exe`）、`.\build\scripts\test-gitnest.ps1`（155/155 通过）、`.\build\scripts\probe-gitnest.ps1 -CommitSync`（11/11 通过）。

阶段二（批次 2.1～2.5）全部完成；接续进入阶段三（批次 3.1：写操作串行化、确认快照、操作记录与恢复基础）。

## 2026-11-14 · 批次 3.1a：写串行化、确认快照、备份引用与恢复日志引擎

- 新增 `src/Models/OperationSafetyModels.cs`、`src/Models/OperationJournalStore.cs`（注册到 `src/App.JsonCodeGen.cs`）与 `src/Commands/CaptureOperationSnapshot.cs`、`src/Commands/BackupRefs.cs`、`src/Commands/OperationJournalService.cs`，建立统一的“确认快照捕获→锁内执行前多维度复核→脱敏操作日志→本地恢复引用（30 天保留 + 同名分支防覆盖）→重启未完成状态核查”引擎。
- 修复 `src/Models/Watcher.cs` 对主仓与 linked worktree 下合并/变基/revert/cherry-pick 状态标记的监听遗漏；分离 `src/ViewModels/Repository.cs` 自动获取计时锚点与真实 `_lastFetchTime`，消除从未获取仓库虚构初始获取时间的问题，并增加重开核查与激活刷新接口。
- 新增 `tests/GitNest.IntegrationTests/OperationSafetyTests.cs` 接入 `Program.cs`；Release 构建 `0` 警告 `0` 错误，`.\build\scripts\test-gitnest.ps1` 全量 **173 / 173** 项检查通过（过程与清单见 [phase-3.1.md](verification/phase-3.1.md)）。
- 下一批次（3.1b）将在 SourceGit 原生界面中提供“最近操作与恢复”视图、未完成提示横幅、焦点激活刷新与 UI 探针验收。

## 2026-11-14 · 批次 3.1b：最近操作与恢复原生视图、同步日志接线、窗口激活刷新与 UI 探针验收

- 在 `src/Commands/GitSyncService.cs`、`OperationJournalService.cs` 与 `Fetch`/`Pull`/`Push`/`WorkingCopy`/`WorkingCopy.CommitSync` 接入可选 `OperationJournalStore` 记录；在 `Repository`、`Launcher.axaml.cs`、`RepositoryToolbar` 与 `Repository.axaml` 接入窗口激活外部变更刷新、未完成恢复横幅与日志角标提醒。
- 将原生 `ViewLogs` 扩展为 SourceGit 风格“最近操作与恢复 / 原始命令日志”双页签窗口，支持实测磁盘核对、状态确认消警、显式目标分支恢复、脱敏诊断折叠区及 30 天过期备份引用预览/确认清理；新增 39 组中英文词条（`en=1325 zh=1325 diff=0`）。
- 同步修复测试与探针暴露的 3 处底层问题：`OperationJournalStore` 时区归一化、无目标 SHA 操作确认后的 `IsAcknowledged` 状态收敛，以及 `BackupRefs.TryReadReflogTimestampAsync` 使用 `--date=unix --format=%gd` 真实读取 reflog 创建时间。
- 末次代码改动后验证：`.\build\scripts\build-gitnest.ps1 -Publish`（0 警告 0 错误，生成 `artifacts/windows-x64/GitNest.exe`）、`.\build\scripts\test-gitnest.ps1`（**175 / 175** 通过）与 `.\build\scripts\probe-gitnest.ps1 -OperationRecovery`（**13 / 13** 通过并逐张验收 `artifacts/probes/phase-3.1/` 下 4 张浅色/暗黑/窄窗截图；详情见 [phase-3.1.md](verification/phase-3.1.md)）。
- 批次 3.1（3.1a / 3.1b）全部完成，接续进入批次 3.2（本地/远端分支删除、显式 `--force-with-lease`、恢复引用与部分失败处理）。

## 2026-11-14 · 批次 3.2a：本地／远端分支删除安全引擎、显式 lease 与部分失败恢复

- 前一批提交：`a9be9eef`（批次 3.1b：最近操作与恢复原生视图、同步日志接线、窗口激活刷新与 UI 探针验收）。
- 新增 `src/Models/BranchDeletionModels.cs` 与 `src/Commands/BranchDeletionService.cs`，重构抽取 `BackupRefs.DeleteBackupRefAsync` 并锚定加固 `DoesBranchExistOnRemote.cs`；实现真实 Git 配置 upstream 解析（支持异名映射与多远端、无上游时不猜测）、当前分支与工作树顺序清理保护、常规 `-d` 与未合并原因明细、删前本地备份引用创建与防覆盖恢复、远程状态精确探测（隔离网络失败与“服务器已缺失”缓存清理），以及冻结确认期哈希的显式 `--force-with-lease` 远端删除与“本地已删除，远程未删除”部分失败回滚通道。
- 新增 `tests/GitNest.IntegrationTests/BranchDeleteTests.cs` 并接入 `Program.cs`；Release 构建 `0` 警告 `0` 错误，`.\build\scripts\test-gitnest.ps1` 初始记录为 188 项后随 `BranchDeleteTests.cs:328` 修正确认实为全量 **191 / 191** 项通过（过程与清单见 [phase-3.2.md](verification/phase-3.2.md)）。
- 下一批次（3.2b）将更新右键分支菜单文案、增强原生 `DeleteBranch` / `DeleteMultipleBranches` 多步骤确认与部分失败就地恢复弹窗，并执行 `-BranchDelete` UI 探针与截图检查。

## 2026-11-14 · 批次 3.2b：原生分支删除交互弹窗、工作树顺序清理入口、双语词条与 UI 探针验收

- 前一批提交：`0288b0cd`（批次 3.2a：本地／远端分支删除安全引擎、显式 lease 与部分失败恢复）。
- 扩展 `src/ViewModels/Popup.cs`、`src/ViewModels/LauncherPage.cs` 与 `src/Views/LauncherPage.axaml` 支持可动态切换的动作按钮标题、`CanConfirm`/`ShowConfirmButton` 快捷键拦截与 `MinWidth="100"` 自适应宽度；更新 `BranchTree.axaml.cs` 与 `Histories.axaml.cs` 为明确区分的`删除本地分支` / `删除远端分支`右键菜单并在历史视图禁用当前检出分支本地删除。
- 重构 `src/ViewModels/DeleteBranch.cs`、`src/Views/DeleteBranch.axaml`、`.axaml.cs` 及 `src/ViewModels/DeleteMultipleBranches.cs`、`src/Views/DeleteMultipleBranches.axaml`：接入工作树占用顺序清理、真实配置 upstream 双目标确认清单、常规 `-d` -> `仍然强制删除`二次确认、分步部分失败就地恢复以及逐项批量结果展示；同时在 `DeleteMultipleBranchesAsync` 中完善需强删/清缓存跳过计数，并修复 Avalonia 横向 `StackPanel` 内文本无法折行导致的窄窗裁剪问题。
- 同步修正 `tests/GitNest.IntegrationTests/BranchDeleteTests.cs:328` 哈希断言命令为 `rev-parse --verify`，补充 `en_US.axaml`/`zh_CN.axaml` 双语词条（`en=1361 zh=1361 diff=0`）及 `tests/GitNest.UIProbe/BranchDeleteProbe.cs`（接入 `-BranchDelete`）。
- 末次代码改动后验证：`.\build\scripts\build-gitnest.ps1 -Publish`（`0` 警告 `0` 错误，生成 `artifacts/windows-x64/GitNest.exe`）、`.\build\scripts\test-gitnest.ps1`（**191 / 191** 通过）与 `.\build\scripts\probe-gitnest.ps1 -BranchDelete`（**13 / 13** 通过并逐张核对 `artifacts/probes/phase-3.2/` 下 4 张浅色/暗黑/窄窗截图；详情见 [phase-3.2.md](verification/phase-3.2.md)）。
- 批次 3.2（3.2a / 3.2b）全部完成，接续进入批次 3.3（工作树检查、删除与任务清理编排）。

## 2026-11-14 · 批次 3.3a：工作树预检引擎、锁定/嵌套/忽略保护、detached 备份与统一三步清理编排

- 前一批提交：`3bac421e`（批次 3.2b：原生分支删除交互弹窗、工作树顺序清理入口、双语词条与 UI 探针验收）。
- 新增 `src/Models/WorktreeDeletionModels.cs` 与 `src/Commands/WorktreeDeletionService.cs`，重构 `src/Models/BranchDeletionModels.cs` 与 `src/Commands/BranchDeletionService.cs` 消除工作树与分支双入口重复路径：共同使用单一的工作树多态检查/移除内核与“本地恢复引用 -> `-d`/`-D` -> 冻结 `--force-with-lease`”分支删除内核；覆盖主工作树保护、`.gitignore` 忽略文件防静默删除、`StateDigest` 内容变化防漂移、锁定阻断与 `UnlockWorktreeAsync`、子模块/嵌套 `.git` 仓库强删阻断、detached HEAD 删前恢复引用及 dry-run `PreviewPruneWorktreesAsync` / `PruneWorktreesAsync`。
- 新增 `tests/GitNest.IntegrationTests/WorktreeDeleteTests.cs`（15 项真实 Git 与多克隆测试）并接入 `Program.cs`；末次代码改动后 `.\build\scripts\build-gitnest.ps1` 构建 `0` 警告 `0` 错误，`.\build\scripts\test-gitnest.ps1` 全量 **206 / 206** 项检查通过（过程与清单见 [phase-3.3.md](verification/phase-3.3.md)）。
- 下一批次（3.3b）将实现正在打开工作树自动切换主仓关闭视图、原生 `RemoveWorktree` 与 `PruneWorktrees` 对话框及 `-WorktreeDelete` UI 探针截图验收。

## 2026-11-14 · 批次 3.3b：工作树移除与清理预览原生弹窗、打开视图自动切换及 UI 探针验收

- 前一批提交：`7d03bcf6`（批次 3.3a：工作树预检引擎、锁定/嵌套/忽略保护、detached 备份与统一三步清理编排）。
- 在 `src/ViewModels/Launcher.cs` 与 `LauncherPage.cs`、`Repository.cs` 新增打开工作树标签页检测与移除前自动切回主仓、转移弹窗进度并关闭原页签能力；在 `src/Views/Repository.axaml` 与 `.axaml.cs` 新增工作树分组标题清理按钮与窄侧栏防重叠布局，开放当前已打开关联工作树菜单移除入口并严格屏蔽主工作树删除。
- 重构 `src/ViewModels/RemoveWorktree.cs`、`src/Views/RemoveWorktree.axaml`/`.axaml.cs` 与 `PruneWorktrees.cs`/`.axaml`/`.axaml.cs`，并在 `src/ViewModels/DeleteBranch.cs`、`src/Views/DeleteBranch.axaml` 复用 `AttachedWorktreePreflight` 细节检查、工作树视图切换回调与任务概览刷新；新增并校对中英双语词条（`en=1401 zh=1401 diff=0`）。
- 同步排查并修复截图检查发现的两处问题：`RemoveWorktree.IsLoadingPreflight` 结束时补发 `CanShowForceOption`/`ShowForceLossWarning`/`CanOfferLocalBranchDelete` 等依赖属性变更通知，并用紧凑滚动列表与窄侧栏 `TextTrimming` 消除弹窗内控件裁切与分组计数重叠。
- 末次代码改动后验证：`.\build\scripts\build-gitnest.ps1 -Publish`（`0` 警告 `0` 错误，生成 `artifacts/windows-x64/GitNest.exe`）、`.\build\scripts\test-gitnest.ps1`（**206 / 206** 通过）、`.\build\scripts\probe-gitnest.ps1 -BranchDelete`（**13 / 13** 通过）与 `.\build\scripts\probe-gitnest.ps1 -WorktreeDelete`（**20 / 20** 通过，并逐张核对 `artifacts/probes/phase-3.3/` 下 4 张浅色/暗黑/窄窗截图；详情见 [phase-3.3.md](verification/phase-3.3.md)）。
- 批次 3.3（3.3a / 3.3b）全部完成；接续进入批次 3.4（定向合并、revert 及冲突归属检查）。

## 2026-11-14 · 批次 3.4a：定向合并引擎、单提交 revert、冲突归属守卫与失败分类

- 前一批提交：`bc348ee1`（批次 3.3b：工作树移除与清理预览原生弹窗、打开视图自动切换及 UI 探针验收）。
- 新增 `src/Models/MergeRevertModels.cs` 与 `src/Commands/MergeRevertService.cs`，抽取 `CommandArgumentHelper.ValidateNonOptionToken` 并重构 `src/Commands/Merge.cs`、`MergeTree.cs`、`Revert.cs` 与 `Command.cs`（移除 `Revert.cs` 硬编码的 `revert -m 1`，增加参数非选项校验、引述与直连非交互超时重载）；同步在 `GitOperationRecord` / `OperationJournalStore` / `OperationJournalService` 增补 `ExpectedPartnerSha` 持久化字段以跟踪冲突伙伴提交。
- 实现「将【来源】的改动合并到【目标】」预检与加锁执行（含显式远端已抓取快照来源、跨分支目标切换、其他 worktree 占用拦截、`merge-tree --write-tree` 无副作用冲突预检、操作前干净态与确认快照检查、仅对 `Parents.Count <= 1` 的单提交 revert）、冲突后逐文件磁盘/暂存 SHA-256 归属与 AI 外部介入护卫（无外部介入方可自动 `--abort` 并二次校验还原；若发现任何外部介入则立即停止并保留现场）、只读外部进行中状态识别（按控制文件修改时序区分外部重跑与保留现场，打开/刷新绝不自动中止），以及非冲突场景下 `HookRejected` / `SigningFailed` 错误分类。
- 新增 `tests/GitNest.IntegrationTests/MergeRevertTests.cs` 并接入 `Program.cs`；末次代码改动后 `.\build\scripts\build-gitnest.ps1` 构建 `0` 警告 `0` 错误，`.\build\scripts\test-gitnest.ps1` 全量 **233 / 233** 项真实 Git 检查通过（过程与清单见 [phase-3.4.md](verification/phase-3.4.md)）。
- 下一批次（3.4b）将在 SourceGit 原生样式中更新 `Merge`、`Revert` 与只读 `Conflict` 进行中状态界面，补全中英词条、`GitNest.UIProbe` 探针与截图验收。

## 2026-11-14 · 批次 3.4b：定向合并 /单提交 Revert 原生弹窗、只读冲突状态面板与探针截图验收

- 前一批提交：`0213d125`（批次 3.4a：定向合并引擎、单提交 revert、冲突归属守卫与失败分类）。
- 重构 `src/ViewModels/Merge.cs` 与 `src/Views/Merge.axaml`/`.axaml.cs`、`src/ViewModels/Revert.cs` 与 `src/Views/Revert.axaml`/`.axaml.cs`，按 SourceGit 原生样式落地来源/目标方向提示、预期冲突无副作用预检、拟并入提交与文件列表、合并提交 revert 明确拦截卡片与非自动关闭诊断结果视图。
- 改造 `WorkingCopy`（`.cs`/`.axaml`/`.axaml.cs`）、`Repository`（`.cs`/`.axaml`/`.axaml.cs`）与 `Conflict`（`.cs`/`.axaml`/`.axaml.cs`）为严格只读冲突归属与状态面板：区分「外部操作进行中」与 GitNest 保留态、门控直接中止按钮、屏蔽内部解决编辑器并移除 `UseMine`/`UseTheirs`，同时从 `MERGE_HEAD`/`REVERT_HEAD`/`CHERRY_PICK_HEAD` 兜底解析 `THEIRS` 提交；同步修复 `Conflict` 面板透明穿透、`Grid` 单行省略号与 `MergeCommitBlock` 空列表/禁用按钮干扰。
- 补全双语词条（`zh=1447 en=1447 diff=0`）、新增 `tests/GitNest.UIProbe/MergeRevertProbe.cs`（`-MergeRevert`）及 `Popup.Cleanup()` 取消后台预检 + 探针回收站非阻塞清理机制（同时消除 `-CommitSync` 收尾弹窗句柄卡死问题）。
- 末次代码改动后验证：`.\build\scripts\build-gitnest.ps1 -Publish`（`0` 警告 `0` 错误，产出 `artifacts/windows-x64/GitNest.exe`）、`.\build\scripts\test-gitnest.ps1`（**233 / 233** 通过）、`.\build\scripts\probe-gitnest.ps1 -MergeRevert`（**11 / 11** 通过，`TEMP_LEFTOVERS=0`）、`.\build\scripts\probe-gitnest.ps1 -CommitSync`（**11 / 11** 通过，`TEMP_LEFTOVERS=0`）并完成 `artifacts/probes/phase-3.4/` 5 张截图视觉复核；详情见 [phase-3.4.md](verification/phase-3.4.md)。
- 批次 3.4（3.4a / 3.4b）全部完成；接续进入批次 3.5（stash、丢弃前备份、文件回收与忽略规则）。

## 2026-11-14 · 批次 3.5a：Stash 事务与冲突保留、丢弃双基线、30 天本地恢复库、回收站安全删除与忽略规则引擎

- 前一批提交：`13fc5ee5`（批次 3.4b：定向合并 / 单提交 revert 原生弹窗、只读冲突状态面板与探针截图验收）。
- 在 `src/Native/OS.cs` / `Windows.cs` / `MacOS.cs` / `Linux.cs` 增补系统回收站接口（Windows 经 `SHFileOperationW` `FOF_ALLOWUNDO | FOF_NOERRORUI` 并二次核验磁盘已移出，失败严禁降级永久删除）；新增 `src/Models/WorkingCopySafetyModels.cs`、`src/Models/DiscardBackupStore.cs` 并在 `App.JsonCodeGen.cs` / `OperationSafetyModels.cs` / `Models/Stash.cs` 完成源分支解析、30 天本地恢复库原子索引与二进制载荷校验及 `SwitchBranch` 日志类型扩展。
- 新增 `src/Commands/StashWorkflowService.cs`、`DiscardBackupService.cs`、`IgnoreRuleService.cs`，强化 `Command.cs`（stdin/NUL 通道）、`CommandArgumentHelper.cs`、`Stash.cs`、`QueryStashes.cs`、`Restore.cs`、`Discard.cs` 与 `MergeRevertService.cs`：实现 unborn HEAD/冲突态 stash 拦截、基于 `stash^1..stash`/`^2`/`^3` 的未跟踪与忽略文件碰撞阻断、SHA 绑定应用及冲突保留（`PendingVerification`「恢复未完成，保存项仍保留」）、分支切换拒绝静默丢弃并安全 `StashAndReapply`、丢弃双基线（`RestoreWorktreeToIndex` 与 `RestoreToHead`）前置 30 天备份与失败中止、后续修改防静默覆盖检查及 30 天到期清理、特殊字符转义与主仓/worktree `info/exclude` / `.gitignore` 去重和已跟踪文件安全预警。
- 新增 `tests/GitNest.IntegrationTests/StashDiscardIgnoreTests.cs` 并接入 `Program.cs`；清理 `DiscardBackupService.cs` 冗余辅助包装后，重新执行 `.\build\scripts\build-gitnest.ps1` 构建（`0` 警告 `0` 错误）与 `.\build\scripts\test-gitnest.ps1`（**274 / 274** 项真实 Git 与 Windows 回收站/文件锁集成检查通过；详情见 [phase-3.5.md](verification/phase-3.5.md)）。
- 下一批次（3.5b）将在 SourceGit 原生样式中构建 Stash/Discard/恢复库/AddToIgnore 对话框与工作区右键基线入口，并完成 `-StashDiscardIgnore` UI 探针与截图验收。

## 2026-11-15 · 批次 3.5b：Stash/丢弃基线/30 天恢复库/忽略规则原生 UI 与探针截图验收

- 前一批提交：`43e9547b`（批次 3.5a：Stash 事务、丢弃双基线、30 天本地恢复库、回收站删除与忽略规则核心引擎）。
- 重构 `src/ViewModels/Discard.cs`、`src/Views/Discard.axaml`/`.axaml.cs` 与 `WorkingCopy`（`.cs`/`.axaml`/`.axaml.cs`）：实现 `RestoreWorktreeToIndex` / `RestoreToHead` / `MoveUntrackedToRecycleBin` 明确三基线交互、unborn HEAD 显式拦截、子模块跳过与部分回收失败不闭窗结果展示、无忽略清理入口边界，并拆分工作区右键菜单与标题栏恢复库直达按钮。
- 扩展 `src/ViewModels/ViewLogs.cs`、`ViewLogs.DiscardBackups.cs` 与 `src/Views/ViewLogs.axaml`/`.axaml.cs`：新增 `丢弃恢复 (30 天)` 页签，提供快照浏览、后续修改冲突 preflight 检查与显式确认覆盖恢复、单条删除及过期记录清理。
- 改造 `StashChanges`、`ApplyStash`、`DropStash`、`CheckoutBranchFromStash`（精确 SHA 绑定与冲突/部分失败保留卡）、`DealWithLocalChangesMethod`（移除 `Discard` 自动弃改）、共享 `BranchSwitchPopup`/`BranchSwitchResultView` 及其四大分支切换子类，与 `AddToIgnore`（`.gitignore` vs `info/exclude` 仅本机提示、`!` 反向规则冲突与已跟踪文件预警）。
- 同步修复截图走查中发现的 `Discard` 列挤压截断、`ApplyStash` 阻断提示与文件数标题重复、`ViewLogs` 恢复绿标裁切及 `AddToIgnore` 仅本机标签/警告重复问题；补全双语词条（`en=1562 zh=1562 diff=0`）并新增 `tests/GitNest.UIProbe/StashDiscardIgnoreProbe.cs`（`-StashDiscardIgnore`）。
- 末次代码改动后验证：`.\build\scripts\build-gitnest.ps1 -Publish`（`0` 警告 `0` 错误，产出 `artifacts/windows-x64/GitNest.exe`）、`.\build\scripts\test-gitnest.ps1`（**274 / 274** 通过）、`.\build\scripts\probe-gitnest.ps1 -StashDiscardIgnore`（**15 / 15** 通过，核对 `artifacts/probes/phase-3.5/` 全部 11 张浅色/暗黑/窄窗截图）、`.\build\scripts\probe-gitnest.ps1 -MergeRevert`（**11 / 11** 回归通过）；详情见 [phase-3.5.md](verification/phase-3.5.md)。
- 批次 3.5（3.5a / 3.5b）全部完成；接续进入批次 3.6（commit reset、`--force-with-lease` 远程历史改写、本地备份恢复与重启对账）。

### 批次 3.6a：提交撤回、分支保护、租约远端改写与安全恢复核心
- 新增 `src/Commands/BranchProtectionPolicy.cs`、`src/Models/HistoryResetModels.cs` 与 `src/Commands/HistoryResetService.cs`，并在 `src/Models/RepositorySettings.cs`（`UnprotectedBranches` 按仓库显式解锁）及 `src/Models/OperationSafetyModels.cs`、`src/Commands/OperationJournalService.cs` 补齐恢复结果标识与重启状态对账。
- 实现提交撤回仅限 `--mixed` / `--hard`、强制沿 `--first-parent` 主线校验、拒绝 unborn/detached HEAD 与将首个提交撤回为空仓库、严格要求干净工作区/暂存区、检测未跟踪与 `.gitignore` 忽略路径覆盖冲突（绝不自动运行 `git clean`），并按完整 `target..HEAD` 跨合并节点统计提交与多人作者归属。
- 实现 `改写远程分支历史` 实时 `ls-remote` 预检、断网显式标记 `远端状态未知`、阻止覆盖未整合的远端分叉提交、多远端精确隔离未推送计数、分离本地与远端撤销列表、执行前创建 30 天本地 `refs/gitnest/backups/<opId>` 备份引用、使用确认阶段冻结的显式 `--force-with-lease=refs/heads/<branch>:<expectedSha>`（不受后台 `fetch` 污染），并在传输超时/中断时单次发起 `ls-remote` 核实而不盲目重试；协作者后推拒绝时明确保留远端提交、回报 `本地已撤回，远端未更新` 与 `PartialSuccess` 恢复入口。
- 新增 `InspectWithoutCheckoutResetAsync` / `ExecuteWithoutCheckoutResetAsync`（阻止被当前或其他工作树检出的分支及受保护分支回退）与 `InspectBackupRestoreForResetAsync` / `RestoreFromBackupAsync`（支持在干净且无后续提交时安全回写原重置分支，否则拦截覆写并安全创建新恢复分支）及 `VerifyRemoteRewriteJournalRecordAsync`。
- 新增并注册多克隆真实 Git 集成测试 [HistoryResetRewriteTests.cs](../tests/GitNest.IntegrationTests/HistoryResetRewriteTests.cs)；末次改动后执行 `.\build\scripts\build-gitnest.ps1`（`0` 警告 `0` 错误）与 `.\build\scripts\test-gitnest.ps1`（**302 / 302** 通过）；详情见 [phase-3.6.md](verification/phase-3.6.md)。
- 下一步执行批次 3.6b（原生 UI、仓库设置保护开关、日志恢复弹窗、`ResetRewriteProbe` 及截图走查）。

## 2026-11-15 · 批次 3.6b：提交撤回、远端历史改写、仓库保护解锁与备份恢复原生 UI 及探针验收

- 前一批提交：`f949810e`（批次 3.6a：提交撤回、分支保护、租约远端改写与安全恢复核心）。
- 重构 `Reset`、`ResetWithoutCheckout` 与 `PushRevision`（补充 `Commands.Push` 显式 `expectedLeaseTipSha` 重载），并新增 `RewriteRemoteHistory` 弹窗及 `ControlExtensions` / `Histories` / `BranchTree` 右键菜单入口：撤回仅保留 `--mixed` 与 `--hard` 两种模式，拦截路径覆盖与未保护分支远端改写，展示跨合并提交范围与多人协作者署名、隔离多远端未推送状态、断网显式阻止并标记 `远端状态未知`，以及在部分失败/租约拒绝或结果待核实状态下保留备份引用卡与「打开操作与恢复日志」直达入口。
- 扩展 `BranchProtectionPolicy`、`RepositoryConfigure`（`.cs`/`.axaml`）与 `ViewLogs`（`.cs`/`.axaml`/`.axaml.cs`、`OperationJournalService.cs`）：在 `仓库配置 → Git` 增加按当前仓库独立解锁 `main`/`master`/`develop` 与 `<remote>/HEAD` 默认分支的保护开关；在「最近操作与恢复」支持「联网核对远端状态」及安全的回滚恢复（默认新建 `recover/...` 分支，有后续提交或工作区脏时拒绝覆写原分支）。
- 同步修复截图走查发现的 4 项问题：`Icons.Error` 警告三角图标替换（修复不存在的 `Icons.Warning` 引用）、`Reset` 窄窗结果页去重 stderr 并提升备份引用与恢复入口位置、缩小撤回列表滚动区使底部远端锁框首屏可见，以及修复 `ViewLogs` 选中刷新与恢复面板滚动定位及标点问题。
- 补全双语词条（`en_US.axaml` 与 `zh_CN.axaml` 对称扩展至 `1624` 条），新增 `tests/GitNest.UIProbe/ResetRewriteProbe.cs`（接入 `probe-gitnest.ps1 -ResetRewrite`）。
- 末次代码改动后验证：`.\build\scripts\probe-gitnest.ps1 -ResetRewrite`（**14 / 14** 探针检查通过并逐张核对 `artifacts/probes/phase-3.6/` 下 14 张浅色/暗黑/窄窗截图）、`.\build\scripts\test-gitnest.ps1`（**302 / 302** 通过）与 `.\build\scripts\build-gitnest.ps1 -Publish`（**0** 警告 **0** 错误，产出 `artifacts/windows-x64/GitNest.exe`）；详情见 [phase-3.6.md](verification/phase-3.6.md)。
- 批次 3.6（3.6a / 3.6b）全部完成；接续进入批次 3.7（配置作用域写入、外部变更复核与刷新）。

## 2026-11-15 批次 3.7：配置作用域写入、外部变更复核与刷新

- 前一批提交：`59f89f0e`（批次 3.6b：撤回提交、默认分支保护开关、远端历史改写与恢复界面及探针）。
- 强化配置作用域锁检查：`src/Models/RepositoryWriteGuard.cs` 补齐 `<gitDir>/config.worktree.lock`、`<commonDir>/config.lock` 与 `GIT_CONFIG_GLOBAL`/用户全局 `.gitconfig.lock` 只读检测（绝不删除锁文件）；`src/Commands/Config.cs`、`Remote.cs`、`Branch.cs` 与 `RepositoryConfigure.SaveAsync()` 在持有全局或仓库配置锁时直接中止写入并保留草稿。
- 实现外部变更复核与刷新同步：`src/ViewModels/RepositoryConfigure.cs` 与 `src/Views/RepositoryConfigure.axaml`/`.axaml.cs` 新增 `GlobalScopeTargetHint` 全局目标文件提示、`ReloadAsync` 世代并发防抖，以及围绕 `Repository.ConfigurationStateChanged` 和窗口激活的 `CheckExternalChangesAsync`——页面无脏改时自动重载；存在未保存草稿时保留全部草稿并显示 `ExternalConfigChangeBanner`（列明 `includeIf` 分支变化与键值差量），提供「丢弃冲突草稿并重新加载」和「保留我的草稿（按最新外部状态重设基线）」。
- 补齐弹窗确认快照失效检测与后台刷新世代隔离：`DiscardBackupService` 在 `InspectDiscardAsync` 捕获确认快照并在 `ExecuteDiscardAsync` 校验 `expectedPreflight`（`BlockedBySnapshot` 时拒绝执行并在 `Discard` 视图刷新重检；修复 `CaptureOperationSnapshot.CaptureDiskFileAsync` 遇独占锁定文件时的容错捕获），`Reset` 在未执行本地撤回的阻断/失败结果卡新增「刷新状态并重新检查」按钮；`Watcher` 与 `Repository` 新增 `config`/`config.worktree`/`HEAD`/`worktrees/*` 管理事件通知、全量 `Refresh*` 世代号与 `_isClosed` UI 派发防护、窗口激活同步刷新及 `Repository.Close()` 判空防护。
- 对称补全中英双语词条（`en_US.axaml` 与 `zh_CN.axaml` 均达 `1634` 条，`diff=0`），新增 `tests/GitNest.IntegrationTests/Phase37SyncTests.cs` 并扩展 `tests/GitNest.UIProbe/ConfigProbe.cs`（接入 `probe-gitnest.ps1 -ConfigRefresh`）。
- 末次代码改动后验证：`.\build\scripts\build-gitnest.ps1 -Publish`（**0** 警告 **0** 错误，产出 `artifacts/windows-x64/GitNest.exe`）、`.\build\scripts\test-gitnest.ps1`（**320 / 320** 通过）、`.\build\scripts\probe-gitnest.ps1 -ConfigRefresh`（**4 / 4** 通过，核对 `artifacts/probes/phase-3.7/` 下 3 张截图）及 `.\build\scripts\probe-gitnest.ps1 -Config`（**13 / 13** 回归通过）；详情见 [phase-3.7.md](verification/phase-3.7.md)。
- 阶段三（3.1～3.7）全部完成；接续进入阶段四批次 4.1（首版入口裁剪、依赖及资源清理）。

## 2026-11-15 · 批次 4.1：首版入口裁剪、依赖及资源清理

- 前一批提交：`681594e1`（批次 3.7：配置作用域写入、外部变更复核与刷新）。
- 按 [设计文档](design.md) 第 2 节完整裁剪首版不纳入能力（共移除 98 个独立模块/命令/弹窗文件并同步清理入口、热键与命令面板）：移除 `Azure.AI.OpenAI` / `OpenAI` 依赖及 `src/AI/`、`CustomAction`、`GitFlow`、`Bisect`、`InteractiveRebase`、`CherryPick`、`MergeConflictEditor`、补丁管理与保存/应用、`TryGetCreatePullRequestURL`、标签写入、工作树新建/迁移与子模块创建/变更按钮；保留只读 Worktree/Submodule/LFS 识别、只读标签视图、`Apply.cs`/`SaveChangesAsPatch.cs` 内部按块暂存底层复用，以及 `CherryPickInProgress`/`GitInProgressKind.Bisect` 外部冲突状态只读拦截。
- 清理 `src/ViewModels/FileHistories.cs` 残留 `_changes` 字段与 `src/Resources/Icons.axaml` 中 18 个未引用图标；在 `en_US.axaml` 与 `zh_CN.axaml` 增补只读标签页所需的 `Text.Tag.Annotated`（`"Annotated"` / `"附注标签"`），同时完整移除 224 条失效功能词条并校验双语零差集（`en=1411 zh=1411 diff=0`）。
- 同步更新 `THIRD-PARTY-LICENSES.md`、[依赖清单](dependency-inventory.md) 与 [依赖快照](dependency-snapshot.json)（Windows x64 Release 发布集合由基线 49 个 NuGet 包精简为 32 个包）；将阶段临时文件移入回收站。
- 新增 `tests/GitNest.IntegrationTests/Phase41TrimTests.cs` 与 `tests/GitNest.UIProbe/TrimmedSurfaceProbe.cs`（接入 `probe-gitnest.ps1 -TrimSurface`）。末次代码改动后验证：`.\build\scripts\build-gitnest.ps1 -Publish`（**0** 警告 **0** 错误，产出 `artifacts/windows-x64/GitNest.exe`）、`.\build\scripts\test-gitnest.ps1`（**370 / 370** 通过）、`.\build\scripts\probe-gitnest.ps1 -TrimSurface`（**24 / 24** 通过，逐张核对 `artifacts/probes/phase-4.1/` 下 4 张截图），并复测 `-Config`（**13 / 13**）、`-ConfigRefresh`（**4 / 4**）、`-CommitSync`（**11 / 11**）、`-OperationRecovery`（**13 / 13**）与 `-Comparison`（**30 / 30**）回归探针均通过；详情见 [phase-4.1.md](verification/phase-4.1.md)。
- 批次 4.1 完成；接续进入批次 4.2（Windows x64 安装包、升级、卸载及高 DPI 验证）。

## 2026-11-15 · 批次 4.2：Windows x64 单用户安装包、升级/卸载与 PerMonitorV2 高 DPI 验证

- 前一批提交：`3913fd50`（批次 4.1：首版入口裁剪、依赖及资源清理）。
- 在 `src/App.manifest` 增加 Windows `PerMonitorV2` 与 `true/pm` DPI 感知声明；新增 [gitnest.iss](../build/installer/gitnest.iss) 与 [build-installer.ps1](../build/scripts/build-installer.ps1)，基于 Inno Setup 6.7.3 编译 Windows x64 单用户安装包（`PrivilegesRequired=lowest`、默认目录 `%LOCALAPPDATA%\Programs\GitNest`、配置目录 `%APPDATA%\GitNest`、不打包 `git.exe`、不改 `PATH`、附带 `LICENSE`/`NOTICE.md`/`THIRD-PARTY-LICENSES.md`/`licenses\AvaloniaEdit-LICENSE.txt` 并将 `*.pdb` 与 `createdump.exe` 排除），同步更新 [build/README.md](../build/README.md)。
- 新增 [verify-installer.ps1](../build/scripts/verify-installer.ps1)（38 项检查：全新静默安装、PE `RT_MANIFEST` 检测、`PATH` 不变性、无 `git.exe`/`OpenAI*.dll`/`Azure*.dll` 检查、冷启动冒烟、高版本 `0.1.1-test` 覆盖升级、完整静默卸载及保留 `%APPDATA%\GitNest` 与用户仓库，测试残留自动进回收站）与 `tests/GitNest.UIProbe/HighDpiProbe.cs`（接入 `probe-gitnest.ps1 -HighDpi`；在首次核图发现 `DeleteBranch` 预检态瞬时截屏后改为等待 `!delVm.IsLoadingPreflight` 并重新出图复核）。
- 末次代码改动后验证：`.\build\scripts\build-gitnest.ps1 -Publish`（**0** 警告 **0** 错误，产出 `artifacts/windows-x64/GitNest.exe`）、`.\build\scripts\probe-gitnest.ps1 -HighDpi`（**7 / 7** 通过，视觉核对 `artifacts/probes/phase-4.2/` 下 150%/200% DPI 共 4 张浅色/暗黑/弹窗截图无裁剪重叠）、`.\build\scripts\build-installer.ps1 -SkipPublish` 编译生成 `artifacts/installer/GitNest-Setup-0.1.0-dev-win-x64.exe`（`35.95 MB`，SHA-256 `02734FF669A34742A3BFA80CE147A52D2063C589E704F989F7230687EDDE2EE3`），以及 `.\build\scripts\verify-installer.ps1`（**38 / 38** 通过）；详情见 [phase-4.2.md](verification/phase-4.2.md)。
- 批次 4.2 完成；接续进入批次 4.3（按设计第 10 节完成全量真实 Git 集成验收与发行记录）。

## 2026-11-15 · 批次 4.3：第 10 节全量真实 Git 集成验收、清理与发行记录

- 前一批提交：`88e81cb8`（批次 4.2：Windows x64 Inno Setup 单用户安装包、升级/卸载与 PerMonitorV2 高 DPI 验证）。
- 对照 [design.md](design.md) 第 10 节逐条审计测试覆盖范围，新增 [Phase43AcceptanceTests.cs](../tests/GitNest.IntegrationTests/Phase43AcceptanceTests.cs) 并接入 [Program.cs](../tests/GitNest.IntegrationTests/Program.cs)：补全缺失/损坏/非 Git 可执行文件检测及恢复就绪验证、unborn 空仓库只读查询、`git fast-import` 真实 650 条大量历史记录查询与搜索、`CompareRevisions`/`IsBinary`/`Diff` 对中文特殊路径文本差异与 NUL 字节二进制文件识别、`core.sshCommand` 模拟 `ASKPASS_CANCELLED` 与本地回环 `TcpListener` 返回 `HTTP 401` 的真实推送认证失败反馈及未污染提交/清洁工作区状态保留，同时核实远端 token 与 `http.extraheader` 敏感凭据绝不进入错误摘要、诊断信息或持久化操作日志。
- 末次代码改动后顺序跑完全套终验：
  - `.\build\scripts\build-gitnest.ps1 -Publish`（**0** 警告 **0** 错误，产出 `artifacts/windows-x64/GitNest.exe`）；
  - `.\build\scripts\test-gitnest.ps1`（**388 / 388** 通过）；
  - 全量 12 组具名 UI 探针依次全部通过：`-Comparison`（**30/30**）、`-Config`（**13/13**）、`-ConfigRefresh`（**4/4**）、`-CommitSync`（**11/11**）、`-OperationRecovery`（**13/13**）、`-BranchDelete`（**13/13**）、`-WorktreeDelete`（**20/20**）、`-MergeRevert`（**11/11**）、`-StashDiscardIgnore`（**15/15**）、`-ResetRewrite`（**14/14**）、`-TrimSurface`（**24/24**）、`-HighDpi`（**7/7**）；
  - `.\build\scripts\build-installer.ps1 -SkipPublish` 生成最终安装包 `artifacts/installer/GitNest-Setup-0.1.0-dev-win-x64.exe`（`35.95 MB`，SHA-256 `2072815A74402B908A2340AAD605F115B93E4081E5CCD39F236DD41D8B53BEDF`）；
  - `.\build\scripts\verify-installer.ps1` 全流程 **38 / 38** 通过，并将 `%TEMP%\*GitNest*` 与阶段二遗留 `artifacts/smoke/` 送入系统回收站（`0` 残留测试目录，唯一本地 `main` 分支与单主工作树）。
- 撰写总验报告 [phase-4.3.md](verification/phase-4.3.md) 与 [发行记录](release-notes.md)，阶段一至阶段四全部批次完成。

## 2026-11-15 · 整体审查修复一：丢弃与恢复

- 修复重命名原路径新文件被覆盖、已删除文件无法恢复及索引删除状态恢复错误；补齐双路径备份、锁内快照复核、恢复失败结果检查及仓库归属检查。
- 新增 11 项真实 Git 回归检查。完整集成检查 399/399 通过，最终代码定向检查 37/37 通过；验证细节见 [审查修复记录](verification/review-fixes.md)。

## 2026-11-15 · 整体审查修复二：远端目标、推送范围与分支保护

- 前一批提交：`a60d6edb`（丢弃与恢复）。
- 修复远端预检与推送地址不一致、多推送地址扩大操作范围、默认保护未覆盖删除，以及普通推送自动携带标签；补齐冻结提交和地址、日志地址指纹、同源跟踪缓存更新与旧 SHA 校验。
- 历史页面的指定修订推送复用单分支推送，强制模式复用历史改写引擎；移除旧推送重载。
- 新增 25 项真实 Git 回归检查及 8 项 UI 检查；修复比较页面探针的异步等待条件。验证结果见 [审查修复记录](verification/review-fixes.md)。
- 最终代码构建发布 0 警告、0 错误，完整集成检查 424/424、分支删除 UI 16/16、撤回／改写与指定修订推送 UI 19/19 通过。

## 2026-11-15 · 整体审查收尾：发行记录、最终验收与清理

- 前一批提交：`e4c7de68`（远端目标、推送范围与分支保护）。
- 更新 README、设计和实施状态、SourceGit 基准版本、安装及依赖打包说明；将历史验收中的本机绝对链接改为仓库相对链接。
- 最终构建发布 0 警告、0 错误，集成检查 424/424、12 组 UI 探针 183/183、安装包生命周期检查 38/38 通过；中英词条各 1411 项且键一致，29 份 Markdown 本地链接通过检查。
- 安装包 `GitNest-Setup-0.1.0-dev-win-x64.exe` 的 SHA-256 为 `12A34A2FFAD8EA8FE5C14F086B0FB9EF6A950EFE88B3191D4455C66348254707`，验证日志归档至 `artifacts/logs/review-*.log`。原用户配置已恢复，临时夹具、补丁及包装脚本送入回收站；仅保留本地 `main` 和主工作树。
- 2.2 原生交互补验收到物理 Esc 中止信号后停止；完整进程重启、资源管理器等剩余条目明确保留在 [phase-2.2.md](verification/phase-2.2.md)。

## 2026-11-15 · 上游残留清理与 GitNest 更新检查

- 删除 4 个无调用文件、6 个旧命令方法及冲突编辑器残留类型；移除 15 份语言资源，保留简体中文和英文，旧语言配置自动回退简体中文。
- 将更新检查切换到 `MOAKIEE/GitNest` 最新正式 GitHub Release，恢复手动检查入口与可选的启动检查，下载按钮指向本项目发行页；处理无可访问发行版及无效响应。
- 按语义版本比较补丁号与预发行标识，构建元数据不影响版本先后；读取 VERSION 时去掉末尾换行，当前版本显示为 `v0.1.0-dev`。
- 新增 35 项检查。最终发布构建通过，完整集成检查 459/459、裁剪与更新 UI 探针 27/27、安装包生命周期检查 38/38 通过；已核对更新窗口及偏好设置截图，安装验证后原用户配置文件哈希一致。
- 安装包大小 35.82 MB，SHA-256 `0A38BD5242525BDFD92468BE50F918C3F9BF7286413F28FFA07B5FE60CEC1C4E`；本轮日志位于 `artifacts/logs/cleanup-final-*.log`，截图位于 `artifacts/probes/phase-4.1/`。删除的源码和语言资源已移至回收站。

## 2026-11-15 · Git 2.56.1 推送兼容修复

- 推送后的 Windows CI 使用 Git 2.56.1，远端回归发现按 URL 推送也会匹配已配置远端并改动跟踪引用；已使用同版本 MinGit 复现获取／推送地址分离时的缓存误删。
- 普通推送、显式 lease 删除和历史改写统一使用进程内临时远端，推送目标仍固定到已确认地址；跟踪引用继续按获取地址及确认旧值显式更新，不落盘临时配置。
- 新增 2 项分离地址推送／历史改写回归，测试支持通过 `GITNEST_TEST_GIT_EXECUTABLE` 指定 Git 版本。Git 2.56.1 全量 461/461、Git 2.45.1 定向 53/53 通过，Release 构建 0 警告 0 错误，自包含发布与安装包构建通过。
- 安装包 SHA-256 更新为 `701DB7868A1C53766627F2C170BE9962C565A17D3B5E2FCF8F650B76CC9922E5`；复现与验证日志位于 `artifacts/logs/git256-*.log` 和 `git245-remote-tests.log`。

## 2026-11-15 · 下一阶段批次 1：清理剩余无调用实现

- 前一批提交：`4f1b2e0a`（Git 2.56.1 推送兼容修复）。
- 复核全部源码、XAML、JSON 源生成与测试引用后，删除无调用的 `src/Commands/Am.cs`、`src/Commands/Clean.cs` 与 `src/Models/CleanMode.cs`（已送入系统回收站）；保留 `Models.GitInProgressKind.Am` 用于外部补丁应用状态只读识别。
- 清理 `src/Commands/Worktree.cs` 中无调用的 `AddAsync`、`PruneAsync`、`RemoveAsync`，保留 `ReadAllAsync`、`LockAsync`、`UnlockAsync`；工作树删除与失效记录清理继续走 `WorktreeDeletionService`。
- 清理 `src/Commands/Submodule.cs` 中无调用的 `AddAsync`、`SetURLAsync`、`DeleteAsync`，保留供克隆与子模块更新使用的 `UpdateAsync`。
- 在 `tests/GitNest.IntegrationTests/NextStageTests.cs` 补充裁剪类型/方法断言及真实 Git 克隆初始化子模块测试。
- 验证：Release 构建 0 警告 0 错误；`.\build\scripts\test-gitnest.ps1` 全量 **472 / 472** 通过（含工作树查询/删除/失效清理及克隆初始化子模块）。

## 2026-11-15 · 下一阶段批次 2：移除 Rebase/多分支合并/Assume-unchanged 设置入口并收口外部只读保护

- 前一批提交：`79575e89`（下一阶段批次 1：清理剩余无调用实现）。
- 删除 9 个文件（`Commands/Rebase.cs`、`Commands/Replay.cs`、`Models/MergeStrategy.cs`、`ViewModels/Rebase.cs`、`ViewModels/MergeMultiple.cs`、`Views/Rebase.axaml(.cs)`、`Views/MergeMultiple.axaml(.cs)`）；清理 `BranchTree`、`Histories`、`WorkingCopy`、`Repository`、`Commands/Merge.cs`、`Commands/Command.cs`、`Models/GitVersions.cs` 与 `Icons.axaml` 中的 Rebase、多分支合并与“假设未修改”设置入口，仅保留历史 `AssumeUnchanged` 查看与清除能力（新增 `Text.AssumeUnchanged.Tip`、`Text.AssumeUnchanged.RemoveTip`）。
- 修复 `MergeRevertService` 对多行命令日志中 `--no-ff --no-commit` 的识别，区分 GitNest `MergeMode.DontCommit`（启用“下一步”/禁用修补、显示 `Text.WorkingCopy.InProgress.DontCommitOrigin`）与外部 `git merge --no-commit`；清除 `ViewModels/InProgressContexts.cs` 中任何 `Directory.Delete`，补充外部 `.git/rebase-merge`、无 `applying` 标记的 `.git/rebase-apply` 与 `.git/BISECT_LOG`（`BisectInProgress`）只读识别、`Text.WorkingCopy.ExternalInProgressGuide` / `Blocked` 及异步所有权刷新同步等待（`IsRefreshingInProgressOwnership`）。
- 优化 `WorkingCopy.axaml(.cs)` 与 `Conflict.axaml` 布局与主题适配：将外部操作提示纳入全宽提示栈并修正 `HasInlineNoticeArea` 通知联动，下调工作区列 `MinWidth` 以消除 `860px` 窄窗右侧截断，并将冲突标记与来源徽章前景改为 `Brush.Conflict.Foreground` 修复深色主题浅底白字问题；同时为 `build/scripts/test-gitnest.ps1` 与 `tests/GitNest.IntegrationTests` 增加 `-Suite` / `-List` / `-NoBuild` 分模块执行参数（默认保持全量）。
- 末次改动后验证：`en_US.axaml` 与 `zh_CN.axaml` 各 `1402` 项对称（`0` 差异/重复）；`build-gitnest.ps1` **0** 警告 **0** 错误；`-Suite core,next-stage,merge-revert,commit-sync` 定向集成检查 **155 / 155** 通过（在最终 XAML 通知微调与拆分参数前全量 `test-gitnest.ps1` **528 / 528** 通过）；UI 探针 `-MergeRevert` **19 / 19**、`-TrimSurface` **32 / 32**、`-CommitSync` **11 / 11** 全部通过，并核对 `artifacts/probes/phase-3.4/` 下 `DontCommit`、`rebase --merge`（浅色与 `860px` 深色窄屏）及 `BISECT_LOG` 截图无裁剪重叠。

## 2026-11-15 · 下一阶段批次 3：完整移除提交模板并增加旧配置迁移备份

- 前一批提交：`7f6e0022`（下一阶段批次 2：移除 Rebase/多分支合并/Assume-unchanged 设置入口并收口外部只读保护）。
- 完整删除提交模板功能与模板引擎：将 `src/Models/CommitTemplate.cs` 与 `src/Models/TemplateEngine.cs` 移入系统回收站；移除 `WorkingCopy.ApplyCommitMessageTemplate`、`RepositoryConfigure` 模板页签及增删绑定、`RepositorySettings.CommitTemplates`，同步清理不再引用的 `Icons.Code` 与 6 条专用中英词条。
- 精简 `Views/CommitMessageToolBox.axaml.cs` 的提交说明辅助菜单（同步提取为 `CreateCommitMessagePickerMenu`），移除自定义模板及 Git `commit.template` 导入项并更新 `Text.WorkingCopy.CommitMessageHelper`（`最近提交信息` / `Recent Commit Messages`），完整保留最近提交说明列表、清空操作与规范化提交生成器；保持仓库 `git config commit.template` 与模板文件原样。
- 为 `Models/RepositorySettings.cs` 补齐兼容迁移逻辑：检测到 `gitnest.settings` 含旧 `CommitTemplates` 字段时，纯读取保持原文件不落盘、不加载不执行；首次触发写入（`Save()`）前自动创建原文件备份（`gitnest.settings.bak`，若已存在则递进 `.legacy-N.bak` 防止覆盖），写出精简配置并继续保留 `DefaultRemote`、`PreferredMergeMode`、`ConventionalTypesOverride`、`UnprotectedBranches` 等其余设置；改用同步计算初始哈希并补 `ClearCacheForRepository` 供重开验证。
- 在 `tests/GitNest.IntegrationTests/NextStageTests.cs` 新增 `next-stage-3` 用例，并同步更新 `TrimmedSurfaceProbe.cs`（`RepositoryConfigure` 页签断言由 `3` 调整为 `2` 且校验菜单不再呈现模板/导入项）。
- 末次改动后验证：`en_US.axaml` 与 `zh_CN.axaml` 各 `1396` 项对称（`0` 差异/重复）；`build-gitnest.ps1` **0** 警告 **0** 错误；`-Suite core,next-stage-3,next-stage-4,config,stash-discard,history-reset,phase41` 定向集成检查 **180 / 180** 通过；UI 探针 `-TrimSurface` **34 / 34**（核对 `trim-config-tabs-light.png` 与 `680px` 深色窄窗 `trim-config-tabs-dark-narrow.png`）、`-Config` **13 / 13**、`-ConfigRefresh` **4 / 4**、`-CommitSync` **11 / 11** 全部通过。

## 2026-11-15 · 下一阶段批次 4：收窄 LFS 管理边界并重做子模块切换确认

- 前一批提交：`8b5fee6c`（下一阶段批次 3：完整移除提交模板并增加旧配置迁移备份）。
- 删除 7 个 LFS 锁与清理界面／模型文件（已移入系统回收站）：`Models/LFSLock.cs`、`ViewModels/LFSLocks.cs`、`ViewModels/LFSPrune.cs`、`Views/LFSLocks.axaml(.cs)` 与 `Views/LFSPrune.axaml(.cs)`；清理 `App.JsonCodeGen.cs` 的 `LFSLock` 序列化项、`Commands/LFS.cs` 的 `PruneAsync`/`GetLocksAsync`/`LockAsync`/`UnlockAsync`/`UnlockMultipleAsync`、`ViewModels/Repository.cs` 的 `LockLFSFileAsync`/`UnlockLFSFileAsync`，以及 `WorkingCopy`、`RevisionFileTreeView`、`CommitDetail` 中的锁右键入口。完整保留 `InstallAsync`、`TrackAsync`、`FetchAsync`、`PullAsync`、`PushAsync` 与 LFS 指针／预览支持（`Commands.Diff`、`LFSImageDiff`、`RevisionLFSImage`），并将 `Views/RepositoryToolbar.axaml.cs` 的 LFS 菜单收口为状态提示加 `Text.GitLFS.Advanced`（高级 LFS 操作）子菜单。
- 移除子模块切换隐式自动更新并改为显式二次确认：移除 `Models/RepositorySettings.cs`、`ViewModels/RepositoryConfigure.cs` 与 `Views/RepositoryConfigure.axaml` 的自动更新子模块与自动递归配置项（纳入 `IsLegacySettingKey` 旧配置迁移兼容），替换 `Repository.AutoUpdateSubmodulesAsync` 与 `Reset.cs` 调用为只刷新状态的 `MarkSubmodulesDirtyAfterResetOrSwitch()` / `MarkSubmodulesDirtyManually()`。
- 基于共享 `BranchSwitchPopup`、`BranchSwitchSubmoduleOptionsView.axaml(.cs)` 与 `BranchSwitchResultView.axaml` 统一 `Checkout`、`CheckoutAndFastForward`、`CheckoutDetached` 与 `CreateBranch` 的切换流程：`UpdateSubmodulesAfterSwitch` 默认关闭且永不自动更新；仅在用户主动勾选且存在可更新或未初始化子模块（`QueryUpdatableSubmodules`）时，主分支切换后暂停于二次确认（`IsAwaitingSubmoduleConfirm`），列出受影响路径与 `--init`/递归提示；提取 `Command.ExecDetailedAsync()` 与 `Submodule.UpdateDetailedAsync()`，子模块更新失败时保持主分支新位置、分离展示绿色主分支结果与红色子模块失败详情并刷新真实状态。同步修复 `ViewModels/Repository.cs` 中空 `CurrentBranch` 与无界面测试线程派发防护，以及 `GitNest.IntegrationTests/Program.cs` 中 `BasicDirectories` 隔离与临时目录重试清理。
- 在 `tests/GitNest.IntegrationTests/NextStageTests.cs` 新增 `next-stage-4` 用例并扩展 `TrimmedSurfaceProbe.cs`。末次改动后验证：`en_US.axaml` 与 `zh_CN.axaml` 各 `1398` 项对称（`uniqueDiff=0`）；`build-gitnest.ps1` **0** 警告 **0** 错误；`-Suite core,next-stage-3,next-stage-4,config,stash-discard,history-reset,phase41 -NoBuild` 定向集成检查 **272 / 272** 通过；UI 探针 `-TrimSurface` **49 / 49**（核对 `artifacts/probes/phase-4.1/` 下子模块默认开关浅色、`600px` 深色窄窗二次确认及部分失败分栏截图）、`-StashDiscardIgnore` **15 / 15**、`-ResetRewrite` **19 / 19**、`-Config` **13 / 13** 全部通过。
