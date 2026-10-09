# Boardcast macOS 本地开发交接

截至 2026 年 10 月 9 日，macOS 是独立 Swift 源码实现，尚未在 Mac 编译、运行或与 Windows 实机互通。首先建立可复现的本地基线，再按阶段实现 [macOS 产品与界面设计](../../docs/macos-design.md)。当前 Windows 0.2.6 源码的功能不能直接算作 Mac 已支持能力。设计基线包含当日 Windows 操作精简：单行设备工具栏和设备项、可点击空状态、按需文件操作和一键清除筛选；Windows 分发与验收状态见 [验证记录](../../docs/verification.md)。

## 当前工程入口

| 位置 | 当前内容 | 首批关注点 |
| --- | --- | --- |
| `Package.swift` | Swift tools 5.9，macOS 13，单可执行目标和测试目标，无第三方包 | 验证当前 Xcode 的编译诊断；新增资源时声明 resources 和本地化 |
| `Sources/Boardcast/BoardcastApp.swift` | 应用场景、全局模型、发送、设置、规则及设备视图 | 拆分草稿和任务，修复无窗口重开、设置提交与生命周期 |
| `Sources/Boardcast/TransferService.swift` | mTLS hello/offer 与 legacy 文件流，最多两个连接 | 确认后重检设置、结构化进度、独立取消、接收存储 |
| `Sources/Boardcast/WireConnection.swift` | 4 MiB JSON 帧及异步 NWConnection 读写 | 真实 TLS、短读、超时、取消和 continuation 竞态 |
| `Sources/Boardcast/Identity.swift` | Keychain P-256、持久证书、DER 指纹与双方 TLS 认证 | 重启后身份不变，未知或不匹配指纹不能继承信任 |
| `Sources/Boardcast/LANDiscovery.swift` | 物理接口 IPv4 广播，60 秒广播，150 秒过期 | 权限、接口变化、probe、Windows goodbye 与状态差异 |
| `Sources/Boardcast/Models.swift` | 设置、设备、offer、规则与设置保存 | 缺任务、能力、LAN 在线和应用版本模型 |
| `Sources/Boardcast/DeviceGroups.swift` | 分组规则与兼容解码 | 80 个 UTF-16 代码单元边界、个人覆盖不外传 |
| `Sources/Boardcast/ReceivePolicy.swift` | 清单、规则、路径与目录安全 | 真实文件系统、同名冲突、重新批准与内容限额 |
| `Tests/BoardcastTests` | 规则与分组单元测试 | 尚无真实 TLS、窗口、登录项及跨平台集成覆盖 |
| `scripts/build.sh` | 测试、双架构 Release、组装 app、签名、ZIP | 图标/资源未打包；默认 ad-hoc，不上传公证 |
| `Info.plist` | bundle ID `local.boardcast.mac`，版本 0.1.0，本地网络说明 | 明确平台版本；资源、名称及正式身份保持稳定 |
| `../../.github/workflows/macos.yml` | macos-14 CI 测试及通用 app 构建配置 | 有配置不等于本次已成功运行 |

源码目录当前不包含 `.git`；如果继续用源码副本开发，应在 Mac 明确代码来源与备份方式。不要把 Windows 发行 ZIP 当作完整开发工程。

## 在 Mac 建立基线

使用完整源码目录和能提供 Swift 5.9 或以上的 Xcode 或命令行工具。先记录系统、工具链、编译错误及依赖条件，避免一边大改布局一边判断原工程是否可用。

```sh
cd platforms/macos
sw_vers
xcode-select -p
swift --version
swift test
bash scripts/build.sh
open dist/Boardcast.app
```

`build.sh` 内部也执行 `swift test`；上面的独立测试用于首次定位问题，日常打包可以直接执行构建脚本。现有脚本拟生成 arm64 与 x86_64 通用 app，以及 `dist/Boardcast-macOS.zip`。构建后核实实际结果：

```sh
file dist/Boardcast.app/Contents/MacOS/Boardcast
lipo -archs dist/Boardcast.app/Contents/MacOS/Boardcast
plutil -lint dist/Boardcast.app/Contents/Info.plist
codesign --verify --strict --verbose=2 dist/Boardcast.app
```

