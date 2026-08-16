import Foundation
import XCTest
@testable import CodexSyncBar

final class CursorRemoteProvisioningTests: XCTestCase {
    func testRequestValidationAndEncodingKeepSecretsOutOfMetadata() throws {
        let apiKey = "cursor_" + String(repeating: "a", count: 32)
        let token = String(repeating: "b", count: 64)
        let modelParameters = [
            "gpt-5.6-sol-high": CursorACPModelParameters(
                model: "gpt-5.6-sol",
                context: "1m",
                effort: .high,
                fast: false,
                thinking: false),
            "composer-2.5": CursorACPModelParameters(
                model: "composer-2.5",
                context: nil,
                effort: nil,
                fast: false,
                thinking: false),
        ]
        let request = try CursorRemoteProvisioningRequest(
            apiKey: apiKey,
            model: "gpt-5.6-sol-high",
            port: 32125,
            bridgeToken: token,
            models: ["gpt-5.6-sol-high", "composer-2.5", "composer-2.5"],
            modelParameters: modelParameters)

        XCTAssertEqual(request.schemaVersion, 1)
        XCTAssertEqual(request.models, ["gpt-5.6-sol-high", "composer-2.5"])
        XCTAssertEqual(request.modelParameters, modelParameters)
        let data = try JSONEncoder().encode(request)
        let object = try XCTUnwrap(JSONSerialization.jsonObject(with: data) as? [String: Any])
        let encodedParameters = try XCTUnwrap(object["modelParameters"] as? [String: Any])
        XCTAssertEqual(Set(encodedParameters.keys), Set(request.models))
        let composer = try XCTUnwrap(encodedParameters["composer-2.5"] as? [String: Any])
        XCTAssertNil(composer["context"])
        XCTAssertNil(composer["effort"])
        let decoded = try JSONDecoder().decode(CursorRemoteProvisioningRequest.self, from: data)
        XCTAssertEqual(decoded, request)
    }

    func testRequestRejectsNonASCIISlugsAndMissingSelectedModel() {
        let apiKey = String(repeating: "a", count: 32)
        let token = String(repeating: "b", count: 64)
        XCTAssertThrowsError(try CursorRemoteProvisioningRequest(
            apiKey: apiKey,
            model: "composer-2.5",
            port: 32125,
            bridgeToken: token,
            models: ["composer-2.5", "모델-1"],
            modelParameters: parameters(for: ["composer-2.5", "모델-1"])))
        XCTAssertThrowsError(try CursorRemoteProvisioningRequest(
            apiKey: apiKey,
            model: "composer-2.5",
            port: 32125,
            bridgeToken: token,
            models: ["gpt-5.6-sol-high"],
            modelParameters: parameters(for: ["gpt-5.6-sol-high"])))

        XCTAssertThrowsError(try CursorRemoteProvisioningRequest(
            apiKey: apiKey,
            model: "composer-2.5",
            port: 32125,
            bridgeToken: token,
            models: ["composer-2.5"],
            modelParameters: parameters(for: ["composer-2.5", "extra-model"])))
        XCTAssertThrowsError(try CursorRemoteProvisioningRequest(
            apiKey: apiKey,
            model: "composer-2.5",
            port: 32125,
            bridgeToken: token,
            models: ["composer-2.5"],
            modelParameters: [
                "composer-2.5": CursorACPModelParameters(
                    model: "composer-2.5",
                    context: "2m",
                    effort: .default,
                    fast: false,
                    thinking: false),
            ]))
    }

    func testProvisioningSummaryParserAcceptsOnlyExactSafeSummary() throws {
        XCTAssertEqual(
            try SwitchService.parseCursorProvisioningResult(
                "SSH banner\ndevice=build-server cursor=provisioned result=ok version=2.2.0\n",
                expectedDeviceID: "build-server"),
            "2.2.0")
        XCTAssertEqual(
            try SwitchService.parseCursorProvisioningResult(
                "device=build-server cursor=provisioned result=ok version=2.2.0 reload=pending\n",
                expectedDeviceID: "build-server"),
            "2.2.0")
        for output in [
            "device=build-server cursor=provisioned result=ok",
            "device=build-server cursor=provisioned result=ok version=2.2.0 extra=value",
            "device=build-server cursor=provisioned result=ok version=2.2.0 reload=failed",
            "device=other cursor=provisioned result=ok version=2.2.0",
            "device=build-server device=build-server cursor=provisioned result=ok version=2.2.0",
        ] {
            XCTAssertThrowsError(try SwitchService.parseCursorProvisioningResult(
                output,
                expectedDeviceID: "build-server"))
        }
    }

