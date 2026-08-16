import AppKit
import Darwin
import Foundation

enum CodexCursorModelCatalogBuilder {
    static let maximumModelCount = 512
    static let maximumBundledCatalogBytes = 8 * 1_024 * 1_024
    static let maximumGeneratedCatalogBytes = 16 * 1_024 * 1_024

    static func bundledModelSlugs(from data: Data) throws -> [String] {
        guard data.count <= maximumBundledCatalogBytes,
              let root = try JSONSerialization.jsonObject(with: data) as? [String: Any],
              let models = root["models"] as? [[String: Any]]
        else {
            throw AppError.processFailed("Codex 모델 카탈로그 형식이 올바르지 않습니다.")
        }
        let slugs = models.compactMap { model -> String? in
            guard let slug = model["slug"] as? String, !slug.isEmpty else { return nil }
            return slug
        }
        guard slugs.count == models.count,
              Set(slugs).count == slugs.count
        else {
            throw AppError.processFailed("Codex 모델 카탈로그의 모델 ID가 올바르지 않습니다.")
        }
        return slugs
    }

    static func build(
        cursorCatalog: CursorModelCatalog,
        bundledCatalogData: Data) throws -> Data
    {
        guard !cursorCatalog.variants.isEmpty else {
            throw AppError.processFailed("Codex에 표시할 Cursor 모델이 없습니다.")
        }
        guard bundledCatalogData.count <= maximumBundledCatalogBytes else {
            throw AppError.processFailed("Codex 번들 모델 카탈로그가 너무 큽니다.")
        }
        guard var root = try JSONSerialization.jsonObject(with: bundledCatalogData) as? [String: Any],
              let bundledModels = root["models"] as? [[String: Any]],
              let template = bundledModels.first(where: { $0["slug"] as? String == "gpt-5.6-sol" })
                ?? bundledModels.first
        else {
            throw AppError.processFailed("Codex 번들 모델 카탈로그 형식이 올바르지 않습니다.")
        }

        let routes = cursorCatalog.codexModelRoutes
        guard bundledModels.count + routes.count <= maximumModelCount else {
            throw AppError.processFailed(
                "Codex 모델 수가 안전 한도(\(maximumModelCount)개)를 넘었습니다.")
        }

        var usedSlugs = Set(try bundledModelSlugs(from: bundledCatalogData))
        var priority = bundledModels.compactMap { model -> Int? in
            if let priority = model["priority"] as? Int { return priority }
            return (model["priority"] as? NSNumber)?.intValue
        }.max().map { $0 + 1 } ?? 1
        var models = bundledModels

        for section in cursorCatalog.sections {
            for family in section.families {
                let thinkingModes = family.group == .anthropicClaude
                    ? [true]
                    : [false, true]
                for thinking in thinkingModes {
                    let modelID = CursorModelCatalog.codexModelID(
                        baseSlug: family.baseSlug,
                        thinking: thinking)
                    guard let route = routes[modelID] else { continue }
                    guard CursorModelCatalog.isValidCodexModelID(modelID) else {
                        throw AppError.processFailed(
                            "Codex에 표시할 Cursor 모델 ID가 너무 길거나 올바르지 않습니다: \(modelID)")
                    }
                    guard usedSlugs.insert(modelID).inserted else {
                        throw AppError.processFailed(
                            "Codex 모델 ID가 기존 카탈로그와 충돌합니다: \(modelID)")
                    }
                    let variants = family.variants.filter { $0.thinking == thinking }
                    var model = template
                    model["slug"] = modelID
                    model["display_name"] = pickerDisplayName(
                        group: section.group,
                        familyName: family.displayName,
                        thinking: thinking)
                    model["description"] = "Cursor 구독을 로컬 Cursor CLI 브리지로 사용하는 모델입니다."
                    model["visibility"] = "list"
                    model["supported_in_api"] = true
                    model["priority"] = priority
                    model["availability_nux"] = NSNull()
                    model["upgrade"] = NSNull()
                    model["additional_speed_tiers"] = route.supportsFast ? ["fast"] : []
                    model["service_tiers"] = route.supportsFast ? [[
                        "id": "priority",
                        "name": "Fast",
                        "description": "Cursor CLI Fast 변형을 사용합니다.",
                    ]] : []
                    model["input_modalities"] = ["text", "image"]
                    model["supports_image_detail_original"] = true
                    applyConservativeContext(
                        variants: variants,
                        to: &model)
                    model["supports_parallel_tool_calls"] = false
                    model["supports_search_tool"] = false
                    model["experimental_supported_tools"] = []
                    model["support_verbosity"] = false
                    model["default_reasoning_level"] = route.defaultEffort.rawValue
                    let advertisesReasoning = variants.contains { $0.effort != .default }
                    model["supported_reasoning_levels"] = advertisesReasoning
                        ? reasoningMetadata(for: route.supportedEfforts)
                        : []
                    models.append(model)
                    priority += 1
                }
            }
        }

        root["models"] = models
        let data = try JSONSerialization.data(
            withJSONObject: root,
            options: [.prettyPrinted, .sortedKeys, .withoutEscapingSlashes]) + Data([0x0A])
        guard data.count <= maximumGeneratedCatalogBytes else {
            throw AppError.processFailed("Codex에 설치할 Cursor 모델 카탈로그가 너무 큽니다.")
        }
        return data
    }

