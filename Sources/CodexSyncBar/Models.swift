import Foundation
import Darwin

struct AccountProfile: Identifiable, Hashable, Codable, Sendable {
    static let maximumAliasLength = 5

    let id: Int
    var email: String
    var customAlias: String?
    var isPending: Bool

    init(id: Int, email: String, alias: String? = nil, isPending: Bool = false) {
        self.id = id
        self.email = email
        customAlias = alias
        self.isPending = isPending
    }

    private enum CodingKeys: String, CodingKey {
        case id
        case email
        case alias
        case isPending
    }

    init(from decoder: Decoder) throws {
        let values = try decoder.container(keyedBy: CodingKeys.self)
        id = try values.decode(Int.self, forKey: .id)
        email = try values.decode(String.self, forKey: .email)
        customAlias = try values.decodeIfPresent(String.self, forKey: .alias)
        // schemaVersion 1 configurations created before account reservations
        // were crash-safe do not contain this field. Those rows represented
        // completed accounts, so missing must decode as false.
        isPending = try values.decodeIfPresent(Bool.self, forKey: .isPending) ?? false
    }

    func encode(to encoder: Encoder) throws {
        var values = encoder.container(keyedBy: CodingKeys.self)
        try values.encode(id, forKey: .id)
        try values.encode(email, forKey: .email)
        try values.encodeIfPresent(customAlias, forKey: .alias)
        try values.encode(isPending, forKey: .isPending)
    }

    var alias: String { customAlias ?? email }

    var shortName: String {
        if let customAlias { return customAlias }
        guard let first = email.first else { return String(id) }
        return String(first).uppercased()
    }

    static func normalizedAlias(_ value: String?) throws -> String? {
        guard let value else { return nil }
        let normalized = value.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !normalized.isEmpty else { return nil }
        guard normalized.count <= maximumAliasLength,
              !normalized.unicodeScalars.contains(where: { scalar in
                  CharacterSet.controlCharacters.contains(scalar) && scalar.value != 0x200D
              })
        else {
            throw AppError.processFailed("계정 별칭은 제어문자 없이 5글자 이하로 입력해 주세요.")
        }
        return normalized
    }
}

struct UsageWindow: Equatable, Sendable {
    let usedPercent: Double
    let resetsAt: Date?
    let durationSeconds: Int?

    var remainingPercent: Double {
        max(0, min(100, 100 - usedPercent))
    }
}

struct UsageSnapshot: Equatable, Sendable {
    let profileID: Int
    let email: String
    let plan: String
    let session: UsageWindow?
    let weekly: UsageWindow?
    let sparkSession: UsageWindow?
    let sparkWeekly: UsageWindow?
    let creditBalance: Double?
    let unlimitedCredits: Bool
    let resetCredits: Int?
    let resetCreditExpirations: [Date]
    let updatedAt: Date

    var menuRemainingPercent: Int? {
        guard let weekly else { return nil }
        return Int(weekly.remainingPercent.rounded())
    }

}

enum UsageState: Equatable, Sendable {
    case idle
    case loading(previous: UsageSnapshot?)
    case loaded(UsageSnapshot)
    case failed(previous: UsageSnapshot?, message: String, loginRequired: Bool)

    var snapshot: UsageSnapshot? {
        switch self {
        case .idle:
            nil
        case let .loading(previous), let .failed(previous, _, _):
            previous
        case let .loaded(snapshot):
            snapshot
        }
    }

    var needsLogin: Bool {
        if case let .failed(_, _, required) = self { return required }
        return false
    }
}

enum ProfileAuthenticationStatus: Equatable, Sendable {
    case checking
    case authenticated
    case reauthenticationRequired
    case unverified

    var title: String {
        switch self {
        case .checking: "인증 확인 중"
        case .authenticated: "인증 정상"
        case .reauthenticationRequired: "재로그인 필요"
        case .unverified: "인증 확인 필요"
        }
    }

    var shortTitle: String {
        switch self {
        case .checking: "확인 중"
        case .authenticated: "정상"
        case .reauthenticationRequired: "재로그인"
        case .unverified: "확인 필요"
        }
    }

    var systemImage: String {
        switch self {
        case .checking: "clock.fill"
        case .authenticated: "checkmark.shield.fill"
        case .reauthenticationRequired: "person.crop.circle.badge.exclamationmark"
        case .unverified: "questionmark.circle.fill"
        }
    }

