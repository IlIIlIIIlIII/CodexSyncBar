import XCTest
@testable import CodexSyncBar

private let testCursorBridgeToken = String(repeating: "a", count: 64)

final class CursorBridgeTests: XCTestCase {
    func testCursorSDKAccountValidatesEmail() throws {
        XCTAssertEqual(CursorAccount(email: "user@example.com")?.email, "user@example.com")
        XCTAssertNil(CursorAccount(email: "not-an-email"))
        XCTAssertNil(CursorAccount(email: "bad user@example.com"))
        XCTAssertNil(CursorAccount(email: "bad\u{200B}@example.com"))
    }

    func testCursorBridgePreferencesValidatePortModelAndAbsoluteAgentPath() throws {
        XCTAssertEqual(try CursorBridgePreferences().validated().port, 32_125)
        XCTAssertEqual(
            try CursorBridgePreferences(
                port: 41_000,
                model: "  composer-2.5  ",
                agentPath: "/opt/homebrew/bin/agent",
                exposedModelIDs: [
                    "syncbar-cursor/gpt-5.6-sol",
                    "syncbar-cursor/auto",
                ]).validated().model,
            "composer-2.5")
        XCTAssertEqual(
            try CursorBridgePreferences(exposedModelIDs: [
                "syncbar-cursor/gpt-5.6-sol",
                "syncbar-cursor/auto",
            ]).validated().exposedModelIDs,
            ["syncbar-cursor/auto", "syncbar-cursor/gpt-5.6-sol"])
        XCTAssertThrowsError(try CursorBridgePreferences(port: 80).validated())
        XCTAssertThrowsError(try CursorBridgePreferences(model: "bad model").validated())
        XCTAssertThrowsError(try CursorBridgePreferences(agentPath: "agent").validated())
        XCTAssertThrowsError(try CursorBridgePreferences(exposedModelIDs: []).validated())
        XCTAssertThrowsError(try CursorBridgePreferences(exposedModelIDs: [
            "syncbar-cursor/auto",
            "syncbar-cursor/auto",
        ]).validated())
        XCTAssertThrowsError(try CursorBridgePreferences(exposedModelIDs: ["gpt-5.6-sol"]).validated())
        XCTAssertThrowsError(try CursorBridgePreferences(bridgeToken: "short").validated())
    }

    func testCursorBridgePreferencesDecodeLegacyFileAsExposeAll() throws {
        let legacy = """
        {
          "schemaVersion": 2,
          "port": 32125,
          "model": "auto",
          "bridgeToken": "\(testCursorBridgeToken)"
        }
        """

        let preferences = try JSONDecoder().decode(
            CursorBridgePreferences.self,
            from: Data(legacy.utf8))

        XCTAssertNil(try preferences.validated().exposedModelIDs)
    }

