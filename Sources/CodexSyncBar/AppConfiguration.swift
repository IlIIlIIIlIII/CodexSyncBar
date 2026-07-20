import Foundation
import Darwin

struct AppConfiguration: Codable, Equatable, Sendable {
    var schemaVersion: Int
    var nextAccountID: Int
    var accounts: [AccountProfile]
    var devices: [SSHDeviceConfiguration]

    static let schemaVersion = 1
}

final class AppConfigurationStore: @unchecked Sendable {
    let configurationURL: URL

    private let home: URL
    private let stateRoot: URL
    private let profileDirectory: URL
    private let fileManager: FileManager
    private let encoder: JSONEncoder
    private let decoder = JSONDecoder()
    private let lock = NSLock()

    init(home: URL = FileManager.default.homeDirectoryForCurrentUser) {
        self.home = home
        stateRoot = home.appendingPathComponent(".local/share/gpt-switch", isDirectory: true)
        profileDirectory = stateRoot.appendingPathComponent("profiles", isDirectory: true)
        configurationURL = stateRoot.appendingPathComponent("config.json")
        fileManager = .default
        encoder = JSONEncoder()
        encoder.outputFormatting = [.prettyPrinted, .sortedKeys, .withoutEscapingSlashes]
    }

    func loadOrMigrate(
        controllerLockHeld: Bool = false,
        reconcilePending: Bool = true) throws -> AppConfiguration
    {
        lock.lock()
        defer { lock.unlock() }
        if fileManager.fileExists(atPath: configurationURL.path) {
            var configuration = try loadUnlocked()
            let canMutate = controllerLockHeld || !hasControllerActivity()
            var didChange = false
            // Startup intentionally loads the original rows first so durable
            // device/credential transactions can compare against the exact
            // pre-crash configuration. Backfill only in the post-recovery
            // reconciliation pass.
            if reconcilePending, canMutate {
                didChange = backfillCredentialIDsUnlocked(&configuration)
            }
            if reconcilePending,
               canMutate,
               try reconcilePendingAccountsUnlocked(&configuration)
            {
                didChange = true
            }
            if didChange {
                try saveUnlocked(configuration)
            }
            return configuration
        }
        guard try !hasLegacySwapJournal() else {
            throw AppError.processFailed(
                "중단된 계정 위치 변경을 먼저 복구해야 설정을 마이그레이션할 수 있습니다.")
        }
        try ensureStateRoot()
        let existing = try discoverExistingAccounts()
        let accounts = existing.isEmpty
            ? [AccountProfile(id: 1, email: "로그인 전 계정 1", isPending: true)]
            : existing
        let maximumID = accounts.map(\.id).max() ?? 0
        let configuration = AppConfiguration(
            schemaVersion: AppConfiguration.schemaVersion,
            nextAccountID: maximumID + 1,
            accounts: accounts,
            devices: Self.migratedDevices)
        try saveUnlocked(configuration)
        return configuration
    }

    func load() throws -> AppConfiguration {
        lock.lock()
        defer { lock.unlock() }
        return try loadUnlocked()
    }

    func save(_ configuration: AppConfiguration) throws {
        lock.lock()
        defer { lock.unlock() }
        try saveUnlocked(configuration)
    }

    /// Resolves account reservations left behind by a terminated login flow.
    /// A complete managed auth file commits the row using its signed email;
    /// a reservation with no auth file is removed. The final empty slot is
    /// retained so the registry invariant always leaves one place to log in.
    @discardableResult
    func reconcilePendingAccounts(controllerLockHeld: Bool = false) throws -> AppConfiguration {
        lock.lock()
        defer { lock.unlock() }
        var configuration = try loadUnlocked()
        guard controllerLockHeld || !hasControllerActivity() else { return configuration }
        if try reconcilePendingAccountsUnlocked(&configuration) {
            try saveUnlocked(configuration)
        }
        return configuration
    }

