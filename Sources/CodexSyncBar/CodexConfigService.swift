import CryptoKit
import Darwin
import Foundation

private let cursorProviderID = "syncbar_cursor_bridge"
private let cursorMarkerBegin = "# BEGIN CODEX SYNCBAR CURSOR BRIDGE v1"
private let cursorMarkerEnd = "# END CODEX SYNCBAR CURSOR BRIDGE v1"

struct CodexCursorActivationState: Codable, Equatable, Sendable {
    static let currentSchemaVersion = 5

    let schemaVersion: Int
    let previousConfigurationExisted: Bool
    let previousModelAssignment: String?
    let previousProviderAssignment: String?
    let previousCatalogAssignment: String?
    let installedModelAssignment: String
    let installedProviderAssignment: String
    let installedCatalogAssignment: String
    let installedCatalogPath: String
    let installedManagedSuffix: String
    let installedModel: String
    let installedPort: Int
    let bridgeToken: String
    let sourceSHA256: String
    let installedSHA256: String

    init(
        previousConfigurationExisted: Bool = true,
        previousModelAssignment: String?,
        previousProviderAssignment: String?,
        previousCatalogAssignment: String?,
        installedModelAssignment: String,
        installedProviderAssignment: String,
        installedCatalogAssignment: String,
        installedCatalogPath: String,
        installedManagedSuffix: String,
        installedModel: String,
        installedPort: Int,
        bridgeToken: String,
        sourceSHA256: String,
        installedSHA256: String)
    {
        schemaVersion = Self.currentSchemaVersion
        self.previousConfigurationExisted = previousConfigurationExisted
        self.previousModelAssignment = previousModelAssignment
        self.previousProviderAssignment = previousProviderAssignment
        self.previousCatalogAssignment = previousCatalogAssignment
        self.installedModelAssignment = installedModelAssignment
        self.installedProviderAssignment = installedProviderAssignment
        self.installedCatalogAssignment = installedCatalogAssignment
        self.installedCatalogPath = installedCatalogPath
        self.installedManagedSuffix = installedManagedSuffix
        self.installedModel = installedModel
        self.installedPort = installedPort
        self.bridgeToken = bridgeToken
        self.sourceSHA256 = sourceSHA256
        self.installedSHA256 = installedSHA256
    }
}

struct ActiveCursorProviderConfiguration: Equatable, Sendable {
    let model: String
    let port: Int
    let bridgeToken: String
    let modelCatalogPath: String
}

struct CodexConfigTransaction: Codable, Equatable, Sendable {
    static let currentSchemaVersion = 1

    let schemaVersion: Int
    let expectedConfigurationExisted: Bool
    let expectedConfigurationSHA256: String
    let candidateConfigurationExisted: Bool
    let candidateConfigurationSHA256: String
    let previousActivationStateData: Data?
    let candidateActivationStateData: Data?

    init(
        expectedConfigurationExisted: Bool,
        expectedConfiguration: Data,
        candidateConfigurationExisted: Bool,
        candidateConfiguration: Data,
        previousActivationStateData: Data?,
        candidateActivationStateData: Data?)
    {
        schemaVersion = Self.currentSchemaVersion
        self.expectedConfigurationExisted = expectedConfigurationExisted
        expectedConfigurationSHA256 = Self.sha256(
            expectedConfigurationExisted ? expectedConfiguration : Data())
        self.candidateConfigurationExisted = candidateConfigurationExisted
        candidateConfigurationSHA256 = Self.sha256(
            candidateConfigurationExisted ? candidateConfiguration : Data())
        self.previousActivationStateData = previousActivationStateData
        self.candidateActivationStateData = candidateActivationStateData
    }

    func matches(configurationExists: Bool, data: Data, candidate: Bool) -> Bool {
        let expectedExists = candidate
            ? candidateConfigurationExisted
            : expectedConfigurationExisted
        let expectedHash = candidate
            ? candidateConfigurationSHA256
            : expectedConfigurationSHA256
        let normalizedData = configurationExists ? data : Data()
        return configurationExists == expectedExists && Self.sha256(normalizedData) == expectedHash
    }

    private static func sha256(_ data: Data) -> String {
        SHA256.hash(data: data).map { String(format: "%02x", $0) }.joined()
    }
}

private struct CodexConfigLine {
    let fullRange: Range<String.Index>
    let content: String
    let ending: String
}

private struct CodexConfigOperation {
    let range: Range<String.Index>
    let replacement: String
}

struct CodexConfigPatchResult: Equatable {
    let text: String
    let state: CodexCursorActivationState
}

