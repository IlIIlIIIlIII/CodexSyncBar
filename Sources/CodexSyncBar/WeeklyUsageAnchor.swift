import Foundation

struct WeeklyAnchorPreferences: Equatable, Sendable {
    var enabledProfileIDs: Set<Int>

    static let disabled = WeeklyAnchorPreferences(enabledProfileIDs: [])

    func isEnabled(for profileID: Int) -> Bool {
        enabledProfileIDs.contains(profileID)
    }

    mutating func setEnabled(_ enabled: Bool, for profileID: Int) {
        if enabled {
            enabledProfileIDs.insert(profileID)
        } else {
            enabledProfileIDs.remove(profileID)
        }
    }
}

struct WeeklyAnchorRecord: Codable, Equatable, Sendable {
    var nextResetAt: Date?
    var lastHandledResetAt: Date?
    var lastAttemptAt: Date?
    var lastSuccessAt: Date?
    var lastError: String?
    var resetDriftCandidateAt: Date? = nil
    var resetDriftObservationCount: Int? = nil

    static let empty = WeeklyAnchorRecord(
        nextResetAt: nil,
        lastHandledResetAt: nil,
        lastAttemptAt: nil,
        lastSuccessAt: nil,
        lastError: nil,
        resetDriftCandidateAt: nil,
        resetDriftObservationCount: nil)
}

struct WeeklyAnchorStore {
    private let defaults: UserDefaults
    private let enabledKey = "weeklyAnchor.enabledProfileIDs.v1"
    private let recordsKey = "weeklyAnchor.records.v1"

    init(defaults: UserDefaults = .standard) {
        self.defaults = defaults
    }

    func loadPreferences() -> WeeklyAnchorPreferences {
        let ids = defaults.array(forKey: enabledKey) as? [Int] ?? []
        return WeeklyAnchorPreferences(enabledProfileIDs: Set(ids.filter { $0 > 0 }))
    }

    func savePreferences(_ preferences: WeeklyAnchorPreferences) {
        defaults.set(preferences.enabledProfileIDs.sorted(), forKey: enabledKey)
    }

    func loadRecords() -> [Int: WeeklyAnchorRecord] {
        guard let data = defaults.data(forKey: recordsKey),
              let stored = try? JSONDecoder().decode([String: WeeklyAnchorRecord].self, from: data)
        else { return [:] }
        return Dictionary(uniqueKeysWithValues: stored.compactMap { entry in
            guard let id = Int(entry.key), id > 0 else { return nil }
            return (id, entry.value)
        })
    }

    func saveRecords(_ records: [Int: WeeklyAnchorRecord]) {
        let stored = Dictionary(uniqueKeysWithValues: records.map { (String($0.key), $0.value) })
        guard let data = try? JSONEncoder().encode(stored) else { return }
        defaults.set(data, forKey: recordsKey)
    }
}

enum WeeklyAnchorDecision: Equatable {
    case none
    case observe(nextResetAt: Date)
    case confirmResetDrift(observedResetAt: Date)
    case trigger(expectedResetAt: Date?)
    case alreadyActive(nextResetAt: Date?)
}

enum WeeklyAnchorDecisionEngine {
    static let unusedThreshold = 99.5
    static let retryInterval: TimeInterval = 30 * 60
    static let noScheduleSuccessGrace: TimeInterval = 6 * 60 * 60
    static let resetDriftTolerance: TimeInterval = 2 * 60
    static let requiredResetDriftObservations = 2

    private static func retryIsCoolingDown(record: WeeklyAnchorRecord, now: Date) -> Bool {
        guard let lastAttemptAt = record.lastAttemptAt,
              now.timeIntervalSince(lastAttemptAt) < retryInterval
        else { return false }
        // 2.2.1 inherited stdin and warmed the remote plugin catalog. Those
        // local runner failures are fixed in 2.2.2, so do not make the user
        // wait for the old cooldown after updating.
        if let error = record.lastError,
           error.contains("Reading additional input from stdin")
            || error.contains("no_biscuit_no_service")
        {
            return false
        }
        return true
    }

