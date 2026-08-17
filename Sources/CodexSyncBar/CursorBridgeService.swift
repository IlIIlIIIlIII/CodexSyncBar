import AppKit
import Darwin
import Foundation

@MainActor
final class CursorBridgeService {
    private(set) var status: CursorBridgeStatus = .stopped
    private(set) var resolvedNodePath: String?

    private let home: URL
    private let fileManager: FileManager
    private let helperURL: URL
    private let cursorSDKCredentialStore: CursorSDKCredentialStoring
    private var process: Process?
    private var activePreferences: CursorBridgePreferences?
    private var cachedModelCatalog: CursorModelCatalog?
    private var cachedModelCatalogCredentialExpiry: Int64?
    private var stderrBuffer = Data()
    private var terminationObserver: NSObjectProtocol?
    var onUnexpectedStatusChange: ((CursorBridgeStatus) -> Void)?

    init(
        home: URL = FileManager.default.homeDirectoryForCurrentUser,
        fileManager: FileManager = .default,
        helperURL: URL? = nil,
        cursorSDKCredentialStore: CursorSDKCredentialStoring = SystemCursorSDKCredentialStore())
    {
        self.home = home
        self.fileManager = fileManager
        self.cursorSDKCredentialStore = cursorSDKCredentialStore
        self.helperURL = helperURL ?? home.appendingPathComponent(
            ".local/lib/gpt-switch/cursor-codex-bridge.mjs")
        terminationObserver = NotificationCenter.default.addObserver(
            forName: NSApplication.willTerminateNotification,
            object: nil,
            queue: .main) { [weak self] _ in
                MainActor.assumeIsolated { self?.stopImmediately() }
            }
    }

    deinit {
        if let terminationObserver {
            NotificationCenter.default.removeObserver(terminationObserver)
        }
        process?.terminate()
    }

    var endpoint: String? {
        guard let activePreferences else { return nil }
        return "http://127.0.0.1:\(activePreferences.port)/v1"
    }

    func refreshAvailability(preferences: CursorBridgePreferences) async -> CursorBridgeStatus {
        if process?.isRunning == true {
            if await isHealthy(
                preferences: preferences,
                expectedPID: process?.processIdentifier)
            {
                let value = CursorBridgeStatus.healthy(pid: process?.processIdentifier ?? 0)
                status = value
                return value
            }
            await stop()
        }
        guard resolveNode(requiringCursorSDK: true) != nil else {
            status = .missingNode
            return status
        }
        do {
            guard let credential = try cursorSDKCredentialStore.read() else {
                status = .unauthenticated
                return status
            }
            _ = try credential.usableAPIKey()
            try requireSafeHelper()
        } catch let error as CursorSDKCredentialValidationError where error == .expired {
            status = .unauthenticated
            return status
        } catch {
            status = .failed(error.localizedDescription)
            return status
        }
        status = .stopped
        return status
    }

    func loadModelCatalog(preferredAgentPath _: String?) async throws -> CursorModelCatalog {
        let credential = try requiredSDKCredential()
        let result = try await runSDKCommand(
            argument: "--sdk-list-models",
            apiKey: credential.apiKey,
            timeoutIterations: 300,
            maximumOutputBytes: 256 * 1_024)
        let catalog = CursorModelCatalog(cliOutput: String(decoding: result.stdout, as: UTF8.self))
        guard !catalog.variants.isEmpty else {
            throw AppError.processFailed("Cursor SDK가 사용 가능한 모델을 반환하지 않았습니다.")
        }
        guard catalog.variants.count <= 512 else {
            throw AppError.processFailed("Cursor 모델 수가 안전 한도(512개)를 초과했습니다.")
        }
        cachedModelCatalog = catalog
        cachedModelCatalogCredentialExpiry = credential.apiKeyExpiresAtMilliseconds
        return catalog
    }

    func loadAccount(preferredAgentPath _: String?) async throws -> CursorAccount? {
        struct AccountResult: Decodable {
            let schemaVersion: Int
            let email: String?

            enum CodingKeys: String, CodingKey {
                case schemaVersion = "schema_version"
                case email
            }
        }

        guard let stored = try cursorSDKCredentialStore.read() else { return nil }
        let apiKey = try stored.usableAPIKey()
        let result = try await runSDKCommand(
            argument: "--sdk-status",
            apiKey: apiKey,
            timeoutIterations: 150,
            maximumOutputBytes: 8 * 1_024)
        guard let payload = try? JSONDecoder().decode(AccountResult.self, from: result.stdout),
              payload.schemaVersion == 1,
              let email = payload.email ?? stored.email,
              let account = CursorAccount(email: email)
        else {
            throw AppError.processFailed("Cursor SDK 로그인 계정 이메일을 확인하지 못했습니다.")
        }
        return account
    }

