# macOS 编译、语言与发布规则

核查日期：**2026-10-10**。本文件把 Apple/Swift 官方规则与本仓库配置对应起来，供 Mac 本地 Xcode 构建和验收使用。示例遵循当前 Swift 5 language mode / macOS 13 deployment target；官网最新页面可能展示较新 SDK 的 overload 或特性，新增调用仍须以所选 Xcode 的 SDK availability 和编译诊断为准。此次未执行 Mac 编译或 Apple 公证上传。

## 工具链、target 和构建设置

| 项目 | 本工程选择 | 核查依据 |
| --- | --- | --- |
| app 构建入口 | `platforms/macos/Boardcast.xcodeproj`，共享 scheme `Boardcast` | app、内嵌 BoardcastUpdater 与 hosted XCTest 三个 target；资源与签名由 Xcode 装配 |
| 最低构建工具 | 完整 Xcode 16；较新版本须运行于兼容的主机系统 | Xcode 16 携带 Swift 6.0 编译器并支持 Swift 5 language mode；主机最低 macOS 14.5 |
| 最低运行系统 | `MACOSX_DEPLOYMENT_TARGET = 13.0`；SwiftPM `.macOS(.v13)` | deployment target 与安装 Xcode 的主机最低系统分开声明 |
| Swift 语言模式 | Xcode `SWIFT_VERSION = 5.0`；Package `swiftLanguageVersions: [.v5]` | 编译器版本与 language mode 分开，不把工具升级当作自动迁移 Swift 6 |
| Package manifest | `// swift-tools-version: 5.9`，`defaultLocalization: "en"` | manifest 只使用该 tools 版本支持的 PackageDescription API |
| 依赖 | 系统 SDK 框架，无外部包 | app 与 helper 直接编译共用的 `Sources/Shared` 验证代码；SwiftPM 使用同一 app/Shared 源码 |
| 身份与版本 | `Config/Base.xcconfig` 定义 bundle ID、版本、build | `Info.plist` 由 Xcode 展开变量，发布脚本读同一配置 |
| 架构 | Debug 只构建当前架构；Release arm64 + x86_64 | `ONLY_ACTIVE_ARCH` 分别 YES/NO，不设置已弃用的 `VALID_ARCHS` |

