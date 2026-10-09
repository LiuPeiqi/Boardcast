import AppKit
import SwiftUI
import ServiceManagement

@main
@MainActor
struct BoardcastApp: App {
    @StateObject private var model = AppModel()
    @NSApplicationDelegateAdaptor(AppDelegate.self) private var delegate
    var body: some Scene {
        WindowGroup("Boardcast") {
            MainView(model: model)
                .frame(minWidth: 760, minHeight: 500)
                .task { model.start() }
        }
        Settings { SettingsView(model: model).frame(width: 600, height: 540) }
        MenuBarExtra("Boardcast", systemImage: "square.and.arrow.down") {
            Text(model.settings.receivingEnabled ? "可接收局域网文件" : "接收已暂停")
            Button("显示 Boardcast") {
                NSApp.activate(ignoringOtherApps: true)
                NSApp.windows.first(where: { $0.canBecomeMain })?.makeKeyAndOrderFront(nil)
            }
            Button(model.settings.receivingEnabled ? "暂停接收" : "恢复接收") {
                model.settings.receivingEnabled.toggle()
                model.save()
            }
            Divider()
            Button("退出 Boardcast") { NSApp.terminate(nil) }.keyboardShortcut("q")
        }
    }
}

final class AppDelegate: NSObject, NSApplicationDelegate {
    func applicationShouldTerminateAfterLastWindowClosed(_ sender: NSApplication) -> Bool { false }
    func applicationShouldHandleReopen(_ sender: NSApplication, hasVisibleWindows flag: Bool) -> Bool {
        sender.windows.first(where: { $0.canBecomeMain })?.makeKeyAndOrderFront(nil)
        return true
    }
}

@MainActor
final class AppModel: ObservableObject {
    @Published var settings = AppSettings()
    @Published var peers: [PeerInfo] = []
    @Published var pending: [PendingOffer] = []
    @Published var status = "正在准备局域网服务"
    @Published var fingerprint = ""
    @Published var selectedPeer: String?
    @Published var busy = false
    // Network callbacks use only successfully loaded or saved settings, never text-field drafts.
    private var activeSettings = AppSettings()
    private var store: SettingsStore?
    private var identity: DeviceIdentity?
    private var discovery: LANDiscovery?
    private var transport: TransferService?
    private var lastSeen: [String: Date] = [:]
    private var started = false

    func start() {
        guard !started else { return }
        started = true
        do {
            let store = try SettingsStore()
            self.store = store
            settings = try store.load()
            activeSettings = settings
            let identity = try DeviceIdentity()
            self.identity = identity
            fingerprint = identity.fingerprint
            let service = TransferService(identity: identity, settings: { [weak self] in await self?.activeSettings ?? AppSettings() })
            service.onStatus = { [weak self] message in Task { @MainActor in self?.status = message } }
            service.onOffer = { [weak self] offer, fingerprint, decision in
                await withCheckedContinuation { continuation in
                    Task { @MainActor in
                        guard let self else { continuation.resume(returning: false); return }
                        self.pending.append(.init(offer: offer, fingerprint: fingerprint, decision: decision))
                        self.approvals[offer.id] = continuation
                        Task { @MainActor [weak self] in
                            try? await Task.sleep(nanoseconds: 120_000_000_000)
                            self?.approvals.removeValue(forKey: offer.id)?.resume(returning: false)
                            self?.pending.removeAll { $0.id == offer.id }
                        }
                    }
                }
            }
            try service.start(port: settings.port)
            transport = service
            let announcement = discoveryMessage()
            let discovery = LANDiscovery(message: { announcement })
            discovery.onPeer = { [weak self] peer in Task { @MainActor in self?.updatePeer(peer) } }
            discovery.onTick = { [weak self] in Task { @MainActor in self?.expirePeers() } }
            try discovery.start()
            self.discovery = discovery
            status = "仅在局域网内等待连接"
        } catch { status = error.localizedDescription }
    }
    private var approvals: [String: CheckedContinuation<Bool, Never>] = [:]

