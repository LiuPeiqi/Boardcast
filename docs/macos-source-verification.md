# macOS 源码交付与静态检查记录

日期：**2026-10-10**。交付版本：**Mac 0.1.0 / build 1**。本轮在 Windows 本地编写原生 Xcode 工程、Swift/SwiftUI 源码、资源、XCTest 与本地构建/发布脚本，包含基础 Mac–Windows legacy 文件互传，以及追加要求的 **Mac 局域网更新、内嵌 updater 和单一 universal DMG**。

交付源码由用户自行 Git push，Mac clone/pull 同一分支或提交，不需要源码交接 ZIP。**尚未执行 Mac 编译、XCTest、app/helper 运行、正式签名、公证上传、DMG 安装或双机更新。** 当前没有本轮编译出的 Mac 可运行二进制，静态检查不能代替上述验收。

## 已执行的检查

| 检查 | 实际结果与边界 |
| --- | --- |
| 原生工程结构 | `scripts/validate_project.py` 通过：Boardcast、BoardcastUpdater、BoardcastTests 三个 target；app 的 23 个编译文件（22 app + 1 Shared）、helper 的 2 个（main + 同一 Shared）、13 个 XCTest 文件全部且唯一加入对应 target；共享 scheme/workspace/plist/config 引用有效 |
| 工程资源与配置 | 主 bundle 的 Icons、ThirdParty、英文/简中 UI 与复数规则、权限用途说明直接装配；AppIcon 十个槽位匹配 PNG 尺寸；app/helper Release 双架构、helper 依赖与 CodeSignOnCopy、唯一版本/bundle 配置；无远程包、生成器或脚本构建阶段 |
| Swift 静态语法 | tree-sitter Swift 解析 24 份不同生产源码、13 个测试文件和 Package.swift，共 38 个文件，零 ERROR/missing 节点；解析不检查 Apple SDK 类型、API availability、链接、签名或并发运行行为 |
| Shell 语法 | `common.sh`、`preflight.sh`、`build.sh`、`release.sh`、`publish.sh` 全部 `bash -n` 通过，保持 LF；没有执行 Mac 构建、签名或公证命令 |
| 图标和许可 | 35 个共享 PNG 与 catalog、既有导出资产逐字节一致，MIT 许可一致；预览专用 Mac 蓝色目录图不进入产品 |
| 本地化 | 英文/简中各 452 个键、各 5 个复数规则；键集合、格式占位符及数值格式检查通过，复数 plist 可解析 |
| 文档与改动格式 | Mac 工程与本轮文档的 scoped `git diff --check` 通过；四份交接文档本地文件链接有效、UTF-8 无 BOM |

源目录物理上包含 22 个 app Swift 文件、1 个 Shared、1 个 updater；Shared 被两个 Xcode target 编译，但只有一份源码。SwiftPM 编译 app/Shared，排除 updater，不装配正式 app 或 DMG。

静态解析的 Python/tree-sitter 只在忽略的 `artifacts` 中使用，不属于 app、Xcode 或本地构建脚本依赖。可选结构 validator 只用 Python 标准库。普通用户安装、运行或 LAN 更新不要求 Xcode/Command Line Tools；运行期通过系统 Security 选择和验证架构。

## 已编写但未执行的测试

13 个 XCTest 文件含 **115 个 `test…` 方法**，已编写、未执行。覆盖兼容解码、分组/规则与限额、设置草稿冲突、扫描取消/排除、符号链接/特殊文件、资源、发现元数据、证书和指纹、网络取消、逐文件安全保存、冲突/部分完成、回执与历史中断；新增真实 Windows legacy 帧与公开 DER fixture，以及 Mac 更新版本/runtime/镜像清单、挂载 plist、私有缓存、安装路径、系统替换和备份恢复夹具。

这是测试源码数量，**不能记录为测试运行通过**。当前环境没有 macOS SDK 与 Apple 运行时；Swift 类型检查、TLS/Keychain、Darwin/Foundation 文件操作、SwiftUI、Gatekeeper 和实际更新都须在 Mac 执行。

## Mac 上的下一步

保留仓库目录结构，完整 Xcode **16 或以上**打开 `platforms/macos/Boardcast.xcodeproj`，选择 **Boardcast / My Mac**，先 Cmd+U 再 Cmd+R；或在 `platforms/macos` 执行：

```sh
bash scripts/preflight.sh
bash scripts/build.sh
```

完整 [工具链和官方语言规则](macos-build-rules.md)、[工程与发布入口](../platforms/macos/README.md)、[实机验收矩阵](../platforms/macos/DEVELOPMENT.md) 已提供。保留 revision、工具链与签名身份、Xcode 诊断和 xcresult，完成 macOS 13/15+、Intel/Apple Silicon、权限/文件系统、Windows 0.2.4/0.2.7 双向基础互传后再记录通过。

正式更新验收需要两个已安装、已公证且同 bundle/Developer ID Team 的不同 Mac 营销版本：将 DMG 内 app 拖入 `~/Applications/Boardcast.app`，弹出映像再启动；已有写权限时可使用 `/Applications/Boardcast.app`。不能用 ad-hoc build、只修改版本号、Downloads/DMG/DerivedData/AppTranslocation 中运行的 app 代替。新版 Mac 分享当前发行 app，旧版明确选择、下载、验证和安装；另验双架构交叉、下载中断、并发任务、启动确认、备份恢复与离线票据。

正式发布在 Mac 本机使用有效 Developer ID/runtime/timestamp。只有显式 `release.sh --notarize` 才上传 Apple：先 app 公证 Accepted/log/staple，再 DMG 公证 Accepted/log/staple 与 Gatekeeper，最终一个 universal DMG 和 SHA-256。`--prepare` 不上传公证，但 secure timestamp 可联网。验收后的最终 DMG 通过 `publish.sh` 明确验证导出仓库根 `dist/macos/`，拒覆盖，不自动 Git 提交/上传；DMG 已配置 Git LFS。此轮仅提供这些脚本，没有执行发布。

chunk 缺块恢复/重启续传、远端文件取消、P2P 内容分发、Finder 分享扩展、Quick Look 与 CLI/MCP 留待后续版本；Mac LAN 更新已写入本轮源码。未声明尚未实现的能力，任务历史不代表可续传。后续范围与剩余迁移项见 [实现状态表](macos-design.md#当前实现与迁移差距)。