    @MainActor
    func testCursorBridgeSidecarEnvironmentIncludesExactACPModelParameters() throws {
        let catalog = CursorModelCatalog(cliOutput: """
        gpt-5.6-sol-high-fast - GPT-5.6 Sol 1M High Fast
        composer-2.5 - Composer 2.5
        """)

        let environment = try CursorBridgeService.sidecarEnvironment(
            inheriting: ["PRESERVED": "yes", "CURSOR_API_KEY": "must-not-leak"],
            bridgeToken: testCursorBridgeToken,
            modelCatalog: catalog,
            nativeModelSlugs: ["gpt-5.6-sol"])

        XCTAssertEqual(environment["PRESERVED"], "yes")
        XCTAssertEqual(environment["SYNCBAR_CURSOR_BACKEND"], "sdk")
        XCTAssertEqual(environment["SYNCBAR_CURSOR_SANDBOX_MODE"], "disabled")
        XCTAssertNil(environment["CURSOR_API_KEY"])
        XCTAssertEqual(environment["SYNCBAR_CURSOR_BRIDGE_TOKEN"], testCursorBridgeToken)
        let slugsData = try XCTUnwrap(
            environment["SYNCBAR_CURSOR_MODELS_JSON"]?.data(using: .utf8))
        XCTAssertEqual(
            try JSONDecoder().decode([String].self, from: slugsData),
            ["gpt-5.6-sol-high-fast", "composer-2.5"])
        let parametersData = try XCTUnwrap(
            environment["SYNCBAR_CURSOR_MODEL_PARAMETERS_JSON"]?.data(using: .utf8))
        let parameters = try JSONDecoder().decode(
            [String: CursorACPModelParameters].self,
            from: parametersData)
        XCTAssertEqual(
            parameters["gpt-5.6-sol-high-fast"],
            CursorACPModelParameters(
                model: "gpt-5.6-sol",
                context: "1m",
                effort: .high,
                fast: true,
                thinking: false))
        let nativeData = try XCTUnwrap(
            environment["SYNCBAR_NATIVE_MODELS_JSON"]?.data(using: .utf8))
        XCTAssertEqual(try JSONDecoder().decode([String].self, from: nativeData), ["gpt-5.6-sol"])
        XCTAssertNotNil(environment["SYNCBAR_CURSOR_MODEL_ROUTES_JSON"])
    }

    @MainActor
    func testCursorBridgeSidecarEnvironmentPassesValidatedSDKKeyOnlyToChild() throws {
        let catalog = CursorModelCatalog(cliOutput: "composer-2.5 - Composer 2.5")
        let apiKey = "cursor_" + String(repeating: "a", count: 32)

        let environment = try CursorBridgeService.sidecarEnvironment(
            inheriting: ["OPENAI_API_KEY": "preserved-provider-key"],
            bridgeToken: testCursorBridgeToken,
            modelCatalog: catalog,
            cursorAPIKey: apiKey)

        XCTAssertEqual(environment["CURSOR_API_KEY"], apiKey)
        XCTAssertEqual(environment["SYNCBAR_CURSOR_BACKEND"], "sdk")
        XCTAssertEqual(environment["SYNCBAR_CURSOR_SANDBOX_MODE"], "disabled")
        XCTAssertEqual(environment["OPENAI_API_KEY"], "preserved-provider-key")
        XCTAssertThrowsError(try CursorBridgeService.sidecarEnvironment(
            inheriting: [:],
            bridgeToken: testCursorBridgeToken,
            modelCatalog: catalog,
            cursorAPIKey: "invalid key"))
    }