    private static func pickerDisplayName(
        group: CursorModelGroup,
        familyName: String,
        thinking: Bool) -> String
    {
        if group == .automatic { return "Cursor · Auto" }
        var name = familyName
        if group == .cursor, name.lowercased().hasPrefix("cursor ") {
            name.removeFirst("cursor ".count)
        }
        let base = "Cursor · \(name)"
        return thinking && group != .anthropicClaude
            ? "\(base) · Thinking"
            : base
    }

    private static func reasoningMetadata(
        for efforts: [CursorModelEffort]) -> [[String: String]]
    {
        efforts.map { effort in
            let description = switch effort {
            case .none: "Reasoning disabled"
            case .minimal: "Minimal reasoning"
            case .low: "Low reasoning"
            case .medium: "Medium reasoning"
            case .default: "Cursor model default"
            case .high: "High reasoning"
            case .xhigh: "Extra high reasoning"
            case .max: "Maximum reasoning"
            }
            return ["effort": effort.rawValue, "description": description]
        }
    }

    private static func applyConservativeContext(
        variants: [CursorModelVariant],
        to model: inout [String: Any])
    {
        if variants.allSatisfy({ $0.context == "1m" }) {
            model["context_window"] = 1_000_000
            model["max_context_window"] = 1_000_000
            return
        }

        let conservativeWindow = 272_000
        let templateWindow = (model["context_window"] as? NSNumber)?.intValue
        let contextWindow = min(templateWindow ?? conservativeWindow, conservativeWindow)
        let templateMaximum = (model["max_context_window"] as? NSNumber)?.intValue
        model["context_window"] = contextWindow
        model["max_context_window"] = min(templateMaximum ?? contextWindow, conservativeWindow)
    }
}

@MainActor
final class CodexCursorModelCatalogService {
    private static let codexBundleIdentifier = "com.openai.codex"

    private let home: URL
    private let fileManager: FileManager
    private let bundledCatalogOverride: Data?

    init(
        home: URL = FileManager.default.homeDirectoryForCurrentUser,
        fileManager: FileManager = .default,
        bundledCatalogOverride: Data? = nil)
    {
        self.home = home
        self.fileManager = fileManager
        self.bundledCatalogOverride = bundledCatalogOverride
    }

    var stateRoot: URL {
        home.appendingPathComponent(".local/share/gpt-switch", isDirectory: true)
    }

    var catalogURL: URL {
        stateRoot.appendingPathComponent("cursor-codex-model-catalog.json")
    }

