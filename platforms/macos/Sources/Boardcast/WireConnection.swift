import Foundation
import Network

final class WireConnection {
    let connection: NWConnection
    static let maxFrame = 4 * 1024 * 1024
    private let queue = DispatchQueue(label: "Boardcast.TLS", qos: .utility)
    private var idleTimer: DispatchSourceTimer?
    private var lastActivity = Date()
    init(_ connection: NWConnection) { self.connection = connection }
    func start() async throws {
        try await withCheckedThrowingContinuation { (continuation: CheckedContinuation<Void, Error>) in
            var finished = false
            connection.stateUpdateHandler = { state in
                guard !finished else { return }
                switch state {
                case .ready: finished = true; continuation.resume()
                case .failed(let error): finished = true; continuation.resume(throwing: error)
                case .cancelled: finished = true; continuation.resume(throwing: CancellationError())
                default: break
                }
            }
            let timer = DispatchSource.makeTimerSource(queue: queue)
            timer.schedule(deadline: .now() + 30, repeating: 30, leeway: .seconds(5))
            timer.setEventHandler { [weak self] in
                guard let self else { return }
                if Date().timeIntervalSince(self.lastActivity) > 150 { self.connection.cancel() }
            }
            idleTimer = timer; timer.resume()
            connection.start(queue: queue)
            queue.asyncAfter(deadline: .now() + 15) { [weak self] in
                if !finished { self?.connection.cancel() }
            }
        }
    }
    func close() { connection.cancel(); queue.async { self.idleTimer?.cancel(); self.idleTimer = nil } }
    func send(_ data: Data) async throws {
        try await withCheckedThrowingContinuation { (continuation: CheckedContinuation<Void, Error>) in
            connection.send(content: data, completion: .contentProcessed { error in
                self.lastActivity = Date()
                if let error { continuation.resume(throwing: error) } else { continuation.resume() }
            })
        }
    }
    func read(_ count: Int) async throws -> Data {
        var data = Data()
        while data.count < count {
            let remaining = count - data.count
            let chunk: Data = try await withCheckedThrowingContinuation { continuation in
                connection.receive(minimumIncompleteLength: 1, maximumLength: remaining) { content, _, complete, error in
                    self.lastActivity = Date()
                    if let error { continuation.resume(throwing: error) }
                    else if let content, !content.isEmpty { continuation.resume(returning: content) }
                    else if complete { continuation.resume(throwing: BoardcastError.invalid("连接提前关闭")) }
                    else { continuation.resume(throwing: BoardcastError.invalid("连接未返回数据")) }
                }
            }
            data.append(chunk)
        }
        return data
    }
    func sendJSON<T: Encodable>(_ value: T) async throws {
        let data = try JSONEncoder().encode(value)
        guard data.count <= Self.maxFrame else { throw BoardcastError.invalid("协议消息过大") }
        var size = UInt32(data.count).bigEndian
        try await send(withUnsafeBytes(of: &size) { Data($0) } + data)
    }
    func readJSON<T: Decodable>(_ type: T.Type) async throws -> T {
        let header = try await read(4)
        let length = header.reduce(UInt32(0)) { ($0 << 8) | UInt32($1) }
        guard length > 0, length <= Self.maxFrame else { throw BoardcastError.invalid("无效协议帧长度") }
        let data = try await read(Int(length))
        return try JSONDecoder().decode(type, from: data)
    }
}
