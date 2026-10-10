# Boardcast for macOS

独立 Swift / SwiftUI 工程，最低运行系统 macOS 13，使用 macOS SDK 的系统框架，没有第三方 Swift 包或额外运行时。一个 universal app 同时包含 **Apple Silicon（arm64）与 Intel（x86_64）**，正式发行使用一个 DMG。普通 Mac 安装、运行和更新不需要 Xcode 或 Command Line Tools。

**本轮交付已编写、待 Mac 验收的源码、Xcode 工程和脚本。** Windows 静态检查不能代替 Swift 编译、真实 TLS、UI、安装更新或 Mac–Windows 双机测试。开发与验收在本机完成，不依赖 CI；此次没有运行 Mac 编译或执行正式签名、公证上传。[检查记录](../../docs/macos-source-verification.md) 分别列出已检查和待执行项目。

## Git 获取与 Xcode 编译

用户通过既有仓库自行 Git push；Mac 使用同一分支/提交 clone 或 pull，保留完整目录结构，不需要源码交接 ZIP。安装完整 **Xcode 16 或以上**。Xcode 16 的构建机最低 macOS 14.5，应用最低运行系统仍为 macOS 13；较新 Xcode 按 [Apple 工具链兼容表](https://developer.apple.com/xcode/system-requirements/) 选择主机系统。

```sh
cd platforms/macos
bash scripts/preflight.sh
open Boardcast.xcodeproj
```

在 Xcode 选共享 scheme **Boardcast / My Mac**，先 Cmd+U 测试，再 Cmd+R 运行。工程采用 Swift 5 language mode；app、内嵌 BoardcastUpdater 和 XCTest 分别是独立 target。

```sh
bash scripts/build.sh
```

本地脚本先执行 hosted XCTest，再构建含 updater 的双架构 Release app，并做 ad-hoc 签名与资源/架构检查。app 与开发 ZIP 位于仓库根 `artifacts/macos/local/` 的本次运行子目录，测试和 DerivedData 在 `artifacts/macos/xcode/`。开发 ZIP 是本机应用验收包；正式发行用下述 DMG。SwiftPM 的 `swift test` 可补充检查同一份 app/Shared 源码，正式 app 始终用 Xcode 工程构建。

## 基础互传与 Mac 更新

已编写发送草稿、拖放与扫描排除、多个接收对象、独立任务和有界队列；设备详情、分组、信任、菜单栏、登录项、原生文件夹/共享类型图标、中英文资源及逐文件安全保存。

Mac 与 Windows 的基础文件互传继续使用相同的 legacy v1：物理接口 IPv4 广播发现和经明确许可的私有网段单播发现、mTLS 证书身份、逐文件 SHA-256 校验，默认 TCP/UDP 端口 **45831**。来件确认 **120 秒**，批准后重检实际设置与保存位置；两个文件传输网络槽位。确认目录直接保存，同名数字后缀不覆盖；取消保留已保存文件，完成回执失败不撤回文件。

工作台可输入同一 LAN 内可路由的私有 IPv4 地址连接设备。只有成功通过 mTLS 并核对 hello 身份后，才保存覆盖该地址的接口网段；旧设备缺少网段数据或接口前缀过宽时使用该地址的 /24。已保存范围限制为 RFC1918 IPv4 /16 至 /32、最多 32 项；/16 采用渐进扫描，UDP 单播与独立、有界的 TCP hello 回退游标逐步识别运行 Boardcast 的设备。普通广播、收到的分享内容和扫描结果不会自动增加许可范围，发现也不会赋予设备信任。

在设置的 **网段发现** 页面可按 CIDR 和端口遗忘网段，立即取消相关扫描；正常同接口广播发现继续工作。工作台选择一台或多台设备后，可预览并勾选本机接口与已保存的网段，主动发起分享。对方看到独立来件，在 120 秒内明确接受或拒绝；接受且持久保存成功后才扫描，不采用文件自动接收规则。发送取消或断开会结束等待确认。范围需要 LAN 路由本来就可达，不创建 NAT 映射、不穿透或探测其他未许可网段。

本轮另编写 **Mac 局域网更新**。已安装的较新 Mac 版本可分享自己的发行 app，旧 Mac 在设置的更新页面选择来源、明确下载和安装。下载用独立 mTLS 更新服务，候选 app 核验 bundle、Developer ID 发行 Team、严格更高的三段版本、主程序与 updater 的全部架构签名和 Gatekeeper 公证；同一营销版本的 build 变化不触发更新，Windows 包不能成为 Mac 更新候选。安装由内嵌 updater 等待旧 app 退出后替换，保留备份并处理失败回滚。新进程仍运行但未确认启动时保留备份、记录状态，不强杀或回滚正在处理用户任务的进程；无自动安装、系统 daemon 或提权步骤。

开发用 ad-hoc app、未公证发行版及非正常安装位置不能作为有效更新入口。文件传输设备指纹与 Apple 发行签名分别核验；高版本号或下载 SHA-256 正确不足以允许安装。运行时共享的缓存 DMG 只作为传递已公证 app 的容器，内部 app 仍须通过发行身份检查。

设置、历史与私有缓存在 `~/Library/Application Support/Boardcast`，私钥在登录 Keychain，默认收件目录为 `~/Downloads/Boardcast`。更新替换 app，不替换这些用户数据。

chunk 缺块恢复、重启续传、远端文件取消控制、P2P 内容分享、Finder 分享扩展、Quick Look 和 CLI/MCP 留待后续版本。任务历史不等于续传能力；IPv6 发现尚未实现；跨 AP 或网段仅使用已有 LAN 路由，不配置路由或穿透隔离。所有已编写功能仍须按 [实机矩阵](DEVELOPMENT.md#本地实机验收矩阵) 验收。

## DMG 安装与正式发布

正常安装：打开 DMG，将 **Boardcast.app 拖到当前用户的 `~/Applications/`**（目录不存在时先在 Finder 创建），弹出磁盘映像，再从该目录启动。当前用户已有写权限时也可放在 `/Applications/`。不要直接运行 DMG 内或 Downloads 中的 app 来验收更新；系统可能把它隔离到只读 AppTranslocation 目录。[Apple 安装说明](https://developer.apple.com/library/archive/technotes/tn2206/)

正式构建在 Mac 本机执行，使用有效 Developer ID Application 证书和十位 Team ID：

```sh
export DEVELOPER_ID_APPLICATION="Developer ID Application: YOUR NAME (TEAMID)"
export DEVELOPMENT_TEAM="TEAMID"
bash scripts/release.sh --prepare
```

`--prepare` 测试、归档、签名 app 与 DMG，生成标有 `NOT-NOTARIZED` 的 DMG，不上传公证；secure timestamp 可能联系 Apple。公证凭据事先保存在本机 Keychain，只有明确执行以下模式才上传：

```sh
NOTARYTOOL_PROFILE="boardcast-notary" bash scripts/release.sh --notarize
```

脚本先对 app 公证并 staple，再制作、签名、公证和 staple DMG，保留 Accepted 响应与日志。最终 `Boardcast-macOS-universal-<版本>-<build>.dmg` 和 SHA-256 位于 `artifacts/macos/release/` 的本次运行目录，归档与检查日志留在 `artifacts/macos/xcode/`。

实机验收后，可明确导出正式产物到仓库根 `dist/macos/`：

```sh
DEVELOPMENT_TEAM="TEAMID" bash scripts/publish.sh /absolute/path/to/final.dmg
```

导出脚本再次核验 app/updater/DMG、两架构、签名、公证票据与 Gatekeeper，拒绝覆盖同名产物；不自动 Git 提交或上传。仓库已将 `dist/macos/*.dmg` 配置为 Git LFS，正式产物沿用该仓库发布流程。日常构建目录已忽略，源码通过 Git 交接。此轮只提供工程与脚本，正式发布须等 Mac 编译、基础互传、双架构运行及更新验收完成。

详细 [目录与验收步骤](DEVELOPMENT.md)、[官方语言和发布规则](../../docs/macos-build-rules.md)、[Mac 设计方案](../../docs/macos-design.md) 随源码保存。
