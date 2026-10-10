import Foundation
import AppKit
import Security
import Darwin

/// A stable CFBundleShortVersionString. Build numbers never authorize a downgrade.
struct MacVersion: Codable, Comparable, Hashable, Sendable, CustomStringConvertible {
    let rawValue: String
    private let components: [UInt64]

    init(_ value: String) throws {
        let parts = value.split(separator: ".", omittingEmptySubsequences: false)
        guard parts.count == 3, parts.allSatisfy({ part in
            !part.isEmpty && part.utf8.allSatisfy { $0 >= 48 && $0 <= 57 }
                && (part.count == 1 || part.first != "0") && UInt64(part) != nil
        }) else { throw MacUpdateError.invalid("The release version must contain three non-negative integers.") }
        rawValue = value
        components = parts.map { UInt64($0)! }
    }
    var description: String { rawValue }
    static func < (lhs: Self, rhs: Self) -> Bool {
        lhs.components.lexicographicallyPrecedes(rhs.components)
    }
    init(from decoder: Decoder) throws { try self.init(decoder.singleValueContainer().decode(String.self)) }
    func encode(to encoder: Encoder) throws {
        var container = encoder.singleValueContainer()
        try container.encode(rawValue)
    }
}

struct MacReleaseIdentity: Codable, Equatable, Sendable {
    let teamID: String
    let bundleID: String
    let version: MacVersion
    let build: String
    let minimumSystemVersion: String
    let runtime: String
}

struct MacInstallPlan: Codable, Sendable {
    let id: UUID
    let currentApp: String
    let stagedApp: String
    let dataDirectory: String
    let parentPID: Int32
    let parentLaunchDate: Date
    let currentIdentity: MacReleaseIdentity
    let candidateIdentity: MacReleaseIdentity
}

struct MacUpdateHelperReady: Codable, Sendable {
    let id: UUID
    let phase: String
}

struct MacUpdateReady: Codable, Sendable {
    let id: UUID
    let version: MacVersion
    let processID: Int32
    let launchDate: Date
}

struct MacUpdateResult: Codable, Sendable {
    let id: UUID
    let status: String
    let previousVersion: MacVersion
    let version: MacVersion
    let backupPath: String?
    let date: Date
    let message: String
    let originalItemLocation: String?
}

enum MacUpdateError: LocalizedError {
    case invalid(String)
    case signatureFailed(detail: String)
    case commandFailed(executable: String, detail: String)
    var errorDescription: String? {
        switch self {
        case .invalid(let message): return message
        case .signatureFailed: return "Developer ID verification failed."
        case .commandFailed: return "The macOS update command failed."
        }
    }
    var diagnosticDescription: String {
        switch self {
        case .invalid(let message): return message
        case .signatureFailed(let detail): return "Developer ID verification failed: \(detail)"
        case .commandFailed(let executable, let detail): return "The macOS update command failed (\(executable)): \(detail)"
        }
    }
}

/// Release authenticity belongs to macOS code signing and Gatekeeper, not a second signature format.
enum MacRelease {
    static let runtime = "osx-universal"
    static let helperName = "BoardcastUpdater"
    private static let architectures: Set<String> = ["arm64", "x86_64"]

    static func current(bundle: Bundle = .main) throws -> MacReleaseIdentity {
        let app = bundle.bundleURL
        let metadata = try readMetadata(app: app)
        let team = try signatureInformation(app)[kSecCodeInfoTeamIdentifier as String] as? String
        guard let team, isTeamID(team) else { throw MacUpdateError.invalid("LAN updates require an installed Developer ID release.") }
        let identity = MacReleaseIdentity(teamID: team, bundleID: metadata.bundleID,
            version: metadata.version, build: metadata.build,
            minimumSystemVersion: metadata.minimumSystemVersion, runtime: runtime)
        try verifyApplication(app, expected: identity)
        return identity
    }

