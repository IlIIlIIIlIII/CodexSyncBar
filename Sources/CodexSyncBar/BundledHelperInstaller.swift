import Darwin
import Foundation

struct BundledHelperInstaller {
    let home: URL
    let resourceDirectory: URL

    init(
        home: URL = FileManager.default.homeDirectoryForCurrentUser,
        resourceDirectory: URL)
    {
        self.home = home
        self.resourceDirectory = resourceDirectory
    }

    static func installFromMainBundleIfPresent() throws {
        guard let resources = Bundle.main.resourceURL else { return }
        let helper = resources.appendingPathComponent("gpt-switch")
        let askpass = resources.appendingPathComponent("codex-syncbar-askpass")
        let usageSummary = resources.appendingPathComponent("usage-summary.mjs")
        guard FileManager.default.fileExists(atPath: helper.path),
              FileManager.default.fileExists(atPath: askpass.path),
              FileManager.default.fileExists(atPath: usageSummary.path)
        else { return }
        try Self(resourceDirectory: resources).install()
    }

    func install() throws {
        let fileManager = FileManager.default
        if fileManager.fileExists(atPath: home.path) {
            let values = try home.resourceValues(forKeys: [.isDirectoryKey, .isSymbolicLinkKey])
            guard values.isDirectory == true, values.isSymbolicLink != true else {
                throw AppError.processFailed("사용자 홈 경로가 안전한 디렉터리가 아닙니다.")
            }
        } else {
            try fileManager.createDirectory(at: home, withIntermediateDirectories: true)
        }
        let local = home.appendingPathComponent(".local", isDirectory: true)
        let bin = local.appendingPathComponent("bin", isDirectory: true)
        let library = local.appendingPathComponent("lib", isDirectory: true)
        let helperLibrary = library.appendingPathComponent("gpt-switch", isDirectory: true)
        for directory in [local, bin, library, helperLibrary] {
            try ensureSafeDirectory(directory)
        }

        try installResource(
            named: "gpt-switch",
            to: bin.appendingPathComponent("gpt-switch"),
            permissions: 0o755)
        try installResource(
            named: "codex-syncbar-askpass",
            to: helperLibrary.appendingPathComponent("codex-syncbar-askpass"),
            permissions: 0o700)
        try installResource(
            named: "usage-summary.mjs",
            to: helperLibrary.appendingPathComponent("usage-summary.mjs"),
            permissions: 0o755)
    }

    private func ensureSafeDirectory(_ url: URL) throws {
        let fileManager = FileManager.default
        if fileManager.fileExists(atPath: url.path) {
            let values = try url.resourceValues(forKeys: [.isDirectoryKey, .isSymbolicLinkKey])
            guard values.isDirectory == true, values.isSymbolicLink != true else {
                throw AppError.processFailed("helper 설치 경로가 안전한 디렉터리가 아닙니다: \(url.path)")
            }
        } else {
            try fileManager.createDirectory(
                at: url,
                withIntermediateDirectories: false,
                attributes: [.posixPermissions: 0o700])
        }
    }

    private func installResource(named name: String, to destination: URL, permissions: Int) throws {
        let fileManager = FileManager.default
        let source = resourceDirectory.appendingPathComponent(name)
        let sourceValues = try source.resourceValues(forKeys: [.isRegularFileKey, .isSymbolicLinkKey])
        guard sourceValues.isRegularFile == true, sourceValues.isSymbolicLink != true else {
            throw AppError.processFailed("번들 helper가 안전한 일반 파일이 아닙니다: \(name)")
        }
        let sourceData = try Data(contentsOf: source)
        guard !sourceData.isEmpty else { throw AppError.processFailed("번들 helper가 비어 있습니다: \(name)") }

        if fileManager.fileExists(atPath: destination.path) {
            let values = try destination.resourceValues(forKeys: [.isRegularFileKey, .isSymbolicLinkKey])
            guard values.isRegularFile == true, values.isSymbolicLink != true else {
                throw AppError.processFailed("기존 helper가 안전한 일반 파일이 아닙니다: \(destination.path)")
            }
            if try Data(contentsOf: destination) == sourceData {
                try fileManager.setAttributes([.posixPermissions: permissions], ofItemAtPath: destination.path)
                return
            }
        }

        let temporary = destination.deletingLastPathComponent()
            .appendingPathComponent(".\(name).\(UUID().uuidString).tmp")
        defer { try? fileManager.removeItem(at: temporary) }
        try sourceData.write(to: temporary, options: [.atomic])
        try fileManager.setAttributes([.posixPermissions: permissions], ofItemAtPath: temporary.path)
        guard rename(temporary.path, destination.path) == 0 else {
            throw AppError.processFailed("helper를 원자적으로 설치하지 못했습니다: \(String(cString: strerror(errno)))")
        }
        guard try Data(contentsOf: destination) == sourceData else {
            throw AppError.processFailed("설치된 helper 검증에 실패했습니다: \(name)")
        }
    }
}
