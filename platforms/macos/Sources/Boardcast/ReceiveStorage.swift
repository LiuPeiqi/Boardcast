import Foundation
import CryptoKit
import Darwin

/// Verified files are committed individually. Only private task records and
/// unfinished bytes remain in Application Support; received folders have no wrapper.
final class ReceiveStorage {
    let destination: URL
    let cacheDirectory: URL
    private let root: DirectoryHandle
    private let cache: DirectoryHandle
    private var directories: [String: String] = [:]
    private(set) var savedFiles = 0
    private(set) var mappings: [String: String] = [:]
    private var partialNames: Set<String> = []
    private let offerID: String
    private let fingerprint: String
    // Offer metadata is at most 3 MiB. Record only explicit manifest entries:
    // source + mapped JSON paths cost at most 6 MiB; each path component may
    // gain at most 9 ASCII bytes (" (100000)"), and component count cannot exceed
    // the offer byte count. Thus paths/container overhead fit below 33 MiB.
    // 64 MiB leaves room for local metadata; writing and cleanup share this cap.
    static let maxReceiptBytes = 64 * 1024 * 1024
    private static let liveLock = NSLock()
    private static var livePaths = Set<String>()

    init(offer: TransferOffer, fingerprint: String, destination: URL, cacheRoot: URL? = nil) throws {
        let approvedRoot = destination.standardized
        self.destination = approvedRoot
        self.offerID = offer.id
        self.fingerprint = fingerprint
        root = try DirectoryHandle.absolute(approvedRoot, create: true)
        let base = cacheRoot ?? FileManager.default.homeDirectoryForCurrentUser
            .appendingPathComponent("Library/Application Support/Boardcast/Transfers", isDirectory: true)
        let privateRoot = try DirectoryHandle.absolute(base, create: true, mode: 0o700)
        guard fchmod(privateRoot.fd, 0o700) == 0 else { throw Self.ioError("设置缓存权限") }
        Self.pruneExpiredTasks(at: base)
        let name = UUID().uuidString.lowercased()
        guard mkdirat(privateRoot.fd, name, 0o700) == 0 else { throw Self.ioError("创建私有接收缓存") }
        cacheDirectory = base.appendingPathComponent(name, isDirectory: true)
        cache = try privateRoot.child(name)
        Self.liveLock.lock(); Self.livePaths.insert(cacheDirectory.path); Self.liveLock.unlock()
        try writeRecord(state: "receiving")
    }

    deinit {
        Self.liveLock.lock(); Self.livePaths.remove(cacheDirectory.path); Self.liveLock.unlock()
    }

    func receiveDirectory(_ entry: TransferEntry) throws {
        guard entry.isDirectory, entry.size == 0 else { throw BoardcastError.invalid(L10n.string("目录条目无效")) }
        let relative = try ReceivePolicy.relativePath(entry.relativePath)
        mappings[relative] = try ensureDirectory(relative)
        try writeRecord(state: "receiving")
    }

    func receiveFile(_ entry: TransferEntry, read: (Int) async throws -> Data,
                     onBytes: (Int) async -> Void, onVerifying: () async -> Void = {}, onVerified: () async -> Void = {},
                     checkCancellation: () throws -> Void = { try Task.checkCancellation() }) async throws {
        guard !entry.isDirectory, entry.size >= 0 else { throw BoardcastError.invalid(L10n.string("文件条目无效")) }
        let relative = try ReceivePolicy.relativePath(entry.relativePath)
        let partial = UUID().uuidString.lowercased() + ".part"
        let fd = openat(cache.fd, partial, O_WRONLY | O_CREAT | O_EXCL | O_NOFOLLOW | O_CLOEXEC, 0o600)
        guard fd >= 0 else { throw Self.ioError("创建接收文件") }
        partialNames.insert(partial)
        let handle = FileHandle(fileDescriptor: fd, closeOnDealloc: true)
        do {
            var remaining = entry.size
            var hash = SHA256()
            while remaining > 0 {
                try checkCancellation()
                let count = Int(min(remaining, 256 * 1024))
                let bytes = try await read(count)
                guard bytes.count == count else { throw BoardcastError.invalid(L10n.string("文件提前结束")) }
                try handle.write(contentsOf: bytes)
                hash.update(data: bytes)
                remaining -= Int64(bytes.count)
                await onBytes(bytes.count)
            }
            await onVerifying()
            let expected = try await read(32)
            guard expected.count == 32, Data(hash.finalize()) == expected else {
                try? handle.close()
                _ = unlinkat(cache.fd, partial, 0)
                partialNames.remove(partial)
                throw BoardcastError.invalid(L10n.format("文件校验失败：%@", relative))
            }
            try checkCancellation()
            try handle.synchronize()
            try handle.close()
            await onVerified()
            try commit(partial: partial, relative: relative, expectedHash: expected, expectedSize: entry.size,
                       checkCancellation: checkCancellation)
        } catch {
            try? handle.close()
            throw error
        }
    }

