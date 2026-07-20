import AppKit
import Darwin
import Foundation

enum BrowserProfileArrangement: Equatable {
    case original
    case swapped
    case unknown
}

@MainActor
final class ChromiumBrowserController {
    nonisolated static let defaultChromeAppURL = URL(
        fileURLWithPath: "/Applications/Google Chrome.app",
        isDirectory: true)

    private let fileManager: FileManager
    private let applicationSupportURL: URL
    private let chromeAppURL: URL
    private var primaryProcess: Process?
    private var runningApplication: NSRunningApplication?
    private var activeUserDataDirectory: URL?

    init(
        applicationSupportURL: URL = FileManager.default.homeDirectoryForCurrentUser
            .appendingPathComponent("Library/Application Support/Codex SyncBar", isDirectory: true),
        chromeAppURL: URL = ChromiumBrowserController.defaultChromeAppURL,
        fileManager: FileManager = .default)
    {
        self.applicationSupportURL = applicationSupportURL
        self.chromeAppURL = chromeAppURL
        self.fileManager = fileManager
    }

    var browserDisplayName: String {
        guard let bundle = Bundle(url: chromeAppURL),
              let version = bundle.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String,
              !version.isEmpty
        else { return "Google Chrome" }
        return "Google Chrome \(version)"
    }

    var isAvailable: Bool {
        fileManager.isExecutableFile(atPath: chromeExecutableURL.path)
    }

    func profileDirectory(for profileID: Int) -> URL {
        Self.profileDirectory(profileID: profileID, applicationSupportURL: applicationSupportURL)
    }

    func profileDisplayPath(for profileID: Int) -> String {
        let path = profileDirectory(for: profileID).path
        let home = FileManager.default.homeDirectoryForCurrentUser.path
        guard path.hasPrefix(home) else { return path }
        return "~" + path.dropFirst(home.count)
    }

    func open(_ url: URL, profileID: Int) async throws {
        guard Self.isAllowedAuthenticationURL(url) else {
            throw AppError.processFailed("안전하지 않은 로그인 주소를 차단했습니다.")
        }
        guard isAvailable else {
            throw AppError.processFailed("Google Chrome을 찾지 못했습니다. /Applications에 Chrome을 설치해 주세요.")
        }

        try recoverInterruptedProfileSwap()
        let directory = profileDirectory(for: profileID)
        try prepareProfileDirectory(directory)
        activeUserDataDirectory = directory

        if let runningApplication, !runningApplication.isTerminated {
            _ = try launchChrome(url: url, userDataDirectory: directory)
            activateBrowser(
                runningApplication,
                processIdentifier: runningApplication.processIdentifier)
            return
        }
        if let primaryProcess, primaryProcess.isRunning {
            _ = try launchChrome(url: url, userDataDirectory: directory)
            if let application = NSRunningApplication(
                processIdentifier: primaryProcess.processIdentifier)
            {
                runningApplication = application
                activateBrowser(
                    application,
                    processIdentifier: application.processIdentifier)
            }
            return
        }

        // Start the executable synchronously so there is always a PID to stop.
        // An asynchronous LaunchServices request can outlive the menu app and
        // create a late orphan browser after applicationWillTerminate returns.
        let process = try launchChrome(url: url, userDataDirectory: directory)
        primaryProcess = process

        for _ in 0..<40 {
            try Task.checkCancellation()
            if let application = NSRunningApplication(
                processIdentifier: process.processIdentifier)
            {
                runningApplication = application
                activateBrowser(
                    application,
                    processIdentifier: application.processIdentifier)
                return
            }
            guard process.isRunning else {
                let discovered = try chromeProcessIDs(userDataDirectory: directory)
                if let processID = discovered.first,
                   let application = NSRunningApplication(processIdentifier: processID)
                {
                    runningApplication = application
                    activateBrowser(
                        application,
                        processIdentifier: application.processIdentifier)
                    return
                }
                primaryProcess = nil
                throw AppError.processFailed("전용 Chrome 로그인 창을 열지 못했습니다.")
            }
            try await Task.sleep(nanoseconds: 50_000_000)
        }
        // Chrome is already running even if LaunchServices has not yet exposed
        // an NSRunningApplication. The stored Process and exact profile path
        // still make cancellation and application shutdown deterministic.
    }