enum CodexCursorConfigEditor {
    static func isActive(_ text: String) throws -> Bool {
        let parsed = try parseTopLevel(text)
        return parsed.provider.flatMap {
            basicStringValue(in: $0.content, key: "model_provider")
        } == cursorProviderID
    }

    static func configuredModelCatalogPath(in text: String) throws -> String? {
        let parsed = try parseTopLevel(text)
        guard let catalog = parsed.catalog else { return nil }
        guard let value = basicStringValue(
            in: catalog.content,
            key: "model_catalog_json")
        else {
            throw AppError.processFailed(
                "기존 Codex model_catalog_json 경로를 안전하게 해석할 수 없습니다.")
        }
        return value
    }

    static func activate(
        _ text: String,
        model: String,
        port: Int,
        bridgeToken: String = String(repeating: "0", count: 64),
        modelCatalogPath: String = "/tmp/codex-syncbar-cursor-model-catalog.json",
        sourceConfigurationExisted: Bool = true,
        existingState: CodexCursorActivationState? = nil) throws -> CodexConfigPatchResult
    {
        let preferences = try CursorBridgePreferences(
            port: port,
            model: model,
            bridgeToken: bridgeToken).validated()
        let catalogPath = try validateModelCatalogPath(modelCatalogPath)
        let parsed = try parseTopLevel(text)
        let installedModel = "model = \"\(preferences.model)\""
        let installedProvider = "model_provider = \"\(cursorProviderID)\""
        let installedCatalog = "model_catalog_json = \"\(catalogPath)\""

        let previousModel: String?
        let previousProvider: String?
        let previousCatalog: String?
        var baseText = text
        if let existingState {
            try validateState(existingState)
            guard parsed.model?.content.trimmingCharacters(in: .whitespaces) ==
                existingState.installedModelAssignment,
                parsed.provider?.content.trimmingCharacters(in: .whitespaces) ==
                existingState.installedProviderAssignment,
                parsed.catalog?.content.trimmingCharacters(in: .whitespaces) ==
                existingState.installedCatalogAssignment,
                baseText.hasSuffix(existingState.installedManagedSuffix)
            else {
                throw AppError.processFailed(
                    "Codex 설정이 Cursor 브리지를 켠 뒤 변경되어 자동으로 덮어쓸 수 없습니다.")
            }
            previousModel = existingState.previousModelAssignment
            previousProvider = existingState.previousProviderAssignment
            previousCatalog = existingState.previousCatalogAssignment
            baseText.removeLast(existingState.installedManagedSuffix.count)
        } else {
            guard !containsManagedProvider(text) else {
                throw AppError.processFailed(
                    "기존 Cursor 브리지 provider가 있지만 원복 기록이 없어 설정을 변경하지 않았습니다.")
            }
            previousModel = parsed.model?.content
            previousProvider = parsed.provider?.content
            previousCatalog = parsed.catalog?.content
        }

        try rejectProviderCollision(baseText)
        let patchedTop = try replaceTopLevel(
            baseText,
            modelAssignment: installedModel,
            providerAssignment: installedProvider,
            catalogAssignment: installedCatalog)
        let managedSuffix = makeManagedSuffix(
            for: patchedTop,
            port: preferences.port,
            bridgeToken: preferences.bridgeToken)
        let patched = patchedTop + managedSuffix
        let sourceHash = existingState?.sourceSHA256 ?? sha256(text)
        let state = CodexCursorActivationState(
            previousConfigurationExisted: existingState?.previousConfigurationExisted
                ?? sourceConfigurationExisted,
            previousModelAssignment: previousModel,
            previousProviderAssignment: previousProvider,
            previousCatalogAssignment: previousCatalog,
            installedModelAssignment: installedModel,
            installedProviderAssignment: installedProvider,
            installedCatalogAssignment: installedCatalog,
            installedCatalogPath: catalogPath,
            installedManagedSuffix: managedSuffix,
            installedModel: preferences.model,
            installedPort: preferences.port,
            bridgeToken: preferences.bridgeToken,
            sourceSHA256: sourceHash,
            installedSHA256: sha256(patched))
        return CodexConfigPatchResult(text: patched, state: state)
    }

    static func deactivate(
        _ text: String,
        state: CodexCursorActivationState) throws -> String
    {
        try validateState(state)
        let parsed = try parseTopLevel(text)
        guard parsed.model?.content.trimmingCharacters(in: .whitespaces) ==
            state.installedModelAssignment,
            parsed.provider?.content.trimmingCharacters(in: .whitespaces) ==
            state.installedProviderAssignment,
            parsed.catalog?.content.trimmingCharacters(in: .whitespaces) ==
            state.installedCatalogAssignment,
            text.hasSuffix(state.installedManagedSuffix)
        else {
            throw AppError.processFailed(
                "Codex 설정이 Cursor 브리지를 켠 뒤 변경되어 이전 모델을 자동 복구하지 않았습니다.")
        }

        var base = text
        base.removeLast(state.installedManagedSuffix.count)
        return try replaceTopLevel(
            base,
            modelAssignment: state.previousModelAssignment,
            providerAssignment: state.previousProviderAssignment,
            catalogAssignment: state.previousCatalogAssignment)
    }

