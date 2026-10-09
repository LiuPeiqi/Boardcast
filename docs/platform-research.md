# 原生平台、后台与传输调研

调研日期：2026-10-08。这里只讨论同一局域网直连；不设计公网 NAT 穿透、中继、云控制面或全球发现。

## 平台决定与代价

Windows 采用 C# / WPF 窗口、独立 .NET 网络 daemon、独立 CLI。它降低原生对话框、DPI、键盘导航、Schannel TLS 和维护成本，并且当前开发机能完整构建。零外部 NuGet 依赖和自包含发行避免用户安装 .NET，但发行体积包含 .NET 运行时。**“无额外安装”不等于“极小发行体积”**。没有同功能基准，不能宣称 .NET 比 Rust/Win32 更省内存。

.NET Native AOT 可以降低启动和内存开销，但 WPF 当前不支持 trimming，而 Native AOT 依赖 trimming。因此不要为 WPF 开启 PublishTrimmed/PublishAot。网络进程可单独评估 AOT、JSON 源生成和兼容性，再决定是否采用。依据：[Microsoft Native AOT](https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot/)、[WPF trimming 限制](https://learn.microsoft.com/en-us/dotnet/core/deploying/trimming/incompatibilities)、[WPF 跟踪源码议题](https://github.com/dotnet/wpf/issues/3811)。

Rust + Win32 可避免 GC、提供小型常驻实现，是未来以实测证明 .NET 达不到资源目标后的候选。其代价是更多 Win32/COM/UI 封装、字体/DPI/可访问性工作和工具链维护。Go 适合网络服务，但仍有运行时/GC，原生桌面整合要另选 UI，不提供足够理由在本项目同时增加第三种主要语言。WinUI 提供现代 Windows 外观，但 Windows App SDK 的部署与依赖需单独管理；当前 WPF 更便于本机验证和稳定分发。以上是工程判断，不是没有基准的性能排名。

macOS 完全独立使用 Swift/SwiftUI，窗口、菜单栏、文件对话框和 ServiceManagement 适配系统习惯，Network.framework 提供系统 TLS。Apple 建议现代网络开发使用 Network.framework，并指出直接 BSD socket 不提供系统 TLS。UDP 发现层使用系统 socket，真正文件传输使用 Network.framework。[Apple 网络 API 选择](https://developer.apple.com/documentation/technotes/tn3151-choosing-the-right-networking-api)。

## 服务与低功耗

Windows 系统服务运行在非交互会话，不能直接显示正常用户界面。因此默认是登录用户的独立后台进程。另已编写可选的 Windows SCM 宿主入口 --service，但未注册或实测服务账号与生命周期，不能把默认用户 daemon 宣称为已验证的系统服务。需要注销后继续接收时，还要验证受限服务账号、接收路径和用户会话代理。[Microsoft Interactive Services](https://learn.microsoft.com/en-us/windows/win32/services/interactive-services)。

macOS 13 起 SMAppService 支持应用登录项、LaunchAgent、LaunchDaemon，各自权限与启动条件不同；当前独立实现使用 mainApp 登录项和菜单栏常驻，不需要系统 daemon 权限。[Apple mainApp](https://developer.apple.com/documentation/servicemanagement/smappservice/mainapp)、[register 行为](https://developer.apple.com/documentation/servicemanagement/smappservice/register())。

低空闲开销来自异步 socket 等待、有界队列、低频广播、关闭窗口后的 UI 成本减少，不是“服务”这个名称。避免网段扫描、每秒主动探测、文件索引与常驻 LLM。资源目标必须通过 CPU/RSS/唤醒次数实测核验。

## TLS 与 QUIC

QUIC 支持多流和连接迁移，但局域网单次文件传输不一定比长连接 TLS/TCP 快。需要两端库、UDP 防火墙和真实 Wi-Fi 丢包基准一起比较。本版采用原生 TLS/TCP 长连接，文件内容无需预压缩或复制到 ZIP。QUIC 保留为基于实测的后续版本，不宣称已经实现。

Apple 的 Network.framework 原生支持 QUIC，也提供 Bonjour 发现；其较新 Swift API 从 2025 年系统开始引入，因此为了 macOS 13 覆盖率采用旧 NWConnection/NWListener API。[Apple TN3213](https://developer.apple.com/documentation/technotes/tn3213-moving-from-multipeer-connectivity-to-network-framework)。Bonjour TXT 可公开设备标识，持续标识有隐私代价；Boardcast 当前只在本地网段广播设备名及公开指纹，不能声称局域网内匿名。

身份以证书 DER SHA-256 固定；发现广播只能作为候选，不能自动建立信任。TLS 校验身份后清单 senderFingerprint 必须一致；人工核对完整指纹后才允许信任，自动接收还需要独立设备授权和规则匹配。Apple 服务端默认并不强制客户端证书，代码必须显式开启 peer authentication。[Apple peer authentication](https://developer.apple.com/documentation/security/sec_protocol_options_set_peer_authentication_required(_:_:))。
