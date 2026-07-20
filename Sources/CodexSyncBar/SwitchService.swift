import Foundation

struct ProcessResult: Sendable {
    let status: Int32
    let output: String
}

struct AuthMaintenanceResult: Sendable, Equatable {
    let didRefresh: Bool
    let didSync: Bool
    let isPartial: Bool
    let output: String
}

struct ProfileLogoutResult: Sendable, Equatable {
    let isPartialCleanup: Bool
    let output: String
}

struct ProfileSlotMap: Sendable, Equatable {
    let firstFingerprint: String
    let secondFingerprint: String
}

struct DeviceBootstrapResult: Sendable, Equatable {
    let deviceID: String
    let activeProfileID: Int
    let output: String
}

actor SwitchService {
    private let executable: URL
    private var maintenanceBusy = false
    private var maintenanceWaiters: [CheckedContinuation<Void, Never>] = []

    init(
        executable: URL = FileManager.default.homeDirectoryForCurrentUser
            .appendingPathComponent(".local/bin/gpt-switch"))
    {
        self.executable = executable
    }

    func fetchStatus() async throws -> [DeviceStatus] {
        await acquireMaintenanceSlot()
        defer { releaseMaintenanceSlot() }
        let result = try await run(arguments: ["status-json"])
        guard result.status == 0 else {
            throw AppError.processFailed(result.output.trimmingCharacters(in: .whitespacesAndNewlines))
        }
        return try Self.parseStatusJSON(result.output)
    }

    func fetchTokenUsage() async throws -> TokenUsageSnapshot {
        await acquireMaintenanceSlot()
        defer { releaseMaintenanceSlot() }
        let result = try await run(arguments: ["usage-summary"])
        guard result.status == 0 else {
            let message = result.output.trimmingCharacters(in: .whitespacesAndNewlines)
            throw AppError.processFailed(message.isEmpty ? "기기 사용량을 수집하지 못했습니다." : message)
        }
        let devices = try result.output.split(whereSeparator: \.isNewline).map { line in
            try JSONDecoder().decode(DeviceTokenUsage.self, from: Data(line.utf8))
        }
        guard !devices.isEmpty else { throw AppError.processFailed("기기 사용량 응답이 비어 있습니다.") }
        return TokenUsageSnapshot(devices: devices, collectedAt: Date())
    }

    /// Running an internal local status check first lets the helper finish
    /// recovery of any node-level journal left by a legacy slot operation.
    func recoverLocalState() async throws {
        await acquireMaintenanceSlot()
        defer { releaseMaintenanceSlot() }
        let result = try await run(arguments: ["__node", "status"])
        guard result.status == 0 else {
            let message = result.output.trimmingCharacters(in: .whitespacesAndNewlines)
            throw AppError.processFailed(
                message.isEmpty ? "로컬 인증 복구 상태를 확인하지 못했습니다." : message)
        }
    }

    /// Recovers durable controller-level login/logout transactions after the
    /// versioned configuration exists. Exit 2 means recovery is valid but an
    /// SSH node is temporarily unavailable, so the app should retry.
    func recoverControllerState() async throws {
        await acquireMaintenanceSlot()
        defer { releaseMaintenanceSlot() }
        let result = try await run(arguments: ["recover-controller"])
        guard result.status == 0 else {
            let message = result.output.trimmingCharacters(in: .whitespacesAndNewlines)
            if message.localizedCaseInsensitiveContains("another controller operation") {
                throw AppError.controllerBusy
            }
            if result.status == 2 {
                var recoveryFields: [String: String]?
                var malformedSummary = false
                for line in message.split(whereSeparator: \.isNewline).reversed() {
                    let raw = String(line)
                    let isSummaryLine = raw.contains("login_recovery=")
                        || raw.contains("logout_recovery=")
                        || raw.contains("overall=")
                    guard isSummaryLine else { continue }
                    guard let parsed = Self.keyValueFields(line),
                          Set(parsed.keys) == Set(["login_recovery", "logout_recovery", "overall"])
                    else {
                        malformedSummary = true
                        break
                    }
                    recoveryFields = parsed
                    break
                }
                if !malformedSummary,
                   recoveryFields?["login_recovery"] == "ok",
                   recoveryFields?["logout_recovery"] == "pending",
                   recoveryFields?["overall"] == "pending"
                {
                    throw AppError.controllerRecoveryPending(
                        message.isEmpty ? "중단된 계정 작업 복구를 다시 시도합니다." : message)
                }
                // Login recovery is local and deterministic; malformed or
                // unknown partial results must fail closed instead of being
                // retried indefinitely.
                throw AppError.processFailed(
                    message.isEmpty ? "중단된 로그인 복구 상태가 모호하여 변경을 차단했습니다." : message)
            }
            throw AppError.processFailed(
                message.isEmpty ? "중단된 계정 작업을 복구하지 못했습니다." : message)
        }
    }

    nonisolated static func isControllerBusy(_ error: Error) -> Bool {
        if case AppError.controllerBusy = error { return true }
        return false
    }

    nonisolated static func isRecoveryPending(_ error: Error) -> Bool {
        if case AppError.controllerRecoveryPending = error { return true }
        return false
    }

    private nonisolated static func keyValueFields<S: StringProtocol>(_ line: S) -> [String: String]? {
        var fields: [String: String] = [:]
        for value in line.split(whereSeparator: \.isWhitespace) {
            guard let separator = value.firstIndex(of: "=") else { continue }
            let key = String(value[..<separator])
            guard fields[key] == nil else { return nil }
            fields[key] = String(value[value.index(after: separator)...])
        }
        return fields
    }

    func switchAll(to profileID: Int, restartApps: Bool = true) async throws -> String {
        await acquireMaintenanceSlot()
        defer { releaseMaintenanceSlot() }
        var arguments = [String(profileID)]
        if !restartApps { arguments.append("--no-restart-app") }
        let result = try await run(arguments: arguments)
        guard result.status == 0 else {
            let message = result.output.trimmingCharacters(in: .whitespacesAndNewlines)
            throw AppError.processFailed(message.isEmpty ? "계정 전환에 실패했습니다." : message)
        }
        return result.output
    }

    func fetchLocalProfileMap() async throws -> ProfileSlotMap {
        await acquireMaintenanceSlot()
        defer { releaseMaintenanceSlot() }
        let result = try await run(arguments: ["__node", "profile-map"])
        let fields = Dictionary(uniqueKeysWithValues: result.output
            .split(whereSeparator: \ .isWhitespace)
            .compactMap { field -> (String, String)? in
                let parts = field.split(separator: "=", maxSplits: 1).map(String.init)
                guard parts.count == 2 else { return nil }
                return (parts[0], parts[1])
            })
        guard result.status == 0,
              let first = fields["profile1_fp"],
              let second = fields["profile2_fp"]
        else {
            let message = result.output.trimmingCharacters(in: .whitespacesAndNewlines)
            throw AppError.processFailed(message.isEmpty ? "로컬 프로필 매핑을 확인하지 못했습니다." : message)
        }
        return ProfileSlotMap(firstFingerprint: first, secondFingerprint: second)
    }

    func reconcileProfileSwap(originalMap: ProfileSlotMap) async throws {
        await acquireMaintenanceSlot()
        defer { releaseMaintenanceSlot() }
        let result = try await run(arguments: [
            "reconcile-profile-swap",
            originalMap.firstFingerprint,
            originalMap.secondFingerprint,
        ])
        guard result.status == 0 else {
            let message = result.output.trimmingCharacters(in: .whitespacesAndNewlines)
            throw AppError.processFailed(
                message.isEmpty ? "등록 장비의 계정 위치 복구를 완료하지 못했습니다." : message)
        }
    }

    func logoutProfile(_ profileID: Int, fallbackProfileID: Int? = nil) async throws -> ProfileLogoutResult {
        await acquireMaintenanceSlot()
        defer { releaseMaintenanceSlot() }
        var arguments = ["logout", String(profileID)]
        if let fallbackProfileID {
            arguments += ["--fallback", String(fallbackProfileID)]
        }
        let result = try await run(arguments: arguments)
        guard result.status == 0 || result.status == 2 else {
            let message = result.output.trimmingCharacters(in: .whitespacesAndNewlines)
            throw AppError.processFailed(message.isEmpty ? "프로필 로그아웃을 완료하지 못했습니다." : message)
        }
        return ProfileLogoutResult(
            isPartialCleanup: result.status == 2,
            output: result.output)
    }

    func refreshAuthIfNeeded(profileID: Int? = nil) async throws -> AuthMaintenanceResult {
        try await runAuthMaintenance(arguments: ["refresh-if-needed", profileID.map(String.init) ?? "all"])
    }

    func forceRefreshAuth(
        profileID: Int,
        expectedAccessToken: String? = nil) async throws -> AuthMaintenanceResult
    {
        await acquireMaintenanceSlot()
        defer { releaseMaintenanceSlot() }

        if let expectedAccessToken,
           let current = Self.canonicalAccessToken(profileID: profileID),
           current != expectedAccessToken
        {
            return AuthMaintenanceResult(
                didRefresh: false,
                didSync: false,
                isPartial: false,
                output: "profile=\(profileID) action=noop reason=credential-changed result=ok")
        }
        return try await executeAuthMaintenance(arguments: ["refresh", String(profileID)])
    }

    func syncAuth(profileID: Int? = nil) async throws -> AuthMaintenanceResult {
        try await runAuthMaintenance(arguments: ["sync-access", profileID.map(String.init) ?? "all"])
    }

    func testDevice(id: String) async throws -> String {
        await acquireMaintenanceSlot()
        defer { releaseMaintenanceSlot() }
        let result = try await run(arguments: ["test-device", id])
        guard result.status == 0 else {
            let message = result.output.trimmingCharacters(in: .whitespacesAndNewlines)
            throw AppError.processFailed(message.isEmpty ? "SSH 연결 테스트에 실패했습니다." : message)
        }
        return result.output
    }

    func bootstrapDevice(id: String) async throws -> DeviceBootstrapResult {
        await acquireMaintenanceSlot()
        defer { releaseMaintenanceSlot() }
        let result = try await run(arguments: ["bootstrap-device", id])
        guard result.status == 0 else {
            let message = result.output.trimmingCharacters(in: .whitespacesAndNewlines)
            throw AppError.processFailed(
                message.isEmpty ? "SSH 장치 초기 설치에 실패했습니다." : message)
        }
        return try Self.parseBootstrapResult(result.output, expectedDeviceID: id)
    }

    /// SSH can add banners or warnings to the combined output. Only the last
    /// bootstrap summary-shaped line is accepted, and duplicate fields fail
    /// closed instead of trapping in Dictionary(uniqueKeysWithValues:).
    nonisolated static func parseBootstrapResult(
        _ output: String,
        expectedDeviceID: String) throws -> DeviceBootstrapResult
    {
        for line in output.split(whereSeparator: \.isNewline).reversed() {
            var fields: [String: String] = [:]
            var sawSummaryField = false
            var malformed = false
            for token in line.split(whereSeparator: \.isWhitespace) {
                guard let separator = token.firstIndex(of: "=") else { continue }
                let key = String(token[..<separator])
                guard ["device", "result", "active", "profiles", "version"].contains(key) else {
                    continue
                }
                sawSummaryField = true
                guard fields[key] == nil else {
                    malformed = true
                    break
                }
                fields[key] = String(token[token.index(after: separator)...])
            }
            guard sawSummaryField else { continue }
            guard !malformed,
                  fields["device"] == expectedDeviceID,
                  fields["result"] == "ok",
                  let active = fields["active"].flatMap(Int.init), active > 0,
                  let profileCount = fields["profiles"].flatMap(Int.init), profileCount > 0,
                  let version = fields["version"], !version.isEmpty
            else {
                throw AppError.processFailed("SSH 장치 설치 검증 응답이 올바르지 않습니다.")
            }
            return DeviceBootstrapResult(
                deviceID: expectedDeviceID,
                activeProfileID: active,
                output: output)
        }
        throw AppError.processFailed("SSH 장치 설치 검증 응답을 찾지 못했습니다.")
    }

    nonisolated static func bootstrapActivationIsConsistent(
        statuses: [DeviceStatus],
        deviceID: String,
        activeProfileID: Int) -> Bool
    {
        guard let macbook = statuses.first(where: { $0.name == "macbook" }),
              macbook.isReachable,
              macbook.profileID == activeProfileID,
              let enrolled = statuses.first(where: { $0.name == deviceID }),
              enrolled.isReachable,
              enrolled.profileID == activeProfileID
        else { return false }
        return true
    }

    private func runAuthMaintenance(arguments: [String]) async throws -> AuthMaintenanceResult {
        await acquireMaintenanceSlot()
        defer { releaseMaintenanceSlot() }
        return try await executeAuthMaintenance(arguments: arguments)
    }

    private func executeAuthMaintenance(arguments: [String]) async throws -> AuthMaintenanceResult {
        let result = try await run(arguments: arguments)
        let output = result.output.trimmingCharacters(in: .whitespacesAndNewlines)
        guard result.status == 0 || result.status == 2 else {
            let lowercased = output.lowercased()
            if lowercased.contains("login required") || lowercased.contains("not chatgpt auth") {
                throw AppError.loginRequired("중앙 갱신 토큰을 사용할 수 없습니다. 이 계정을 한 번 다시 로그인해 주세요.")
            }
            throw AppError.processFailed(output.isEmpty ? "중앙 인증 갱신에 실패했습니다." : output)
        }
        return Self.parseAuthMaintenance(output: output, exitStatus: result.status)
    }

    private func acquireMaintenanceSlot() async {
        if !maintenanceBusy {
            maintenanceBusy = true
            return
        }
        await withCheckedContinuation { continuation in
            maintenanceWaiters.append(continuation)
        }
    }

    private func releaseMaintenanceSlot() {
        guard !maintenanceWaiters.isEmpty else {
            maintenanceBusy = false
            return
        }
        maintenanceWaiters.removeFirst().resume()
    }

    private func run(arguments: [String]) async throws -> ProcessResult {
        guard FileManager.default.isReadableFile(atPath: executable.path) else {
            throw AppError.processFailed("gpt-switch를 찾을 수 없습니다: \(executable.path)")
        }

        return try await withCheckedThrowingContinuation { continuation in
            let process = Process()
            let pipe = Pipe()
            let completion = ProcessExecutionCompletion(continuation: continuation)

            // Locally installed helper scripts can inherit macOS provenance
            // metadata from the app bundle. Launching such a script directly
            // may be rejected with SIGKILL even though its contents are safe
            // and readable. Invoke the Bash script explicitly so execution is
            // stable across app updates and provenance changes.
            process.executableURL = URL(fileURLWithPath: "/bin/bash")
            process.arguments = [executable.path] + arguments
            var environment = ProcessInfo.processInfo.environment
            environment["PATH"] = "/opt/homebrew/bin:/usr/local/bin:\(FileManager.default.homeDirectoryForCurrentUser.path)/.local/bin:/usr/bin:/bin:/usr/sbin:/sbin"
            process.environment = environment
            process.standardOutput = pipe
            process.standardError = pipe
            process.terminationHandler = { finished in
                completion.finish(status: finished.terminationStatus)
            }

            do {
                try process.run()
                // One reader owns the pipe through EOF. This both drains
                // verbose SSH output continuously and avoids the previous
                // termination/readability race that could lose the summary.
                pipe.fileHandleForWriting.closeFile()
                DispatchQueue.global(qos: .utility).async {
                    let data = pipe.fileHandleForReading.readDataToEndOfFile()
                    completion.finish(output: data)
                }
            } catch {
                completion.fail(error)
            }
        }
    }

    static func parseStatus(_ output: String) -> [DeviceStatus] {
        output.split(whereSeparator: \ .isNewline).compactMap { line -> DeviceStatus? in
            let fields = line.split(whereSeparator: \ .isWhitespace).map(String.init)
            guard fields.count >= 6 else { return nil }
            let name = fields[0]
            guard !["NODE", "---------"].contains(name) else { return nil }
            let profile = Int(fields[1])
            let reachable = fields[1] != "unreachable" && fields[1] != "error"
            return DeviceStatus(
                name: name,
                profileID: profile,
                accountFingerprint: fields[2] == "unknown" ? nil : fields[2],
                authMode: fields[4] == "unknown" ? nil : fields[4],
                cliState: fields[5] == "unknown" ? nil : fields[5],
                isReachable: reachable)
        }
    }

    static func parseStatusJSON(_ output: String) throws -> [DeviceStatus] {
        try output.split(whereSeparator: \.isNewline).map { line in
            let item = try JSONDecoder().decode(StatusItem.self, from: Data(line.utf8))
            return DeviceStatus(
                name: item.id,
                configuredDisplayName: item.displayName,
                profileID: item.profileID,
                accountFingerprint: item.accountFingerprint,
                authMode: item.authMode,
                cliState: item.cliState,
                isReachable: item.isReachable)
        }
    }

    static func parseAuthMaintenance(output: String, exitStatus: Int32) -> AuthMaintenanceResult {
        let lowercased = output.lowercased()
        let syncedCount = lowercased
            .split(whereSeparator: \ .isWhitespace)
            .compactMap { field -> Int? in
                guard field.hasPrefix("synced=") else { return nil }
                return Int(field.dropFirst("synced=".count))
            }
            .max() ?? 0
        return AuthMaintenanceResult(
            didRefresh: lowercased.contains("action=refreshed") || lowercased.contains("refreshed=true"),
            didSync: syncedCount > 0 || lowercased.contains("sync=ok") || lowercased.contains("action=synced"),
            isPartial: exitStatus == 2 || lowercased.contains("result=partial"),
            output: output)
    }

    private static func canonicalAccessToken(profileID: Int) -> String? {
        let url = FileManager.default.homeDirectoryForCurrentUser
            .appendingPathComponent(".local/share/gpt-switch/profiles/\(profileID).auth.json")
        guard let data = try? Data(contentsOf: url),
              let json = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
              let tokens = json["tokens"] as? [String: Any]
        else { return nil }
        return tokens["access_token"] as? String
    }
}

