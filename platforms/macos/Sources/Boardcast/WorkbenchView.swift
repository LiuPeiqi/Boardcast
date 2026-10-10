import AppKit
import SwiftUI

private enum WorkbenchSection: Hashable { case send, transfers }

@MainActor
struct WorkbenchView: View {
    @ObservedObject var model: AppModel
    @State private var section: WorkbenchSection? = .send
    @State private var incoming: PendingOffer?
    var body: some View {
        NavigationSplitView {
            VStack(spacing: 0) {
                List(selection: $section) {
                    Label { Text("发送", bundle: ResourceBundle.resources) } icon: { Image(systemName: "paperplane") }
                        .tag(WorkbenchSection.send)
                    Label { Text("传输记录", bundle: ResourceBundle.resources) } icon: { Image(systemName: "clock.arrow.circlepath") }
                        .tag(WorkbenchSection.transfers)
                }.listStyle(.sidebar)
                Divider()
                VStack(alignment: .leading, spacing: 9) {
                    Text(verbatim: model.settings.deviceName).font(.headline).textSelection(.enabled)
                    Text(verbatim: model.status).font(.caption).foregroundStyle(.secondary)
                    Button { model.toggleReceiving() } label: {
                        Label {
                            Text(verbatim: L10n.string(model.settings.receivingEnabled ? "允许新接收" : "接收已暂停"))
                        } icon: { Image(systemName: model.settings.receivingEnabled ? "checkmark.circle" : "pause.circle") }
                    }.disabled(model.terminationPending)
                    Button { model.reveal(path: model.settings.receiveDirectory) } label: {
                        Text("接收位置", bundle: ResourceBundle.resources)
                    }
                    Text(verbatim: model.settings.receiveDirectory).font(.caption).lineLimit(2)
                        .textSelection(.enabled).help(model.settings.receiveDirectory)
                    if model.terminationPending {
                        Text("任务结束后退出", bundle: ResourceBundle.resources).font(.caption)
                    }
                }.padding(16)
            }.navigationSplitViewColumnWidth(min: 180, ideal: 200, max: 260)
        } detail: {
            VStack(spacing: 0) {
                if !model.pending.isEmpty {
                    HStack {
                        Text(verbatim: L10n.format("%d 个来件等待确认", model.pending.count))
                        Spacer()
                        Menu {
                            ForEach(model.pending) { request in
                                Button { incoming = request } label: { Text(verbatim: request.offer.senderName) }
                            }
                        } label: { Text("查看来件", bundle: ResourceBundle.resources) }
                    }.padding(12)
                    Divider()
                }
                if section == .transfers {
                    TransfersView(model: model, store: model.tasks)
                } else {
                    HSplitView {
                        SendContentsView(model: model, draft: model.draft)
                            .frame(minWidth: 300, idealWidth: 490)
                        PeerListView(model: model, draft: model.draft)
                            .frame(minWidth: 300, idealWidth: 340)
                    }
                }
            }
            .navigationTitle(Text(verbatim: L10n.string(section == .transfers ? "传输记录" : "发送")))
        }
        .onReceive(NotificationCenter.default.publisher(for: .boardcastShowSend)) { _ in section = .send }
        .onReceive(NotificationCenter.default.publisher(for: .boardcastShowTransfers)) { _ in section = .transfers }
        .sheet(item: $incoming) { request in IncomingOfferView(model: model, request: request) }
    }
}

