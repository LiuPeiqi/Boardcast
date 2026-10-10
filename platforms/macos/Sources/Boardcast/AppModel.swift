import AppKit
import Foundation
import Combine
import ServiceManagement

/// Owns UI state and composes the existing protocol, policy and platform services.
@MainActor
final class AppModel: ObservableObject {
    @Published private(set) var settings = AppSettings()
    @Published private(set) var peers: [PeerInfo] = []
    @Published private(set) var pending: [PendingOffer] = []
    @Published private(set) var status = L10n.string("正在准备局域网服务")
    @Published private(set) var fingerprint = ""
    @Published private(set) var updates: MacUpdateService?
    @Published private(set) var selectedPeers = Set<String>()
    @Published var peerSearch = ""
    @Published var fileSearch = ""
    @Published private(set) var terminationPending = false
    @Published private(set) var loginStatus = SMAppService.mainApp.status
    let draft = SendDraft()
    let tasks = TransferStore()

    private var store: SettingsStore?
    private var discovery: LANDiscovery?
    private var transport: TransferService?
    private var lastSeen: [String: Date] = [:]
    private var sendTasks: [String: Task<Void, Never>] = [:]
    private var approvals: [String: CheckedContinuation<OfferApproval, Never>] = [:]
    private var approvalTimers: [String: Task<Void, Never>] = [:]
    private var wakeObserver: NSObjectProtocol?
    private var started = false
    private var updateStartTask: Task<Void, Never>?
    private var updateObservation: AnyCancellable?

    init() {
        guard ProcessInfo.processInfo.environment["BOARDCAST_TESTING"] != "1" else { return }
        start()
    }

    func start() {
        guard !started else { return }
        do {
            let store = try SettingsStore()
            let loaded = try store.load()
            self.store = store
            settings = loaded
            tasks.configure(directory: store.directory)
            let cacheRoot = store.directory.appendingPathComponent("Transfers", isDirectory: true)
            Task.detached(priority: .utility) { ReceiveStorage.pruneExpiredTasks(at: cacheRoot) }
            let identity = try DeviceIdentity()
            fingerprint = identity.fingerprint
            let transport = TransferService(identity: identity, settings: { [weak self] in
                await self?.runtimeSettings() ?? AppSettings()
            })
            transport.onStatus = { [weak self] message in
                Task { @MainActor in self?.status = message }
            }
            transport.onProgress = { [weak self] progress in
                await MainActor.run {
                    guard let self else { return }
                    let name = progress.peerName ?? self.peers.first(where: { $0.fingerprint == progress.peerFingerprint })?.name
                    self.tasks.update(progress, peerName: name)
                }
            }
            transport.onConnectionsChanged = { [weak self] in
                Task { @MainActor in self?.objectWillChange.send() }
            }
            transport.onOffer = { [weak self] offer, fingerprint, decision in
                await self?.requestApproval(offer, fingerprint: fingerprint, decision: decision) ?? .reject
            }
            try transport.start(port: settings.port)
            self.transport = transport
            let discovery = LANDiscovery(message: { self.discoveryMessage() })
            discovery.onPeer = { [weak self] peer in Task { @MainActor in self?.updatePeer(peer) } }
            discovery.onDeparture = { [weak self] peer in Task { @MainActor in self?.markDeparture(peer) } }
            discovery.onTick = { [weak self] in Task { @MainActor in self?.expirePeers() } }
            discovery.onError = { [weak self] message in Task { @MainActor in self?.status = message } }
            try discovery.start()
            self.discovery = discovery
            let updater = MacUpdateService(identity: identity, dataDirectory: store.directory)
            updates = updater
            updater.onAdvertisement = { [weak self] in
                guard let self else { return }
                self.discovery?.setMessage(self.discoveryMessage())
                self.discovery?.announce()
            }
            updateObservation = updater.objectWillChange.sink { [weak self] _ in self?.objectWillChange.send() }
            updateStartTask = Task { [weak self] in
                defer {
                    if let self, self.updates === updater {
                        self.updateStartTask = nil
                        self.objectWillChange.send()
                    }
                }
                await updater.start()
            }
            wakeObserver = NSWorkspace.shared.notificationCenter.addObserver(
                forName: NSWorkspace.didWakeNotification, object: nil, queue: .main
            ) { [weak self] _ in Task { @MainActor in self?.refreshPeers() } }
            started = true
            try? MacRelease.acknowledgeStartup(bundle: .main)
            status = L10n.string("仅在局域网内等待连接")
        } catch {
            updateStartTask?.cancel()
            updateStartTask = nil
            updates?.stop()
            transport?.stop()
            transport = nil
            discovery?.stop()
            discovery = nil
            status = L10n.string(error.localizedDescription)
        }
    }