    @discardableResult
    static func verify(app: URL, expected: MacReleaseIdentity, requireNewer: Bool = true) throws -> MacReleaseIdentity {
        try requireNoSymlinks(app)
        let metadata = try readMetadata(app: app)
        guard metadata.bundleID == expected.bundleID, expected.runtime == runtime, isTeamID(expected.teamID) else {
            throw MacUpdateError.invalid("The update belongs to a different application or developer.")
        }
        guard !requireNewer || metadata.version > expected.version else {
            throw MacUpdateError.invalid("The update version must be higher than the installed version.")
        }
        try verifyApplication(app, expected: expected)
        guard systemSupports(metadata.minimumSystemVersion) else {
            throw MacUpdateError.invalid("This release requires a newer macOS version.")
        }
        _ = try run(executable: "/usr/sbin/spctl", arguments: ["--assess", "--type", "execute", app.path])
        // --check-notarization requires a valid notarization ticket, including the stapled offline ticket.
        _ = try run(executable: "/usr/bin/codesign", arguments: ["--verify", "--check-notarization", app.path])
        return MacReleaseIdentity(teamID: expected.teamID, bundleID: metadata.bundleID,
            version: metadata.version, build: metadata.build,
            minimumSystemVersion: metadata.minimumSystemVersion, runtime: runtime)
    }

