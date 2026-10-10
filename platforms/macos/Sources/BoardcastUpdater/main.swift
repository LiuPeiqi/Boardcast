import Foundation
import AppKit
import Darwin

/// An ordinary, signed child process. It never requests administrator rights or kills an app.
private final class Installer {
    private let planURL: URL
    private let plan: MacInstallPlan
    private let current: URL
    private let candidate: URL
    private let root: URL
    private let stageDirectory: URL
    private let backup: URL
    private var backupCreated = false
    private var replacementInstalled = false
    private var lockDescriptor: Int32 = -1

    init(planURL: URL) throws {
        self.planURL = planURL
        plan = try MacRelease.loadPlan(at: planURL)
        current = URL(fileURLWithPath: plan.currentApp)
        candidate = URL(fileURLWithPath: plan.stagedApp)
        root = URL(fileURLWithPath: plan.dataDirectory)
        let parent = current.deletingLastPathComponent()
        stageDirectory = parent.appendingPathComponent(".Boardcast-install-\(plan.id.uuidString)", isDirectory: true)
        backup = parent.appendingPathComponent(".Boardcast-backup-\(plan.id.uuidString).app", isDirectory: true)
    }
    deinit { if lockDescriptor >= 0 { _ = flock(lockDescriptor, LOCK_UN); close(lockDescriptor) } }

    func run() throws {
        try acquireLock()
        let ownURL = URL(fileURLWithPath: CommandLine.arguments[0])
        guard ownURL.path == planURL.deletingLastPathComponent().appendingPathComponent(MacRelease.helperName).path else {
            throw MacUpdateError.invalid("The installer is outside its private installation directory.")
        }
        try MacRelease.verifyHelper(at: ownURL, expected: plan.currentIdentity)
        try verifyInstalledRelease()
        guard try MacRelease.verify(app: candidate, expected: plan.currentIdentity) == plan.candidateIdentity else {
            throw MacUpdateError.invalid("The staged application identity changed.")
        }
        try waitForParentExit()
        try verifyInstalledRelease()
        try requireNoOtherRunningInstance()
        let fileManager = FileManager.default
        guard !fileManager.fileExists(atPath: stageDirectory.path), !fileManager.fileExists(atPath: backup.path) else {
            throw MacUpdateError.invalid("An installation directory already exists.")
        }
        try MacRelease.createPrivateDirectory(stageDirectory)
        let copied = stageDirectory.appendingPathComponent("Boardcast.app")
        _ = try MacRelease.run(executable: "/usr/bin/ditto", arguments: [candidate.path, copied.path], timeout: 600)
        guard try MacRelease.verify(app: copied, expected: plan.currentIdentity) == plan.candidateIdentity else {
            throw MacUpdateError.invalid("The copied application identity changed.")
        }
        try requireNoOtherRunningInstance()
        do {
            try MacRelease.requireNoSymlinks(backup, allowMissing: true)
            _ = try fileManager.replaceItemAt(current, withItemAt: copied,
                backupItemName: backup.lastPathComponent,
                options: [.usingNewMetadataOnly, .withoutDeletingBackupItem])
            backupCreated = true
            replacementInstalled = true
            guard try MacRelease.verify(app: current, expected: plan.currentIdentity) == plan.candidateIdentity else {
                throw MacUpdateError.invalid("The installed application identity changed.")
            }
        } catch {
            if replacementInstalled {
                try restoreIfStopped()
                _ = try? launch(update: false)
            } else {
                backupCreated = fileManager.fileExists(atPath: backup.path)
                if (try? recoverReplacementFailure()) == true { _ = try? launch(update: false) }
            }
            throw error
        }
        var launchedApplication: NSRunningApplication?
        do {
            let application = try launch(update: true)
            launchedApplication = application
            if try awaitReady(application: application) {
                try record(status: "completed", message: "The higher version started successfully.")
            } else if application.isTerminated {
                try restoreIfStopped()
                _ = try? launch(update: false)
                try record(status: "rolledBack", message: "The new application exited before confirming startup. The previous version was restored.")
            } else {
                // A process may already be handling user files. Keep it and the previous app intact.
                try record(status: "startupUnconfirmed", message: "The new application is running, but startup was not confirmed. The previous application is retained for manual recovery.")
            }
        } catch is PendingLaunchError {
            try record(status: "startupUnconfirmed", message: "The macOS launch request is still pending. The new and previous applications are retained.")
        } catch {
            let confirmedStopped = launchedApplication.map { $0.isTerminated } ?? true
            if confirmedStopped && noRunningInstance() {
                try restoreIfStopped()
                _ = try? launch(update: false)
                try record(status: "rolledBack", message: "Application launch failed. The previous version was restored: \(error.localizedDescription)")
            } else {
                try record(status: "startupUnconfirmed", message: "The new application is running. The previous application is retained: \(error.localizedDescription)")
            }
        }
    }