    func install(cursorCatalog: CursorModelCatalog) async throws -> Data? {
        let previousData: Data?
        if pathEntryExists(catalogURL) {
            let size = try requireOwnedRegularFile(catalogURL)
            guard size >= 0,
                  size <= Int64(CodexCursorModelCatalogBuilder.maximumGeneratedCatalogBytes)
            else {
                throw AppError.processFailed("기존 Cursor 모델 카탈로그가 너무 큽니다.")
            }
            previousData = try Data(contentsOf: catalogURL)
            guard previousData?.count ?? 0 <= CodexCursorModelCatalogBuilder.maximumGeneratedCatalogBytes else {
                throw AppError.processFailed("기존 Cursor 모델 카탈로그가 너무 큽니다.")
            }
        } else {
            previousData = nil
        }
        let candidate = try await generatedCatalog(cursorCatalog: cursorCatalog)
        try ensureStateRoot()
        try atomicWrite(candidate, to: catalogURL)
        return previousData
    }

    func generatedCatalog(cursorCatalog: CursorModelCatalog) async throws -> Data {
        let bundled = try await loadBundledCatalog()
        return try CodexCursorModelCatalogBuilder.build(
            cursorCatalog: cursorCatalog,
            bundledCatalogData: bundled)
    }

    func restore(_ previousData: Data?) throws {
        try ensureStateRoot()
        if let previousData {
            guard previousData.count
                <= CodexCursorModelCatalogBuilder.maximumGeneratedCatalogBytes
            else {
                throw AppError.processFailed("복원할 Cursor 모델 카탈로그가 너무 큽니다.")
            }
            try atomicWrite(previousData, to: catalogURL)
        } else if pathEntryExists(catalogURL) {
            try requireOwnedRegularFile(catalogURL)
            try fileManager.removeItem(at: catalogURL)
        }
    }

    func removeManagedCatalog() throws {
        guard pathEntryExists(catalogURL) else { return }
        try requireOwnedRegularFile(catalogURL)
        try fileManager.removeItem(at: catalogURL)
    }

    private func loadBundledCatalog() async throws -> Data {
        if let bundledCatalogOverride {
            guard bundledCatalogOverride.count
                <= CodexCursorModelCatalogBuilder.maximumBundledCatalogBytes
            else {
                throw AppError.processFailed("Codex 번들 모델 카탈로그가 너무 큽니다.")
            }
            return bundledCatalogOverride
        }
        if let cachedCatalog = try loadCodexModelCache() {
            return cachedCatalog
        }
        guard let codex = resolveCodex() else {
            throw AppError.processFailed("Codex 실행 파일을 찾지 못해 모델 선택기 카탈로그를 만들 수 없습니다.")
        }
        let probeHome = fileManager.temporaryDirectory.appendingPathComponent(
            "codex-syncbar-catalog-probe-\(UUID().uuidString)",
            isDirectory: true)
        try fileManager.createDirectory(
            at: probeHome,
            withIntermediateDirectories: false,
            attributes: [.posixPermissions: 0o700])
        defer { try? fileManager.removeItem(at: probeHome) }

        let child = Process()
        let stdout = Pipe()
        let stderr = Pipe()
        child.executableURL = codex
        child.arguments = ["debug", "models", "--bundled"]
        var environment = ProcessInfo.processInfo.environment
        environment["CODEX_HOME"] = probeHome.path
        environment["NO_COLOR"] = "1"
        child.environment = environment
        child.standardInput = FileHandle.nullDevice
        child.standardOutput = stdout
        child.standardError = stderr
        do {
            try child.run()
        } catch {
            throw AppError.processFailed(
                "Codex 번들 모델 카탈로그를 실행하지 못했습니다: \(error.localizedDescription)")
        }
        let stdoutRead = Task.detached(priority: .userInitiated) {
            stdout.fileHandleForReading.readDataToEndOfFile()
        }
        let stderrRead = Task.detached(priority: .userInitiated) {
            stderr.fileHandleForReading.readDataToEndOfFile()
        }
        for _ in 0 ..< 150 where child.isRunning {
            try? await Task.sleep(nanoseconds: 100_000_000)
        }
        if child.isRunning {
            child.terminate()
            for _ in 0 ..< 10 where child.isRunning {
                try? await Task.sleep(nanoseconds: 100_000_000)
            }
            if child.isRunning { kill(child.processIdentifier, SIGKILL) }
            _ = await stdoutRead.value
            _ = await stderrRead.value
            throw AppError.processFailed("Codex 모델 카탈로그 확인 시간이 초과되었습니다.")
        }
        let output = await stdoutRead.value
        let errorOutput = await stderrRead.value
        guard child.terminationStatus == 0 else {
            let metadata = String(decoding: errorOutput.prefix(1_024), as: UTF8.self)
                .trimmingCharacters(in: .whitespacesAndNewlines)
            let suffix = metadata.isEmpty ? "" : ": \(metadata)"
            throw AppError.processFailed("Codex 모델 카탈로그를 가져오지 못했습니다\(suffix)")
        }
        guard output.count <= CodexCursorModelCatalogBuilder.maximumBundledCatalogBytes else {
            throw AppError.processFailed("Codex 번들 모델 카탈로그가 너무 큽니다.")
        }
        return output
    }

