import Foundation
import Darwin

struct CodexAuthFile: Codable, Sendable {
    var openAIAPIKey: String?
    var authMode: String?
    var lastRefresh: String?
    var tokens: CodexTokens

    enum CodingKeys: String, CodingKey {
        case openAIAPIKey = "OPENAI_API_KEY"
        case authMode = "auth_mode"
        case lastRefresh = "last_refresh"
        case tokens
    }
}

struct CodexTokens: Codable, Sendable {
    var idToken: String?
    var accessToken: String
    var refreshToken: String
    var accountID: String

    enum CodingKeys: String, CodingKey {
        case idToken = "id_token"
        case accessToken = "access_token"
        case refreshToken = "refresh_token"
        case accountID = "account_id"
    }
}

struct ProfileCredentials: Sendable {
    let profileID: Int
    let accessToken: String
    let idToken: String?
    let accountID: String
    let email: String
    let expiresAt: Date?
    let sourceURL: URL
    let profileURL: URL
    let isActiveOnMac: Bool

}

actor AuthStore {
    private let fileManager = FileManager.default
    private let home: URL
    private let switchExecutable: URL
    private let decoder = JSONDecoder()

    private var profileDirectory: URL {
        home.appendingPathComponent(".local/share/gpt-switch/profiles", isDirectory: true)
    }

    private var activeAuthURL: URL {
        home.appendingPathComponent(".codex/auth.json")
    }

    init(
        home: URL = FileManager.default.homeDirectoryForCurrentUser,
        switchExecutable: URL = FileManager.default.homeDirectoryForCurrentUser
            .appendingPathComponent(".local/bin/gpt-switch"))
    {
        self.home = home
        self.switchExecutable = switchExecutable
    }

    func profileURL(for profileID: Int) -> URL {
        profileDirectory.appendingPathComponent("\(profileID).auth.json")
    }

    /// Returns true for any existing canonical artifact, including malformed
    /// files and symlinks. Destructive UI must never treat an unreadable auth
    /// file as if the account had already been logged out.
    func profileArtifactExists(for profileID: Int) -> Bool {
        var info = stat()
        return lstat(profileURL(for: profileID).path, &info) == 0
    }

    func credentials(for profileID: Int) throws -> ProfileCredentials {
        let profileURL = profileURL(for: profileID)
        guard fileManager.fileExists(atPath: profileURL.path) else {
            throw AppError.missingAuth(profileID)
        }

        let profileAuth = try loadAuth(at: profileURL)
        guard Self.isFullManagedAuth(profileAuth) else { throw AppError.invalidAuth }
        let isActive = (try? loadAuth(at: activeAuthURL).tokens.accountID) == profileAuth.tokens.accountID

        return makeCredentials(
            profileID: profileID,
            auth: profileAuth,
            sourceURL: profileURL,
            profileURL: profileURL,
            isActive: isActive)
    }

    func importLoggedInAuth(
        from sourceURL: URL,
        for profileID: Int,
        replaceExisting: Bool = false) throws
    {
        let auth = try loadAuth(at: sourceURL)
        guard Self.isFullManagedAuth(auth) else { throw AppError.invalidAuth }
        for otherProfileID in try existingProfileIDs() where otherProfileID != profileID {
            guard let otherAuth = try? loadAuth(at: profileURL(for: otherProfileID)),
                  Self.isFullManagedAuth(otherAuth)
            else { continue }
            if otherAuth.tokens.accountID == auth.tokens.accountID {
                throw AppError.loginRequired(
                    "이 계정은 이미 \(otherProfileID)번 계정에 연결되어 있습니다. 다른 계정으로 로그인해 주세요.")
            }
        }
        let destination = profileURL(for: profileID)
        if let previous = try? loadAuth(at: destination),
           previous.tokens.accountID != auth.tokens.accountID,
           !replaceExisting
        {
            throw AppError.loginRequired("선택한 프로필과 다른 계정입니다. 올바른 계정으로 다시 로그인해 주세요.")
        }

        try importThroughController(
            sourceURL: sourceURL,
            profileID: profileID,
            replaceExisting: replaceExisting)
        let installed = try loadAuth(at: destination)
        guard Self.isFullManagedAuth(installed),
              installed.tokens.accountID == auth.tokens.accountID
        else { throw AppError.invalidAuth }
    }

    private func loadAuth(at url: URL) throws -> CodexAuthFile {
        guard !url.hasDirectoryPath else { throw AppError.invalidAuth }
        let values = try url.resourceValues(forKeys: [.isSymbolicLinkKey, .isRegularFileKey])
        guard values.isSymbolicLink != true, values.isRegularFile == true else {
            throw AppError.invalidAuth
        }
        let data = try Data(contentsOf: url)
        do {
            return try decoder.decode(CodexAuthFile.self, from: data)
        } catch {
            throw AppError.invalidAuth
        }
    }

    private func importThroughController(
        sourceURL: URL,
        profileID: Int,
        replaceExisting: Bool) throws
    {
        guard fileManager.isExecutableFile(atPath: switchExecutable.path) else {
            throw AppError.processFailed("gpt-switch를 찾을 수 없습니다: \(switchExecutable.path)")
        }
        let input = try FileHandle(forReadingFrom: sourceURL)
        defer { try? input.close() }

        let process = Process()
        let output = Pipe()
        process.executableURL = switchExecutable
        process.arguments = ["import-login", String(profileID)]
            + (replaceExisting ? ["--replace-account"] : [])
        var environment = ProcessInfo.processInfo.environment
        environment["PATH"] = "/opt/homebrew/bin:/usr/local/bin:\(FileManager.default.homeDirectoryForCurrentUser.path)/.local/bin:/usr/bin:/bin:/usr/sbin:/sbin"
        environment["GPT_SWITCH_STATE_ROOT"] = home
            .appendingPathComponent(".local/share/gpt-switch", isDirectory: true).path
        environment["CODEX_HOME"] = home.appendingPathComponent(".codex", isDirectory: true).path
        process.environment = environment
        process.standardInput = input
        process.standardOutput = output
        process.standardError = output

        try process.run()
        let data = output.fileHandleForReading.readDataToEndOfFile()
        process.waitUntilExit()
        guard process.terminationStatus == 0 else {
            let message = String(data: data, encoding: .utf8)?
                .trimmingCharacters(in: .whitespacesAndNewlines)
            throw AppError.processFailed(
                (message?.isEmpty == false ? message : nil)
                    ?? "로그인 인증을 안전하게 반영하지 못했습니다.")
        }
    }

    private func makeCredentials(
        profileID: Int,
        auth: CodexAuthFile,
        sourceURL: URL,
        profileURL: URL,
        isActive: Bool) -> ProfileCredentials
    {
        let claims = Self.jwtClaims(auth.tokens.idToken ?? auth.tokens.accessToken)
        let email = (claims["email"] as? String)
            ?? (claims["https://api.openai.com/profile"] as? [String: Any])?["email"] as? String
            ?? "계정 \(profileID)"
        let expiry = Self.jwtClaims(auth.tokens.accessToken)["exp"] as? TimeInterval
        return ProfileCredentials(
            profileID: profileID,
            accessToken: auth.tokens.accessToken,
            idToken: auth.tokens.idToken,
            accountID: auth.tokens.accountID,
            email: email,
            expiresAt: expiry.map { Date(timeIntervalSince1970: $0) },
            sourceURL: sourceURL,
            profileURL: profileURL,
            isActiveOnMac: isActive)
    }

    private static func jwtClaims(_ token: String) -> [String: Any] {
        let segments = token.split(separator: ".", omittingEmptySubsequences: false)
        guard segments.count >= 2 else { return [:] }
        var base64 = String(segments[1]).replacingOccurrences(of: "-", with: "+")
            .replacingOccurrences(of: "_", with: "/")
        let padding = (4 - base64.count % 4) % 4
        base64 += String(repeating: "=", count: padding)
        guard let data = Data(base64Encoded: base64),
              let json = try? JSONSerialization.jsonObject(with: data) as? [String: Any]
        else { return [:] }
        return json
    }

    private static func isFullManagedAuth(_ auth: CodexAuthFile) -> Bool {
        auth.authMode == "chatgpt"
            && !auth.tokens.accessToken.isEmpty
            && !(auth.tokens.idToken ?? "").isEmpty
            && !auth.tokens.refreshToken.isEmpty
            && !auth.tokens.accountID.isEmpty
    }

    private func existingProfileIDs() throws -> [Int] {
        guard fileManager.fileExists(atPath: profileDirectory.path) else { return [] }
        return try fileManager.contentsOfDirectory(
            at: profileDirectory,
            includingPropertiesForKeys: [.isRegularFileKey, .isSymbolicLinkKey],
            options: [.skipsHiddenFiles])
            .compactMap { url in
                let name = url.lastPathComponent
                guard name.hasSuffix(".auth.json") else { return nil }
                return Int(name.dropLast(".auth.json".count))
            }
            .filter { $0 > 0 }
            .sorted()
    }

}
