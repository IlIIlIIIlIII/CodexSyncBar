import AppKit
import Darwin
import Foundation

@MainActor
final class LoginCoordinator: ObservableObject {
    enum State: Equatable {
        case idle
        case starting
        case resetting
        case waiting
        case validating
        case importing
        case completed
        case failed(String)
    }

    enum AppServerEvent: Equatable {
        case initialized
        case loginStarted(url: URL, loginID: String)
        case loginCompleted(loginID: String?, success: Bool, error: String?)
        case accountUpdated(authMode: String?)
        case accountStateReady
        case accountValidated
        case failed(String)
        case ignored
    }

    @Published private(set) var state: State = .idle
    @Published private(set) var authenticationURL: URL?
    @Published private(set) var rawStatus = "로그인 세션을 준비하고 있습니다…"

    private let authStore: AuthStore
    private let browserController: ChromiumBrowserController
    private var process: Process?
    private var inputPipe: Pipe?
    private var outputPipe: Pipe?
    private var errorPipe: Pipe?
    private var outputBuffer = ""
    private var errorBuffer = ""
    private var profileID: Int?
    private var loginHome: URL?
    private var loginID: String?
    private var replaceExistingAccount = false
    private var didStartLoginRequest = false
    private var didReceiveLoginCompletion = false
    private var didObserveChatGPTAccount = false
    private var didRequestAccountRead = false
    private var didRequestRateLimits = false
    private var activeSessionID = UUID()
    private var terminationObserver: NSObjectProtocol?
    var onCompletion: ((Result<Void, Error>) -> Void)?

    init(
        authStore: AuthStore,
        browserController: ChromiumBrowserController? = nil)
    {
        self.authStore = authStore
        self.browserController = browserController ?? ChromiumBrowserController()
        terminationObserver = NotificationCenter.default.addObserver(
            forName: NSApplication.willTerminateNotification,
            object: nil,
            queue: .main) { [weak self] _ in
                // A Task queued from willTerminate may never run because AppKit
                // exits the process as soon as this notification returns.
                // The observer is delivered on the main queue, so finish the
                // small amount of shutdown cleanup synchronously instead.
                MainActor.assumeIsolated {
                    self?.cancelForApplicationTermination()
                }
            }
    }

    var browserDisplayName: String { browserController.browserDisplayName }

    func browserProfileDisplayPath(profileID: Int) -> String {
        browserController.profileDisplayPath(for: profileID)
    }

