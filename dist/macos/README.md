# macOS 正式分发目录

Mac 源码及原生 Xcode 工程位于 [`platforms/macos`](../../platforms/macos/README.md)。开发者从 Git 检出完整仓库后在 Mac 上打开 `Boardcast.xcodeproj`；无需下载源码 ZIP，也不依赖云端 CI、第三方包管理器或工程生成器。

本目录只接收经过签名、公证及完整校验的正式安装包：

- `Boardcast-macOS-universal-<版本>-<构建号>.dmg`：同一个应用支持 Apple Silicon 与 Intel，应用内包含签名的 `BoardcastUpdater`。
- 同名 `.dmg.sha256`：最终磁盘镜像的 SHA-256 校验值。

本地测试及发布准备产物留在 Git 忽略的 `platforms/macos/.build/` 与 `platforms/macos/dist/`。`release.sh --prepare` 生成的 `NOT-NOTARIZED.dmg` 不能进入本目录。正式 `release.sh --notarize` 会先公证并装订应用票据，再生成、签名、公证并装订 DMG；只有全部验证通过才输出正式校验文件。凭据及具体命令见 [Mac 开发与发布说明](../../platforms/macos/DEVELOPMENT.md)。

在 Mac 的 `platforms/macos` 目录显式执行以下命令，验证最终 DMG、应用与更新程序的签名身份、通用架构、公证票据及校验值后导出：

```bash
DEVELOPMENT_TEAM=YOURTEAMID bash scripts/publish.sh \
  /absolute/path/Boardcast-macOS-universal-0.1.0-1.dmg
```

导出脚本拒绝覆盖现有安装包及校验文件，也不会执行 Git 提交、推送或上传。本目录的 DMG 使用仓库 `.gitattributes` 中的 Git LFS 规则；发布者审核产物后自行提交和分发。

用户下载 DMG 后，用 Finder 将 `Boardcast.app` 拖入当前用户的 `~/Applications`（目录不存在时先创建），然后从复制后的目录启动。当前账户有写权限时也可选择 `/Applications`。不要从只读磁盘镜像内运行；应用及更新程序保持在同一个 `.app` 内，后续局域网更新使用经过验证的更高版本正式通用安装包。