    func resetProfile(for profileID: Int) async throws {
        try recoverInterruptedProfileSwap()
        let directory = profileDirectory(for: profileID)
        try await closeAndWait(userDataDirectory: directory)

        if fileManager.fileExists(atPath: directory.path) {
            let values = try directory.resourceValues(forKeys: [.isSymbolicLinkKey, .isDirectoryKey])
            guard values.isSymbolicLink != true, values.isDirectory == true else {
                throw AppError.processFailed("Chrome 프로필 저장소가 올바르지 않아 초기화를 중단했습니다.")
            }

            let backupRoot = applicationSupportURL
                .appendingPathComponent("ChromeProfileBackups", isDirectory: true)
            try prepareSecureDirectory(applicationSupportURL)
            try prepareSecureDirectory(backupRoot)
            let stamp = Int(Date().timeIntervalSince1970)
            let backup = backupRoot.appendingPathComponent(
                "profile-\(profileID)-\(stamp)-\(UUID().uuidString.prefix(8))",
                isDirectory: true)
            do {
                try fileManager.moveItem(at: directory, to: backup)
            } catch {
                throw AppError.processFailed(
                    "전용 Chrome 창을 완전히 닫은 뒤 다시 시도해 주세요. 기존 로그인 데이터는 그대로 보존했습니다.")
            }
        }

        try prepareProfileDirectory(directory)
    }

    func swapProfiles() async throws {
        try recoverInterruptedProfileSwap()
        let first = profileDirectory(for: 1)
        let second = profileDirectory(for: 2)
        try await closeAndWait(userDataDirectory: first)
        try await closeAndWait(userDataDirectory: second)
        try prepareProfileDirectory(first)
        try prepareProfileDirectory(second)

        let profilesRoot = first.deletingLastPathComponent()
        let temporary = profilesRoot.appendingPathComponent(
            ".profile-swap-\(UUID().uuidString)",
            isDirectory: true)
        do {
            try fileManager.moveItem(at: first, to: temporary)
            do {
                try fileManager.moveItem(at: second, to: first)
            } catch {
                try? fileManager.moveItem(at: temporary, to: first)
                throw error
            }
            do {
                try fileManager.moveItem(at: temporary, to: second)
            } catch {
                try? fileManager.moveItem(at: first, to: second)
                try? fileManager.moveItem(at: temporary, to: first)
                throw error
            }
            // Both directories were validated and forced to 0700 before the
            // same-volume renames. A rename preserves those permissions, so
            // there is no fallible post-swap step that could report failure
            // after the sessions have already exchanged locations.
        } catch let error as AppError {
            throw error
        } catch {
            throw AppError.processFailed(
                "Chrome 프로필 위치를 바꾸지 못했습니다. 기존 로그인 데이터는 복원했습니다.")
        }
    }

    func prepareSwapMarkers(firstToken: String, secondToken: String) throws {
        try recoverInterruptedProfileSwap()
        let first = profileDirectory(for: 1)
        let second = profileDirectory(for: 2)
        try prepareProfileDirectory(first)
        try prepareProfileDirectory(second)
        try writeSwapMarker(firstToken, directory: first)
        try writeSwapMarker(secondToken, directory: second)
    }

    func swapMarkerArrangement(
        firstToken: String,
        secondToken: String) throws -> BrowserProfileArrangement
    {
        try recoverInterruptedProfileSwap()
        let firstMarker = try readSwapMarker(directory: profileDirectory(for: 1))
        let secondMarker = try readSwapMarker(directory: profileDirectory(for: 2))
        if firstMarker == firstToken, secondMarker == secondToken { return .original }
        if firstMarker == secondToken, secondMarker == firstToken { return .swapped }
        return .unknown
    }

    func clearProfile(for profileID: Int) async throws {
        try recoverInterruptedProfileSwap()
        let directory = profileDirectory(for: profileID)
        try await closeAndWait(userDataDirectory: directory)
        if fileManager.fileExists(atPath: directory.path) {
            let values = try directory.resourceValues(forKeys: [.isSymbolicLinkKey, .isDirectoryKey])
            guard values.isSymbolicLink != true, values.isDirectory == true else {
                throw AppError.processFailed("Chrome 프로필 저장소가 올바르지 않아 로그아웃을 중단했습니다.")
            }
            try fileManager.removeItem(at: directory)
        }
        try prepareProfileDirectory(directory)
    }

