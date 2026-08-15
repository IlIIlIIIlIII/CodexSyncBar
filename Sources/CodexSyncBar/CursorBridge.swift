import Darwin
import Foundation

struct CursorBridgePreferences: Codable, Equatable, Sendable {
    static let currentSchemaVersion = 2
    static let defaultPort = 32_125

    var schemaVersion = Self.currentSchemaVersion
    var port = Self.defaultPort
    var model = "auto"
    var agentPath: String?
    var bridgeToken: String

    init(
        schemaVersion: Int = Self.currentSchemaVersion,
        port: Int = Self.defaultPort,
        model: String = "auto",
        agentPath: String? = nil,
        bridgeToken: String = Self.makeBridgeToken())
    {
        self.schemaVersion = schemaVersion
        self.port = port
        self.model = model
        self.agentPath = agentPath
        self.bridgeToken = bridgeToken
    }

    func validated() throws -> Self {
        guard schemaVersion == Self.currentSchemaVersion else {
            throw AppError.processFailed("지원하지 않는 Cursor 브리지 설정 버전입니다.")
        }
        guard (1_024 ... 65_535).contains(port) else {
            throw AppError.processFailed("Cursor 브리지 포트는 1024~65535 사이여야 합니다.")
        }
        let normalizedModel = model.trimmingCharacters(in: .whitespacesAndNewlines)
        guard normalizedModel.range(
            of: #"^[A-Za-z0-9][A-Za-z0-9._:/-]{0,127}$"#,
            options: .regularExpression) != nil
        else {
            throw AppError.processFailed("Cursor 모델 ID 형식이 올바르지 않습니다.")
        }
        if let agentPath {
            guard agentPath.hasPrefix("/"), !agentPath.contains("\n"), !agentPath.contains("\0") else {
                throw AppError.processFailed("Cursor CLI 경로는 절대 경로여야 합니다.")
            }
        }
        guard bridgeToken.range(of: #"^[a-f0-9]{64}$"#, options: .regularExpression) != nil else {
            throw AppError.processFailed("Cursor 브리지 인증 token 형식이 올바르지 않습니다.")
        }
        var value = self
        value.model = normalizedModel
        return value
    }

    private static func makeBridgeToken() -> String {
        (UUID().uuidString + UUID().uuidString)
            .replacingOccurrences(of: "-", with: "")
            .lowercased()
    }
}

enum CursorBridgeStatus: Equatable, Sendable {
    case stopped
    case starting
    case healthy(pid: Int32)
    case missingNode
    case missingAgent
    case unauthenticated
    case portConflict
    case failed(String)

    var title: String {
        switch self {
        case .stopped: "중지됨"
        case .starting: "시작 중…"
        case .healthy: "연결됨"
        case .missingNode: "Node.js 필요"
        case .missingAgent: "Cursor CLI 필요"
        case .unauthenticated: "Cursor 로그인 필요"
        case .portConflict: "포트 사용 중"
        case .failed: "오류"
        }
    }

    var isHealthy: Bool {
        if case .healthy = self { return true }
        return false
    }

    var detail: String? {
        if case let .failed(message) = self { return message }
        return nil
    }
}

struct CursorBridgePreferencesStore {
    let home: URL
    let fileManager: FileManager

    init(
        home: URL = FileManager.default.homeDirectoryForCurrentUser,
        fileManager: FileManager = .default)
    {
        self.home = home
        self.fileManager = fileManager
    }

    var stateRoot: URL {
        home.appendingPathComponent(".local/share/gpt-switch", isDirectory: true)
    }

    var preferencesURL: URL {
        stateRoot.appendingPathComponent("cursor-bridge.json")
    }

    func load() throws -> CursorBridgePreferences {
        guard fileManager.fileExists(atPath: preferencesURL.path) else {
            return try CursorBridgePreferences().validated()
        }
        try requireSafeRegularFile(preferencesURL)
        let value: CursorBridgePreferences
        do {
            value = try JSONDecoder().decode(
                CursorBridgePreferences.self,
                from: Data(contentsOf: preferencesURL))
        } catch {
            throw AppError.processFailed("Cursor 브리지 설정을 읽지 못했습니다: \(error.localizedDescription)")
        }
        return try value.validated()
    }

    func save(_ preferences: CursorBridgePreferences) throws {
        let value = try preferences.validated()
        try ensureStateRoot()
        if fileManager.fileExists(atPath: preferencesURL.path) {
            try requireSafeRegularFile(preferencesURL)
        }
        let encoder = JSONEncoder()
        encoder.outputFormatting = [.prettyPrinted, .sortedKeys]
        var data = try encoder.encode(value)
        data.append(0x0A)
        let temporary = stateRoot.appendingPathComponent(".cursor-bridge.\(UUID().uuidString).tmp")
        defer { try? fileManager.removeItem(at: temporary) }
        try data.write(to: temporary, options: [.atomic])
        try fileManager.setAttributes([.posixPermissions: 0o600], ofItemAtPath: temporary.path)
        guard rename(temporary.path, preferencesURL.path) == 0 else {
            throw AppError.processFailed(
                "Cursor 브리지 설정을 원자적으로 저장하지 못했습니다: \(String(cString: strerror(errno)))")
        }
    }

    private func ensureStateRoot() throws {
        if fileManager.fileExists(atPath: home.path) {
            let values = try home.resourceValues(forKeys: [.isDirectoryKey, .isSymbolicLinkKey])
            guard values.isDirectory == true, values.isSymbolicLink != true else {
                throw AppError.processFailed("사용자 홈 경로가 안전한 디렉터리가 아닙니다.")
            }
        } else {
            try fileManager.createDirectory(at: home, withIntermediateDirectories: true)
        }
        let local = home.appendingPathComponent(".local", isDirectory: true)
        let share = local.appendingPathComponent("share", isDirectory: true)
        for directory in [local, share, stateRoot] {
            if fileManager.fileExists(atPath: directory.path) {
                let values = try directory.resourceValues(forKeys: [.isDirectoryKey, .isSymbolicLinkKey])
                guard values.isDirectory == true, values.isSymbolicLink != true else {
                    throw AppError.processFailed("Cursor 브리지 설정 경로가 안전한 디렉터리가 아닙니다.")
                }
            } else {
                try fileManager.createDirectory(
                    at: directory,
                    withIntermediateDirectories: false,
                    attributes: [.posixPermissions: 0o700])
            }
        }
    }

    private func requireSafeRegularFile(_ url: URL) throws {
        let values = try url.resourceValues(forKeys: [.isRegularFileKey, .isSymbolicLinkKey])
        guard values.isRegularFile == true, values.isSymbolicLink != true else {
            throw AppError.processFailed("Cursor 브리지 설정 파일이 안전한 일반 파일이 아닙니다.")
        }
        let attributes = try fileManager.attributesOfItem(atPath: url.path)
        let permissions = (attributes[.posixPermissions] as? NSNumber)?.intValue ?? 0o777
        guard permissions & 0o077 == 0 else {
            throw AppError.processFailed("Cursor 브리지 설정 파일 권한은 0600이어야 합니다.")
        }
    }
}