@MainActor
private struct SendContentsView: View {
    @ObservedObject var model: AppModel
    @ObservedObject var draft: SendDraft
    @State private var selected = Set<UUID>()
    @State private var expanded = false
    @State private var showSummary = false
    @State private var excluding: DraftItem?
    @State private var isDropTarget = false
    private var filtered: [DraftItem] {
        let query = model.fileSearch.trimmingCharacters(in: .whitespacesAndNewlines)
        return draft.items.filter { item in
            query.isEmpty || item.url.path.localizedCaseInsensitiveContains(query)
                || (expanded && item.entries.contains { $0.relativePath.localizedCaseInsensitiveContains(query) })
        }
    }
    var body: some View {
        VStack(spacing: 0) {
            HStack(spacing: 8) {
                Text("待发送", bundle: ResourceBundle.resources).font(.headline)
                Spacer(minLength: 8)
                if !draft.items.isEmpty {
                    Button { model.addFiles() } label: { Text("添加…", bundle: ResourceBundle.resources) }.disabled(draft.isScanning || model.terminationPending)
                    Button { draft.clear(); selected.removeAll(); model.fileSearch = "" } label: { Text("清空", bundle: ResourceBundle.resources) }
                }
            }.padding(12)
            if !draft.items.isEmpty {
                Text(verbatim: L10n.format("%d 项 · %d 个文件 · %d 个文件夹 · %@", draft.items.count,
                    draft.items.reduce(0) { $0 + $1.fileCount }, draft.items.reduce(0) { $0 + $1.directoryCount },
                    byteSize(draft.items.reduce(Int64(0)) { total, item in let sum = total.addingReportingOverflow(item.totalBytes); return sum.overflow ? Int64.max : sum.partialValue })))
                    .font(.caption).foregroundStyle(.secondary).frame(maxWidth: .infinity, alignment: .leading)
                    .padding(.horizontal, 12).padding(.bottom, 8)
            }
            if !selected.isEmpty || draft.items.contains(where: \.isDirectory) || draft.items.count >= 8 || !model.fileSearch.isEmpty {
                HStack(spacing: 8) {
                    if !selected.isEmpty {
                        Button { removeSelected() } label: { Text("移除", bundle: ResourceBundle.resources) }
                        if selected.count == 1, let item = draft.items.first(where: { selected.contains($0.id) }) {
                            Button { excluding = item } label: { Text("排除…", bundle: ResourceBundle.resources) }
                        }
                    }
                    if draft.items.contains(where: \.isDirectory) {
                        Toggle(isOn: $expanded) { Text("展开清单", bundle: ResourceBundle.resources) }.toggleStyle(.button)
                    }
                    if draft.items.count >= 8 || !model.fileSearch.isEmpty {
                        TextField(L10n.string("搜索文件"), text: $model.fileSearch)
                            .textFieldStyle(.roundedBorder).accessibilityLabel(Text("搜索文件", bundle: ResourceBundle.resources))
                    }
                    Spacer(minLength: 0)
                }.controlSize(.small).padding(.horizontal, 12).padding(.bottom, 8)
            }
            Divider()
            if filtered.isEmpty {
                Button {
                    if !draft.items.isEmpty && !model.fileSearch.isEmpty { model.fileSearch = "" } else { model.addFiles() }
                } label: {
                    VStack(spacing: 10) {
                        Image(systemName: draft.items.isEmpty ? "arrow.down.doc" : "magnifyingglass").font(.largeTitle)
                        Text(verbatim: L10n.string(draft.items.isEmpty ? "拖入文件或文件夹，也可以点击添加" : "没有匹配文件，点击恢复清单"))
                            .multilineTextAlignment(.center)
                    }.frame(maxWidth: .infinity, maxHeight: .infinity).padding(24).contentShape(Rectangle())
                }.buttonStyle(.plain).disabled(model.terminationPending)
            } else {
                List(selection: $selected) {
                    ForEach(filtered) { item in
                        VStack(alignment: .leading, spacing: 4) {
                            AdaptiveFileRow(url: item.url, name: item.url.lastPathComponent, isDirectory: item.isDirectory,
                                            metadata: metadata(item), detail: item.url.path + "\n" + metadata(item))
                            if item.isPackage { Text("包目录；不承诺专有元数据保真", bundle: ResourceBundle.resources).font(.caption).foregroundStyle(.secondary) }
                            if !item.unresolvedIssues.isEmpty {
                                Text(verbatim: L10n.format("%d 个问题需要排除或移除后发送", item.unresolvedIssues.count))
                                    .font(.caption).foregroundStyle(.red)
                            }
                            if expanded && item.isDirectory {
                                ForEach(item.entries.filter { entry in
                                    entry.relativePath != item.url.lastPathComponent &&
                                        (model.fileSearch.isEmpty || entry.relativePath.localizedCaseInsensitiveContains(model.fileSearch))
                                }, id: \.relativePath) { entry in
                                    AdaptiveFileRow(url: nil, name: entry.relativePath, isDirectory: entry.isDirectory,
                                        metadata: item.isExcluded(entry.relativePath) ? L10n.string("已排除") : byteSize(entry.size),
                                        detail: entry.relativePath).padding(.leading, 14)
                                        .foregroundStyle(item.isExcluded(entry.relativePath) ? .secondary : .primary)
                                }
                            }
                        }.tag(item.id)
                        .contextMenu {
                            Button { excluding = item } label: { Text("查看清单与排除…", bundle: ResourceBundle.resources) }
                            Button { model.reveal(path: item.url.path) } label: { Text("在 Finder 中显示", bundle: ResourceBundle.resources) }
                            Button { draft.remove(ids: [item.id]) } label: { Text("移除", bundle: ResourceBundle.resources) }
                        }
                    }
                }.listStyle(.inset).onDeleteCommand { removeSelected() }
            }
            if draft.isScanning {
                HStack {
                    ProgressView().controlSize(.small)
                    Text("正在统计", bundle: ResourceBundle.resources)
                    Spacer()
                    Button { draft.cancelScanning() } label: { Text("停止统计", bundle: ResourceBundle.resources) }
                }.padding(12)
            }
            if let error = draft.error {
                Text(verbatim: error).font(.caption).foregroundStyle(.red).textSelection(.enabled)
                    .frame(maxWidth: .infinity, alignment: .leading).padding(12)
            }
            Divider()
            DisclosureGroup(isExpanded: $showSummary) {
                TextEditor(text: $draft.summary).frame(height: 72)
                    .accessibilityLabel(Text("发送说明", bundle: ResourceBundle.resources))
            } label: { Text("添加说明", bundle: ResourceBundle.resources) }.padding(12)
        }
        .background(isDropTarget ? Color.accentColor.opacity(0.08) : Color.clear)
        .dropDestination(for: URL.self) { urls, _ in
            let files = urls.filter(\.isFileURL)
            guard !files.isEmpty, !model.terminationPending else { return false }
            model.add(urls: files)
            return true
        } isTargeted: { isDropTarget = $0 }
        .sheet(item: $excluding) { item in ExclusionsView(draft: draft, item: item) }
        .onChange(of: draft.items.map(\.id)) { ids in selected.formIntersection(ids) }
    }
    private func removeSelected() { draft.remove(ids: selected); selected.removeAll() }
    private func metadata(_ item: DraftItem) -> String {
        var value = item.isDirectory ? L10n.format("%d 个文件 · %@", item.fileCount, byteSize(item.totalBytes)) : byteSize(item.totalBytes)
        if !item.excluded.isEmpty { value += " · " + L10n.format("排除 %d 项", item.excluded.count) }
        return value
    }
}