以上工具链与配置规则见 [Xcode 系统与 SDK 对照表](https://developer.apple.com/xcode/system-requirements/)、[Xcode build settings](https://developer.apple.com/documentation/xcode/build-settings-reference)、[PackageDescription](https://docs.swift.org/package-manager/PackageDescription/PackageDescription.html)。

Apple 推荐 Standard Architectures 随 SDK 演进；本工程 Release 明确锁定当前要求的 arm64/x86_64，并检查实际 Mach-O 的两种 slice。用 `lipo -archs Boardcast.app/Contents/MacOS/Boardcast` 验证实际二进制。Intel 不能执行 arm64；Apple Silicon 上的 x86_64 需 Rosetta，仍要安排 Intel 原生实机验收。[通用二进制规则](https://developer.apple.com/documentation/apple-silicon/building-a-universal-macos-binary)、[TN3117 架构设置](https://developer.apple.com/documentation/technotes/tn3117-resolving-build-errors-for-apple-silicon)

源码通过 Git 获取与同步，不需要源码交接 ZIP。本地脚本不自动下载 Swift 包、不依赖 CI。安装后的 app 与 updater 不要求 Xcode 或 Command Line Tools；`lipo` 仅用于开发者构建/发布检查，运行期的两架构签名验证使用系统 Security API。完整 Xcode 的 `xcodebuild`、`xcrun`、`codesign`、`ditto`、`plutil`、`lipo`、`security`，系统 `hdiutil`、`spctl`、`shasum` 与 macOS Bash 3.2 是脚本使用的本机工具。Python 结构检查属于可选离线开发检查，不是编译 app 的条件。

## Swift 语法与状态所有权

结构体需要默认和 memberwise 初始化器，同时又要实现兼容解码时，将自定义 `init(from:)` 放在 extension；若在原结构体主体中自定义初始化器，会改变自动初始化器可用性。模型的必需身份字段继续严格解码，新增可选字段提供明确缺省值。[Swift 初始化规则](https://docs.swift.org/swift-book/documentation/the-swift-programming-language/initialization/)

`decodeIfPresent` 在键缺失或值为 null 时返回 nil，在类型不符时仍抛错；需要区分缺失与显式 null 时使用 `contains` 等检查。属性的 Swift 默认值不能替代所需的 wire 兼容策略。[Apple 解码语义](https://developer.apple.com/documentation/swift/keyeddecodingcontainer/decodeifpresent(_:forkey:)-1zmt1)

不可靠的端口、证书包装、URL 和 Keychain 结果用 `guard let` 或 `if let` 解包；强制解包 nil 是运行时错误。通过 `UInt16(exactly:)` 表达可失败的端口范围转换。字节和大小累计用 `Int64` 与 `addingReportingOverflow`，先拒绝负数并判断 overflow，再使用 partialValue。[Swift Optional](https://docs.swift.org/swift-book/documentation/the-swift-programming-language/optionalchaining/)、[溢出检查](https://docs.swift.org/latest/documentation/swift/fixedwidthinteger/addingreportingoverflow(_:)/)

UI、设置、发送草稿和任务列表由 `@MainActor` 所有；网络回调发布 UI 时显式回到主 actor。扫描在后台完成，以值类型结果回写；扫描取消和项目移除后旧结果不能写回。跨并发域传值符合 `Sendable`。使用 `@unchecked Sendable` 必须实际用 actor、锁或串行队列保护全部共享状态，并注释所有权理由，不能把标注当作压制警告的工具。[Swift Sendable](https://docs.swift.org/latest/documentation/swift/sendable/)

当前工程并发检查为 `SWIFT_STRICT_CONCURRENCY = targeted`。Mac 上可额外诊断：

```sh
xcodebuild -project Boardcast.xcodeproj -scheme Boardcast \
  -configuration Debug -destination 'platform=macOS' \
  SWIFT_STRICT_CONCURRENCY=complete CODE_SIGN_IDENTITY=- build
swift test -Xswiftc -strict-concurrency=complete
```

Swift 5 的 complete 模式可用于完整诊断；Swift 6 language mode 已强制启用完整检查。切换 language mode、manifest tools 与 feature flags 应一起审核，不重复开启 Swift 6 已内置的 `StrictConcurrency`，也不把最新 `swiftLanguageModes` API 直接塞入 tools 5.9 manifest。[Swift 6 官方迁移规则](https://www.swift.org/migration/documentation/swift-6-concurrency-migration-guide/enabledataracesafety/)

回调转 async 的 `CheckedContinuation` 每条路径必须恰好 resume 一次。成功、错误、超时、取消共同经过唯一完成入口；未恢复会悬挂和泄漏，重复恢复可崩溃。取消本机任务需同时结束网络等待与审批等待，不能只取消 UI 状态。[Apple continuation 规则](https://developer.apple.com/documentation/swift/checkedcontinuation)

## 系统 API 与最低系统兼容

本工程依赖的框架均随 macOS SDK 和系统提供，不单独下载安装。

| 系统职责 | 本工程 API | macOS 13 边界 |
| --- | --- | --- |
| 主窗口和菜单栏 | SwiftUI `Window`、`openWindow(id:)`、`MenuBarExtra` | 使用唯一 main scene ID，不猜测 `NSApp.windows.first` |
| 分栏与文件行 | `NavigationSplitView`、`Layout` | 以系统 View/Layout 测量与布局，不另做文本像素估计框架 |
| 设置入口 | `Settings`；macOS 14+ `SettingsLink` | `#available(macOS 14.0, *)` 后使用新入口，13 使用兼容设置动作 |
| 登录启动 | `SMAppService.mainApp` | macOS 13+ 直接注册主 app，不另建登录 helper 或 daemon |
| 文件选择与 Finder | `NSOpenPanel`、`NSWorkspace.activateFileViewerSelecting` | 系统面板选择内容或目录，系统 Finder 显示文件 |
| 文件夹图标 | `NSWorkspace.icon(forFile:)` / `icon(for: .folder)` | 本地普通目录用授权实际路径；远端清单不探测路径 |
| TLS 与身份 | Network、Security、CryptoKit | 系统 TLS、Keychain、SHA-256；自签证书身份仍严格核验 |
| 物理接口 | SystemConfiguration 与 Darwin socket | 识别真实 Ethernet/Wi-Fi 类型，不假定接口名固定为 en0 |
| 安全落盘 | Darwin `openat`、`mkdirat`、`renameatx_np` | 相对已打开目录句柄操作；独占提交失败要报告，不覆盖 |

SwiftUI 场景与窗口规则见 [Apple Windows](https://developer.apple.com/documentation/swiftui/windows)；新 API 的发布范围见 [SwiftUI updates](https://developer.apple.com/documentation/updates/swiftui)。登录项 `register()` 受系统批准约束；读取真实 `status`，分别表达 enabled、requiresApproval、notRegistered、notFound 和错误，并提供系统登录项设置入口。[SMAppService](https://developer.apple.com/documentation/servicemanagement/smappservice)、[系统状态](https://developer.apple.com/documentation/servicemanagement/smappservice/status-swift.enum)

主 app 是当前登录用户的应用。关主窗继续服务、Cmd+Q 终止服务、登录项启动尽量静默，行为由 app 生命周期实现。自动启动不意味着注销后继续运行，也不能通过安装系统 daemon 绕过权限。

`renameatx_np` 是 Darwin 的安全保存 API，`RENAME_EXCL` 依赖目标卷支持的独占重命名语义。原子无覆盖提交与普通“先检查存在再移动”不同；跨卷需单独处理复制与提交，符号链接防护需检查每级目录句柄。卷移除、空间不足、SMB/外置卷不支持该操作等情况必须实机测试。[Apple APFS API](https://developer.apple.com/library/archive/documentation/FileManagement/Conceptual/APFS_Guide/ToolsandAPIs/ToolsandAPIs.html)、[目标卷独占 rename 支持](https://developer.apple.com/documentation/foundation/urlresourcevalues/volumesupportsexclusiverenaming)

## 网络、身份和权限

Network.framework 负责 TLS 与连接行为。服务端 peer authentication 默认与客户端不同，双方认证必须显式要求；TLS local identity 的创建返回失败时应终止初始化。证书 pin 为证书 DER 的 SHA-256，握手证书身份再与 hello/offer 核对。信任不能从设备名、地址或同组关系继承。[Apple TLS security options](https://developer.apple.com/documentation/network/security-options)、[要求对方认证](https://developer.apple.com/documentation/security/sec_protocol_options_set_peer_authentication_required(_:_:))

`NWConnection.receive` 每次调用只交付一次回调；错误或流关闭可能返回无数据或不足量。`isComplete` 不是“达到了我要求的固定长度”。帧头、JSON 与文件块必须累计到精确长度，识别 EOF/错误，取消时结束未完成读写。JSON 帧先检查 4 MiB 上限再分配，legacy 文件以有界块传送。[Apple receive 语义](https://developer.apple.com/documentation/network/nwconnection/receive(minimumincompletelength:maximumlength:completion:))

macOS **15** 开始支持本地网络隐私。app 声明真实 `NSLocalNetworkUsageDescription`；只有实际浏览/注册 Bonjour 时才列 `NSBonjourServices`。现有 IPv4 UDP 广播不需要伪造 Bonjour 条目，macOS 不要求 iOS 的 multicast entitlement。Terminal/SSH 中的命令行程序及子进程自动允许，因此不能用 `swift run` 验收 app 的权限。空设备列表不等于权限拒绝；允许/拒绝/系统设置恢复要在 Finder 启动的 app 与稳定签名身份上核验。[Apple TN3179](https://developer.apple.com/documentation/technotes/tn3179-understanding-local-network-privacy)

当前直接分发 app 没有启用 App Sandbox；Hardened Runtime 是独立机制。此版本不需要为目录预先建立 security-scoped bookmark 框架；仍要处理系统受保护目录、Keychain 和网络权限。未来真正启用沙箱后，持久目录访问才使用安全作用域书签，并配对 start/stop、处理失效与 stale 更新。[Apple 沙箱文件访问](https://developer.apple.com/documentation/security/accessing-files-from-the-macos-app-sandbox)

## SwiftPM 与原生 app 的资源组合

app 源目录为 `Sources/Boardcast`。SwiftPM target 以 `Sources` 为根，编译 `Boardcast` 与 `Shared`，排除 `BoardcastUpdater`；它不装配 helper 或正式 app。资源路径相对 target 根：对 `Boardcast/Resources/Icons` 与 `Boardcast/Resources/ThirdParty` 使用 `.copy` 保留目录，对 `Boardcast/Resources/en.lproj` 与 `Boardcast/Resources/zh-Hans.lproj` 使用 `.process`。不能把 Icons 改为递归 process 后仍假定文件在 Icons 子目录：没有特殊处理的 process 文件会复制到资源 bundle 顶层。[Apple package 资源规则](https://developer.apple.com/documentation/xcode/bundling-resources-with-a-swift-package)

有资源的 package target 自动提供 `Bundle.module`；原生 app target 不提供该 accessor。本工程 `ResourceBundle` 在 `#if SWIFT_PACKAGE` 使用 `.module`，在 Xcode app 中直接使用 `Bundle.main`；所有类型图标、catalog、许可和 UI 字符串都通过这个入口访问，不依赖仓库工作目录。Xcode 将 Icons、ThirdParty 放入 `Contents/Resources/` 对应子目录，将本地化字符串装配到主 bundle 的 `.lproj`，并把 `Resources/Assets.xcassets` 交给 asset compiler，不能只复制原始 catalog。资源和代码按 [Apple bundle 布局规则](https://developer.apple.com/documentation/bundleresources/placing-content-in-a-bundle) 放置，不把普通数据目录假设为独立 bundle。

Package 声明 `defaultLocalization: "en"`；语言目录使用 `en.lproj` 与 `zh-Hans.lproj`，其中没有嵌套子目录。UI 的 `Localizable.strings` 属于共享资源；主 app 的本地网络用途 `InfoPlist.strings` 属于 `platforms/macos/Resources/<语言>.lproj`。避免把用途说明只放在子资源 bundle，导致系统权限弹窗无法获取本地化。[Apple package 本地化](https://developer.apple.com/documentation/xcode/localizing-package-resources)

Mac AppIcon 使用各尺寸资产并在 app target 指定 AppIcon set；系统编译后检查 Dock/Finder 实际显示。图标来源与许可随共享资源复制，文件类型保留 original 色彩，平台标记 template，普通目录使用系统图标。缺资源有本地 fallback，但 fallback 不能算资源验收通过。[Apple app icon 配置](https://developer.apple.com/documentation/xcode/configuring-your-app-icon)

复数字符串使用同名 `.stringsdict`，由系统按语言选取数量类别；`other` 必需，英文包含 `one` 和 `other`，简中使用 `other`。格式化的数值类型与调用处一致，不自行按数量拼接单复数。[Apple 复数规则](https://developer.apple.com/documentation/xcode/localizing-strings-that-contain-plurals)

## Mac 局域网更新与安装边界

局域网设备身份由 mTLS 证书 DER 指纹识别，发行身份由 Apple Developer ID 签名识别，两种身份分别核验。附近设备公布的版本、端口、大小和 SHA-256 只供来源选择与下载完整性检查，不能授予安装权。候选 app 必须符合本机已安装发行 app 的 bundle ID、Developer ID Team ID 与 `osx-universal` runtime，是严格更高的三段 marketing version，且主程序和 BoardcastUpdater 都含 arm64/x86_64。

`SecStaticCodeCheckValidity` 必须带完整、严格和全部架构验证，并结合严格的 `SecRequirement`。仅使用 `kSecCSCheckAllArchitectures` 不足以锁定发行者：Apple 明确指出不同 slice 可由不同 signer 签署，甚至含 ad-hoc slice。签名要求必须限制 Apple Developer ID 与正确 Team/app/helper 标识，而不是只读取当前运行架构的 signing information。签名检查之后仍需 Gatekeeper 公证验收，不能把有效签名等同于已公证。[Apple 静态验证规则](https://developer.apple.com/documentation/security/secstaticcodecheckvalidity(_:_:_:))、[验证 flags](https://developer.apple.com/documentation/security/static-code-validation-flags)

下载 DMG 后用系统 `hdiutil` 只读挂载，读取机器可解析的 plist 挂载结果，不解析易变的界面文字；只复制预期的 Boardcast.app，在本机私有 staging 再次验证。Apple DTS 确认该工具的 `-plist` 适合程序调用。[Apple hdiutil 说明](https://developer.apple.com/forums/thread/721232)

应用内明确安装入口只作用于安装后的 app，默认位置为当前用户的 `~/Applications/Boardcast.app`；`/Applications/Boardcast.app` 仅在当前用户已有写权限时使用。不要从 Downloads、挂载 DMG、AppTranslocation 或 Xcode DerivedData 运行来验收自更新，也不主动提权。DMG 文件可以下载到 Downloads；需要先将里面的 app 拖到应用目录，再从该目录启动。Gatekeeper 可能把下载位置启动的 app 隔离到随机只读目录，不能试图清除 quarantine 或绕过系统隔离。[Apple TN2206 安装位置](https://developer.apple.com/library/archive/technotes/tn2206/)

BoardcastUpdater 是随 app 分发的一次性命令行 target，位于 `Contents/MacOS/BoardcastUpdater`。app 通过 Xcode target dependency、Copy Files 的 Executables 目的地与 CodeSignOnCopy 嵌入它；helper 使用 `SKIP_INSTALL = YES`，避免 archive 多出独立安装产品。此项目不是沙箱 app，不照搬参考文档中的 sandbox inheritance entitlement。[Apple 内嵌工具规则](https://developer.apple.com/documentation/xcode/embedding-a-helper-tool-in-a-sandboxed-app)

更新 helper 验证原 app 的 PID、launch date 和 bundle/executable 路径，等待该实例自然退出，不强杀。系统 ditto 在安装目录同一卷上复制候选并再次验证签名，再用 Foundation `FileManager.replaceItemAt` 完成替换和备份。选择 `.usingNewMetadataOnly`，让新 app 使用候选元数据；选择 `.withoutDeletingBackupItem` 保留旧 app。backupItemName 使用先确认不存在的 UUID 名，因为 Apple 说明同名备份存在时会删除它。失败回滚先复验旧备份，再用同一 API 恢复，并保留失败的新 app；系统替换异常记录 `NSFileOriginalItemLocationKey` 的真实恢复位置，不另造搬移逻辑。它没有 daemon、root 安装或长期监听职责。新实例由 NSWorkspace 启动；确认匹配安装计划与进程，仍运行却未确认时保留备份，不强杀或回滚用户任务。复制后/替换前的真实内容、链接变化与异常恢复需要实机验证。[Apple 系统替换与恢复规则](https://developer.apple.com/documentation/foundation/filemanager/replaceitemat(_:withitemat:backupitemname:options:))

## 正式分发的本机流程

本地开发使用 ad-hoc 签名，可输出本机 app 与开发 ZIP；这不是源码交接包，也不是局域网发行来源。正式对外分发使用一个包含 Intel 与 Apple Silicon slice 的 **universal DMG**，里面是单一 Boardcast.app（内含 updater）和安装说明。需要有效 **Developer ID Application**、Hardened Runtime、secure timestamp，发行 entitlement 不含值为 true 的 `com.apple.security.get-task-allow`。本工程 Release 关闭 `CODE_SIGN_INJECT_BASE_ENTITLEMENTS`；脚本检查实际签名内容。[Apple 公证条件](https://developer.apple.com/documentation/security/notarizing-macos-software-before-distribution)

资源装配、helper 嵌入与 asset 编译在外层 app 签名前完成；签名顺序从 helper 到 app，再到 DMG。签后不修改封签内容。`codesign --deep` 用于递归验证，不能作为正常签名和结构修补策略。[Apple 分发签名](https://developer.apple.com/documentation/xcode/creating-distribution-signed-code-for-the-mac)、[Apple TN2206](https://developer.apple.com/library/archive/technotes/tn2206/)

签名的 secure timestamp 需要网络连接，故 `release.sh --prepare` 不上传公证，但仍可能联系 Apple timestamp 服务。本地开发 `build.sh` 没有该正式发布步骤。证书、密码和 API 密钥不写进仓库；notarytool 使用本机 Keychain profile。[Apple timestamp 与发行权限规则](https://developer.apple.com/documentation/security/resolving-common-notarization-issues)

首次正式发布前，在本机 Keychain 安装有效 Developer ID Application 证书及其私钥，以 `security find-identity -v -p codesigning` 确认完整 identity。若用 Apple ID 认证，可创建 profile，密码按 Terminal 提示输入：

```sh
xcrun notarytool store-credentials "boardcast-notary" \
  --apple-id "YOUR_APPLE_ID" --team-id "$DEVELOPMENT_TEAM"
```

随后 `NOTARYTOOL_PROFILE=boardcast-notary` 引用该 profile；这里的密码是 app-specific password，不写进命令或配置。[Apple notarytool 凭据规则](https://developer.apple.com/documentation/technotes/tn3147-migrating-to-the-latest-notarization-tool)

本项目显式 `--notarize` 先将 app 的中间 ZIP 提交公证，检查 Accepted、获取并审阅日志，staple/validate **app 本体**，随后制作并签署 DMG，再提交公证、审阅日志、staple/validate DMG。最终产物是有 ticket 的 DMG 与 SHA-256；中间 ZIP 不作为发行包。ZIP 可以提交但不能直接 staple。[Apple 公证工作流](https://developer.apple.com/documentation/security/customizing-the-notarization-workflow)

app 本体先带 ticket 是本项目只读 hdiutil 挂载与局域网重打包的要求。Apple DTS 解释：用 `hdiutil` 挂载不像 Finder/NSWorkspace 一样自动 ingest DMG 票据，离线核验内部 app 时应先对 app 公证并 staple，再包装 DMG。对最终 DMG 另签名、公证并 staple 是此项目的正式发行策略，不把“只公证外层容器”误当作能覆盖全部离线更新路径。[Apple 离线 DMG 更新说明](https://developer.apple.com/forums/thread/741219)

正式 DMG 在私有构建目录验收完成后，才通过独立 `publish.sh` 显式导出到仓库根 `dist/macos/`。该脚本验证 DMG 与内部 app/helper、签名、两架构、票据和 Gatekeeper，复制 DMG 与 SHA-256，拒绝覆盖既有同名产物；不会提交 Git 或自动上传。根 `dist/macos/*.dmg` 使用仓库已有 Git LFS 规则。用户自行 Git push/pull 源码，开发产物保留在忽略的 `platforms/macos/.build/` 与 `platforms/macos/dist/`。

最终在干净 Mac 用真实分发 DMG 安装，保留下载隔离语义；从应用目录启动、覆盖首次安装/升级/回滚/离线运行，并分别验收 Apple Silicon 和 Intel。开发机器命令成功不代替这一过程。[Apple 分发包验收](https://developer.apple.com/documentation/xcode/packaging-mac-software-for-distribution)

脚本完整接口与实机检查在 [Mac README](../platforms/macos/README.md) 和 [本地开发交接](../platforms/macos/DEVELOPMENT.md)。本轮仅交付源码、资源、工程与脚本，尚未执行 Mac 编译、正式签名发布或公证上传。
