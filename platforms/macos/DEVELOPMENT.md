# Boardcast macOS 本地开发交接

本轮依据 [Mac 设计方案](../../docs/macos-design.md) 编写独立 Swift / SwiftUI 工程、基础 Mac–Windows 互传、工作台、逐文件安全保存，并按用户追加要求加入本轮 **Mac 局域网更新、内嵌 updater 和单一 universal DMG**。交接日期为 **2026-10-10**。源码与工程已写入仓库，当前 Windows 环境只能检查结构、资源与静态语法；**尚未在 Mac 编译或运行，尚未完成双机、安装或更新验收**。以下“已编写”描述源码行为，“通过”只能在真实执行后记录。

## 工具链与首次构建

使用完整 Xcode **16 或以上**，不能仅安装独立 Command Line Tools。应用 deployment target 为 macOS **13.0**，Package tools 为 **5.9**，Swift language mode 为 **5**。Xcode 16 构建机最低 macOS 14.5；较新 Xcode 有自己的主机要求，按 [Apple 版本表](https://developer.apple.com/xcode/system-requirements/) 选择。没有第三方 Swift 包、Homebrew、XcodeGen 或 Python 编译依赖。完整 Xcode 是源码构建条件；普通用户安装、运行与 LAN 更新不需要 Xcode/Command Line Tools，运行时用系统 Security 验证各架构。

用户自行 Git push；Mac 使用既有 remote clone 或 pull 同一分支/提交，不使用源码交接 ZIP。源码同步后保留相对目录，记录 revision 和本地改动，再在 Terminal 记录基线：

```sh
cd platforms/macos
sw_vers
xcode-select -p
xcodebuild -version
xcrun swift --version
bash scripts/preflight.sh
open Boardcast.xcodeproj
```

若当前选择的是独立 Command Line Tools，选择实际安装的 Xcode，例如 `sudo xcode-select -s /Applications/Xcode.app/Contents/Developer`，并在 Xcode 完成首次组件安装与许可步骤。此操作仅在本机工具链尚未就绪时需要。

在 Xcode 选 **Boardcast / My Mac**，Cmd+U 后 Cmd+R。共享 scheme 的 TestAction 注入 `BOARDCAST_TESTING=1`，测试宿主不会启动日常网络服务或创建真实设备身份。测试使用临时目录；不要删除日常设置或 Keychain 来“修复”测试。

终端等价测试命令：

```sh
xcodebuild -project Boardcast.xcodeproj -scheme Boardcast \
  -configuration Debug -destination 'platform=macOS' \
  -disableAutomaticPackageResolution -skipPackageUpdates \
  CODE_SIGN_IDENTITY=- test
bash scripts/build.sh
```

`build.sh` 内部再次执行测试，随后构建通用 Release app。首次单独执行测试用于定位诊断；日常打包直接使用脚本即可。每次运行使用新目录，不覆盖旧产物：

| 产物 | 路径 |
| --- | --- |
| 本地 app / 开发 ZIP | `dist/local/Boardcast-<版本>-<build>-local.<随机值>/` |
| 本地测试和 DerivedData | `.build/xcode/local.<随机值>/` |
| 发布 app / universal DMG / SHA-256 | `dist/release/Boardcast-<版本>-<build>-release.<随机值>/` |
| 发布 archive / 测试 / app 与 DMG 公证日志 | `.build/xcode/release.<随机值>/` |
| 显式导出的正式 DMG / SHA-256 | 仓库根 `dist/macos/`；通过 `publish.sh` 验证后导出，拒绝覆盖 |

本机 Run 可调试 UI 与基础传输。正式安装/更新验收先打开 DMG，把 `Boardcast.app` 拖到当前用户 `~/Applications/`，弹出映像后从应用目录启动；`/Applications/` 仅在已有写权限时可选。从 Downloads、DMG、AppTranslocation 或 Xcode DerivedData 运行不作为自更新安装位置。界面、登录项和本地网络权限用 app 形态验证；Terminal 的 `swift run` 不能替代。双架构生成也不等于两种架构已经运行通过。

## 文件目录与职责

| 位置 | 职责 |
| --- | --- |
| `Boardcast.xcodeproj/` | Boardcast app、BoardcastUpdater 命令行、hosted XCTest 三个 target；共享 Boardcast scheme |
| `Config/Base.xcconfig` | 产品身份、版本/build、macOS 13 与 Swift 5；plist 和脚本共用 |
| `Config/Debug.xcconfig` / `Release.xcconfig` | Debug 当前架构/可测试；Release 双架构/优化/发行调试权限关闭 |
| `Info.plist` | app bundle 属性、本地网络用途及发行身份配置，变量由 Xcode 展开 |
| `Resources/Assets.xcassets` | app 图标，交给 Xcode 的 asset compiler |
| `Resources/en.lproj` / `zh-Hans.lproj` | 主 app 的本地网络用途本地化 |
| `Sources/Boardcast/BoardcastApp.swift` | SwiftUI scenes、主窗重开、菜单、登录启动和退出流程 |
| `AppModel.swift` | 持久有效设置、平台服务组合、设备选择、批准、任务与更新提交 |
| `WorkbenchView.swift` / `SettingsView.swift` | 工作台、设备/来件/任务详情、独立设置草稿 |
| `SendDraft.swift` / `SelectionScanner.swift` | 发送草稿、异步扫描、问题和排除项；传输时重新检查源文件 |
| `TransferStore.swift` | 独立任务状态、进度及有界历史，不自动恢复 legacy 传输 |
| `TransferService.swift` / `WireConnection.swift` | 两个文件网络槽位、mTLS legacy 流、有界帧、累计读、超时和取消 |
| `Identity.swift` | Keychain 身份、证书 DER 指纹与 TLS 参数 |
| `LANDiscovery.swift` | 系统物理接口识别、IPv4 广播/probe、离开提示和过期 |
| `ReceivePolicy.swift` / `ReceiveStorage.swift` | 清单/规则/路径验证、安全逐文件提交、私有缓存与有界过期清理 |
| `MacUpdateService.swift` / `MacUpdatesView.swift` | Mac 专用局域网更新来源、缓存、明确下载与安装 |
| `Sources/Shared/MacRelease.swift` | app/helper 共用发行身份、DMG、安装路径与候选验证 |
| `Sources/BoardcastUpdater/main.swift` | 一次性等待旧 PID 退出、stage/替换/回滚，不提权 |
| `Models.swift` / `DeviceGroups.swift` / `SettingsValidation.swift` | 兼容解码、分组、设置验证与字段冲突检查 |
| `FileIcons.swift` / `AdaptiveFileRow.swift` | catalog 分类、系统文件夹、自适应文件信息排版 |
| `ResourceBundle.swift` / `L10n.swift` | Xcode 与 SwiftPM 共用资源入口、字符串格式化 |
| `Sources/Boardcast/Resources/` | Icons、ThirdParty 许可、两种语言的 UI 字符串 |
| `Tests/BoardcastTests/` | 规则/分组/发现解码、图标、扫描草稿、身份和存储规则测试 |
| `scripts/preflight.sh` / `build.sh` / `release.sh` / `publish.sh` | 本机预检、测试/构建、明确模式的正式签名公证、正式 DMG 导出 |
| `Package.swift` | 共用 Boardcast/Shared 源码的 SwiftPM 测试入口，不负责装配正式 app/updater/DMG |

文件职责保持最少分层：UI 状态在主 actor，系统网络/证书/登录项/文件图标交给系统 API；不新增 daemon、通用平台框架、App Sandbox 或书签管理层。具体语言和 API 边界见 [编译规则](../../docs/macos-build-rules.md)。

## 已编写行为与验收边界

发送内容先进入草稿；扫描发现不可读内容、符号链接或特殊文件时明确显示问题，只有用户排除或解决问题后才能发送。搜索不会默默丢掉已选设备，分组筛选会清除不可见选择并反馈。多个接收者创建独立任务，共用两个网络槽位；单任务取消不应改写另一任务状态。

来件确认有 **120 秒**期限；批准后重读实际接收开关、限额、规则和目的地。设置窗口保存仅合并自己修改的字段，同字段在其他窗口变化时要求重新载入；暂停接收或切换分组不会提交尚未保存的文字。端口本版固定 **45831**，不提供与实际监听不一致的端口编辑。

文件保存到确认的目录，保留来源的顶层目录语义，没有额外批次包装目录。每个文件完成 SHA-256 校验后独立提交；同名文件/目录使用 ` (1)`、` (2)` 后缀，不覆盖既有内容。目标卷支持的独占 rename 由 Darwin API 执行；跨卷先复制校验后的内容再独占提交。遇不支持的卷操作应报告错误，不能降级为覆盖写入。

设置与历史位于 `~/Library/Application Support/Boardcast`，私有未完成数据在其 `Transfers` 子目录，默认接收位置是 `~/Downloads/Boardcast`，私钥位于登录 Keychain。取消清理本次未完成内容，保留已经保存的文件；断线记录中断。启动和后续接收会有界清理超过七天的私有回执缓存，最多检查 512 个目录、删除 32 项；跳过活动任务、外来目录和链接，不递归进入接收位置或删除已保存文件。路径回执只记录明确清单条目，写入和清理共享 64 MiB 上限。接收端已保存完成后回执失败不撤回文件，发送端显示回执未确认。legacy 历史在重启后标为中断，重新发送前检查接收方是否已有副本。当前没有 `--data-dir` app 参数。

发现仅覆盖当前物理 Wi-Fi/Ethernet 的 IPv4 同子网。离开广播未经认证，只作为与已知端点匹配的提示，不能撤销信任。同组显示不代表授权；自动接收仍需设备指纹信任、设备自动接收授权和匹配规则共同满足。文件传输不按设备版本推断能力。Mac 专用更新端口与元数据只在真实更新服务可用时公布；Windows 的版本/更新包不能转化成 Mac 安装候选。

UI 已写入共享 32 类文件图标、三类平台图标、实际本地普通目录的系统图标与远端通用 folder 图标。文件行先测量完整名称和元数据，放得下才一行；否则两行，主清单与展开清单共用组件。图标和中英文字符串随资源打包，移动 app 后仍应能加载；无网络资源补取。

## 本轮 Mac 局域网更新

更新服务与文件 legacy 流分开，使用同子网的独立 mTLS 连接和随机 TCP 更新端口。协议为 `mac-update-info` → `mac-update-image`，版本 1、runtime `osx-universal`；不是 Windows 更新协议。附近列表不把 Windows 版本较高当作 Mac 有更新；只有真实可用的 Mac 来源进入设置更新页。用户明确选择来源、下载、查看验证结果，再决定安装；不自动下载或运行下载内容。

最多两个入站镜像分享和一个明确下载，镜像必须大于零且不超过 2 GiB，128 KiB 串流 SHA-256；握手 15 秒、空闲等待 90 秒。取消删除未完成镜像，没有 chunk 或下载续传。缓存位于 `~/Library/Application Support/Boardcast/Updates/{Published,Downloads,Ready,Install}`；重启恢复 `ready.json` 时重新验证候选 app，不能只信任缓存记录。Ready/Published 只清理自己此前指针所指的已知 UUID 缓存；不扫描删除未知目录、Install 计划或安装备份。已验证 ready 版本不高于当前安装版本时可清理该已知候选；同版本发布缓存校验 SHA-256 后复用。

来源是本机已安装并通过发行验证的 app；只有当前 app 验证、生成可分享镜像并启动监听成功后才公布更新能力。共享时用系统 ditto/hdiutil 缓存 DMG 容器，不重新签名或修改 app；候选 app 的 Apple Developer ID、发行 Team、bundle、严格更高版本、主程序/helper 双架构与 Gatekeeper 必须独立核验。版本是三个非负整数，不接受前导零或预发行后缀；仅 marketing version 更高可升级，build 变化不允许同版本替换。DMG SHA-256 只证明完整性，mTLS 指纹证明来源设备，两者都不能替代 app 的发行信任。

下载、只读挂载、复制和验证完成后，一次性 BoardcastUpdater 从私有安装计划启动。helper 再次验签并确认就绪，父 app 才退出；helper 识别原 PID、launch date 和路径，最长等待自然退出 120 秒，不强杀。它用系统 ditto 把候选 app 复制到安装目录同一卷并再次验签，然后用 Foundation `FileManager.replaceItemAt` 替换。设置 `.usingNewMetadataOnly` 与 `.withoutDeletingBackupItem`，旧 app 保留在安装父目录的 `.Boardcast-backup-<UUID>.app`；唯一备份名先确认不存在。

新实例由 NSWorkspace 启动，服务启动后按计划 ID、版本、PID 和 launch date 确认，最长等待 45 秒。启动失败或新进程已退出时重新验证旧备份，再用同一系统替换 API 回滚并重开旧版；失败的新 app 保留为 `.Boardcast-failed-<UUID>.app`。仍运行却未确认时记录 `startupUnconfirmed`、保留备份，不强杀或回滚用户任务。系统替换异常只在完整验签、同目录路径和无运行实例均可确认时安全恢复旧 app；未知系统临时位置仅记录真实恢复 URL，不猜测或搬移。启动请求尚待系统回调也记录 `startupUnconfirmed`，不能误判为明确失败。`Updates/last-update.json` 保留结果。正常位置只支持 `~/Applications/Boardcast.app` 或已有写权限的 `/Applications/Boardcast.app`，不可写或有链接时引导用户手动安装，不请求 root 权限。设置、接收历史、文件和 Keychain 不随 app 替换。

开发 ad-hoc app 与未完成公证的正式准备包不作为更新来源。需要两个已安装的正式 Mac 版本才能完成“高版本 Mac 更新低版本 Mac”实机验收；只修改版本号或测试模型不构成验收。本轮仍保留 legacy 文件互传为基础能力，不把 chunk 恢复、P2P 内容分发或 Windows 更新移入 Mac 路径。

## 本地实机验收矩阵

每次记录源码 revision/本地改动、系统/Xcode/Swift 版本、架构、签名身份、步骤、实际结果及相应摘要。单元测试结果、Mac app 验收和 Windows 既有结果分别记录。

| 场景 | 具体检查 |
| --- | --- |
| 首次编译 | Boardcast scheme 可打开；Cmd+U、Debug Run、Release universal build；保存诊断和 xcresult |
| 资源与语言 | DMG 安装后移动 app、中英文切换；32 类/特殊文件名/未知后缀/三平台；许可、app 图标、Retina/浅深外观 |
| 主窗与后台 | 820×560 点、侧栏收起、Cmd+W/红色关闭、菜单/Dock 重开、Settings 和任务详情各自打开；Cmd+Q 三种选择 |
| 登录项 | enabled/requiresApproval/notRegistered/实际错误；系统设置批准、注销登录、静默启动、取消注册 |
| macOS 13 | 最低运行系统实机测试所有界面与系统入口；新 API 不可用时兼容行为有效 |
| macOS 15+ 权限 | 用 Finder 启动 app，首次允许/拒绝/系统设置恢复；实际 Apple 签名身份复验，空设备列表不误报拒绝 |
| 网络变化 | Wi-Fi/Ethernet、多个物理接口、接口切换、休眠恢复、广播受阻/隔离 Wi-Fi、VPN；只在允许同子网工作 |
| legacy 双向 | Windows 0.2.7↔Mac，并保留 0.2.4 场景；单文件、空文件/目录、Unicode 多级目录、逐文件 SHA-256 |
| 身份与规则 | 重启指纹不变；锁定 Keychain 不换身份；未知/不符指纹拒绝；撤销信任、完整批次后缀、目录条件和限额 |
| 等待与设置 | 批准/拒绝/120 秒超时/断线；确认前暂停接收或修改规则/限额/目录；未保存设置不影响网络 |
| 草稿与密度 | 1000 项清单、长中英文名、宽窄宽切换、扫描/排除后的统计、展开一致；重复/重叠来源与不可读内容 |
| 独立任务 | 多接收者排队、两槽资源上限、取消其中一个、排队取消、来件取消；进度和状态不互相覆盖 |
| 部分保存 | 第二文件校验失败/断线/取消；已保存数与 Finder 一致；同名、大小写/NFC、目录替换攻击 |
| 文件系统差异 | APFS/外置卷/跨卷、目标卷移除、磁盘不足、句柄占用；不覆盖、临时内容安全、无包装目录 |
| 回执与重启 | 接收保存后回执丢失、发送端未确认；重启历史中断，不显示已续传；缓存记录与实际文件一致 |
| 辅助功能 | 键盘顺序、详情查看不改变选择、VoiceOver、增加对比、减少透明度、图标原因帮助 |
| 资源与性能 | 菜单栏空闲 30 分钟 CPU/RSS/唤醒次数；大文件和当前限额内的小文件吞吐；按真实机器记录 |
| 正式分发 | app/helper 两架构实机；Developer ID/runtime/timestamp、无 get-task-allow、app+DMG 公证/日志/ticket、干净机器下载 Gatekeeper |
| 正常安装 | DMG 拖 app 到 `~/Applications` 后启动；已有权限的 `/Applications`；Downloads/只读映像/DerivedData/AppTranslocation 不误进入更新 |
| Mac LAN 更新 | 新 Mac 发布本机版本、旧 Mac 明确选择下载/安装；Intel↔Apple Silicon 双向；接收端旧安装数据保持 |
| 更新包验证 | 非 Mac/非 universal/错误 bundle/Team/runtime/版本、ad-hoc、未公证、损坏或篡改包拒绝；全部 slices 和 helper 校验 |
| 更新中断 | 下载断线/取消、磁盘不足、旧 PID 尚未退出/被复用、活动传输、安装目录不可写；明确结果，旧 app 可继续使用 |
| 替换与回滚 | 同卷 staging、路径/链接变化、替换失败/回滚失败、启动确认和仍运行未确认；备份与日志可核查，不强杀、不提权 |
| 离线更新 | DMG 只读隐藏挂载后 app 本体 ticket 仍有效；无互联网的 LAN 下载/验证/安装/首次打开 |

十万项传输超出当前 50,000 清单限额，不把该场景当作已支持性能承诺。当前两槽限额先做实测，不先扩成四路。

## 发布与后续版本

`Config/Base.xcconfig` 是 bundle ID、marketing version 和 build number 的唯一来源；当前预览版本为 **0.1.0 / 1**。正式版本按真实 Mac 行为递增，保持 bundle 与发行身份稳定。源码由用户通过 Git push/pull 同步；开发 `.build/`、`dist/` 已忽略。正式的一个 universal DMG 与 SHA-256 只有在本机完成验收后，才用 `publish.sh` 明确导出到仓库根 `dist/macos/`，脚本拒覆盖且不自动 Git 提交/上传。`dist/macos/*.dmg` 已配置 Git LFS；发布二进制时使用仓库现有 Git LFS 流程，这不是编译源码的依赖。

正式流程使用 [README 入口](README.md#dmg-安装与正式发布)：先实机验收，再 Developer ID 归档。`--prepare` 生成签名但未公证的 DMG，secure timestamp 可联网；`--notarize` 才明确提交 Apple。app 先公证并 staple，随后包装并签名、公证和 staple DMG，确保 hdiutil 挂载后的内部 app 也有离线票据。保留 archive、xcresult、helper/app/DMG 签名、entitlements、公证响应与日志、最终 DMG SHA-256，随后干净机器验收。仓库内可选 Mac CI 不构成本地计划的前置条件。

后续补 chunk manifest、缺块复用、持久发送任务及身份认证的远端文件取消；真实可用并验证后才宣告相应能力。Finder 分享扩展、Quick Look、CLI/MCP 与 P2P 内容分发按后续版本实施，当前没有假入口或自动执行。未来真正需要 App Sandbox 时再实现安全作用域书签。Mac LAN 更新已纳入本轮源码，不再列作 P3 未实现项。

最终 Windows 结构、资源和静态检查结果见 [源码检查记录](../../docs/macos-source-verification.md)。这些检查不执行 Swift 编译，也不验证 Apple SDK 类型或运行时行为。Mac 编译、测试、权限、两架构运行、Mac–Windows 基础互传和 Mac LAN 更新完成后才可记录通过；本轮不改写既有 Windows 验证记录。