    private func runtimeSettings() -> AppSettings {
        var value = settings
        if terminationPending { value.receivingEnabled = false }
        return value
    }

    private func discoveryMessage() -> DiscoveryMessage {
        DiscoveryMessage(fingerprint: fingerprint, name: settings.deviceName,
                         groupName: DeviceGroups.normalized(settings.groupName), port: settings.port,
                         receivingEnabled: settings.receivingEnabled && !terminationPending,
                         runtime: updates?.advertisedRuntime,
                         updatePort: terminationPending ? 0 : updates?.advertisedPort ?? 0,
                         updateVersion: terminationPending ? nil : updates?.advertisedVersion)
    }

    func stop() {
        terminationPending = true
        for request in pending { decide(request, accepted: false) }
        draft.cancelScanning()
        for (id, handle) in sendTasks {
            handle.cancel()
            transport?.cancel(id: id)
            tasks.finish(id: id, phase: .interrupted, message: L10n.string("应用已停止传输。"))
        }
        sendTasks.removeAll()
        updateStartTask?.cancel()
        updateStartTask = nil
        updates?.stop()
        transport?.stop()
        discovery?.stop()
        if let wakeObserver { NSWorkspace.shared.notificationCenter.removeObserver(wakeObserver) }
        wakeObserver = nil
        started = false
    }

    var hasActiveOperationsAndTasks: Bool {
        tasks.tasks.contains(where: \.isActive) || !pending.isEmpty || draft.isScanning ||
        !sendTasks.isEmpty || updateStartTask != nil || transport?.hasActiveConnections == true || updates?.hasActiveOperations == true
    }

    func beginWaitingForTermination() {
        terminationPending = true
        for request in pending { decide(request, accepted: false) }
        transport?.quiesce()
        updates?.quiesce()
        discovery?.setMessage(discoveryMessage())
        discovery?.announce()
    }

    func cancelWaitingForTermination() {
        terminationPending = false
        do { try transport?.start(port: settings.port) }
        catch { status = L10n.string(error.localizedDescription) }
        Task { await updates?.resumeSharing() }
        discovery?.setMessage(discoveryMessage())
        discovery?.announce()
    }

    func installUpdate() {
        guard !terminationPending, !hasActiveOperationsAndTasks, let updates, updates.readyVersion != nil else {
            status = L10n.string("请先结束所有文件传输、来件确认和更新下载，再安装已验证的更新。")
            return
        }
        beginWaitingForTermination()
        Task {
            do {
                let helper = try await updates.prepareInstall()
                guard helper.isRunning else { throw BoardcastError.invalid(L10n.string("更新安装器未能启动。")) }
                NSApp.terminate(nil)
            } catch {
                cancelWaitingForTermination()
                status = L10n.string(error.localizedDescription)
            }
        }
    }

    private func requestApproval(_ offer: TransferOffer, fingerprint: String,
                                 decision: ReceiveDecision) async -> OfferApproval {
        let request = PendingOffer(offer: offer, fingerprint: fingerprint, decision: decision)
        return await withTaskCancellationHandler(operation: {
            await withCheckedContinuation { continuation in
                guard !Task.isCancelled, !terminationPending, pending.count < 2,
                      approvals[request.id] == nil else {
                    continuation.resume(returning: .reject)
                    return
                }
                approvals[request.id] = continuation
                pending.append(request)
                approvalTimers[request.id] = Task { @MainActor [weak self] in
                    do { try await Task.sleep(nanoseconds: 120_000_000_000) }
                    catch { return }
                    self?.decide(request, accepted: false)
                }
            }
        }, onCancel: { [weak self] in
            Task { @MainActor in self?.decide(request, accepted: false) }
        })
    }

    func decide(_ request: PendingOffer, accepted: Bool, destination: String? = nil) {
        guard let continuation = approvals.removeValue(forKey: request.id) else { return }
        approvalTimers.removeValue(forKey: request.id)?.cancel()
        pending.removeAll { $0.id == request.id }
        let valid = accepted && !terminationPending && request.expiresAt > Date()
        continuation.resume(returning: valid ? .accept(destination: destination) : .reject)
    }