    func decide(_ request: PendingOffer, accepted: Bool) {
        approvals.removeValue(forKey: request.id)?.resume(returning: accepted)
        pending.removeAll { $0.id == request.id }
    }
    private func discoveryMessage() -> DiscoveryMessage {
        DiscoveryMessage(fingerprint: fingerprint, name: activeSettings.deviceName, groupName: DeviceGroups.normalized(activeSettings.groupName), port: activeSettings.port, receivingEnabled: activeSettings.receivingEnabled)
    }
    private func expirePeers() {
        peers.removeAll { Date().timeIntervalSince(lastSeen[$0.id] ?? .distantPast) > 150 }
        lastSeen = lastSeen.filter { Date().timeIntervalSince($0.value) <= 150 }
        clearHiddenSelection()
    }
    private func updatePeer(_ peer: PeerInfo) {
        guard lastSeen[peer.id] != nil || lastSeen.count < 256 else { return }
        lastSeen[peer.id] = Date()
        peers.removeAll { Date().timeIntervalSince(lastSeen[$0.id] ?? .distantPast) > 150 }
        if let index = peers.firstIndex(where: { $0.id == peer.id }) { peers[index] = peer }
        else if peers.count < 256 { peers.append(peer) }
        clearHiddenSelection()
    }
    func save() {
        do {
            try DeviceGroups.validate(settings)
            settings.groupName = DeviceGroups.normalized(settings.groupName)
            for index in settings.devices.indices {
                settings.devices[index].groupName = DeviceGroups.normalized(settings.devices[index].groupName)
            }
            var groups: [String] = []
            for group in settings.visibleGroups {
                let name = DeviceGroups.normalized(group)
                if !groups.contains(where: { DeviceGroups.same($0, name) }) { groups.append(name) }
            }
            settings.visibleGroups = groups
            try store?.save(settings)
            activeSettings = settings
            clearHiddenSelection()
            let announcement = discoveryMessage()
            discovery?.setMessage(announcement)
            discovery?.announce()
            status = "设置已保存"
        } catch { status = error.localizedDescription }
    }
    var visiblePeers: [PeerInfo] { peers.filter { DeviceGroups.isVisible($0, settings: settings) } }
    var availableGroups: [String] { DeviceGroups.available(peers: peers, settings: settings) }
    var localGroup: String { DeviceGroups.local(settings) }
    var canSend: Bool { !busy && visiblePeers.contains { $0.id == selectedPeer } }
    func groupName(_ peer: PeerInfo) -> String { DeviceGroups.peer(peer, settings: settings) }
    func clearHiddenSelection() {
        if !visiblePeers.contains(where: { $0.id == selectedPeer }) { selectedPeer = nil }
    }
    func setGroupFilter(_ filter: PeerGroupFilter) {
        settings.peerGroupFilter = filter
        save()
    }
    func groupSelected(_ group: String) -> Bool {
        settings.visibleGroups.contains { DeviceGroups.same($0, group) }
    }
    func setGroupSelected(_ group: String, selected: Bool) {
        settings.visibleGroups.removeAll { DeviceGroups.same($0, group) }
        if selected {
            guard settings.visibleGroups.count < DeviceGroups.maximumVisibleGroups else {
                status = "最多选择 128 个分组"
                return
            }
            settings.visibleGroups.append(group)
        }
        save()
    }

    func displayName(_ peer: PeerInfo) -> String {
        settings.devices.first(where: { $0.id.caseInsensitiveCompare(peer.id) == .orderedSame })?.displayName ?? peer.name
    }
    func remember(_ peer: PeerInfo) {
        guard !settings.devices.contains(where: { $0.id.caseInsensitiveCompare(peer.id) == .orderedSame }) else { return }
        settings.devices.append(KnownDevice(fingerprint: peer.fingerprint, name: peer.name, lastAddress: peer.address, port: peer.port))
        save()
    }
    func sendFiles() {
        guard let peer = visiblePeers.first(where: { $0.id == selectedPeer }), let transport, !busy else { return }
        let panel = NSOpenPanel()
        panel.canChooseFiles = true; panel.canChooseDirectories = true; panel.allowsMultipleSelection = true
        panel.prompt = "发送"
        guard panel.runModal() == .OK else { return }
        guard let currentPeer = visiblePeers.first(where: { $0.id == peer.id }), selectedPeer == peer.id else {
            status = "目标设备已离线或已被分组筛选隐藏，请重新选择。"
            return
        }
        busy = true
        let urls = panel.urls
        Task {
            do { try await transport.send(urls: urls, to: currentPeer); status = "传输完成" }
            catch { status = error.localizedDescription }
            busy = false
        }
    }
    func chooseReceiveDirectory() {
        let panel = NSOpenPanel()
        panel.canChooseFiles = false; panel.canChooseDirectories = true; panel.canCreateDirectories = true
        guard panel.runModal() == .OK, let url = panel.url else { return }
        settings.receiveDirectory = url.path
        save()
    }
    func setLogin(_ enabled: Bool) {
        do {
            if enabled { try SMAppService.mainApp.register() }
            else { try SMAppService.mainApp.unregister() }
        } catch { status = error.localizedDescription }
    }
}