    func finish() throws { try writeRecord(state: "completed") }
    func interrupted() { try? writeRecord(state: "interrupted") }

    /// Explicit cancellation removes only this task's unfinished private bytes.
    /// Files already published to the chosen destination always survive.
    func cancel() {
        for name in partialNames { _ = unlinkat(cache.fd, name, 0) }
        partialNames.removeAll()
        try? writeRecord(state: "cancelled")
    }

    private func ensureDirectory(_ source: String) throws -> String {
        if source.isEmpty { return "" }
        if let mapped = directories[source] { return mapped }
        let parentSource = (source as NSString).deletingLastPathComponent
        let parentMapped = try ensureDirectory(parentSource)
        let parent = try directory(parentMapped)
        let name = (source as NSString).lastPathComponent
        for number in 0...100_000 {
            let candidate = Self.conflictName(name, number: number, isDirectory: true)
            if mkdirat(parent.fd, candidate, 0o755) == 0 {
                let mapped = parentMapped.isEmpty ? candidate : parentMapped + "/" + candidate
                directories[source] = mapped
                return mapped
            }
            guard errno == EEXIST else { throw Self.ioError("创建接收目录") }
        }
        throw BoardcastError.invalid(L10n.string("接收目录存在过多同名项目"))
    }

    private func directory(_ relative: String) throws -> DirectoryHandle {
        var current = try root.duplicate()
        for component in relative.split(separator: "/") { current = try current.child(String(component)) }
        return current
    }

    static func conflictName(_ name: String, number: Int, isDirectory: Bool) -> String {
        guard number > 0 else { return name }
        let suffix = " (" + String(number) + ")"
        var ext = isDirectory ? "" : (name as NSString).pathExtension
        var base = ext.isEmpty ? name : (name as NSString).deletingPathExtension
        if ext.utf8.count + suffix.utf8.count + 1 >= 255 { ext = ""; base = name }
        let ending = suffix + (ext.isEmpty ? "" : "." + ext)
        while base.utf8.count + ending.utf8.count > 255 { base.removeLast() }
        return base + ending
    }

    private func commit(partial: String, relative: String, expectedHash: Data, expectedSize: Int64,
                        checkCancellation: () throws -> Void) throws {
        let parentSource = (relative as NSString).deletingLastPathComponent
        let parentMapped = try ensureDirectory(parentSource)
        let parent = try directory(parentMapped)
        let name = (relative as NSString).lastPathComponent
        var sourceDirectory = cache
        var sourceName = partial
        var crossVolumeCopy: String?
        defer { if let name = crossVolumeCopy { _ = unlinkat(parent.fd, name, 0) } }
        for number in 0...100_000 {
            try checkCancellation()
            let candidate = Self.conflictName(name, number: number, isDirectory: false)
            if renameatx_np(sourceDirectory.fd, sourceName, parent.fd, candidate, UInt32(RENAME_EXCL)) == 0 {
                if crossVolumeCopy != nil { _ = unlinkat(cache.fd, partial, 0) }
                partialNames.remove(partial)
                let mapped = parentMapped.isEmpty ? candidate : parentMapped + "/" + candidate
                mappings[relative] = mapped
                savedFiles += 1
                try writeRecord(state: "receiving")
                return
            }
            let failure = errno
            if failure == EEXIST { continue }
            if failure == EXDEV && crossVolumeCopy == nil {
                let temporary = ".boardcast-verified-" + UUID().uuidString.lowercased()
                try copyVerifiedFile(partial, to: parent, name: temporary, expectedHash: expectedHash,
                    expectedSize: expectedSize, checkCancellation: checkCancellation)
                crossVolumeCopy = temporary
                sourceDirectory = parent
                sourceName = temporary
                // Reattempt the same number, so numeric mapping stays stable.
                try checkCancellation()
                if renameatx_np(parent.fd, temporary, parent.fd, candidate, UInt32(RENAME_EXCL)) == 0 {
                    _ = unlinkat(cache.fd, partial, 0)
                    partialNames.remove(partial)
                    mappings[relative] = parentMapped.isEmpty ? candidate : parentMapped + "/" + candidate
                    savedFiles += 1
                    try writeRecord(state: "receiving")
                    return
                }
                guard errno == EEXIST else { throw Self.ioError("保存已校验文件") }
                continue
            }
            throw Self.ioError("保存已校验文件", code: failure)
        }
        throw BoardcastError.invalid(L10n.string("接收文件存在过多同名项目"))
    }