    private func loadCodexModelCache() throws -> Data? {
        let cacheURL = home.appendingPathComponent(
            ".codex/models_cache.json",
            isDirectory: false)
        guard pathEntryExists(cacheURL) else { return nil }

        let descriptor = cacheURL.path.withCString {
            open($0, O_RDONLY | O_CLOEXEC | O_NOFOLLOW)
        }
        guard descriptor >= 0 else {
            throw AppError.processFailed("Codex 모델 캐시 파일을 안전하게 열 수 없습니다.")
        }
        let handle = FileHandle(fileDescriptor: descriptor, closeOnDealloc: true)

        var info = stat()
        guard fstat(descriptor, &info) == 0,
              (info.st_mode & S_IFMT) == S_IFREG,
              info.st_uid == getuid(),
              (info.st_mode & 0o022) == 0,
              info.st_size >= 0,
              info.st_size <= CodexCursorModelCatalogBuilder.maximumBundledCatalogBytes
        else {
            try? handle.close()
            throw AppError.processFailed("Codex 모델 캐시 파일이 안전한 읽기 전용 파일이 아닙니다.")
        }

        let data: Data
        do {
            data = try handle.readToEnd() ?? Data()
            try handle.close()
        } catch {
            try? handle.close()
            throw AppError.processFailed(
                "Codex 모델 캐시 파일을 읽지 못했습니다: \(error.localizedDescription)")
        }
        guard data.count == Int(info.st_size),
              data.count <= CodexCursorModelCatalogBuilder.maximumBundledCatalogBytes
        else {
            throw AppError.processFailed("Codex 모델 캐시 파일 크기가 읽는 중 변경되었습니다.")
        }
        _ = try CodexCursorModelCatalogBuilder.bundledModelSlugs(from: data)
        return data
    }

    private func resolveCodex() -> URL? {
        var candidates: [URL] = []
        if let application = NSWorkspace.shared.urlForApplication(
            withBundleIdentifier: Self.codexBundleIdentifier)
        {
            candidates.append(
                application.appendingPathComponent("Contents/Resources/codex"))
        }
        candidates.append(contentsOf: [
            URL(fileURLWithPath: "/Applications/ChatGPT.app/Contents/Resources/codex"),
            URL(fileURLWithPath: "/opt/homebrew/bin/codex"),
            URL(fileURLWithPath: "/usr/local/bin/codex"),
            home.appendingPathComponent(".local/bin/codex"),
        ])

        var visited = Set<String>()
        for candidate in candidates {
            let url = candidate.resolvingSymlinksInPath()
            guard visited.insert(url.path).inserted else { continue }
            guard fileManager.isExecutableFile(atPath: url.path),
                  let values = try? url.resourceValues(forKeys: [.isRegularFileKey]),
                  values.isRegularFile == true
            else { continue }
            return url
        }
        return nil
    }

