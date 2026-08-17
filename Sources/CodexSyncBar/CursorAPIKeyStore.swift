import Foundation
import Security

enum CursorAPIKeyValidationError: LocalizedError, Equatable, Sendable {
    case invalidByteCount(actual: Int)
    case containsWhitespace
    case containsControlCharacter
    case containsFormatCharacter
    case containsNUL

    var errorDescription: String? {
        switch self {
        case let .invalidByteCount(actual):
            "Cursor SDK 자격증명은 UTF-8 기준 16~1024바이트여야 합니다. 현재: \(actual)바이트"
        case .containsWhitespace:
            "Cursor SDK 자격증명에는 공백 문자를 포함할 수 없습니다."
        case .containsControlCharacter:
            "Cursor SDK 자격증명에는 제어 문자를 포함할 수 없습니다."
        case .containsFormatCharacter:
            "Cursor SDK 자격증명에는 보이지 않는 형식 문자를 포함할 수 없습니다."
        case .containsNUL:
            "Cursor SDK 자격증명에는 NUL 문자를 포함할 수 없습니다."
        }
    }
}

enum CursorAPIKeyValidator {
    static let minimumByteCount = 16
    static let maximumByteCount = 1_024

    static func validated(_ apiKey: String) throws -> String {
        let scalars = apiKey.unicodeScalars
        if scalars.contains(where: { $0.value == 0 }) {
            throw CursorAPIKeyValidationError.containsNUL
        }
        if scalars.contains(where: { $0.properties.generalCategory == .format }) {
            throw CursorAPIKeyValidationError.containsFormatCharacter
        }
        if scalars.contains(where: { CharacterSet.whitespacesAndNewlines.contains($0) }) {
            throw CursorAPIKeyValidationError.containsWhitespace
        }
        if scalars.contains(where: { CharacterSet.controlCharacters.contains($0) }) {
            throw CursorAPIKeyValidationError.containsControlCharacter
        }

        let byteCount = apiKey.utf8.count
        guard minimumByteCount ... maximumByteCount ~= byteCount else {
            throw CursorAPIKeyValidationError.invalidByteCount(actual: byteCount)
        }
        return apiKey
    }
}

enum CursorSDKCredentialValidationError: LocalizedError, Equatable, Sendable {
    case invalidSchema
    case invalidEmail
    case invalidExpiration
    case expired
    case invalidLoginResult

    var errorDescription: String? {
        switch self {
        case .invalidSchema:
            "지원하지 않는 Cursor SDK 자격증명 형식입니다."
        case .invalidEmail:
            "Cursor SDK 계정 이메일 형식이 올바르지 않습니다."
        case .invalidExpiration:
            "Cursor SDK 자격증명 만료 시각이 올바르지 않습니다."
        case .expired:
            "Cursor SDK 로그인이 만료되었습니다. Cursor 구독으로 다시 로그인해 주세요."
        case .invalidLoginResult:
            "Cursor SDK 로그인 결과 형식이 올바르지 않습니다."
        }
    }
}

struct CursorSDKCredential: Codable, Equatable, Sendable {
    static let currentSchemaVersion = 1
    static let maximumEncodedBytes = 8 * 1_024

    let schemaVersion: Int
    let apiKey: String
    let email: String?
    let apiKeyExpiresAtMilliseconds: Int64

    init(
        apiKey: String,
        email: String?,
        apiKeyExpiresAtMilliseconds: Int64,
        now: Date = Date()) throws
    {
        schemaVersion = Self.currentSchemaVersion
        self.apiKey = try CursorAPIKeyValidator.validated(apiKey)
        self.email = try Self.validatedEmail(email)
        self.apiKeyExpiresAtMilliseconds = apiKeyExpiresAtMilliseconds
        try validateExpiration(now: now, requireUnexpired: true)
    }

    init(loginResultData: Data, now: Date = Date()) throws {
        struct LoginResult: Decodable {
            let schemaVersion: Int
            let apiKey: String
            let email: String?
            let apiKeyExpiresAtMilliseconds: Int64

            enum CodingKeys: String, CodingKey {
                case schemaVersion = "schema_version"
                case apiKey = "api_key"
                case email
                case apiKeyExpiresAtMilliseconds = "api_key_expires_at_ms"
            }
        }

        guard loginResultData.count <= Self.maximumEncodedBytes,
              let value = try? JSONDecoder().decode(LoginResult.self, from: loginResultData),
              value.schemaVersion == Self.currentSchemaVersion
        else {
            throw CursorSDKCredentialValidationError.invalidLoginResult
        }
        try self.init(
            apiKey: value.apiKey,
            email: value.email,
            apiKeyExpiresAtMilliseconds: value.apiKeyExpiresAtMilliseconds,
            now: now)
    }

    var expiresAt: Date {
        Date(timeIntervalSince1970: Double(apiKeyExpiresAtMilliseconds) / 1_000)
    }

    func isExpired(at date: Date = Date()) -> Bool {
        expiresAt <= date
    }