    func testCodexCursorConfigRoundTripsExactlyAndPreservesCRLF() throws {
        let original = [
            "# keep this comment",
            "model = \"gpt-5.6-sol\" # restore exactly",
            "model_provider = \"openai\"",
            "model_catalog_json = \"/previous/catalog.json\" # restore exactly",
            "openai_base_url = \"https://example.test/original/v1\" # restore exactly",
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
        XCTAssertTrue(patch.text.contains("requires_openai_auth = true"))
        XCTAssertTrue(patch.text.contains(
            "http_headers = { \"X-SyncBar-Bridge-Token\" = \"\(defaultToken)\", originator = \"codex_cli_rs\" }"))
        XCTAssertTrue(patch.text.contains(
            "model_catalog_json = \"/managed/cursor-catalog.json\"\r\n"))
        XCTAssertTrue(patch.text.contains(
            "openai_base_url = \"http://127.0.0.1:32125/v1/\(defaultToken)\"\r\n"))
        XCTAssertTrue(patch.text.contains(
            "features = { responses_websockets = false }\r\n"))
        XCTAssertEqual(
            patch.state.previousCatalogAssignment,
            "model_catalog_json = \"/previous/catalog.json\" # restore exactly")
        XCTAssertEqual(
            patch.state.previousOpenAIBaseURLAssignment,
            "openai_base_url = \"https://example.test/original/v1\" # restore exactly")
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
            "openai_base_url = \"http://127.0.0.1:32126/v1/\(String(repeating: "0", count: 64))\"",
            "features = { responses_websockets = false }",
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
        XCTAssertTrue(second.text.contains(
            "openai_base_url = \"http://127.0.0.1:32130/v1/\(String(repeating: "0", count: 64))\""))
        XCTAssertEqual(
            try CodexCursorConfigEditor.deactivate(second.text, state: second.state),
            original)
    }

    func testCodexCursorConfigDisablesWebsocketsInExistingFeaturesTableAndRestoresIt() throws {
        let original = [
            "model = \"gpt-5.6-sol\"",
            "model_provider = \"openai\"",
            "",
            "[features]",
            "responses_websockets = true # restore exactly",
            "multi_agent = true",
            "",
        ].joined(separator: "\n")
        let patch = try CodexCursorConfigEditor.activate(
            original,
            model: "syncbar-cursor/cursor-grok-4.6",
            port: 32_125)

        XCTAssertTrue(patch.text.contains(
            "[features]\nresponses_websockets = false\nmulti_agent = true"))
        XCTAssertEqual(
            patch.state.previousResponsesWebsocketsAssignment,
            "responses_websockets = true # restore exactly")
        XCTAssertEqual(
            try CodexCursorConfigEditor.deactivate(patch.text, state: patch.state),
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
            "openai_base_url = \"https://one.test/v1\"\nopenai_base_url = \"https://two.test/v1\"\n",
            model: "auto",
            port: 32_125))
        XCTAssertThrowsError(try CodexCursorConfigEditor.activate(
            "\"openai_base_url\" = \"https://one.test/v1\"\n",
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
        let providerChanged = patch.text.replacingOccurrences(
            of: "model_provider = \"syncbar_cursor_bridge\"",
            with: "model_provider = \"openai\"")
        XCTAssertThrowsError(try CodexCursorConfigEditor.deactivate(
            providerChanged,
            state: patch.state))
        let catalogChanged = patch.text.replacingOccurrences(
            of: "model_catalog_json = \"/tmp/codex-syncbar-cursor-model-catalog.json\"",
            with: "model_catalog_json = \"/someone-elses/catalog.json\"")
        XCTAssertThrowsError(try CodexCursorConfigEditor.deactivate(
            catalogChanged,
            state: patch.state))
        let baseURLChanged = patch.text.replacingOccurrences(
            of: "openai_base_url = \"http://127.0.0.1:32125/v1/\(String(repeating: "0", count: 64))\"",
            with: "openai_base_url = \"https://someone-elses.example/v1\"")
        XCTAssertThrowsError(try CodexCursorConfigEditor.deactivate(
            baseURLChanged,
            state: patch.state))
        let managedSuffixChanged = patch.text.replacingOccurrences(
            of: "Cursor Subscription (local SyncBar bridge)",
            with: "externally changed provider")
        XCTAssertThrowsError(try CodexCursorConfigEditor.deactivate(
            managedSuffixChanged,
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
        XCTAssertTrue(active.contains("# preserved\n"))
        XCTAssertTrue(active.contains("features = { responses_websockets = false }\n[notice]"))
        XCTAssertTrue(active.contains("model_catalog_json = \"\(catalogPath)\""))
        XCTAssertEqual(
            try service.activeCursorProviderConfiguration()?.modelCatalogPath,
            catalogPath)
        XCTAssertEqual(
            try service.activeCursorProviderConfiguration()?.routesBuiltInOpenAIProvider,
            true)
        let activationMode = try FileManager.default.attributesOfItem(
            atPath: service.activationURL.path)[.posixPermissions] as? NSNumber
        XCTAssertEqual(activationMode?.intValue, 0o600)

        try service.deactivate()

        XCTAssertFalse(try service.isActive())
        XCTAssertEqual(try String(contentsOf: config, encoding: .utf8), original)
        XCTAssertFalse(FileManager.default.fileExists(atPath: service.activationURL.path))
    }

    func testCodexConfigServiceStaysActiveWhenPickerChangesManagedCursorModel() throws {
        let home = FileManager.default.temporaryDirectory
            .appendingPathComponent(
                "CodexCursorPickerSelection-\(UUID().uuidString)",
                isDirectory: true)
        defer { try? FileManager.default.removeItem(at: home) }
        let codex = home.appendingPathComponent(".codex", isDirectory: true)
        try FileManager.default.createDirectory(at: codex, withIntermediateDirectories: true)
        let original = "model = \"gpt-5.6-sol\"\nmodel_provider = \"openai\"\n"
        let config = codex.appendingPathComponent("config.toml")
        try Data(original.utf8).write(to: config)
        try FileManager.default.setAttributes(
            [.posixPermissions: 0o600],
            ofItemAtPath: config.path)
        let service = CodexConfigService(home: home)

        try service.activate(
            model: "syncbar-cursor/auto",
            port: 32_125,
            bridgeToken: testCursorBridgeToken)
        var selected = try String(contentsOf: service.configurationURL, encoding: .utf8)
        selected = selected.replacingOccurrences(
            of: "model = \"syncbar-cursor/auto\"",
            with: "model = \"syncbar-cursor/cursor-grok-4.6\"")
        try Data(selected.utf8).write(to: service.configurationURL)

        let restartedService = CodexConfigService(home: home)
        XCTAssertTrue(try restartedService.isActive())
        XCTAssertEqual(
            try restartedService.activeCursorProviderConfiguration()?.model,
            "syncbar-cursor/auto")

        try restartedService.deactivate()
        XCTAssertEqual(
            try String(contentsOf: restartedService.configurationURL, encoding: .utf8),
            original)
    }

    func testCodexConfigServiceStaysActiveWhenPickerChangesToNativeModel() throws {
        let home = FileManager.default.temporaryDirectory
            .appendingPathComponent(
                "CodexCursorNativePickerSelection-\(UUID().uuidString)",
                isDirectory: true)
        defer { try? FileManager.default.removeItem(at: home) }
        let codex = home.appendingPathComponent(".codex", isDirectory: true)
        try FileManager.default.createDirectory(at: codex, withIntermediateDirectories: true)
        let original = "model = \"gpt-5.4\"\nmodel_provider = \"openai\"\n"
        let config = codex.appendingPathComponent("config.toml")
        try Data(original.utf8).write(to: config)
        try FileManager.default.setAttributes(
            [.posixPermissions: 0o600],
            ofItemAtPath: config.path)
        let service = CodexConfigService(home: home)

        try service.activate(
            model: "syncbar-cursor/auto",
            port: 32_125,
            bridgeToken: testCursorBridgeToken)
        var selected = try String(contentsOf: service.configurationURL, encoding: .utf8)
        selected = selected.replacingOccurrences(
            of: "model = \"syncbar-cursor/auto\"",
            with: "model = \"gpt-5.6-sol\"")
        try Data(selected.utf8).write(to: service.configurationURL)

        let restartedService = CodexConfigService(home: home)
        XCTAssertTrue(try restartedService.isActive())
        XCTAssertNotNil(try restartedService.activeCursorProviderConfiguration())

        try restartedService.deactivate()
        XCTAssertEqual(
            try String(contentsOf: restartedService.configurationURL, encoding: .utf8),
            original)
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
            agentPath: "/opt/homebrew/bin/agent",
            exposedModelIDs: ["syncbar-cursor/composer-2.5"])

        try store.save(expected)

        XCTAssertEqual(try store.load(), expected)
        let mode = try FileManager.default.attributesOfItem(
            atPath: store.preferencesURL.path)[.posixPermissions] as? NSNumber
        XCTAssertEqual(mode?.intValue, 0o600)
    }
}