@MainActor
private struct ExclusionsView: View {
    @ObservedObject var draft: SendDraft
    let item: DraftItem
    @Environment(\.dismiss) private var dismiss
    @State private var excluded = Set<String>()
    private var paths: [String] { Array(Set(item.entries.map(\.relativePath) + item.issues.map(\.relativePath))).sorted() }
    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            Text("清单与排除", bundle: ResourceBundle.resources).font(.headline)
            Text(verbatim: item.url.path).font(.caption).textSelection(.enabled)
            Text("勾选的路径及其子目录不会发送；源文件不会删除。", bundle: ResourceBundle.resources).font(.caption)
            List(paths, id: \.self) { path in
                VStack(alignment: .leading) {
                    Toggle(isOn: Binding(get: { excluded.contains { path == $0 || path.hasPrefix($0 + "/") } }, set: { enabled in
                        if enabled { excluded.insert(path) } else { excluded.remove(path) }
                    })) { Text(verbatim: path) }
                    .disabled(excluded.contains { path != $0 && path.hasPrefix($0 + "/") })
                    ForEach(item.issues.filter { $0.relativePath == path }) { issue in
                        Text(verbatim: issue.reason).font(.caption).foregroundStyle(.red)
                    }
                }
            }
            HStack {
                Button { excluded.removeAll() } label: { Text("恢复全部", bundle: ResourceBundle.resources) }
                Spacer()
                Button { dismiss() } label: { Text("取消", bundle: ResourceBundle.resources) }.keyboardShortcut(.cancelAction)
                Button {
                    draft.restoreExclusions(itemID: item.id)
                    draft.exclude(itemID: item.id, paths: excluded)
                    dismiss()
                } label: { Text("应用", bundle: ResourceBundle.resources) }.keyboardShortcut(.defaultAction)
            }
        }.padding(20).frame(minWidth: 540, minHeight: 400).onAppear { excluded = item.excluded }
    }
}