    func closeAfterSuccessfulLogin() {
        guard let processIdentifier = runningApplication?.processIdentifier
            ?? primaryProcess?.processIdentifier
        else { return }
        Task { @MainActor [weak self] in
            try? await Task.sleep(nanoseconds: 1_500_000_000)
            guard let self,
                  self.runningApplication?.processIdentifier == processIdentifier
                    || self.primaryProcess?.processIdentifier == processIdentifier
            else { return }
            self.close()
        }
    }

    func close() {
        let application = runningApplication
        let process = primaryProcess
        let directory = activeUserDataDirectory
        primaryProcess = nil
        runningApplication = nil
        activeUserDataDirectory = nil

        var processIDs = Set<pid_t>()
        if let directory,
           let discovered = try? chromeProcessIDs(userDataDirectory: directory)
        {
            processIDs.formUnion(discovered)
        }
        if let application, !application.isTerminated {
            processIDs.insert(application.processIdentifier)
            application.terminate()
        }
        if let process, process.isRunning {
            processIDs.insert(process.processIdentifier)
            process.terminate()
        }
        for processID in processIDs { kill(processID, SIGTERM) }

        guard !processIDs.isEmpty else { return }
        DispatchQueue.global(qos: .utility).asyncAfter(deadline: .now() + 3) {
            for processID in processIDs where kill(processID, 0) == 0 {
                kill(processID, SIGKILL)
            }
        }
    }

    func closeForApplicationTermination() {
        let application = runningApplication
        let process = primaryProcess
        let directory = activeUserDataDirectory
        primaryProcess = nil
        runningApplication = nil
        activeUserDataDirectory = nil

        var processIDs = Set<pid_t>()
        if let directory,
           let discovered = try? chromeProcessIDs(userDataDirectory: directory)
        {
            processIDs.formUnion(discovered)
        }
        if let application, !application.isTerminated {
            processIDs.insert(application.processIdentifier)
            application.terminate()
        }
        if let process, process.isRunning {
            processIDs.insert(process.processIdentifier)
            process.terminate()
        }
        for processID in processIDs { kill(processID, SIGTERM) }

        let deadline = Date().addingTimeInterval(3)
        while Date() < deadline,
              processIDs.contains(where: { kill($0, 0) == 0 })
        {
            usleep(50_000)
        }
        for processID in processIDs where kill(processID, 0) == 0 {
            kill(processID, SIGKILL)
        }
        if !processIDs.isEmpty { usleep(100_000) }
    }

    static func profileDirectory(profileID: Int, applicationSupportURL: URL) -> URL {
        applicationSupportURL
            .appendingPathComponent("ChromeProfiles", isDirectory: true)
            .appendingPathComponent("profile-\(profileID)", isDirectory: true)
    }

    static func launchArguments(userDataDirectory: URL, authenticationURL: URL) -> [String] {
        [
            "--user-data-dir=\(userDataDirectory.path)",
            "--profile-directory=Default",
            "--new-window",
            "--no-first-run",
            "--no-default-browser-check",
            authenticationURL.absoluteString,
        ]
    }

    static func isAllowedAuthenticationURL(_ url: URL) -> Bool {
        guard url.scheme?.lowercased() == "https",
              let host = url.host?.lowercased()
        else { return false }
        return host == "auth.openai.com"
    }

    static func processCommand(_ command: String, usesUserDataDirectory directory: URL) -> Bool {
        let marker = "--user-data-dir=\(directory.path)"
        guard let range = command.range(of: marker) else { return false }
        guard range.upperBound < command.endIndex else { return true }
        return command[range.upperBound].isWhitespace
    }

    private var chromeExecutableURL: URL {
        chromeAppURL.appendingPathComponent("Contents/MacOS/Google Chrome")
    }

    private func launchChrome(url: URL, userDataDirectory: URL) throws -> Process {
        let process = Process()
        process.executableURL = chromeExecutableURL
        process.arguments = Self.launchArguments(
            userDataDirectory: userDataDirectory,
            authenticationURL: url)
        process.standardOutput = FileHandle.nullDevice
        process.standardError = FileHandle.nullDevice
        try process.run()
        return process
    }