    func loginToCursorSubscription() async throws -> CursorSDKCredential {
        guard let node = resolveNode(requiringCursorSDK: true) else {
            throw AppError.processFailed("Cursor SDK를 실행할 Node.js 22.13 이상을 찾지 못했습니다.")
        }
        try requireSafeHelper()
        let result = try await runSDKCommand(
            node: node,
            argument: "--sdk-login",
            apiKey: nil,
            timeoutIterations: 6_000,
            maximumOutputBytes: CursorSDKCredential.maximumEncodedBytes)
        let credential = try CursorSDKCredential(loginResultData: result.stdout)
        cachedModelCatalog = nil
        cachedModelCatalogCredentialExpiry = nil
        return credential
    }

    @discardableResult
    func start(
        preferences proposedPreferences: CursorBridgePreferences,
        forceRestart: Bool = false) async -> CursorBridgeStatus
    {
        let preferences: CursorBridgePreferences
        do {
            preferences = try proposedPreferences.validated()
        } catch {
            status = .failed(error.localizedDescription)
            return status
        }
        if !forceRestart,
           process?.isRunning == true,
           activePreferences == preferences,
           await isHealthy(
               preferences: preferences,
               expectedPID: process?.processIdentifier)
        {
            status = .healthy(pid: process?.processIdentifier ?? 0)
            return status
        }
        if process != nil { await stop() }

        status = .starting
        let credential: CursorSDKCredential
        do {
            guard let stored = try cursorSDKCredentialStore.read() else {
                status = .unauthenticated
                return status
            }
            _ = try stored.usableAPIKey()
            credential = stored
        } catch let error as CursorSDKCredentialValidationError where error == .expired {
            status = .unauthenticated
            return status
        } catch {
            status = .failed("Cursor SDK 자격증명을 Keychain에서 읽지 못했습니다: \(error.localizedDescription)")
            return status
        }
        guard let node = resolveNode(requiringCursorSDK: true) else {
            status = .missingNode
            return status
        }
        let modelCatalog: CursorModelCatalog
        do {
            let accountCatalog: CursorModelCatalog
            if cachedModelCatalogCredentialExpiry == credential.apiKeyExpiresAtMilliseconds,
               let cachedModelCatalog
            {
                accountCatalog = cachedModelCatalog
            } else {
                accountCatalog = try await loadModelCatalog(preferredAgentPath: nil)
            }
            modelCatalog = try accountCatalog.exposingCodexModelIDs(
                preferences.exposedModelIDs)
            guard modelCatalog.variants.contains(where: { $0.slug == preferences.model }) else {
                status = .failed(
                    "기본 Cursor 모델은 Codex에 노출할 모델 목록에 포함되어야 합니다: \(preferences.model)")
                return status
            }
        } catch {
            status = .failed(error.localizedDescription)
            return status
        }
        do {
            try requireSafeHelper()
        } catch {
            status = .failed(error.localizedDescription)
            return status
        }

        let child = Process()
        let stdout = Pipe()
        let stderr = Pipe()
        child.executableURL = node
        child.arguments = [
            helperURL.path,
            "--host", "127.0.0.1",
            "--port", String(preferences.port),
            "--model", preferences.model,
            "--workspace", bridgeWorkspaceURL.path,
            "--parent-pid", String(getpid()),
        ]
        let environment: [String: String]
        do {
            let nativeModelSlugs = try managedNativeModelSlugs()
            environment = try Self.sidecarEnvironment(
                inheriting: ProcessInfo.processInfo.environment,
                bridgeToken: preferences.bridgeToken,
                modelCatalog: modelCatalog,
                nativeModelSlugs: nativeModelSlugs,
                cursorAPIKey: credential.apiKey)
        } catch {
            status = .failed("Cursor 모델 설정을 만들지 못했습니다: \(error.localizedDescription)")
            return status
        }
        child.environment = environment
        child.standardInput = FileHandle.nullDevice
        child.standardOutput = stdout
        child.standardError = stderr
        stderrBuffer.removeAll(keepingCapacity: true)
        stdout.fileHandleForReading.readabilityHandler = { handle in
            _ = handle.availableData
        }
        stderr.fileHandleForReading.readabilityHandler = { [weak self] handle in
            let data = handle.availableData
            guard !data.isEmpty else { return }
            Task { @MainActor [weak self] in self?.recordStderrMetadata(data) }
        }
        child.terminationHandler = { [weak self, weak child] _ in
            Task { @MainActor [weak self, weak child] in
                guard let self, let child, self.process === child else { return }
                stdout.fileHandleForReading.readabilityHandler = nil
                stderr.fileHandleForReading.readabilityHandler = nil
                self.process = nil
                self.activePreferences = nil
                if case .starting = self.status {
                    self.status = self.classifyStartupFailure()
                } else if self.status.isHealthy {
                    self.status = .failed("Cursor 브리지 프로세스가 종료되었습니다.")
                }
                self.onUnexpectedStatusChange?(self.status)
            }
        }
        do {
            try child.run()
        } catch {
            stdout.fileHandleForReading.readabilityHandler = nil
            stderr.fileHandleForReading.readabilityHandler = nil
            status = .failed("Cursor 브리지를 시작하지 못했습니다: \(error.localizedDescription)")
            return status
        }
        process = child
        activePreferences = preferences

        for _ in 0 ..< 50 {
            if !child.isRunning {
                status = classifyStartupFailure()
                return status
            }
            if await isHealthy(
                preferences: preferences,
                expectedPID: child.processIdentifier)
            {
                status = .healthy(pid: child.processIdentifier)
                return status
            }
            try? await Task.sleep(nanoseconds: 100_000_000)
        }
        await stop()
        status = .failed("Cursor 브리지 health check가 시간 안에 완료되지 않았습니다.")
        return status
    }