    func start(profileID: Int, replaceExisting: Bool = false) {
        cancel(silent: true)
        let sessionID = UUID()
        activeSessionID = sessionID
        self.profileID = profileID
        replaceExistingAccount = replaceExisting
        state = .starting
        authenticationURL = nil
        loginID = nil
        outputBuffer = ""
        errorBuffer = ""
        didStartLoginRequest = false
        didReceiveLoginCompletion = false
        didObserveChatGPTAccount = false
        didRequestAccountRead = false
        didRequestRateLimits = false
        rawStatus = "Codex 보안 로그인 주소를 준비하고 있습니다…"

        let applicationSupport = FileManager.default.homeDirectoryForCurrentUser
            .appendingPathComponent("Library/Application Support/Codex SyncBar", isDirectory: true)
        let sessions = applicationSupport.appendingPathComponent("LoginSessions", isDirectory: true)
        let home = sessions.appendingPathComponent(
            "profile-\(profileID)-\(sessionID.uuidString)",
            isDirectory: true)
        loginHome = home
        do {
            try Self.prepareSecureDirectory(applicationSupport)
            try Self.prepareSecureDirectory(sessions)
            try Self.prepareSecureDirectory(home)
        } catch {
            finish(.failure(error))
            return
        }

        let process = Process()
        let input = Pipe()
        let output = Pipe()
        let errors = Pipe()
        process.executableURL = URL(fileURLWithPath: "/opt/homebrew/bin/codex")
        process.arguments = [
            "app-server",
            "--stdio",
            "-c",
            "cli_auth_credentials_store=\"file\"",
        ]
        var environment = ProcessInfo.processInfo.environment
        environment["CODEX_HOME"] = home.path
        environment["PATH"] = "/opt/homebrew/bin:/usr/local/bin:\(FileManager.default.homeDirectoryForCurrentUser.path)/.local/bin:/usr/bin:/bin:/usr/sbin:/sbin"
        environment["NO_COLOR"] = "1"
        process.environment = environment
        process.standardInput = input
        process.standardOutput = output
        process.standardError = errors

        output.fileHandleForReading.readabilityHandler = { [weak self] handle in
            let data = handle.availableData
            guard !data.isEmpty, let text = String(data: data, encoding: .utf8) else { return }
            Task { @MainActor [weak self] in
                guard let self, self.activeSessionID == sessionID else { return }
                self.consumeOutput(text, sessionID: sessionID)
            }
        }
        errors.fileHandleForReading.readabilityHandler = { [weak self] handle in
            let data = handle.availableData
            guard !data.isEmpty, let text = String(data: data, encoding: .utf8) else { return }
            Task { @MainActor [weak self] in
                guard let self, self.activeSessionID == sessionID else { return }
                self.errorBuffer = String((self.errorBuffer + Self.stripANSI(text)).suffix(16_000))
            }
        }
        process.terminationHandler = { [weak self] process in
            output.fileHandleForReading.readabilityHandler = nil
            errors.fileHandleForReading.readabilityHandler = nil
            let outputTail = output.fileHandleForReading.readDataToEndOfFile()
            let errorTail = errors.fileHandleForReading.readDataToEndOfFile()
            Task { @MainActor [weak self] in
                guard let self, self.activeSessionID == sessionID else { return }
                if let text = String(data: outputTail, encoding: .utf8), !text.isEmpty {
                    self.consumeOutput(text, sessionID: sessionID)
                }
                self.consumeFinalOutputLine(sessionID: sessionID)
                if let text = String(data: errorTail, encoding: .utf8), !text.isEmpty {
                    self.errorBuffer = String((self.errorBuffer + Self.stripANSI(text)).suffix(16_000))
                }
                // Let a final JSONL notification already queued on the main actor
                // win over the process-exit callback.
                try? await Task.sleep(nanoseconds: 200_000_000)
                guard self.activeSessionID == sessionID else { return }
                guard self.state != .idle, self.state != .completed else { return }
                let message = Self.loginFailureMessage(self.errorBuffer)
                self.finish(.failure(AppError.processFailed(
                    process.terminationStatus == 0
                        ? "로그인 연결이 완료되기 전에 종료되었습니다."
                        : message)))
            }
        }

        do {
            try process.run()
            self.process = process
            inputPipe = input
            outputPipe = output
            errorPipe = errors
            try writeJSON([
                "id": 1,
                "method": "initialize",
                "params": [
                    "clientInfo": [
                        "name": "codex-syncbar",
                        "title": "Codex SyncBar",
                        "version": AppVersion.current,
                    ],
                    "capabilities": [:],
                ],
            ])
        } catch {
            finish(.failure(error))
        }
    }

    func reopenBrowser() {
        guard let url = authenticationURL, let profileID else {
            retry()
            return
        }
        state = .starting
        rawStatus = "전용 Chrome 로그인 창을 앞으로 가져오고 있습니다…"
        launchBrowser(url: url, profileID: profileID, sessionID: activeSessionID)
    }

    func retry() {
        guard let profileID else { return }
        start(profileID: profileID, replaceExisting: replaceExistingAccount)
    }

    func restartWithFreshBrowserProfile(profileID: Int) async {
        cancel(silent: true, closeBrowser: false)
        let resetSessionID = UUID()
        activeSessionID = resetSessionID
        self.profileID = profileID
        state = .resetting
        rawStatus = "기존 Chrome 로그인 데이터는 백업하고 새 프로필을 준비하고 있습니다…"
        do {
            try await browserController.resetProfile(for: profileID)
            guard !Task.isCancelled, activeSessionID == resetSessionID else {
                state = .idle
                return
            }
            start(profileID: profileID, replaceExisting: true)
        } catch {
            finish(.failure(error))
        }
    }

