import AppKit
import Foundation
import SwiftUI
import UniformTypeIdentifiers

/// One local catalog supplies the shared Windows/Mac classification and assets.
struct FileIconCatalog: Decodable, Sendable {
    struct Asset: Decodable, Sendable {
        let id: String
        let file: String
        let sha256: String
    }

    let extensionMapping: [String: String]
    let namedMapping: [String: String]
    let fallback: String
    let directory: String
    let platformMapping: [String: String]
    let assets: [Asset]

    static let shared: FileIconCatalog = {
        guard let url = ResourceBundle.url(forResource: "catalog", withExtension: "json", subdirectory: "Icons"),
              let data = try? Data(contentsOf: url),
              let catalog = try? JSONDecoder().decode(FileIconCatalog.self, from: data) else {
            assertionFailure("The bundled file icon catalog is missing or invalid.")
            return FileIconCatalog(extensionMapping: [:], namedMapping: [:], fallback: "file",
                                   directory: "folder", platformMapping: [:], assets: [])
        }
        return catalog
    }()

    /// Directory, special basename, final suffix, then generic file, in that order.
    func fileType(name: String, isDirectory: Bool) -> String {
        if isDirectory { return directory }
        let basename = name.split(omittingEmptySubsequences: false, whereSeparator: { $0 == "/" || $0 == "\\" }).last.map(String.init) ?? name
        let normalized = basename.lowercased()
        if let icon = namedMapping[normalized] { return icon }
        guard let dot = normalized.lastIndex(of: ".") else { return fallback }
        return extensionMapping[String(normalized[dot...])] ?? fallback
    }

    func platformType(_ platform: String) -> String {
        platformMapping[platform.lowercased()] ?? platformMapping["unknown"] ?? "platform-unknown"
    }
}

@MainActor
enum FileIcons {
    private static let images: [String: NSImage] = {
        var result: [String: NSImage] = [:]
        for asset in FileIconCatalog.shared.assets {
            let file = asset.file as NSString
            guard let url = ResourceBundle.url(forResource: file.deletingPathExtension,
                                               withExtension: file.pathExtension, subdirectory: "Icons"),
                  let image = NSImage(contentsOf: url) else { continue }
            result[asset.id] = image
        }
        return result
    }()

    static func file(url: URL?, name: String, isDirectory: Bool) -> NSImage {
        if isDirectory {
            // Callers pass only an authorized local URL. Remote entries pass nil.
            if let url, url.isFileURL,
               let values = try? url.resourceValues(forKeys: [.isDirectoryKey, .isPackageKey, .isSymbolicLinkKey]),
               values.isDirectory == true, values.isPackage != true, values.isSymbolicLink != true {
                return NSWorkspace.shared.icon(forFile: url.path)
            }
            return NSWorkspace.shared.icon(for: .folder)
        }
        let catalog = FileIconCatalog.shared
        return images[catalog.fileType(name: name, isDirectory: false)]
            ?? images[catalog.fallback]
            ?? NSWorkspace.shared.icon(for: .data)
    }

    static func platform(_ platform: String) -> NSImage {
        images[FileIconCatalog.shared.platformType(platform)]
            ?? NSImage(systemSymbolName: "desktopcomputer", accessibilityDescription: nil)
            ?? NSWorkspace.shared.icon(for: .data)
    }
}

/// File colors and native folder appearance are preserved in both color schemes.
@MainActor
struct FileIcon: View {
    var url: URL?
    var name: String
    var isDirectory: Bool
    var size: CGFloat = 28

    var body: some View {
        Image(nsImage: FileIcons.file(url: url, name: name, isDirectory: isDirectory))
            .renderingMode(.original)
            .resizable()
            .interpolation(.high)
            .aspectRatio(contentMode: .fit)
            .frame(width: size, height: size)
            .accessibilityHidden(true)
    }
}

@MainActor
struct PlatformIcon: View {
    var platform: String
    var size: CGFloat = 16

    var body: some View {
        Image(nsImage: FileIcons.platform(platform))
            .renderingMode(.template)
            .resizable()
            .interpolation(.high)
            .aspectRatio(contentMode: .fit)
            .frame(width: size, height: size)
            .foregroundStyle(.secondary)
            .accessibilityHidden(true)
    }
}