    func stop() async {
        guard let child = process else {
            activePreferences = nil
            status = .stopped
            return
        }
        process = nil
        activePreferences = nil
        if child.isRunning {
            child.terminate()
            // The sidecar first terminates any active Cursor child and waits
            // up to two seconds before forcing it down. Keep this grace longer
            // so killing Node cannot orphan that child cleanup.
            for _ in 0 ..< 30 where child.isRunning {
                try? await Task.sleep(nanoseconds: 100_000_000)
            }
            if child.isRunning { kill(child.processIdentifier, SIGKILL) }
        }
        status = .stopped
    }

    func stopImmediately() {
        guard let child = process else { return }
        process = nil
        activePreferences = nil
        if child.isRunning { child.terminate() }
        status = .stopped
    }

    static func sidecarEnvironment(
        inheriting base: [String: String],
        bridgeToken: String,
        modelCatalog: CursorModelCatalog,
        nativeModelSlugs: [String] = [],
        cursorAPIKey: String? = nil) throws -> [String: String]
    {
        var environment = base
        environment["SYNCBAR_CURSOR_BRIDGE_TOKEN"] = bridgeToken
        environment["SYNCBAR_CURSOR_MODELS_JSON"] = String(
            decoding: try JSONEncoder().encode(modelCatalog.variants.map(\.slug)),
            as: UTF8.self)
        environment["SYNCBAR_CURSOR_MODEL_PARAMETERS_JSON"] =
            try modelCatalog.acpModelParametersJSON()
        environment["SYNCBAR_CURSOR_MODEL_ROUTES_JSON"] =
            try modelCatalog.cursorRouteJSON()
        environment["SYNCBAR_CURSOR_BACKEND"] = "sdk"
        environment.removeValue(forKey: "CURSOR_API_KEY")
        if let cursorAPIKey {
            environment["CURSOR_API_KEY"] = try CursorAPIKeyValidator.validated(cursorAPIKey)
        }
        if nativeModelSlugs.isEmpty {
            environment.removeValue(forKey: "SYNCBAR_NATIVE_MODELS_JSON")
        } else {
            environment["SYNCBAR_NATIVE_MODELS_JSON"] = String(
                decoding: try JSONEncoder().encode(nativeModelSlugs),
                as: UTF8.self)
        }
        return environment
    }