    func swapBrowserProfiles() async throws {
        guard process == nil else {
            throw AppError.processFailed("진행 중인 로그인 창을 닫은 뒤 다시 시도해 주세요.")
        }
        try await browserController.swapProfiles()
    }

    func prepareBrowserSwapMarkers(firstToken: String, secondToken: String) throws {
        guard process == nil else {
            throw AppError.processFailed("진행 중인 로그인 창을 닫은 뒤 다시 시도해 주세요.")
        }
        try browserController.prepareSwapMarkers(firstToken: firstToken, secondToken: secondToken)
    }

    func browserSwapArrangement(
        firstToken: String,
        secondToken: String) throws -> BrowserProfileArrangement
    {
        try browserController.swapMarkerArrangement(
            firstToken: firstToken,
            secondToken: secondToken)
    }

    func clearBrowserProfile(profileID: Int) async throws {
        guard process == nil else {
            throw AppError.processFailed("진행 중인 로그인 창을 닫은 뒤 다시 시도해 주세요.")
        }
        try await browserController.clearProfile(for: profileID)
    }

    func cancel(silent: Bool = false) {
        cancel(silent: silent, closeBrowser: true)
    }

    private func cancel(silent: Bool, closeBrowser: Bool) {
        activeSessionID = UUID()
        let runningProcess = process
        let staleLoginHome = loginHome
        process = nil
        loginHome = nil
        detachPipes()
        if let runningProcess, runningProcess.isRunning { Self.stop(runningProcess) }
        Self.cleanUpLoginHome(staleLoginHome, after: runningProcess)
        if closeBrowser { browserController.close() }
        authenticationURL = nil
        loginID = nil
        if silent {
            state = .idle
        } else if state != .completed {
            state = .idle
            onCompletion?(.failure(AppError.loginCancelled))
        }
    }

    private func cancelForApplicationTermination() {
        activeSessionID = UUID()
        let runningProcess = process
        let staleLoginHome = loginHome
        process = nil
        loginHome = nil
        detachPipes()
        if let runningProcess, runningProcess.isRunning {
            Self.stopAndWaitForApplicationTermination(runningProcess)
        }
        Self.removeLoginHomeWithRetries(staleLoginHome)
        browserController.closeForApplicationTermination()
        authenticationURL = nil
        loginID = nil
        state = .idle
    }

    private func consumeOutput(_ text: String, sessionID: UUID) {
        outputBuffer += text
        while let newline = outputBuffer.firstIndex(of: "\n") {
            let line = String(outputBuffer[..<newline])
            outputBuffer.removeSubrange(...newline)
            handle(Self.parseAppServerLine(line), sessionID: sessionID)
        }
    }

    private func consumeFinalOutputLine(sessionID: UUID) {
        let line = outputBuffer.trimmingCharacters(in: .whitespacesAndNewlines)
        outputBuffer = ""
        guard !line.isEmpty else { return }
        handle(Self.parseAppServerLine(line), sessionID: sessionID)
    }