    private static func validateState(_ state: CodexCursorActivationState) throws {
        let preferences = try CursorBridgePreferences(
            port: state.installedPort,
            model: state.installedModel,
            bridgeToken: state.bridgeToken).validated()
        let catalogPath = try validateModelCatalogPath(state.installedCatalogPath)
        guard state.schemaVersion == CodexCursorActivationState.currentSchemaVersion,
              state.installedProviderAssignment == "model_provider = \"\(cursorProviderID)\"",
              state.installedCatalogAssignment == "model_catalog_json = \"\(catalogPath)\"",
              preferences.model == state.installedModel,
              state.installedManagedSuffix.contains(cursorMarkerBegin),
              state.installedManagedSuffix.contains(cursorMarkerEnd)
        else {
            throw AppError.processFailed("Cursor 브리지 원복 기록이 올바르지 않습니다.")
        }
    }

    private static func makeManagedSuffix(
        for text: String,
        port: Int,
        bridgeToken: String) -> String
    {
        let newline = preferredNewline(in: text)
        let separator: String
        if text.isEmpty {
            separator = ""
        } else if text.hasSuffix(newline + newline) {
            separator = ""
        } else if text.hasSuffix(newline) {
            separator = newline
        } else {
            separator = newline + newline
        }
        let block = [
            cursorMarkerBegin,
            "[model_providers.\(cursorProviderID)]",
            "name = \"Cursor Subscription (local SyncBar bridge)\"",
            "base_url = \"http://127.0.0.1:\(port)/v1\"",
            "wire_api = \"responses\"",
            "requires_openai_auth = false",
            "http_headers = { \"X-SyncBar-Bridge-Token\" = \"\(bridgeToken)\" }",
            "request_max_retries = 0",
            "stream_max_retries = 0",
            "stream_idle_timeout_ms = 900000",
            cursorMarkerEnd,
        ].joined(separator: newline)
        return separator + block + newline
    }

    private static func replaceTopLevel(
        _ text: String,
        modelAssignment: String?,
        providerAssignment: String?,
        catalogAssignment: String?) throws -> String
    {
        let parsed = try parseTopLevel(text)
        var operations: [CodexConfigOperation] = []
        var insertions: [String] = []

        func schedule(_ line: CodexConfigLine?, value: String?) {
            if let line {
                operations.append(CodexConfigOperation(
                    range: line.fullRange,
                    replacement: value.map { $0 + line.ending } ?? ""))
            } else if let value {
                insertions.append(value)
            }
        }
        schedule(parsed.model, value: modelAssignment)
        schedule(parsed.provider, value: providerAssignment)
        schedule(parsed.catalog, value: catalogAssignment)

        if !insertions.isEmpty {
            let newline = preferredNewline(in: text)
            let existingAssignments = [parsed.model, parsed.provider, parsed.catalog].compactMap { $0 }
            let insertionIndex = existingAssignments
                .map(\.fullRange.upperBound)
                .max() ?? text.startIndex
            let needsLeadingNewline = insertionIndex == text.endIndex &&
                !text.isEmpty &&
                !text.hasSuffix("\n")
            let insertion = (needsLeadingNewline ? newline : "") +
                insertions.joined(separator: newline) + newline
            operations.append(CodexConfigOperation(
                range: insertionIndex ..< insertionIndex,
                replacement: insertion))
        }

        var result = text
        for operation in operations.sorted(by: { $0.range.lowerBound > $1.range.lowerBound }) {
            result.replaceSubrange(operation.range, with: operation.replacement)
        }
        return result
    }

    private static func containsManagedProvider(_ text: String) -> Bool {
        text.contains(cursorMarkerBegin) || text.contains(cursorMarkerEnd)
    }