    private func activateBrowser(
        _ application: NSRunningApplication,
        processIdentifier: pid_t)
    {
        application.activate(options: [.activateAllWindows, .activateIgnoringOtherApps])

        // The NSRunningApplication can exist slightly before Chrome creates its
        // first window. Reassert focus only while this exact profile process is
        // still owned by the current login session.
        Task { @MainActor [weak self] in
            for delay in [350_000_000, 700_000_000, 1_500_000_000] as [UInt64] {
                try? await Task.sleep(nanoseconds: delay)
                guard let self,
                      self.runningApplication?.processIdentifier == processIdentifier
                        || self.primaryProcess?.processIdentifier == processIdentifier
                else { return }
                application.activate(options: [.activateAllWindows, .activateIgnoringOtherApps])
            }
        }
    }

    private func prepareProfileDirectory(_ directory: URL) throws {
        let profilesRoot = applicationSupportURL
            .appendingPathComponent("ChromeProfiles", isDirectory: true)
        try prepareSecureDirectory(applicationSupportURL)
        try prepareSecureDirectory(profilesRoot)
        try prepareSecureDirectory(directory)
    }

    private func writeSwapMarker(_ token: String, directory: URL) throws {
        let marker = directory.appendingPathComponent(".codex-syncbar-swap-marker")
        if fileManager.fileExists(atPath: marker.path) {
            let values = try marker.resourceValues(forKeys: [.isSymbolicLinkKey, .isRegularFileKey])
            guard values.isSymbolicLink != true, values.isRegularFile == true else {
                throw AppError.processFailed("Chrome 프로필 교환 마커가 올바르지 않습니다.")
            }
        }
        let temporary = directory.appendingPathComponent(".codex-syncbar-marker-\(UUID().uuidString)")
        try Data((token + "\n").utf8).write(to: temporary)
        try fileManager.setAttributes([.posixPermissions: 0o600], ofItemAtPath: temporary.path)
        do {
            if fileManager.fileExists(atPath: marker.path) {
                _ = try fileManager.replaceItemAt(marker, withItemAt: temporary)
            } else {
                try fileManager.moveItem(at: temporary, to: marker)
            }
        } catch {
            try? fileManager.removeItem(at: temporary)
            throw error
        }
    }

    private func readSwapMarker(directory: URL) throws -> String? {
        let marker = directory.appendingPathComponent(".codex-syncbar-swap-marker")
        guard fileManager.fileExists(atPath: marker.path) else { return nil }
        let values = try marker.resourceValues(forKeys: [.isSymbolicLinkKey, .isRegularFileKey])
        guard values.isSymbolicLink != true, values.isRegularFile == true else {
            throw AppError.processFailed("Chrome 프로필 교환 마커가 올바르지 않습니다.")
        }
        return try String(contentsOf: marker, encoding: .utf8)
            .trimmingCharacters(in: .whitespacesAndNewlines)
    }

    private func recoverInterruptedProfileSwap() throws {
        let profilesRoot = applicationSupportURL
            .appendingPathComponent("ChromeProfiles", isDirectory: true)
        guard fileManager.fileExists(atPath: profilesRoot.path) else { return }
        let rootValues = try profilesRoot.resourceValues(forKeys: [.isSymbolicLinkKey, .isDirectoryKey])
        guard rootValues.isSymbolicLink != true, rootValues.isDirectory == true else {
            throw AppError.processFailed("Chrome 프로필 저장소가 올바르지 않습니다.")
        }

        let temporaryDirectories = try fileManager.contentsOfDirectory(
            at: profilesRoot,
            includingPropertiesForKeys: [.isSymbolicLinkKey, .isDirectoryKey],
            options: [])
            .filter { $0.lastPathComponent.hasPrefix(".profile-swap-") }
        guard !temporaryDirectories.isEmpty else { return }
        guard temporaryDirectories.count == 1 else {
            throw AppError.processFailed("중단된 Chrome 프로필 교환이 여러 개 있어 자동 복구하지 못했습니다.")
        }

        let temporary = temporaryDirectories[0]
        let temporaryValues = try temporary.resourceValues(forKeys: [.isSymbolicLinkKey, .isDirectoryKey])
        guard temporaryValues.isSymbolicLink != true, temporaryValues.isDirectory == true else {
            throw AppError.processFailed("중단된 Chrome 프로필 교환 데이터가 올바르지 않습니다.")
        }
        let first = profileDirectory(for: 1)
        let second = profileDirectory(for: 2)
        let firstExists = fileManager.fileExists(atPath: first.path)
        let secondExists = fileManager.fileExists(atPath: second.path)

        if !firstExists, secondExists {
            try fileManager.moveItem(at: temporary, to: first)
        } else if firstExists, !secondExists {
            try fileManager.moveItem(at: first, to: second)
            try fileManager.moveItem(at: temporary, to: first)
        } else {
            throw AppError.processFailed(
                "중단된 Chrome 프로필 교환 상태가 모호합니다. 로그인 데이터는 삭제하지 않았습니다.")
        }
        try prepareProfileDirectory(first)
        try prepareProfileDirectory(second)
    }