双架构文件存在不等于两种架构都经过运行验收。先在当前 Mac 运行，再安排 Apple Silicon 和 Intel 各自验证。界面与网络权限测试用打包后的 `.app`；从 Terminal 执行 `swift run` 不能替代应用权限和签名测试。

默认设置保存到 `~/Library/Application Support/Boardcast/settings.json`，设备身份私钥在登录 Keychain。当前 Swift 入口没有 Windows 那样已经实现的 `--data-dir` 隔离选项；不要在测试命令中假设该参数存在。建议首批新增可注入的数据目录和测试身份提供器，自动化测试不要重置真实设备身份或更改日常接收位置。

## 首批必须修复的行为

1. **批准后重检有效设置。** `TransferService.receive` 在等待用户确认前捕获 configuration，确认后仍使用旧接收开关、路径和限额。确认回调应返回明确决定和选定 URL，随后重新读取有效设置、验证允许接收、限额和目的地。等待期间更改设置不应绕过新的限制。
2. **设置草稿与快捷操作隔离。** 当前 `activeSettings` 能防止网络读取未保存文字，但分组筛选和菜单开关调用整份 `save()`。改为字段级提交，筛选或暂停接收不得保存未完成的规则和名称编辑。
3. **单窗口重开与服务启动独立。** 当前从主窗口 `.task` 启动服务，以 `NSApp.windows.first` 恢复。关闭最后一个窗口、登录项启动和 Dock 重开都需可达；服务初始化不能依赖一个可见 View，菜单栏能重新创建窗口。
4. **任务拥有独立状态。** 当前 `status`、`selectedPeer`、`busy` 和未保存句柄的 Task 无法承载并行进度和取消。先定义任务 ID、方向、对端指纹、阶段、字节、目的地、已保存文件数、错误和可用动作。
5. **接收完成与回执区分。** 文件提交后 complete 发送失败时，接收结果依旧已保存。保留完成事实；legacy 发送者可能只能显示结果未确认，再发前告知可能产生副本。
6. **端口修改与 listener 一致。** 若保留可编辑端口，保存时校验并有序重启发现与 listener；首版也可只读展示固定端口，避免修改模型但仍监听旧端口。
7. **真实在线与接收状态。** 展示远端 `receivingEnabled`；新增 `lanOnline` 和离开提示时按协议解码。未认证 goodbye 只能作为端点匹配的离开提示，不可删除信任。
8. **修正溢出测试。** 原测试默认 100 GiB 上限可能先触发容量限制。构造累计溢出测试时显式将上限设到 `Int64.max`，分别断言容量限制与溢出错误，确保真正命中目标分支。

## 实现任务与验收条件

### P0 构建与 legacy 互通

保留原生 Swift 技术栈，先修复以上行为与 Mac 编译错误。记录本次实际 Swift 版本和所有诊断；避免为了消除并发警告批量加入 `@unchecked Sendable`，应确认队列和 actor 的所有权。

完成条件是打包 app 可正常运行、关闭和重开，Keychain 指纹重启不变，Mac 与 Windows 0.2.6 双向传输，并验证 0.2.4 兼容场景：单文件、空文件、空目录与 Unicode 多级目录。核实目标实际 SHA-256、结构和最终路径；批准、拒绝、确认超时、接收暂停、指纹不一致与断线均有明确结果。

### P1 工作台与原生系统入口

先完成 SendDraft 和 TransferStore，再拆视图。文件面板选择加入草稿而非立即发送；实现拖入、多个接收者、独立任务、异步统计、具体排除项、说明、完整来件清单、选择接收目录和历史中的 Finder reveal。

文件操作统一在内容区的一个位置。空列表用可点击拖放区域承载添加，已有内容才显示紧凑操作栏；筛选无结果显示清除筛选。附近设备标题、分组、数量与刷新在宽度允许时共用一行；窄窗口先缩短标签和图标化刷新，完整名称通过帮助与可访问标签保留，避免标题和控件相互挤压。设备项使用单行：勾选、平台图标、设备名、必要的状态图标和版本；名称占剩余宽度，长文本省略。只有暂停接收、可能离开或离线时显示警示图标，并通过帮助与 VoiceOver 解释原因。备注、完整原名、分组、信任、地址和指纹通过可点击且键盘可达的详情入口查看，查看详情不改变目标选择。