    var needsReauthentication: Bool {
        self == .reauthenticationRequired
    }

    static func resolve(
        usageState: UsageState,
        knownReauthenticationRequired: Bool) -> ProfileAuthenticationStatus
    {
        if knownReauthenticationRequired || usageState.needsLogin {
            return .reauthenticationRequired
        }
        switch usageState {
        case .idle:
            return .checking
        case let .loading(previous):
            return previous == nil ? .checking : .authenticated
        case .loaded:
            return .authenticated
        case .failed:
            return .unverified
        }
    }
}

enum AuthenticationFailureClassifier {
    /// CLI output that warrants one central access-token refresh and retry.
    /// This must not by itself mark the canonical account as logged out: the
    /// models endpoint can reject an old access token while the managed
    /// refresh token is still healthy.
    static func requiresReauthentication(_ message: String?) -> Bool {
        guard let message else { return false }
        let normalized = message.lowercased()
        return [
            "401 unauthorized",
            "chatgpt login did not make it to this service",
            "login required",
            "failed to refresh token",
            "invalid 'refresh_token'",
            "not chatgpt auth",
            "signed in to another account",
            "access token could not be refreshed",
        ].contains(where: normalized.contains)
    }

    /// Only structured failures from the canonical credential store are
    /// allowed to turn the whole account into a re-login state.
    static func requiresCanonicalReauthentication(_ error: Error) -> Bool {
        guard let appError = error as? AppError else { return false }
        switch appError {
        case .missingAuth, .invalidAuth, .loginRequired:
            return true
        case .network, .invalidResponse, .processFailed, .controllerBusy,
             .controllerRecoveryPending, .loginCancelled:
            return false
        }
    }
}

enum MenuTitleFormatter {
    static func title(
        profile: AccountProfile,
        state: UsageState,
        items: [UsageDisplayItem] = MenuBarUsagePreferences.default.items,
        isRefreshing: Bool,
        hasDeviceMismatch: Bool) -> String
    {
        let label = profile.shortName
        let selectedItems = MenuBarUsagePreferences(items: items).items
        if state.needsLogin { return "\(label) 🔒" }
        if selectedItems.isEmpty {
            return "\(label)\(hasDeviceMismatch ? " !" : "")"
        }
        if isRefreshing, state.snapshot == nil { return "\(label) ···" }
        guard let snapshot = state.snapshot else { return "\(label) —" }

        let fragments = selectedItems.map { item in
            let remaining = item.window(in: snapshot).map {
                Int($0.remainingPercent.rounded())
            }
            return remaining.map { "\($0)%" } ?? "—"
        }
        let needsWarning = selectedItems.compactMap { item in
            item.window(in: snapshot).map { Int($0.remainingPercent.rounded()) }
        }.contains(where: { $0 <= 10 })
        let prefix = needsWarning ? "⚠ " : ""
        return "\(prefix)\(label) \(fragments.joined(separator: " · "))\(hasDeviceMismatch ? " !" : "")"
    }
}

struct DeviceStatus: Identifiable, Equatable, Sendable {
    var id: String { name }
    let name: String
    let configuredDisplayName: String?
    let profileID: Int?
    let accountFingerprint: String?
    let authMode: String?
    let cliState: String?
    let isReachable: Bool

    init(
        name: String,
        configuredDisplayName: String? = nil,
        profileID: Int?,
        accountFingerprint: String?,
        authMode: String?,
        cliState: String?,
        isReachable: Bool)
    {
        self.name = name
        self.configuredDisplayName = configuredDisplayName
        self.profileID = profileID
        self.accountFingerprint = accountFingerprint
        self.authMode = authMode
        self.cliState = cliState
        self.isReachable = isReachable
    }

    var displayName: String {
        if let configuredDisplayName, !configuredDisplayName.isEmpty { return configuredDisplayName }
        return switch name {
        case "macbook": "이 MacBook"
        case "ml": "ML 서버"
        case "rogally": "ROG Ally"
        case "laptop": "노트북"
        default: name
        }
    }
}

enum SSHAuthenticationKind: String, Codable, CaseIterable, Sendable {
    case openSSHConfig
    case privateKey
    case password

