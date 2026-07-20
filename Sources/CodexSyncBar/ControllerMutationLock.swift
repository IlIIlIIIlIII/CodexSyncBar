import Darwin
import Foundation

@_silgen_name("flock")
private func systemFlock(_ descriptor: Int32, _ operation: Int32) -> Int32

enum ControllerMutationLockError: LocalizedError {
    case busy
    case unsafe(String)
    case unavailable(String)

    var errorDescription: String? {
        switch self {
        case .busy:
            "다른 계정 작업이 진행 중입니다. 완료 후 다시 시도해 주세요."
        case let .unsafe(message), let .unavailable(message):
            message
        }
    }
}

/// Cross-process lock shared with `gpt-switch`.
///
/// The owner record is completely written before `link(2)` publishes it at
/// `.controller-lock`. Hard-link creation is the single atomic ownership
/// boundary: it cannot expose an ownerless lock or replace a current owner.
/// Release verifies both the unique token and the acquired inode before one
/// `unlink(2)`, so it cannot remove a successor's lock.
struct ControllerMutationLock {
    private struct Ownership {
        let token: String
        let device: dev_t
        let inode: ino_t
        let gateDescriptor: Int32
    }

    private struct OwnerRecord {
        let pid: pid_t
        let token: String?
    }

    private enum OwnerFormat {
        case atomic
        case legacyDirectory
    }

    let stateRoot: URL

    init(home: URL = FileManager.default.homeDirectoryForCurrentUser) {
        stateRoot = home.appendingPathComponent(".local/share/gpt-switch", isDirectory: true)
    }

    var lockURL: URL {
        stateRoot.appendingPathComponent(".controller-lock")
    }

    var gateURL: URL {
        stateRoot.appendingPathComponent(".controller-gate")
    }

    func withLock<T>(_ operation: () throws -> T) throws -> T {
        try ensureStateRoot()
        let ownership = try acquire()
        defer { release(ownership) }
        return try operation()
    }

    static func isBusy(_ error: Error) -> Bool {
        guard let lockError = error as? ControllerMutationLockError else { return false }
        if case .busy = lockError { return true }
        return false
    }

    private func ensureStateRoot() throws {
        let fileManager = FileManager.default
        if pathEntryExists(stateRoot) {
            let values = try stateRoot.resourceValues(forKeys: [.isDirectoryKey, .isSymbolicLinkKey])
            guard values.isDirectory == true, values.isSymbolicLink != true else {
                throw ControllerMutationLockError.unsafe("gpt-switch 상태 경로가 안전하지 않습니다.")
            }
        } else {
            try fileManager.createDirectory(
                at: stateRoot,
                withIntermediateDirectories: true,
                attributes: [.posixPermissions: 0o700])
        }
        try fileManager.setAttributes([.posixPermissions: 0o700], ofItemAtPath: stateRoot.path)
    }

    private func acquire() throws -> Ownership {
        let gateDescriptor = try acquireGate()
        var keepGate = false
        defer {
            if !keepGate {
                _ = systemFlock(gateDescriptor, LOCK_UN)
                _ = Darwin.close(gateDescriptor)
            }
        }
        let fileManager = FileManager.default
        let token = UUID().uuidString
        let temporary = stateRoot.appendingPathComponent(".controller-owner.\(token)")
        defer { try? fileManager.removeItem(at: temporary) }
        do {
            try Data("pid=\(getpid())\ntoken=\(token)\n".utf8)
                .write(to: temporary, options: [.withoutOverwriting])
            try fileManager.setAttributes([.posixPermissions: 0o600], ofItemAtPath: temporary.path)
        } catch {
            throw ControllerMutationLockError.unavailable("계정 작업 잠금 소유자를 준비하지 못했습니다.")
        }

        while true {
            let linkResult = temporary.path.withCString { source in
                lockURL.path.withCString { destination in Darwin.link(source, destination) }
            }
            if linkResult == 0 {
                let info = try secureRegularFileInfo(at: lockURL)
                keepGate = true
                return Ownership(
                    token: token,
                    device: info.st_dev,
                    inode: info.st_ino,
                    gateDescriptor: gateDescriptor)
            }
            guard errno == EEXIST else {
                throw ControllerMutationLockError.unavailable(
                    "계정 작업 잠금을 만들지 못했습니다: \(String(cString: strerror(errno)))")
            }

            var info = stat()
            guard lockURL.path.withCString({ lstat($0, &info) }) == 0 else {
                if errno == ENOENT { continue }
                throw ControllerMutationLockError.unsafe("계정 작업 잠금 경로를 확인하지 못했습니다.")
            }
            switch info.st_mode & S_IFMT {
            case S_IFREG:
                _ = try secureRegularFileInfo(at: lockURL)
                guard let owner = try readOwner(at: lockURL, format: .atomic) else {
                    throw ControllerMutationLockError.unsafe("계정 작업 잠금 소유자 파일이 손상되었습니다.")
                }
                if processIsAlive(owner.pid) { throw ControllerMutationLockError.busy }
                try detachAndRemoveStaleFile()
            case S_IFDIR:
                try recoverDeadLegacyDirectory()
            default:
                throw ControllerMutationLockError.unsafe("계정 작업 잠금 경로 형식이 안전하지 않습니다.")
            }
        }
    }