    func testDeprovisioningSummaryParserAcceptsOnlyExactSafeSummary() throws {
        XCTAssertEqual(
            try SwitchService.parseCursorDeprovisioningResult(
                "SSH banner\ndevice=build-server cursor=deprovisioned result=ok version=2.2.0\n",
                expectedDeviceID: "build-server"),
            "2.2.0")
        for output in [
            "device=build-server cursor=deprovisioned result=ok",
            "device=build-server cursor=deprovisioned result=ok version=2.2.0 extra=value",
            "device=other cursor=deprovisioned result=ok version=2.2.0",
            "device=build-server cursor=provisioned result=ok version=2.2.0",
        ] {
            XCTAssertThrowsError(try SwitchService.parseCursorDeprovisioningResult(
                output,
                expectedDeviceID: "build-server"))
        }
    }

    func testCursorOperationsExecuteTrustedHelperAndReturnOnlyNormalizedSummary() async throws {
        let root = try makeTemporaryDirectory(named: "TrustedHelper")
        defer { try? FileManager.default.removeItem(at: root) }
        let installed = root.appendingPathComponent("installed-gpt-switch")
        let trusted = root.appendingPathComponent("trusted-gpt-switch")
        let invocation = root.appendingPathComponent("invocation")
        let helperEnvironment = root.appendingPathComponent("helper-environment")
        let script = """
        #!/bin/bash
        printf '%s' "$0" >"\(invocation.path)"
        printf '%s\n%s\n' "$GPT_SWITCH_CURSOR_BRIDGE_HELPER" "$GPT_SWITCH_CURSOR_REMOTE_MANAGER" >"\(helperEnvironment.path)"
        case "$1" in
          provision-cursor)
            cat >/dev/null
            printf 'untrusted diagnostic\n'
            printf 'device=%s cursor=provisioned result=ok version=2.2.0 reload=pending\n' "$2"
            ;;
          deprovision-cursor)
            printf 'untrusted diagnostic\n'
            printf 'device=%s cursor=deprovisioned result=ok version=2.2.0\n' "$2"
            ;;
          *) exit 64 ;;
        esac
        """
        try writeHelper(script, to: installed)
        try writeHelper(script, to: trusted)
        let support = try writeMatchingCursorSupportHelpers(to: root)
        let service = makeService(
            executable: installed,
            trusted: trusted,
            support: support)

        let provisioned = try await service.provisionCursor(
            deviceID: "build-server",
            request: try request())
        XCTAssertEqual(
            provisioned.output,
            "device=build-server cursor=provisioned result=ok version=2.2.0 reload=pending\n")
        XCTAssertTrue(provisioned.requiresCodexReload)
        let executedPath = try String(contentsOf: invocation, encoding: .utf8)
        XCTAssertNotEqual(executedPath, trusted.path)
        XCTAssertEqual(URL(fileURLWithPath: executedPath).lastPathComponent, "gpt-switch")
        XCTAssertTrue(executedPath.contains("CodexSyncBarProvisioning-"))
        let snapshotHelpers = try String(contentsOf: helperEnvironment, encoding: .utf8)
            .split(whereSeparator: \.isNewline)
            .map(String.init)
        XCTAssertEqual(snapshotHelpers.map { URL(fileURLWithPath: $0).lastPathComponent }, [
            "cursor-codex-bridge.mjs",
            "cursor-remote-manager.mjs",
        ])
        XCTAssertTrue(snapshotHelpers.allSatisfy { $0.contains("CodexSyncBarProvisioning-") })
        XCTAssertTrue(snapshotHelpers.allSatisfy { !FileManager.default.fileExists(atPath: $0) })

        let deprovisioned = try await service.deprovisionCursor(deviceID: "build-server")
        XCTAssertEqual(
            deprovisioned.output,
            "device=build-server cursor=deprovisioned result=ok version=2.2.0\n")
        let deprovisionedPath = try String(contentsOf: invocation, encoding: .utf8)
        XCTAssertNotEqual(deprovisionedPath, trusted.path)
        XCTAssertEqual(URL(fileURLWithPath: deprovisionedPath).lastPathComponent, "gpt-switch")
        XCTAssertFalse(FileManager.default.fileExists(atPath: deprovisionedPath))
    }