    private static func rejectProviderCollision(_ text: String) throws {
        for line in scanLines(text) {
            let trimmed = line.content.trimmingCharacters(in: .whitespaces)
            guard !trimmed.hasPrefix("#") else { continue }
            let syntax = String(trimmed.split(separator: "#", maxSplits: 1).first ?? "")
            let compact = syntax.filter {
                !$0.isWhitespace && $0 != "\"" && $0 != "'"
            }
            if compact == "[model_providers.\(cursorProviderID)]" ||
                compact == "[[model_providers.\(cursorProviderID)]]" ||
                compact.hasPrefix("model_providers.\(cursorProviderID).") ||
                compact.hasPrefix("model_providers.\(cursorProviderID)=")
            {
                throw AppError.processFailed(
                    "model_providers.\(cursorProviderID)가 이미 정의되어 있어 충돌을 피했습니다.")
            }
        }
        guard !containsManagedProvider(text) else {
            throw AppError.processFailed("Cursor 브리지 설정 marker가 손상되어 자동 수정하지 않았습니다.")
        }
    }

    private static func parseTopLevel(_ text: String) throws -> (
        model: CodexConfigLine?,
        provider: CodexConfigLine?,
        catalog: CodexConfigLine?,
        firstTableStart: String.Index)
    {
        var modelLines: [CodexConfigLine] = []
        var providerLines: [CodexConfigLine] = []
        var catalogLines: [CodexConfigLine] = []
        var firstTableStart = text.endIndex
        for line in scanLines(text) {
            let trimmed = line.content.trimmingCharacters(in: .whitespaces)
            if !trimmed.hasPrefix("#"),
               trimmed.contains("\"\"\"") || trimmed.contains("'''")
            {
                throw AppError.processFailed(
                    "Codex 최상위 multiline TOML은 안전하게 보존할 수 없어 자동 수정하지 않았습니다.")
            }
            if trimmed.hasPrefix("[") {
                firstTableStart = line.fullRange.lowerBound
                break
            }
            guard !trimmed.isEmpty, !trimmed.hasPrefix("#"), let equal = trimmed.firstIndex(of: "=") else {
                continue
            }
            let key = trimmed[..<equal].trimmingCharacters(in: .whitespaces)
            if key == "\"model\"" || key == "'model'" ||
                key == "\"model_provider\"" || key == "'model_provider'" ||
                key == "\"model_catalog_json\"" || key == "'model_catalog_json'"
            {
                throw AppError.processFailed(
                    "Codex 최상위 model 관련 key가 quoted 형식이라 자동 수정하지 않았습니다.")
            }
            guard key == "model" || key == "model_provider" || key == "model_catalog_json" else {
                continue
            }
            let value = trimmed[trimmed.index(after: equal)...].trimmingCharacters(in: .whitespaces)
            guard isSafeBasicString(value) else {
                throw AppError.processFailed("Codex 최상위 \(key) 값이 안전한 한 줄 문자열이 아닙니다.")
            }
            switch key {
            case "model": modelLines.append(line)
            case "model_provider": providerLines.append(line)
            default: catalogLines.append(line)
            }
        }
        guard modelLines.count <= 1, providerLines.count <= 1, catalogLines.count <= 1 else {
            throw AppError.processFailed("Codex 최상위 model 관련 설정이 중복되어 자동 수정하지 않았습니다.")
        }
        return (modelLines.first, providerLines.first, catalogLines.first, firstTableStart)
    }

    private static func validateModelCatalogPath(_ path: String) throws -> String {
        let hasUnsafeCharacter = path.unicodeScalars.contains { scalar in
            scalar == "\"" || scalar == "\\" || scalar.value < 0x20 || scalar.value == 0x7F
        }
        guard path.hasPrefix("/"), path.count > 1, !hasUnsafeCharacter else {
            throw AppError.processFailed(
                "Codex 모델 카탈로그 경로는 따옴표, 역슬래시, 제어 문자가 없는 절대 경로여야 합니다.")
        }
        return path
    }

    private static func isSafeBasicString(_ value: String) -> Bool {
        guard value.first == "\"" else { return false }
        var index = value.index(after: value.startIndex)
        var escaped = false
        while index < value.endIndex {
            let character = value[index]
            if escaped {
                escaped = false
            } else if character == "\\" {
                escaped = true
            } else if character == "\"" {
                let remainder = value[value.index(after: index)...]
                    .trimmingCharacters(in: .whitespaces)
                return remainder.isEmpty || remainder.hasPrefix("#")
            }
            index = value.index(after: index)
        }
        return false
    }

    private static func basicStringValue(in content: String, key: String) -> String? {
        let trimmed = content.trimmingCharacters(in: .whitespaces)
        guard let equal = trimmed.firstIndex(of: "="),
              trimmed[..<equal].trimmingCharacters(in: .whitespaces) == key
        else { return nil }
        let raw = String(trimmed[trimmed.index(after: equal)...])
            .trimmingCharacters(in: .whitespaces)
        guard isSafeBasicString(raw), raw.first == "\"" else { return nil }
        var index = raw.index(after: raw.startIndex)
        let start = index
        while index < raw.endIndex {
            if raw[index] == "\\" { return nil }
            if raw[index] == "\"" { return String(raw[start ..< index]) }
            index = raw.index(after: index)
        }
        return nil
    }