    private func handle(_ event: AppServerEvent, sessionID: UUID) {
        guard activeSessionID == sessionID else { return }
        switch event {
        case .initialized:
            guard !didStartLoginRequest else { return }
            didStartLoginRequest = true
            do {
                try writeJSON(["method": "initialized", "params": [:]])
                try writeJSON([
                    "id": 2,
                    "method": "account/login/start",
                    "params": ["type": "chatgpt"],
                ])
            } catch {
                finish(.failure(error))
            }

        case let .loginStarted(url, newLoginID):
            guard let profileID else {
                finish(.failure(AppError.invalidAuth))
                return
            }
            authenticationURL = url
            loginID = newLoginID
            state = .starting
            rawStatus = "전용 Chrome 로그인 창을 열고 있습니다…"
            launchBrowser(url: url, profileID: profileID, sessionID: sessionID)

        case let .loginCompleted(completedLoginID, success, error):
            if let completedLoginID, let loginID, completedLoginID != loginID { return }
            guard success else {
                finish(.failure(AppError.processFailed(
                    error ?? "로그인을 완료하지 못했습니다. 다시 시도해 주세요.")))
                return
            }
            didReceiveLoginCompletion = true
            state = .validating
            rawStatus = "Codex가 새 인증을 적용할 때까지 기다리고 있습니다…"
            requestAccountReadIfReady()

        case let .accountUpdated(authMode):
            guard authMode == "chatgpt" else { return }
            didObserveChatGPTAccount = true
            requestAccountReadIfReady()

        case .accountStateReady:
            guard !didRequestRateLimits else { return }
            didRequestRateLimits = true
            rawStatus = "새 인증으로 Codex 서버 연결을 확인하고 있습니다…"
            do {
                try writeJSON([
                    "id": 4,
                    "method": "account/rateLimits/read",
                ])
            } catch {
                finish(.failure(error))
            }

        case .accountValidated:
            importCompletedLogin(sessionID: sessionID)

        case let .failed(message):
            finish(.failure(AppError.processFailed(message)))

        case .ignored:
            break
        }
    }

    private func requestAccountReadIfReady() {
        guard Self.shouldRequestAccountRead(
            loginCompleted: didReceiveLoginCompletion,
            accountUpdated: didObserveChatGPTAccount,
            alreadyRequested: didRequestAccountRead)
        else { return }

        didRequestAccountRead = true
        rawStatus = "새 Codex 계정 상태를 확인하고 있습니다…"
        do {
            try writeJSON([
                "id": 3,
                "method": "account/read",
                "params": ["refreshToken": false],
            ])
        } catch {
            finish(.failure(error))
        }
    }

    nonisolated static func shouldRequestAccountRead(
        loginCompleted: Bool,
        accountUpdated: Bool,
        alreadyRequested: Bool) -> Bool
    {
        loginCompleted && accountUpdated && !alreadyRequested
    }

    private func importCompletedLogin(sessionID: UUID) {
        guard state != .importing, let home = loginHome, let profileID else { return }
        state = .importing
        rawStatus = "새 인증 정보를 검증하고 안전하게 저장하고 있습니다…"
        let source = home.appendingPathComponent("auth.json")
        let mayReplace = replaceExistingAccount
        Task { @MainActor [weak self] in
            guard let self, self.activeSessionID == sessionID else { return }
            do {
                try await self.importWithRetry(
                    from: source,
                    profileID: profileID,
                    replaceExisting: mayReplace)
                guard self.activeSessionID == sessionID else { return }
                self.state = .completed
                self.rawStatus = "로그인이 완료되었습니다. Chrome 세션은 다음 로그인에도 유지됩니다."
                self.browserController.closeAfterSuccessfulLogin()
                self.completeSession()
                self.onCompletion?(.success(()))
            } catch {
                self.finish(.failure(error))
            }
        }
    }

    private func launchBrowser(url: URL, profileID: Int, sessionID: UUID) {
        Task { @MainActor [weak self] in
            guard let self else { return }
            do {
                try await self.browserController.open(url, profileID: profileID)
                guard self.activeSessionID == sessionID else {
                    self.browserController.close()
                    return
                }
                self.state = .waiting
                self.rawStatus = "전용 Chrome 창에서 로그인하세요. 패스키와 Touch ID를 사용할 수 있습니다."
            } catch {
                guard self.activeSessionID == sessionID else { return }
                self.finish(.failure(error))
            }
        }
    }

