import Foundation
import Security

enum SSHSecretKind: String, Sendable {
    case password
    case passphrase
}

protocol SSHSecretStoring: Sendable {
    func save(_ secret: String, credentialID: String, kind: SSHSecretKind) throws
    func read(credentialID: String, kind: SSHSecretKind) throws -> String?
    func delete(credentialID: String, kind: SSHSecretKind) throws
}

struct SystemKeychainStore: SSHSecretStoring {
    static let service = "com.sunggu.codexsyncbar.ssh"

    func save(_ secret: String, credentialID: String, kind: SSHSecretKind) throws {
        guard !secret.isEmpty else {
            try delete(credentialID: credentialID, kind: kind)
            return
        }
        let account = accountName(credentialID: credentialID, kind: kind)
        let base: [String: Any] = [
            kSecClass as String: kSecClassGenericPassword,
            kSecAttrService as String: Self.service,
            kSecAttrAccount as String: account,
            kSecAttrSynchronizable as String: kCFBooleanFalse as Any,
        ]
        let attributes: [String: Any] = [
            kSecValueData as String: Data(secret.utf8),
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

    func read(credentialID: String, kind: SSHSecretKind) throws -> String? {
        let query: [String: Any] = [
            kSecClass as String: kSecClassGenericPassword,
            kSecAttrService as String: Self.service,
            kSecAttrAccount as String: accountName(credentialID: credentialID, kind: kind),
            kSecReturnData as String: true,
            kSecMatchLimit as String: kSecMatchLimitOne,
        ]
        var result: CFTypeRef?
        let status = SecItemCopyMatching(query as CFDictionary, &result)
        if status == errSecItemNotFound { return nil }
        guard status == errSecSuccess, let data = result as? Data else { throw keychainError(status) }
        return String(data: data, encoding: .utf8)
    }

    func delete(credentialID: String, kind: SSHSecretKind) throws {
        let query: [String: Any] = [
            kSecClass as String: kSecClassGenericPassword,
            kSecAttrService as String: Self.service,
            kSecAttrAccount as String: accountName(credentialID: credentialID, kind: kind),
        ]
        let status = SecItemDelete(query as CFDictionary)
        guard status == errSecSuccess || status == errSecItemNotFound else { throw keychainError(status) }
    }

    private func accountName(credentialID: String, kind: SSHSecretKind) -> String {
        "\(credentialID).\(kind.rawValue)"
    }

    private func keychainError(_ status: OSStatus) -> AppError {
        let message = SecCopyErrorMessageString(status, nil) as String? ?? "오류 \(status)"
        return .processFailed("Keychain 작업에 실패했습니다: \(message)")
    }
}