    func cancelTask(_ id: String) {
        if let request = pending.first(where: { $0.id == id }) { decide(request, accepted: false) }
        sendTasks[id]?.cancel()
        transport?.cancel(id: id)
        tasks.finish(id: id, phase: .cancelled, message: L10n.string("已取消本机任务；已保存文件保留。"))
    }

    func refreshPeers() {
        if !started { start() }
        discovery?.probe()
        expirePeers()
        loginStatus = SMAppService.mainApp.status
    }

    private func updatePeer(_ peer: PeerInfo) {
        guard lastSeen[peer.id] != nil || lastSeen.count < 256 else { return }
        lastSeen[peer.id] = Date()
        if let index = peers.firstIndex(where: { $0.id == peer.id }) { peers[index] = peer }
        else { peers.append(peer) }
        clearHiddenSelection()
    }

    private func markDeparture(_ hint: PeerInfo) {
        guard let index = peers.firstIndex(where: {
            $0.fingerprint == hint.fingerprint && $0.address == hint.address && $0.port == hint.port
        }) else { return }
        peers[index].departureHint = true
        selectedPeers.remove(hint.id)
    }

    private func expirePeers() {
        peers.removeAll { Date().timeIntervalSince(lastSeen[$0.id] ?? .distantPast) > 150 }
        lastSeen = lastSeen.filter { Date().timeIntervalSince($0.value) <= 150 }
        clearHiddenSelection()
    }

    /// Search hides rows without discarding selected recipients. Group changes do discard them.
    private var groupedPeers: [PeerInfo] {
        peers.filter { DeviceGroups.isVisible($0, settings: settings) }
    }
    var visiblePeers: [PeerInfo] {
        groupedPeers.filter {
            peerSearch.isEmpty || displayName($0).localizedCaseInsensitiveContains(peerSearch)
                || $0.name.localizedCaseInsensitiveContains(peerSearch)
                || $0.address.localizedCaseInsensitiveContains(peerSearch)
                || groupName($0).localizedCaseInsensitiveContains(peerSearch)
        }.sorted { displayName($0).localizedCaseInsensitiveCompare(displayName($1)) == .orderedAscending }
    }
    var availableGroups: [String] { DeviceGroups.available(peers: peers, settings: settings) }
    var localGroup: String { DeviceGroups.local(settings) }
    var canSend: Bool {
        transport != nil && !terminationPending && !draft.isScanning && !draft.items.isEmpty
            && groupedPeers.contains { selectedPeers.contains($0.id) && $0.lanOnline && $0.receivingEnabled && !$0.departureHint }
            && draft.items.allSatisfy { $0.unresolvedIssues.isEmpty }
            && draft.items.contains { !$0.includedEntries.isEmpty }
            && sendTasks.count < 128
    }

    func groupName(_ peer: PeerInfo) -> String { DeviceGroups.peer(peer, settings: settings) }
    func displayName(_ peer: PeerInfo) -> String {
        guard let device = settings.devices.first(where: { $0.fingerprint.caseInsensitiveCompare(peer.fingerprint) == .orderedSame }) else { return peer.name }
        return device.alias.trimmingCharacters(in: .whitespaces).isEmpty ? peer.name : device.alias
    }
    func togglePeer(_ peer: PeerInfo) {
        if selectedPeers.contains(peer.id) { selectedPeers.remove(peer.id) }
        else if peer.lanOnline && peer.receivingEnabled && !peer.departureHint && selectedPeers.count < 32 {
            selectedPeers.insert(peer.id)
        }
    }
    private func clearHiddenSelection() {
        let allowed = Set(groupedPeers.filter { $0.lanOnline && $0.receivingEnabled && !$0.departureHint }.map(\.id))
        let removed = selectedPeers.subtracting(allowed).count
        selectedPeers.formIntersection(allowed)
        if removed > 0 { status = L10n.format("已清除 %lld 个不可用接收对象。", Int64(removed)) }
    }
    func setGroupFilter(_ filter: PeerGroupFilter) {
        commit { $0.peerGroupFilter = filter }
    }
    func groupSelected(_ group: String) -> Bool {
        settings.visibleGroups.contains { DeviceGroups.same($0, group) }
    }
    func setGroupSelected(_ group: String, selected: Bool) {
        commit {
            $0.visibleGroups.removeAll { DeviceGroups.same($0, group) }
            if selected { $0.visibleGroups.append(group) }
        }
    }
    func clearPeerFilters() {
        peerSearch = ""
        commit { $0.peerGroupFilter = .all; $0.visibleGroups = [] }
    }
    func toggleReceiving() { commit { $0.receivingEnabled.toggle() } }

