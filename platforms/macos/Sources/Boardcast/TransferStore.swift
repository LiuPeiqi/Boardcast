import Foundation
import Combine

enum TransferDirection: String, Codable, Sendable {
    case sending, receiving
    var title: String { L10n.string(self == .sending ? "发送" : "接收") }
}

enum TransferPhase: String, Codable, CaseIterable, Sendable {
    case queued, preparing, awaitingApproval, transferring, verifying, saving
    case completed, failed, rejected, cancelled, interrupted, receiptUnconfirmed

    var isActive: Bool {
        switch self {
        case .queued, .preparing, .awaitingApproval, .transferring, .verifying, .saving: return true
        default: return false
        }
    }
    var title: String {
        let key: String
        switch self {
        case .queued: key = "排队"
        case .preparing: key = "准备内容"
        case .awaitingApproval: key = "等待确认"
        case .transferring: key = "传输"
        case .verifying: key = "校验"
        case .saving: key = "保存"
        case .completed: key = "完成"
        case .failed: key = "失败"
        case .rejected: key = "已拒绝"
        case .cancelled: key = "已取消"
        case .interrupted: key = "已中断"
        case .receiptUnconfirmed: key = "回执未确认"
        }
        return L10n.string(key)
    }
}

struct TransferProgress: Sendable {
    let id: String
    let direction: TransferDirection
    let peerFingerprint: String
    var phase: TransferPhase
    var completedBytes: Int64
    var totalBytes: Int64
    var savedFiles: Int = 0
    var destination: String? = nil
    var message: String? = nil
    var peerName: String? = nil
    var kind: String = "files"
}

struct TransferRecord: Codable, Identifiable, Sendable {
    var id: String
    var direction: TransferDirection
    var peerFingerprint: String
    var peerName: String
    var phase: TransferPhase
    var completedBytes: Int64 = 0
    var totalBytes: Int64 = 0
    var savedFiles: Int = 0
    var destination: String? = nil
    var message: String? = nil
    var kind: String? = nil
    var createdAt = Date()
    var updatedAt = Date()
    var isActive: Bool { phase.isActive }
}

enum OfferApproval: Sendable {
    case reject
    case accept(destination: String?)
}

/// A bounded journal of known outcomes. Legacy tasks never resume automatically.
@MainActor
final class TransferStore: ObservableObject {
    @Published private(set) var tasks: [TransferRecord] = []
    @Published private(set) var persistenceError: String?
    private var file: URL?
    private var lastWrite = Date.distantPast

    func configure(directory: URL) {
        file = directory.appendingPathComponent("history.json")
        guard let file, FileManager.default.fileExists(atPath: file.path) else { return }
        do {
            let data = try Data(contentsOf: file)
            guard data.count <= 4 * 1024 * 1024 else { throw BoardcastError.invalid(L10n.string("任务历史过大")) }
            tasks = Array(try JSONDecoder().decode([TransferRecord].self, from: data).prefix(200))
            for index in tasks.indices where tasks[index].isActive {
                tasks[index].phase = .interrupted
                tasks[index].message = L10n.string("上次运行已结束；请核查接收端后重新发送。")
            }
            persist()
        } catch { persistenceError = error.localizedDescription }
    }

    func enqueue(id: String, peer: PeerInfo, totalBytes: Int64) {
        tasks.insert(TransferRecord(id: id, direction: .sending, peerFingerprint: peer.fingerprint,
                                    peerName: peer.name, phase: .queued, totalBytes: totalBytes), at: 0)
        trim()
        persist()
    }

    func update(_ progress: TransferProgress, peerName: String? = nil) {
        if let index = tasks.firstIndex(where: { $0.id == progress.id && $0.direction == progress.direction }) {
            // Active progress cannot reopen a terminal task. A receiver keeps a known saved completion.
            guard tasks[index].isActive || tasks[index].phase == progress.phase ||
                (!progress.phase.isActive && tasks[index].phase != .completed && tasks[index].phase != .cancelled) ||
                (progress.direction == .receiving && progress.phase == .completed) else { return }
            tasks[index].kind = progress.kind
            tasks[index].phase = progress.phase
            tasks[index].completedBytes = progress.completedBytes
            tasks[index].totalBytes = progress.totalBytes
            tasks[index].savedFiles = progress.savedFiles
            tasks[index].destination = progress.destination ?? tasks[index].destination
            tasks[index].message = progress.message
            tasks[index].updatedAt = Date()
        } else {
            tasks.insert(TransferRecord(id: progress.id, direction: progress.direction,
                                        peerFingerprint: progress.peerFingerprint,
                                        peerName: peerName ?? progress.peerFingerprint,
                                        phase: progress.phase, completedBytes: progress.completedBytes,
                                        totalBytes: progress.totalBytes, savedFiles: progress.savedFiles,
                                        destination: progress.destination, message: progress.message, kind: progress.kind), at: 0)
        }
        trim()
        if !progress.phase.isActive || Date().timeIntervalSince(lastWrite) >= 2 { persist() }
    }

    func finish(id: String, phase: TransferPhase, message: String?) {
        guard let index = tasks.firstIndex(where: { $0.id == id }), tasks[index].isActive else { return }
        tasks[index].phase = phase
        tasks[index].message = message
        tasks[index].updatedAt = Date()
        persist()
    }

    private func trim() {
        let active = tasks.filter(\.isActive)
        tasks = active + Array(tasks.filter { !$0.isActive }.prefix(max(0, 200 - active.count)))
    }

    private func persist() {
        guard let file else { return }
        do {
            let data = try JSONEncoder().encode(tasks)
            try data.write(to: file, options: .atomic)
            try FileManager.default.setAttributes([.posixPermissions: 0o600], ofItemAtPath: file.path)
            lastWrite = Date()
            persistenceError = nil
        } catch { persistenceError = error.localizedDescription }
    }
}