@MainActor
private struct PeerListView: View {
    @ObservedObject var model: AppModel
    @ObservedObject var draft: SendDraft
    @State private var inspecting: PeerInfo?
    @State private var inspectingUpdate: PeerInfo?
    private var hiddenCount: Int { model.selectedPeers.subtracting(Set(model.visiblePeers.map(\.id))).count }
    private var sendReason: String {
        if model.terminationPending { return L10n.string("正在等待退出") }
        if draft.isScanning { return L10n.string("等待统计完成") }
        if draft.items.isEmpty { return L10n.string("请添加文件或文件夹") }
        if draft.items.contains(where: { !$0.unresolvedIssues.isEmpty }) { return L10n.string("请处理文件清单问题") }
        if model.selectedPeers.isEmpty { return L10n.string("请选择接收设备") }
        return L10n.string("内容或目标当前不可发送，请检查服务状态")
    }
    var body: some View {
        VStack(spacing: 0) {
            HStack(spacing: 8) {
                Text("附近设备", bundle: ResourceBundle.resources).font(.headline).fixedSize()
                Menu {
                    ForEach(PeerGroupFilter.allCases, id: \.self) { filter in
                        Button { model.setGroupFilter(filter) } label: {
                            if model.settings.peerGroupFilter == filter { Label(filter.title, systemImage: "checkmark") }
                            else { Text(verbatim: filter.title) }
                        }
                    }
                    Divider()
                    ForEach(model.availableGroups, id: \.self) { group in
                        Toggle(isOn: Binding(get: { model.groupSelected(group) }, set: { model.setGroupSelected(group, selected: $0) })) {
                            Text(verbatim: group)
                        }
                    }
                    Divider()
                    Button { model.clearPeerFilters() } label: { Text("显示全部设备", bundle: ResourceBundle.resources) }
                } label: {
                    Text(verbatim: model.settings.peerGroupFilter.title).lineLimit(1).frame(maxWidth: .infinity, alignment: .leading)
                }.help(model.settings.visibleGroups.joined(separator: ", "))
                Text(verbatim: "\(model.visiblePeers.count)/\(model.peers.count)").font(.caption).monospacedDigit().fixedSize()
                    .accessibilityLabel(Text(verbatim: L10n.format("显示 %d 台，共 %d 台", model.visiblePeers.count, model.peers.count)))
                Button { model.refreshPeers() } label: { Image(systemName: "arrow.clockwise") }
                    .help(L10n.string("刷新设备")).accessibilityLabel(Text("刷新设备", bundle: ResourceBundle.resources))
            }.padding(12)
            TextField(L10n.string("搜索名称、地址或分组"), text: $model.peerSearch)
                .textFieldStyle(.roundedBorder).padding(.horizontal, 12).padding(.bottom, 10)
            Divider()
            if model.visiblePeers.isEmpty {
                VStack(spacing: 12) {
                    Text(verbatim: L10n.string(model.peers.isEmpty ? "尚未发现设备" : "筛选无结果"))
                    Text("请检查双方局域网连接和服务状态。", bundle: ResourceBundle.resources)
                        .font(.caption).foregroundStyle(.secondary).multilineTextAlignment(.center)
                    Button { model.clearPeerFilters() } label: { Text("显示全部设备", bundle: ResourceBundle.resources) }
                    Button { model.refreshPeers() } label: { Text("刷新设备", bundle: ResourceBundle.resources) }
                }.padding(20).frame(maxWidth: .infinity, maxHeight: .infinity)
            } else {
                List(model.visiblePeers) { peer in
                    HStack(spacing: 8) {
                        Toggle(isOn: Binding(get: { model.selectedPeers.contains(peer.id) }, set: { _ in model.togglePeer(peer) })) {
                            Text(verbatim: model.displayName(peer))
                        }.labelsHidden().toggleStyle(.checkbox)
                            .accessibilityLabel(Text(verbatim: L10n.format("选择设备 %@", model.displayName(peer))))
                            .disabled(!peer.lanOnline || peer.departureHint || !peer.receivingEnabled || model.terminationPending)
                        PlatformIcon(platform: peer.platform, size: 14).accessibilityHidden(true)
                        Button { inspecting = peer } label: {
                            Text(verbatim: model.displayName(peer)).lineLimit(1).frame(maxWidth: .infinity, alignment: .leading)
                        }.buttonStyle(.plain).help(L10n.string("查看设备详情"))
                        if !peer.lanOnline || peer.departureHint || !peer.receivingEnabled {
                            Image(systemName: "exclamationmark.circle").foregroundStyle(.secondary)
                                .help(peerState(peer)).accessibilityLabel(Text(verbatim: peerState(peer)))
                        }
                        if let service = model.updates {
                            PeerUpdateBadge(service: service, peer: peer) { inspectingUpdate = peer }
                        } else if let version = peer.appVersion, !version.isEmpty {
                            Text(verbatim: version).font(.caption).foregroundStyle(.secondary).lineLimit(1).frame(maxWidth: 72).help(version)
                        }
                    }.padding(.vertical, 6)
                    .contextMenu { Button { inspecting = peer } label: { Text("查看设备详情", bundle: ResourceBundle.resources) } }
                }.listStyle(.inset)
            }
            if hiddenCount > 0 {
                Button { model.peerSearch = "" } label: {
                    Text(verbatim: L10n.format("%d 个已选目标被搜索隐藏，显示目标", hiddenCount))
                }.font(.caption).padding(10)
            }
            Divider()
            VStack(spacing: 7) {
                Button { model.sendDraft() } label: {
                    Text(verbatim: L10n.format("发送给 %d 台设备", model.selectedPeers.count)).frame(maxWidth: .infinity)
                }.buttonStyle(.borderedProminent).controlSize(.large).disabled(!model.canSend)
                    .help(model.canSend ? L10n.string("创建每台设备的独立发送任务") : sendReason)
                if !model.canSend { Text(verbatim: sendReason).font(.caption).foregroundStyle(.secondary) }
            }.padding(12)
        }.sheet(item: $inspecting) { peer in DeviceDetailView(model: model, peer: peer) }
        .sheet(item: $inspectingUpdate) { _ in
            UpdateDetailsSheet(model: model)
        }
    }
}