    /// Pending reservations must not be removed while an import or another
    /// controller transaction can still publish its canonical auth file.
    func hasControllerActivity() -> Bool {
        let lockDirectory = stateRoot.appendingPathComponent(".controller-lock")
        if pathEntryExists(lockDirectory) { return true }
        for directoryName in [
            "controller-transactions",
            "login-transactions",
            "device-activation-transactions",
            "credential-transactions",
        ] {
            let directory = stateRoot.appendingPathComponent(directoryName, isDirectory: true)
            guard pathEntryExists(directory) else { continue }
            guard let values = try? directory.resourceValues(forKeys: [.isDirectoryKey, .isSymbolicLinkKey]),
                  values.isDirectory == true,
                  values.isSymbolicLink != true
            else { return true }
            guard let entries = try? fileManager.contentsOfDirectory(
                at: directory,
                includingPropertiesForKeys: nil,
                options: [])
            else { return true }
            if !entries.isEmpty { return true }
        }
        return false
    }

    func reserveAccount() throws -> AccountProfile {
        lock.lock()
        defer { lock.unlock() }
        var configuration = try loadUnlocked()
        let id = configuration.nextAccountID
        guard id > 0 else { throw AppError.processFailed("새 계정 ID를 만들 수 없습니다.") }
        let account = AccountProfile(id: id, email: "로그인 전 계정 \(id)", isPending: true)
        configuration.accounts.append(account)
        configuration.nextAccountID = id + 1
        try saveUnlocked(configuration)
        return account
    }

    func updateAccountEmail(id: Int, email: String) throws {
        lock.lock()
        defer { lock.unlock() }
        var configuration = try loadUnlocked()
        guard let index = configuration.accounts.firstIndex(where: { $0.id == id }) else {
            throw AppError.processFailed("계정 \(id)을 찾을 수 없습니다.")
        }
        let normalized = email.trimmingCharacters(in: .whitespacesAndNewlines)
        guard normalized.contains("@"), !normalized.contains(where: \.isWhitespace) else {
            throw AppError.processFailed("로그인한 계정 이메일을 확인하지 못했습니다.")
        }
        configuration.accounts[index].email = normalized
        configuration.accounts[index].isPending = false
        try saveUnlocked(configuration)
    }

    func updateAccountAlias(id: Int, alias: String?) throws {
        lock.lock()
        defer { lock.unlock() }
        var configuration = try loadUnlocked()
        guard let index = configuration.accounts.firstIndex(where: { $0.id == id }) else {
            throw AppError.processFailed("계정 \(id)을 찾을 수 없습니다.")
        }
        configuration.accounts[index].customAlias = try AccountProfile.normalizedAlias(alias)
        try saveUnlocked(configuration)
    }

    func reorderAccounts(ids: [Int]) throws {
        lock.lock()
        defer { lock.unlock() }
        var configuration = try loadUnlocked()
        guard ids.count == configuration.accounts.count,
              Set(ids).count == ids.count,
              Set(ids) == Set(configuration.accounts.map(\.id))
        else { throw AppError.processFailed("계정 순서가 현재 계정 목록과 일치하지 않습니다.") }
        let byID = Dictionary(uniqueKeysWithValues: configuration.accounts.map { ($0.id, $0) })
        configuration.accounts = try ids.map { id in
            guard let account = byID[id] else {
                throw AppError.processFailed("계정 \(id)을 찾을 수 없습니다.")
            }
            return account
        }
        try saveUnlocked(configuration)
    }

    func removeAccount(id: Int) throws {
        lock.lock()
        defer { lock.unlock() }
        var configuration = try loadUnlocked()
        guard configuration.accounts.count > 1 else {
            throw AppError.processFailed("마지막 계정은 제거할 수 없습니다.")
        }
        guard configuration.accounts.contains(where: { $0.id == id }) else { return }
        configuration.accounts.removeAll { $0.id == id }
        try saveUnlocked(configuration)
    }

    @discardableResult
    func upsertDevice(_ device: SSHDeviceConfiguration) throws -> SSHDeviceConfiguration {
        try device.validate()
        lock.lock()
        defer { lock.unlock() }
        var configuration = try loadUnlocked()
        var persisted = device
        if let index = configuration.devices.firstIndex(where: { $0.id == device.id }) {
            if persisted.credentialID == nil {
                persisted.credentialID = configuration.devices[index].credentialID ?? UUID()
            }
            configuration.devices[index] = persisted
        } else {
            if persisted.credentialID == nil { persisted.credentialID = UUID() }
            configuration.devices.append(persisted)
        }
        try saveUnlocked(configuration)
        return persisted
    }

    func removeDevice(id: String) throws {
        lock.lock()
        defer { lock.unlock() }
        var configuration = try loadUnlocked()
        configuration.devices.removeAll { $0.id == id }
        try saveUnlocked(configuration)
    }

