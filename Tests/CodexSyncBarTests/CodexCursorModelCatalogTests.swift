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
    func testPreservesBundledModelsAndBuildsCollapsedNativePickerEntries() throws {
        let cursor = CursorModelCatalog(cliOutput: """
        gpt-5.6-sol-low - GPT-5.6 Sol 1M Low
        gpt-5.6-sol-low-fast - GPT-5.6 Sol Low Fast
        gpt-5.6-sol-medium - GPT-5.6 Sol 1M
        gpt-5.6-sol-medium-fast - GPT-5.6 Sol Fast
        gpt-5.6-sol-high-fast - GPT-5.6 Sol High Fast
        gpt-5.3-codex-low - Codex 5.3 Low
        composer-2.5 - Composer 2.5
        cursor-grok-4.6-high - Cursor Grok 4.6
        cursor-grok-4.6-high-fast - Cursor Grok 4.6 Fast
        """)
        let template = try JSONSerialization.data(withJSONObject: [
            "client_version": "preserved",
            "models": [
                [
                    "slug": "gpt-5.6-sol",
                    "display_name": "GPT-5.6-Sol",
                    "description": "bundled sol",
                    "default_reasoning_level": "low",
                    "supported_reasoning_levels": [],
                    "shell_type": "shell_command",
                    "visibility": "list",
                    "supported_in_api": true,
                    "priority": 1,
                    "base_instructions": "template instructions",
                    "context_window": 272_000,
                    "max_context_window": 272_000,
                ],
                [
                    "slug": "gpt-5.2",
                    "display_name": "GPT-5.2",
                    "description": "bundled 5.2",
                    "priority": 2,
                ],
            ],
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

        XCTAssertEqual(root["client_version"] as? String, "preserved")
        XCTAssertEqual(models.map { $0["slug"] as? String }, [
            "gpt-5.6-sol",
            "gpt-5.2",
            "syncbar-cursor/composer-2.5",
            "syncbar-cursor/cursor-grok-4.6",
            "syncbar-cursor/gpt-5.6-sol",
            "syncbar-cursor/gpt-5.3-codex",
        ])
        XCTAssertEqual(models[0]["description"] as? String, "bundled sol")
        XCTAssertEqual(models[1]["description"] as? String, "bundled 5.2")

        let grok = models[3]
        XCTAssertEqual(grok["display_name"] as? String, "Cursor · Grok 4.6")
        XCTAssertEqual(grok["additional_speed_tiers"] as? [String], ["fast"])
        let grokTiers = try XCTUnwrap(grok["service_tiers"] as? [[String: String]])
        XCTAssertEqual(grokTiers.first?["id"], "priority")

        let gpt = models[4]
        XCTAssertEqual(gpt["display_name"] as? String,
                       "Cursor · GPT · GPT-5.6 Sol")
        XCTAssertEqual(gpt["default_reasoning_level"] as? String, "medium")
        let levels = try XCTUnwrap(gpt["supported_reasoning_levels"] as? [[String: String]])
        XCTAssertEqual(levels.compactMap { $0["effort"] }, ["low", "medium"])
        XCTAssertEqual(gpt["base_instructions"] as? String, "template instructions")
        XCTAssertEqual(gpt["input_modalities"] as? [String], ["text", "image"])
        XCTAssertEqual(gpt["supports_image_detail_original"] as? Bool, true)

        XCTAssertEqual(models[5]["display_name"] as? String,
                       "Cursor · Codex · Codex 5.3")
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
        let generated = try XCTUnwrap(models.last)
        XCTAssertEqual(generated["default_reasoning_level"] as? String, "medium")
        XCTAssertEqual((generated["supported_reasoning_levels"] as? [Any])?.count, 0)
    }

    func testCollapsedModelsUseConservativeContextAcrossEveryNativeControlVariant() throws {
        let cursor = CursorModelCatalog(cliOutput: """
        gpt-5.6-sol-high - GPT-5.6 Sol 1M High
        gpt-5.6-sol-high-fast - GPT-5.6 Sol High Fast
        claude-opus-5-high - Opus 5 1M
        claude-opus-5-max - Opus 5 1M Max
        """)
        let template = try JSONSerialization.data(withJSONObject: [
            "models": [[
                "slug": "gpt-5.6-sol",
                "context_window": 272_000,
                "max_context_window": 272_000,
                "effective_context_window_percent": 95,
            ]],
        ])

        let data = try CodexCursorModelCatalogBuilder.build(
            cursorCatalog: cursor,
            bundledCatalogData: template)
        let root = try XCTUnwrap(JSONSerialization.jsonObject(with: data) as? [String: Any])
        let models = try XCTUnwrap(root["models"] as? [[String: Any]])
        let bySlug = Dictionary(uniqueKeysWithValues: models.compactMap { model in
            (model["slug"] as? String).map { ($0, model) }
        })

        XCTAssertEqual(bySlug["gpt-5.6-sol"]?["context_window"] as? Int, 272_000)
        XCTAssertEqual(
            bySlug["gpt-5.6-sol"]?["effective_context_window_percent"] as? Int,
            95)
        XCTAssertEqual(
            bySlug["syncbar-cursor/gpt-5.6-sol"]?["context_window"] as? Int,
            272_000)
        XCTAssertEqual(
            bySlug["syncbar-cursor/gpt-5.6-sol"]?["max_context_window"] as? Int,
            272_000)
        XCTAssertEqual(
            bySlug["syncbar-cursor/gpt-5.6-sol"]?["effective_context_window_percent"] as? Int,
            95)
        XCTAssertEqual(
            bySlug["syncbar-cursor/claude-opus-5"]?["context_window"] as? Int,
            1_000_000)
        XCTAssertEqual(
            bySlug["syncbar-cursor/claude-opus-5"]?["max_context_window"] as? Int,
            1_000_000)
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

    func testExtractsBundledModelSlugsAndRejectsDuplicateIDs() throws {
        let valid = try JSONSerialization.data(withJSONObject: [
            "models": [
                ["slug": "gpt-5.6-sol"],
                ["slug": "gpt-5.2"],
            ],
        ])
        XCTAssertEqual(
            try CodexCursorModelCatalogBuilder.bundledModelSlugs(from: valid),
            ["gpt-5.6-sol", "gpt-5.2"])

        let duplicate = try JSONSerialization.data(withJSONObject: [
            "models": [
                ["slug": "gpt-5.2"],
                ["slug": "gpt-5.2"],
            ],
        ])
        XCTAssertThrowsError(
            try CodexCursorModelCatalogBuilder.bundledModelSlugs(from: duplicate))
    }

    func testRejectsSyntheticIDsThatCollideOrExceedCodexSlugLimit() throws {
        let collisionID = CursorModelCatalog.codexModelID(
            baseSlug: "gpt-5.2",
            thinking: false)
        let collisionTemplate = try JSONSerialization.data(withJSONObject: [
            "models": [
                ["slug": "gpt-5.6-sol"],
                ["slug": collisionID],
            ],
        ])
        XCTAssertThrowsError(try CodexCursorModelCatalogBuilder.build(
            cursorCatalog: CursorModelCatalog(cliOutput: "gpt-5.2 - GPT-5.2"),
            bundledCatalogData: collisionTemplate))

        let longBase = "m" + String(repeating: "x", count: 113)
        let longCatalog = CursorModelCatalog(cliOutput: "\(longBase) - Long Model")
        let template = try JSONSerialization.data(withJSONObject: [
            "models": [["slug": "gpt-5.6-sol"]],
        ])
        XCTAssertFalse(CursorModelCatalog.isValidCodexModelID(
            CursorModelCatalog.codexModelID(baseSlug: longBase, thinking: false)))
        XCTAssertThrowsError(try CodexCursorModelCatalogBuilder.build(
            cursorCatalog: longCatalog,
            bundledCatalogData: template))
        XCTAssertThrowsError(try longCatalog.cursorRouteJSON())
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
    func testServicePrefersSafeCodexModelCacheOverBundledProbe() async throws {
        let fileManager = FileManager.default
        let home = fileManager.temporaryDirectory
            .appendingPathComponent("codex-model-cache-\(UUID().uuidString)", isDirectory: true)
        let codexHome = home.appendingPathComponent(".codex", isDirectory: true)
        try fileManager.createDirectory(
            at: codexHome,
            withIntermediateDirectories: true,
            attributes: [.posixPermissions: 0o700])
        defer { try? fileManager.removeItem(at: home) }

        let cacheURL = codexHome.appendingPathComponent("models_cache.json")
        let cache = try JSONSerialization.data(withJSONObject: [
            "client_version": "cached",
            "models": [[
                "slug": "cached-codex-model",
                "display_name": "Cached Codex Model",
                "priority": 7,
                "context_window": 272_000,
                "max_context_window": 272_000,
            ]],
        ])
        try cache.write(to: cacheURL)
        try fileManager.setAttributes(
            [.posixPermissions: 0o644],
            ofItemAtPath: cacheURL.path)

        let service = CodexCursorModelCatalogService(home: home)
        _ = try await service.install(cursorCatalog: CursorModelCatalog(
            cliOutput: "composer-2.5 - Composer 2.5"))

        let output = try Data(contentsOf: service.catalogURL)
        let root = try XCTUnwrap(
            JSONSerialization.jsonObject(with: output) as? [String: Any])
        let models = try XCTUnwrap(root["models"] as? [[String: Any]])
        XCTAssertEqual(root["client_version"] as? String, "cached")
        XCTAssertEqual(models.first?["slug"] as? String, "cached-codex-model")
        XCTAssertEqual(models.last?["slug"] as? String, "syncbar-cursor/composer-2.5")
    }

    @MainActor
    func testServiceRejectsUnsafeOrInvalidExistingCodexModelCache() async throws {
        let fileManager = FileManager.default
        let home = fileManager.temporaryDirectory
            .appendingPathComponent("unsafe-codex-model-cache-\(UUID().uuidString)", isDirectory: true)
        let codexHome = home.appendingPathComponent(".codex", isDirectory: true)
        try fileManager.createDirectory(
            at: codexHome,
            withIntermediateDirectories: true,
            attributes: [.posixPermissions: 0o700])
        defer { try? fileManager.removeItem(at: home) }

        let cacheURL = codexHome.appendingPathComponent("models_cache.json")
        try Data("{}".utf8).write(to: cacheURL)
        try fileManager.setAttributes(
            [.posixPermissions: 0o666],
            ofItemAtPath: cacheURL.path)
        let service = CodexCursorModelCatalogService(home: home)
        let cursor = CursorModelCatalog(cliOutput: "composer-2.5 - Composer 2.5")
        do {
            _ = try await service.install(cursorCatalog: cursor)
            XCTFail("Unsafe model cache should fail closed")
        } catch {}

        try fileManager.setAttributes(
            [.posixPermissions: 0o600],
            ofItemAtPath: cacheURL.path)
        do {
            _ = try await service.install(cursorCatalog: cursor)
            XCTFail("Invalid model cache should fail closed")
        } catch {}
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
        gpt-5.6-sol-medium - GPT-5.6 Sol 1M
        gpt-5.6-sol-medium-fast - GPT-5.6 Sol Fast
        gpt-5.6-sol-high - GPT-5.6 Sol 1M High
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
        let byID = Dictionary(uniqueKeysWithValues: models.compactMap { model in
            (model["id"] as? String).map { ($0, model) }
        })
        XCTAssertNotNil(byID["gpt-5.6-sol"])
        XCTAssertNotNil(byID["gpt-5.2"])
        XCTAssertNotNil(byID["syncbar-cursor/composer-2.5"])
        XCTAssertEqual(
            byID["syncbar-cursor/gpt-5.6-sol"]?["displayName"] as? String,
            "Cursor · GPT · GPT-5.6 Sol")
        XCTAssertEqual(
            byID["syncbar-cursor/gpt-5.3-codex"]?["displayName"] as? String,
            "Cursor · Codex · Codex 5.3")
        XCTAssertEqual(
            byID["syncbar-cursor/gpt-5.6-sol"]?["defaultReasoningEffort"] as? String,
            "medium")
        let reasoning = try XCTUnwrap(
            byID["syncbar-cursor/gpt-5.6-sol"]?["supportedReasoningEfforts"]
                as? [[String: Any]])
        XCTAssertEqual(reasoning.count, 2)
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