    static func decision(
        enabled: Bool,
        window: UsageWindow?,
        record: WeeklyAnchorRecord,
        now: Date = Date()) -> WeeklyAnchorDecision
    {
        guard enabled else { return .none }
        let currentResetAt = window?.resetsAt

        if let scheduled = record.nextResetAt {
            if scheduled > now {
                if let currentResetAt, currentResetAt > now {
                    let shift = currentResetAt.timeIntervalSince(scheduled)
                    if shift > resetDriftTolerance {
                        guard let remaining = window?.remainingPercent else { return .none }
                        if remaining >= unusedThreshold {
                            let observations = (record.resetDriftObservationCount ?? 0) + 1
                            if observations >= requiredResetDriftObservations {
                                if retryIsCoolingDown(record: record, now: now) {
                                    return .none
                                }
                                return .trigger(expectedResetAt: scheduled)
                            }
                            return .confirmResetDrift(observedResetAt: currentResetAt)
                        }
                        // A user already started the shifted period, so adopt
                        // the new schedule without sending an anchor request.
                        return .observe(nextResetAt: currentResetAt)
                    }
                    if shift < -resetDriftTolerance {
                        return .observe(nextResetAt: currentResetAt)
                    }
                }
                if record.resetDriftObservationCount != nil {
                    return .observe(nextResetAt: scheduled)
                }
                return .none
            }

            if let handled = record.lastHandledResetAt,
               abs(handled.timeIntervalSince(scheduled)) <= 60
            {
                if let currentResetAt, currentResetAt > now {
                    return .observe(nextResetAt: currentResetAt)
                }
                return .none
            }

            if let remaining = window?.remainingPercent, remaining < unusedThreshold {
                return .alreadyActive(nextResetAt: currentResetAt.flatMap { $0 > now ? $0 : nil })
            }

            if retryIsCoolingDown(record: record, now: now) {
                return .none
            }
            return .trigger(expectedResetAt: scheduled)
        }

        guard let remaining = window?.remainingPercent else { return .none }
        if remaining < unusedThreshold {
            if let currentResetAt, currentResetAt > now {
                return .observe(nextResetAt: currentResetAt)
            }
            return .none
        }
        if let lastSuccessAt = record.lastSuccessAt,
           now.timeIntervalSince(lastSuccessAt) < noScheduleSuccessGrace
        {
            if let currentResetAt, currentResetAt > now {
                return .observe(nextResetAt: currentResetAt)
            }
            return .none
        }
        // A successful send can share the same recent lastAttemptAt that is
        // used for failure backoff. Persist the server's newly anchored reset
        // immediately; the cooldown should suppress only another send.
        if retryIsCoolingDown(record: record, now: now) {
            return .none
        }
        // The first opt-in should anchor an account that is currently fully
        // unused instead of waiting as long as another rolling week. After a
        // successful request, the refreshed future reset is observed above.
        return .trigger(expectedResetAt: nil)
    }
}