private struct StatusItem: Decodable {
    let id: String
    let displayName: String
    let profileID: Int?
    let accountFingerprint: String?
    let authMode: String?
    let cliState: String?
    let isReachable: Bool
}

/// Process termination and pipe EOF are independent signals. Joining them
/// under one lock avoids both dropped tail output and waitUntilExit races.
private final class ProcessExecutionCompletion: @unchecked Sendable {
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
        let completed = takeCompletedResultLocked()
        lock.unlock()
        if let (continuation, result) = completed {
            continuation.resume(returning: result)
        }
    }

    func finish(output: Data) {
        lock.lock()
        self.output = output
        let completed = takeCompletedResultLocked()
        lock.unlock()
        if let (continuation, result) = completed {
            continuation.resume(returning: result)
        }
    }

    func fail(_ error: Error) {
        lock.lock()
        let continuation = self.continuation
        self.continuation = nil
        lock.unlock()
        continuation?.resume(throwing: error)
    }

    private func takeCompletedResultLocked()
        -> (CheckedContinuation<ProcessResult, Error>, ProcessResult)?
    {
        guard let continuation, let status, let output else { return nil }
        self.continuation = nil
        return (
            continuation,
            ProcessResult(
                status: status,
                output: String(data: output, encoding: .utf8) ?? ""))
    }
}
