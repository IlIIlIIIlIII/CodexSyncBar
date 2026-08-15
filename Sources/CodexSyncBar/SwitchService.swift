import Darwin
import Foundation
import Security

struct ProcessResult: Sendable {
    let status: Int32
    let output: String
}

struct AuthMaintenanceResult: Sendable, Equatable {
    let didRefresh: Bool
    let didSync: Bool
    let didDefer: Bool
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
    private struct TrustedProvisioningLaunch {
        let directory: URL
        let executable: URL
        let environment: [String: String]

        func cleanup() {
            try? FileManager.default.removeItem(at: directory)
        }
    }

    private struct ProvisioningHelperBytes {
        let data: Data
    }

    private static let maximumProvisioningHelperBytes = 4 * 1024 * 1024
    private let executable: URL
    private let trustedProvisioningExecutable: URL?
    private let installedCursorRemoteManager: URL
    private let installedCursorBridgeHelper: URL
    private let trustedCursorRemoteManager: URL?
    private let trustedCursorBridgeHelper: URL?
    private var maintenanceBusy = false
    private var maintenanceWaiters: [CheckedContinuation<Void, Never>] = []

    init(
        executable: URL = FileManager.default.homeDirectoryForCurrentUser
            .appendingPathComponent(".local/bin/gpt-switch"),
        trustedProvisioningExecutable: URL? = Bundle.main.resourceURL?
            .appendingPathComponent("gpt-switch"),
        installedCursorRemoteManager: URL? = nil,
        installedCursorBridgeHelper: URL? = nil,
        trustedCursorRemoteManager: URL? = nil,
        trustedCursorBridgeHelper: URL? = nil)
    {
        self.executable = executable
        self.trustedProvisioningExecutable = trustedProvisioningExecutable
        let installedLibrary = executable.deletingLastPathComponent()
            .deletingLastPathComponent()
            .appendingPathComponent("lib/gpt-switch", isDirectory: true)
        self.installedCursorRemoteManager = installedCursorRemoteManager
            ?? installedLibrary.appendingPathComponent("cursor-remote-manager.mjs")
        self.installedCursorBridgeHelper = installedCursorBridgeHelper
            ?? installedLibrary.appendingPathComponent("cursor-codex-bridge.mjs")
        let trustedResources = trustedProvisioningExecutable?.deletingLastPathComponent()
        self.trustedCursorRemoteManager = trustedCursorRemoteManager
            ?? trustedResources?.appendingPathComponent("cursor-remote-manager.mjs")
        self.trustedCursorBridgeHelper = trustedCursorBridgeHelper
            ?? trustedResources?.appendingPathComponent("cursor-codex-bridge.mjs")
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

    func reloadLocalCodexConfiguration() async throws {
        await acquireMaintenanceSlot()
        defer { releaseMaintenanceSlot() }
        let result = try await run(arguments: ["__node", "stop-clients"])
        guard result.status == 0 else {
            let message = result.output.trimmingCharacters(in: .whitespacesAndNewlines)
            throw AppError.processFailed(
                message.isEmpty ? "Codex 설정 프로세스를 다시 불러오지 못했습니다." : message)
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
        try await runAuthMaintenance(arguments: [
            "refresh-if-needed", profileID.map(String.init) ?? "all", "--no-restart-app",
        ])
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
                didDefer: false,
                isPartial: false,
                output: "profile=\(profileID) action=noop reason=credential-changed result=ok")
        }
        return try await executeAuthMaintenance(arguments: [
            "refresh", String(profileID), "--no-restart-app",
        ])
    }