    func testProvisioningEnvironmentBlocksHostileShellAndHelperOverrides() async throws {
        let root = try makeTemporaryDirectory(named: "EnvironmentAllowlist")
        defer { try? FileManager.default.removeItem(at: root) }
        let installed = root.appendingPathComponent("installed-gpt-switch")
        let trusted = root.appendingPathComponent("trusted-gpt-switch")
        let capturedEnvironment = root.appendingPathComponent("environment")
        let bashEnvMarker = root.appendingPathComponent("bash-env-ran")
        let hostileBashEnvironment = root.appendingPathComponent("hostile-bash-env")
        try writeHelper("touch \"\(bashEnvMarker.path)\"\n", to: hostileBashEnvironment)
        let script = """
        #!/bin/bash
        /usr/bin/env >"\(capturedEnvironment.path)"
        case "$1" in
          provision-cursor)
            /bin/cat >/dev/null
            printf 'device=%s cursor=provisioned result=ok version=2.2.0\n' "$2"
            ;;
          *) exit 64 ;;
        esac
        """
        try writeHelper(script, to: installed)
        try writeHelper(script, to: trusted)
        let support = try writeMatchingCursorSupportHelpers(to: root)
        let service = makeService(executable: installed, trusted: trusted, support: support)
        let hostileValues = [
            "BASH_ENV": hostileBashEnvironment.path,
            "ENV": hostileBashEnvironment.path,
            "SHELLOPTS": "xtrace",
            "BASHOPTS": "extdebug",
            "DYLD_LIBRARY_PATH": root.path,
            "DYLD_INSERT_LIBRARIES": root.appendingPathComponent("capture.dylib").path,
            "GPT_SWITCH_SSH_BIN": root.appendingPathComponent("hostile-ssh").path,
            "PATH": root.path,
        ]
        let previous = ProcessInfo.processInfo.environment
        for (key, value) in hostileValues { setenv(key, value, 1) }
        defer {
            for key in hostileValues.keys {
                if let value = previous[key] {
                    setenv(key, value, 1)
                } else {
                    unsetenv(key)
                }
            }
        }

        _ = try await service.provisionCursor(deviceID: "build-server", request: try request())
        let environment = try String(contentsOf: capturedEnvironment, encoding: .utf8)
        XCTAssertTrue(environment.contains("PATH=/usr/bin:/bin:/usr/sbin:/sbin\n"))
        for blocked in [
            "BASH_ENV=", "ENV=", "SHELLOPTS=", "BASHOPTS=", "DYLD_LIBRARY_PATH=",
            "DYLD_INSERT_LIBRARIES=", "GPT_SWITCH_SSH_BIN=",
        ] {
            XCTAssertFalse(environment.contains(blocked), "unexpected inherited key: \(blocked)")
        }
        XCTAssertFalse(FileManager.default.fileExists(atPath: bashEnvMarker.path))
    }

    func testProvisioningSnapshotDoesNotRereadReplacedBundleHelpers() async throws {
        let root = try makeTemporaryDirectory(named: "SnapshotRace")
        defer { try? FileManager.default.removeItem(at: root) }
        let installed = root.appendingPathComponent("installed-gpt-switch")
        let trusted = root.appendingPathComponent("trusted-gpt-switch")
        let support = try writeMatchingCursorSupportHelpers(to: root)
        let marker = root.appendingPathComponent("snapshot-used")
        let script = """
        #!/bin/bash
        printf 'replaced manager\n' >"\(support.trustedManager.path)"
        printf 'replaced bridge\n' >"\(support.trustedBridge.path)"
        if /usr/bin/grep -Fq 'trusted cursor remote manager' "$GPT_SWITCH_CURSOR_REMOTE_MANAGER" &&
           /usr/bin/grep -Fq 'trusted cursor bridge' "$GPT_SWITCH_CURSOR_BRIDGE_HELPER"; then
          /usr/bin/touch "\(marker.path)"
        fi
        /bin/cat >/dev/null
        printf 'device=%s cursor=provisioned result=ok version=2.2.0\n' "$2"
        """
        try writeHelper(script, to: installed)
        try writeHelper(script, to: trusted)
        let service = makeService(executable: installed, trusted: trusted, support: support)

        _ = try await service.provisionCursor(deviceID: "build-server", request: try request())
        XCTAssertTrue(FileManager.default.fileExists(atPath: marker.path))
    }