    private static func scanLines(_ text: String) -> [CodexConfigLine] {
        guard !text.isEmpty else { return [] }
        var lines: [CodexConfigLine] = []
        var start = text.startIndex
        while start < text.endIndex {
            if let newlineIndex = text[start...].firstIndex(where: {
                $0 == "\n" || $0 == "\r\n" || $0 == "\r"
            }) {
                let afterNewline = text.index(after: newlineIndex)
                let ending = String(text[newlineIndex ..< afterNewline])
                lines.append(CodexConfigLine(
                    fullRange: start ..< afterNewline,
                    content: String(text[start ..< newlineIndex]),
                    ending: ending))
                start = afterNewline
            } else {
                lines.append(CodexConfigLine(
                    fullRange: start ..< text.endIndex,
                    content: String(text[start...]),
                    ending: ""))
                break
            }
        }
        return lines
    }

    private static func preferredNewline(in text: String) -> String {
        text.contains("\r\n") ? "\r\n" : "\n"
    }

    private static func sha256(_ text: String) -> String {
        SHA256.hash(data: Data(text.utf8)).map { String(format: "%02x", $0) }.joined()
    }
}

struct CodexConfigService {
    let home: URL
    let fileManager: FileManager
    private let configuredCodexDirectory: URL?

    init(
        home: URL = FileManager.default.homeDirectoryForCurrentUser,
        fileManager: FileManager = .default,
        codexDirectory: URL? = nil)
    {
        self.home = home
        self.fileManager = fileManager
        if let codexDirectory {
            configuredCodexDirectory = codexDirectory.standardizedFileURL
        } else if home.standardizedFileURL == FileManager.default.homeDirectoryForCurrentUser.standardizedFileURL,
                  let value = ProcessInfo.processInfo.environment["CODEX_HOME"],
                  value.hasPrefix("/")
        {
            configuredCodexDirectory = URL(fileURLWithPath: value, isDirectory: true)
                .standardizedFileURL
        } else {
            configuredCodexDirectory = nil
        }
    }

    var codexDirectory: URL {
        configuredCodexDirectory ?? home.appendingPathComponent(".codex", isDirectory: true)
    }

    var configurationURL: URL {
        codexDirectory.appendingPathComponent("config.toml")
    }

    var stateRoot: URL {
        home.appendingPathComponent(".local/share/gpt-switch", isDirectory: true)
    }

    var activationURL: URL {
        stateRoot.appendingPathComponent("cursor-codex-activation.json")
    }

    var transactionURL: URL {
        stateRoot.appendingPathComponent("cursor-codex-transaction.json")
    }

    var lockURL: URL {
        stateRoot.appendingPathComponent("cursor-codex.lock")
    }

    func isActive() throws -> Bool {
        try ensureDirectories()
        return try withConfigurationLock { try isActiveLocked() }
    }

    private func isActiveLocked() throws -> Bool {
        try activeConfigurationLocked() != nil
    }

    func activeCursorProviderConfiguration() throws -> ActiveCursorProviderConfiguration? {
        try ensureDirectories()
        return try withConfigurationLock { try activeConfigurationLocked() }
    }

    func configuredModelCatalogPath() throws -> String? {
        try ensureDirectories()
        return try withConfigurationLock {
            try recoverTransactionIfNeeded()
            let snapshot = try configurationSnapshot()
            guard snapshot.exists else { return nil }
            guard let text = String(data: snapshot.data, encoding: .utf8) else {
                throw AppError.processFailed("Codex 설정 파일이 UTF-8이 아닙니다.")
            }
            return try CodexCursorConfigEditor.configuredModelCatalogPath(in: text)
        }
    }

    private func activeConfigurationLocked() throws -> ActiveCursorProviderConfiguration? {
        try recoverTransactionIfNeeded()
        let snapshot = try configurationSnapshot()
        guard snapshot.exists else {
            if try readActivationStateData() != nil {
                throw AppError.processFailed("Codex 설정은 없지만 Cursor 브리지 원복 기록이 남아 있습니다.")
            }
            return nil
        }
        let data = snapshot.data
        guard let text = String(data: data, encoding: .utf8) else {
            throw AppError.processFailed("Codex 설정 파일이 UTF-8이 아닙니다.")
        }
        guard try CodexCursorConfigEditor.isActive(text) else {
            if try readActivationStateData() != nil {
                throw AppError.processFailed(
                    "Codex 설정과 Cursor 브리지 원복 기록이 일치하지 않아 자동 변경을 중단했습니다.")
            }
            return nil
        }
        guard let stateData = try readActivationStateData() else {
            throw AppError.processFailed(
                "Cursor provider가 설정되어 있지만 SyncBar 원복 기록이 없습니다.")
        }
        let state = try decodeActivationState(stateData)
        _ = try CodexCursorConfigEditor.deactivate(text, state: state)
        return ActiveCursorProviderConfiguration(
            model: state.installedModel,
            port: state.installedPort,
            bridgeToken: state.bridgeToken,
            modelCatalogPath: state.installedCatalogPath)
    }