    /// Publishes the enabled bit only when the complete device record still
    /// matches the record that was tested. This keeps endpoint edits from
    /// being overwritten by a delayed bootstrap completion.
    func beginDeviceActivation(_ original: SSHDeviceConfiguration) throws {
        lock.lock()
        defer { lock.unlock() }
        var configuration = try loadUnlocked()
        guard !original.enabled,
              let index = configuration.devices.firstIndex(where: { $0.id == original.id }),
              configuration.devices[index] == original
        else {
            throw AppError.processFailed("SSH 장치 설정이 설치 도중 변경되어 활성화를 중단했습니다.")
        }
        configuration.devices[index].enabled = true
        try saveUnlocked(configuration)
    }

    /// Reverts only the activation bit. A record that differs in any other
    /// field is left untouched and recovery fails closed for manual review.
    func rollbackDeviceActivation(_ original: SSHDeviceConfiguration) throws {
        lock.lock()
        defer { lock.unlock() }
        var configuration = try loadUnlocked()
        guard let index = configuration.devices.firstIndex(where: { $0.id == original.id }) else {
            throw AppError.processFailed("복구할 SSH 장치 설정을 찾지 못했습니다.")
        }
        if configuration.devices[index] == original { return }
        var activated = original
        activated.enabled = true
        guard configuration.devices[index] == activated else {
            throw AppError.processFailed("SSH 장치 설정이 변경되어 자동 비활성화를 중단했습니다.")
        }
        configuration.devices[index] = original
        try saveUnlocked(configuration)
    }

    private func loadUnlocked() throws -> AppConfiguration {
        let values = try configurationURL.resourceValues(forKeys: [.isRegularFileKey, .isSymbolicLinkKey])
        guard values.isRegularFile == true, values.isSymbolicLink != true else {
            throw AppError.processFailed("설정 파일이 안전한 일반 파일이 아닙니다.")
        }
        let attributes = try fileManager.attributesOfItem(atPath: configurationURL.path)
        let permissions = (attributes[.posixPermissions] as? NSNumber)?.intValue
        guard permissions == 0o600 else {
            throw AppError.processFailed("설정 파일 권한은 0600이어야 합니다.")
        }
        let configuration: AppConfiguration
        do {
            configuration = try decoder.decode(AppConfiguration.self, from: Data(contentsOf: configurationURL))
        } catch {
            throw AppError.processFailed("설정 파일을 읽지 못했습니다: \(error.localizedDescription)")
        }
        try validate(configuration)
        return configuration
    }

    private func saveUnlocked(_ configuration: AppConfiguration) throws {
        try validate(configuration)
        try ensureStateRoot()
        let data = try encoder.encode(configuration)
        let temporary = stateRoot.appendingPathComponent(".config.\(UUID().uuidString).tmp")
        defer { try? fileManager.removeItem(at: temporary) }
        try data.write(to: temporary, options: [.atomic])
        try fileManager.setAttributes([.posixPermissions: 0o600], ofItemAtPath: temporary.path)
        if fileManager.fileExists(atPath: configurationURL.path) {
            let values = try configurationURL.resourceValues(forKeys: [.isRegularFileKey, .isSymbolicLinkKey])
            guard values.isRegularFile == true, values.isSymbolicLink != true else {
                throw AppError.processFailed("기존 설정 파일이 안전하지 않아 덮어쓰지 않았습니다.")
            }
            _ = try fileManager.replaceItemAt(configurationURL, withItemAt: temporary)
        } else {
            try fileManager.moveItem(at: temporary, to: configurationURL)
        }
        try fileManager.setAttributes([.posixPermissions: 0o600], ofItemAtPath: configurationURL.path)
    }

    /// schemaVersion 1 initially allowed OpenSSH devices without a Keychain
    /// namespace. Assign one durable identifier per legacy row before any
    /// secret-backed edit can occur, without changing endpoint or enablement.
    @discardableResult
    private func backfillCredentialIDsUnlocked(_ configuration: inout AppConfiguration) -> Bool {
        var used = Set(configuration.devices.compactMap(\.credentialID))
        var didChange = false
        for index in configuration.devices.indices where configuration.devices[index].credentialID == nil {
            var identifier = UUID()
            while used.contains(identifier) { identifier = UUID() }
            used.insert(identifier)
            configuration.devices[index].credentialID = identifier
            didChange = true
        }
        return didChange
    }

