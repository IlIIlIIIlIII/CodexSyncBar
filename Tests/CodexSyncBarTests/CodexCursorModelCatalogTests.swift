import AppKit
import XCTest
@testable import CodexSyncBar

private final class CodexModelListProbeState: @unchecked Sendable {
    private let lock = NSLock()
    private var bufferedOutput = Data()
    private var sentModelList = false
    private var modelListResponse: [String: Any]?

    func append(_ chunk: Data) -> [Data] {
        lock.lock()
        defer { lock.unlock() }
        bufferedOutput.append(chunk)
        var lines: [Data] = []
        while let newline = bufferedOutput.firstIndex(of: 0x0A) {
            lines.append(Data(bufferedOutput[..<newline]))
            bufferedOutput.removeSubrange(...newline)
        }
        return lines
    }

    func markModelListSent() -> Bool {
        lock.lock()
        defer { lock.unlock() }
        guard !sentModelList else { return false }
        sentModelList = true
        return true
    }

    func storeModelListResponse(_ response: [String: Any]) -> Bool {
        lock.lock()
        defer { lock.unlock() }
        guard modelListResponse == nil else { return false }
        modelListResponse = response
        return true
    }

    func response() -> [String: Any]? {
        lock.lock()
        defer { lock.unlock() }
        return modelListResponse
    }
}

final class CodexCursorModelCatalogTests: XCTestCase {
    func testBuildsPrefixedPickerEntriesAndKeepsGPTSeparateFromCodex() throws {
        let cursor = CursorModelCatalog(cliOutput: """
        gpt-5.6-sol-high-fast - GPT-5.6 Sol High Fast
        gpt-5.3-codex-low - Codex 5.3 Low
        composer-2.5 - Composer 2.5
        """)
        let template = try JSONSerialization.data(withJSONObject: [
            "models": [[
                "slug": "gpt-5.6-sol",
                "display_name": "GPT-5.6-Sol",
                "description": "template",
                "default_reasoning_level": "low",
                "supported_reasoning_levels": [],
                "shell_type": "shell_command",
                "visibility": "list",
                "supported_in_api": true,
                "priority": 1,
                "base_instructions": "template instructions",
                "context_window": 100_000,
            ]],
        ])

        let data = try CodexCursorModelCatalogBuilder.build(
            cursorCatalog: cursor,
            bundledCatalogData: template)
        XCTAssertEqual(
            data,
            try CodexCursorModelCatalogBuilder.build(
                cursorCatalog: cursor,
                bundledCatalogData: template))
        let root = try XCTUnwrap(
            JSONSerialization.jsonObject(with: data) as? [String: Any])
        let models = try XCTUnwrap(root["models"] as? [[String: Any]])

        XCTAssertEqual(models.map { $0["slug"] as? String }, [
            "composer-2.5",
            "gpt-5.6-sol-high-fast",
            "gpt-5.3-codex-low",
        ])
        XCTAssertEqual(models[1]["display_name"] as? String,
                       "Cursor · GPT · GPT-5.6 Sol High Fast")
        XCTAssertEqual(models[2]["display_name"] as? String,
                       "Cursor · Codex · Codex 5.3 Low")
        XCTAssertEqual(models[1]["default_reasoning_level"] as? String, "high")
        let levels = try XCTUnwrap(models[1]["supported_reasoning_levels"] as? [[String: String]])
        XCTAssertEqual(levels.first?["effort"], "high")
        XCTAssertEqual(models[1]["base_instructions"] as? String, "template instructions")
        XCTAssertEqual(models[1]["input_modalities"] as? [String], ["text"])
    }

    func testDefaultCursorVariantDoesNotAdvertiseChangeableReasoning() throws {
        let cursor = CursorModelCatalog(cliOutput: "composer-2.5 - Composer 2.5")
        let template = try JSONSerialization.data(withJSONObject: [
            "models": [["slug": "template", "default_reasoning_level": "low"]],
        ])

        let data = try CodexCursorModelCatalogBuilder.build(
            cursorCatalog: cursor,
            bundledCatalogData: template)
        let root = try XCTUnwrap(JSONSerialization.jsonObject(with: data) as? [String: Any])
        let models = try XCTUnwrap(root["models"] as? [[String: Any]])
        XCTAssertEqual(models[0]["default_reasoning_level"] as? String, "medium")
        XCTAssertEqual((models[0]["supported_reasoning_levels"] as? [Any])?.count, 0)
    }

    func testRejectsAnUnboundedCursorModelCatalog() throws {
        let lines = (0 ... CodexCursorModelCatalogBuilder.maximumModelCount)
            .map { "model-\($0) - Model \($0)" }
            .joined(separator: "\n")
        let cursor = CursorModelCatalog(cliOutput: lines)
        let template = try JSONSerialization.data(withJSONObject: [
            "models": [["slug": "template"]],
        ])

        XCTAssertThrowsError(try CodexCursorModelCatalogBuilder.build(
            cursorCatalog: cursor,
            bundledCatalogData: template))
    }