    @discardableResult
    func activate(
        model: String,
        port: Int,
        bridgeToken: String,
        modelCatalogPath: String? = nil) throws -> CodexCursorActivationState
    {
        try ensureDirectories()
        return try withConfigurationLock {
            try activateLocked(
                model: model,
                port: port,
                bridgeToken: bridgeToken,
                modelCatalogPath: modelCatalogPath ?? stateRoot
                    .appendingPathComponent("cursor-codex-model-catalog.json")
                    .path)
        }
    }

    private func activateLocked(
        model: String,
        port: Int,
        bridgeToken: String,
        modelCatalogPath: String) throws -> CodexCursorActivationState
    {
        try recoverTransactionIfNeeded()
        let original = try configurationSnapshot()
        let originalData = original.data
        guard let originalText = String(data: originalData, encoding: .utf8) else {
            throw AppError.processFailed("Codex 설정 파일이 UTF-8이 아닙니다.")
        }
        let previousStateData = try readActivationStateData()
        let previousState = try previousStateData.map(decodeActivationState)
        let patch = try CodexCursorConfigEditor.activate(
            originalText,
            model: model,
            port: port,
            bridgeToken: bridgeToken,
            modelCatalogPath: modelCatalogPath,
            sourceConfigurationExisted: original.exists,
            existingState: previousState)
        let candidate = Data(patch.text.utf8)
        let candidateStateData = try encodeActivationState(patch.state)
        let transaction = CodexConfigTransaction(
            expectedConfigurationExisted: original.exists,
            expectedConfiguration: originalData,
            candidateConfigurationExisted: true,
            candidateConfiguration: candidate,
            previousActivationStateData: previousStateData,
            candidateActivationStateData: candidateStateData)
        try writeTransaction(transaction)
        do {
            try compareAndSwapConfiguration(
                expectedExists: original.exists,
                expected: originalData,
                candidateExists: true,
                candidate: candidate)
            try installActivationState(candidateStateData)
            try removeTransaction()
        } catch {
            let operationError = error
            try recoverTransactionIfNeeded()
            let recovered = try configurationSnapshot()
            if recovered.exists, recovered.data == candidate,
               try readActivationStateData() == candidateStateData
            {
                return patch.state
            }
            throw operationError
        }
        return patch.state
    }

    func deactivate() throws {
        try ensureDirectories()
        try withConfigurationLock { try deactivateLocked() }
    }

    private func deactivateLocked() throws {
        try recoverTransactionIfNeeded()
        guard let stateData = try readActivationStateData() else {
            throw AppError.processFailed("Cursor 브리지의 이전 Codex 모델 원복 기록이 없습니다.")
        }
        let state = try decodeActivationState(stateData)
        let original = try configurationSnapshot()
        guard original.exists else {
            throw AppError.processFailed("Cursor provider가 활성 상태인데 Codex 설정 파일이 없습니다.")
        }
        let originalData = original.data
        guard let originalText = String(data: originalData, encoding: .utf8) else {
            throw AppError.processFailed("Codex 설정 파일이 UTF-8이 아닙니다.")
        }
        let restoredText = try CodexCursorConfigEditor.deactivate(originalText, state: state)
        let candidate = Data(restoredText.utf8)
        // If config.toml did not exist before activation, remove the generated
        // file only when nothing else remains. User settings added ahead of
        // the managed suffix must survive deactivation.
        let candidateExists = state.previousConfigurationExisted || !candidate.isEmpty
        let transaction = CodexConfigTransaction(
            expectedConfigurationExisted: true,
            expectedConfiguration: originalData,
            candidateConfigurationExisted: candidateExists,
            candidateConfiguration: candidate,
            previousActivationStateData: stateData,
            candidateActivationStateData: nil)
        try writeTransaction(transaction)
        do {
            try compareAndSwapConfiguration(
                expectedExists: true,
                expected: originalData,
                candidateExists: candidateExists,
                candidate: candidate)
            try installActivationState(nil)
            try removeTransaction()
        } catch {
            let operationError = error
            try recoverTransactionIfNeeded()
            let recovered = try configurationSnapshot()
            if recovered.exists == candidateExists,
               recovered.data == candidate,
               try readActivationStateData() == nil
            {
                return
            }
            throw operationError
        }
    }