    private func copyVerifiedFile(_ name: String, to parent: DirectoryHandle, name target: String,
                                  expectedHash: Data, expectedSize: Int64, checkCancellation: () throws -> Void) throws {
        let inputFD = openat(cache.fd, name, O_RDONLY | O_NOFOLLOW | O_CLOEXEC)
        guard inputFD >= 0 else { throw Self.ioError("读取已校验文件") }
        let input = FileHandle(fileDescriptor: inputFD, closeOnDealloc: true)
        defer { try? input.close() }
        let outputFD = openat(parent.fd, target, O_WRONLY | O_CREAT | O_EXCL | O_NOFOLLOW | O_CLOEXEC, 0o600)
        guard outputFD >= 0 else { throw Self.ioError("创建已校验文件") }
        let output = FileHandle(fileDescriptor: outputFD, closeOnDealloc: true)
        do {
            var copied: Int64 = 0
            var hash = SHA256()
            while let data = try input.read(upToCount: 256 * 1024), !data.isEmpty {
                try checkCancellation()
                let sum = copied.addingReportingOverflow(Int64(data.count))
                guard !sum.overflow, sum.partialValue <= expectedSize else {
                    throw BoardcastError.invalid(L10n.string("已校验缓存大小改变"))
                }
                copied = sum.partialValue
                hash.update(data: data)
                try output.write(contentsOf: data)
            }
            guard copied == expectedSize, Data(hash.finalize()) == expectedHash else {
                throw BoardcastError.invalid(L10n.string("已校验缓存内容改变"))
            }
            try checkCancellation()
            try output.synchronize()
            try output.close()
        } catch {
            try? output.close()
            _ = unlinkat(parent.fd, target, 0)
            throw error
        }
    }

    private struct Record: Codable {
        let transferID: String
        let senderFingerprint: String
        let destination: String
        let savedPaths: [String: String]
        let state: String
        let updated: Date
    }

    /// Bounded cleanup is local only; a legacy stream never resumes from these bytes.
    /// Only recognized task files are removed, never any recorded destination.
    static func pruneExpiredTasks(at base: URL, now: Date = Date(), limit: Int = 32) {
        guard limit > 0, let parent = try? DirectoryHandle.absolute(base, create: false),
              let enumerator = FileManager.default.enumerator(at: base, includingPropertiesForKeys: [],
                  options: [.skipsSubdirectoryDescendants]) else { return }
        var inspected = 0
        var removed = 0
        while inspected < 512, removed < min(limit, 32), let url = enumerator.nextObject() as? URL {
            inspected += 1
            let name = url.lastPathComponent
            guard UUID(uuidString: name) != nil else { continue }
            liveLock.lock(); let live = livePaths.contains(url.path); liveLock.unlock()
            guard !live, let task = try? parent.child(name) else { continue }
            let recordFD = openat(task.fd, "receipt.json", O_RDONLY | O_NOFOLLOW | O_NONBLOCK | O_CLOEXEC)
            guard recordFD >= 0 else { continue }
            var recordInfo = stat()
            guard fstat(recordFD, &recordInfo) == 0, (recordInfo.st_mode & S_IFMT) == S_IFREG,
                  recordInfo.st_size <= Int64(maxReceiptBytes) else { Darwin.close(recordFD); continue }
            let handle = FileHandle(fileDescriptor: recordFD, closeOnDealloc: true)
            let data = try? handle.read(upToCount: maxReceiptBytes + 1)
            try? handle.close()
            guard let data, data.count <= maxReceiptBytes,
                  let record = try? JSONDecoder().decode(Record.self, from: data),
                  DeviceIdentity.isFingerprint(record.senderFingerprint),
                  record.destination.hasPrefix("/"),
                  record.transferID.range(of: "^[A-Za-z0-9-]{1,64}$", options: .regularExpression) != nil,
                  ["receiving", "interrupted", "completed", "cancelled"].contains(record.state),
                  now.timeIntervalSince(record.updated) >= 7 * 24 * 60 * 60,
                  let contents = try? FileManager.default.contentsOfDirectory(atPath: url.path),
                  contents.count <= 4 else { continue }
            // Interrupted legacy tasks have at most one partial file. Refuse foreign
            // or nested content rather than applying a recursive deletion.
            var recognized = true
            for item in contents {
                let ext = (item as NSString).pathExtension
                let stem = (item as NSString).deletingPathExtension
                guard item == "receipt.json" || ((ext == "part" || ext == "json") && UUID(uuidString: stem) != nil) else {
                    recognized = false; break
                }
                var attributes = stat()
                guard fstatat(task.fd, item, &attributes, AT_SYMLINK_NOFOLLOW) == 0,
                      (attributes.st_mode & S_IFMT) == S_IFREG else { recognized = false; break }
            }
            guard recognized else { continue }
            liveLock.lock(); let becameLive = livePaths.contains(url.path); liveLock.unlock()
            guard !becameLive else { continue }
            var deleted = true
            for item in contents where item != "receipt.json" {
                if unlinkat(task.fd, item, 0) != 0 { deleted = false }
            }
            // Keep the record when deleting incomplete bytes fails, so another
            // cleanup pass can recognize and retry this task safely.
            if deleted && unlinkat(task.fd, "receipt.json", 0) == 0 &&
                unlinkat(parent.fd, name, AT_REMOVEDIR) == 0 { removed += 1 }
        }
    }