actor WeeklyUsageAnchorService {
    static let prompt = "Codex SyncBar 주간 주기 시작 확인입니다. 도구를 사용하지 말고 ‘확인’만 답해주세요."

    private let authStore: AuthStore
    private let codexExecutable: URL?
    private let credentialRefresher: (@Sendable (Int, String) async throws -> Void)?
    private let fileManager = FileManager.default

    init(
        authStore: AuthStore,
        codexExecutable: URL? = nil,
        home: URL = FileManager.default.homeDirectoryForCurrentUser,
        credentialRefresher: (@Sendable (Int, String) async throws -> Void)? = nil)
    {
        self.authStore = authStore
        self.codexExecutable = codexExecutable ?? Self.locateCodex(home: home)
        self.credentialRefresher = credentialRefresher
    }

    func send(profileID: Int) async throws -> String {
        let credentials = try await authStore.credentials(for: profileID)
        do {
            return try await sendOnce(credentials: credentials)
        } catch {
            guard let credentialRefresher,
                  AuthenticationFailureClassifier.requiresReauthentication(error.localizedDescription)
            else { throw error }

            // The temporary Codex home intentionally has no refresh token.
            // Rotate only the canonical profile through gpt-switch, then retry
            // once with its newly issued access token.
            try await credentialRefresher(profileID, credentials.accessToken)
            let refreshed = try await authStore.credentials(for: profileID)
            return try await sendOnce(credentials: refreshed)
        }
    }

    private func sendOnce(credentials: ProfileCredentials) async throws -> String {
        guard let codexExecutable,
              fileManager.isExecutableFile(atPath: codexExecutable.path)
        else {
            throw AppError.processFailed("Codex CLI를 찾지 못했습니다. 공식 Codex CLI를 설치해 주세요.")
        }

        let runtime = fileManager.temporaryDirectory
            .appendingPathComponent("CodexSyncBarWeeklyAnchor-\(UUID().uuidString)", isDirectory: true)
        let codexHome = runtime.appendingPathComponent("codex", isDirectory: true)
        let workspace = runtime.appendingPathComponent("workspace", isDirectory: true)
        let resultURL = runtime.appendingPathComponent("response.txt")
        defer { try? fileManager.removeItem(at: runtime) }

        try fileManager.createDirectory(at: codexHome, withIntermediateDirectories: true)
        try fileManager.createDirectory(at: workspace, withIntermediateDirectories: true)
        try fileManager.setAttributes([.posixPermissions: 0o700], ofItemAtPath: runtime.path)
        try fileManager.setAttributes([.posixPermissions: 0o700], ofItemAtPath: codexHome.path)
        try fileManager.setAttributes([.posixPermissions: 0o700], ofItemAtPath: workspace.path)

        var environment = ProcessInfo.processInfo.environment
        environment["CODEX_HOME"] = codexHome.path
        environment["CODEX_SYNCBAR_ACCESS_TOKEN"] = credentials.accessToken
        environment["CODEX_SYNCBAR_ACCOUNT_ID"] = credentials.accountID
        environment.removeValue(forKey: "OPENAI_API_KEY")
        environment.removeValue(forKey: "AZURE_OPENAI_API_KEY")
        environment["PATH"] = "/opt/homebrew/bin:/usr/local/bin:\(FileManager.default.homeDirectoryForCurrentUser.path)/.local/bin:/usr/bin:/bin:/usr/sbin:/sbin"
        environment["NO_COLOR"] = "1"
        let arguments = [
            "exec",
            "--disable", "plugins",
            "--disable", "remote_plugin",
            "--disable", "apps",
            "--ephemeral",
            "--ignore-user-config",
            "--ignore-rules",
            "--skip-git-repo-check",
            "--sandbox", "read-only",
            "--color", "never",
            "--model", "gpt-5.4-mini",
            "--config", "model_provider=\"syncbar_chatgpt\"",
            "--config", "model_providers.syncbar_chatgpt.name=\"ChatGPT SyncBar\"",
            "--config", "model_providers.syncbar_chatgpt.base_url=\"https://chatgpt.com/backend-api/codex\"",
            "--config", "model_providers.syncbar_chatgpt.env_key=\"CODEX_SYNCBAR_ACCESS_TOKEN\"",
            "--config", "model_providers.syncbar_chatgpt.env_http_headers={\"ChatGPT-Account-Id\"=\"CODEX_SYNCBAR_ACCOUNT_ID\"}",
            "--config", "model_providers.syncbar_chatgpt.http_headers={\"originator\"=\"codex_cli_rs\"}",
            "--config", "model_providers.syncbar_chatgpt.wire_api=\"responses\"",
            "--config", "model_providers.syncbar_chatgpt.requires_openai_auth=false",
            "-C", workspace.path,
            "--output-last-message", resultURL.path,
            Self.prompt,
        ]
        let result = try await run(
            executable: codexExecutable,
            arguments: arguments,
            environment: environment)
        let output = result.output.trimmingCharacters(in: .whitespacesAndNewlines)
        guard result.status == 0 else {
            throw AppError.processFailed(
                output.isEmpty ? "주간 주기 시작 메시지를 보내지 못했습니다." : Self.errorSummary(output))
        }
        return (try? String(contentsOf: resultURL, encoding: .utf8)
            .trimmingCharacters(in: .whitespacesAndNewlines)) ?? output
    }

    nonisolated static func locateCodex(home: URL) -> URL? {
        let candidates = [
            URL(fileURLWithPath: "/opt/homebrew/bin/codex"),
            URL(fileURLWithPath: "/usr/local/bin/codex"),
            home.appendingPathComponent(".local/bin/codex"),
        ]
        return candidates.first { FileManager.default.isExecutableFile(atPath: $0.path) }
    }

    nonisolated static func errorSummary(_ output: String) -> String {
        guard output.count > 900 else { return output }
        return "\(output.prefix(350))\n…\n\(output.suffix(550))"
    }

    private func run(
        executable: URL,
        arguments: [String],
        environment: [String: String]) async throws -> ProcessResult
    {
        try await withCheckedThrowingContinuation { continuation in
            let process = Process()
            let pipe = Pipe()
            let completion = WeeklyAnchorProcessCompletion(continuation: continuation)
            process.executableURL = executable
            process.arguments = arguments
            process.environment = environment
            process.standardInput = FileHandle.nullDevice
            process.standardOutput = pipe
            process.standardError = pipe
            process.terminationHandler = { finished in
                completion.finish(status: finished.terminationStatus)
            }
            do {
                try process.run()
                pipe.fileHandleForWriting.closeFile()
                DispatchQueue.global(qos: .utility).async {
                    completion.finish(output: pipe.fileHandleForReading.readDataToEndOfFile())
                }
            } catch {
                completion.fail(error)
            }
        }
    }
}

private final class WeeklyAnchorProcessCompletion: @unchecked Sendable {
    private let lock = NSLock()
    private var continuation: CheckedContinuation<ProcessResult, Error>?
    private var status: Int32?
    private var output: Data?

    init(continuation: CheckedContinuation<ProcessResult, Error>) {
        self.continuation = continuation
    }

    func finish(status: Int32) {
        lock.lock()
        self.status = status
        completeIfReady()
        lock.unlock()
    }

    func finish(output: Data) {
        lock.lock()
        self.output = output
        completeIfReady()
        lock.unlock()
    }

    func fail(_ error: Error) {
        lock.lock()
        let continuation = self.continuation
        self.continuation = nil
        lock.unlock()
        continuation?.resume(throwing: error)
    }

    private func completeIfReady() {
        guard let continuation, let status, let output else { return }
        self.continuation = nil
        continuation.resume(returning: ProcessResult(
            status: status,
            output: String(decoding: output, as: UTF8.self)))
    }
}