private func peerState(_ peer: PeerInfo) -> String {
    if !peer.lanOnline { return L10n.string("设备离线") }
    if peer.departureHint { return L10n.string("设备可能已离开，等待发现确认") }
    if !peer.receivingEnabled { return L10n.string("对方已暂停新接收") }
    return L10n.string("允许新接收")
}

@MainActor
struct DeviceDetailView: View {
    @ObservedObject var model: AppModel
    let peer: PeerInfo
    @Environment(\.dismiss) private var dismiss
    @State private var device = KnownDevice(fingerprint: "", name: "")
    @State private var saveError: String?
    var body: some View {
        VStack(spacing: 12) {
            ScrollView {
                Form {
                    LabeledContent(L10n.string("原设备名")) { Text(verbatim: peer.name).textSelection(.enabled) }
                    LabeledContent(L10n.string("平台")) { Text(verbatim: peer.platform) }
                    if let version = peer.appVersion { LabeledContent(L10n.string("运行版本")) { Text(verbatim: version) } }
                    LabeledContent(L10n.string("接收状态")) { Text(verbatim: peerState(peer)) }
                    LabeledContent(L10n.string("地址")) { Text(verbatim: "\(peer.address):\(peer.port)").textSelection(.enabled) }
                    LabeledContent(L10n.string("有效分组")) { Text(verbatim: model.groupName(peer)).textSelection(.enabled) }
                    DeviceEditor(device: $device)
                }.formStyle(.grouped)
            }
            if let saveError { Text(verbatim: saveError).font(.caption).foregroundStyle(.red).padding(.horizontal, 20) }
            HStack {
                Spacer()
                Button { dismiss() } label: { Text("取消", bundle: ResourceBundle.resources) }.keyboardShortcut(.cancelAction)
                Button { if model.saveDevice(device) { dismiss() } else { saveError = model.status } } label: { Text("保存设备设置", bundle: ResourceBundle.resources) }
                    .keyboardShortcut(.defaultAction)
            }.padding(.horizontal, 20).padding(.bottom, 16)
        }.frame(minWidth: 520, minHeight: 470)
        .onAppear {
            device = model.settings.devices.first(where: { $0.fingerprint.caseInsensitiveCompare(peer.fingerprint) == .orderedSame })
                ?? KnownDevice(fingerprint: peer.fingerprint, name: peer.name, lastAddress: peer.address, port: peer.port)
        }
    }
}