    static func verifyHelper(at helper: URL, expected: MacReleaseIdentity) throws {
        try requireNoSymlinks(helper)
        try verifySignature(helper, identifier: expected.bundleID + ".updater", team: expected.teamID)
    }
    private struct Metadata {
        let bundleID: String
        let version: MacVersion
        let build: String
        let minimumSystemVersion: String
    }
    private static func readMetadata(app: URL) throws -> Metadata {
        guard app.pathExtension == "app" else { throw MacUpdateError.invalid("The release must be an application bundle.") }
        let plist = app.appendingPathComponent("Contents/Info.plist")
        try requireNoSymlinks(plist)
        let bytes = try boundedData(at: plist, maximum: 256 * 1024)
        guard let dictionary = try PropertyListSerialization.propertyList(from: bytes, options: [], format: nil) as? [String: Any],
              let bundleID = dictionary["CFBundleIdentifier"] as? String,
              isBundleID(bundleID), dictionary["CFBundlePackageType"] as? String == "APPL",
              dictionary["CFBundleExecutable"] as? String == "Boardcast",
              let version = dictionary["CFBundleShortVersionString"] as? String,
              let build = dictionary["CFBundleVersion"] as? String, isBuildNumber(build),
              let minimum = dictionary["LSMinimumSystemVersion"] as? String,
              parseSystemVersion(minimum) != nil else {
            throw MacUpdateError.invalid("The application bundle metadata is invalid.")
        }
        return Metadata(bundleID: bundleID, version: try MacVersion(version), build: build, minimumSystemVersion: minimum)
    }
    private static func isTeamID(_ value: String) -> Bool {
        value.utf8.count == 10 && value.utf8.allSatisfy { ($0 >= 65 && $0 <= 90) || ($0 >= 48 && $0 <= 57) }
    }
    private static func isBundleID(_ value: String) -> Bool {
        value.utf8.count <= 255 && value.contains(".") && value.utf8.allSatisfy {
            ($0 >= 65 && $0 <= 90) || ($0 >= 97 && $0 <= 122) || ($0 >= 48 && $0 <= 57) || $0 == 45 || $0 == 46
        }
    }
    private static func isBuildNumber(_ value: String) -> Bool {
        let parts = value.split(separator: ".", omittingEmptySubsequences: false)
        guard (1...3).contains(parts.count) else { return false }
        return parts.enumerated().allSatisfy { index, part in
            !part.isEmpty && part.count <= (index == 0 ? 4 : 2)
                && part.utf8.allSatisfy { $0 >= 48 && $0 <= 57 }
                && UInt64(part) != nil && (index != 0 || UInt64(part)! > 0)
        }
    }
    private static func parseSystemVersion(_ value: String) -> OperatingSystemVersion? {
        let parts = value.split(separator: ".", omittingEmptySubsequences: false)
        guard (2...3).contains(parts.count), parts.allSatisfy({ !$0.isEmpty && $0.utf8.allSatisfy { $0 >= 48 && $0 <= 57 } && Int($0) != nil }) else { return nil }
        return OperatingSystemVersion(majorVersion: Int(parts[0])!, minorVersion: Int(parts[1])!, patchVersion: parts.count == 3 ? Int(parts[2])! : 0)
    }
    private static func systemSupports(_ value: String) -> Bool {
        guard let version = parseSystemVersion(value) else { return false }
        return ProcessInfo.processInfo.isOperatingSystemAtLeast(version)
    }
    private static func signatureInformation(_ url: URL) throws -> [String: Any] {
        var code: SecStaticCode?
        guard SecStaticCodeCreateWithPath(url as CFURL, [], &code) == errSecSuccess, let code else {
            throw MacUpdateError.invalid("The code signature cannot be read.")
        }
        let flags = SecCSFlags(rawValue: kSecCSCheckAllArchitectures | kSecCSCheckNestedCode | kSecCSStrictValidate)
        guard SecStaticCodeCheckValidity(code, flags, nil) == errSecSuccess else {
            throw MacUpdateError.invalid("The installed application signature is invalid.")
        }
        var information: CFDictionary?
        guard SecCodeCopySigningInformation(code, SecCSFlags(rawValue: kSecCSSigningInformation), &information) == errSecSuccess,
              let result = information as? [String: Any] else { throw MacUpdateError.invalid("The signing identity is unavailable.") }
        return result
    }
    private static func verifySignature(_ url: URL, identifier: String, team: String) throws {
        guard isBundleID(identifier), isTeamID(team) else { throw MacUpdateError.invalid("The signing identity is invalid.") }
        let text = "anchor apple generic and identifier \"\(identifier)\" and certificate 1[field.1.2.840.113635.100.6.2.6] exists and certificate leaf[field.1.2.840.113635.100.6.1.13] exists and certificate leaf[subject.OU] = \"\(team)\""
        var requirement: SecRequirement?
        guard SecRequirementCreateWithString(text as CFString, [], &requirement) == errSecSuccess, let requirement else {
            throw MacUpdateError.invalid("The Developer ID requirement cannot be created.")
        }
        // Verify the same signer and identifier in each executable slice, not only the host slice.
        for architecture in architectures.sorted() {
            var code: SecStaticCode?
            let attributes = [kSecCodeAttributeArchitecture as String: architecture] as CFDictionary
            guard SecStaticCodeCreateWithPathAndAttributes(url as CFURL, [], attributes, &code) == errSecSuccess, let code else {
                throw MacUpdateError.invalid("The release is missing a supported architecture.")
            }
            var failure: Unmanaged<CFError>?
            let flags = SecCSFlags(rawValue: kSecCSCheckAllArchitectures | kSecCSCheckNestedCode | kSecCSStrictValidate)
            guard SecStaticCodeCheckValidityWithErrors(code, flags, requirement, &failure) == errSecSuccess else {
                let detail = failure.map { ($0.takeRetainedValue() as Error).localizedDescription } ?? "invalid signature"
                throw MacUpdateError.signatureFailed(detail: detail)
            }
        }
    }
    private static func verifyApplication(_ app: URL, expected: MacReleaseIdentity) throws {
        // Security's architecture attribute verifies both required slices without developer tools.
        try verifySignature(app, identifier: expected.bundleID, team: expected.teamID)
        try verifyHelper(at: app.appendingPathComponent("Contents/MacOS/\(helperName)"), expected: expected)
    }

    static func updateDirectory() throws -> URL {
        let url = FileManager.default.homeDirectoryForCurrentUser
            .appendingPathComponent("Library/Application Support/Boardcast/Updates", isDirectory: true)
        try requireNoSymlinks(url, allowMissing: true)
        return url
    }

