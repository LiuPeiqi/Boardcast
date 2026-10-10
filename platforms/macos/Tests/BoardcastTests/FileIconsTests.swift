import AppKit
import CryptoKit
import XCTest
@testable import Boardcast

final class FileIconsTests: XCTestCase {
    private var catalog: FileIconCatalog { .shared }

    func testDirectoriesOverrideNamesAndSuffixes() {
        for name in ["report.pdf", "Dockerfile", ".gitignore", "", "/tmp/photo.PNG"] {
            XCTAssertEqual(catalog.fileType(name: name, isDirectory: true), "folder", name)
        }
    }

    func testAllSharedSuffixesAndSpecialNamesIgnoreCase() {
        for (suffix, icon) in catalog.extensionMapping {
            XCTAssertEqual(catalog.fileType(name: "example" + suffix, isDirectory: false), icon, suffix)
            XCTAssertEqual(catalog.fileType(name: "example" + suffix.uppercased(), isDirectory: false), icon, suffix)
        }
        for (name, icon) in catalog.namedMapping {
            XCTAssertEqual(catalog.fileType(name: "/remote/path/" + name.uppercased(), isDirectory: false), icon, name)
            XCTAssertEqual(catalog.fileType(name: "C:\\remote\\" + name, isDirectory: false), icon, name)
        }
        // The final suffix is txt; the named rule must take precedence.
        XCTAssertEqual(catalog.fileType(name: "CMakeLists.TXT", isDirectory: false), "settings")
    }

    func testOnlyTheFinalSuffixIsUsedAndUnknownNamesFallBack() {
        XCTAssertEqual(catalog.fileType(name: "archive.pdf.zip", isDirectory: false), "zip")
        XCTAssertEqual(catalog.fileType(name: "photo.png.unknown", isDirectory: false), "file")
        for name in ["", "README.unknown", "extensionless", "trailing.", ".unknown", "/folder.pdf/", "C:\\folder.png\\"] {
            XCTAssertEqual(catalog.fileType(name: name, isDirectory: false), "file", name)
        }
    }

    func testPlatformAliasesAndUnknownPlatform() {
        for platform in ["mac", "MACOS", "osx"] {
            XCTAssertEqual(catalog.platformType(platform), "platform-macos")
        }
        XCTAssertEqual(catalog.platformType("WINDOWS"), "platform-windows")
        XCTAssertEqual(catalog.platformType("linux"), "platform-unknown")
        XCTAssertEqual(catalog.platformType(""), "platform-unknown")
    }

    func testEveryBundledAssetHasTheExportedBytesAndAllMappingsResolve() throws {
        XCTAssertEqual(catalog.assets.count, 35)
        let ids = Set(catalog.assets.map(\.id))
        XCTAssertEqual(ids.count, catalog.assets.count)
        XCTAssertFalse(ids.contains("folder-macos"))
        let references = Set(catalog.extensionMapping.values)
            .union(catalog.namedMapping.values)
            .union(catalog.platformMapping.values)
            .union([catalog.fallback, catalog.directory])
        XCTAssertTrue(references.isSubset(of: ids))
        for asset in catalog.assets {
            let filename = asset.file as NSString
            let url = try XCTUnwrap(ResourceBundle.url(forResource: filename.deletingPathExtension,
                                                      withExtension: filename.pathExtension, subdirectory: "Icons"))
            let data = try Data(contentsOf: url)
            let hash = SHA256.hash(data: data).map { String(format: "%02x", $0) }.joined()
            XCTAssertEqual(hash, asset.sha256, asset.id)
            let image = try XCTUnwrap(NSBitmapImageRep(data: data), asset.id)
            XCTAssertEqual(image.pixelsWide, 112, asset.id)
            XCTAssertEqual(image.pixelsHigh, 112, asset.id)
        }
        XCTAssertNotNil(ResourceBundle.url(forResource: "Material-Icon-Theme", withExtension: "LICENSE", subdirectory: "ThirdParty"))
    }

    @MainActor
    func testRemoteAndUnavailableFoldersUseTheNativeFolderIcon() throws {
        let expected = try XCTUnwrap(NSWorkspace.shared.icon(for: .folder).tiffRepresentation)
        XCTAssertEqual(FileIcons.file(url: nil, name: "remote.pdf", isDirectory: true).tiffRepresentation, expected)
        let missing = URL(fileURLWithPath: "/tmp/boardcast-missing-\(UUID().uuidString)", isDirectory: true)
        XCTAssertEqual(FileIcons.file(url: missing, name: "missing", isDirectory: true).tiffRepresentation, expected)
        let remote = URL(string: "https://example.invalid/folder")
        XCTAssertEqual(FileIcons.file(url: remote, name: "remote", isDirectory: true).tiffRepresentation, expected)
    }
}