    func testProvisioningFailureNeverPromotesEchoedAPIKeyToError() async throws {
        let root = try makeTemporaryDirectory(named: "SecretRedaction")
        defer { try? FileManager.default.removeItem(at: root) }
        let installed = root.appendingPathComponent("installed-gpt-switch")
        let trusted = root.appendingPathComponent("trusted-gpt-switch")
        let script = """
        #!/bin/bash
        payload=$(cat)
        printf '%s\n' "$payload" >&2
        exit 1
        """
        try writeHelper(script, to: installed)
        try writeHelper(script, to: trusted)
        let support = try writeMatchingCursorSupportHelpers(to: root)
        let apiKey = "cursor_" + String(repeating: "s", count: 40)
        let service = makeService(
            executable: installed,
            trusted: trusted,
            support: support)

        do {
            _ = try await service.provisionCursor(
                deviceID: "build-server",
                request: try request(apiKey: apiKey))
            XCTFail("secret-echoing helper unexpectedly succeeded")
        } catch {
            XCTAssertEqual(
                error.localizedDescription,
                "SSH 장치의 Cursor 설치와 인증에 실패했습니다.")
            XCTAssertFalse(error.localizedDescription.contains(apiKey))
            XCTAssertFalse(error.localizedDescription.contains("apiKey"))
        }
    }

    func testProvisioningHandlesEarlyHelperExitWhileWritingLargeSecretInput() async throws {
        let root = try makeTemporaryDirectory(named: "EarlyExit")
        defer { try? FileManager.default.removeItem(at: root) }
        let installed = root.appendingPathComponent("installed-gpt-switch")
        let trusted = root.appendingPathComponent("trusted-gpt-switch")
        let script = "#!/bin/bash\nexit 1\n"
        try writeHelper(script, to: installed)
        try writeHelper(script, to: trusted)
        let support = try writeMatchingCursorSupportHelpers(to: root)
        let models = ["selected-model"] + (0..<511).map { index in
            "model-\(index)-" + String(repeating: "x", count: 110)
        }
        let service = makeService(
            executable: installed,
            trusted: trusted,
            support: support)
        let largeRequest = try request(model: "selected-model", models: models)
        let encoded = try JSONEncoder().encode(largeRequest)
        XCTAssertGreaterThan(encoded.count, 128 * 1024)
        XCTAssertLessThanOrEqual(
            encoded.count,
            CursorRemoteProvisioningRequest.maximumEncodedBytes)

        do {
            _ = try await service.provisionCursor(
                deviceID: "build-server",
                request: largeRequest)
            XCTFail("early-exit helper unexpectedly succeeded")
        } catch {
            XCTAssertFalse(error.localizedDescription.contains("cursor_"))
            XCTAssertFalse(error.localizedDescription.contains("설정이 너무 큽니다"))
        }
    }

    func testProvisioningRejectsChangedUnsafeOrSymlinkedInstalledHelper() async throws {
        let root = try makeTemporaryDirectory(named: "HelperIntegrity")
        defer { try? FileManager.default.removeItem(at: root) }
        let installed = root.appendingPathComponent("installed-gpt-switch")
        let trusted = root.appendingPathComponent("trusted-gpt-switch")
        let marker = root.appendingPathComponent("executed")
        let trustedScript = "#!/bin/bash\ntouch \"\(marker.path)\"\nexit 1\n"
        try writeHelper(trustedScript, to: trusted)
        try writeHelper("#!/bin/bash\nexit 1\n", to: installed)
        let support = try writeMatchingCursorSupportHelpers(to: root)
        let service = makeService(
            executable: installed,
            trusted: trusted,
            support: support)

        do {
            _ = try await service.provisionCursor(
                deviceID: "build-server",
                request: try request())
            XCTFail("changed helper unexpectedly received the request")
        } catch {
            XCTAssertTrue(error.localizedDescription.contains("앱 번들과 일치하지 않아"))
        }
        XCTAssertFalse(FileManager.default.fileExists(atPath: marker.path))

        // A modified bundle-side helper must also fail closed; comparing only
        // a previously checked pathname would let the changed bytes be used.
        try writeHelper(trustedScript, to: installed)
        try writeHelper("#!/bin/bash\ntouch \"\(marker.path)\"\n", to: trusted)
        let changedTrustedService = makeService(
            executable: installed,
            trusted: trusted,
            support: support)
        do {
            _ = try await changedTrustedService.provisionCursor(
                deviceID: "build-server",
                request: try request())
            XCTFail("modified bundle helper unexpectedly received the request")
        } catch {
            XCTAssertTrue(error.localizedDescription.contains("앱 번들과 일치하지 않아"))
        }
        XCTAssertFalse(FileManager.default.fileExists(atPath: marker.path))

        // Even with a pristine gpt-switch, a changed installed manager must
        // fail before the trusted script receives the secret-bearing request.
        try writeHelper(trustedScript, to: installed)
        try writeHelper(trustedScript, to: trusted)
        try writeHelper("#!/usr/bin/env node\nconsole.log('malicious')\n", to: support.installedManager)
        let changedManagerService = makeService(
            executable: installed,
            trusted: trusted,
            support: support)
        do {
            _ = try await changedManagerService.provisionCursor(
                deviceID: "build-server",
                request: try request())
            XCTFail("changed remote manager unexpectedly received the request")
        } catch {
            XCTAssertTrue(error.localizedDescription.contains("cursor-remote-manager.mjs"))
            XCTAssertTrue(error.localizedDescription.contains("앱 번들과 일치하지 않아"))
        }
        XCTAssertFalse(FileManager.default.fileExists(atPath: marker.path))

        try writeHelper(trustedScript, to: installed, permissions: 0o600)
        XCTAssertThrowsError(try SwitchService.validateProvisioningHelper(
            installed: installed,
            trusted: trusted))

        try FileManager.default.removeItem(at: installed)
        try FileManager.default.createSymbolicLink(at: installed, withDestinationURL: trusted)
        XCTAssertThrowsError(try SwitchService.validateProvisioningHelper(
            installed: installed,
            trusted: trusted))
    }

