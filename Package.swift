// swift-tools-version: 6.0
import PackageDescription

let package = Package(
    name: "CodexSyncBar",
    platforms: [
        .macOS(.v13),
    ],
    products: [
        .executable(name: "CodexSyncBar", targets: ["CodexSyncBar"]),
    ],
    targets: [
        .executableTarget(
            name: "CodexSyncBar",
            path: "Sources/CodexSyncBar"),
        .testTarget(
            name: "CodexSyncBarTests",
            dependencies: ["CodexSyncBar"],
            path: "Tests/CodexSyncBarTests"),
    ],
    swiftLanguageModes: [.v5]
)