struct MainView: View {
    @ObservedObject var model: AppModel
    @State private var showDevices = false
    var body: some View {
        VStack(alignment: .leading, spacing: 24) {
            HStack(alignment: .firstTextBaseline) {
                VStack(alignment: .leading, spacing: 5) {
                    Text("Boardcast").font(.system(size: 32, weight: .bold, design: .rounded))
                    Text("同一网络，直接送达。").foregroundStyle(.secondary)
                }
                Spacer()
                Circle().fill(model.settings.receivingEnabled ? Color.blue : Color.secondary).frame(width: 10, height: 10)
                Text(model.settings.receivingEnabled ? "可接收" : "已暂停").font(.callout)
            }
            if !model.pending.isEmpty {
                ForEach(model.pending) { request in
                    VStack(alignment: .leading, spacing: 8) {
                        Text("\(request.offer.senderName) 想发送 \(request.offer.entries.count) 项").font(.headline)
                        if !request.offer.summary.isEmpty { Text(request.offer.summary).lineLimit(4).textSelection(.enabled) }
                        Text("指纹：\(request.fingerprint)").font(.system(.caption, design: .monospaced)).textSelection(.enabled)
                        Text("保存至 \(request.decision.destination)").font(.caption).foregroundStyle(.secondary)
                        HStack {
                            Button("拒绝") { model.decide(request, accepted: false) }
                            Button("接收") { model.decide(request, accepted: true) }.buttonStyle(.borderedProminent)
                        }
                    }.padding().frame(maxWidth: .infinity, alignment: .leading).background(Color.blue.opacity(0.08), in: RoundedRectangle(cornerRadius: 8))
                }
            }
            VStack(alignment: .leading, spacing: 8) {
                HStack {
                    Text("附近设备").font(.headline)
                    Spacer()
                    Text("显示 \(model.visiblePeers.count) / \(model.peers.count) 台").font(.caption).foregroundStyle(.secondary)
                }
                Picker("显示分组", selection: Binding(get: { model.settings.peerGroupFilter }, set: model.setGroupFilter)) {
                    ForEach(PeerGroupFilter.allCases, id: \.self) { filter in
                        Text(filter.title).tag(filter)
                    }
                }.pickerStyle(.segmented)
                HStack {
                    Text("我的分组：\(DeviceGroups.label(model.localGroup))").font(.caption).foregroundStyle(.secondary)
                    Spacer()
                    if model.settings.peerGroupFilter == .selectedGroups {
                        Menu("选择分组（\(model.settings.visibleGroups.count)）") {
                            ForEach(model.availableGroups, id: \.self) { group in
                                Toggle(DeviceGroups.label(group), isOn: Binding(
                                    get: { model.groupSelected(group) },
                                    set: { model.setGroupSelected(group, selected: $0) }
                                ))
                            }
                        }
                    }
                }
            }
            if model.visiblePeers.isEmpty {
                VStack(spacing: 12) {
                    Image(systemName: "desktopcomputer").font(.system(size: 42)).foregroundStyle(.secondary)
                    Text(model.peers.isEmpty ? "等待同一局域网的设备" : "当前分组中没有在线设备").font(.headline)
                    Text(model.peers.isEmpty ? "在另一台设备上打开 Boardcast。" : "切换为全部设备，或选择其他分组。").foregroundStyle(.secondary)
                    if model.settings.peerGroupFilter != .all {
                        Button("显示全部设备") { model.setGroupFilter(.all) }
                    }
                }.frame(maxWidth: .infinity, maxHeight: .infinity)
            } else {
                List(model.visiblePeers, selection: $model.selectedPeer) { peer in
                    HStack(spacing: 16) {
                        Image(systemName: peer.platform.lowercased().contains("mac") ? "laptopcomputer" : "desktopcomputer").font(.title)
                        VStack(alignment: .leading, spacing: 4) {
                            Text(model.displayName(peer)).font(.headline)
                            Text("\(peer.platform) · \(peer.address)").font(.caption).foregroundStyle(.secondary)
                            Text("分组：\(DeviceGroups.label(model.groupName(peer)))").font(.caption).foregroundStyle(.secondary)
                        }
                        Spacer()
                        Button("备注、分组与信任") { model.remember(peer); showDevices = true }
                    }.padding(.vertical, 8).tag(peer.id)
                }.listStyle(.inset)
            }
            HStack {
                Text(model.status).font(.caption).foregroundStyle(.secondary).lineLimit(3)
                Spacer()
                Button("发送文件或文件夹…") { model.sendFiles() }
                    .buttonStyle(.borderedProminent)
                    .disabled(!model.canSend)
                    .keyboardShortcut("o", modifiers: .command)
            }
        }.padding(28)
        .onChange(of: model.visiblePeers.map(\.id)) { _ in model.clearHiddenSelection() }
        .sheet(isPresented: $showDevices) { DeviceSettings(model: model).padding(24).frame(width: 600, height: 400) }
    }
}