    var displayName: String {
        switch self {
        case .openSSHConfig: "기존 OpenSSH 설정"
        case .privateKey: "개인 키 / SSH 인증서"
        case .password: "비밀번호"
        }
    }
}

struct SSHDeviceConfiguration: Identifiable, Codable, Hashable, Sendable {
    var id: String
    /// Random Keychain namespace. It is intentionally independent from the
    /// user-editable SSH endpoint and is optional only for pre-2.0 configs.
    var credentialID: UUID? = nil
    var displayName: String
    var host: String
    var port: Int
    var username: String
    var authentication: SSHAuthenticationKind
    var identityFile: String?
    var certificateFile: String?
    var hasPassword: Bool
    var hasKeyPassphrase: Bool
    var enabled: Bool

    var keychainCredentialKey: String? {
        credentialID?.uuidString.lowercased()
    }

    /// Display-only edits may keep a Keychain credential namespace. Changes
    /// to any SSH endpoint/authentication input must rotate credentialID and
    /// require the corresponding secret to be entered again.
    func hasSameCredentialEndpoint(as other: SSHDeviceConfiguration) -> Bool {
        host == other.host
            && port == other.port
            && username == other.username
            && authentication == other.authentication
            && identityFile == other.identityFile
            && certificateFile == other.certificateFile
    }

    func requiresActivationValidation(
        replacing existing: SSHDeviceConfiguration,
        secretWasMutated: Bool) -> Bool
    {
        !existing.hasSameCredentialEndpoint(as: self) || secretWasMutated
    }

    func validate(checkFiles: Bool = true) throws {
        guard id != "macbook",
              Self.matches(id, pattern: "^[a-z0-9][a-z0-9-]{0,62}$")
        else {
            throw AppError.processFailed("장치 ID는 영문 소문자, 숫자, 하이픈만 사용할 수 있습니다.")
        }
        let cleanName = displayName.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !cleanName.isEmpty, cleanName.count <= 64,
              !cleanName.unicodeScalars.contains(where: {
                  CharacterSet.controlCharacters.contains($0)
              })
        else {
            throw AppError.processFailed("장치 이름은 1~64자로 입력해 주세요.")
        }
        guard Self.matches(host, pattern: "^[A-Za-z0-9][A-Za-z0-9._:-]{0,252}$") else {
            throw AppError.processFailed("SSH 호스트 형식이 올바르지 않습니다.")
        }
        guard Self.matches(username, pattern: "^[A-Za-z0-9_][A-Za-z0-9._-]{0,63}$") else {
            throw AppError.processFailed("SSH 사용자 이름 형식이 올바르지 않습니다.")
        }
        guard (1 ... 65_535).contains(port) else {
            throw AppError.processFailed("SSH 포트는 1~65535 사이여야 합니다.")
        }

        switch authentication {
        case .openSSHConfig:
            break
        case .privateKey:
            if hasKeyPassphrase, credentialID == nil {
                throw AppError.processFailed("키 암호용 Keychain 식별자가 없습니다.")
            }
            guard let identityFile, !identityFile.isEmpty else {
                throw AppError.processFailed("개인 키 파일을 선택해 주세요.")
            }
            if checkFiles {
                try Self.validateCredentialFile(identityFile, privateKey: true)
                if let certificateFile, !certificateFile.isEmpty {
                    try Self.validateCredentialFile(certificateFile, privateKey: false)
                }
            } else {
                guard identityFile.hasPrefix("/") else {
                    throw AppError.processFailed("개인 키 경로는 절대 경로여야 합니다.")
                }
                if let certificateFile, !certificateFile.isEmpty, !certificateFile.hasPrefix("/") {
                    throw AppError.processFailed("SSH 인증서 경로는 절대 경로여야 합니다.")
                }
            }
        case .password:
            guard hasPassword, credentialID != nil else {
                throw AppError.processFailed("SSH 비밀번호를 저장해 주세요.")
            }
        }
    }

