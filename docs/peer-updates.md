# 局域网版本更新

应用版本独立于设置文件的 `Version=1` 配置格式，目前应用版本为 `0.2.7`。设备会通告运行版本、处理器平台和可分享的签名发行版本。通告仅用于发现候选；下载连接重新通过 mTLS 验证指定设备的证书指纹。设备证书证明文件来自哪台设备，发行签名决定是否允许执行更新，两者不可互换。

版本探测随附近设备发现更新，不启动下载。设置页列出当前 Windows 处理器架构和稳定/预览渠道中所有可用的较新发行版本及来源；用户可选择其中某个版本，不限于最高版本。首页优先展示最高兼容版本的推荐来源，所有在线且可提供适用新版本的设备直接显示蓝色可点击版本气泡；当前或较旧版本显示灰色标记，不另设“有更新”气泡。语义版本比较遵循 SemVer 2.0；`1.10.0` 高于 `1.9.0`，正式版高于同版本预览版，构建元数据不影响优先级。同一个最高版本有多台设备时按名称和指纹稳定选择一台。右键置顶指定设备版本仅改变推荐来源，仍必须通过相同的兼容、签名和版本检查；不支持降级，也不让稳定版自动进入预览渠道。

## 发布方准备

仓库不包含发行私钥，Windows Authenticode 代码签名证书尚未配置。2026-10-09 已使用项目外单独保管的发行密钥制作 0.2.7 Windows x64 签名包，见[签名发行说明](signed-release.md)。不带签名参数的默认构建仍是开发包，界面会显示“此开发版本未配置受信任的发行更新包”，不分享可执行更新，也不接受设备提供的未知发行公钥。私钥不能随软件、仓库或设备传输分享。

发布者应在离线机器或受控签名服务中创建并保管 ECDSA P-256 私钥，使用同一发行信任根为首发版及后续版本签名。PowerShell 7 示例（路径必须是受保护的离线目录；只写文件，不打印私钥）：

```powershell
$releaseKey = [Security.Cryptography.ECDsa]::Create([Security.Cryptography.ECCurve+NamedCurves]::nistP256)
try { [IO.File]::WriteAllText('E:\OfflineReleaseKeys\boardcast-release-private.pem', $releaseKey.ExportPkcs8PrivateKeyPem()) }
finally { $releaseKey.Dispose() }
```

正常发布：

```powershell
./scripts/Publish-Windows.ps1 -Runtime win-x64 -Version 0.2.2 -SigningKeyPem 'E:\OfflineReleaseKeys\boardcast-release-private.pem' -ReleaseKeyId boardcast-release-1
```

`Sign-Release.ps1` 生成 `release-trust.json`（发行公钥）、`Boardcast-update.zip`（不包含自身的更新载荷）和 `release-manifest.json`（ECDSA P-256/SHA-256 签名清单）。ZIP 的 SHA-256、总大小及每个文件的相对路径、大小与 SHA-256 都在签名覆盖范围内。完整分发 ZIP 包含上述三个文件，已安装设备可直接分享自己的同一发行包。没有签名参数的重建会移除旧生成签名，避免错把新开发二进制当成旧签名发行包。

ECDSA 发行签名保护此便携版更新流程，和 Windows Authenticode、SmartScreen 或 MSIX 安装签名是不同机制。正式渠道仍应使用正式代码签名；使用 MSIX/Store 管理的安装需由对应系统安装器更新。本实现只自动切换由当前用户可写的便携式程序目录，Windows 服务模式和受管理员保护的安装不绕过权限。

## 下载、安装与恢复

1. 用户在设置中选定版本并点击传输后，才开始下载。后台重新验证所选设备指纹及当前程序目录内固定发行公钥，拒绝未知发布方、坏签名、旧版本、错架构或 Windows 版本不兼容的清单。探测到新版本、刷新、后台重启或旧配置中的自动下载开关均不会自行下载。
2. 将发行 ZIP 作为单个文件交给普通文件同用的 `ChunkTransfer` / `ChunkStore`，使用相同的分块摘要、缺块请求、有限并发、缓存复查与完成校验。下载缓存位于用户数据目录 `updates/download-<package-sha256>`。用户暂停、断线或后台重启会保留已验证块；选择同一版本继续时只请求缺失或损坏块。下载完成后仍验证签名覆盖的完整 ZIP 摘要及每个文件，拒绝路径逃逸、保留名称、文件冲突、链接及超限条目。安装辅助程序在此阶段从当前已签名发行版复制到用户数据目录并重新验证。
3. 用户明确点击“重启并更新”。后台只在所有发送、接收及待批准请求都已结束时启动安装，立即下线并禁止新传输和连接，返回成功后界面与后台正常退出。安装器按 PID 和进程启动时间等待这两个指定进程，不按程序名终止其他实例。
4. 独立的已安装版本 `Boardcast.Updater` 再次验证下载清单与全部文件，在程序目录的同一父目录中暂存新版本，并确认新包没有偷换发行公钥。待旧进程退出且后台锁释放后，先将旧目录移至备份，再将新目录移至原位置；第二次移动失败会恢复原目录。
5. 重启新界面，最多等待 45 秒确认其仍存活且该用户数据目录的后台 IPC 报告目标应用版本。检查失败且没有新传输时，停止该后台和安装器刚启动的新界面，恢复旧目录并重启旧界面。新版本已开始传输或无法安全释放目录时保留旧版备份及失败记录，不强制中断传输。切换前失败也会等待原界面和后台退出，然后重新启动旧界面，避免后台延迟退出使应用永久关闭。