    @discardableResult
    static func validateInstallTarget(_ app: URL) throws -> URL {
        let allowed = [URL(fileURLWithPath: "/Applications/Boardcast.app"),
            FileManager.default.homeDirectoryForCurrentUser.appendingPathComponent("Applications/Boardcast.app")]
        guard allowed.contains(where: { $0.path == app.path }), app.path == app.standardized.path else {
            throw MacUpdateError.invalid("Install Boardcast in /Applications or ~/Applications before updating.")
        }
        try requireNoSymlinks(app)
        guard FileManager.default.isWritableFile(atPath: app.deletingLastPathComponent().path) else {
            throw MacUpdateError.invalid("The application directory is not writable. Move Boardcast to ~/Applications.")
        }
        return app
    }

    static func processLaunchDate(pid: Int32) throws -> Date {
        guard pid > 1, let application = NSRunningApplication(processIdentifier: pid),
              !application.isTerminated, let date = application.launchDate else {
            throw MacUpdateError.invalid("The running application instance cannot be identified.")
        }
        return date
    }

    static func loadPlan(at url: URL) throws -> MacInstallPlan {
        let plan = try JSONDecoder().decode(MacInstallPlan.self, from: boundedData(at: url, maximum: 64 * 1024))
        let root = try updateDirectory()
        let expected = root.appendingPathComponent("Install/\(plan.id.uuidString)/plan.json")
        guard url.path == expected.path, plan.dataDirectory == root.path,
              plan.parentPID > 1, plan.parentLaunchDate <= Date(),
              plan.currentIdentity.runtime == runtime, plan.candidateIdentity.runtime == runtime,
              plan.candidateIdentity.teamID == plan.currentIdentity.teamID,
              plan.candidateIdentity.bundleID == plan.currentIdentity.bundleID,
              plan.candidateIdentity.version > plan.currentIdentity.version else {
            throw MacUpdateError.invalid("The installation plan is invalid.")
        }
        try requirePrivateDirectory(root)
        try requirePrivateDirectory(url.deletingLastPathComponent())
        try requirePrivateFile(url)
        _ = try validateInstallTarget(URL(fileURLWithPath: plan.currentApp))
        let staged = URL(fileURLWithPath: plan.stagedApp)
        guard staged.path.hasPrefix(root.path + "/"), staged.lastPathComponent == "Boardcast.app" else {
            throw MacUpdateError.invalid("The staged release is outside the private update cache.")
        }
        try requireNoSymlinks(staged)
        return plan
    }

    /// Called once after the application's services have successfully started.
    static func acknowledgeStartup(bundle: Bundle = .main) throws {
        let environment = ProcessInfo.processInfo.environment
        guard let path = environment["BOARDCAST_UPDATE_PLAN"] else { return }
        guard let token = environment["BOARDCAST_UPDATE_TOKEN"], let id = UUID(uuidString: token) else {
            throw MacUpdateError.invalid("The update startup token is invalid.")
        }
        let url = URL(fileURLWithPath: path)
        let plan = try loadPlan(at: url)
        guard id == plan.id, bundle.bundleURL.path == plan.currentApp,
              try current(bundle: bundle) == plan.candidateIdentity else {
            throw MacUpdateError.invalid("The started application does not match this update.")
        }
        let ready = MacUpdateReady(id: id, version: plan.candidateIdentity.version,
            processID: getpid(), launchDate: try processLaunchDate(pid: getpid()))
        try writePrivateJSON(ready, at: url.deletingLastPathComponent().appendingPathComponent("ready.json"))
    }