    private struct CursorSupportHelpers {
        let installedManager: URL
        let installedBridge: URL
        let trustedManager: URL
        let trustedBridge: URL
    }

    private func writeMatchingCursorSupportHelpers(to root: URL) throws
        -> CursorSupportHelpers
    {
        let helpers = CursorSupportHelpers(
            installedManager: root.appendingPathComponent("installed-cursor-remote-manager.mjs"),
            installedBridge: root.appendingPathComponent("installed-cursor-codex-bridge.mjs"),
            trustedManager: root.appendingPathComponent("cursor-remote-manager.mjs"),
            trustedBridge: root.appendingPathComponent("cursor-codex-bridge.mjs"))
        let manager = "#!/usr/bin/env node\n// trusted cursor remote manager\n"
        let bridge = "#!/usr/bin/env node\n// trusted cursor bridge\n"
        try writeHelper(manager, to: helpers.installedManager)
        try writeHelper(manager, to: helpers.trustedManager)
        try writeHelper(bridge, to: helpers.installedBridge)
        try writeHelper(bridge, to: helpers.trustedBridge)
        return helpers
    }

    private func makeService(
        executable: URL,
        trusted: URL,
        support: CursorSupportHelpers) -> SwitchService
    {
        SwitchService(
            executable: executable,
            trustedProvisioningExecutable: trusted,
            installedCursorRemoteManager: support.installedManager,
            installedCursorBridgeHelper: support.installedBridge,
            trustedCursorRemoteManager: support.trustedManager,
            trustedCursorBridgeHelper: support.trustedBridge)
    }

    private func request(
        apiKey: String = "cursor_" + String(repeating: "a", count: 32),
        model: String = "gpt-5.6-sol-high",
        models: [String] = ["gpt-5.6-sol-high", "composer-2.5"]
    ) throws -> CursorRemoteProvisioningRequest {
        try CursorRemoteProvisioningRequest(
            apiKey: apiKey,
            model: model,
            port: 32125,
            bridgeToken: String(repeating: "b", count: 64),
            models: models,
            modelParameters: parameters(for: models))
    }

    private func parameters(for models: [String]) -> [String: CursorACPModelParameters] {
        Dictionary(uniqueKeysWithValues: Set(models).map { slug in
            (
                slug,
                CursorACPModelParameters(
                    model: slug,
                    context: nil,
                    effort: nil,
                    fast: false,
                    thinking: false)
            )
        })
    }

    private func makeTemporaryDirectory(named name: String) throws -> URL {
        let root = FileManager.default.temporaryDirectory
            .appendingPathComponent("CodexSyncBar\(name)-\(UUID().uuidString)", isDirectory: true)
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        return root
    }

    private func writeHelper(
        _ script: String,
        to url: URL,
        permissions: Int = 0o755) throws
    {
        try Data(script.utf8).write(to: url)
        try FileManager.default.setAttributes(
            [.posixPermissions: permissions],
            ofItemAtPath: url.path)
    }
}