    @MainActor
    func testServiceAtomicallyWritesPrivateCatalogAndCanRestoreIt() async throws {
        let fileManager = FileManager.default
        let home = fileManager.temporaryDirectory
            .appendingPathComponent("codex-cursor-catalog-\(UUID().uuidString)", isDirectory: true)
        try fileManager.createDirectory(at: home, withIntermediateDirectories: false)
        defer { try? fileManager.removeItem(at: home) }

        let template = try JSONSerialization.data(withJSONObject: [
            "models": [[
                "slug": "gpt-5.6-sol",
                "display_name": "GPT-5.6-Sol",
                "default_reasoning_level": "low",
                "supported_reasoning_levels": [],
            ]],
        ])
        let service = CodexCursorModelCatalogService(
            home: home,
            bundledCatalogOverride: template)

        let firstCatalog = CursorModelCatalog(
            cliOutput: "composer-2.5 - Composer 2.5")
        let initialPrevious = try await service.install(cursorCatalog: firstCatalog)
        XCTAssertNil(initialPrevious)
        let firstData = try Data(contentsOf: service.catalogURL)
        let attributes = try fileManager.attributesOfItem(atPath: service.catalogURL.path)
        let permissions = try XCTUnwrap(attributes[.posixPermissions] as? NSNumber)
        XCTAssertEqual(permissions.intValue & 0o777, 0o600)

        let secondCatalog = CursorModelCatalog(
            cliOutput: "gpt-5.3-codex-low - Codex 5.3 Low")
        let previous = try await service.install(cursorCatalog: secondCatalog)
        XCTAssertEqual(previous, firstData)
        XCTAssertNotEqual(try Data(contentsOf: service.catalogURL), firstData)

        try service.restore(previous)
        XCTAssertEqual(try Data(contentsOf: service.catalogURL), firstData)
        try service.restore(nil)
        XCTAssertFalse(fileManager.fileExists(atPath: service.catalogURL.path))
    }

    @MainActor
    func testDesktopCodexModelListAcceptsGeneratedCatalogWhenInstalled() async throws {
        let application = NSWorkspace.shared.urlForApplication(
            withBundleIdentifier: "com.openai.codex")
            ?? URL(fileURLWithPath: "/Applications/ChatGPT.app")
        let codex = application.appendingPathComponent("Contents/Resources/codex")
            .resolvingSymlinksInPath()
        guard FileManager.default.isExecutableFile(atPath: codex.path) else {
            throw XCTSkip("Desktop Codex executable is not installed")
        }

        let fileManager = FileManager.default
        let home = fileManager.temporaryDirectory
            .appendingPathComponent("codex-model-list-\(UUID().uuidString)", isDirectory: true)
        try fileManager.createDirectory(at: home, withIntermediateDirectories: false)
        defer { try? fileManager.removeItem(at: home) }

        let cursor = CursorModelCatalog(cliOutput: """
        gpt-5.6-sol-high-fast - GPT-5.6 Sol High Fast
        gpt-5.3-codex-low - Codex 5.3 Low
        composer-2.5 - Composer 2.5
        """)
        let service = CodexCursorModelCatalogService(home: home)
        let previous = try await service.install(cursorCatalog: cursor)
        XCTAssertNil(previous)

        let process = Process()
        let input = Pipe()
        let output = Pipe()
        process.executableURL = codex
        process.arguments = [
            "app-server",
            "--stdio",
            "-c",
            "model_catalog_json=\"\(service.catalogURL.path)\"",
        ]
        var environment = ProcessInfo.processInfo.environment
        environment["CODEX_HOME"] = home.path
        environment["NO_COLOR"] = "1"
        process.environment = environment
        process.standardInput = input
        process.standardOutput = output
        process.standardError = FileHandle.nullDevice

        let modelListExpectation = expectation(description: "model/list response")
        let probeState = CodexModelListProbeState()
        let initializedMessages = try Self.jsonLines([
            ["method": "initialized", "params": [:]],
            ["id": 2, "method": "model/list", "params": [:]],
        ])

        output.fileHandleForReading.readabilityHandler = { handle in
            let chunk = handle.availableData
            guard !chunk.isEmpty else {
                handle.readabilityHandler = nil
                return
            }

            for line in probeState.append(chunk) {
                guard let json = try? JSONSerialization.jsonObject(with: line) as? [String: Any],
                      let identifier = (json["id"] as? NSNumber)?.intValue
                else { continue }
                if identifier == 1 {
                    if probeState.markModelListSent() {
                        try? input.fileHandleForWriting.write(contentsOf: initializedMessages)
                    }
                } else if identifier == 2 {
                    if probeState.storeModelListResponse(json) {
                        modelListExpectation.fulfill()
                    }
                }
            }
        }

        defer {
            output.fileHandleForReading.readabilityHandler = nil
            try? input.fileHandleForWriting.close()
            if process.isRunning { process.terminate() }
        }
        try process.run()
        try input.fileHandleForWriting.write(contentsOf: Self.jsonLines([[
            "id": 1,
            "method": "initialize",
            "params": [
                "clientInfo": [
                    "name": "codex-syncbar-tests",
                    "title": "Codex SyncBar Tests",
                    "version": "1",
                ],
                "capabilities": [:],
            ],
        ]]))
        await fulfillment(of: [modelListExpectation], timeout: 10)

        let response = probeState.response()
        let result = try XCTUnwrap(response?["result"] as? [String: Any])
        let models = try XCTUnwrap(result["data"] as? [[String: Any]])
        XCTAssertEqual(models.compactMap { $0["id"] as? String }, [
            "composer-2.5",
            "gpt-5.6-sol-high-fast",
            "gpt-5.3-codex-low",
        ])
        XCTAssertEqual(models[1]["displayName"] as? String,
                       "Cursor · GPT · GPT-5.6 Sol High Fast")
        XCTAssertEqual(models[2]["displayName"] as? String,
                       "Cursor · Codex · Codex 5.3 Low")
        XCTAssertEqual(models[1]["defaultReasoningEffort"] as? String, "high")
        let reasoning = try XCTUnwrap(
            models[1]["supportedReasoningEfforts"] as? [[String: Any]])
        XCTAssertEqual(reasoning.first?["reasoningEffort"] as? String, "high")
    }

    private static func jsonLines(_ objects: [[String: Any]]) throws -> Data {
        var data = Data()
        for object in objects {
            data.append(try JSONSerialization.data(withJSONObject: object))
            data.append(0x0A)
        }
        return data
    }
}