用户设置、设备身份、规则及收件目录在程序目录之外，不随目录切换覆盖。更新要求下载临时目录、安装辅助副本、新程序目录和旧版本备份所需的可用空间。成功更新保留 `.Boardcast-backup-*` 旧目录，失败的新版本可保留为 `.Boardcast-failed-*`。用户数据目录 `updates/last-update.json` 记录结果及恢复目录；确认运行正常后可手动清理这些备份和临时辅助目录。

运行期功能、兼容性或后续崩溃不属于此启动检查的自动回滚范围。`updates/selected-update.json` 持久记录用户选定发行包；后台重启先检查缓存、签名及内容，再恢复为可继续状态。若全部内容齐全且验证通过，则恢复 `ready` 并重建已验证的安装辅助程序，不重新下载，也不自动安装。损坏或不匹配的记录不能授权执行更新。已安装到该版本或更高版本时清除过时选择记录，正常进入空闲；选择另一版本若在建立有效新选择前失败，保留此前可继续或已准备好的更新内容。

更新测试使用临时发行密钥、本机 mTLS 和独立安装器夹具，涵盖版本选择、共享块恢复、发行验证、目录切换及启动失败恢复；本轮实际通过数量见[验证记录](verification.md)。临时测试密钥、隔离 UI/后台宿主和回环连接不等同于正式 Windows 签名、真实 WPF 跨设备升级或干净机器验收。

## 接口

- `PeerInfo.AppVersion / Runtime / UpdatePort / UpdateVersion`：运行版本、架构、独立 mTLS 更新端口、可分享的签名发行版本；未签名版本没有更新端口与可分享版本。
- `DaemonSnapshot.Update`：`disabled / idle / restoring / downloading / paused / ready / installing / failed` 状态，以及进度、目标版本、来源设备、`CanResume`、`ReusedBytes`、`PeerBytes` 和 `SourceCount`。
- IPC `update-download` 使用所选版本来源的 `Peer` 启动或继续异步传输；`update-cancel` 暂停当前下载并保留可复用数据；`update-install` 使用当前界面 `ProcessId` 请求正常退出、安装及重启。
- `PeerUpdateCatalog.AvailableVersions(peers, currentVersion, runtime)` 按版本分组并按 SemVer 降序列出兼容候选；`Recommend(...)` 保留首页唯一推荐来源。候选发现不代表通过发行验证。

官方参考：[.NET ECDsa.VerifyData](https://learn.microsoft.com/en-us/dotnet/api/system.security.cryptography.ecdsa.verifydata?view=net-10.0)、[Windows 包签名](https://learn.microsoft.com/en-us/windows/win32/appxpkg/how-to-sign-a-package-using-signtool)、[MSIX 更新机制](https://learn.microsoft.com/en-us/windows/msix/app-package-updates)。

## 版本探测与共享传输

`AppSettings.AutoDownloadUpdates` 只保留旧配置字段兼容，默认关闭，读取旧配置及应用设置时都归一为 `false`，不再启动自动下载任务。后台可发现多个设备公布的不同版本；每台设备分享的是自身已验证的当前发行包，不是完整历史版本仓库。没有设备提供的旧发行版本不会凭空出现在列表中；降级和跨架构更新仍被拒绝。

更新保留独立的 mTLS 更新端口和签名清单握手，以区分可安装发行包的信任边界；文件块的调度、缓存、恢复与校验复用普通文件引擎，不再实现第二套整包下载循环。旧设备若不提供块清单，明确报不支持可恢复更新传输；普通文件仍可通过旧协议互通。

选定来源验证后立即开始传输，不先连接所有候选。后台最多保留两个额外来源候选，仅在需要时连接；每个来源必须验证证书、同一受信任发行签名、版本、包 SHA-256、包大小及块内容 ID。可恢复后的剩余载荷至少 64 MiB 才启用并行候选试探，最多三个块工作者；先测量选定来源，速度低于 32 MiB/s、仍至少有 8 MiB 未完成且当前块至少 256 KiB 时才尝试额外来源。过慢或失败的来源会退避或停用。主来源断开时，即使是小包也可尝试另一受验证的同包副本以完成传输；这属于故障回退。所有来源不可用时保留已验证内容，等待用户继续。它不接入公网，阈值是保守工程初值，尚不构成实体 LAN 加速保证。

普通文件的 P2P 与更新多来源共享 `ChunkPeerPolicy` 的大小门槛、实测速度判断、受限试探和退避；普通文件还受同批接收批准和发送者授权约束。研究依据和边界见[P2P 分发研究](p2p-distribution-research.md)。

测试项目 `Boardcast.AutomaticUpdateTests` 保留历史名称，当前覆盖默认/旧配置禁用自动下载和手动版本目录等行为；名称不表示产品仍提供自动下载。macOS 更新与正式发行证书准备本轮暂缓。