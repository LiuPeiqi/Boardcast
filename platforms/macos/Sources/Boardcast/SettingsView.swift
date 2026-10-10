import ServiceManagement
import SwiftUI

@MainActor
struct SettingsView: View {
    @ObservedObject var model: AppModel
    @State private var draft = AppSettings()
    @State private var baseline = AppSettings()
    @State private var saveMessage: String?
    var body: some View {
        VStack(spacing: 0) {
            TabView {
                general.tabItem { Label { Text("通用", bundle: ResourceBundle.resources) } icon: { Image(systemName: "gearshape") } }
                ReceiveRulesEditor(model: model, rules: $draft.rules, devices: draft.devices, configuration: draft)
                    .tabItem { Label { Text("接收规则", bundle: ResourceBundle.resources) } icon: { Image(systemName: "line.3.horizontal.decrease.circle") } }
                devices.tabItem { Label { Text("设备与信任", bundle: ResourceBundle.resources) } icon: { Image(systemName: "desktopcomputer") } }
                updates.tabItem { Label { Text("更新", bundle: ResourceBundle.resources) } icon: { Image(systemName: "arrow.down.circle") } }
                about.tabItem { Label { Text("关于", bundle: ResourceBundle.resources) } icon: { Image(systemName: "info.circle") } }
            }
            Divider()
            HStack {
                if let saveMessage { Text(verbatim: saveMessage).font(.caption).textSelection(.enabled) }
                Spacer()
                Button { draft = model.settings; baseline = model.settings; saveMessage = nil } label: { Text("取消更改", bundle: ResourceBundle.resources) }
                Button {
                    if model.saveSettings(draft, baseline: baseline) {
                        draft = model.settings
                        baseline = model.settings
                        saveMessage = L10n.string("设置已保存")
                    } else { saveMessage = model.status }
                } label: { Text("应用", bundle: ResourceBundle.resources) }.buttonStyle(.borderedProminent)
            }.padding(16)
        }.frame(minWidth: 640, idealWidth: 700, minHeight: 560, idealHeight: 600)
        .onAppear { draft = model.settings; baseline = model.settings; saveMessage = nil }
    }
    private var general: some View {
        ScrollView {
            Form {
                Section {
                    TextField(L10n.string("设备名称"), text: $draft.deviceName)
                    TextField(L10n.string("公开分组"), text: $draft.groupName)
                    Picker(L10n.string("默认设备筛选"), selection: $draft.peerGroupFilter) {
                        ForEach(PeerGroupFilter.allCases, id: \.self) { filter in Text(verbatim: filter.title).tag(filter) }
                    }
                    if draft.peerGroupFilter == .selectedGroups {
                        ForEach(DeviceGroups.available(peers: model.peers, settings: draft), id: \.self) { group in
                            Toggle(isOn: Binding(get: { draft.visibleGroups.contains { DeviceGroups.same($0, group) } }, set: { selected in
                                draft.visibleGroups.removeAll { DeviceGroups.same($0, group) }
                                if selected { draft.visibleGroups.append(group) }
                            })) { Text(verbatim: group) }
                        }
                    }
                    Text("留空按原设备名自动分组。分组不授予身份信任或自动接收权限。", bundle: ResourceBundle.resources)
                        .font(.caption).foregroundStyle(.secondary)
                    if !DeviceGroups.isValid(draft.groupName) {
                        Text("分组最多 80 个 UTF-16 代码单元，不能含控制字符。", bundle: ResourceBundle.resources).font(.caption).foregroundStyle(.red)
                    }
                } header: { Text("此设备", bundle: ResourceBundle.resources) }
                Section {
                    TextField(L10n.string("接收目录"), text: $draft.receiveDirectory)
                    HStack {
                        Button { if let path = model.chooseDirectory(current: draft.receiveDirectory) { draft.receiveDirectory = path } } label: {
                            Text("选择接收目录…", bundle: ResourceBundle.resources)
                        }
                        Button { model.reveal(path: draft.receiveDirectory) } label: { Text("在 Finder 中显示", bundle: ResourceBundle.resources) }
                    }
                    LabeledContent(L10n.string("端口")) { Text(verbatim: String(draft.port)) }
                    TextField(L10n.string("每批最大字节数"), value: $draft.maxTransferBytes, format: .number)
                    TextField(L10n.string("每批最大清单项数"), value: $draft.maxEntries, format: .number)
                    Text("局域网协议使用固定端口 45831。大小和清单限额在接收前再次检查。", bundle: ResourceBundle.resources)
                        .font(.caption).foregroundStyle(.secondary)
                } header: { Text("接收", bundle: ResourceBundle.resources) }
                Section {
                    Toggle(isOn: Binding(get: { model.loginStatus == .enabled }, set: { model.setLogin($0) })) {
                        Text("登录时启动", bundle: ResourceBundle.resources)
                    }
                    Text(verbatim: loginDescription).font(.caption).foregroundStyle(.secondary)
                    if model.loginStatus == .requiresApproval {
                        Button { SMAppService.openSystemSettingsLoginItems() } label: { Text("打开系统登录项设置", bundle: ResourceBundle.resources) }
                    }
                } header: { Text("系统登录项", bundle: ResourceBundle.resources) }
                Section { FingerprintView(value: model.fingerprint) } header: { Text("本机身份", bundle: ResourceBundle.resources) }
            }.formStyle(.grouped)
        }
    }
    private var loginDescription: String {
        switch model.loginStatus {
        case .enabled: return L10n.string("系统已允许登录启动；登录时保留菜单栏并静默等待。")
        case .requiresApproval: return L10n.string("需要在系统设置中批准登录启动。")
        case .notRegistered: return L10n.string("尚未注册登录启动。")
        case .notFound: return L10n.string("未找到可注册的应用，请从完整 app bundle 启动。")
        @unknown default: return L10n.string("登录启动状态不可用。")
        }
    }
    private var devices: some View {
        ScrollView {
            if draft.devices.isEmpty {
                Text("在附近设备详情中核对指纹后保存设备。", bundle: ResourceBundle.resources)
                    .foregroundStyle(.secondary).padding(24)
            } else {
                Form {
                    ForEach($draft.devices) { $device in
                        DisclosureGroup {
                            DeviceEditor(device: $device)
                            Button(role: .destructive) { draft.devices.removeAll { $0.id == device.id } } label: {
                                Text("移除本机设备记录", bundle: ResourceBundle.resources)
                            }
                        } label: { Text(verbatim: device.displayName) }
                    }
                }.formStyle(.grouped)
            }
        }
    }
    @ViewBuilder
    private var updates: some View {
        if let service = model.updates {
            MacUpdatesView(model: model, service: service)
        } else {
            Text("更新服务尚未就绪。", bundle: ResourceBundle.resources)
                .foregroundStyle(.secondary).padding(24)
        }
    }
    private var about: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 16) {
                Text("Boardcast").font(.title)
                Text(verbatim: Bundle.main.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String ?? L10n.string("版本信息不可用"))
                Text("仅在局域网内发现和直接传输文件。", bundle: ResourceBundle.resources)
                Text("文件和目录保留普通内容与层级；不承诺 Finder 标签、资源分叉、ACL、硬链接及平台专有元数据保真。包目录中的符号链接和特殊文件会阻止发送，可在清单中明确排除。", bundle: ResourceBundle.resources)
                    .font(.caption).foregroundStyle(.secondary)
                Text("顺序传输中断后需核查接收端并重新发送完整内容。", bundle: ResourceBundle.resources)
                    .font(.caption).foregroundStyle(.secondary)
            }.padding(24).frame(maxWidth: .infinity, alignment: .leading)
        }
    }
}