    func usableAPIKey(at date: Date = Date()) throws -> String {
        try validated(requireUnexpiredAt: date).apiKey
    }

    func validated(requireUnexpiredAt date: Date? = nil) throws -> Self {
        guard schemaVersion == Self.currentSchemaVersion else {
            throw CursorSDKCredentialValidationError.invalidSchema
        }
        _ = try CursorAPIKeyValidator.validated(apiKey)
        _ = try Self.validatedEmail(email)
        try validateExpiration(
            now: date ?? Date(timeIntervalSince1970: 0),
            requireUnexpired: date != nil)
        return self
    }

    private func validateExpiration(now: Date, requireUnexpired: Bool) throws {
        guard apiKeyExpiresAtMilliseconds > 0,
              expiresAt.timeIntervalSince1970.isFinite
        else {
            throw CursorSDKCredentialValidationError.invalidExpiration
        }
        if requireUnexpired, isExpired(at: now) {
            throw CursorSDKCredentialValidationError.expired
        }
    }

    private static func validatedEmail(_ email: String?) throws -> String? {
        guard let email else { return nil }
        guard !email.isEmpty,
              email.utf8.count <= 320,
              email.contains("@"),
              !email.contains(where: { $0.isWhitespace }),
              email.unicodeScalars.allSatisfy({ scalar in
                  !CharacterSet.controlCharacters.contains(scalar)
                      && scalar.properties.generalCategory != .format
              })
        else {
            throw CursorSDKCredentialValidationError.invalidEmail
        }
        return email
    }
}

protocol CursorSDKCredentialStoring: Sendable {
    func save(_ credential: CursorSDKCredential) throws
    func read() throws -> CursorSDKCredential?
    func delete() throws
}

struct SystemCursorSDKCredentialStore: CursorSDKCredentialStoring {
    static let service = "com.sunggu.codexsyncbar.cursor"
    static let account = "sdk-subscription-credential-v1"
    static let legacyAccount = "user-api-key"

    func save(_ credential: CursorSDKCredential) throws {
        let validated = try credential.validated(requireUnexpiredAt: Date())
        let encoder = JSONEncoder()
        encoder.outputFormatting = [.sortedKeys]
        let data = try encoder.encode(validated)
        guard data.count <= CursorSDKCredential.maximumEncodedBytes else {
            throw AppError.processFailed("Cursor SDK 자격증명 데이터가 허용 크기를 초과했습니다.")
        }
        try upsert(data, account: Self.account)
        try delete(account: Self.legacyAccount)
    }

    func read() throws -> CursorSDKCredential? {
        let query = baseQuery(account: Self.account).merging([
            kSecReturnData as String: true,
            kSecMatchLimit as String: kSecMatchLimitOne,
        ]) { _, new in new }
        var result: CFTypeRef?
        let status = SecItemCopyMatching(query as CFDictionary, &result)
        if status == errSecItemNotFound { return nil }
        guard status == errSecSuccess, let data = result as? Data else {
            throw keychainError(status)
        }
        guard data.count <= CursorSDKCredential.maximumEncodedBytes,
              let credential = try? JSONDecoder().decode(CursorSDKCredential.self, from: data)
        else {
            throw AppError.processFailed("Keychain의 Cursor SDK 자격증명 형식이 올바르지 않습니다.")
        }
        return try credential.validated()
    }

    func delete() throws {
        try delete(account: Self.account)
        try delete(account: Self.legacyAccount)
    }

    private func upsert(_ data: Data, account: String) throws {
        let base = baseQuery(account: account)
        let attributes: [String: Any] = [
            kSecValueData as String: data,
            kSecAttrAccessible as String: kSecAttrAccessibleWhenUnlockedThisDeviceOnly,
        ]
        let updateStatus = SecItemUpdate(base as CFDictionary, attributes as CFDictionary)
        if updateStatus == errSecItemNotFound {
            var item = base
            attributes.forEach { item[$0.key] = $0.value }
            let addStatus = SecItemAdd(item as CFDictionary, nil)
            guard addStatus == errSecSuccess else { throw keychainError(addStatus) }
        } else if updateStatus != errSecSuccess {
            throw keychainError(updateStatus)
        }
    }

    private func delete(account: String) throws {
        let status = SecItemDelete(baseQuery(account: account) as CFDictionary)
        guard status == errSecSuccess || status == errSecItemNotFound else {
            throw keychainError(status)
        }
    }

    private func baseQuery(account: String) -> [String: Any] {
        [
            kSecClass as String: kSecClassGenericPassword,
            kSecAttrService as String: Self.service,
            kSecAttrAccount as String: account,
            kSecAttrSynchronizable as String: kCFBooleanFalse as Any,
        ]
    }

    private func keychainError(_ status: OSStatus) -> AppError {
        let message = SecCopyErrorMessageString(status, nil) as String? ?? "오류 \(status)"
        return .processFailed("Cursor SDK 자격증명 Keychain 작업에 실패했습니다: \(message)")
    }
}