    private func validate(_ configuration: AppConfiguration) throws {
        guard configuration.schemaVersion == AppConfiguration.schemaVersion else {
            throw AppError.processFailed("지원하지 않는 설정 버전입니다.")
        }
        let accountIDs = configuration.accounts.map(\.id)
        guard !accountIDs.isEmpty,
              accountIDs.allSatisfy({ $0 > 0 }),
              Set(accountIDs).count == accountIDs.count,
              configuration.nextAccountID > (accountIDs.max() ?? 0)
        else { throw AppError.processFailed("계정 설정이 손상되었습니다.") }
        guard configuration.accounts.allSatisfy({ account in
            let email = account.email.trimmingCharacters(in: .whitespacesAndNewlines)
            return !email.isEmpty && !email.contains(where: { $0.isNewline || $0 == "\t" })
        }) else { throw AppError.processFailed("계정 이메일 설정이 손상되었습니다.") }
        for account in configuration.accounts {
            guard let alias = account.customAlias else { continue }
            guard (try? AccountProfile.normalizedAlias(alias)) == alias else {
                throw AppError.processFailed("계정 별칭 설정이 손상되었습니다.")
            }
        }
        let deviceIDs = configuration.devices.map(\.id)
        guard Set(deviceIDs).count == deviceIDs.count else {
            throw AppError.processFailed("중복된 장치 ID가 있습니다.")
        }
        let credentialIDs = configuration.devices.compactMap(\.credentialID)
        guard Set(credentialIDs).count == credentialIDs.count else {
            throw AppError.processFailed("중복된 SSH Keychain 식별자가 있습니다.")
        }
        try configuration.devices.forEach { try $0.validate(checkFiles: false) }
    }

    private enum PendingAuthState {
        case missing
        case complete(email: String)
    }

    private func reconcilePendingAccountsUnlocked(_ configuration: inout AppConfiguration) throws -> Bool {
        guard configuration.accounts.contains(where: \.isPending) else { return false }

        var resolved: [AccountProfile] = []
        var abandoned: [AccountProfile] = []
        for var account in configuration.accounts {
            guard account.isPending else {
                resolved.append(account)
                continue
            }
            switch try pendingAuthState(profileID: account.id) {
            case .missing:
                abandoned.append(account)
            case let .complete(email):
                account.email = email
                account.isPending = false
                resolved.append(account)
            }
        }

        // A configuration is never allowed to become account-less. Keeping
        // one fresh reservation is preferable to synthesizing/reusing an ID.
        if resolved.isEmpty, let first = abandoned.first {
            resolved.append(first)
        }
        guard resolved != configuration.accounts else { return false }
        configuration.accounts = resolved
        return true
    }

    private func pendingAuthState(profileID: Int) throws -> PendingAuthState {
        let url = profileDirectory.appendingPathComponent("\(profileID).auth.json")
        guard pathEntryExists(url) else { return .missing }

        let values = try url.resourceValues(forKeys: [.isRegularFileKey, .isSymbolicLinkKey])
        guard values.isRegularFile == true, values.isSymbolicLink != true else {
            throw AppError.processFailed("계정 \(profileID)의 대기 중 인증 파일이 안전하지 않습니다.")
        }
        let attributes = try fileManager.attributesOfItem(atPath: url.path)
        let permissions = (attributes[.posixPermissions] as? NSNumber)?.intValue
        let owner = (attributes[.ownerAccountID] as? NSNumber)?.uint32Value
        guard permissions == 0o600, owner == getuid() else {
            throw AppError.processFailed("계정 \(profileID)의 대기 중 인증 파일 권한이 안전하지 않습니다.")
        }
        let object: [String: Any]
        do {
            guard let decoded = try JSONSerialization.jsonObject(with: Data(contentsOf: url)) as? [String: Any] else {
                throw AppError.invalidAuth
            }
            object = decoded
        } catch {
            throw AppError.processFailed("계정 \(profileID)의 대기 중 인증 파일 형식이 올바르지 않습니다.")
        }
        guard object["auth_mode"] as? String == "chatgpt",
              let tokens = object["tokens"] as? [String: Any],
              let idToken = tokens["id_token"] as? String, !idToken.isEmpty,
              let accessToken = tokens["access_token"] as? String, !accessToken.isEmpty,
              let refreshToken = tokens["refresh_token"] as? String, !refreshToken.isEmpty,
              let accountID = tokens["account_id"] as? String, !accountID.isEmpty,
              let email = Self.email(fromAuthAt: url)?.trimmingCharacters(in: .whitespacesAndNewlines),
              email.contains("@"), !email.contains(where: \.isWhitespace)
        else {
            throw AppError.processFailed("계정 \(profileID)의 대기 중 인증 정보가 완전하지 않습니다.")
        }
        return .complete(email: email)
    }