@MainActor
private struct IncomingOfferView: View {
    @ObservedObject var model: AppModel
    let request: PendingOffer
    @Environment(\.dismiss) private var dismiss
    @State private var destination = ""
    var body: some View {
        VStack(spacing: 12) {
            ScrollView {
                VStack(alignment: .leading, spacing: 12) {
                    Text(verbatim: request.offer.senderName).font(.title2).textSelection(.enabled)
                    Text(verbatim: L10n.format("%d 个文件 · %d 个文件夹 · %@", request.offer.entries.filter { !$0.isDirectory }.count, request.offer.entries.filter(\.isDirectory).count, byteSize(request.offer.totalBytes)))
                    Text(verbatim: request.decision.reason).foregroundStyle(.secondary)
                    FingerprintView(value: request.fingerprint)
                    if !request.offer.summary.isEmpty {
                        Text("发送说明", bundle: ResourceBundle.resources).font(.headline)
                        Text(verbatim: request.offer.summary).textSelection(.enabled)
                    }
                    HStack {
                        Text("保存到", bundle: ResourceBundle.resources)
                        Text(verbatim: destination).textSelection(.enabled).frame(maxWidth: .infinity, alignment: .leading)
                        Button { if let path = model.chooseDirectory(current: destination) { destination = path } } label: {
                            Text("选择…", bundle: ResourceBundle.resources)
                        }
                    }
                    DisclosureGroup {
                        LazyVStack(alignment: .leading, spacing: 3) {
                            ForEach(request.offer.entries, id: \.relativePath) { entry in
                                AdaptiveFileRow(url: nil, name: entry.relativePath, isDirectory: entry.isDirectory,
                                                metadata: entry.isDirectory ? L10n.string("文件夹") : byteSize(entry.size), detail: entry.relativePath)
                            }
                        }.padding(.top, 8)
                    } label: { Text("完整文件清单", bundle: ResourceBundle.resources).font(.headline) }
                }.padding(20)
            }
            TimelineView(.periodic(from: Date(), by: 1)) { context in
                let valid = context.date < request.expiresAt && model.pending.contains(where: { $0.id == request.id })
                VStack(spacing: 8) {
                    Text(verbatim: valid ? L10n.format("请在 %@ 前确认", request.expiresAt.formatted(date: .omitted, time: .standard)) : L10n.string("请求已过期"))
                        .font(.caption).foregroundStyle(.secondary)
                    HStack {
                        Button { dismiss() } label: { Text("稍后处理", bundle: ResourceBundle.resources) }.keyboardShortcut(.cancelAction)
                        Spacer()
                        Button { model.decide(request, accepted: false); dismiss() } label: { Text("拒绝", bundle: ResourceBundle.resources) }.disabled(!valid)
                        Button { model.decide(request, accepted: true, destination: destination); dismiss() } label: {
                            Text("接受并保存", bundle: ResourceBundle.resources)
                        }.buttonStyle(.borderedProminent).disabled(!valid || !model.settings.receivingEnabled || model.terminationPending)
                    }
                    if !model.settings.receivingEnabled { Text("新接收已暂停，请先恢复接收。", bundle: ResourceBundle.resources).font(.caption) }
                }.padding(.horizontal, 20).padding(.bottom, 16)
            }
        }.frame(minWidth: 580, minHeight: 460).onAppear { destination = request.decision.destination }
    }
}