    private func release(_ ownership: Ownership) {
        defer {
            _ = systemFlock(ownership.gateDescriptor, LOCK_UN)
            _ = Darwin.close(ownership.gateDescriptor)
        }
        guard let info = try? secureRegularFileInfo(at: lockURL),
              info.st_dev == ownership.device,
              info.st_ino == ownership.inode,
              let owner = try? readOwner(at: lockURL, format: .atomic),
              owner.token == ownership.token
        else { return }
        _ = lockURL.path.withCString { Darwin.unlink($0) }
    }

    /// The permanent advisory gate serializes stale-owner recovery across the
    /// Swift app and the shell helper. Without it, two cleaners can both see
    /// a dead owner and the second can detach a newly published live lock.
    private func acquireGate() throws -> Int32 {
        let descriptor = gateURL.path.withCString {
            Darwin.open($0, O_RDWR | O_CREAT | O_NOFOLLOW, S_IRUSR | S_IWUSR)
        }
        guard descriptor >= 0 else {
            if errno == ELOOP {
                throw ControllerMutationLockError.unsafe("계정 작업 잠금 게이트가 안전하지 않습니다.")
            }
            throw ControllerMutationLockError.unavailable(
                "계정 작업 잠금 게이트를 열지 못했습니다: \(String(cString: strerror(errno)))")
        }
        var info = stat()
        guard Darwin.fstat(descriptor, &info) == 0,
              (info.st_mode & S_IFMT) == S_IFREG,
              info.st_uid == getuid()
        else {
            _ = Darwin.close(descriptor)
            throw ControllerMutationLockError.unsafe("계정 작업 잠금 게이트가 안전하지 않습니다.")
        }
        guard Darwin.fchmod(descriptor, S_IRUSR | S_IWUSR) == 0 else {
            _ = Darwin.close(descriptor)
            throw ControllerMutationLockError.unavailable("계정 작업 잠금 게이트 권한을 설정하지 못했습니다.")
        }
        guard systemFlock(descriptor, LOCK_EX | LOCK_NB) == 0 else {
            let lockError = errno
            _ = Darwin.close(descriptor)
            if lockError == EWOULDBLOCK || lockError == EAGAIN {
                throw ControllerMutationLockError.busy
            }
            throw ControllerMutationLockError.unavailable(
                "계정 작업 잠금 게이트를 획득하지 못했습니다: \(String(cString: strerror(lockError)))")
        }
        return descriptor
    }

    private func detachAndRemoveStaleFile() throws {
        let staleURL = stateRoot.appendingPathComponent(".controller-lock.stale.\(UUID().uuidString)")
        let result = lockURL.path.withCString { source in
            staleURL.path.withCString { destination in Darwin.rename(source, destination) }
        }
        if result != 0 {
            if errno == ENOENT { return }
            throw ControllerMutationLockError.busy
        }
        if let owner = try readOwner(at: staleURL, format: .atomic), processIsAlive(owner.pid) {
            _ = staleURL.path.withCString { source in
                lockURL.path.withCString { destination in Darwin.rename(source, destination) }
            }
            throw ControllerMutationLockError.busy
        }
        try FileManager.default.removeItem(at: staleURL)
    }

