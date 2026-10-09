# Windows 签名发行与密钥保管

## 当前交付：0.2.7 / win-x64

源码版本见 `Directory.Build.props`（当前 **0.2.7**），与 `dist/` 中最新完整签名包一致。

2026-10-09 增加自适应文件行：文件名与大小、文件数量能完整放下时合为一行，否则保留两行，窗口缩放和统计变化后自动重排，首页与展开清单一致。继续保留发送页精简、接收存储、取消清理、设备分组与设置功能。

**对外完整分发包：** [Boardcast-win-x64-0.2.7.zip](../dist/Boardcast-win-x64-0.2.7.zip)  
**下载完整性校验：** [Boardcast-win-x64-0.2.7.zip.sha256](../dist/Boardcast-win-x64-0.2.7.zip.sha256)

使用原有 `boardcast-release-1` 发行密钥。已有签名旧版可在局域网选择本版下载；未签名开发版须退出旧程序，完整解压安装。外部 ZIP 内包含全部程序、运行时、公钥、签名清单与内部更新包。对外分发完整外部 ZIP 和校验文件，不单独分发 `Boardcast-update.zip` 或某个 EXE。

本包采用 Boardcast ECDSA 发行签名，Windows Authenticode 证书仍未配置。可运行发行包为 Windows x64；macOS 和 Windows ARM64 不属于本次分发验收范围。

## 验证与产物

源码测试与实际覆盖范围见 [验证记录](verification.md)。最终外部 ZIP 重新解压后核验发行签名、全部文件大小与摘要、各组件版本、公钥一致性、自包含运行时、更新启用状态、真实回环 mTLS 和 CLI/MCP；旧签名 0.2.6 到本版验证完整更新下载与待安装状态恢复。

验收结果与正式 ZIP 一并放在 `dist/`（Git LFS 跟踪 `*.zip`），本地中间产物留在被忽略的 `artifacts/`：

- [最终发行验收报告](../dist/Boardcast-win-x64-0.2.7.verification.json)
- [旧版更新下载与恢复验收报告](../dist/Boardcast-win-x64-0.2.7.upgrade-verification.json)
- [发行公钥记录](../dist/publisher-public-key.json)

仓库 `dist/` 只保留当前正式版；历史旧版不发布入库，升级验收所需旧包留在本地 `artifacts/`。真实双机、防火墙穿越、干净机器以及正式 WPF 程序目录切换不由本机回环验收替代。

## 首次安装与后续升级

从可信渠道取得 ZIP，退出旧程序及后台，完整解压到干净、当前用户可写且父目录可写的位置，再运行 `Boardcast.exe`。保留 `release-trust.json`、`release-manifest.json` 与 `Boardcast-update.zip`。

未签名开发包须手动换装。后续发行版本必须严格递增，并使用相同公钥与 keyId；同版本重新构建不视为更新。局域网中需要至少一台运行本版的设备提供新版，用户选择下载并点击“重启并更新”。用户数据在 `%LOCALAPPDATA%\Boardcast`，与程序目录分开；不分发发布机器的配置、接收数据或设备 `identity.pfx`。

## 发行密钥：发布者保管

- keyId：`boardcast-release-1`
- 公钥 SHA-256（SubjectPublicKeyInfo DER）：`378df73141c35ef6d5754067c28ce58d637670c04550a5fbbc1e13fcfc465722`
- 本机私钥：`C:\Users\Administrator\AppData\Local\BoardcastRelease\keys\boardcast-release-1-private.pem`

私钥位于项目与发行目录之外，受限目录仅允许当前 Windows 账户和 SYSTEM 访问。私钥不进入分发包或日志。该位置不是离线备份，请将原私钥另行备份到受保护的离线介质；后续继续使用该私钥与相同 keyId，不要重新生成替代密钥。当前更新器拒绝发行身份变化。

## 后续发布与复验

使用 PowerShell 7 和 .NET 10 SDK，先完成源码测试，再用全新目录构建。下例中的 0.2.8 须替换为实际待发布版本：

~~~powershell
./scripts/Test.ps1
$keyPath = 'C:\Users\Administrator\AppData\Local\BoardcastRelease\keys\boardcast-release-1-private.pem'
if (-not (Test-Path -LiteralPath $keyPath)) { throw '发行私钥缺失：恢复原私钥，不要生成新密钥。' }
# 本地签名构建写入 artifacts（已 gitignore）；勿直接覆盖 dist 中已发布文件
./scripts/Publish-Windows.ps1 -Runtime win-x64 -Version 0.2.8 -OutputDirectory './artifacts/releases/Boardcast-win-x64-0.2.8' -SigningKeyPem $keyPath -ReleaseKeyId boardcast-release-1

./scripts/Test-Release.ps1 -ReleaseZip './artifacts/releases/Boardcast-win-x64-0.2.8.zip' -ExpectedVersion 0.2.8
# 升级验收需要已解压目录；可从 dist ZIP 解压到 artifacts，或沿用本地 artifacts/releases 解压树
./scripts/Test-ReleaseUpgrade.ps1 -OldReleaseDirectory './artifacts/releases/Boardcast-win-x64-0.2.7' -NewReleaseDirectory './artifacts/releases/Boardcast-win-x64-0.2.8'
# 验收通过后：将 zip / sha256 / verification JSON 复制到 dist/，再 git add（LFS）
./scripts/Clean-Workspace.ps1 -KeepVersion 0.2.8 -Apply
~~~

清理脚本先检查 `artifacts/releases` 中的完整包、摘要和验收报告，限定项目内生成内容，跳过仍在运行的目录，并核对关键源码文件清理前后摘要一致。完成验收后把最终 ZIP 与旁路文件复制到 `dist/` 再分发，不再修改已签名内容。将来采用 Windows Authenticode 时须先签 EXE，再生成更新载荷、发行清单与外部 ZIP。

GitHub 默认构建未传发行签名参数，仍产出开发包；请以 `dist/` 中经过验收的正式签名产物为准。
