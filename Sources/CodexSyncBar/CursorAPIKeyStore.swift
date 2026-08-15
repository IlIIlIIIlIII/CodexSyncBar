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
            "Cursor API key는 UTF-8 기준 16~1024바이트여야 합니다. 현재: \(actual)바이트"
        case .containsWhitespace:
            "Cursor API key에는 공백 문자를 포함할 수 없습니다."
        case .containsControlCharacter:
            "Cursor API key에는 제어 문자를 포함할 수 없습니다."
        case .containsFormatCharacter:
            "Cursor API key에는 보이지 않는 형식 문자를 포함할 수 없습니다."
        case .containsNUL:
            "Cursor API key에는 NUL 문자를 포함할 수 없습니다."
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
        guard minimumByteCount...maximumByteCount ~= byteCount else {
            throw CursorAPIKeyValidationError.invalidByteCount(actual: byteCount)
        }
        return apiKey
    }
}

protocol CursorAPIKeyStoring: Sendable {
    func save(_ apiKey: String) throws
    func read() throws -> String?
    func delete() throws
}

struct SystemCursorAPIKeyStore: CursorAPIKeyStoring {
    static let service = "com.sunggu.codexsyncbar.cursor"
    static let account = "user-api-key"

    func save(_ apiKey: String) throws {
        let validatedAPIKey = try CursorAPIKeyValidator.validated(apiKey)
        let base: [String: Any] = [
            kSecClass as String: kSecClassGenericPassword,
            kSecAttrService as String: Self.service,
            kSecAttrAccount as String: Self.account,
            kSecAttrSynchronizable as String: kCFBooleanFalse as Any,
        ]
        let attributes: [String: Any] = [
            kSecValueData as String: Data(validatedAPIKey.utf8),
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

    func read() throws -> String? {
        let query: [String: Any] = [
            kSecClass as String: kSecClassGenericPassword,
            kSecAttrService as String: Self.service,
            kSecAttrAccount as String: Self.account,
            kSecAttrSynchronizable as String: kCFBooleanFalse as Any,
            kSecReturnData as String: true,
            kSecMatchLimit as String: kSecMatchLimitOne,
        ]
        var result: CFTypeRef?
        let status = SecItemCopyMatching(query as CFDictionary, &result)
        if status == errSecItemNotFound { return nil }
        guard status == errSecSuccess, let data = result as? Data else {
            throw keychainError(status)
        }
        guard let apiKey = String(data: data, encoding: .utf8) else {
            throw AppError.processFailed("Keychain의 Cursor API key가 UTF-8 형식이 아닙니다.")
        }
        return try CursorAPIKeyValidator.validated(apiKey)
    }

    func delete() throws {
        let query: [String: Any] = [
            kSecClass as String: kSecClassGenericPassword,
            kSecAttrService as String: Self.service,
            kSecAttrAccount as String: Self.account,
            kSecAttrSynchronizable as String: kCFBooleanFalse as Any,
        ]
        let status = SecItemDelete(query as CFDictionary)
        guard status == errSecSuccess || status == errSecItemNotFound else {
            throw keychainError(status)
        }
    }

    private func keychainError(_ status: OSStatus) -> AppError {
        let message = SecCopyErrorMessageString(status, nil) as String? ?? "오류 \(status)"
        return .processFailed("Cursor API key Keychain 작업에 실패했습니다: \(message)")
    }
}