当前 Swift 没有应用版本与更新来源模型。P1 添加可选字段的兼容解码后，只用灰色显示实际运行版本，未知版本不占标记空间。Windows 的蓝色气泡显示可提供的适用更新版本，点击只进入更新详情；Mac 在 P3 完成平台、架构、渠道、发行签名与安装链路验收后才开放相应入口，不能依据 Windows 版本较高就显示 Mac 更新按钮。

多接收者使用独立 TLS 流和有界队列。首批可沿用当前两连接资源上限，不在引擎未改变前宣称四路并发；是否对齐 Windows 的四路上限由实机 CPU、磁盘与网络测量决定。取消支持后才开放对应动作；chunk 未实现时不显示“继续”，只提供明确的“重新发送”。

补本地化字符串、复数、图标和资源 bundle。菜单栏管理在线/接收、待确认、任务、打开窗口、接收目录和退出；Settings 使用独立窗口。登录启动应区分 enabled、requiresApproval 和实际错误，并设计静默启动路径。

完成条件是 820×560 点、侧栏收起、长中英文名称和 1000 项清单可操作；两个任务互不覆盖状态；搜索隐藏选中者仍明确显示目标，分组筛选清除隐藏选择并反馈。VoiceOver、键盘、深浅模式、增加对比和减少透明度均经过实际检查。

### P2 接收存储与恢复

先把 legacy 接收迁移到 ReceiveStorage：确认目录直接保存、稳定数字后缀、逐文件提交、私有缓存、取消保留完成文件。连接中断和用户主动取消具有不同保留策略；跨卷、磁盘不足、目标卷移除、句柄占用和重启清理必须验证。

随后按 [线协议](../../docs/protocol.md) 实现 chunk manifest、内容标识、缺块请求、校验后复用、持久发送任务及重启恢复。能力真正可用后才宣告 `chunks-v1`。取消控制单独移植 `receive-cancel-v1`，验证原发送方 mTLS 指纹，其他设备不能通过猜测任务 ID 清理任务。

Mac 与 Windows 使用同一组协议 fixture：4 字节大端长度、camelCase JSON、64 位小写十六进制证书指纹、legacy 清单顺序和每文件 32 字节摘要，以及 chunk manifest 内容 ID 的精确定义。缺失可选字段的旧设备继续兼容。不要依据版本号代替能力协商。

完成条件是断网、暂停、退出和重启后继续只复用重新校验的内容；源文件变化不会混入旧内容；同名文件/文件夹不覆盖；完成后无下载目录 JSON 或任务包装目录；取消只清理属于本次任务的未完成内容。

### P3 平台深化与正式分发

Finder 分享扩展、Quick Look、本机 CLI/MCP、P2P 和局域网更新可分版本实施。共享扩展需要独立 target、资源、签名和与主应用交换草稿的容器配置；当前 SwiftPM 单可执行目标没有这些能力。用户选择的内容先入草稿，再明确发送。

CLI/MCP 使用当前用户保护的本地接口，与 UI 共用任务和规则。摘要工具显式运行，结果先进入说明草稿。P2P 必须移植签名授权、来源校验、撤销与回退；更新必须另核验 Mac 平台、架构、渠道、固定发行信任根和 app 签名，不接受 Windows 包。

## Apple 平台核查