private enum TransferFilter: CaseIterable, Hashable { case all, active, completed, failed }
@MainActor
private struct TransfersView: View {
    @ObservedObject var model: AppModel
    @ObservedObject var store: TransferStore
    @Environment(\.openWindow) private var openWindow
    @State private var filter: TransferFilter = .all
    private var records: [TransferRecord] {
        store.tasks.filter { record in
            switch filter {
            case .all: return true
            case .active: return record.isActive
            case .completed: return record.phase == .completed
            case .failed: return !record.isActive && record.phase != .completed
            }
        }
    }
    var body: some View {
        VStack(spacing: 0) {
            Picker(L10n.string("任务筛选"), selection: $filter) {
                Text("全部", bundle: ResourceBundle.resources).tag(TransferFilter.all)
                Text("进行中", bundle: ResourceBundle.resources).tag(TransferFilter.active)
                Text("已完成", bundle: ResourceBundle.resources).tag(TransferFilter.completed)
                Text("其他结果", bundle: ResourceBundle.resources).tag(TransferFilter.failed)
            }.pickerStyle(.segmented).padding(12)
            if let error = store.persistenceError { Text(verbatim: error).font(.caption).foregroundStyle(.red).padding(12) }
            if records.isEmpty {
                Text("没有符合条件的任务", bundle: ResourceBundle.resources).foregroundStyle(.secondary)
                    .frame(maxWidth: .infinity, maxHeight: .infinity)
            } else {
                List(records) { record in
                    VStack(alignment: .leading, spacing: 8) {
                        HStack {
                            Button { openWindow(id: "task", value: record.id) } label: {
                                Text(verbatim: record.peerName).font(.headline)
                            }.buttonStyle(.plain)
                            Text(verbatim: record.direction.title).foregroundStyle(.secondary)
                            Spacer()
                            Text(verbatim: record.phase.title)
                            if record.isActive {
                                Button { model.cancelTask(record.id) } label: { Text("取消任务", bundle: ResourceBundle.resources) }
                            }
                        }
                        if record.isActive { TransferProgressView(record: record) }
                        if let path = record.destination {
                            Button { model.reveal(path: path) } label: { Text(verbatim: path).lineLimit(1) }.buttonStyle(.link).help(path)
                        }
                        if let message = record.message { Text(verbatim: message).font(.caption).foregroundStyle(.secondary).lineLimit(2) }
                    }.padding(.vertical, 6)
                    .contextMenu {
                        Button { openWindow(id: "task", value: record.id) } label: { Text("查看任务详情", bundle: ResourceBundle.resources) }
                    }
                }
            }
        }
    }
}