    private func managedNativeModelSlugs() throws -> [String] {
        let catalogURL = home.appendingPathComponent(
            ".local/share/gpt-switch/cursor-codex-model-catalog.json")
        var info = stat()
        let result = catalogURL.path.withCString { lstat($0, &info) }
        if result != 0 {
            guard errno == ENOENT else {
                throw AppError.processFailed(
                    "Codex 모델 카탈로그를 확인하지 못했습니다: \(String(cString: strerror(errno)))")
            }
            return []
        }
        guard (info.st_mode & S_IFMT) == S_IFREG,
              info.st_uid == getuid(),
              (info.st_mode & 0o077) == 0,
              info.st_size >= 0,
              info.st_size <= 16 * 1_024 * 1_024
        else {
            throw AppError.processFailed("Codex 모델 카탈로그 파일이 안전하지 않습니다.")
        }
        let data = try Data(contentsOf: catalogURL)
        guard let root = try JSONSerialization.jsonObject(with: data) as? [String: Any],
              let models = root["models"] as? [[String: Any]]
        else {
            throw AppError.processFailed("Codex 모델 카탈로그 형식이 올바르지 않습니다.")
        }
        var seen = Set<String>()
        let slugs = try models.compactMap { model -> String? in
            guard let slug = model["slug"] as? String else {
                throw AppError.processFailed("Codex 모델 카탈로그에 ID가 없는 항목이 있습니다.")
            }
            if slug.hasPrefix("syncbar-cursor/") { return nil }
            guard slug.range(
                of: #"^[A-Za-z0-9][A-Za-z0-9._:/-]{0,127}$"#,
                options: .regularExpression) != nil,
                seen.insert(slug).inserted
            else {
                throw AppError.processFailed("Codex 기본 모델 ID가 올바르지 않습니다: \(slug)")
            }
            return slug
        }
        guard slugs.count <= 512 else {
            throw AppError.processFailed("Codex 기본 모델 수가 안전 한도를 초과했습니다.")
        }
        return slugs
    }

    private var bridgeWorkspaceURL: URL {
        home.appendingPathComponent(
            ".local/share/gpt-switch/cursor-bridge-workspace",
            isDirectory: true)
    }

    private func resolveNode(requiringCursorSDK: Bool = false) -> URL?
    {
        if let resolvedNodePath {
            let cached = URL(fileURLWithPath: resolvedNodePath)
            if !requiringCursorSDK || nodeSupportsCursorSDK(cached) { return cached }
            self.resolvedNodePath = nil
        }
        var candidates: [String] = []
        candidates.append(contentsOf: [
            "/Applications/Cursor.app/Contents/Resources/app/resources/helpers/node",
            "/opt/homebrew/bin/node",
            "/usr/local/bin/node",
            home.appendingPathComponent(".local/bin/node").path,
            "/usr/bin/node",
        ])
        let versions = home.appendingPathComponent(
            ".local/share/cursor-agent/versions", isDirectory: true)
        if let entries = try? fileManager.contentsOfDirectory(
            at: versions,
            includingPropertiesForKeys: [.isDirectoryKey],
            options: [.skipsHiddenFiles])
        {
            candidates.insert(
                contentsOf: entries.sorted { $0.lastPathComponent > $1.lastPathComponent }
                    .map { $0.appendingPathComponent("node").path },
                at: min(1, candidates.count))
        }
        guard let result = firstExecutable(candidates, requiringCursorSDK: requiringCursorSDK) else {
            return nil
        }
        resolvedNodePath = result.path
        return result
    }

    private func nodeSupportsCursorSDK(_ node: URL) -> Bool {
        let process = Process()
        process.executableURL = node
        process.arguments = [
            "-e",
            "const [a,b]=process.versions.node.split('.').map(Number);process.exit(a>22||(a===22&&b>=13)?0:1)",
        ]
        process.standardInput = FileHandle.nullDevice
        process.standardOutput = FileHandle.nullDevice
        process.standardError = FileHandle.nullDevice
        do { try process.run() } catch { return false }
        process.waitUntilExit()
        return process.terminationStatus == 0
    }

    private func firstExecutable(
        _ candidates: [String],
        requiringCursorSDK: Bool = false) -> URL?
    {
        for candidate in candidates {
            let url = URL(fileURLWithPath: candidate).resolvingSymlinksInPath()
            guard fileManager.isExecutableFile(atPath: url.path),
                  let values = try? url.resourceValues(forKeys: [.isRegularFileKey]),
                  values.isRegularFile == true
            else { continue }
            if requiringCursorSDK, !nodeSupportsCursorSDK(url) { continue }
            return url
        }
        return nil
    }

    private func requireSafeHelper() throws {
        var info = stat()
        guard helperURL.path.withCString({ lstat($0, &info) }) == 0,
              (info.st_mode & S_IFMT) == S_IFREG,
              info.st_uid == getuid(),
              (info.st_mode & 0o022) == 0
        else {
            throw AppError.processFailed("Cursor 브리지 helper가 안전한 일반 파일이 아닙니다.")
        }
    }