macOS 15 起引入本地网络隐私权限。现有 plist 已有 `NSLocalNetworkUsageDescription`；实际用途说明、拒绝、再次启动和系统设置恢复应在 app 形态验收。系统没有通用的本地网络权限查询 API；设备数为零不能判定拒绝。可以对本地 TCP 的 NWConnection 状态及 path 的 `localNetworkDenied` 原因做针对性诊断，UDP 广播失败另核实错误和网络状态。[Apple TN3179](https://developer.apple.com/documentation/technotes/tn3179-understanding-local-network-privacy)

现有发现是 IPv4 UDP 子网广播。只有实际新增 Bonjour 浏览或注册时才声明对应 `NSBonjourServices`；macOS 不需要 iOS 的 `com.apple.developer.networking.multicast` entitlement。当前直接分发形态没有启用 App Sandbox；若后续开启沙箱，另行验证网络 client/server 与文件访问权限，不把 Hardened Runtime 当成 App Sandbox。[Apple 网络 client entitlement](https://developer.apple.com/documentation/bundleresources/entitlements/com.apple.security.network.client)

沙箱版需要持久访问用户选择目录时使用 security scoped bookmarks，解析过期书签、调用访问开始/结束并处理失效。非沙箱版仍有系统受保护目录、Keychain、本地网络与通知权限，分别测试。[Apple 沙箱文件访问](https://developer.apple.com/documentation/security/accessing-files-from-the-macos-app-sandbox)

`SMAppService.mainApp` 提供 macOS 13 以上登录项注册。系统可能要求用户批准，UI 应反映真实 status 并提供系统设置入口；启用后是否静默显示主窗口由应用自行实现。[Apple SMAppService](https://developer.apple.com/documentation/servicemanagement/smappservice)

Network.framework 的 TLS 参数要显式要求客户端证书，pin 校验基于持久证书 DER 指纹，并核对 hello 和 offer 的发送身份；不能把自签名证书当成跳过身份校验的理由。[Apple TLS 身份验证](https://developer.apple.com/documentation/network/security-options)

## 正式构建与分发关口

当前构建脚本默认 ad-hoc 签名，适用于本地开发，不等于 Developer ID 分发和公证完成。正式打包前确定稳定的 bundle ID、Mac 版本号、build number、最低系统和签名团队；plist 与发现版本必须一致。0.1.0 可作为实际 Mac 预览版本，不能只改成 Windows 的版本号就宣称功能对齐。

新增 resources 后，检查 SwiftPM 资源 bundle 与图标进入最终 app，并在移动 ZIP 解压位置后验证资源仍可读取。签名在资源复制完成后执行；公开版需签名所有真实嵌套组件，不能依赖 `codesign --deep` 粗暴修补结构。

现有签名入口如下，示例身份需要替换为本机实际证书，当前交接不执行分发：

```sh
SIGN_IDENTITY="Developer ID Application: YOUR NAME (TEAMID)" bash scripts/build.sh
```

后续发布流程增加 notarytool 提交、公证结果检查、app stapling 及重新生成 ZIP。签名、公证、Boardcast 自己的发行包签名是不同验证层；若实现 LAN 更新，两者都需要。不能将成功 codesign verify 当成成功公证或 Gatekeeper 干净机器验收。[Apple 公证](https://developer.apple.com/documentation/security/notarizing-macos-software-before-distribution)

最终记录以下矩阵，每项附系统版本、架构、构建身份、文件摘要或明确结果：

| 场景 | 验收内容 |
| --- | --- |
| Mac 首次安装 | 本地网络说明、Keychain、接收目录、资源和语言 |
| 窗口与后台 | Cmd+W、红色关闭、菜单重开、Dock 重开、Cmd+Q、登录启动与系统批准 |
| 网络差异 | Ethernet/Wi-Fi、接口切换、休眠恢复、广播受阻和手动 IP、隔离 Wi-Fi；不从空列表推断远端离线 |
| 双向 legacy | Windows 0.2.6 与 Mac，并保留 0.2.4 兼容、空文件/目录、Unicode、hash、完整相对目录 |
| 大清单 | 1000 项 UI；在清单限额内的小文件压力；读取失败、链接与排除 |
| 部分完成 | 第二文件失败/取消，已保存数与 Finder 目录一致；同名与跨卷 |
| 恢复与取消 | 能力缺失降级、重启、源变化、缓存损坏、非法身份取消 |
| 规则与信任 | 同组不授权、未知指纹、撤销信任、完整批次后缀、设备条件、大小上限 |
| 资源与性能 | 前台和菜单栏空闲 30 分钟的 CPU/RSS/唤醒；Ethernet/Wi-Fi 吞吐与磁盘条件 |
| 分发 | arm64 与 Intel 实机、签名、公证、ZIP 资源、下载后 Gatekeeper |

Windows 0.2.6 的检查与发行结果见项目验证记录，不能算作 Mac 的测试结果。将新的 Mac 构建、互通和 UI 结果独立写入验证记录；测试通过之前保持“源码待验证”表述。