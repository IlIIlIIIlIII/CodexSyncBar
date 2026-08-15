import XCTest
@testable import CodexSyncBar

private let testCursorBridgeToken = String(repeating: "a", count: 64)

final class CursorBridgeTests: XCTestCase {
    @MainActor
    func testCursorBridgeServiceLoadsAccountModelCatalogFromCursorCLI() async throws {
        let fixture = URL(fileURLWithPath: #filePath)
            .deletingLastPathComponent()
            .deletingLastPathComponent()
            .appendingPathComponent("Fixtures/fake-cursor-agent.mjs")
        let home = FileManager.default.temporaryDirectory
            .appendingPathComponent("CursorModelsService-\(UUID().uuidString)", isDirectory: true)
        defer { try? FileManager.default.removeItem(at: home) }
        try FileManager.default.createDirectory(at: home, withIntermediateDirectories: true)
        let service = CursorBridgeService(home: home)

        let catalog = try await service.loadModelCatalog(preferredAgentPath: fixture.path)

        XCTAssertEqual(catalog.variants.count, 4)
        XCTAssertEqual(catalog.family(baseSlug: "gpt-5.6-sol")?.group, .openAIGPT)
        XCTAssertEqual(catalog.family(baseSlug: "gpt-5.3-codex")?.group, .openAICodex)
    }

    func testCursorBridgePreferencesValidatePortModelAndAbsoluteAgentPath() throws {
        XCTAssertEqual(try CursorBridgePreferences().validated().port, 32_125)
        XCTAssertEqual(
            try CursorBridgePreferences(
                port: 41_000,
                model: "  composer-2.5  ",
                agentPath: "/opt/homebrew/bin/agent").validated().model,
            "composer-2.5")
        XCTAssertThrowsError(try CursorBridgePreferences(port: 80).validated())
        XCTAssertThrowsError(try CursorBridgePreferences(model: "bad model").validated())
        XCTAssertThrowsError(try CursorBridgePreferences(agentPath: "agent").validated())
        XCTAssertThrowsError(try CursorBridgePreferences(bridgeToken: "short").validated())
    }

    func testCodexCursorConfigRoundTripsExactlyAndPreservesCRLF() throws {
        let original = [
            "# keep this comment",
            "model = \"gpt-5.6-sol\" # restore exactly",
            "model_provider = \"openai\"",
            "model_catalog_json = \"/previous/catalog.json\" # restore exactly",
            "personality = \"pragmatic\"",
            "",
            "[projects.\"/tmp/example\"]",
            "trust_level = \"trusted\"",
            "",
        ].joined(separator: "\r\n")
        let patch = try CodexCursorConfigEditor.activate(
            original,
            model: "composer-2.5",
            port: 32_125,
            modelCatalogPath: "/managed/cursor-catalog.json")
        let defaultToken = String(repeating: "0", count: 64)

        XCTAssertTrue(try CodexCursorConfigEditor.isActive(patch.text))
        XCTAssertTrue(patch.text.contains("base_url = \"http://127.0.0.1:32125/v1\"\r\n"))
        XCTAssertTrue(patch.text.contains(
            "http_headers = { \"X-SyncBar-Bridge-Token\" = \"\(defaultToken)\" }"))
        XCTAssertTrue(patch.text.contains(
            "model_catalog_json = \"/managed/cursor-catalog.json\"\r\n"))
        XCTAssertEqual(
            patch.state.previousCatalogAssignment,
            "model_catalog_json = \"/previous/catalog.json\" # restore exactly")
        XCTAssertTrue(patch.text.contains("personality = \"pragmatic\"\r\n"))
        XCTAssertEqual(
            try CodexCursorConfigEditor.deactivate(patch.text, state: patch.state),
            original)
    }

    func testCodexCursorConfigAddsAndRemovesMissingTopLevelAssignments() throws {
        let original = "[notice]\nhide_prompt = true\n"
        let patch = try CodexCursorConfigEditor.activate(original, model: "auto", port: 32_126)

        XCTAssertTrue(patch.text.hasPrefix([
            "model = \"auto\"",
            "model_provider = \"syncbar_cursor_bridge\"",
            "model_catalog_json = \"/tmp/codex-syncbar-cursor-model-catalog.json\"",
            "",
        ].joined(separator: "\n")))
        XCTAssertEqual(
            try CodexCursorConfigEditor.deactivate(patch.text, state: patch.state),
            original)
    }

    func testCodexCursorConfigUpdatesActiveModelWithoutLosingOriginalSelection() throws {
        let original = "model = \"gpt-5.6-sol\"\nmodel_provider = \"openai\"\n"
        let first = try CodexCursorConfigEditor.activate(
            original,
            model: "composer-2.5",
            port: 32_125)
        let second = try CodexCursorConfigEditor.activate(
            first.text,
            model: "claude-4-sonnet",
            port: 32_130,
            existingState: first.state)

        XCTAssertTrue(second.text.contains("model = \"claude-4-sonnet\""))
        XCTAssertTrue(second.text.contains("127.0.0.1:32130/v1"))
        XCTAssertEqual(
            try CodexCursorConfigEditor.deactivate(second.text, state: second.state),
            original)
    }

    func testCodexCursorConfigFailsClosedOnCollisionDuplicateAndExternalDrift() throws {
        XCTAssertThrowsError(try CodexCursorConfigEditor.activate(
            "model = \"one\"\nmodel = \"two\"\n",
            model: "auto",
            port: 32_125))
        XCTAssertThrowsError(try CodexCursorConfigEditor.activate(
            "model_catalog_json = \"/one.json\"\nmodel_catalog_json = \"/two.json\"\n",
            model: "auto",
            port: 32_125))
        XCTAssertThrowsError(try CodexCursorConfigEditor.activate(
            "\"model_catalog_json\" = \"/one.json\"\n",
            model: "auto",
            port: 32_125))
        XCTAssertThrowsError(try CodexCursorConfigEditor.activate(
            "[model_providers.syncbar_cursor_bridge]\nname = \"manual\"\n",
            model: "auto",
            port: 32_125))
        XCTAssertThrowsError(try CodexCursorConfigEditor.activate(
            "[model_providers.\"syncbar_cursor_bridge\"]\nname = \"manual\"\n",
            model: "auto",
            port: 32_125))
        XCTAssertThrowsError(try CodexCursorConfigEditor.activate(
            "\"model\" = \"gpt-5.6-sol\"\n",
            model: "auto",
            port: 32_125))
        XCTAssertThrowsError(try CodexCursorConfigEditor.activate(
            "notes = \"\"\"\n[looks-like-a-table]\n\"\"\"\nmodel = \"gpt-5.6-sol\"\n",
            model: "auto",
            port: 32_125))

        let original = "model = \"gpt-5.6-sol\"\nmodel_provider = \"openai\"\n"
        let patch = try CodexCursorConfigEditor.activate(original, model: "auto", port: 32_125)
        let externallyChanged = patch.text.replacingOccurrences(
            of: "model = \"auto\"",
            with: "model = \"composer-2.5\"")
        XCTAssertThrowsError(try CodexCursorConfigEditor.deactivate(
            externallyChanged,
            state: patch.state))
        let catalogChanged = patch.text.replacingOccurrences(
            of: "model_catalog_json = \"/tmp/codex-syncbar-cursor-model-catalog.json\"",
            with: "model_catalog_json = \"/someone-elses/catalog.json\"")
        XCTAssertThrowsError(try CodexCursorConfigEditor.deactivate(
            catalogChanged,
            state: patch.state))
    }

    func testCodexConfigServiceAtomicallyActivatesAndRestoresConfiguration() throws {
        let home = FileManager.default.temporaryDirectory
            .appendingPathComponent("CodexCursorConfig-\(UUID().uuidString)", isDirectory: true)
        defer { try? FileManager.default.removeItem(at: home) }
        let codex = home.appendingPathComponent(".codex", isDirectory: true)
        try FileManager.default.createDirectory(at: codex, withIntermediateDirectories: true)
        let config = codex.appendingPathComponent("config.toml")
        let original = "model = \"gpt-5.6-sol\"\n# preserved\n[notice]\nhide = true\n"
        try Data(original.utf8).write(to: config)
        try FileManager.default.setAttributes([.posixPermissions: 0o600], ofItemAtPath: config.path)
        let service = CodexConfigService(home: home)
        let catalogPath = home.appendingPathComponent("managed-cursor-catalog.json").path

        try service.activate(
            model: "composer-2.5",
            port: 32_125,
            bridgeToken: testCursorBridgeToken,
            modelCatalogPath: catalogPath)

        XCTAssertTrue(try service.isActive())
        let active = try String(contentsOf: config, encoding: .utf8)
        XCTAssertTrue(active.contains("# preserved\n[notice]"))
        XCTAssertTrue(active.contains("model_catalog_json = \"\(catalogPath)\""))
        XCTAssertEqual(
            try service.activeCursorProviderConfiguration()?.modelCatalogPath,
            catalogPath)
        let activationMode = try FileManager.default.attributesOfItem(
            atPath: service.activationURL.path)[.posixPermissions] as? NSNumber
        XCTAssertEqual(activationMode?.intValue, 0o600)

        try service.deactivate()

        XCTAssertFalse(try service.isActive())
        XCTAssertEqual(try String(contentsOf: config, encoding: .utf8), original)
        XCTAssertFalse(FileManager.default.fileExists(atPath: service.activationURL.path))
    }

    func testCodexConfigServiceRestoresAnOriginallyMissingConfiguration() throws {
        let home = FileManager.default.temporaryDirectory
            .appendingPathComponent("CodexCursorMissing-\(UUID().uuidString)", isDirectory: true)
        defer { try? FileManager.default.removeItem(at: home) }
        try FileManager.default.createDirectory(at: home, withIntermediateDirectories: true)
        let service = CodexConfigService(home: home)

        try service.activate(model: "auto", port: 32_125, bridgeToken: testCursorBridgeToken)
        XCTAssertTrue(FileManager.default.fileExists(atPath: service.configurationURL.path))

        try service.deactivate()
        XCTAssertFalse(FileManager.default.fileExists(atPath: service.configurationURL.path))
        XCTAssertFalse(FileManager.default.fileExists(atPath: service.activationURL.path))
    }

    func testCodexConfigServicePreservesSettingsAddedAfterCreatingMissingConfiguration() throws {
        let home = FileManager.default.temporaryDirectory
            .appendingPathComponent("CodexCursorMissingEdited-\(UUID().uuidString)", isDirectory: true)
        defer { try? FileManager.default.removeItem(at: home) }
        try FileManager.default.createDirectory(at: home, withIntermediateDirectories: true)
        let service = CodexConfigService(home: home)

        try service.activate(model: "auto", port: 32_125, bridgeToken: testCursorBridgeToken)
        var active = try String(contentsOf: service.configurationURL, encoding: .utf8)
        active = active.replacingOccurrences(
            of: "# BEGIN CODEX SYNCBAR CURSOR BRIDGE v1",
            with: "custom_setting = \"keep\"\n\n# BEGIN CODEX SYNCBAR CURSOR BRIDGE v1")
        try Data(active.utf8).write(to: service.configurationURL)
        try FileManager.default.setAttributes(
            [.posixPermissions: 0o600],
            ofItemAtPath: service.configurationURL.path)

        try service.deactivate()

        let restored = try String(contentsOf: service.configurationURL, encoding: .utf8)
        XCTAssertEqual(restored.trimmingCharacters(in: .whitespacesAndNewlines),
                       "custom_setting = \"keep\"")
        XCTAssertFalse(restored.contains("syncbar_cursor_bridge"))
        XCTAssertFalse(FileManager.default.fileExists(atPath: service.activationURL.path))
    }

    func testCodexConfigServiceUsesExplicitCodexHome() throws {
        let home = FileManager.default.temporaryDirectory
            .appendingPathComponent("CodexCursorCustomHome-\(UUID().uuidString)", isDirectory: true)
        defer { try? FileManager.default.removeItem(at: home) }
        let custom = home.appendingPathComponent("custom-codex", isDirectory: true)
        try FileManager.default.createDirectory(at: home, withIntermediateDirectories: true)
        let service = CodexConfigService(home: home, codexDirectory: custom)

        try service.activate(model: "auto", port: 32_125, bridgeToken: testCursorBridgeToken)

        XCTAssertEqual(service.configurationURL, custom.appendingPathComponent("config.toml"))
        XCTAssertTrue(FileManager.default.fileExists(atPath: service.configurationURL.path))
        XCTAssertFalse(FileManager.default.fileExists(
            atPath: home.appendingPathComponent(".codex/config.toml").path))
        try service.deactivate()
    }

    func testCodexConfigServiceRecoversBothSidesOfActivationTransaction() throws {
        let home = FileManager.default.temporaryDirectory
            .appendingPathComponent("CodexCursorRecovery-\(UUID().uuidString)", isDirectory: true)
        defer { try? FileManager.default.removeItem(at: home) }
        let codex = home.appendingPathComponent(".codex", isDirectory: true)
        let stateRoot = home.appendingPathComponent(".local/share/gpt-switch", isDirectory: true)
        try FileManager.default.createDirectory(at: codex, withIntermediateDirectories: true)
        try FileManager.default.createDirectory(at: stateRoot, withIntermediateDirectories: true)
        let service = CodexConfigService(home: home)
        let original = Data("model = \"gpt-5.6-sol\"\n".utf8)
        try original.write(to: service.configurationURL)
        try FileManager.default.setAttributes(
            [.posixPermissions: 0o600],
            ofItemAtPath: service.configurationURL.path)
        let patch = try CodexCursorConfigEditor.activate(
            String(decoding: original, as: UTF8.self),
            model: "auto",
            port: 32_125)
        let candidate = Data(patch.text.utf8)
        var stateData = try JSONEncoder().encode(patch.state)
        stateData.append(0x0A)
        let transaction = CodexConfigTransaction(
            expectedConfigurationExisted: true,
            expectedConfiguration: original,
            candidateConfigurationExisted: true,
            candidateConfiguration: candidate,
            previousActivationStateData: nil,
            candidateActivationStateData: stateData)
        var transactionData = try JSONEncoder().encode(transaction)
        transactionData.append(0x0A)

        // Crash before config installation: recovery keeps the original and
        // removes a prematurely installed activation state.
        try stateData.write(to: service.activationURL)
        try transactionData.write(to: service.transactionURL)
        for url in [service.activationURL, service.transactionURL] {
            try FileManager.default.setAttributes([.posixPermissions: 0o600], ofItemAtPath: url.path)
        }
        XCTAssertFalse(try service.isActive())
        XCTAssertEqual(try Data(contentsOf: service.configurationURL), original)
        XCTAssertFalse(FileManager.default.fileExists(atPath: service.activationURL.path))

        // Crash after config installation: recovery commits the matching
        // activation state, making exact restore possible.
        try candidate.write(to: service.configurationURL)
        try transactionData.write(to: service.transactionURL)
        try FileManager.default.setAttributes(
            [.posixPermissions: 0o600],
            ofItemAtPath: service.transactionURL.path)
        XCTAssertTrue(try service.isActive())
        XCTAssertTrue(FileManager.default.fileExists(atPath: service.activationURL.path))
        XCTAssertFalse(FileManager.default.fileExists(atPath: service.transactionURL.path))
        try service.deactivate()
        XCTAssertEqual(try Data(contentsOf: service.configurationURL), original)
    }

    func testCodexConfigServiceRejectsSymlinkedConfiguration() throws {
        let home = FileManager.default.temporaryDirectory
            .appendingPathComponent("CodexCursorSymlink-\(UUID().uuidString)", isDirectory: true)
        defer { try? FileManager.default.removeItem(at: home) }
        let codex = home.appendingPathComponent(".codex", isDirectory: true)
        try FileManager.default.createDirectory(at: codex, withIntermediateDirectories: true)
        let target = home.appendingPathComponent("target.toml")
        try Data("model = \"safe\"\n".utf8).write(to: target)
        try FileManager.default.createSymbolicLink(
            at: codex.appendingPathComponent("config.toml"),
            withDestinationURL: target)

        XCTAssertThrowsError(try CodexConfigService(home: home).activate(
            model: "auto",
            port: 32_125,
            bridgeToken: testCursorBridgeToken))
        XCTAssertEqual(try String(contentsOf: target, encoding: .utf8), "model = \"safe\"\n")
    }

    func testCursorBridgePreferencesStoreUsesPrivateAtomicFile() throws {
        let home = FileManager.default.temporaryDirectory
            .appendingPathComponent("CursorBridgePreferences-\(UUID().uuidString)", isDirectory: true)
        defer { try? FileManager.default.removeItem(at: home) }
        let store = CursorBridgePreferencesStore(home: home)
        let expected = CursorBridgePreferences(
            port: 41_001,
            model: "composer-2.5",
            agentPath: "/opt/homebrew/bin/agent")

        try store.save(expected)

        XCTAssertEqual(try store.load(), expected)
        let mode = try FileManager.default.attributesOfItem(
            atPath: store.preferencesURL.path)[.posixPermissions] as? NSNumber
        XCTAssertEqual(mode?.intValue, 0o600)
    }
}