    private static func validateCredentialFile(_ path: String, privateKey: Bool) throws {
        guard path.hasPrefix("/") else {
            throw AppError.processFailed("SSH 인증 파일 경로는 절대 경로여야 합니다.")
        }
        let url = URL(fileURLWithPath: path)
        let values = try url.resourceValues(forKeys: [.isRegularFileKey, .isSymbolicLinkKey])
        guard values.isRegularFile == true, values.isSymbolicLink != true else {
            throw AppError.processFailed("SSH 인증 파일은 심볼릭 링크가 아닌 일반 파일이어야 합니다.")
        }
        let attributes = try FileManager.default.attributesOfItem(atPath: path)
        let owner = (attributes[.ownerAccountID] as? NSNumber)?.uint32Value
        guard owner == getuid() else {
            throw AppError.processFailed("SSH 인증 파일은 현재 사용자 소유여야 합니다.")
        }
        if privateKey {
            let permissions = (attributes[.posixPermissions] as? NSNumber)?.intValue ?? 0o777
            guard permissions & 0o077 == 0 else {
                throw AppError.processFailed("개인 키 권한은 0600 이하로 제한해 주세요.")
            }
        }
    }

    private static func matches(_ value: String, pattern: String) -> Bool {
        value.range(of: pattern, options: .regularExpression) != nil
    }
}

enum BannerStyle: Sendable {
    case info
    case success
    case warning
    case error
}

struct AppBanner: Identifiable, Sendable {
    let id = UUID()
    let style: BannerStyle
    let message: String
}

enum AppError: LocalizedError, Sendable {
    case missingAuth(Int)
    case invalidAuth
    case loginRequired(String)
    case network(String)
    case invalidResponse
    case processFailed(String)
    case controllerBusy
    case controllerRecoveryPending(String)
    case loginCancelled

    var errorDescription: String? {
        switch self {
        case let .missingAuth(profile):
            "프로필 \(profile)의 인증 파일이 없습니다."
        case .invalidAuth:
            "인증 파일 형식이 올바르지 않습니다."
        case let .loginRequired(message):
            message
        case let .network(message):
            "네트워크 오류: \(message)"
        case .invalidResponse:
            "사용량 응답을 해석하지 못했습니다."
        case let .processFailed(message):
            message
        case .controllerBusy:
            "다른 계정 작업이 진행 중입니다."
        case let .controllerRecoveryPending(message):
            message
        case .loginCancelled:
            "로그인이 취소되었습니다."
        }
    }
}

enum Formatting {
    static func maskedEmail(_ email: String) -> String {
        guard let at = email.firstIndex(of: "@"), at > email.startIndex else { return email }
        let local = String(email[..<at])
        let domain = String(email[at...])
        guard let first = local.first else { return email }
        return "\(first)\(String(repeating: "•", count: min(4, max(2, local.count - 1))))\(domain)"
    }

    static func resetDescription(_ date: Date?, relativeTo now: Date = Date()) -> String {
        guard let date else { return "초기화 시각 미확인" }
        let interval = date.timeIntervalSince(now)
        if interval <= 0 { return "곧 초기화" }

        let totalMinutes = Int(interval / 60)
        let days = totalMinutes / 1_440
        let hours = (totalMinutes % 1_440) / 60
        let minutes = totalMinutes % 60
        if days > 0 { return "\(days)일 \(hours)시간 후 초기화" }
        if hours > 0 { return "\(hours)시간 \(minutes)분 후 초기화" }
        return "\(max(1, minutes))분 후 초기화"
    }

    static func resetCreditExpiryDescription(_ date: Date, relativeTo now: Date = Date()) -> String {
        let interval = date.timeIntervalSince(now)
        guard interval > 0 else { return "만료됨" }

        let totalMinutes = max(1, Int(interval / 60))
        if interval <= 86_400 {
            return "\(totalMinutes / 60)시간 \(totalMinutes % 60)분"
        }

        let days = totalMinutes / 1_440
        let hours = (totalMinutes % 1_440) / 60
        return "\(days)일 \(hours)시간"
    }

    static func compactResetCreditExpiryDescription(
        _ expirations: [Date],
        relativeTo now: Date = Date()) -> String?
    {
        guard let next = expirations.sorted().first else { return nil }
        let nextDescription = resetCreditExpiryDescription(next, relativeTo: now)
        let remainingCount = expirations.count - 1
        guard remainingCount > 0 else { return "다음 만료 \(nextDescription)" }
        return "다음 만료 \(nextDescription) · 외 \(remainingCount)회"
    }
}