    private func ensureStateRoot() throws {
        let local = home.appendingPathComponent(".local", isDirectory: true)
        let share = local.appendingPathComponent("share", isDirectory: true)
        for directory in [local, share, stateRoot] {
            if pathEntryExists(directory) {
                let values = try directory.resourceValues(forKeys: [.isDirectoryKey, .isSymbolicLinkKey])
                guard values.isDirectory == true, values.isSymbolicLink != true else {
                    throw AppError.processFailed("Cursor 모델 카탈로그 경로가 안전한 디렉터리가 아닙니다.")
                }
            } else {
                try fileManager.createDirectory(
                    at: directory,
                    withIntermediateDirectories: false,
                    attributes: [.posixPermissions: 0o700])
            }
        }
    }

    private func atomicWrite(_ data: Data, to destination: URL) throws {
        let temporary = destination.deletingLastPathComponent()
            .appendingPathComponent(".\(destination.lastPathComponent).\(UUID().uuidString).tmp")
        var descriptor: Int32? = temporary.path.withCString {
            open(
                $0,
                O_WRONLY | O_CREAT | O_EXCL | O_CLOEXEC | O_NOFOLLOW,
                S_IRUSR | S_IWUSR)
        }
        guard let openedDescriptor = descriptor, openedDescriptor >= 0 else {
            throw AppError.processFailed(
                "Cursor 모델 카탈로그 임시 파일을 만들지 못했습니다: \(String(cString: strerror(errno)))")
        }
        defer {
            if let descriptor { close(descriptor) }
            _ = temporary.path.withCString { unlink($0) }
        }
        try data.withUnsafeBytes { bytes in
            guard let baseAddress = bytes.baseAddress else { return }
            var offset = 0
            while offset < bytes.count {
                let result = Darwin.write(
                    openedDescriptor,
                    baseAddress.advanced(by: offset),
                    bytes.count - offset)
                if result < 0, errno == EINTR { continue }
                guard result > 0 else {
                    throw AppError.processFailed(
                        "Cursor 모델 카탈로그를 저장하지 못했습니다: \(String(cString: strerror(errno)))")
                }
                offset += result
            }
        }
        guard fchmod(openedDescriptor, S_IRUSR | S_IWUSR) == 0,
              fsync(openedDescriptor) == 0
        else {
            throw AppError.processFailed(
                "Cursor 모델 카탈로그를 안전하게 저장하지 못했습니다: \(String(cString: strerror(errno)))")
        }
        let closeResult = close(openedDescriptor)
        descriptor = nil
        guard closeResult == 0 else {
            throw AppError.processFailed(
                "Cursor 모델 카탈로그 저장을 마치지 못했습니다: \(String(cString: strerror(errno)))")
        }
        guard rename(temporary.path, destination.path) == 0 else {
            throw AppError.processFailed(
                "Cursor 모델 카탈로그를 원자적으로 저장하지 못했습니다: \(String(cString: strerror(errno)))")
        }
        try requireOwnedRegularFile(destination)
    }

    @discardableResult
    private func requireOwnedRegularFile(_ url: URL) throws -> Int64 {
        var info = stat()
        let result = url.path.withCString { lstat($0, &info) }
        guard result == 0,
              (info.st_mode & S_IFMT) == S_IFREG,
              info.st_uid == getuid(),
              (info.st_mode & 0o077) == 0
        else {
            throw AppError.processFailed("Cursor 모델 카탈로그 파일이 안전한 개인 파일이 아닙니다.")
        }
        return Int64(info.st_size)
    }

    private func pathEntryExists(_ url: URL) -> Bool {
        var info = stat()
        return url.path.withCString { lstat($0, &info) } == 0
    }
}