    /// `FileManager.fileExists` follows symlinks and reports a dangling link as
    /// absent. Pending account reconciliation must instead fail closed when an
    /// auth pathname entry exists but is not a safe regular file.
    private func pathEntryExists(_ url: URL) -> Bool {
        var info = stat()
        return url.path.withCString { lstat($0, &info) } == 0
    }

    private func hasLegacySwapJournal() throws -> Bool {
        let appJournal = home
            .appendingPathComponent("Library/Application Support/Codex SyncBar", isDirectory: true)
            .appendingPathComponent("profile-swap-journal.json")
        if fileManager.fileExists(atPath: appJournal.path) { return true }

        guard fileManager.fileExists(atPath: stateRoot.path) else { return false }
        let names = try fileManager.contentsOfDirectory(atPath: stateRoot.path)
        return names.contains { name in
            name.hasPrefix(".swap-profiles.") || name.hasPrefix(".swap-building.")
        }
    }

    private func ensureStateRoot() throws {
        if fileManager.fileExists(atPath: stateRoot.path) {
            let values = try stateRoot.resourceValues(forKeys: [.isDirectoryKey, .isSymbolicLinkKey])
            guard values.isDirectory == true, values.isSymbolicLink != true else {
                throw AppError.processFailed("gpt-switch 상태 경로가 안전하지 않습니다.")
            }
        } else {
            try fileManager.createDirectory(at: stateRoot, withIntermediateDirectories: true)
        }
        try fileManager.setAttributes([.posixPermissions: 0o700], ofItemAtPath: stateRoot.path)
    }

    private func discoverExistingAccounts() throws -> [AccountProfile] {
        guard fileManager.fileExists(atPath: profileDirectory.path) else { return [] }
        let entries = try fileManager.contentsOfDirectory(
            at: profileDirectory,
            includingPropertiesForKeys: [.isRegularFileKey, .isSymbolicLinkKey],
            options: [.skipsHiddenFiles])
        return try entries.compactMap { url -> AccountProfile? in
            let name = url.lastPathComponent
            guard name.hasSuffix(".auth.json"),
                  let id = Int(name.dropLast(".auth.json".count)), id > 0
            else { return nil }
            let values = try url.resourceValues(forKeys: [.isRegularFileKey, .isSymbolicLinkKey])
            guard values.isRegularFile == true, values.isSymbolicLink != true else { return nil }
            let email = Self.email(fromAuthAt: url) ?? "계정 \(id)"
            return AccountProfile(id: id, email: email)
        }.sorted { $0.id < $1.id }
    }

    private static func email(fromAuthAt url: URL) -> String? {
        guard let data = try? Data(contentsOf: url),
              let json = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
              let tokens = json["tokens"] as? [String: Any]
        else { return nil }
        for key in ["id_token", "access_token"] {
            guard let token = tokens[key] as? String else { continue }
            let parts = token.split(separator: ".", omittingEmptySubsequences: false)
            guard parts.count >= 2 else { continue }
            var base64 = String(parts[1]).replacingOccurrences(of: "-", with: "+")
                .replacingOccurrences(of: "_", with: "/")
            base64 += String(repeating: "=", count: (4 - base64.count % 4) % 4)
            guard let payload = Data(base64Encoded: base64),
                  let claims = try? JSONSerialization.jsonObject(with: payload) as? [String: Any]
            else { continue }
            if let email = claims["email"] as? String { return email }
            if let profile = claims["https://api.openai.com/profile"] as? [String: Any],
               let email = profile["email"] as? String
            { return email }
        }
        return nil
    }

    static var migratedDevices: [SSHDeviceConfiguration] {
        []
    }
}
