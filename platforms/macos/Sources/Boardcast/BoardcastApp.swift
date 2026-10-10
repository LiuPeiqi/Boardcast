import AppKit
import Carbon
import Combine
import SwiftUI

@main
@MainActor
struct BoardcastApp: App {
    @StateObject private var model: AppModel
    @NSApplicationDelegateAdaptor(AppDelegate.self) private var delegate

    init() {
        let model = AppModel()
        _model = StateObject(wrappedValue: model)
        AppDelegate.model = model
    }

    var body: some Scene {
        Window("Boardcast", id: "main") {
            WorkbenchView(model: model)
                .background(MainWindowRegistration())
                .frame(minWidth: 820, minHeight: 560)
        }
        .defaultSize(width: 1040, height: 680)
        .commands { SidebarCommands(); BoardcastCommands(model: model, draft: model.draft) }
        WindowGroup(id: "task", for: String.self) { $taskID in
            TransferDetailView(model: model, taskID: taskID ?? "")
                .frame(minWidth: 460, minHeight: 360)
        }
        .defaultSize(width: 560, height: 480)
        .commandsRemoved()
        Settings { SettingsView(model: model) }
        MenuBarExtra("Boardcast", systemImage: "square.and.arrow.down") {
            BoardcastStatusMenu(model: model, tasks: model.tasks)
        }
    }
}

@MainActor
final class AppDelegate: NSObject, NSApplicationDelegate {
    static weak var model: AppModel?
    static weak var instance: AppDelegate?
    static var reopenMainWindow: (() -> Void)?
    private var terminationObservation: AnyCancellable?
    private var isWaitingForTasks = false
    private var handledLoginLaunch = false
    private var didFinishLaunching = false
    private var isLoginLaunch = false
    private weak var mainWindow: NSWindow?

    override init() { super.init(); Self.instance = self }
    func applicationDidFinishLaunching(_ notification: Notification) {
        didFinishLaunching = true
        let event = NSAppleEventManager.shared().currentAppleEvent
        isLoginLaunch = event?.eventID == kAEOpenApplication
            && event?.paramDescriptor(forKeyword: keyAEPropData)?.enumCodeValue == keyAELaunchedAsLogInItem
        if let mainWindow { registerMainWindow(mainWindow) }
    }

    func applicationShouldTerminateAfterLastWindowClosed(_ sender: NSApplication) -> Bool { false }
    func applicationShouldHandleReopen(_ sender: NSApplication, hasVisibleWindows flag: Bool) -> Bool {
        Self.reopenMainWindow?()
        return false
    }
    func applicationShouldTerminate(_ sender: NSApplication) -> NSApplication.TerminateReply {
        guard let model = Self.model else { return .terminateNow }
        if isWaitingForTasks { return .terminateLater }
        guard model.hasActiveOperationsAndTasks else {
            model.stop()
            return .terminateNow
        }
        let alert = NSAlert()
        alert.messageText = L10n.string("还有未结束的文件传输或更新操作")
        alert.informativeText = L10n.string("完成后退出会拒绝待确认来件并停止接受新的文件和更新请求。停止任务并退出会取消本机文件传输和更新操作，已保存的文件会保留。")
        alert.addButton(withTitle: L10n.string("完成后退出"))
        alert.addButton(withTitle: L10n.string("停止任务并退出"))
        alert.addButton(withTitle: L10n.string("返回"))
        switch alert.runModal() {
        case .alertFirstButtonReturn:
            isWaitingForTasks = true
            var changes = [model.objectWillChange.eraseToAnyPublisher(),
                           model.tasks.objectWillChange.eraseToAnyPublisher(),
                           model.draft.objectWillChange.eraseToAnyPublisher()]
            if let updates = model.updates { changes.append(updates.objectWillChange.eraseToAnyPublisher()) }
            terminationObservation = Publishers.MergeMany(changes).prepend(Void()).sink { [weak self, weak model] _ in
                // ObservableObject emits before mutations; evaluate the committed state on the next turn.
                DispatchQueue.main.async {
                    guard let self, let model, self.isWaitingForTasks,
                          !model.hasActiveOperationsAndTasks else { return }
                    self.isWaitingForTasks = false
                    self.terminationObservation = nil
                    model.stop()
                    NSApp.reply(toApplicationShouldTerminate: true)
                }
            }
            model.beginWaitingForTermination()
            model.draft.cancelScanning()
            return .terminateLater
        case .alertSecondButtonReturn:
            model.stop()
            return .terminateNow
        default:
            return .terminateCancel
        }
    }
    func applicationWillTerminate(_ notification: Notification) { Self.model?.stop() }
    func registerMainWindow(_ window: NSWindow) {
        window.identifier = NSUserInterfaceItemIdentifier("Boardcast.main")
        mainWindow = window
        guard didFinishLaunching, isLoginLaunch, !handledLoginLaunch else { return }
        handledLoginLaunch = true
        DispatchQueue.main.async { window.close() }
    }
}