    private func writeRecord(state: String) throws {
        let data = try JSONEncoder().encode(Record(transferID: offerID, senderFingerprint: fingerprint,
            destination: destination.path, savedPaths: mappings, state: state, updated: Date()))
        guard data.count <= Self.maxReceiptBytes else {
            throw BoardcastError.invalid(L10n.string("接收记录超过 64 MiB 限额"))
        }
        let temporary = UUID().uuidString + ".json"
        let fd = openat(cache.fd, temporary, O_WRONLY | O_CREAT | O_EXCL | O_NOFOLLOW | O_CLOEXEC, 0o600)
        guard fd >= 0 else { throw Self.ioError("创建接收记录") }
        let handle = FileHandle(fileDescriptor: fd, closeOnDealloc: true)
        do {
            try handle.write(contentsOf: data)
            try handle.synchronize()
            try handle.close()
            guard renameat(cache.fd, temporary, cache.fd, "receipt.json") == 0 else { throw Self.ioError("保存接收记录") }
        } catch { try? handle.close(); _ = unlinkat(cache.fd, temporary, 0); throw error }
    }

    private static func ioError(_ action: String, code: Int32 = errno) -> Error {
        BoardcastError.invalid(L10n.format("%@：%@", L10n.string(action), String(cString: strerror(code))))
    }

    /// Each ancestor is opened without following a symlink. File descriptors
    /// retain the selected directory across pathname changes during the transfer.
    private final class DirectoryHandle {
        let fd: Int32
        init(_ fd: Int32) { self.fd = fd }
        deinit { Darwin.close(fd) }
        func duplicate() throws -> DirectoryHandle {
            let copy = fcntl(fd, F_DUPFD_CLOEXEC, 0)
            guard copy >= 0 else { throw ReceiveStorage.ioError("复制接收目录句柄") }
            return DirectoryHandle(copy)
        }
        func child(_ name: String) throws -> DirectoryHandle {
            let result = openat(fd, name, O_RDONLY | O_DIRECTORY | O_NOFOLLOW | O_CLOEXEC)
            guard result >= 0 else { throw ReceiveStorage.ioError("打开接收目录（禁止符号链接）") }
            return DirectoryHandle(result)
        }
        static func absolute(_ url: URL, create: Bool, mode: mode_t = 0o755) throws -> DirectoryHandle {
            guard url.isFileURL, url.path.hasPrefix("/") else { throw BoardcastError.invalid(L10n.string("接收路径必须为绝对路径")) }
            let fd = Darwin.open("/", O_RDONLY | O_DIRECTORY | O_CLOEXEC)
            guard fd >= 0 else { throw ReceiveStorage.ioError("打开根目录") }
            var current = DirectoryHandle(fd)
            for component in url.standardized.pathComponents.dropFirst() {
                if create && mkdirat(current.fd, component, mode) != 0 && errno != EEXIST {
                    throw ReceiveStorage.ioError("创建接收路径")
                }
                current = try current.child(component)
            }
            return current
        }
    }
}
