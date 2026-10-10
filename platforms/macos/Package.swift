// swift-tools-version: 5.9
import PackageDescription

// Native app/helper builds use Boardcast.xcodeproj. SwiftPM shares the release policy.
let package = Package(
    name: "BoardcastMac",
    defaultLocalization: "en",
    platforms: [.macOS(.v13)],
    products: [.executable(name: "Boardcast", targets: ["Boardcast"])],
    targets: [
        .executableTarget(
            name: "Boardcast",
            path: "Sources",
            exclude: ["BoardcastUpdater"],
            sources: ["Boardcast", "Shared"],
            resources: [
                .copy("Boardcast/Resources/Icons"),
                .copy("Boardcast/Resources/ThirdParty"),
                .process("Boardcast/Resources/en.lproj"),
                .process("Boardcast/Resources/zh-Hans.lproj")
            ]
        ),
        .testTarget(name: "BoardcastTests", dependencies: ["Boardcast"])
    ],
    swiftLanguageVersions: [.v5]
)