    static func boundedData(at url: URL, maximum: Int) throws -> Data {
        try requireNoSymlinks(url)
        var info = stat()
        guard lstat(url.path, &info) == 0, (info.st_mode & S_IFMT) == S_IFREG,
              info.st_size >= 0, info.st_size <= Int64(maximum) else { throw MacUpdateError.invalid("The update metadata file is invalid or too large.") }
        let descriptor = open(url.path, O_RDONLY | O_NOFOLLOW | O_NONBLOCK | O_CLOEXEC)
        guard descriptor >= 0 else { throw MacUpdateError.invalid("The update metadata file cannot be opened.") }
        defer { close(descriptor) }
        var opened = stat()
        guard fstat(descriptor, &opened) == 0, (opened.st_mode & S_IFMT) == S_IFREG,
              opened.st_size >= 0, opened.st_size <= Int64(maximum) else {
            throw MacUpdateError.invalid("The update metadata file changed while opening.")
        }
        let handle = FileHandle(fileDescriptor: descriptor, closeOnDealloc: false)
        var data = Data()
        while data.count <= maximum {
            let bytes = try handle.read(upToCount: min(64 * 1024, maximum + 1 - data.count)) ?? Data()
            if bytes.isEmpty { break }
            data.append(bytes)
        }
        guard data.count <= maximum else { throw MacUpdateError.invalid("The update metadata file is too large.") }
        return data
    }

    static func requireNoSymlinks(_ url: URL, allowMissing: Bool = false) throws {
        guard url.isFileURL, url.path.hasPrefix("/"), url.path == url.standardized.path,
              !url.path.utf8.contains(0) else { throw MacUpdateError.invalid("An update path is not a canonical absolute path.") }
        var current = ""
        let components = url.path.split(separator: "/")
        for (index, component) in components.enumerated() {
            current += "/" + component
            var info = stat()
            if lstat(current, &info) != 0 {
                if allowMissing && errno == ENOENT { return }
                throw MacUpdateError.invalid("An update path cannot be inspected.")
            }
            guard (info.st_mode & S_IFMT) != S_IFLNK,
                  index == components.count - 1 || (info.st_mode & S_IFMT) == S_IFDIR else {
                throw MacUpdateError.invalid("An update path contains a symbolic link or a non-directory parent.")
            }
        }
    }