    private func isHealthy(
        preferences: CursorBridgePreferences,
        expectedPID: Int32?) async -> Bool
    {
        guard let url = URL(string: "http://127.0.0.1:\(preferences.port)/healthz") else { return false }
        var request = URLRequest(url: url)
        request.cachePolicy = .reloadIgnoringLocalCacheData
        request.timeoutInterval = 0.5
        request.setValue(preferences.bridgeToken, forHTTPHeaderField: "X-SyncBar-Bridge-Token")
        do {
            let (data, response) = try await URLSession.shared.data(for: request)
            guard (response as? HTTPURLResponse)?.statusCode == 200,
                  let object = try JSONSerialization.jsonObject(with: data) as? [String: Any]
            else { return false }
            return object["status"] as? String == "ok" &&
                object["protocol"] as? String == "responses" &&
                object["cursor_backend"] as? String == "sdk" &&
                object["model"] as? String == preferences.model &&
                (object["pid"] as? NSNumber)?.int32Value == expectedPID
        } catch {
            return false
        }
    }

    private func requiredSDKCredential() throws -> CursorSDKCredential {
        guard let credential = try cursorSDKCredentialStore.read() else {
            throw AppError.processFailed("Cursor 구독으로 먼저 로그인해 주세요.")
        }
        _ = try credential.usableAPIKey()
        return credential
    }

    private func runSDKCommand(
        node proposedNode: URL? = nil,
        argument: String,
        apiKey: String?,
        timeoutIterations: Int,
        maximumOutputBytes: Int) async throws -> (stdout: Data, stderr: Data)
    {
        guard ["--sdk-login", "--sdk-status", "--sdk-list-models"].contains(argument),
              timeoutIterations > 0,
              maximumOutputBytes > 0,
              let node = proposedNode ?? resolveNode(requiringCursorSDK: true)
        else {
            throw AppError.processFailed("Cursor SDK 명령 설정이 올바르지 않습니다.")
        }
        try requireSafeHelper()
        let child = Process()
        let stdout = Pipe()
        let stderr = Pipe()
        child.executableURL = node
        child.arguments = [helperURL.path, argument]
        var environment = ProcessInfo.processInfo.environment
        environment["NO_COLOR"] = "1"
        environment.removeValue(forKey: "CURSOR_API_KEY")
        if let apiKey {
            environment["CURSOR_API_KEY"] = try CursorAPIKeyValidator.validated(apiKey)
        }
        child.environment = environment
        child.standardInput = FileHandle.nullDevice
        child.standardOutput = stdout
        child.standardError = stderr
        do {
            try child.run()
        } catch {
            throw AppError.processFailed(
                "Cursor SDK 명령을 실행하지 못했습니다: \(error.localizedDescription)")
        }
        let stdoutRead = Task.detached(priority: .userInitiated) {
            stdout.fileHandleForReading.readDataToEndOfFile()
        }
        let stderrRead = Task.detached(priority: .userInitiated) {
            stderr.fileHandleForReading.readDataToEndOfFile()
        }
        var cancelled = false
        for _ in 0 ..< timeoutIterations where child.isRunning {
            if Task.isCancelled {
                cancelled = true
                break
            }
            try? await Task.sleep(nanoseconds: 100_000_000)
        }
        let timedOut = child.isRunning && !cancelled
        if child.isRunning {
            child.terminate()
            for _ in 0 ..< 10 where child.isRunning {
                try? await Task.sleep(nanoseconds: 100_000_000)
            }
            if child.isRunning { kill(child.processIdentifier, SIGKILL) }
        }
        let outputData = await stdoutRead.value
        let errorData = await stderrRead.value
        if cancelled { throw CancellationError() }
        if timedOut { throw AppError.processFailed("Cursor SDK 명령 확인 시간이 초과되었습니다.") }
        guard !child.isRunning else {
            throw AppError.processFailed("Cursor SDK 명령을 종료하지 못했습니다.")
        }
        if child.terminationStatus != 0 {
            let metadata = String(decoding: errorData.prefix(1_024), as: UTF8.self)
                .trimmingCharacters(in: .whitespacesAndNewlines)
            let suffix = metadata.isEmpty ? "" : ": \(metadata)"
            throw AppError.processFailed("Cursor SDK 명령이 완료되지 않았습니다\(suffix)")
        }
        guard outputData.count <= maximumOutputBytes,
              errorData.count <= 64 * 1_024
        else {
            throw AppError.processFailed("Cursor SDK 명령 출력이 허용 크기를 초과했습니다.")
        }
        return (outputData, errorData)
    }

    private func recordStderrMetadata(_ data: Data) {
        let remaining = max(0, 4_096 - stderrBuffer.count)
        if remaining > 0 { stderrBuffer.append(data.prefix(remaining)) }
    }

    private func classifyStartupFailure() -> CursorBridgeStatus {
        let metadata = String(decoding: stderrBuffer, as: UTF8.self)
        if metadata.contains("EADDRINUSE") { return .portConflict }
        return .failed("Cursor 브리지가 시작 직후 종료되었습니다.")
    }
}