    @discardableResult
    private func commit(_ change: (inout AppSettings) -> Void) -> Bool {
        var value = settings
        change(&value)
        return saveSettings(value)
    }

    @discardableResult
    func saveSettings(_ value: AppSettings, baseline: AppSettings? = nil) -> Bool {
        do {
            guard let store else { throw BoardcastError.invalid(L10n.string("设置存储尚未就绪。")) }
            var value = try baseline.map { try SettingsValidation.merge(draft: value, baseline: $0, current: settings) } ?? value
            value.deviceName = value.deviceName.trimmingCharacters(in: .whitespacesAndNewlines)
            value.groupName = DeviceGroups.normalized(value.groupName)
            for index in value.devices.indices {
                value.devices[index].groupName = DeviceGroups.normalized(value.devices[index].groupName)
                if !value.devices[index].trusted { value.devices[index].autoAccept = false }
            }
            try store.save(value)
            settings = value
            clearHiddenSelection()
            discovery?.setMessage(discoveryMessage())
            discovery?.announce()
            status = L10n.string("设置已保存")
            return true
        } catch { status = L10n.string(error.localizedDescription); return false }
    }

    @discardableResult
    func saveDevice(_ value: KnownDevice) -> Bool {
        return commit {
            if let index = $0.devices.firstIndex(where: { $0.fingerprint.caseInsensitiveCompare(value.fingerprint) == .orderedSame }) {
                $0.devices[index] = value
            } else { $0.devices.append(value) }
        }
    }

    func addFiles() {
        let panel = NSOpenPanel()
        panel.canChooseFiles = true
        panel.canChooseDirectories = true
        panel.allowsMultipleSelection = true
        panel.prompt = L10n.string("添加")
        guard panel.runModal() == .OK else { return }
        add(urls: panel.urls)
    }
    func add(urls: [URL]) {
        let configuration = settings
        Task { await draft.add(urls: urls, settings: configuration) }
    }

    func sendDraft() {
        guard canSend, let transport else { return }
        do {
            let prepared = try draft.prepare(settings: settings)
            let recipients = groupedPeers.filter { selectedPeers.contains($0.id) && $0.lanOnline && $0.receivingEnabled && !$0.departureHint }
            guard !recipients.isEmpty, recipients.count <= 32, sendTasks.count + recipients.count <= 128 else {
                throw BoardcastError.invalid(L10n.string("接收对象或排队任务数量超出限额。"))
            }
            let summary = draft.summary
            for peer in recipients {
                let id = UUID().uuidString.replacingOccurrences(of: "-", with: "").lowercased()
                let total = prepared.entries.reduce(Int64(0)) { $0 + $1.size }
                tasks.enqueue(id: id, peer: peer, totalBytes: total)
                sendTasks[id] = Task { [weak self] in
                    defer {
                        self?.sendTasks.removeValue(forKey: id)
                        self?.objectWillChange.send()
                    }
                    do { try await transport.send(prepared: prepared, summary: summary, to: peer, id: id) }
                    catch is CancellationError { self?.tasks.finish(id: id, phase: .cancelled, message: L10n.string("已取消本机任务。")) }
                    catch { self?.tasks.finish(id: id, phase: .failed, message: error.localizedDescription) }
                }
            }
            status = L10n.format("已为 %lld 台设备创建独立任务。", Int64(recipients.count))
        } catch { status = L10n.string(error.localizedDescription) }
    }

    func chooseDirectory(current: String) -> String? {
        let panel = NSOpenPanel()
        panel.canChooseFiles = false
        panel.canChooseDirectories = true
        panel.canCreateDirectories = true
        panel.directoryURL = URL(fileURLWithPath: current, isDirectory: true)
        return panel.runModal() == .OK ? panel.url?.path : nil
    }
    func reveal(path: String) {
        NSWorkspace.shared.activateFileViewerSelecting([URL(fileURLWithPath: path)])
    }
    func setLogin(_ enabled: Bool) {
        do {
            if enabled { try SMAppService.mainApp.register() }
            else { try SMAppService.mainApp.unregister() }
            loginStatus = SMAppService.mainApp.status
        } catch { status = L10n.string(error.localizedDescription); loginStatus = SMAppService.mainApp.status }
    }
}