    func recordFailure(_ error: Error) {
        let location = (error as NSError).userInfo["NSFileOriginalItemLocationKey"] as? URL
        let detail = (error as? MacUpdateError)?.diagnosticDescription ?? error.localizedDescription
        try? record(status: "failed", message: detail, originalItemLocation: location?.path)
    }
    private func verifyInstalledRelease() throws {
        guard let bundle = Bundle(url: current), try MacRelease.current(bundle: bundle) == plan.currentIdentity else {
            throw MacUpdateError.invalid("The installed application changed before replacement.")
        }
    }
    private func acquireLock() throws {
        let path = root.appendingPathComponent("install.lock")
        try MacRelease.requireNoSymlinks(path, allowMissing: true)
        lockDescriptor = open(path.path, O_RDWR | O_CREAT | O_NOFOLLOW | O_CLOEXEC, 0o600)
        var info = stat()
        guard lockDescriptor >= 0, fstat(lockDescriptor, &info) == 0,
              (info.st_mode & S_IFMT) == S_IFREG, info.st_uid == getuid(), info.st_nlink == 1,
              (info.st_mode & 0o077) == 0, flock(lockDescriptor, LOCK_EX | LOCK_NB) == 0 else {
            throw MacUpdateError.invalid("Another update is running or the installation lock is invalid.")
        }
    }
    private func waitForParentExit() throws {
        guard let parent = NSRunningApplication(processIdentifier: plan.parentPID), !parent.isTerminated else {
            try markWaiting(); return
        }
        guard parent.launchDate == plan.parentLaunchDate else { try markWaiting(); return } // The original instance has already exited; its PID was reused.
        guard parent.bundleURL?.path == current.path,
              parent.executableURL?.path == current.appendingPathComponent("Contents/MacOS/Boardcast").path else {
            throw MacUpdateError.invalid("The original application process does not match the installation plan.")
        }
        try markWaiting()
        let deadline = ProcessInfo.processInfo.systemUptime + 120
        while !parent.isTerminated && ProcessInfo.processInfo.systemUptime < deadline {
            waitTick()
        }
        guard parent.isTerminated else { throw MacUpdateError.invalid("Boardcast did not finish exiting. No application was replaced.") }
    }
    private func waitTick() {
        if !RunLoop.current.run(mode: .default, before: Date().addingTimeInterval(0.1)) {
            Thread.sleep(forTimeInterval: 0.05)
        }
    }
    private func markWaiting() throws {
        try MacRelease.writePrivateJSON(MacUpdateHelperReady(id: plan.id, phase: "waiting"),
            at: planURL.deletingLastPathComponent().appendingPathComponent("helper-ready.json"))
    }
    private func noRunningInstance() -> Bool {
        !NSRunningApplication.runningApplications(withBundleIdentifier: plan.currentIdentity.bundleID)
            .contains { !$0.isTerminated && $0.bundleURL?.path == current.path }
    }
    private func requireNoOtherRunningInstance() throws {
        guard noRunningInstance() else { throw MacUpdateError.invalid("Another Boardcast instance is still running. No application was replaced.") }
    }
    /// Recover only known, signed states at our fixed same-volume application paths.
    private func recoverReplacementFailure() throws -> Bool {
        try requireNoOtherRunningInstance()
        if FileManager.default.fileExists(atPath: current.path) {
            guard let bundle = Bundle(url: current) else { return false }
            let identity = try MacRelease.current(bundle: bundle)
            if identity == plan.currentIdentity { return true }
            guard identity == plan.candidateIdentity, backupCreated else { return false }
            replacementInstalled = true
        } else {
            guard backupCreated else { return false }
        }
        // restoreIfStopped validates the retained backup's full Developer ID identity.
        try restoreIfStopped()
        guard let bundle = Bundle(url: current) else { return false }
        return try MacRelease.current(bundle: bundle) == plan.currentIdentity
    }
    private func restoreIfStopped() throws {
        try requireNoOtherRunningInstance()
        guard backupCreated else { return }
        guard let bundle = Bundle(url: backup), try MacRelease.current(bundle: bundle) == plan.currentIdentity else {
            throw MacUpdateError.invalid("The retained application backup is invalid.")
        }
        if replacementInstalled {
            let failed = current.deletingLastPathComponent().appendingPathComponent(".Boardcast-failed-\(plan.id.uuidString).app")
            try MacRelease.requireNoSymlinks(failed, allowMissing: true)
            guard !FileManager.default.fileExists(atPath: failed.path) else {
                throw MacUpdateError.invalid("A failed-release recovery item already exists.")
            }
            _ = try FileManager.default.replaceItemAt(current, withItemAt: backup,
                backupItemName: failed.lastPathComponent,
                options: [.usingNewMetadataOnly, .withoutDeletingBackupItem])
            replacementInstalled = false
        } else if !FileManager.default.fileExists(atPath: current.path) {
            try FileManager.default.moveItem(at: backup, to: current)
        }
        backupCreated = FileManager.default.fileExists(atPath: backup.path)
    }
    private enum PendingLaunchError: Error { case unconfirmed }
    private final class LaunchOutcome: @unchecked Sendable {
        private let lock = NSLock()
        private var finished = false
        private var application: NSRunningApplication?
        private var failure: Error?
        func complete(_ application: NSRunningApplication?, _ error: Error?) {
            lock.lock(); defer { lock.unlock() }
            self.application = application; failure = error; finished = true
        }
        func value() -> (Bool, NSRunningApplication?, Error?) {
            lock.lock(); defer { lock.unlock() }
            return (finished, application, failure)
        }
    }
    private func launch(update: Bool) throws -> NSRunningApplication {
        let configuration = NSWorkspace.OpenConfiguration()
        configuration.activates = true
        configuration.createsNewApplicationInstance = true
        if update {
            configuration.environment = ["BOARDCAST_UPDATE_PLAN": planURL.path,
                "BOARDCAST_UPDATE_TOKEN": plan.id.uuidString]
        }
        let outcome = LaunchOutcome()
        NSWorkspace.shared.openApplication(at: current, configuration: configuration) { app, error in outcome.complete(app, error) }
        let deadline = ProcessInfo.processInfo.systemUptime + 30
        while !outcome.value().0 && ProcessInfo.processInfo.systemUptime < deadline {
            waitTick()
        }
        let (finished, application, failure) = outcome.value()
        if let failure { throw failure }
        guard finished, let application else { throw PendingLaunchError.unconfirmed }
        return application
    }
    private func awaitReady(application: NSRunningApplication) throws -> Bool {
        let url = planURL.deletingLastPathComponent().appendingPathComponent("ready.json")
        let deadline = ProcessInfo.processInfo.systemUptime + 45
        while ProcessInfo.processInfo.systemUptime < deadline {
            if FileManager.default.fileExists(atPath: url.path) {
                let ready = try JSONDecoder().decode(MacUpdateReady.self, from: MacRelease.boundedData(at: url, maximum: 4096))
                guard ready.id == plan.id, ready.version == plan.candidateIdentity.version,
                      ready.processID == application.processIdentifier, ready.launchDate == application.launchDate,
                      application.bundleURL?.path == current.path, !application.isTerminated else {
                    throw MacUpdateError.invalid("The startup acknowledgment does not match the newly launched instance.")
                }
                return true
            }
            if application.isTerminated { return false }
            waitTick()
        }
        return false
    }
    private func record(status: String, message: String, originalItemLocation: String? = nil) throws {
        let result = MacUpdateResult(id: plan.id, status: status,
            previousVersion: plan.currentIdentity.version, version: plan.candidateIdentity.version,
            backupPath: backupCreated ? backup.path : nil,
            date: Date(), message: String(message.prefix(2048)), originalItemLocation: originalItemLocation)
        try MacRelease.writePrivateJSON(result, at: root.appendingPathComponent("last-update.json"))
    }
}

private var installer: Installer?
do {
    guard CommandLine.arguments.count == 3, CommandLine.arguments[1] == "--plan" else {
        throw MacUpdateError.invalid("Usage: BoardcastUpdater --plan <absolute-plan.json>")
    }
    let candidate = try Installer(planURL: URL(fileURLWithPath: CommandLine.arguments[2]))
    installer = candidate
    try candidate.run()
    exit(EXIT_SUCCESS)
} catch {
    installer?.recordFailure(error)
    let detail = (error as? MacUpdateError)?.diagnosticDescription ?? error.localizedDescription
    fputs("Boardcast update failed: \(detail)\n", stderr)
    exit(EXIT_FAILURE)
}