    /// One-time compatibility for a dead directory lock left by a pre-2.0
    /// build. Ownerless or malformed legacy directories remain fail-closed.
    private func recoverDeadLegacyDirectory() throws {
        guard let owner = try validatedLegacyDirectoryOwner(at: lockURL) else {
            throw ControllerMutationLockError.unsafe("이전 형식의 계정 작업 잠금이 손상되었습니다.")
        }
        if processIsAlive(owner.pid) { throw ControllerMutationLockError.busy }
        let staleURL = stateRoot.appendingPathComponent(".controller-lock.legacy.\(UUID().uuidString)")
        guard lockURL.path.withCString({ source in
            staleURL.path.withCString { destination in Darwin.rename(source, destination) }
        }) == 0 else {
            if errno == ENOENT { return }
            throw ControllerMutationLockError.busy
        }
        let movedOwner: OwnerRecord
        do {
            guard let validated = try validatedLegacyDirectoryOwner(at: staleURL) else {
                throw ControllerMutationLockError.unsafe("이전 형식의 계정 작업 잠금이 변경되었습니다.")
            }
            movedOwner = validated
        } catch {
            _ = staleURL.path.withCString { source in
                lockURL.path.withCString { destination in Darwin.rename(source, destination) }
            }
            throw error
        }
        if processIsAlive(movedOwner.pid) {
            _ = staleURL.path.withCString { source in
                lockURL.path.withCString { destination in Darwin.rename(source, destination) }
            }
            throw ControllerMutationLockError.busy
        }
        try FileManager.default.removeItem(at: staleURL)
    }

    private func validatedLegacyDirectoryOwner(at directory: URL) throws -> OwnerRecord? {
        var info = stat()
        guard directory.path.withCString({ lstat($0, &info) }) == 0,
              (info.st_mode & S_IFMT) == S_IFDIR,
              info.st_uid == getuid(),
              (info.st_mode & 0o777) == 0o700
        else {
            throw ControllerMutationLockError.unsafe("이전 형식의 계정 작업 잠금 경로가 안전하지 않습니다.")
        }
        let names = try FileManager.default.contentsOfDirectory(atPath: directory.path)
        guard names == ["owner"] else {
            throw ControllerMutationLockError.unsafe("이전 형식의 계정 작업 잠금 내용이 안전하지 않습니다.")
        }
        return try readOwner(
            at: directory.appendingPathComponent("owner"),
            format: .legacyDirectory)
    }

    private func secureRegularFileInfo(at url: URL) throws -> stat {
        var info = stat()
        guard url.path.withCString({ lstat($0, &info) }) == 0 else {
            throw ControllerMutationLockError.unsafe("계정 작업 잠금 파일을 확인하지 못했습니다.")
        }
        guard (info.st_mode & S_IFMT) == S_IFREG,
              info.st_uid == getuid(),
              (info.st_mode & 0o777) == 0o600,
              info.st_size > 0,
              info.st_size <= 1024
        else {
            throw ControllerMutationLockError.unsafe("계정 작업 잠금 파일이 안전하지 않습니다.")
        }
        return info
    }

    private func readOwner(at url: URL, format: OwnerFormat) throws -> OwnerRecord? {
        _ = try secureRegularFileInfo(at: url)
        let text = try String(contentsOf: url, encoding: .utf8)
        var fields: [String: String] = [:]
        for line in text.split(whereSeparator: \.isNewline) {
            guard let separator = line.firstIndex(of: "=") else {
                throw ControllerMutationLockError.unsafe("계정 작업 잠금 파일이 손상되었습니다.")
            }
            let key = String(line[..<separator])
            guard fields[key] == nil else {
                throw ControllerMutationLockError.unsafe("계정 작업 잠금 파일에 중복 필드가 있습니다.")
            }
            fields[key] = String(line[line.index(after: separator)...])
        }
        guard let rawPID = fields["pid"], let pid = pid_t(rawPID), pid > 0 else { return nil }
        switch format {
        case .atomic:
            guard Set(fields.keys) == Set(["pid", "token"]),
                  let token = fields["token"],
                  !token.isEmpty,
                  token.range(of: #"^[A-Za-z0-9._-]{1,128}$"#, options: .regularExpression) != nil
            else { return nil }
            return OwnerRecord(pid: pid, token: token)
        case .legacyDirectory:
            guard Set(fields.keys) == Set(["pid"]) else { return nil }
            return OwnerRecord(pid: pid, token: nil)
        }
    }

    private func processIsAlive(_ pid: pid_t) -> Bool {
        if Darwin.kill(pid, 0) == 0 { return true }
        return errno == EPERM
    }

    private func pathEntryExists(_ url: URL) -> Bool {
        var info = stat()
        return url.path.withCString { lstat($0, &info) } == 0
    }
}