    private func prepareSecureDirectory(_ directory: URL) throws {
        if fileManager.fileExists(atPath: directory.path) {
            let values = try directory.resourceValues(forKeys: [.isSymbolicLinkKey, .isDirectoryKey])
            guard values.isSymbolicLink != true, values.isDirectory == true else {
                throw AppError.processFailed("Chrome 프로필 저장소가 올바르지 않습니다.")
            }
        } else {
            try fileManager.createDirectory(
                at: directory,
                withIntermediateDirectories: false,
                attributes: [.posixPermissions: 0o700])
        }
        try fileManager.setAttributes([.posixPermissions: 0o700], ofItemAtPath: directory.path)
    }

    private func closeAndWait(userDataDirectory: URL) async throws {
        let application = runningApplication
        let process = primaryProcess
        primaryProcess = nil
        runningApplication = nil
        activeUserDataDirectory = nil
        if let application, !application.isTerminated { application.terminate() }
        if let process, process.isRunning { process.terminate() }

        var processIDs = Set(try chromeProcessIDs(userDataDirectory: userDataDirectory))
        if let application, !application.isTerminated {
            processIDs.insert(application.processIdentifier)
        }
        if let process, process.isRunning {
            processIDs.insert(process.processIdentifier)
        }
        for processID in processIDs {
            kill(processID, SIGTERM)
        }

        var deadline = Date().addingTimeInterval(3)
        while Date() < deadline {
            try Task.checkCancellation()
            processIDs = Set(try chromeProcessIDs(userDataDirectory: userDataDirectory))
            if processIDs.isEmpty, application?.isTerminated != false { return }
            try await Task.sleep(nanoseconds: 100_000_000)
        }
        for processID in processIDs { kill(processID, SIGKILL) }
        if let application, !application.isTerminated {
            application.forceTerminate()
        }

        deadline = Date().addingTimeInterval(1)
        while Date() < deadline {
            try Task.checkCancellation()
            processIDs = Set(try chromeProcessIDs(userDataDirectory: userDataDirectory))
            if processIDs.isEmpty, application?.isTerminated != false { return }
            try await Task.sleep(nanoseconds: 100_000_000)
        }
        throw AppError.processFailed(
            "전용 Chrome 프로세스가 아직 프로필을 사용 중입니다. Chrome 창을 닫고 다시 시도해 주세요.")
    }

    private func chromeProcessIDs(userDataDirectory: URL) throws -> [pid_t] {
        let process = Process()
        let pipe = Pipe()
        process.executableURL = URL(fileURLWithPath: "/bin/ps")
        process.arguments = ["-axo", "pid=,command="]
        process.standardOutput = pipe
        process.standardError = FileHandle.nullDevice
        try process.run()
        let data = pipe.fileHandleForReading.readDataToEndOfFile()
        process.waitUntilExit()
        guard process.terminationStatus == 0,
              let output = String(data: data, encoding: .utf8)
        else { return [] }

        return output.split(whereSeparator: \.isNewline).compactMap { line in
            let parts = line.split(maxSplits: 1, whereSeparator: \.isWhitespace)
            guard parts.count == 2,
                  let processID = pid_t(parts[0]),
                  processID != getpid(),
                  Self.processCommand(String(parts[1]), usesUserDataDirectory: userDataDirectory)
            else { return nil }
            return processID
        }
    }
}
