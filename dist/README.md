# 正式分发产物

本目录仅存放**当前正式版本**的对外安装包与校验文件。Windows ZIP 与验收报告在本目录根下；Mac 正式 DMG 在 [`macos/`](macos/README.md)。本地/开发构建写入被忽略的 `artifacts/`（Windows）与 `artifacts/macos/`（Mac），不要把本地产物放进本目录。

## 当前签名包

| 版本 | 完整包 | 校验 |
|------|--------|------|
| **0.2.8** | [Boardcast-win-x64-0.2.8.zip](Boardcast-win-x64-0.2.8.zip) | [SHA-256](Boardcast-win-x64-0.2.8.zip.sha256) |

公钥记录：[publisher-public-key.json](publisher-public-key.json)。签名与保管说明见 [docs/signed-release.md](../docs/signed-release.md)。

源码版本见根目录 `Directory.Build.props`（当前为 **0.2.8**），与本目录签名包一致。

## 终端用户安装（无需 .NET）

1. 安装 [Git LFS](https://git-lfs.com/)，再克隆或拉取本仓库，否则 ZIP 可能只是指针文件。
2. 下载 `Boardcast-win-x64-0.2.8.zip`，用旁边的 `.sha256` 校验。
3. 退出旧版 Boardcast 及后台，将 ZIP **完整解压**到干净、可写目录。
4. 运行 `Boardcast.exe`。不要只复制单个 EXE；包内含共享运行时。

最终用户**不必**安装 .NET：正式包为自包含发布。开发构建才需要 .NET 10 SDK，见根 [README.md](../README.md)。

## 开发者本地产物

日常 `Publish-Windows.ps1` 默认输出到被忽略的 `artifacts/`。验收通过后，将最终 ZIP / `.sha256` / 验收 JSON 复制到本目录并纳入 Git LFS，替换旧版正式包；勿提交解压目录、历史旧版或 `*.next-*` 候选包。