struct SettingsView: View {
    @ObservedObject var model: AppModel
    var body: some View {
        TabView {
            Form {
                TextField("设备名称", text: $model.settings.deviceName)
                Section("分组") {
                    TextField("我的分组（留空自动匹配名称前缀）", text: $model.settings.groupName)
                    Text("当前分组：\(DeviceGroups.label(model.localGroup))；名称按 -、_、. 或空白分隔。").font(.caption).foregroundStyle(.secondary)
                    Picker("附近设备默认显示", selection: $model.settings.peerGroupFilter) {
                        ForEach(PeerGroupFilter.allCases, id: \.self) { filter in
                            Text(filter.title).tag(filter)
                        }
                    }
                    Text("选择“与我同组”并保存，重启后也只显示同组设备；可在附近设备中随时切回全部。").font(.caption).foregroundStyle(.secondary)
                }
                Toggle("允许接收", isOn: $model.settings.receivingEnabled)
                LabeledContent("保存位置") {
                    Text(model.settings.receiveDirectory).lineLimit(2)
                    Button("选择…") { model.chooseReceiveDirectory() }
                }
                Toggle("登录时启动", isOn: Binding(get: { SMAppService.mainApp.status == .enabled }, set: model.setLogin))
                Text("本机身份指纹").font(.caption)
                Text(model.fingerprint).font(.system(.caption, design: .monospaced)).textSelection(.enabled)
                Button("保存") { model.save() }.buttonStyle(.borderedProminent)
            }.padding().tabItem { Label("通用", systemImage: "gearshape") }
            DeviceSettings(model: model).tabItem { Label("设备", systemImage: "desktopcomputer") }
            RuleSettings(model: model).tabItem { Label("规则", systemImage: "line.3.horizontal.decrease.circle") }
        }.padding()
    }
}

struct DeviceSettings: View {
    @ObservedObject var model: AppModel
    var body: some View {
        VStack(alignment: .leading) {
            Text("信任前请在两台设备上核对完整指纹。").font(.caption).foregroundStyle(.secondary)
            List {
                ForEach($model.settings.devices) { $device in
                    VStack(alignment: .leading, spacing: 8) {
                        Text(device.name).font(.headline)
                        TextField("本机备注", text: $device.alias)
                        TextField("个人分组（留空跟随对方分组或设备名称前缀）", text: $device.groupName)
                        Text("个人分组仅对本机可见，不影响设备信任与接收规则。").font(.caption).foregroundStyle(.secondary)
                        Text(device.fingerprint).font(.system(.caption2, design: .monospaced)).textSelection(.enabled)
                        Toggle("已核对身份，信任此设备", isOn: $device.trusted)
                        Toggle("允许匹配规则后自动接收", isOn: $device.autoAccept).disabled(!device.trusted)
                    }.padding(.vertical, 8)
                }
            }
            Button("保存") { model.save() }.buttonStyle(.borderedProminent)
        }
    }
}

struct RuleSettings: View {
    @ObservedObject var model: AppModel
    var body: some View {
        VStack(alignment: .leading) {
            Text("按顺序匹配第一条规则；自动接收还需要设备信任及授权。").font(.caption)
            List {
                ForEach($model.settings.rules) { $rule in
                    VStack(alignment: .leading, spacing: 8) {
                        TextField("规则名", text: $rule.name)
                        HStack {
                            Toggle("启用", isOn: $rule.enabled)
                            Toggle("自动接收", isOn: $rule.autoAccept)
                        }
                        Picker("内容", selection: $rule.scope) {
                            Text("全部").tag(EntryScope.any)
                            Text("单独文件").tag(EntryScope.files)
                            Text("文件夹").tag(EntryScope.folders)
                        }
                        TextField("后缀（逗号分隔，留空不限）", text: Binding(
                            get: { rule.extensions.joined(separator: ", ") },
                            set: { rule.extensions = $0.split(separator: ",").map { $0.trimmingCharacters(in: .whitespaces) } }
                        ))
                        TextField("文件夹名称，如 Photo*", text: $rule.folderNamePattern)
                        TextField("保存路径（留空使用默认）", text: $rule.destination)
                        Button("删除规则", role: .destructive) { model.settings.rules.removeAll { $0.id == rule.id } }
                    }.padding(.vertical, 8)
                }
                .onMove { model.settings.rules.move(fromOffsets: $0, toOffset: $1) }
            }
            HStack {
                Button("添加规则") { model.settings.rules.append(ReceiveRule()) }
                Spacer()
                Button("保存") { model.save() }.buttonStyle(.borderedProminent)
            }
        }
    }
}
