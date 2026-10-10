import Foundation
import Network

/// State, timers and continuation completion live on one queue.
final class WireConnection {
    let connection: NWConnection
    static let maxFrame = 4 * 1024 * 1024
    private let queue = DispatchQueue(label: "Boardcast.TLS", qos: .utility)
    private var pending: [UUID: (Error) -> Void] = [:]
    private var startID: UUID?
    private var startCompletion: (() -> Void)?
    private var terminalError: Error?
    private var closeHandlers: [() -> Void] = []
    private var started = false
    private var idleTimer: DispatchSourceTimer?
    private var lastActivity = DispatchTime.now().uptimeNanoseconds
    private var idleSeconds: UInt64 = 90

    init(_ connection: NWConnection) { self.connection = connection }

    func start() async throws {
        try Task.checkCancellation()
        try await withTaskCancellationHandler(operation: {
            try await withCheckedThrowingContinuation { (continuation: CheckedContinuation<Void, Error>) in
                queue.async {
                    if let error = self.terminalError { continuation.resume(throwing: error); return }
                    guard !self.started else { continuation.resume(throwing: BoardcastError.invalid(L10n.string("连接已经启动"))); return }
                    self.started = true
                    let id = UUID()
                    self.startID = id
                    self.pending[id] = { continuation.resume(throwing: $0) }
                    self.startCompletion = { continuation.resume() }
                    self.connection.stateUpdateHandler = { [weak self] state in
                        guard let self else { return }
                        switch state {
                        case .ready:
                            guard let id = self.startID, self.pending.removeValue(forKey: id) != nil else { return }
                            self.startID = nil
                            let completion = self.startCompletion
                            self.startCompletion = nil
                            self.lastActivity = DispatchTime.now().uptimeNanoseconds
                            self.startIdleTimer()
                            completion?()
                        case .failed(let error): self.terminate(error)
                        case .cancelled: self.terminate(CancellationError())
                        default: break
                        }
                    }
                    self.connection.start(queue: self.queue)
                    self.queue.asyncAfter(deadline: .now() + 15) { [weak self] in
                        guard let self, self.startID != nil else { return }
                        self.terminate(BoardcastError.invalid(L10n.string("TLS 握手超时")))
                    }
                }
            }
        }, onCancel: { self.close() })
    }

    func close() { queue.async { self.terminate(CancellationError()) } }
    func whenClosed(_ handler: @escaping () -> Void) {
        queue.async { if self.terminalError != nil { handler() } else { self.closeHandlers.append(handler) } }
    }
    func setIdleTimeout(_ seconds: UInt64) {
        queue.async { self.idleSeconds = seconds; self.lastActivity = DispatchTime.now().uptimeNanoseconds }
    }

    private func startIdleTimer() {
        let timer = DispatchSource.makeTimerSource(queue: queue)
        timer.schedule(deadline: .now() + 5, repeating: 5, leeway: .seconds(1))
        timer.setEventHandler { [weak self] in
            guard let self else { return }
            let elapsed = DispatchTime.now().uptimeNanoseconds - self.lastActivity
            if elapsed > self.idleSeconds * 1_000_000_000 { self.terminate(BoardcastError.invalid(L10n.string("连接无数据传输超时"))) }
        }
        idleTimer = timer; timer.resume()
    }

    private func terminate(_ error: Error) {
        guard terminalError == nil else { return }
        terminalError = error
        startID = nil; startCompletion = nil
        idleTimer?.cancel(); idleTimer = nil
        connection.cancel()
        let waiters = Array(pending.values)
        pending.removeAll()
        for resume in waiters { resume(error) }
        let handlers = closeHandlers; closeHandlers.removeAll()
        for handler in handlers { handler() }
    }

    func send(_ data: Data) async throws {
        try Task.checkCancellation()
        try await withTaskCancellationHandler(operation: {
            try await withCheckedThrowingContinuation { (continuation: CheckedContinuation<Void, Error>) in
                queue.async {
                    if let error = self.terminalError { continuation.resume(throwing: error); return }
                    let id = UUID()
                    self.pending[id] = { continuation.resume(throwing: $0) }
                    self.connection.send(content: data, completion: .contentProcessed { error in
                        guard self.pending.removeValue(forKey: id) != nil else { return }
                        self.lastActivity = DispatchTime.now().uptimeNanoseconds
                        if let error { continuation.resume(throwing: error); self.terminate(error) }
                        else { continuation.resume() }
                    })
                }
            }
        }, onCancel: { self.close() })
    }

    func read(_ count: Int) async throws -> Data {
        guard count >= 0, count <= Self.maxFrame else { throw BoardcastError.invalid(L10n.string("读取长度无效")) }
        var data = Data()
        while data.count < count {
            try Task.checkCancellation()
            let remaining = count - data.count
            let chunk: Data = try await withTaskCancellationHandler(operation: {
                try await withCheckedThrowingContinuation { continuation in
                    queue.async {
                        if let error = self.terminalError { continuation.resume(throwing: error); return }
                        let id = UUID()
                        self.pending[id] = { continuation.resume(throwing: $0) }
                        self.connection.receive(minimumIncompleteLength: 1, maximumLength: remaining) { content, _, complete, error in
                            guard self.pending.removeValue(forKey: id) != nil else { return }
                            if let error { continuation.resume(throwing: error); self.terminate(error) }
                            else if let content, !content.isEmpty {
                                self.lastActivity = DispatchTime.now().uptimeNanoseconds
                                continuation.resume(returning: content)
                            } else {
                                let failure = BoardcastError.invalid(L10n.string(complete ? "连接提前关闭" : "连接未返回数据"))
                                continuation.resume(throwing: failure); self.terminate(failure)
                            }
                        }
                    }
                }
            }, onCancel: { self.close() })
            data.append(chunk)
        }
        return data
    }

    func sendJSON<T: Encodable>(_ value: T) async throws {
        let data = try JSONEncoder().encode(value)
        guard (2...Self.maxFrame).contains(data.count) else { throw BoardcastError.invalid(L10n.string("协议消息大小无效")) }
        var size = UInt32(data.count).bigEndian
        try await send(withUnsafeBytes(of: &size) { Data($0) } + data)
    }
    func readJSON<T: Decodable>(_ type: T.Type) async throws -> T {
        let header = try await read(4)
        let length = header.reduce(UInt32(0)) { ($0 << 8) | UInt32($1) }
        guard (2...Self.maxFrame).contains(Int(length)) else { throw BoardcastError.invalid(L10n.string("无效协议帧长度")) }
        return try JSONDecoder().decode(type, from: try await read(Int(length)))
    }
}
