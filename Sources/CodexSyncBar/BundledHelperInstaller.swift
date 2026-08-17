import CryptoKit
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
        let cursorBridge = resources.appendingPathComponent("cursor-codex-bridge.mjs")
        let cursorFileExtractor = resources.appendingPathComponent("cursor-file-extractor")
        let cursorRemoteManager = resources.appendingPathComponent("cursor-remote-manager.mjs")
        let cursorSDKRuntime = resources.appendingPathComponent("cursor-sdk-runtime.tar.gz")
        let cursorSDKManifest = resources.appendingPathComponent("cursor-sdk-runtime.manifest")
        guard FileManager.default.fileExists(atPath: helper.path),
              FileManager.default.fileExists(atPath: askpass.path),
              FileManager.default.fileExists(atPath: usageSummary.path),
              FileManager.default.fileExists(atPath: cursorBridge.path),
              FileManager.default.fileExists(atPath: cursorFileExtractor.path),
              FileManager.default.fileExists(atPath: cursorRemoteManager.path),
              FileManager.default.fileExists(atPath: cursorSDKRuntime.path),
              FileManager.default.fileExists(atPath: cursorSDKManifest.path)
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
        try installResource(
            named: "cursor-file-extractor",
            to: helperLibrary.appendingPathComponent("cursor-file-extractor"),
            permissions: 0o755)
        try installResource(
            named: "cursor-codex-bridge.mjs",
            to: helperLibrary.appendingPathComponent("cursor-codex-bridge.mjs"),
            permissions: 0o755)
        try installResource(
            named: "cursor-remote-manager.mjs",
            to: helperLibrary.appendingPathComponent("cursor-remote-manager.mjs"),
            permissions: 0o755)
        try installResource(
            named: "cursor-sdk-runtime.tar.gz",
            to: helperLibrary.appendingPathComponent("cursor-sdk-runtime.tar.gz"),
            permissions: 0o600)
        try installResource(
            named: "cursor-sdk-runtime.manifest",
            to: helperLibrary.appendingPathComponent("cursor-sdk-runtime.manifest"),
            permissions: 0o600)
        try installCursorSDKRuntime(in: helperLibrary)
    }

    private struct CursorSDKRuntimeManifest {
        let data: Data
        let archiveSHA256: String
    }

    private func installCursorSDKRuntime(in helperLibrary: URL) throws {
        let fileManager = FileManager.default
        let archive = helperLibrary.appendingPathComponent("cursor-sdk-runtime.tar.gz")
        let manifestURL = helperLibrary.appendingPathComponent("cursor-sdk-runtime.manifest")
        let installedManifestURL = helperLibrary.appendingPathComponent(
            ".cursor-sdk-runtime-installed.manifest")
        let manifest = try validatedCursorSDKManifest(at: manifestURL)
        let archiveData = try Data(contentsOf: archive, options: [.mappedIfSafe])
        let actualHash = SHA256.hash(data: archiveData)
            .map { String(format: "%02x", $0) }
            .joined()
        guard actualHash == manifest.archiveSHA256 else {
            throw AppError.processFailed("번들 Cursor SDK 런타임 해시가 일치하지 않습니다.")
        }

        let destination = helperLibrary.appendingPathComponent("node_modules", isDirectory: true)
        if fileManager.fileExists(atPath: installedManifestURL.path),
           try Data(contentsOf: installedManifestURL) == manifest.data,
           (try? validateCursorSDKRuntime(at: destination)) != nil
        {
            return
        }

        try validateCursorSDKArchive(at: archive)
        let stagingRoot = helperLibrary.appendingPathComponent(
            ".cursor-sdk-runtime-stage.\(UUID().uuidString)",
            isDirectory: true)
        let backup = helperLibrary.appendingPathComponent(
            ".cursor-sdk-runtime-backup.\(UUID().uuidString)",
            isDirectory: true)
        try fileManager.createDirectory(
            at: stagingRoot,
            withIntermediateDirectories: false,
            attributes: [.posixPermissions: 0o700])
        defer {
            try? fileManager.removeItem(at: stagingRoot)
            try? fileManager.removeItem(at: backup)
        }
        try runTar(["-xzf", archive.path, "-C", stagingRoot.path,
                    "--no-same-owner", "--no-same-permissions"])
        let stagedRuntime = stagingRoot.appendingPathComponent("node_modules", isDirectory: true)
        try validateCursorSDKRuntime(at: stagedRuntime)

        var movedExisting = false
        do {
            if fileManager.fileExists(atPath: destination.path) {
                let values = try destination.resourceValues(
                    forKeys: [.isDirectoryKey, .isSymbolicLinkKey])
                guard values.isDirectory == true, values.isSymbolicLink != true else {
                    throw AppError.processFailed("기존 Cursor SDK 런타임 경로가 안전하지 않습니다.")
                }
                try fileManager.moveItem(at: destination, to: backup)
                movedExisting = true
            }
            try fileManager.moveItem(at: stagedRuntime, to: destination)
            try installData(manifest.data, to: installedManifestURL, permissions: 0o600)
            if movedExisting { try fileManager.removeItem(at: backup) }
        } catch {
            if fileManager.fileExists(atPath: destination.path) {
                try? fileManager.removeItem(at: destination)
            }
            if movedExisting, fileManager.fileExists(atPath: backup.path) {
                try? fileManager.moveItem(at: backup, to: destination)
            }
            throw error
        }
    }

    private func validatedCursorSDKManifest(at url: URL) throws -> CursorSDKRuntimeManifest {
        let data = try Data(contentsOf: url)
        guard data.count <= 4 * 1_024,
              let text = String(data: data, encoding: .utf8)
        else {
            throw AppError.processFailed("Cursor SDK 런타임 manifest가 올바르지 않습니다.")
        }
        let lines = text.split(separator: "\n", omittingEmptySubsequences: false)
        guard lines.count == 5, lines[0] == "schema_version=1",
              lines[1] == "sdk_version=1.0.28",
              lines[2].hasPrefix("lock_sha256="),
              lines[3].hasPrefix("archive_sha256="),
              lines[4].isEmpty
        else {
            throw AppError.processFailed("Cursor SDK 런타임 manifest가 올바르지 않습니다.")
        }
        var fields: [String: String] = [:]
        for line in lines.dropLast() {
            guard let separator = line.firstIndex(of: "=") else {
                throw AppError.processFailed("Cursor SDK 런타임 manifest가 올바르지 않습니다.")
            }
            let key = String(line[..<separator])
            guard fields[key] == nil else {
                throw AppError.processFailed("Cursor SDK 런타임 manifest가 올바르지 않습니다.")
            }
            fields[key] = String(line[line.index(after: separator)...])
        }
        guard fields.count == 4,
              fields["archive_sha256"]?.range(
                of: "^[a-f0-9]{64}$", options: .regularExpression) != nil,
              fields["lock_sha256"]?.range(
                of: "^[a-f0-9]{64}$", options: .regularExpression) != nil,
              let archiveHash = fields["archive_sha256"]
        else {
            throw AppError.processFailed("Cursor SDK 런타임 manifest가 올바르지 않습니다.")
        }
        return CursorSDKRuntimeManifest(data: data, archiveSHA256: archiveHash)
    }

    private func validateCursorSDKArchive(at archive: URL) throws {
        let paths = try runTar(["-tzf", archive.path])
        let details = try runTar(["-tvzf", archive.path])
        let entries = paths.split(whereSeparator: \.isNewline)
        let detailLines = details.split(whereSeparator: \.isNewline)
        guard !entries.isEmpty, entries.count == detailLines.count, entries.count <= 20_000 else {
            throw AppError.processFailed("Cursor SDK 런타임 archive 구성이 올바르지 않습니다.")
        }
        for (entry, detail) in zip(entries, detailLines) {
            let value = String(entry)
            let components = value.split(separator: "/", omittingEmptySubsequences: true)
            guard !value.hasPrefix("/"), !value.contains("\\"),
                  components.first == "node_modules",
                  !components.contains("."), !components.contains(".."),
                  let kind = detail.first, kind == "-" || kind == "d"
            else {
                throw AppError.processFailed("Cursor SDK 런타임 archive에 안전하지 않은 항목이 있습니다.")
            }
        }
    }

    @discardableResult
    private func runTar(_ arguments: [String]) throws -> String {
        let process = Process()
        let output = Pipe()
        process.executableURL = URL(fileURLWithPath: "/usr/bin/tar")
        process.arguments = arguments
        process.standardInput = FileHandle.nullDevice
        process.standardOutput = output
        process.standardError = output
        try process.run()
        output.fileHandleForWriting.closeFile()
        let data = output.fileHandleForReading.readDataToEndOfFile()
        process.waitUntilExit()
        guard process.terminationStatus == 0, data.count <= 8 * 1_024 * 1_024 else {
            throw AppError.processFailed("Cursor SDK 런타임 archive를 처리하지 못했습니다.")
        }
        return String(decoding: data, as: UTF8.self)
    }

    private func validateCursorSDKRuntime(at root: URL) throws {
        let fileManager = FileManager.default
        let rootValues = try root.resourceValues(forKeys: [.isDirectoryKey, .isSymbolicLinkKey])
        guard rootValues.isDirectory == true, rootValues.isSymbolicLink != true else {
            throw AppError.processFailed("Cursor SDK 런타임 디렉터리가 안전하지 않습니다.")
        }
        try requireSafeRuntimeItem(root)
        var enumerationFailed = false
        guard let enumerator = fileManager.enumerator(
            at: root,
            includingPropertiesForKeys: [
                .isRegularFileKey, .isDirectoryKey, .isSymbolicLinkKey, .fileSizeKey,
            ],
            options: [],
            errorHandler: { _, _ in
                enumerationFailed = true
                return false
            })
        else {
            throw AppError.processFailed("Cursor SDK 런타임을 검증하지 못했습니다.")
        }
        var itemCount = 0
        var totalBytes = 0
        for case let item as URL in enumerator {
            itemCount += 1
            let values = try item.resourceValues(forKeys: [
                .isRegularFileKey, .isDirectoryKey, .isSymbolicLinkKey, .fileSizeKey,
            ])
            guard itemCount <= 20_000, values.isSymbolicLink != true,
                  values.isRegularFile == true || values.isDirectory == true
            else {
                throw AppError.processFailed("Cursor SDK 런타임에 안전하지 않은 항목이 있습니다.")
            }
            try requireSafeRuntimeItem(item)
            if values.isRegularFile == true {
                totalBytes += values.fileSize ?? 0
                guard totalBytes <= 256 * 1_024 * 1_024 else {
                    throw AppError.processFailed("Cursor SDK 런타임 크기가 허용 범위를 초과했습니다.")
                }
            }
        }
        guard !enumerationFailed else {
            throw AppError.processFailed("Cursor SDK 런타임을 모두 검증하지 못했습니다.")
        }
        for package in [
            "sdk", "sdk-darwin-arm64", "sdk-darwin-x64", "sdk-linux-arm64", "sdk-linux-x64",
        ] {
            let metadataURL = root.appendingPathComponent("@cursor/\(package)/package.json")
            let object = try JSONSerialization.jsonObject(with: Data(contentsOf: metadataURL))
            guard let metadata = object as? [String: Any],
                  metadata["name"] as? String == "@cursor/\(package)",
                  metadata["version"] as? String == "1.0.28"
            else {
                throw AppError.processFailed("Cursor SDK 런타임 패키지 버전이 올바르지 않습니다.")
            }
        }
    }

    private func requireSafeRuntimeItem(_ url: URL) throws {
        var info = stat()
        guard url.path.withCString({ lstat($0, &info) }) == 0,
              info.st_uid == getuid(),
              (info.st_mode & 0o022) == 0,
              (info.st_mode & S_IFMT) == S_IFREG || (info.st_mode & S_IFMT) == S_IFDIR
        else {
            throw AppError.processFailed("Cursor SDK 런타임 항목의 소유권 또는 권한이 안전하지 않습니다.")
        }
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

    private func installData(_ data: Data, to destination: URL, permissions: Int) throws {
        let fileManager = FileManager.default
        if fileManager.fileExists(atPath: destination.path) {
            let values = try destination.resourceValues(forKeys: [.isRegularFileKey, .isSymbolicLinkKey])
            guard values.isRegularFile == true, values.isSymbolicLink != true else {
                throw AppError.processFailed("기존 helper 상태 파일이 안전하지 않습니다: \(destination.path)")
            }
            if try Data(contentsOf: destination) == data {
                try fileManager.setAttributes([.posixPermissions: permissions], ofItemAtPath: destination.path)
                return
            }
        }
        let temporary = destination.deletingLastPathComponent()
            .appendingPathComponent(".\(destination.lastPathComponent).\(UUID().uuidString).tmp")
        defer { try? fileManager.removeItem(at: temporary) }
        try data.write(to: temporary, options: [.atomic])
        try fileManager.setAttributes([.posixPermissions: permissions], ofItemAtPath: temporary.path)
        guard rename(temporary.path, destination.path) == 0 else {
            throw AppError.processFailed("helper 상태 파일을 원자적으로 설치하지 못했습니다: \(String(cString: strerror(errno)))")
        }
    }
}