    private func importWithRetry(
        from source: URL,
        profileID: Int,
        replaceExisting: Bool) async throws
    {
        var lastError: Error = AppError.invalidAuth
        for attempt in 0..<60 {
            do {
                try await authStore.importLoggedInAuth(
                    from: source,
                    for: profileID,
                    replaceExisting: replaceExisting)
                return
            } catch let error as AppError {
                switch error {
                case .loginRequired:
                    throw error
                case let .processFailed(message)
                    where message.localizedCaseInsensitiveContains("another controller operation"):
                    lastError = error
                case .invalidAuth, .missingAuth:
                    lastError = error
                default:
                    throw error
                }
            } catch {
                lastError = error
            }

            if attempt < 59 {
                try? await Task.sleep(nanoseconds: 250_000_000)
            }
        }
        throw lastError
    }

    private func completeSession() {
        activeSessionID = UUID()
        let runningProcess = process
        let completedLoginHome = loginHome
        process = nil
        loginHome = nil
        detachPipes()
        if let runningProcess, runningProcess.isRunning { Self.stop(runningProcess) }
        Self.cleanUpLoginHome(completedLoginHome, after: runningProcess)
    }

    private func finish(_ result: Result<Void, Error>) {
        activeSessionID = UUID()
        let runningProcess = process
        let failedLoginHome = loginHome
        process = nil
        loginHome = nil
        detachPipes()
        if let runningProcess, runningProcess.isRunning { Self.stop(runningProcess) }
        Self.cleanUpLoginHome(failedLoginHome, after: runningProcess)
        browserController.close()

        switch result {
        case .success:
            state = .completed
        case let .failure(error):
            state = .failed(error.localizedDescription)
            rawStatus = error.localizedDescription
        }
        onCompletion?(result)
    }

    private func writeJSON(_ object: [String: Any]) throws {
        guard let handle = inputPipe?.fileHandleForWriting else {
            throw AppError.processFailed("로그인 서버에 연결하지 못했습니다.")
        }
        var data = try JSONSerialization.data(withJSONObject: object)
        data.append(0x0A)
        try handle.write(contentsOf: data)
    }

    private func detachPipes() {
        outputPipe?.fileHandleForReading.readabilityHandler = nil
        errorPipe?.fileHandleForReading.readabilityHandler = nil
        try? inputPipe?.fileHandleForWriting.close()
        inputPipe = nil
        outputPipe = nil
        errorPipe = nil
    }

    static func parseAppServerLine(_ line: String) -> AppServerEvent {
        guard let data = line.data(using: .utf8),
              let object = try? JSONSerialization.jsonObject(with: data),
              let message = object as? [String: Any]
        else { return .ignored }

        let responseID = (message["id"] as? NSNumber)?.intValue
        if let error = message["error"] as? [String: Any] {
            return .failed((error["message"] as? String) ?? "로그인 서버 요청에 실패했습니다.")
        }
        if responseID == 1, message["result"] != nil {
            return .initialized
        }
        if responseID == 2 {
            guard let result = message["result"] as? [String: Any],
                  result["type"] as? String == "chatgpt",
                  let urlString = result["authUrl"] as? String,
                  let url = URL(string: urlString),
                  ChromiumBrowserController.isAllowedAuthenticationURL(url),
                  let loginID = result["loginId"] as? String,
                  !loginID.isEmpty
            else { return .failed("Codex가 올바른 로그인 주소를 반환하지 않았습니다.") }
            return .loginStarted(url: url, loginID: loginID)
        }
        if responseID == 3 {
            guard let result = message["result"] as? [String: Any],
                  let account = result["account"] as? [String: Any],
                  account["type"] as? String == "chatgpt"
            else { return .failed("새 Codex 계정 상태를 확인하지 못했습니다.") }
            return .accountStateReady
        }
        if responseID == 4 {
            guard let result = message["result"] as? [String: Any],
                  result["rateLimits"] is [String: Any]
            else { return .failed("새 인증으로 Codex 서버 연결을 확인하지 못했습니다.") }
            return .accountValidated
        }
        if message["method"] as? String == "account/updated",
           let params = message["params"] as? [String: Any]
        {
            return .accountUpdated(authMode: params["authMode"] as? String)
        }
        if message["method"] as? String == "account/login/completed",
           let params = message["params"] as? [String: Any],
           let success = params["success"] as? Bool
        {
            return .loginCompleted(
                loginID: params["loginId"] as? String,
                success: success,
                error: params["error"] as? String)
        }
        return .ignored
    }