@MainActor
struct DeviceEditor: View {
    @Binding var device: KnownDevice
    var body: some View {
        TextField(L10n.string("本机备注"), text: $device.alias)
        TextField(L10n.string("个人分组"), text: $device.groupName)
        Text("个人分组仅在此 Mac 生效，留空使用对方公开分组或原设备名。", bundle: ResourceBundle.resources)
            .font(.caption).foregroundStyle(.secondary)
        FingerprintView(value: device.fingerprint)
        Toggle(isOn: $device.trusted) { Text("已核对身份，信任此设备", bundle: ResourceBundle.resources) }
            .onChange(of: device.trusted) { trusted in if !trusted { device.autoAccept = false } }
        Toggle(isOn: $device.autoAccept) { Text("允许匹配规则后自动接收", bundle: ResourceBundle.resources) }
            .disabled(!device.trusted)
        Text("自动接收同时要求身份信任、设备授权和首条匹配规则授权。接受一次来件不建立信任。", bundle: ResourceBundle.resources)
            .font(.caption).foregroundStyle(.secondary)
    }
}

@MainActor
private struct ReceiveRulesEditor: View {
    @ObservedObject var model: AppModel
    @Binding var rules: [ReceiveRule]
    let devices: [KnownDevice]
    let configuration: AppSettings
    @State private var selectedID: String?
    var body: some View {
        VStack(spacing: 0) {
            HSplitView {
                List(selection: $selectedID) {
                    ForEach(rules) { rule in
                        HStack {
                            Image(systemName: rule.enabled ? "checkmark.circle" : "pause.circle").accessibilityHidden(true)
                            Text(verbatim: rule.name).lineLimit(1)
                        }.tag(rule.id)
                    }.onMove { rules.move(fromOffsets: $0, toOffset: $1) }
                }.frame(minWidth: 180, idealWidth: 210)
                ScrollView {
                    if let index = rules.firstIndex(where: { $0.id == selectedID }) {
                        RuleEditor(model: model, rule: $rules[index], devices: devices, configuration: configuration)
                    } else {
                        Text("选择规则进行编辑；按从上到下的首条匹配规则执行。", bundle: ResourceBundle.resources)
                            .foregroundStyle(.secondary).padding(20)
                    }
                }.frame(minWidth: 350)
            }
            HStack(spacing: 10) {
                Button {
                    var rule = ReceiveRule()
                    rule.name = L10n.string("新规则")
                    rules.append(rule)
                    selectedID = rule.id
                } label: { Text("添加规则", bundle: ResourceBundle.resources) }
                Button {
                    rules.removeAll { $0.id == selectedID }
                    selectedID = nil
                } label: { Text("删除规则", bundle: ResourceBundle.resources) }.disabled(selectedID == nil)
                if let index = rules.firstIndex(where: { $0.id == selectedID }) {
                    Button { rules.swapAt(index, index - 1) } label: { Image(systemName: "arrow.up") }
                        .disabled(index == 0).accessibilityLabel(Text("规则上移", bundle: ResourceBundle.resources))
                    Button { rules.swapAt(index, index + 1) } label: { Image(systemName: "arrow.down") }
                        .disabled(index == rules.count - 1).accessibilityLabel(Text("规则下移", bundle: ResourceBundle.resources))
                }
                Spacer()
            }.padding(12)
        }
    }
}

