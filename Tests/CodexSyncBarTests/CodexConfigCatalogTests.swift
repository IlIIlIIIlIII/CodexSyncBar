import XCTest
@testable import CodexSyncBar

private let catalogTestBridgeToken = String(repeating: "c", count: 64)

final class CodexConfigCatalogTests: XCTestCase {
    func testConfiguredCatalogPathIsReadableAndEscapedValueFailsClosed() throws {
        XCTAssertEqual(
            try CodexCursorConfigEditor.configuredModelCatalogPath(
                in: "model_catalog_json = \"/managed/catalog.json\" # keep\n"),
            "/managed/catalog.json")
        XCTAssertThrowsError(try CodexCursorConfigEditor.configuredModelCatalogPath(
            in: "model_catalog_json = \"/managed\\u002fcatalog.json\"\n"))
    }

    func testCatalogAssignmentRoundTripsRawPreviousValueAndPath() throws {
        let original = [
            "# keep",
            "model = \"gpt-5.6-sol\"",
            "model_provider = \"openai\"",
            "model_catalog_json = \"/Users/test/Old Catalog.json\" # restore exactly",
            "personality = \"pragmatic\"",
            "",
        ].joined(separator: "\r\n")
        let installedPath = "/Users/test/.local/share/gpt-switch/cursor catalog.json"

        let patch = try CodexCursorConfigEditor.activate(
            original,
            model: "composer-2.5",
            port: 32_125,
            modelCatalogPath: installedPath)

        XCTAssertEqual(patch.state.previousCatalogAssignment,
                       "model_catalog_json = \"/Users/test/Old Catalog.json\" # restore exactly")
        XCTAssertEqual(patch.state.installedCatalogPath, installedPath)
        XCTAssertEqual(patch.state.installedCatalogAssignment,
                       "model_catalog_json = \"\(installedPath)\"")
        XCTAssertTrue(patch.text.contains("model_catalog_json = \"\(installedPath)\"\r\n"))
        XCTAssertEqual(
            try CodexCursorConfigEditor.deactivate(patch.text, state: patch.state),
            original)
    }

    func testCatalogAssignmentSurvivesReactivationAndUnrelatedEditsArePreserved() throws {
        let original = [
            "model = \"gpt-5.6-sol\"",
            "model_provider = \"openai\"",
            "model_catalog_json = \"/original/catalog.json\"",
            "personality = \"pragmatic\"",
            "",
        ].joined(separator: "\n")
        let first = try CodexCursorConfigEditor.activate(
            original,
            model: "composer-2.5",
            port: 32_125,
            modelCatalogPath: "/managed/first.json")
        let second = try CodexCursorConfigEditor.activate(
            first.text,
            model: "gpt-5.6-sol-high",
            port: 32_126,
            modelCatalogPath: "/managed/second.json",
            existingState: first.state)
        let edited = second.text.replacingOccurrences(
            of: "# BEGIN CODEX SYNCBAR CURSOR BRIDGE v1",
            with: "unrelated_setting = \"keep\"\n\n# BEGIN CODEX SYNCBAR CURSOR BRIDGE v1")

        let restored = try CodexCursorConfigEditor.deactivate(edited, state: second.state)

        XCTAssertTrue(restored.contains("model_catalog_json = \"/original/catalog.json\"\n"))
        XCTAssertTrue(restored.contains("personality = \"pragmatic\"\n"))
        XCTAssertTrue(restored.contains("unrelated_setting = \"keep\"\n"))
        XCTAssertFalse(restored.contains("/managed/first.json"))
        XCTAssertFalse(restored.contains("/managed/second.json"))
    }

    func testCatalogAssignmentFailsClosedOnDuplicateQuotedKeyAndDrift() throws {
        XCTAssertThrowsError(try CodexCursorConfigEditor.activate(
            "model_catalog_json = \"/one.json\"\nmodel_catalog_json = \"/two.json\"\n",
            model: "auto",
            port: 32_125))
        XCTAssertThrowsError(try CodexCursorConfigEditor.activate(
            "\"model_catalog_json\" = \"/one.json\"\n",
            model: "auto",
            port: 32_125))
        XCTAssertThrowsError(try CodexCursorConfigEditor.activate(
            "model_catalog_json = '/one.json'\n",
            model: "auto",
            port: 32_125))

        let patch = try CodexCursorConfigEditor.activate(
            "model = \"gpt-5.6-sol\"\n",
            model: "auto",
            port: 32_125,
            modelCatalogPath: "/managed/catalog.json")
        let drifted = patch.text.replacingOccurrences(
            of: "model_catalog_json = \"/managed/catalog.json\"",
            with: "model_catalog_json = \"/someone-elses/catalog.json\"")

        XCTAssertThrowsError(try CodexCursorConfigEditor.deactivate(drifted, state: patch.state))
    }

    func testCatalogPathRejectsRelativeQuotingBackslashAndControlCharacters() throws {
        let invalidPaths = [
            "relative/catalog.json",
            "/tmp/quoted\"catalog.json",
            "/tmp/back\\slash.json",
            "/tmp/new\nline.json",
            "/tmp/tab\tcatalog.json",
            "/",
        ]

        for path in invalidPaths {
            XCTAssertThrowsError(try CodexCursorConfigEditor.activate(
                "",
                model: "auto",
                port: 32_125,
                modelCatalogPath: path), "Expected rejection for \(path.debugDescription)")
        }
    }

    func testConfigServiceInstallsExplicitCatalogPathAndReportsIt() throws {
        let home = FileManager.default.temporaryDirectory
            .appendingPathComponent("CodexCatalogConfig-\(UUID().uuidString)", isDirectory: true)
        defer { try? FileManager.default.removeItem(at: home) }
        try FileManager.default.createDirectory(at: home, withIntermediateDirectories: true)
        let service = CodexConfigService(home: home)
        let catalogPath = home.appendingPathComponent("managed catalog.json").path

        let state = try service.activate(
            model: "gpt-5.6-sol-high",
            port: 32_125,
            bridgeToken: catalogTestBridgeToken,
            modelCatalogPath: catalogPath)

        XCTAssertEqual(state.schemaVersion, 5)
        XCTAssertEqual(state.installedCatalogPath, catalogPath)
        XCTAssertEqual(
            try service.activeCursorProviderConfiguration()?.modelCatalogPath,
            catalogPath)
        let active = try String(contentsOf: service.configurationURL, encoding: .utf8)
        XCTAssertTrue(active.contains("model_catalog_json = \"\(catalogPath)\""))

        try service.deactivate()
        XCTAssertFalse(FileManager.default.fileExists(atPath: service.configurationURL.path))
    }
}