    static func stripANSI(_ text: String) -> String {
        text.replacingOccurrences(
            of: "\u{001B}\\[[0-?]*[ -/]*[@-~]",
            with: "",
            options: .regularExpression)
    }

    static func loginFailureMessage(_ output: String) -> String {
        let cleaned = stripANSI(output)
        if cleaned.localizedCaseInsensitiveContains("address already in use") {
            return "로그인 콜백 포트를 사용 중입니다. 다른 로그인 창을 닫고 다시 시도해 주세요."
        }
        if cleaned.localizedCaseInsensitiveContains("429 Too Many Requests") {
            return "로그인 요청이 너무 많습니다. 잠시 후 다시 시도해 주세요."
        }
        let lastLine = cleaned.split(whereSeparator: \.isNewline)
            .map(String.init)
            .last(where: { !$0.trimmingCharacters(in: .whitespaces).isEmpty })
        return lastLine ?? "로그인을 완료하지 못했습니다. 다시 시도해 주세요."
    }

    static func stop(_ process: Process) {
        process.interrupt()
        DispatchQueue.global(qos: .utility).asyncAfter(deadline: .now() + 0.8) {
            guard process.isRunning else { return }
            process.terminate()
            DispatchQueue.global(qos: .utility).asyncAfter(deadline: .now() + 0.8) {
                guard process.isRunning else { return }
                kill(process.processIdentifier, SIGKILL)
            }
        }
    }

    private nonisolated static func stopAndWaitForApplicationTermination(_ process: Process) {
        process.interrupt()
        var deadline = Date().addingTimeInterval(0.6)
        while process.isRunning, Date() < deadline { usleep(50_000) }
        if process.isRunning { process.terminate() }
        deadline = Date().addingTimeInterval(0.6)
        while process.isRunning, Date() < deadline { usleep(50_000) }
        if process.isRunning {
            kill(process.processIdentifier, SIGKILL)
            usleep(100_000)
        }
    }

    private static func prepareSecureDirectory(_ url: URL) throws {
        let fileManager = FileManager.default
        if fileManager.fileExists(atPath: url.path) {
            let values = try url.resourceValues(forKeys: [.isSymbolicLinkKey, .isDirectoryKey])
            guard values.isSymbolicLink != true, values.isDirectory == true else {
                throw AppError.processFailed("로그인 저장소의 보안 상태가 올바르지 않습니다.")
            }
        } else {
            try fileManager.createDirectory(
                at: url,
                withIntermediateDirectories: false,
                attributes: [.posixPermissions: 0o700])
        }
        try fileManager.setAttributes([.posixPermissions: 0o700], ofItemAtPath: url.path)
    }

    private nonisolated static func cleanUpLoginHome(_ home: URL?, after process: Process?) {
        guard let home else { return }
        DispatchQueue.global(qos: .utility).async {
            let deadline = Date().addingTimeInterval(3)
            while process?.isRunning == true, Date() < deadline {
                usleep(100_000)
            }
            if process?.isRunning == true {
                kill(process!.processIdentifier, SIGKILL)
                usleep(150_000)
            }
            removeLoginHomeWithRetries(home)
        }
    }

    private nonisolated static func removeLoginHomeWithRetries(_ home: URL?) {
        guard let home else { return }
        let fileManager = FileManager.default
        for attempt in 0..<10 {
            do {
                try fileManager.removeItem(at: home)
                return
            } catch {
                guard fileManager.fileExists(atPath: home.path) else { return }
                if attempt < 9 { usleep(50_000) }
            }
        }
    }
}