@MainActor
private struct RuleEditor: View {
    @ObservedObject var model: AppModel
    @Binding var rule: ReceiveRule
    let devices: [KnownDevice]
    let configuration: AppSettings
    @State private var checking: PendingOffer?
    @State private var extensionsText = ""
    var body: some View {
        Form {
            TextField(L10n.string("规则名称"), text: $rule.name)
            Toggle(isOn: $rule.enabled) { Text("启用规则", bundle: ResourceBundle.resources) }
            Picker(L10n.string("设备范围"), selection: $rule.deviceFingerprint) {
                Text("任何设备", bundle: ResourceBundle.resources).tag("")
                ForEach(devices) { device in Text(verbatim: device.displayName).tag(device.fingerprint) }
                if !rule.deviceFingerprint.isEmpty && !devices.contains(where: { $0.fingerprint == rule.deviceFingerprint }) {
                    Text(verbatim: rule.deviceFingerprint).tag(rule.deviceFingerprint)
                }
            }
            Picker(L10n.string("内容范围"), selection: $rule.scope) {
                Text("任何内容", bundle: ResourceBundle.resources).tag(EntryScope.any)
                Text("仅顶层文件", bundle: ResourceBundle.resources).tag(EntryScope.files)
                Text("仅顶层目录", bundle: ResourceBundle.resources).tag(EntryScope.folders)
            }
            TextField(L10n.string("全部文件后缀，逗号分隔"), text: $extensionsText)
                .onChange(of: extensionsText) { rule.extensions = parseExtensions($0) }
            TextField(L10n.string("顶层目录名称模式"), text: $rule.folderNamePattern)
            Text("目录模式使用 * 和 ?；留空不限制。后缀条件需要所有文件都匹配。", bundle: ResourceBundle.resources)
                .font(.caption).foregroundStyle(.secondary)
            TextField(L10n.string("最大字节数，0 表示不限制"), value: $rule.maxBytes, format: .number)
            TextField(L10n.string("目的地，留空用默认目录"), text: $rule.destination)
            Button { if let path = model.chooseDirectory(current: rule.destination.isEmpty ? model.settings.receiveDirectory : rule.destination) { rule.destination = path } } label: {
                Text("选择目的地…", bundle: ResourceBundle.resources)
            }
            Toggle(isOn: $rule.autoAccept) { Text("匹配时授权自动接收", bundle: ResourceBundle.resources) }
            if !model.pending.isEmpty {
                Menu {
                    ForEach(model.pending) { request in
                        Button { checking = request } label: { Text(verbatim: request.offer.senderName) }
                    }
                } label: { Text("用当前来件检查规则草稿", bundle: ResourceBundle.resources) }
            }
            if let request = checking {
                RulePreview(configuration: configuration, request: request)
            }
        }.formStyle(.grouped)
        .onAppear { extensionsText = rule.extensions.joined(separator: ", ") }
        .onChange(of: rule.id) { _ in extensionsText = rule.extensions.joined(separator: ", ") }
        .onChange(of: rule.extensions) { value in
            if parseExtensions(extensionsText) != value { extensionsText = value.joined(separator: ", ") }
        }
    }
    private func parseExtensions(_ text: String) -> [String] {
        text.split(whereSeparator: { $0 == "," || $0 == "，" }).map { $0.trimmingCharacters(in: .whitespacesAndNewlines) }
    }
}
@MainActor
private struct RulePreview: View {
    let configuration: AppSettings
    let request: PendingOffer
    var body: some View {
        VStack(alignment: .leading, spacing: 8) {
            let matched = configuration.rules.first {
                $0.enabled && ReceivePolicy.matches($0, offer: request.offer, fingerprint: request.fingerprint)
            }
            Text(verbatim: L10n.format("首条匹配规则：%@", matched?.name ?? L10n.string("无")))
            if let decision = try? ReceivePolicy.evaluate(settings: configuration, offer: request.offer, fingerprint: request.fingerprint) {
                Text(verbatim: decision.reason)
                Text(verbatim: decision.destination).textSelection(.enabled)
                Text(verbatim: L10n.string(decision.autoAccept ? "满足自动接收条件" : "需要手动确认"))
            } else {
                Text("当前草稿或来件未通过接收验证。", bundle: ResourceBundle.resources)
            }
            Text("预览不授予许可。实际接收使用已保存设置并再次检查身份、路径与接收状态。", bundle: ResourceBundle.resources)
                .font(.caption).foregroundStyle(.secondary)
        }
    }
}