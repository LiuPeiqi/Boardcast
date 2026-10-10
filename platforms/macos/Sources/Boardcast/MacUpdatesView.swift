import SwiftUI

/// Presents the same verified LAN update workflow in Settings and device details.
@MainActor
struct MacUpdatesView: View {
    @ObservedObject var model: AppModel
    @ObservedObject var service: MacUpdateService
    @ObservedObject private var tasks: TransferStore
    @ObservedObject private var draft: SendDraft

    init(model: AppModel, service: MacUpdateService) {
        self.model = model
        self.service = service
        self.tasks = model.tasks
        self.draft = model.draft
    }

    private var installedVersion: String {
        Bundle.main.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String
            ?? L10n.string("版本信息不可用")
    }
    private var sources: [PeerInfo] { service.candidates(peers: model.peers) }
    private var canInstall: Bool {
        service.phase == .ready && service.readyVersion != nil
            && !model.terminationPending && !model.hasActiveOperationsAndTasks
    }

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 18) {
                Text("局域网 Mac 更新", bundle: ResourceBundle.resources).font(.title2)
                LabeledContent(L10n.string("当前运行版本")) { Text(verbatim: installedVersion).textSelection(.enabled) }
                LabeledContent(L10n.string("更新包架构")) { Text(verbatim: "osx-universal · arm64 + x86_64") }
                Text(verbatim: service.status).textSelection(.enabled)
                    .accessibilityLabel(Text(verbatim: service.status))

                if service.phase == .downloading {
                    ProgressView(value: service.progress, total: 1)
                        .accessibilityLabel(Text("正在下载更新", bundle: ResourceBundle.resources))
                    Text(service.progress, format: .percent.precision(.fractionLength(0)))
                        .font(.caption).monospacedDigit()
                } else if service.phase == .publishing || service.phase == .verifying {
                    ProgressView().accessibilityLabel(Text("正在准备或验证更新", bundle: ResourceBundle.resources))
                }
                if service.phase == .publishing || service.phase == .downloading || service.phase == .verifying {
                    Button { service.cancel() } label: { Text("取消更新操作", bundle: ResourceBundle.resources) }.disabled(model.terminationPending)
                }

                if let version = service.readyVersion, service.phase == .ready {
                    Divider()
                    Text(verbatim: L10n.format("更新 %@ 已验证并准备安装", version)).font(.headline)
                    Button { model.installUpdate() } label: {
                        Text("重新启动并安装", bundle: ResourceBundle.resources)
                    }.buttonStyle(.borderedProminent).disabled(!canInstall)
                    if !canInstall {
                        Text("请等待文件传输、来件确认、统计和更新操作结束后安装。", bundle: ResourceBundle.resources)
                            .font(.caption).foregroundStyle(.secondary)
                    }
                    Text(verbatim: model.status).font(.caption).textSelection(.enabled)
                }

                Divider()
                Text("可用更新来源", bundle: ResourceBundle.resources).font(.headline)
                if sources.isEmpty {
                    Text("尚未发现在线、兼容且版本更高的 Mac 更新来源。", bundle: ResourceBundle.resources)
                        .foregroundStyle(.secondary)
                } else {
                    LazyVStack(alignment: .leading, spacing: 12) {
                        ForEach(sources) { peer in
                            HStack(alignment: .top, spacing: 12) {
                                PlatformIcon(platform: peer.platform, size: 20).accessibilityHidden(true)
                                VStack(alignment: .leading, spacing: 5) {
                                    Text(verbatim: model.displayName(peer)).font(.headline)
                                    Text(verbatim: L10n.format("提供更新 %@", peer.updateVersion ?? ""))
                                    Text(verbatim: "\(peer.address):\(peer.updatePort)")
                                        .font(.caption).foregroundStyle(.secondary).textSelection(.enabled)
                                    if let running = peer.appVersion {
                                        Text(verbatim: L10n.format("来源运行版本 %@", running))
                                            .font(.caption).foregroundStyle(.secondary)
                                    }
                                }.frame(maxWidth: .infinity, alignment: .leading)
                                Button { Task { await service.download(peer: peer) } } label: {
                                    Text("下载更新", bundle: ResourceBundle.resources)
                                }.disabled(service.hasActiveOperations || model.terminationPending)
                            }
                            Divider()
                        }
                    }
                }
                Text("点击下载后才向所选局域网设备请求更新。设备身份信任不能替代发行签名、公证和更新包校验。", bundle: ResourceBundle.resources)
                    .font(.caption).foregroundStyle(.secondary)
            }.padding(24).frame(maxWidth: .infinity, alignment: .leading)
        }
    }
}

/// Observes the update service directly so an advertisement change refreshes the device row.
@MainActor
struct PeerUpdateBadge: View {
    @ObservedObject var service: MacUpdateService
    let peer: PeerInfo
    let showDetails: () -> Void
    var body: some View {
        if let candidate = service.candidates(peers: [peer]).first, let version = candidate.updateVersion {
            Button(action: showDetails) {
                Text(verbatim: version).font(.caption).lineLimit(1).padding(.horizontal, 7).padding(.vertical, 3)
                    .foregroundStyle(Color.blue)
                    .background(Color.blue.opacity(0.12), in: Capsule())
            }.buttonStyle(.plain).frame(maxWidth: 80)
                .help(L10n.format("运行版本 %@；提供 Mac 更新 %@。点击查看详情。", peer.appVersion ?? L10n.string("未知"), version))
                .accessibilityLabel(Text(verbatim: L10n.format("查看来自 %@ 的 Mac 更新 %@", peer.name, version)))
        } else if let version = peer.appVersion, !version.isEmpty {
            Text(verbatim: version).font(.caption).foregroundStyle(.secondary).lineLimit(1)
                .frame(maxWidth: 72).help(L10n.format("运行版本 %@", version))
        }
    }
}