    private func configurationSnapshot() throws -> (exists: Bool, data: Data) {
        guard pathEntryExists(configurationURL) else { return (false, Data()) }
        return (true, try readSafeConfiguration())
    }

    private func readSafeConfiguration() throws -> Data {
        try requireOwnedRegularFile(configurationURL, privateFile: true)
        return try Data(contentsOf: configurationURL)
    }

    private func readActivationStateData() throws -> Data? {
        guard pathEntryExists(activationURL) else { return nil }
        try requireOwnedRegularFile(activationURL, privateFile: true)
        return try Data(contentsOf: activationURL)
    }

    private func encodeActivationState(_ state: CodexCursorActivationState) throws -> Data {
        let encoder = JSONEncoder()
        encoder.outputFormatting = [.prettyPrinted, .sortedKeys]
        var data = try encoder.encode(state)
        data.append(0x0A)
        return data
    }

    private func decodeActivationState(_ data: Data) throws -> CodexCursorActivationState {
        let state = try JSONDecoder().decode(CodexCursorActivationState.self, from: data)
        guard state.schemaVersion == CodexCursorActivationState.currentSchemaVersion else {
            throw AppError.processFailed("Cursor 브리지 원복 기록 버전이 올바르지 않습니다.")
        }
        return state
    }

    private func installActivationState(_ data: Data?) throws {
        if let data {
            _ = try decodeActivationState(data)
            try atomicWrite(data, to: activationURL, permissions: 0o600)
        } else if pathEntryExists(activationURL) {
            try fileManager.removeItem(at: activationURL)
        }
    }

    private func writeTransaction(_ transaction: CodexConfigTransaction) throws {
        let encoder = JSONEncoder()
        encoder.outputFormatting = [.prettyPrinted, .sortedKeys]
        var data = try encoder.encode(transaction)
        data.append(0x0A)
        try atomicWrite(data, to: transactionURL, permissions: 0o600)
    }

    private func readTransaction() throws -> CodexConfigTransaction? {
        guard pathEntryExists(transactionURL) else { return nil }
        try requireOwnedRegularFile(transactionURL, privateFile: true)
        let value = try JSONDecoder().decode(
            CodexConfigTransaction.self,
            from: Data(contentsOf: transactionURL))
        guard value.schemaVersion == CodexConfigTransaction.currentSchemaVersion else {
            throw AppError.processFailed("Cursor provider 설정 transaction 버전이 올바르지 않습니다.")
        }
        if let data = value.previousActivationStateData { _ = try decodeActivationState(data) }
        if let data = value.candidateActivationStateData { _ = try decodeActivationState(data) }
        return value
    }

    private func removeTransaction() throws {
        if pathEntryExists(transactionURL) { try fileManager.removeItem(at: transactionURL) }
    }

    private func recoverTransactionIfNeeded() throws {
        guard let transaction = try readTransaction() else { return }
        let live = try configurationSnapshot()
        if transaction.matches(
            configurationExists: live.exists,
            data: live.data,
            candidate: false)
        {
            try installActivationState(transaction.previousActivationStateData)
            try removeTransaction()
            return
        }
        if transaction.matches(
            configurationExists: live.exists,
            data: live.data,
            candidate: true)
        {
            try installActivationState(transaction.candidateActivationStateData)
            try removeTransaction()
            return
        }
        throw AppError.processFailed(
            "중단된 Cursor provider transaction 이후 Codex 설정이 외부에서 변경되어 자동 복구하지 않았습니다.")
    }

    private func compareAndSwapConfiguration(
        expectedExists: Bool,
        expected: Data,
        candidateExists: Bool,
        candidate: Data) throws
    {
        let permissions: Int
        if expectedExists {
            let attributes = try fileManager.attributesOfItem(atPath: configurationURL.path)
            permissions = (attributes[.posixPermissions] as? NSNumber)?.intValue ?? 0o600
        } else {
            permissions = 0o600
        }
        let temporary = configurationURL.deletingLastPathComponent()
            .appendingPathComponent(".config.toml.\(UUID().uuidString).tmp")
        defer { try? fileManager.removeItem(at: temporary) }
        if candidateExists {
            try candidate.write(to: temporary, options: [.atomic])
            try fileManager.setAttributes([.posixPermissions: permissions], ofItemAtPath: temporary.path)
        }

        let live = try configurationSnapshot()
        guard live.exists == expectedExists, live.data == expected else {
            throw AppError.processFailed("Codex 설정이 동시에 변경되어 Cursor provider 적용을 중단했습니다.")
        }
        if candidateExists {
            guard rename(temporary.path, configurationURL.path) == 0 else {
                throw AppError.processFailed(
                    "Codex 설정을 원자적으로 저장하지 못했습니다: \(String(cString: strerror(errno)))")
            }
        } else {
            try fileManager.removeItem(at: configurationURL)
        }
        let installed = try configurationSnapshot()
        guard installed.exists == candidateExists, installed.data == candidate else {
            throw AppError.processFailed("Codex 설정 저장 후 검증에 실패했습니다.")
        }
    }