@MainActor
struct TransferDetailView: View {
    @ObservedObject var model: AppModel
    @ObservedObject private var store: TransferStore
    let taskID: String
    init(model: AppModel, taskID: String) { self.model = model; self.store = model.tasks; self.taskID = taskID }
    var body: some View {
        ScrollView {
            if let record = store.tasks.first(where: { $0.id == taskID }) {
                VStack(alignment: .leading, spacing: 16) {
                    Text(verbatim: record.peerName).font(.title2).textSelection(.enabled)
                    Text(verbatim: record.direction.title + " · " + record.phase.title)
                    TransferProgressView(record: record)
                    LabeledContent(L10n.string("任务标识")) { Text(verbatim: record.id).textSelection(.enabled) }
                    LabeledContent(L10n.string("开始时间")) { Text(record.createdAt, format: .dateTime) }
                    LabeledContent(L10n.string("最后更新")) { Text(record.updatedAt, format: .dateTime) }
                    FingerprintView(value: record.peerFingerprint)
                    Text(verbatim: L10n.format("已保存 %d 个文件", record.savedFiles))
                    if let path = record.destination {
                        Text("实际保存位置", bundle: ResourceBundle.resources).font(.headline)
                        Text(verbatim: path).textSelection(.enabled)
                        Button { model.reveal(path: path) } label: { Text("在 Finder 中显示", bundle: ResourceBundle.resources) }
                    }
                    if let message = record.message { Text(verbatim: message).textSelection(.enabled) }
                    if record.isActive {
                        Button { model.cancelTask(record.id) } label: { Text("取消本机任务", bundle: ResourceBundle.resources) }
                        Text("顺序传输取消后需重新发送；已保存的文件保留。", bundle: ResourceBundle.resources).font(.caption).foregroundStyle(.secondary)
                    }
                }.padding(24).frame(maxWidth: .infinity, alignment: .leading)
            } else {
                Text("任务记录不可用", bundle: ResourceBundle.resources).padding(24)
            }
        }.navigationTitle(Text("传输详情", bundle: ResourceBundle.resources))
    }
}

@MainActor
private struct TransferProgressView: View {
    let record: TransferRecord
    var body: some View {
        VStack(alignment: .leading, spacing: 6) {
            if record.isActive && record.totalBytes > 0 {
                ProgressView(value: Double(max(0, record.completedBytes)), total: Double(record.totalBytes))
                    .accessibilityLabel(Text(verbatim: record.phase.title))
            }
            Text(verbatim: byteSize(record.completedBytes) + " / " + byteSize(record.totalBytes)).font(.caption).monospacedDigit()
        }
    }
}

@MainActor
struct FingerprintView: View {
    let value: String
    var body: some View {
        VStack(alignment: .leading, spacing: 6) {
            HStack {
                Text("证书 SHA-256 指纹", bundle: ResourceBundle.resources).font(.caption).foregroundStyle(.secondary)
                Spacer()
                Button {
                    NSPasteboard.general.clearContents()
                    NSPasteboard.general.setString(value, forType: .string)
                } label: { Text("复制", bundle: ResourceBundle.resources) }.disabled(value.isEmpty)
            }
            Text(verbatim: value).font(.system(.caption, design: .monospaced)).textSelection(.enabled)
        }
    }
}

func byteSize(_ value: Int64) -> String { ByteCountFormatter.string(fromByteCount: value, countStyle: .file) }

@MainActor
private struct UpdateDetailsSheet: View {
    @ObservedObject var model: AppModel
    @Environment(\.dismiss) private var dismiss
    var body: some View {
        VStack(spacing: 0) {
            if let service = model.updates {
                MacUpdatesView(model: model, service: service)
            } else {
                Text("更新服务尚未就绪。", bundle: ResourceBundle.resources).padding(24)
            }
            Divider()
            HStack {
                Spacer()
                Button { dismiss() } label: { Text("关闭", bundle: ResourceBundle.resources) }
                    .keyboardShortcut(.cancelAction)
            }.padding(16)
        }.frame(minWidth: 550, minHeight: 430)
    }
}