    static func createPrivateDirectory(_ url: URL) throws {
        try requireNoSymlinks(url, allowMissing: true)
        try FileManager.default.createDirectory(at: url, withIntermediateDirectories: true,
            attributes: [.posixPermissions: NSNumber(value: 0o700)])
        try requireNoSymlinks(url)
        try FileManager.default.setAttributes([.posixPermissions: NSNumber(value: 0o700)], ofItemAtPath: url.path)
        try requirePrivateDirectory(url)
    }
    static func requirePrivateDirectory(_ url: URL) throws {
        try requireNoSymlinks(url)
        var info = stat()
        guard lstat(url.path, &info) == 0, (info.st_mode & S_IFMT) == S_IFDIR,
              info.st_uid == getuid(), (info.st_mode & 0o077) == 0 else {
            throw MacUpdateError.invalid("The update cache must be a private directory owned by the current user.")
        }
    }
    private static func requirePrivateFile(_ url: URL) throws {
        try requireNoSymlinks(url)
        var info = stat()
        guard lstat(url.path, &info) == 0, (info.st_mode & S_IFMT) == S_IFREG,
              info.st_uid == getuid(), (info.st_mode & 0o077) == 0 else {
            throw MacUpdateError.invalid("The installation plan must be a private file owned by the current user.")
        }
    }
    static func writePrivateJSON<T: Encodable>(_ value: T, at url: URL) throws {
        try requirePrivateDirectory(url.deletingLastPathComponent())
        try requireNoSymlinks(url, allowMissing: true)
        let encoder = JSONEncoder()
        encoder.outputFormatting = [.sortedKeys]
        let data = try encoder.encode(value)
        guard data.count <= 64 * 1024 else { throw MacUpdateError.invalid("The update metadata is too large.") }
        try data.write(to: url, options: .atomic)
        try FileManager.default.setAttributes([.posixPermissions: NSNumber(value: 0o600)], ofItemAtPath: url.path)
    }
    /// Arguments go directly to a system executable. A reader drains the pipe while it runs.
    static func run(executable: String, arguments: [String], timeout: TimeInterval = 120, cancellable: Bool = true) throws -> Data {
        let tools: Set<String> = ["/usr/sbin/spctl", "/usr/bin/codesign", "/usr/bin/hdiutil", "/usr/bin/ditto"]
        guard tools.contains(executable), timeout > 0, timeout <= 600,
              arguments.allSatisfy({ !$0.utf8.contains(0) }) else {
            throw MacUpdateError.invalid("An unsupported update command was requested.")
        }
        if cancellable { try Task.checkCancellation() }
        let process = Process()
        process.executableURL = URL(fileURLWithPath: executable)
        process.arguments = arguments
        var environment = ProcessInfo.processInfo.environment.filter { !$0.key.hasPrefix("DYLD_") && !$0.key.hasPrefix("LD_") }
        environment["PATH"] = "/usr/bin:/bin:/usr/sbin:/sbin"
        process.environment = environment
        let outputPipe = Pipe()
        let errorPipe = Pipe()
        process.standardOutput = outputPipe
        process.standardError = errorPipe
        let collector = CommandOutput()
        let errors = CommandOutput()
        let drained = DispatchGroup()
        for (pipe, buffer) in [(outputPipe, collector), (errorPipe, errors)] {
            drained.enter()
            DispatchQueue.global(qos: .utility).async {
                defer { drained.leave() }
                do {
                    while let bytes = try pipe.fileHandleForReading.read(upToCount: 64 * 1024), !bytes.isEmpty {
                        buffer.append(bytes)
                    }
                } catch { buffer.fail() }
            }
        }
        do { try process.run() } catch {
            try? outputPipe.fileHandleForWriting.close()
            try? errorPipe.fileHandleForWriting.close()
            _ = drained.wait(timeout: .now() + 2)
            throw error
        }
        try? outputPipe.fileHandleForWriting.close()
        try? errorPipe.fileHandleForWriting.close()
        let deadline = ProcessInfo.processInfo.systemUptime + timeout
        while process.isRunning && !(cancellable && Task.isCancelled) && ProcessInfo.processInfo.systemUptime < deadline { Thread.sleep(forTimeInterval: 0.02) }
        let cancelled = cancellable && Task.isCancelled
        let timedOut = process.isRunning && !cancelled
        if process.isRunning {
            process.terminate()
            let grace = ProcessInfo.processInfo.systemUptime + 2
            while process.isRunning && ProcessInfo.processInfo.systemUptime < grace { Thread.sleep(forTimeInterval: 0.02) }
            if process.isRunning { _ = kill(process.processIdentifier, SIGKILL) }
        }
        process.waitUntilExit()
        let drainedNormally = drained.wait(timeout: .now() + 5) == .success
        try? outputPipe.fileHandleForReading.close()
        try? errorPipe.fileHandleForReading.close()
        let (output, overflow, readFailed) = collector.snapshot()
        let (errorOutput, errorOverflow, errorReadFailed) = errors.snapshot()
        if cancelled { throw CancellationError() }
        guard !timedOut else { throw MacUpdateError.invalid("A macOS update command timed out.") }
        guard drainedNormally, !overflow, !readFailed, !errorOverflow, !errorReadFailed else { throw MacUpdateError.invalid("A macOS update command returned invalid or excessive output.") }
        guard process.terminationReason == .exit, process.terminationStatus == 0 else {
            let detail = String(decoding: (errorOutput.isEmpty ? output : errorOutput).prefix(2048), as: UTF8.self)
                .trimmingCharacters(in: .whitespacesAndNewlines)
            throw MacUpdateError.commandFailed(executable: executable, detail: detail)
        }
        return output
    }

    /// Only the pipe reader and the calling thread share this bounded buffer.
    private final class CommandOutput: @unchecked Sendable {
        private let lock = NSLock()
        private var data = Data()
        private var overflow = false
        private var readFailed = false
        func append(_ bytes: Data) {
            lock.lock(); defer { lock.unlock() }
            if data.count + bytes.count > 4 * 1024 * 1024 { overflow = true }
            if !overflow { data.append(bytes) }
        }
        func fail() { lock.lock(); readFailed = true; lock.unlock() }
        func snapshot() -> (Data, Bool, Bool) {
            lock.lock(); defer { lock.unlock() }
            return (data, overflow, readFailed)
        }
    }
}