    private func atomicWrite(_ data: Data, to destination: URL, permissions: Int) throws {
        let parent = destination.deletingLastPathComponent()
        let temporary = parent.appendingPathComponent(".\(destination.lastPathComponent).\(UUID().uuidString).tmp")
        defer { try? fileManager.removeItem(at: temporary) }
        try data.write(to: temporary, options: [.atomic])
        try fileManager.setAttributes([.posixPermissions: permissions], ofItemAtPath: temporary.path)
        guard rename(temporary.path, destination.path) == 0 else {
            throw AppError.processFailed(
                "설정을 원자적으로 저장하지 못했습니다: \(String(cString: strerror(errno)))")
        }
    }

    private func pathEntryExists(_ url: URL) -> Bool {
        var info = stat()
        return url.path.withCString { lstat($0, &info) } == 0
    }

    private func withConfigurationLock<T>(_ operation: () throws -> T) throws -> T {
        let descriptor = open(
            lockURL.path,
            O_CREAT | O_RDWR | O_CLOEXEC | O_NOFOLLOW,
            S_IRUSR | S_IWUSR)
        guard descriptor >= 0 else {
            throw AppError.processFailed(
                "Cursor provider 설정 lock을 열지 못했습니다: \(String(cString: strerror(errno)))")
        }
        defer { close(descriptor) }
        var info = stat()
        guard fstat(descriptor, &info) == 0,
              (info.st_mode & S_IFMT) == S_IFREG,
              info.st_uid == getuid()
        else {
            throw AppError.processFailed("Cursor provider 설정 lock이 안전한 일반 파일이 아닙니다.")
        }
        guard fchmod(descriptor, S_IRUSR | S_IWUSR) == 0,
              flock(descriptor, LOCK_EX) == 0
        else {
            throw AppError.processFailed(
                "Cursor provider 설정 lock을 획득하지 못했습니다: \(String(cString: strerror(errno)))")
        }
        defer { _ = flock(descriptor, LOCK_UN) }
        return try operation()
    }

    private func ensureDirectories() throws {
        let local = home.appendingPathComponent(".local", isDirectory: true)
        let share = local.appendingPathComponent("share", isDirectory: true)
        for directory in [codexDirectory, local, share, stateRoot] {
            if fileManager.fileExists(atPath: directory.path) {
                let values = try directory.resourceValues(forKeys: [.isDirectoryKey, .isSymbolicLinkKey])
                guard values.isDirectory == true, values.isSymbolicLink != true else {
                    throw AppError.processFailed("설정 경로가 안전한 디렉터리가 아닙니다: \(directory.path)")
                }
            } else {
                try fileManager.createDirectory(
                    at: directory,
                    withIntermediateDirectories: false,
                    attributes: [.posixPermissions: 0o700])
            }
        }
    }

    private func requireOwnedRegularFile(_ url: URL, privateFile: Bool) throws {
        let values = try url.resourceValues(forKeys: [.isRegularFileKey, .isSymbolicLinkKey])
        guard values.isRegularFile == true, values.isSymbolicLink != true else {
            throw AppError.processFailed("설정 파일이 안전한 일반 파일이 아닙니다: \(url.path)")
        }
        let attributes = try fileManager.attributesOfItem(atPath: url.path)
        let owner = (attributes[.ownerAccountID] as? NSNumber)?.uint32Value
        let permissions = (attributes[.posixPermissions] as? NSNumber)?.intValue ?? 0o777
        guard owner == getuid() else {
            throw AppError.processFailed("설정 파일 소유자가 현재 사용자와 다릅니다: \(url.path)")
        }
        if privateFile {
            guard permissions & 0o077 == 0 else {
                throw AppError.processFailed("Cursor provider 설정 파일 권한은 0600이어야 합니다.")
            }
        } else {
            guard permissions & 0o022 == 0 else {
                throw AppError.processFailed("Codex 설정 파일은 다른 사용자가 쓸 수 없어야 합니다.")
            }
        }
    }
}
