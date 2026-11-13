# 远程仓库与协作

仓库：[MOAKIEE/GitNest](https://github.com/MOAKIEE/GitNest)，私有仓库，开发分支为 `main`。

## 克隆与依赖

```powershell
git clone --recurse-submodules https://github.com/MOAKIEE/GitNest.git
cd GitNest
```

AvaloniaEdit 作为子模块固定在 `ddf00a91dc0da29b85c79b85c9583860213a99ff`。子模块显示提交号表示按固定版本检出，构建脚本会初始化该版本。更新依赖时单独核对和提交子模块指针。

## 远端与提交

- `origin`：`https://github.com/MOAKIEE/GitNest.git`，GitNest 的提交和推送目标。
- `upstream`：`https://github.com/sourcegit-scm/sourcegit.git`，用于核对及获取底座更新。
- `main` 跟踪 `origin/main`，本地默认推送远端设为 `origin`，推送模式为 `simple`。
- 本地已补齐 SourceGit 完整祖先历史，保留原有导入关系。
- 本仓库提交邮箱使用 GitHub noreply 地址；首次推送前调整的提交号见 [提交映射](commit-email-migration.md)。

每批完成对应验证、更新实施记录后提交。推送当前开发分支：

```powershell
git push origin main
```

## Windows CI

`.github/workflows/ci.yml` 在 `main` 的代码推送、合并请求和手动触发时执行：

1. 检出完整历史及固定子模块。
2. 按 `global.json` 安装 SDK。
3. 运行 Windows Release 构建与 x64 自包含发布。
4. 运行集成检查。
5. 上传日志和 `GitNest-windows-x64` 应用目录，保留 7 天。

仅修改文档时跳过自动 CI；工作流权限为仓库内容只读。安装器与正式发行包按实施计划阶段四接入。