/// Captures this scene's window without selecting Settings or a file panel.
@MainActor
private struct MainWindowRegistration: NSViewRepresentable {
    @Environment(\.openWindow) private var openWindow
    func makeNSView(context: Context) -> WindowView { WindowView() }
    func updateNSView(_ view: WindowView, context: Context) {
        AppDelegate.reopenMainWindow = { openWindow(id: "main"); NSApp.activate(ignoringOtherApps: true) }
    }
    final class WindowView: NSView {
        override func viewDidMoveToWindow() {
            super.viewDidMoveToWindow()
            guard let window, let delegate = AppDelegate.instance else { return }
            delegate.registerMainWindow(window)
        }
    }
}

extension Notification.Name {
    static let boardcastShowSend = Notification.Name("Boardcast.showSend")
    static let boardcastShowTransfers = Notification.Name("Boardcast.showTransfers")
}

@MainActor
private struct BoardcastCommands: Commands {
    @ObservedObject var model: AppModel
    @ObservedObject var draft: SendDraft
    @Environment(\.openWindow) private var openWindow
    var body: some Commands {
        CommandGroup(replacing: .newItem) {
            Button { model.addFiles() } label: { Text("添加文件或文件夹…", bundle: ResourceBundle.resources) }
                .keyboardShortcut("o")
            Button { draft.clear(); model.fileSearch = "" } label: { Text("清空发送草稿", bundle: ResourceBundle.resources) }
                .disabled(draft.items.isEmpty && !draft.isScanning)
            Divider()
            Button { model.reveal(path: model.settings.receiveDirectory) } label: { Text("显示接收位置", bundle: ResourceBundle.resources) }
        }
        CommandGroup(after: .sidebar) {
            Button { show(.boardcastShowSend) } label: { Text("发送", bundle: ResourceBundle.resources) }
                .keyboardShortcut("1")
            Button { show(.boardcastShowTransfers) } label: { Text("传输记录", bundle: ResourceBundle.resources) }
                .keyboardShortcut("2")
        }
        CommandMenu(L10n.string("传输")) {
            Button { model.sendDraft() } label: { Text("发送", bundle: ResourceBundle.resources) }
                .keyboardShortcut(.return, modifiers: .command)
                .disabled(!model.canSend)
            Button { model.toggleReceiving() } label: {
                Text(verbatim: L10n.string(model.settings.receivingEnabled ? "暂停新接收" : "恢复新接收"))
            }.disabled(model.terminationPending)
        }
        CommandGroup(after: .windowArrangement) {
            Button { openWindow(id: "main"); NSApp.activate(ignoringOtherApps: true) } label: {
                Text("显示 Boardcast", bundle: ResourceBundle.resources)
            }
        }
    }
    private func show(_ notification: Notification.Name) {
        openWindow(id: "main")
        NotificationCenter.default.post(name: notification, object: nil)
        NSApp.activate(ignoringOtherApps: true)
    }
}

@MainActor
private struct BoardcastStatusMenu: View {
    @ObservedObject var model: AppModel
    @ObservedObject var tasks: TransferStore
    @Environment(\.openWindow) private var openWindow
    var body: some View {
        Text(verbatim: model.status)
        Text(verbatim: L10n.string(model.settings.receivingEnabled ? "允许新接收" : "接收已暂停"))
        Text(verbatim: L10n.format("%d 个待确认 · %d 个活动任务", model.pending.count, tasks.tasks.filter(\.isActive).count))
        Divider()
        Button { openWindow(id: "main"); NSApp.activate(ignoringOtherApps: true) } label: {
            Text("显示 Boardcast", bundle: ResourceBundle.resources)
        }
        Button { model.toggleReceiving() } label: {
            Text(verbatim: L10n.string(model.settings.receivingEnabled ? "暂停新接收" : "恢复新接收"))
        }.disabled(model.terminationPending)
        Button { model.reveal(path: model.settings.receiveDirectory) } label: { Text("显示接收位置", bundle: ResourceBundle.resources) }
        if #available(macOS 14.0, *) {
            SettingsLink { Text("设置…", bundle: ResourceBundle.resources) }
        } else {
            Button {
                NSApp.activate(ignoringOtherApps: true)
                NSApp.sendAction(Selector(("showSettingsWindow:")), to: nil, from: nil)
            } label: { Text("设置…", bundle: ResourceBundle.resources) }
        }
        Divider()
        Button { NSApp.terminate(nil) } label: { Text("退出 Boardcast", bundle: ResourceBundle.resources) }
            .keyboardShortcut("q")
        .onAppear {
            AppDelegate.reopenMainWindow = { openWindow(id: "main"); NSApp.activate(ignoringOtherApps: true) }
        }
    }
}