    func syncAuth(profileID: Int? = nil) async throws -> AuthMaintenanceResult {
        try await runAuthMaintenance(arguments: [
            "sync-access", profileID.map(String.init) ?? "all", "--no-restart-app",
        ])
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

    func provisionCursor(
        deviceID: String,
        request: CursorRemoteProvisioningRequest) async throws -> CursorRemoteProvisioningResult
    {
        await acquireMaintenanceSlot()
        defer { releaseMaintenanceSlot() }
        let trustedLaunch = try validatedProvisioningLaunch()
        defer { trustedLaunch.cleanup() }
        let encoder = JSONEncoder()
        encoder.outputFormatting = [.sortedKeys, .withoutEscapingSlashes]
        let input = try encoder.encode(request)
        let result = try await run(
            arguments: ["provision-cursor", deviceID],
            input: input,
            executableOverride: trustedLaunch.executable,
            environmentOverrides: trustedLaunch.environment,
            inheritsEnvironment: false)
        let output = result.output.trimmingCharacters(in: .whitespacesAndNewlines)
        guard result.status == 0 else {
            // The helper receives the raw API key on stdin. Never promote its
            // untrusted output into a user-visible error, even if a future or
            // locally modified helper accidentally echoes its input.
            throw AppError.processFailed("SSH 장치의 Cursor 설치와 인증에 실패했습니다.")
        }
        let version = try Self.parseCursorProvisioningResult(
            output,
            expectedDeviceID: deviceID)
        let normalized = Self.normalizedCursorResult(
            deviceID: deviceID,
            cursorState: "provisioned",
            version: version)
        return CursorRemoteProvisioningResult(deviceID: deviceID, output: normalized)
    }

    func deprovisionCursor(deviceID: String) async throws -> CursorRemoteDeprovisioningResult {
        await acquireMaintenanceSlot()
        defer { releaseMaintenanceSlot() }
        let trustedLaunch = try validatedProvisioningLaunch()
        defer { trustedLaunch.cleanup() }
        let result = try await run(
            arguments: ["deprovision-cursor", deviceID],
            executableOverride: trustedLaunch.executable,
            environmentOverrides: trustedLaunch.environment,
            inheritsEnvironment: false)
        let output = result.output.trimmingCharacters(in: .whitespacesAndNewlines)
        guard result.status == 0 else {
            throw AppError.processFailed("SSH 장치의 Cursor provider 해제에 실패했습니다.")
        }
        let version = try Self.parseCursorDeprovisioningResult(
            output,
            expectedDeviceID: deviceID)
        let normalized = Self.normalizedCursorResult(
            deviceID: deviceID,
            cursorState: "deprovisioned",
            version: version)
        return CursorRemoteDeprovisioningResult(deviceID: deviceID, output: normalized)
    }

    nonisolated static func parseCursorProvisioningResult(
        _ output: String,
        expectedDeviceID: String) throws -> String
    {
        try parseCursorResult(
            output,
            expectedDeviceID: expectedDeviceID,
            expectedCursorState: "provisioned",
            invalidMessage: "SSH Cursor 설치 검증 응답이 올바르지 않습니다.",
            missingMessage: "SSH Cursor 설치 검증 응답을 찾지 못했습니다.")
    }

    nonisolated static func parseCursorDeprovisioningResult(
        _ output: String,
        expectedDeviceID: String) throws -> String
    {
        try parseCursorResult(
            output,
            expectedDeviceID: expectedDeviceID,
            expectedCursorState: "deprovisioned",
            invalidMessage: "SSH Cursor 해제 검증 응답이 올바르지 않습니다.",
            missingMessage: "SSH Cursor 해제 검증 응답을 찾지 못했습니다.")
    }

    private nonisolated static func parseCursorResult(
        _ output: String,
        expectedDeviceID: String,
        expectedCursorState: String,
        invalidMessage: String,
        missingMessage: String) throws -> String
    {
        for line in output.split(whereSeparator: \.isNewline).reversed() {
            var fields: [String: String] = [:]
            var malformed = false
            for token in line.split(whereSeparator: \.isWhitespace) {
                guard let separator = token.firstIndex(of: "=") else {
                    malformed = true
                    break
                }
                let key = String(token[..<separator])
                guard ["device", "cursor", "result", "version"].contains(key),
                      fields[key] == nil
                else {
                    malformed = true
                    break
                }
                fields[key] = String(token[token.index(after: separator)...])
            }
            guard !fields.isEmpty else { continue }
            guard !malformed,
                  Set(fields.keys) == Set(["device", "cursor", "result", "version"]),
                  fields["device"] == expectedDeviceID,
                  fields["cursor"] == expectedCursorState,
                  fields["result"] == "ok",
                  let version = fields["version"], !version.isEmpty
            else {
                throw AppError.processFailed(invalidMessage)
            }
            return version
        }
        throw AppError.processFailed(missingMessage)
    }

    private nonisolated static func normalizedCursorResult(
        deviceID: String,
        cursorState: String,
        version: String) -> String
    {
        "device=\(deviceID) cursor=\(cursorState) result=ok version=\(version)\n"
    }

    private func validatedProvisioningLaunch() throws
        -> TrustedProvisioningLaunch
    {
        guard let trustedProvisioningExecutable,
              let trustedCursorRemoteManager,
              let trustedCursorBridgeHelper
        else {
            throw AppError.processFailed(
                "앱 번들의 Cursor helper를 찾지 못해 Cursor 자격증명을 전달하지 않았습니다.")
        }
        try Self.validateMainBundleSignatureIfRequired(trustedHelpers: [
            trustedProvisioningExecutable,
            trustedCursorRemoteManager,
            trustedCursorBridgeHelper,
        ])
        let executableBytes = try Self.validatedProvisioningHelperBytes(
            installed: executable,
            trusted: trustedProvisioningExecutable,
            name: "gpt-switch")
        let managerBytes = try Self.validatedProvisioningHelperBytes(
            installed: installedCursorRemoteManager,
            trusted: trustedCursorRemoteManager,
            name: "cursor-remote-manager.mjs")
        let bridgeBytes = try Self.validatedProvisioningHelperBytes(
            installed: installedCursorBridgeHelper,
            trusted: trustedCursorBridgeHelper,
            name: "cursor-codex-bridge.mjs")
        // Re-check the signed bundle after opening and reading every resource.
        // A bundle that changed during validation fails closed before the
        // secret-bearing child process is created.
        try Self.validateMainBundleSignatureIfRequired(trustedHelpers: [
            trustedProvisioningExecutable,
            trustedCursorRemoteManager,
            trustedCursorBridgeHelper,
        ])

        let snapshotDirectory = try Self.makeProvisioningSnapshotDirectory()
        do {
            let snapshotExecutable = snapshotDirectory.appendingPathComponent("gpt-switch")
            let snapshotManager = snapshotDirectory.appendingPathComponent("cursor-remote-manager.mjs")
            let snapshotBridge = snapshotDirectory.appendingPathComponent("cursor-codex-bridge.mjs")
            try Self.writeProvisioningSnapshot(executableBytes.data, to: snapshotExecutable)
            try Self.writeProvisioningSnapshot(managerBytes.data, to: snapshotManager)
            try Self.writeProvisioningSnapshot(bridgeBytes.data, to: snapshotBridge)

            var environment = Self.provisioningEnvironmentAllowlist()
            environment["GPT_SWITCH_CURSOR_REMOTE_MANAGER"] = snapshotManager.path
            environment["GPT_SWITCH_CURSOR_BRIDGE_HELPER"] = snapshotBridge.path
            return TrustedProvisioningLaunch(
                directory: snapshotDirectory,
                executable: snapshotExecutable,
                environment: environment)
        } catch {
            try? FileManager.default.removeItem(at: snapshotDirectory)
            throw error
        }
    }

    nonisolated static func validateProvisioningHelper(
        installed: URL,
        trusted: URL,
        name: String = "gpt-switch") throws
    {
        _ = try validatedProvisioningHelperBytes(
            installed: installed,
            trusted: trusted,
            name: name)
    }

    private nonisolated static func validatedProvisioningHelperBytes(
        installed: URL,
        trusted: URL,
        name: String) throws -> ProvisioningHelperBytes
    {
        let installedData = try securelyReadProvisioningHelper(
            installed,
            allowedOwners: [getuid()],
            unsafeMessage: "설치된 \(name)이 안전한 사용자 소유 파일이 아니어서 Cursor 자격증명을 전달하지 않았습니다.")
        let trustedData = try securelyReadProvisioningHelper(
            trusted,
            allowedOwners: [getuid(), 0],
            unsafeMessage: "앱 번들의 \(name)이 안전하지 않아 Cursor 자격증명을 전달하지 않았습니다.")
        guard installedData == trustedData else {
            throw AppError.processFailed(
                "설치된 \(name)이 앱 번들과 일치하지 않아 Cursor 자격증명을 전달하지 않았습니다. 앱을 다시 열어 주세요.")
        }
        return ProvisioningHelperBytes(data: trustedData)
    }

    private nonisolated static func securelyReadProvisioningHelper(
        _ url: URL,
        allowedOwners: Set<uid_t>,
        unsafeMessage: String) throws -> Data
    {
        let descriptor = url.path.withCString {
            Darwin.open($0, O_RDONLY | O_CLOEXEC | O_NOFOLLOW)
        }
        guard descriptor >= 0 else { throw AppError.processFailed(unsafeMessage) }
        defer { Darwin.close(descriptor) }

        var before = stat()
        guard fstat(descriptor, &before) == 0,
              (before.st_mode & S_IFMT) == S_IFREG,
              allowedOwners.contains(before.st_uid),
              [mode_t(0o700), mode_t(0o755)].contains(before.st_mode & 0o777),
              before.st_nlink == 1,
              before.st_size > 0,
              before.st_size <= off_t(maximumProvisioningHelperBytes)
        else {
            throw AppError.processFailed(unsafeMessage)
        }

        var data = Data()
        data.reserveCapacity(Int(before.st_size))
        var buffer = [UInt8](repeating: 0, count: 64 * 1024)
        while true {
            let count = buffer.withUnsafeMutableBytes { storage in
                Darwin.read(descriptor, storage.baseAddress, storage.count)
            }
            if count == 0 { break }
            guard count > 0 else {
                if errno == EINTR { continue }
                throw AppError.processFailed(unsafeMessage)
            }
            guard data.count + count <= maximumProvisioningHelperBytes else {
                throw AppError.processFailed(unsafeMessage)
            }
            data.append(buffer, count: count)
        }

        var after = stat()
        guard fstat(descriptor, &after) == 0,
              before.st_dev == after.st_dev,
              before.st_ino == after.st_ino,
              before.st_uid == after.st_uid,
              before.st_mode == after.st_mode,
              before.st_nlink == after.st_nlink,
              before.st_size == after.st_size,
              before.st_mtimespec.tv_sec == after.st_mtimespec.tv_sec,
              before.st_mtimespec.tv_nsec == after.st_mtimespec.tv_nsec,
              before.st_ctimespec.tv_sec == after.st_ctimespec.tv_sec,
              before.st_ctimespec.tv_nsec == after.st_ctimespec.tv_nsec,
              data.count == Int(after.st_size)
        else {
            throw AppError.processFailed(unsafeMessage)
        }
        return data
    }

    private nonisolated static func validateMainBundleSignatureIfRequired(
        trustedHelpers: [URL]) throws
    {
        let bundleURL = Bundle.main.bundleURL.standardizedFileURL
        guard bundleURL.pathExtension == "app",
              let resources = Bundle.main.resourceURL?.standardizedFileURL
        else { return }

        let expected = Set([
            resources.appendingPathComponent("gpt-switch").standardizedFileURL.path,
            resources.appendingPathComponent("cursor-remote-manager.mjs").standardizedFileURL.path,
            resources.appendingPathComponent("cursor-codex-bridge.mjs").standardizedFileURL.path,
        ])
        guard Set(trustedHelpers.map { $0.standardizedFileURL.path }) == expected else {
            throw AppError.processFailed(
                "앱 번들의 Cursor helper 위치가 올바르지 않아 Cursor 자격증명을 전달하지 않았습니다.")
        }

        var staticCode: SecStaticCode?
        guard SecStaticCodeCreateWithPath(bundleURL as CFURL, [], &staticCode) == errSecSuccess,
              let staticCode,
              SecStaticCodeCheckValidity(
                  staticCode,
                  SecCSFlags(rawValue: kSecCSCheckAllArchitectures | kSecCSStrictValidate),
                  nil) == errSecSuccess
        else {
            throw AppError.processFailed(
                "앱 번들의 코드 서명 또는 리소스가 변경되어 Cursor 자격증명을 전달하지 않았습니다.")
        }
    }

    private nonisolated static func makeProvisioningSnapshotDirectory() throws -> URL {
        let directory = FileManager.default.temporaryDirectory.appendingPathComponent(
            "CodexSyncBarProvisioning-\(UUID().uuidString)",
            isDirectory: true)
        guard directory.path.withCString({ Darwin.mkdir($0, 0o700) }) == 0 else {
            throw AppError.processFailed("Cursor helper 보안 스냅샷을 준비하지 못했습니다.")
        }
        return directory
    }

    private nonisolated static func writeProvisioningSnapshot(_ data: Data, to url: URL) throws {
        let descriptor = url.path.withCString {
            Darwin.open($0, O_WRONLY | O_CREAT | O_EXCL | O_CLOEXEC | O_NOFOLLOW, 0o700)
        }
        guard descriptor >= 0 else {
            throw AppError.processFailed("Cursor helper 보안 스냅샷을 준비하지 못했습니다.")
        }
        defer { Darwin.close(descriptor) }

        do {
            try data.withUnsafeBytes { storage in
                guard let base = storage.baseAddress else { return }
                var written = 0
                while written < storage.count {
                    let count = Darwin.write(
                        descriptor,
                        base.advanced(by: written),
                        storage.count - written)
                    if count < 0, errno == EINTR { continue }
                    guard count > 0 else {
                        throw AppError.processFailed("Cursor helper 보안 스냅샷을 기록하지 못했습니다.")
                    }
                    written += count
                }
            }
            // Provisioning only reads these files (Bash interprets the main
            // helper explicitly), so remove write permission before launch.
            guard fchmod(descriptor, 0o500) == 0, fsync(descriptor) == 0 else {
                throw AppError.processFailed("Cursor helper 보안 스냅샷을 확정하지 못했습니다.")
            }
        } catch {
            try? FileManager.default.removeItem(at: url)
            throw error
        }
    }

    private nonisolated static func provisioningEnvironmentAllowlist() -> [String: String] {
        var environment = [
            "HOME": FileManager.default.homeDirectoryForCurrentUser.path,
            "PATH": "/usr/bin:/bin:/usr/sbin:/sbin",
            "LANG": "C",
            "LC_ALL": "C",
            "TMPDIR": "/private/tmp",
        ]
        if let socket = ProcessInfo.processInfo.environment["SSH_AUTH_SOCK"],
           socket.hasPrefix("/"),
           !socket.contains("\n"),
           !socket.contains("\r")
        {
            environment["SSH_AUTH_SOCK"] = socket
        }
        return environment
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

    private func run(
        arguments: [String],
        input: Data? = nil,
        executableOverride: URL? = nil,
        environmentOverrides: [String: String] = [:],
        inheritsEnvironment: Bool = true) async throws -> ProcessResult
    {
        let launchedExecutable = executableOverride ?? executable
        guard FileManager.default.isReadableFile(atPath: launchedExecutable.path) else {
            throw AppError.processFailed("gpt-switch를 찾을 수 없습니다: \(launchedExecutable.path)")
        }

        return try await withCheckedThrowingContinuation { continuation in
            let process = Process()
            let pipe = Pipe()
            let inputPipe = input.map { _ in Pipe() }
            let completion = ProcessExecutionCompletion(
                continuation: continuation,
                expectsInput: input != nil)

            // Locally installed helper scripts can inherit macOS provenance
            // metadata from the app bundle. Launching such a script directly
            // may be rejected with SIGKILL even though its contents are safe
            // and readable. Invoke the Bash script explicitly so execution is
            // stable across app updates and provenance changes.
            process.executableURL = URL(fileURLWithPath: "/bin/bash")
            process.arguments = [launchedExecutable.path] + arguments
            var environment = inheritsEnvironment ? ProcessInfo.processInfo.environment : [:]
            if inheritsEnvironment {
                environment["PATH"] = "/opt/homebrew/bin:/usr/local/bin:\(FileManager.default.homeDirectoryForCurrentUser.path)/.local/bin:/usr/bin:/bin:/usr/sbin:/sbin"
            }
            for (key, value) in environmentOverrides {
                environment[key] = value
            }
            process.environment = environment
            process.standardOutput = pipe
            process.standardError = pipe
            process.standardInput = inputPipe
            process.terminationHandler = { finished in
                completion.finish(status: finished.terminationStatus)
            }

            do {
                try process.run()
                // One reader owns the pipe through EOF. This both drains
                // verbose SSH output continuously and avoids the previous
                // termination/readability race that could lose the summary.
                pipe.fileHandleForWriting.closeFile()
                if let input, let inputPipe {
                    DispatchQueue.global(qos: .utility).async {
                        let writer = inputPipe.fileHandleForWriting
                        var writeError: Error?
                        do {
                            try writer.write(contentsOf: input)
                        } catch {
                            writeError = AppError.processFailed(
                                "gpt-switch에 입력을 안전하게 전달하지 못했습니다.")
                        }
                        do {
                            try writer.close()
                        } catch {
                            if writeError == nil {
                                writeError = AppError.processFailed(
                                    "gpt-switch 입력 스트림을 안전하게 닫지 못했습니다.")
                            }
                        }
                        completion.finish(inputError: writeError)
                    }
                }
                DispatchQueue.global(qos: .utility).async {
                    let data = pipe.fileHandleForReading.readDataToEndOfFile()
                    completion.finish(output: data)
                }
            } catch {
                if let inputPipe {
                    try? inputPipe.fileHandleForWriting.close()
                }
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
            didDefer: lowercased.contains("action=deferred-client-running"),
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
    private var inputFinished: Bool
    private var inputError: Error?

    init(
        continuation: CheckedContinuation<ProcessResult, Error>,
        expectsInput: Bool)
    {
        self.continuation = continuation
        self.inputFinished = !expectsInput
    }

    func finish(status: Int32) {
        lock.lock()
        self.status = status
        let completed = takeCompletedResultLocked()
        lock.unlock()
        resume(completed)
    }

    func finish(output: Data) {
        lock.lock()
        self.output = output
        let completed = takeCompletedResultLocked()
        lock.unlock()
        resume(completed)
    }

    func finish(inputError: Error?) {
        lock.lock()
        self.inputError = inputError
        inputFinished = true
        let completed = takeCompletedResultLocked()
        lock.unlock()
        resume(completed)
    }

    func fail(_ error: Error) {
        lock.lock()
        let continuation = self.continuation
        self.continuation = nil
        lock.unlock()
        continuation?.resume(throwing: error)
    }

    private func takeCompletedResultLocked()
        -> (CheckedContinuation<ProcessResult, Error>, Result<ProcessResult, Error>)?
    {
        guard let continuation, let status, let output, inputFinished else { return nil }
        self.continuation = nil
        if let inputError {
            return (continuation, .failure(inputError))
        }
        return (
            continuation,
            .success(ProcessResult(
                status: status,
                output: String(data: output, encoding: .utf8) ?? "")))
    }

    private func resume(
        _ completed: (CheckedContinuation<ProcessResult, Error>, Result<ProcessResult, Error>)?)
    {
        guard let (continuation, result) = completed else { return }
        continuation.resume(with: result)
    }
}
