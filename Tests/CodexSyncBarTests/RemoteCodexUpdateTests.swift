import Foundation
import XCTest
@testable import CodexSyncBar

final class RemoteCodexUpdateTests: XCTestCase {
    func testUpdateAndReconnectStatesRemainDistinct() {
        let ready = RemoteCodexUpdateResult(deviceID: "server", exitStatus: 0,
            output: "npm output\nbefore=1.0.0 after=2.0.0 manager=npm restart=reconnected\n")
        XCTAssertTrue(ready.succeeded)
        XCTAssertTrue(ready.detail.contains("재시작 확인"))
        let pending = RemoteCodexUpdateResult(deviceID: "server", exitStatus: 2,
            output: "before=1.0.0 after=2.0.0 manager=npm restart=reconnect-pending\n")
        XCTAssertFalse(pending.succeeded)
        XCTAssertTrue(pending.detail.contains("다시 연결"))
        let stopped = RemoteCodexUpdateResult(deviceID: "server", exitStatus: 0,
            output: "before=1.0.0 after=2.0.0 manager=npm restart=not-running\n")
        XCTAssertTrue(stopped.succeeded)
        XCTAssertTrue(stopped.detail.contains("실행 중인 SSH Codex 없음"))
        XCTAssertFalse(RemoteCodexUpdateResult(deviceID: "server", exitStatus: 0,
            output: "npm install succeeded").succeeded)
        for output in [
            "after=2.0.0 restart=reconnected",
            "before=1.0.0 after=broken manager=npm restart=reconnected",
            "before=1.0.0 after=2.0.0 manager=npm restart=reconnect-pending restart=reconnected",
        ] {
            XCTAssertFalse(RemoteCodexUpdateResult(deviceID: "server", exitStatus: 0, output: output).succeeded)
        }
    }

    func testServicePreservesPerDeviceFailureAndValidatesIdentity() async throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: root) }
        let helper = root.appendingPathComponent("helper")
        func write(_ body: String) throws {
            try Data(("#!/bin/bash\n[ \"$1\" = update-codex ] && [ \"$2\" = server ] || exit 64\n" + body).utf8)
                .write(to: helper)
        }
        try write("echo '{\"deviceID\":\"server\",\"exitStatus\":255,\"output\":\"SSH connection failed\"}'\nexit 2\n")
        let service = SwitchService(executable: helper)
        let failed = try await service.updateRemoteCodex(deviceID: "server")
        XCTAssertFalse(failed.succeeded)
        XCTAssertEqual(failed.exitStatus, 255)
        try write("echo '{\"deviceID\":\"other\",\"exitStatus\":255,\"output\":\"SSH connection failed\"}'\nexit 2\n")
        do {
            _ = try await service.updateRemoteCodex(deviceID: "server")
            XCTFail("Accepted a response for the wrong device")
        } catch { }
        try write("echo '{\"deviceID\":\"server\",\"exitStatus\":0,\"output\":\"unverified\"}'\n")
        do {
            _ = try await service.updateRemoteCodex(deviceID: "server")
            XCTFail("Accepted success without version/restart verification")
        } catch { }
    }
}
