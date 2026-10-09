// swift-tools-version: 5.9
import PackageDescription
let package = Package(
    name: "BoardcastMac", platforms: [.macOS(.v13)],
    products: [.executable(name: "Boardcast", targets: ["Boardcast"])],
    targets: [
        .executableTarget(name: "Boardcast"),
        .testTarget(name: "BoardcastTests", dependencies: ["Boardcast"])
    ]
)
