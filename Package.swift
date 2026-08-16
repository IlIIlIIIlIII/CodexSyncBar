// swift-tools-version: 6.0
import PackageDescription

let package = Package(
    name: "CodexSyncBar",
    platforms: [
        .macOS(.v13),
    ],
    products: [
        .executable(name: "CodexSyncBar", targets: ["CodexSyncBar"]),
        .library(name: "CursorFileExtractorCore", targets: ["CursorFileExtractorCore"]),
        .executable(name: "cursor-file-extractor", targets: ["CursorFileExtractor"]),
    ],
    targets: [
        .executableTarget(
            name: "CodexSyncBar",
            path: "Sources/CodexSyncBar"),
        .target(
            name: "CursorFileExtractorCore",
            path: "Sources/CursorFileExtractorCore"),
        .executableTarget(
            name: "CursorFileExtractor",
            dependencies: ["CursorFileExtractorCore"],
            path: "Sources/CursorFileExtractor"),
        .testTarget(
            name: "CodexSyncBarTests",
            dependencies: ["CodexSyncBar"],
            path: "Tests/CodexSyncBarTests"),
        .testTarget(
            name: "CursorFileExtractorCoreTests",
            dependencies: ["CursorFileExtractorCore"],
            path: "Tests/CursorFileExtractorCoreTests"),
    ],
    swiftLanguageModes: [.v5]
)
