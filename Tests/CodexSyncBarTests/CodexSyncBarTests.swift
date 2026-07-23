import Darwin
import XCTest
@testable import CodexSyncBar

final class CodexSyncBarTests: XCTestCase {
    func testReadmeDemoCommandAcceptsKnownScreenAndAbsoluteOutput() {
        let output = URL(fileURLWithPath: "/tmp/readme-popover.png")

        XCTAssertEqual(
            ReadmeDemoCommand.parse(arguments: [
                "CodexSyncBar",
                "--readme-demo=popover",
                "--readme-output=\(output.path)",
            ]),
            ReadmeDemoCommand(screen: .popover, outputURL: output))
    }

    func testReadmeDemoCommandRejectsUnknownDuplicateAndRelativeArguments() {
        XCTAssertNil(ReadmeDemoCommand.parse(arguments: [
            "CodexSyncBar",
            "--readme-demo=unknown",
            "--readme-output=/tmp/demo.png",
        ]))
        XCTAssertNil(ReadmeDemoCommand.parse(arguments: [
            "CodexSyncBar",
            "--readme-demo=popover",
            "--readme-demo=settings",
            "--readme-output=/tmp/demo.png",
        ]))
        XCTAssertNil(ReadmeDemoCommand.parse(arguments: [
            "CodexSyncBar",
            "--readme-demo=settings",
            "--readme-output=docs/images/demo.png",
        ]))
    }

    func testReadmeDemoFixtureUsesOnlyDeterministicDocumentationIdentities() throws {
        let createdAfter = Date()
        let fixture = ReadmeDemoFixture.standard

        XCTAssertEqual(fixture.referenceDate, Date(timeIntervalSince1970: 1_784_552_400))
        XCTAssertEqual(fixture.selectedProfileID, 1)
        XCTAssertEqual(fixture.profiles.map(\.email), [
            "demo.main@example.com",
            "demo.sub@example.com",
        ])
        XCTAssertEqual(fixture.profiles.map(\.alias), ["메인", "서브"])
        XCTAssertEqual(
            fixture.usageStates[1]?.snapshot?.weekly?.remainingPercent,
            68)
        XCTAssertEqual(
            fixture.usageStates[2]?.snapshot?.weekly?.remainingPercent,
            42)
        let sessionReset = try XCTUnwrap(
            fixture.usageStates[1]?.snapshot?.session?.resetsAt)
        XCTAssertGreaterThanOrEqual(
            sessionReset.timeIntervalSince(createdAfter),
            2 * 60 * 60 + 34 * 60)
        XCTAssertLessThanOrEqual(
            sessionReset.timeIntervalSince(createdAfter),
            2 * 60 * 60 + 36 * 60)
        XCTAssertEqual(fixture.configuredDevices.map(\.displayName), ["작업 서버", "빌드 서버"])
        XCTAssertEqual(fixture.configuredDevices.map(\.host), [
            "workstation.example.net",
            "build.example.net",
        ])
        XCTAssertEqual(fixture.tokenUsageSnapshot.counts.totalTokens, 12_345_678)

        let identityText = (
            fixture.profiles.map(\.email)
                + fixture.configuredDevices.flatMap {
                    [$0.displayName, $0.host, $0.username]
                })
            .joined(separator: "\n")
        XCTAssertFalse(identityText.contains("/Users/"))
        XCTAssertNil(identityText.range(
            of: #"\b(?:\d{1,3}\.){3}\d{1,3}\b"#,
            options: .regularExpression))
        XCTAssertTrue(fixture.profiles.allSatisfy { $0.email.hasSuffix("@example.com") })
        XCTAssertTrue(fixture.configuredDevices.allSatisfy { $0.host.hasSuffix(".example.net") })
    }

    @MainActor
    func testReadmeDemoModelStartsFromFixtureAndDoesNotRefreshExternalState() async {
        let fixture = ReadmeDemoFixture.standard
        let model = AppModel(readmeDemoFixture: fixture)

        XCTAssertEqual(model.profiles, fixture.profiles)
        XCTAssertEqual(model.selectedProfileID, 1)
        XCTAssertEqual(model.activeProfileID, 1)
        XCTAssertEqual(model.configuredDevices, fixture.configuredDevices)
        XCTAssertEqual(model.devices, fixture.devices)
        XCTAssertEqual(model.usageStates, fixture.usageStates)
        XCTAssertEqual(model.tokenUsageSnapshot, fixture.tokenUsageSnapshot)
        XCTAssertEqual(model.usageDisplayPreferences, .allVisible)
        XCTAssertEqual(
            model.menuBarUsagePreferences,
            MenuBarUsagePreferences(items: [.codexWeekly, .sparkWeekly]))
        XCTAssertNil(model.configurationError)

        await model.start()

        XCTAssertEqual(model.profiles, fixture.profiles)
        XCTAssertEqual(model.devices, fixture.devices)
        XCTAssertEqual(model.usageStates, fixture.usageStates)
        XCTAssertFalse(model.isRefreshing)
        XCTAssertFalse(model.isMaintainingAuth)
        XCTAssertFalse(model.isCollectingTokenUsage)
    }

    @MainActor
    func testTransientBannerDismissesAfterFocusLoss() {
        let model = AppModel(readmeDemoFixture: .standard)

        model.showTransientBanner(
            style: .success,
            message: "계정 별칭을 저장했습니다.",
            dismissAfterNanoseconds: 60_000_000_000)
        XCTAssertEqual(model.banner?.message, "계정 별칭을 저장했습니다.")

        model.dismissTransientBannerAfterFocusLoss()

        XCTAssertNil(model.banner)
    }

    @MainActor
    func testTransientBannerFocusLossPreservesReplacementError() {
        let model = AppModel(readmeDemoFixture: .standard)
        model.showTransientBanner(
            style: .success,
            message: "저장했습니다.",
            dismissAfterNanoseconds: 60_000_000_000)
        model.banner = AppBanner(style: .error, message: "확인이 필요합니다.")

        model.dismissTransientBannerAfterFocusLoss()

        XCTAssertEqual(model.banner?.message, "확인이 필요합니다.")
    }

    func testAliasSuccessBannerUsesFocusDismissalLifecycle() throws {
        let packageRoot = URL(fileURLWithPath: #filePath)
            .deletingLastPathComponent()
            .deletingLastPathComponent()
            .deletingLastPathComponent()
        let appModel = try String(
            contentsOf: packageRoot.appendingPathComponent("Sources/CodexSyncBar/AppModel.swift"),
            encoding: .utf8)
        let popover = try String(
            contentsOf: packageRoot.appendingPathComponent("Sources/CodexSyncBar/Views/PopoverView.swift"),
            encoding: .utf8)
        let settings = try String(
            contentsOf: packageRoot.appendingPathComponent("Sources/CodexSyncBar/Views/SettingsView.swift"),
            encoding: .utf8)

        XCTAssertNotNil(appModel.range(
            of: #"showTransientBanner\(\s*style: \.success,\s*message: "계정 별칭을 저장했습니다\."\)"#,
            options: .regularExpression))
        XCTAssertNotNil(popover.range(
            of: #"\.onDisappear\s*\{\s*model\.dismissTransientBannerAfterFocusLoss\(\)\s*\}"#,
            options: .regularExpression))
        XCTAssertNotNil(settings.range(
            of: #"\.onDisappear\s*\{\s*model\.dismissTransientBannerAfterFocusLoss\(\)\s*\}"#,
            options: .regularExpression))
        XCTAssertNotNil(settings.range(
            of: #"}\s*else\s*\{\s*model\.dismissTransientBannerAfterFocusLoss\(\)\s*\}"#,
            options: .regularExpression))
    }

    func testReadmeCaptureOutputRequiresExistingDirectoryAndRejectsSymlink() throws {
        let root = FileManager.default.temporaryDirectory
            .appendingPathComponent("codex-readme-output-\(UUID().uuidString)", isDirectory: true)
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: false)
        defer { try? FileManager.default.removeItem(at: root) }

        let valid = root.appendingPathComponent("demo.png")
        XCTAssertEqual(
            try ReadmeCaptureOutputValidator.validatedOutputURL(valid),
            valid.standardizedFileURL)

        let missingParent = root.appendingPathComponent("missing/demo.png")
        XCTAssertThrowsError(try ReadmeCaptureOutputValidator.validatedOutputURL(missingParent))

        let target = root.appendingPathComponent("target.png")
        XCTAssertTrue(FileManager.default.createFile(atPath: target.path, contents: Data()))
        let symlink = root.appendingPathComponent("linked.png")
        try FileManager.default.createSymbolicLink(at: symlink, withDestinationURL: target)
        XCTAssertThrowsError(try ReadmeCaptureOutputValidator.validatedOutputURL(symlink))
    }

    func testTokenPricingSeparatesCachedInputAndAppliesAPIPriorityPricing() {
        let usage = ModelTokenUsage(
            model: "gpt-5.6-sol",
            serviceTier: "priority",
            inputTokens: 1_000_000,
            cachedInputTokens: 400_000,
            cacheWriteInputTokens: 0,
            outputTokens: 100_000,
            reasoningOutputTokens: 50_000,
            totalTokens: 1_100_000,
            requests: 1)

        let estimate = TokenUsagePricing.estimateUSD(for: usage)
        XCTAssertTrue(estimate.isPriced)
        XCTAssertEqual(estimate.canonicalModel, "GPT-5.6 Sol")
        XCTAssertEqual(estimate.multiplier, Decimal(string: "2"))
        XCTAssertEqual(estimate.pricedUSD, Decimal(string: "12.4"))
    }

    func testTokenPricingUsesPublishedModelSpecificPriorityRates() {
        let gpt55 = ModelTokenUsage(
            model: "gpt-5.5",
            serviceTier: "fast",
            inputTokens: 1_000_000,
            cachedInputTokens: 0,
            cacheWriteInputTokens: 0,
            outputTokens: 0,
            reasoningOutputTokens: 0,
            totalTokens: 1_000_000,
            requests: 1)
        let gpt52 = ModelTokenUsage(
            model: "gpt-5.2",
            serviceTier: "priority",
            inputTokens: 1_000_000,
            cachedInputTokens: 0,
            cacheWriteInputTokens: 0,
            outputTokens: 0,
            reasoningOutputTokens: 0,
            totalTokens: 1_000_000,
            requests: 1)
        let mini = ModelTokenUsage(
            model: "gpt-5.4-mini",
            serviceTier: "default",
            inputTokens: 0,
            cachedInputTokens: 0,
            cacheWriteInputTokens: 0,
            outputTokens: 1_000_000,
            reasoningOutputTokens: 0,
            totalTokens: 1_000_000,
            requests: 1)

        XCTAssertEqual(TokenUsagePricing.estimateUSD(for: gpt55).pricedUSD, Decimal(string: "12.5"))
        XCTAssertEqual(TokenUsagePricing.estimateUSD(for: gpt52).pricedUSD, Decimal(string: "3.5"))
        XCTAssertEqual(TokenUsagePricing.estimateUSD(for: mini).pricedUSD, Decimal(string: "4.5"))
    }

    func testDollarFormattingUsesThousandsSeparatorsAndKeepsCompactDecimals() {
        XCTAssertEqual(TokenUsageFormatting.dollars(Decimal(string: "265327.49")!), "$265,327")
        XCTAssertEqual(TokenUsageFormatting.dollars(Decimal(string: "113587.6")!), "$113,588")
        XCTAssertEqual(TokenUsageFormatting.dollars(Decimal(string: "18.6")!), "$18.6")
        XCTAssertEqual(TokenUsageFormatting.dollars(Decimal(string: "1.25")!), "$1.25")
    }

    func testTokenPricingMapsReviewModelAndDoesNotInventUnknownPrice() {
        let review = ModelTokenUsage(
            model: "codex-auto-review",
            serviceTier: "default",
            inputTokens: 1_000_000,
            cachedInputTokens: 0,
            cacheWriteInputTokens: 0,
            outputTokens: 0,
            reasoningOutputTokens: 0,
            totalTokens: 1_000_000,
            requests: 1)
        XCTAssertEqual(TokenUsagePricing.estimateUSD(for: review).pricedUSD, Decimal(string: "1.75"))

        let unknown = ModelTokenUsage(
            model: "gpt-5.3-codex-spark",
            serviceTier: "default",
            inputTokens: 500_000,
            cachedInputTokens: 0,
            cacheWriteInputTokens: 0,
            outputTokens: 0,
            reasoningOutputTokens: 0,
            totalTokens: 500_000,
            requests: 1)
        XCTAssertFalse(TokenUsagePricing.estimateUSD(for: unknown).isPriced)
        XCTAssertEqual(TokenUsagePricing.estimateUSD(for: unknown).pricedUSD, 0)

        let unpublishedCyber = ModelTokenUsage(
            model: "gpt-5.5-cyber",
            serviceTier: "default",
            inputTokens: 1_000_000,
            cachedInputTokens: 0,
            cacheWriteInputTokens: 0,
            outputTokens: 0,
            reasoningOutputTokens: 0,
            totalTokens: 1_000_000,
            requests: 1)
        XCTAssertFalse(TokenUsagePricing.estimateUSD(for: unpublishedCyber).isPriced)
    }

    func testUsageSummaryHelperUsesCumulativeDeltasAndIncrementalCache() throws {
        let packageRoot = URL(fileURLWithPath: #filePath)
            .deletingLastPathComponent().deletingLastPathComponent().deletingLastPathComponent()
        let helper = packageRoot.appendingPathComponent("Support/usage-summary.mjs")
        let root = FileManager.default.temporaryDirectory
            .appendingPathComponent("CodexSyncBarUsageSummary-\(UUID().uuidString)", isDirectory: true)
        defer { try? FileManager.default.removeItem(at: root) }
        let sessions = root.appendingPathComponent("sessions", isDirectory: true)
        try FileManager.default.createDirectory(at: sessions, withIntermediateDirectories: true)
        let session = sessions.appendingPathComponent("one.jsonl")
        let cache = root.appendingPathComponent("cache.json")
        let initial = """
        {"type":"event_msg","payload":{"type":"thread_settings_applied","thread_settings":{"model":"gpt-5.6-sol","service_tier":"default"}}}
        {"type":"event_msg","payload":{"type":"token_count","info":{"last_token_usage":{"input_tokens":80,"cached_input_tokens":20,"output_tokens":20,"reasoning_output_tokens":5,"total_tokens":100},"total_token_usage":{"input_tokens":80,"cached_input_tokens":20,"output_tokens":20,"reasoning_output_tokens":5,"total_tokens":100}}}}
        {"type":"event_msg","payload":{"type":"token_count","info":{"last_token_usage":{"input_tokens":80,"cached_input_tokens":20,"output_tokens":20,"reasoning_output_tokens":5,"total_tokens":100},"total_token_usage":{"input_tokens":80,"cached_input_tokens":20,"output_tokens":20,"reasoning_output_tokens":5,"total_tokens":100}}}}
        {"type":"event_msg","payload":{"type":"thread_settings_applied","thread_settings":{"model":"gpt-5.6-terra","service_tier":"priority"}}}
        {"type":"event_msg","payload":{"type":"token_count","info":{"last_token_usage":{"input_tokens":45,"cached_input_tokens":5,"output_tokens":15,"reasoning_output_tokens":3,"total_tokens":60},"total_token_usage":{"input_tokens":125,"cached_input_tokens":25,"output_tokens":35,"reasoning_output_tokens":8,"total_tokens":160}}}}
        """
        try Data((initial + "\n").utf8).write(to: session)

        func collect() throws -> DeviceTokenUsageSummary {
            let process = Process()
            let pipe = Pipe()
            process.executableURL = URL(fileURLWithPath: "/usr/bin/env")
            process.arguments = ["node", helper.path, sessions.path, cache.path]
            process.standardOutput = pipe
            process.standardError = pipe
            try process.run()
            let data = pipe.fileHandleForReading.readDataToEndOfFile()
            process.waitUntilExit()
            XCTAssertEqual(process.terminationStatus, 0, String(decoding: data, as: UTF8.self))
            return try JSONDecoder().decode(DeviceTokenUsageSummary.self, from: data)
        }

        let first = try collect()
        XCTAssertEqual(first.schemaVersion, 4)
        XCTAssertEqual(first.totalTokens, 160)
        XCTAssertEqual(first.buckets.count, 2)
        XCTAssertEqual(try collect().totalTokens, 160)

        let handle = try FileHandle(forWritingTo: session)
        try handle.seekToEnd()
        try handle.write(contentsOf: Data("{\"type\":\"event_msg\",\"payload\":{\"type\":\"token_count\",\"info\":{\"last_token_usage\":{\"input_tokens\":30,\"cached_input_tokens\":10,\"output_tokens\":10,\"reasoning_output_tokens\":2,\"total_tokens\":40},\"total_token_usage\":{\"input_tokens\":155,\"cached_input_tokens\":35,\"output_tokens\":45,\"reasoning_output_tokens\":10,\"total_tokens\":200}}}}\n".utf8))
        try handle.close()
        XCTAssertEqual(try collect().totalTokens, 200)

        let oldSession = sessions.appendingPathComponent("old.jsonl")
        let oldTimestamp = ISO8601DateFormatter().string(
            from: Date().addingTimeInterval(-31 * 24 * 60 * 60))
        let oldLog = """
        {"timestamp":"\(oldTimestamp)","type":"event_msg","payload":{"type":"thread_settings_applied","thread_settings":{"model":"gpt-5.6-sol","service_tier":"default"}}}
        {"timestamp":"\(oldTimestamp)","type":"event_msg","payload":{"type":"token_count","info":{"last_token_usage":{"input_tokens":900,"cached_input_tokens":0,"output_tokens":100,"reasoning_output_tokens":0,"total_tokens":1000},"total_token_usage":{"input_tokens":900,"cached_input_tokens":0,"output_tokens":100,"reasoning_output_tokens":0,"total_tokens":1000}}}}
        """
        try Data((oldLog + "\n").utf8).write(to: oldSession)
        XCTAssertEqual(try collect().totalTokens, 200)
    }

    func testShellUsageCollectorFallsBackToJQWhenNodeIsUnavailable() throws {
        let packageRoot = URL(fileURLWithPath: #filePath)
            .deletingLastPathComponent().deletingLastPathComponent().deletingLastPathComponent()
        let controller = packageRoot.appendingPathComponent("Support/gpt-switch")
        let usageHelper = packageRoot.appendingPathComponent("Support/usage-summary.mjs")
        let root = FileManager.default.temporaryDirectory
            .appendingPathComponent("CodexSyncBarJQUsage-\(UUID().uuidString)", isDirectory: true)
        defer { try? FileManager.default.removeItem(at: root) }
        let sessions = root.appendingPathComponent(".codex/sessions/2026/07/19", isDirectory: true)
        try FileManager.default.createDirectory(at: sessions, withIntermediateDirectories: true)
        let log = """
        {"type":"event_msg","payload":{"type":"thread_settings_applied","thread_settings":{"model":"gpt-5.6-sol","service_tier":"priority"}}}
        {"type":"event_msg","payload":{"type":"token_count","info":{"last_token_usage":{"input_tokens":80,"cached_input_tokens":20,"output_tokens":20,"reasoning_output_tokens":5,"total_tokens":100},"total_token_usage":{"input_tokens":80,"cached_input_tokens":20,"output_tokens":20,"reasoning_output_tokens":5,"total_tokens":100}}}}
        """
        try Data((log + "\n").utf8).write(to: sessions.appendingPathComponent("one.jsonl"))

        let process = Process()
        let pipe = Pipe()
        process.executableURL = URL(fileURLWithPath: "/bin/bash")
        process.arguments = [controller.path, "__node", "usage-summary"]
        process.environment = ProcessInfo.processInfo.environment.merging([
            "HOME": root.path,
            "CODEX_HOME": root.appendingPathComponent(".codex").path,
            "GPT_SWITCH_STATE_ROOT": root.appendingPathComponent(".local/share/gpt-switch").path,
            "GPT_SWITCH_USAGE_HELPER": usageHelper.path,
            "GPT_SWITCH_NODE_BIN": "/missing/node",
        ]) { _, new in new }
        process.standardOutput = pipe
        process.standardError = pipe
        try process.run()
        let data = pipe.fileHandleForReading.readDataToEndOfFile()
        process.waitUntilExit()
        XCTAssertEqual(process.terminationStatus, 0, String(decoding: data, as: UTF8.self))
        let summary = try JSONDecoder().decode(DeviceTokenUsageSummary.self, from: data)
        XCTAssertEqual(summary.schemaVersion, 4)
        XCTAssertEqual(summary.totalTokens, 100)
        XCTAssertEqual(summary.buckets.first?.serviceTier, "priority")
    }

    func testWeeklyAnchorPreferencesDefaultOffAndPersistPerAccount() throws {
        let suiteName = "CodexSyncBarWeeklyAnchor-\(UUID().uuidString)"
        guard let defaults = UserDefaults(suiteName: suiteName) else {
            return XCTFail("isolated UserDefaults 생성 실패")
        }
        defer { defaults.removePersistentDomain(forName: suiteName) }
        let store = WeeklyAnchorStore(defaults: defaults)

        XCTAssertEqual(store.loadPreferences(), .disabled)
        var preferences = WeeklyAnchorPreferences.disabled
        preferences.setEnabled(true, for: 2)
        store.savePreferences(preferences)
        XCTAssertEqual(store.loadPreferences().enabledProfileIDs, [2])

        let resetAt = Date(timeIntervalSince1970: 1_800_000_000)
        let record = WeeklyAnchorRecord(
            nextResetAt: resetAt,
            lastHandledResetAt: nil,
            lastAttemptAt: nil,
            lastSuccessAt: nil,
            lastError: nil)
        store.saveRecords([2: record])
        XCTAssertEqual(store.loadRecords(), [2: record])
    }

    func testWeeklyAnchorDecisionTriggersOnFirstUnusedOptInAndAfterReset() {
        let now = Date(timeIntervalSince1970: 1_800_000_000)
        let futureReset = now.addingTimeInterval(7 * 86_400)
        let unused = UsageWindow(usedPercent: 0, resetsAt: futureReset, durationSeconds: nil)

        XCTAssertEqual(
            WeeklyAnchorDecisionEngine.decision(
                enabled: true,
                window: unused,
                record: .empty,
                now: now),
            .trigger(expectedResetAt: nil))

        let elapsedReset = now.addingTimeInterval(-30)
        let observed = WeeklyAnchorRecord(
            nextResetAt: elapsedReset,
            lastHandledResetAt: nil,
            lastAttemptAt: nil,
            lastSuccessAt: nil,
            lastError: nil)
        XCTAssertEqual(
            WeeklyAnchorDecisionEngine.decision(
                enabled: true,
                window: unused,
                record: observed,
                now: now),
            .trigger(expectedResetAt: elapsedReset))
    }

    func testWeeklyAnchorDecisionDoesNotSendWhenUserAlreadyStartedNewPeriod() {
        let now = Date(timeIntervalSince1970: 1_800_000_000)
        let elapsedReset = now.addingTimeInterval(-60)
        let nextReset = now.addingTimeInterval(7 * 86_400)
        let record = WeeklyAnchorRecord(
            nextResetAt: elapsedReset,
            lastHandledResetAt: nil,
            lastAttemptAt: nil,
            lastSuccessAt: nil,
            lastError: nil)
        let alreadyUsed = UsageWindow(usedPercent: 1, resetsAt: nextReset, durationSeconds: nil)

        XCTAssertEqual(
            WeeklyAnchorDecisionEngine.decision(
                enabled: true,
                window: alreadyUsed,
                record: record,
                now: now),
            .alreadyActive(nextResetAt: nextReset))
    }

    func testWeeklyAnchorDecisionRespectsFailureRetryCooldown() {
        let now = Date(timeIntervalSince1970: 1_800_000_000)
        let record = WeeklyAnchorRecord(
            nextResetAt: now.addingTimeInterval(-60),
            lastHandledResetAt: nil,
            lastAttemptAt: now.addingTimeInterval(-10 * 60),
            lastSuccessAt: nil,
            lastError: "failed")
        let unused = UsageWindow(usedPercent: 0, resetsAt: nil, durationSeconds: nil)

        XCTAssertEqual(
            WeeklyAnchorDecisionEngine.decision(
                enabled: true,
                window: unused,
                record: record,
                now: now),
            .none)
    }

    func testWeeklyAnchorDecisionStoresResetImmediatelyAfterSuccessfulSend() {
        let now = Date(timeIntervalSince1970: 1_800_000_000)
        let anchoredReset = now.addingTimeInterval(7 * 86_400)
        let record = WeeklyAnchorRecord(
            nextResetAt: nil,
            lastHandledResetAt: nil,
            lastAttemptAt: now.addingTimeInterval(-60),
            lastSuccessAt: now.addingTimeInterval(-30),
            lastError: nil)

        XCTAssertEqual(
            WeeklyAnchorDecisionEngine.decision(
                enabled: true,
                window: UsageWindow(usedPercent: 0, resetsAt: anchoredReset, durationSeconds: nil),
                record: record,
                now: now),
            .observe(nextResetAt: anchoredReset))
    }

    func testWeeklyAnchorDecisionConfirmsForwardResetDriftTwiceBeforeSending() {
        let now = Date(timeIntervalSince1970: 1_800_000_000)
        let baseline = now.addingTimeInterval(2 * 86_400)
        let shifted = baseline.addingTimeInterval(15 * 60)
        let unused = UsageWindow(usedPercent: 0, resetsAt: shifted, durationSeconds: nil)
        let firstObservation = WeeklyAnchorRecord(
            nextResetAt: baseline,
            lastHandledResetAt: nil,
            lastAttemptAt: nil,
            lastSuccessAt: nil,
            lastError: nil)

        XCTAssertEqual(
            WeeklyAnchorDecisionEngine.decision(
                enabled: true,
                window: unused,
                record: firstObservation,
                now: now),
            .confirmResetDrift(observedResetAt: shifted))

        var confirmed = firstObservation
        confirmed.resetDriftCandidateAt = shifted
        confirmed.resetDriftObservationCount = 1
        XCTAssertEqual(
            WeeklyAnchorDecisionEngine.decision(
                enabled: true,
                window: unused,
                record: confirmed,
                now: now.addingTimeInterval(5 * 60)),
            .trigger(expectedResetAt: baseline))
    }

    func testWeeklyAnchorDecisionIgnoresSmallResetTimeJitter() {
        let now = Date(timeIntervalSince1970: 1_800_000_000)
        let baseline = now.addingTimeInterval(2 * 86_400)
        let jittered = baseline.addingTimeInterval(
            WeeklyAnchorDecisionEngine.resetDriftTolerance - 1)
        let record = WeeklyAnchorRecord(
            nextResetAt: baseline,
            lastHandledResetAt: nil,
            lastAttemptAt: nil,
            lastSuccessAt: nil,
            lastError: nil)

        XCTAssertEqual(
            WeeklyAnchorDecisionEngine.decision(
                enabled: true,
                window: UsageWindow(usedPercent: 0, resetsAt: jittered, durationSeconds: nil),
                record: record,
                now: now),
            .none)
    }

    func testWeeklyAnchorDecisionAdoptsShiftWhenAccountIsAlreadyInUse() {
        let now = Date(timeIntervalSince1970: 1_800_000_000)
        let baseline = now.addingTimeInterval(2 * 86_400)
        let shifted = baseline.addingTimeInterval(60 * 60)
        let record = WeeklyAnchorRecord(
            nextResetAt: baseline,
            lastHandledResetAt: nil,
            lastAttemptAt: nil,
            lastSuccessAt: nil,
            lastError: nil)

        XCTAssertEqual(
            WeeklyAnchorDecisionEngine.decision(
                enabled: true,
                window: UsageWindow(usedPercent: 4, resetsAt: shifted, durationSeconds: nil),
                record: record,
                now: now),
            .observe(nextResetAt: shifted))
    }

    func testWeeklyAnchorDecisionKeepsRetryCooldownAfterConfirmedDriftFailure() {
        let now = Date(timeIntervalSince1970: 1_800_000_000)
        let baseline = now.addingTimeInterval(2 * 86_400)
        let shifted = baseline.addingTimeInterval(60 * 60)
        var record = WeeklyAnchorRecord(
            nextResetAt: baseline,
            lastHandledResetAt: nil,
            lastAttemptAt: now.addingTimeInterval(-10 * 60),
            lastSuccessAt: nil,
            lastError: "failed")
        record.resetDriftCandidateAt = shifted
        record.resetDriftObservationCount = 1

        XCTAssertEqual(
            WeeklyAnchorDecisionEngine.decision(
                enabled: true,
                window: UsageWindow(usedPercent: 0, resetsAt: shifted, durationSeconds: nil),
                record: record,
                now: now),
            .none)
    }

    func testWeeklyAnchorDecisionImmediatelyRetriesLegacyLocalRunnerFailure() {
        let now = Date(timeIntervalSince1970: 1_800_000_000)
        let record = WeeklyAnchorRecord(
            nextResetAt: nil,
            lastHandledResetAt: nil,
            lastAttemptAt: now.addingTimeInterval(-5 * 60),
            lastSuccessAt: nil,
            lastError: "Reading additional input from stdin… no_biscuit_no_service")

        XCTAssertEqual(
            WeeklyAnchorDecisionEngine.decision(
                enabled: true,
                window: UsageWindow(usedPercent: 0, resetsAt: nil, durationSeconds: nil),
                record: record,
                now: now),
            .trigger(expectedResetAt: nil))
    }

    func testWeeklyAnchorServiceUsesEphemeralProviderWithoutRefreshToken() async throws {
        let home = FileManager.default.temporaryDirectory
            .appendingPathComponent("CodexSyncBarWeeklyAnchorService-\(UUID().uuidString)", isDirectory: true)
        defer { try? FileManager.default.removeItem(at: home) }
        let profileDirectory = home.appendingPathComponent(".local/share/gpt-switch/profiles", isDirectory: true)
        try FileManager.default.createDirectory(at: profileDirectory, withIntermediateDirectories: true)
        let auth = CodexAuthFile(
            openAIAPIKey: nil,
            authMode: "chatgpt",
            lastRefresh: nil,
            tokens: CodexTokens(
                idToken: "header.payload.signature",
                accessToken: "test-access-token",
                refreshToken: "real-refresh-secret",
                accountID: "test-account"))
        let profileURL = profileDirectory.appendingPathComponent("1.auth.json")
        try JSONEncoder().encode(auth).write(to: profileURL)
        try FileManager.default.setAttributes([.posixPermissions: 0o600], ofItemAtPath: profileURL.path)

        let fakeCodex = home.appendingPathComponent("fake-codex")
        let script = #"""
        #!/bin/sh
        set -eu
        if IFS= read -r unexpected_input; then exit 42; fi
        test ! -e "$CODEX_HOME/auth.json"
        test "$CODEX_SYNCBAR_ACCESS_TOKEN" = 'test-access-token'
        test "$CODEX_SYNCBAR_ACCOUNT_ID" = 'test-account'
        printf '%s\n' "$@" > "$0.args"
        output=''
        previous=''
        for argument in "$@"; do
          if [ "$previous" = '--output-last-message' ]; then output="$argument"; fi
          previous="$argument"
        done
        test -n "$output"
        printf '확인\n' > "$output"
        printf 'fake codex complete\n'
        """#
        try Data(script.utf8).write(to: fakeCodex)
        try FileManager.default.setAttributes([.posixPermissions: 0o700], ofItemAtPath: fakeCodex.path)

        let store = AuthStore(home: home, switchExecutable: home.appendingPathComponent("unused-switch"))
        let service = WeeklyUsageAnchorService(
            authStore: store,
            codexExecutable: fakeCodex,
            home: home)
        let response = try await service.send(profileID: 1)

        XCTAssertEqual(response, "확인")
        let arguments = try String(contentsOf: URL(fileURLWithPath: fakeCodex.path + ".args"), encoding: .utf8)
        XCTAssertTrue(arguments.contains("--ephemeral"))
        XCTAssertTrue(arguments.contains("plugins"))
        XCTAssertTrue(arguments.contains("remote_plugin"))
        XCTAssertTrue(arguments.contains("apps"))
        XCTAssertTrue(arguments.contains("--ignore-user-config"))
        XCTAssertTrue(arguments.contains("--ignore-rules"))
        XCTAssertTrue(arguments.contains("read-only"))
        XCTAssertTrue(arguments.contains("gpt-5.4-mini"))
        XCTAssertTrue(arguments.contains("syncbar_chatgpt"))
        XCTAssertTrue(arguments.contains("CODEX_SYNCBAR_ACCESS_TOKEN"))
        XCTAssertTrue(arguments.contains("CODEX_SYNCBAR_ACCOUNT_ID"))
        XCTAssertTrue(arguments.contains("requires_openai_auth=false"))
        XCTAssertTrue(arguments.contains(WeeklyUsageAnchorService.prompt))
    }

    func testWeeklyAnchorServiceRefreshesCanonicalAccessAndRetriesOnce() async throws {
        let home = FileManager.default.temporaryDirectory
            .appendingPathComponent("CodexSyncBarWeeklyAnchorRetry-\(UUID().uuidString)", isDirectory: true)
        defer { try? FileManager.default.removeItem(at: home) }
        let profileDirectory = home.appendingPathComponent(".local/share/gpt-switch/profiles", isDirectory: true)
        try FileManager.default.createDirectory(at: profileDirectory, withIntermediateDirectories: true)
        let profileURL = profileDirectory.appendingPathComponent("2.auth.json")
        let staleAuth = CodexAuthFile(
            openAIAPIKey: nil,
            authMode: "chatgpt",
            lastRefresh: nil,
            tokens: CodexTokens(
                idToken: "header.payload.signature",
                accessToken: "stale-access-token",
                refreshToken: "canonical-refresh-secret",
                accountID: "test-account"))
        try JSONEncoder().encode(staleAuth).write(to: profileURL)
        try FileManager.default.setAttributes([.posixPermissions: 0o600], ofItemAtPath: profileURL.path)

        let fakeCodex = home.appendingPathComponent("fake-codex-retry")
        let script = #"""
        #!/bin/sh
        set -eu
        count_file="$0.count"
        count=0
        if [ -f "$count_file" ]; then count=$(cat "$count_file"); fi
        count=$((count + 1))
        printf '%s\n' "$count" > "$count_file"
        test ! -e "$CODEX_HOME/auth.json"
        test "$CODEX_SYNCBAR_ACCOUNT_ID" = 'test-account'
        if [ "$CODEX_SYNCBAR_ACCESS_TOKEN" = 'stale-access-token' ]; then
          printf '401 Unauthorized: ChatGPT login did not make it to this service\n'
          exit 1
        fi
        test "$CODEX_SYNCBAR_ACCESS_TOKEN" = 'fresh-access-token'
        output=''
        previous=''
        for argument in "$@"; do
          if [ "$previous" = '--output-last-message' ]; then output="$argument"; fi
          previous="$argument"
        done
        test -n "$output"
        printf '확인\n' > "$output"
        printf 'fake codex retry complete\n'
        """#
        try Data(script.utf8).write(to: fakeCodex)
        try FileManager.default.setAttributes([.posixPermissions: 0o700], ofItemAtPath: fakeCodex.path)
        let refreshMarker = home.appendingPathComponent("central-refresh-ran")

        let store = AuthStore(home: home, switchExecutable: home.appendingPathComponent("unused-switch"))
        let service = WeeklyUsageAnchorService(
            authStore: store,
            codexExecutable: fakeCodex,
            home: home,
            credentialRefresher: { profileID, failedAccessToken in
                guard profileID == 2, failedAccessToken == "stale-access-token" else {
                    throw AppError.processFailed("unexpected refresh request")
                }
                let refreshedAuth = CodexAuthFile(
                    openAIAPIKey: nil,
                    authMode: "chatgpt",
                    lastRefresh: nil,
                    tokens: CodexTokens(
                        idToken: "header.payload.signature",
                        accessToken: "fresh-access-token",
                        refreshToken: "canonical-refresh-secret",
                        accountID: "test-account"))
                try JSONEncoder().encode(refreshedAuth).write(to: profileURL, options: [.atomic])
                try Data("ok".utf8).write(to: refreshMarker)
            })

        let response = try await service.send(profileID: 2)

        XCTAssertEqual(response, "확인")
        XCTAssertTrue(FileManager.default.fileExists(atPath: refreshMarker.path))
        XCTAssertEqual(
            try String(contentsOf: URL(fileURLWithPath: fakeCodex.path + ".count"), encoding: .utf8)
                .trimmingCharacters(in: .whitespacesAndNewlines),
            "2")
        let stored = try JSONDecoder().decode(CodexAuthFile.self, from: Data(contentsOf: profileURL))
        XCTAssertEqual(stored.tokens.accessToken, "fresh-access-token")
        XCTAssertEqual(stored.tokens.refreshToken, "canonical-refresh-secret")
    }

    func testControllerMutationLockUsesAtomicFileAndBlocksConcurrentOwner() throws {
        let home = FileManager.default.temporaryDirectory
            .appendingPathComponent("CodexSyncBarLockAtomic-\(UUID().uuidString)", isDirectory: true)
        defer { try? FileManager.default.removeItem(at: home) }
        let lock = ControllerMutationLock(home: home)

        try lock.withLock {
            var info = stat()
            XCTAssertEqual(lock.lockURL.path.withCString { lstat($0, &info) }, 0)
            XCTAssertEqual(info.st_mode & S_IFMT, S_IFREG)
            XCTAssertThrowsError(try lock.withLock {}) { error in
                XCTAssertTrue(ControllerMutationLock.isBusy(error))
            }
        }
        var info = stat()
        XCTAssertNotEqual(lock.lockURL.path.withCString { lstat($0, &info) }, 0)
        XCTAssertEqual(errno, ENOENT)
    }

    func testControllerMutationLockReleaseRequiresMatchingOwnershipToken() throws {
        let home = FileManager.default.temporaryDirectory
            .appendingPathComponent("CodexSyncBarLockToken-\(UUID().uuidString)", isDirectory: true)
        defer { try? FileManager.default.removeItem(at: home) }
        let lock = ControllerMutationLock(home: home)

        try lock.withLock {
            try Data("pid=\(getpid())\ntoken=replacement\n".utf8)
                .write(to: lock.lockURL, options: [.atomic])
            try FileManager.default.setAttributes([.posixPermissions: 0o600], ofItemAtPath: lock.lockURL.path)
        }

        XCTAssertTrue(FileManager.default.fileExists(atPath: lock.lockURL.path))
        XCTAssertTrue(try String(contentsOf: lock.lockURL, encoding: .utf8).contains("token=replacement"))
    }

    func testControllerMutationLockRecoversDeadAtomicOwnerAndUnlinks() throws {
        let home = FileManager.default.temporaryDirectory
            .appendingPathComponent("CodexSyncBarLockStale-\(UUID().uuidString)", isDirectory: true)
        defer { try? FileManager.default.removeItem(at: home) }
        let lock = ControllerMutationLock(home: home)
        try FileManager.default.createDirectory(at: lock.stateRoot, withIntermediateDirectories: true)
        try Data("pid=999999\ntoken=stale\n".utf8).write(to: lock.lockURL)
        try FileManager.default.setAttributes([.posixPermissions: 0o600], ofItemAtPath: lock.lockURL.path)

        var ran = false
        try lock.withLock { ran = true }

        XCTAssertTrue(ran)
        var info = stat()
        XCTAssertNotEqual(lock.lockURL.path.withCString { lstat($0, &info) }, 0)
        XCTAssertEqual(errno, ENOENT)
    }

    func testControllerMutationLockSharesAdvisoryGateWithShellHelper() throws {
        let home = FileManager.default.temporaryDirectory
            .appendingPathComponent("CodexSyncBarLockGate-\(UUID().uuidString)", isDirectory: true)
        defer { try? FileManager.default.removeItem(at: home) }
        let lock = ControllerMutationLock(home: home)

        try lock.withLock {
            let process = Process()
            if FileManager.default.isExecutableFile(atPath: "/usr/bin/lockf") {
                process.executableURL = URL(fileURLWithPath: "/usr/bin/lockf")
                process.arguments = ["-s", "-t", "0", "-k", lock.gateURL.path, "/usr/bin/true"]
            } else {
                let candidates = [
                    "/opt/homebrew/opt/util-linux/bin/flock",
                    "/usr/local/opt/util-linux/bin/flock",
                    "/opt/homebrew/bin/flock",
                    "/usr/local/bin/flock",
                ]
                guard let flock = candidates.first(where: {
                    FileManager.default.isExecutableFile(atPath: $0)
                }) else {
                    throw XCTSkip("lockf 또는 flock이 설치되어 있지 않습니다.")
                }
                process.executableURL = URL(fileURLWithPath: flock)
                process.arguments = ["-n", lock.gateURL.path, "/usr/bin/true"]
            }
            try process.run()
            process.waitUntilExit()
            XCTAssertNotEqual(process.terminationStatus, 0)
        }
    }

    func testControllerMutationLockMigratesOnlyExactSecurePidOnlyLegacyDirectory() throws {
        let home = FileManager.default.temporaryDirectory
            .appendingPathComponent("CodexSyncBarLegacyLock-\(UUID().uuidString)", isDirectory: true)
        defer { try? FileManager.default.removeItem(at: home) }
        let lock = ControllerMutationLock(home: home)
        try FileManager.default.createDirectory(
            at: lock.lockURL,
            withIntermediateDirectories: true,
            attributes: [.posixPermissions: 0o700])
        let owner = lock.lockURL.appendingPathComponent("owner")
        try Data("pid=2147483647\n".utf8).write(to: owner)
        try FileManager.default.setAttributes([.posixPermissions: 0o600], ofItemAtPath: owner.path)

        XCTAssertNoThrow(try lock.withLock {})
        XCTAssertFalse(FileManager.default.fileExists(atPath: lock.lockURL.path))

        try FileManager.default.createDirectory(
            at: lock.lockURL,
            withIntermediateDirectories: false,
            attributes: [.posixPermissions: 0o700])
        try Data("pid=2147483647\n".utf8).write(to: owner)
        try FileManager.default.setAttributes([.posixPermissions: 0o600], ofItemAtPath: owner.path)
        try Data("unexpected".utf8).write(to: lock.lockURL.appendingPathComponent("extra"))
        XCTAssertThrowsError(try lock.withLock {})
    }

    func testControllerMutationLockRejectsPidOnlyAtomicOwnerFile() throws {
        let home = FileManager.default.temporaryDirectory
            .appendingPathComponent("CodexSyncBarAtomicLockFormat-\(UUID().uuidString)", isDirectory: true)
        defer { try? FileManager.default.removeItem(at: home) }
        let lock = ControllerMutationLock(home: home)
        try FileManager.default.createDirectory(at: lock.stateRoot, withIntermediateDirectories: true)
        try Data("pid=2147483647\n".utf8).write(to: lock.lockURL)
        try FileManager.default.setAttributes([.posixPermissions: 0o600], ofItemAtPath: lock.lockURL.path)

        XCTAssertThrowsError(try lock.withLock {})
        XCTAssertTrue(FileManager.default.fileExists(atPath: lock.lockURL.path))
    }

    func testBundledHelperInstallerAtomicallyInstallsMatchingExecutables() throws {
        let root = FileManager.default.temporaryDirectory
            .appendingPathComponent("CodexSyncBarHelperInstaller-\(UUID().uuidString)", isDirectory: true)
        defer { try? FileManager.default.removeItem(at: root) }
        let home = root.appendingPathComponent("home", isDirectory: true)
        let resources = root.appendingPathComponent("resources", isDirectory: true)
        try FileManager.default.createDirectory(at: resources, withIntermediateDirectories: true)
        let helper = resources.appendingPathComponent("gpt-switch")
        let askpass = resources.appendingPathComponent("codex-syncbar-askpass")
        let usageSummary = resources.appendingPathComponent("usage-summary.mjs")
        try Data("#!/bin/bash\nprintf '2.0.0\\n'\n".utf8).write(to: helper)
        try Data("#!/bin/bash\nprintf 'secret'\n".utf8).write(to: askpass)
        try Data("#!/usr/bin/env node\nconsole.log('{}')\n".utf8).write(to: usageSummary)
        try FileManager.default.setAttributes([.posixPermissions: 0o755], ofItemAtPath: helper.path)
        try FileManager.default.setAttributes([.posixPermissions: 0o755], ofItemAtPath: askpass.path)

        try BundledHelperInstaller(home: home, resourceDirectory: resources).install()

        let installedHelper = home.appendingPathComponent(".local/bin/gpt-switch")
        let installedAskpass = home.appendingPathComponent(".local/lib/gpt-switch/codex-syncbar-askpass")
        let installedUsageSummary = home.appendingPathComponent(".local/lib/gpt-switch/usage-summary.mjs")
        XCTAssertEqual(try Data(contentsOf: installedHelper), try Data(contentsOf: helper))
        XCTAssertEqual(try Data(contentsOf: installedAskpass), try Data(contentsOf: askpass))
        XCTAssertEqual(try Data(contentsOf: installedUsageSummary), try Data(contentsOf: usageSummary))
        let helperMode = try FileManager.default.attributesOfItem(atPath: installedHelper.path)[.posixPermissions] as? NSNumber
        let askpassMode = try FileManager.default.attributesOfItem(atPath: installedAskpass.path)[.posixPermissions] as? NSNumber
        let usageMode = try FileManager.default.attributesOfItem(atPath: installedUsageSummary.path)[.posixPermissions] as? NSNumber
        XCTAssertEqual(helperMode?.intValue, 0o755)
        XCTAssertEqual(askpassMode?.intValue, 0o700)
        XCTAssertEqual(usageMode?.intValue, 0o755)
    }

    func testConfigurationBootstrapsExistingAccountsAndDevicesWithoutTouchingAuth() throws {
        let home = FileManager.default.temporaryDirectory
            .appendingPathComponent("CodexSyncBarConfigurationMigration-\(UUID().uuidString)", isDirectory: true)
        defer { try? FileManager.default.removeItem(at: home) }
        let profiles = home.appendingPathComponent(".local/share/gpt-switch/profiles", isDirectory: true)
        try FileManager.default.createDirectory(at: profiles, withIntermediateDirectories: true)

        func token(email: String) throws -> String {
            let payload = try JSONSerialization.data(withJSONObject: ["email": email])
                .base64EncodedString()
                .replacingOccurrences(of: "+", with: "-")
                .replacingOccurrences(of: "/", with: "_")
                .replacingOccurrences(of: "=", with: "")
            return "header.\(payload).signature"
        }
        func writeAuth(_ id: Int, email: String) throws {
            let data = try JSONSerialization.data(withJSONObject: [
                "auth_mode": "chatgpt",
                "tokens": [
                    "id_token": try token(email: email),
                    "access_token": "header.payload.signature",
                    "refresh_token": "refresh",
                    "account_id": "account-\(id)",
                ],
            ])
            let url = profiles.appendingPathComponent("\(id).auth.json")
            try data.write(to: url)
            try FileManager.default.setAttributes([.posixPermissions: 0o600], ofItemAtPath: url.path)
        }
        try writeAuth(1, email: "one@example.com")
        try writeAuth(2, email: "two@example.com")
        let stateRoot = home.appendingPathComponent(".local/share/gpt-switch", isDirectory: true)
        let current = stateRoot.appendingPathComponent("current")
        try Data("2\n".utf8).write(to: current)
        try FileManager.default.setAttributes([.posixPermissions: 0o600], ofItemAtPath: current.path)
        let chromeProfiles = home.appendingPathComponent(
            "Library/Application Support/Codex SyncBar/ChromeProfiles", isDirectory: true)
        let chromeOne = chromeProfiles.appendingPathComponent("profile-1", isDirectory: true)
        let chromeTwo = chromeProfiles.appendingPathComponent("profile-2", isDirectory: true)
        try FileManager.default.createDirectory(at: chromeOne, withIntermediateDirectories: true)
        try FileManager.default.createDirectory(at: chromeTwo, withIntermediateDirectories: true)
        try Data("one-session".utf8).write(to: chromeOne.appendingPathComponent("marker"))
        try Data("two-session".utf8).write(to: chromeTwo.appendingPathComponent("marker"))
        let before = try Data(contentsOf: profiles.appendingPathComponent("1.auth.json"))
        let secondBefore = try Data(contentsOf: profiles.appendingPathComponent("2.auth.json"))
        let currentBefore = try Data(contentsOf: current)

        let store = AppConfigurationStore(home: home)
        let configuration = try store.loadOrMigrate()

        XCTAssertEqual(configuration.accounts.map(\.id), [1, 2])
        XCTAssertEqual(configuration.accounts.map(\.email), ["one@example.com", "two@example.com"])
        XCTAssertEqual(configuration.nextAccountID, 3)
        XCTAssertEqual(configuration.devices.map(\.id), [])
        let migratedCredentialIDs = configuration.devices.compactMap(\.credentialID)
        XCTAssertEqual(migratedCredentialIDs.count, 0)
        XCTAssertEqual(Set(migratedCredentialIDs).count, 0)
        XCTAssertEqual(try Data(contentsOf: profiles.appendingPathComponent("1.auth.json")), before)
        XCTAssertEqual(try Data(contentsOf: profiles.appendingPathComponent("2.auth.json")), secondBefore)
        XCTAssertEqual(try Data(contentsOf: current), currentBefore)
        XCTAssertEqual(
            try String(contentsOf: chromeOne.appendingPathComponent("marker"), encoding: .utf8),
            "one-session")
        XCTAssertEqual(
            try String(contentsOf: chromeTwo.appendingPathComponent("marker"), encoding: .utf8),
            "two-session")
        let mode = try FileManager.default.attributesOfItem(atPath: store.configurationURL.path)[.posixPermissions] as? NSNumber
        XCTAssertEqual(mode?.intValue, 0o600)

        let firstConfigurationBytes = try Data(contentsOf: store.configurationURL)
        XCTAssertEqual(try store.loadOrMigrate(), configuration)
        XCTAssertEqual(try Data(contentsOf: store.configurationURL), firstConfigurationBytes)
    }

    func testConfigurationBackfillsMissingDeviceCredentialIDsOnceWithoutChangingDeviceData() throws {
        let home = FileManager.default.temporaryDirectory
            .appendingPathComponent("CodexSyncBarCredentialBackfill-\(UUID().uuidString)", isDirectory: true)
        defer { try? FileManager.default.removeItem(at: home) }
        let store = AppConfigurationStore(home: home)
        var initial = try store.loadOrMigrate()
        initial.devices = [
            SSHDeviceConfiguration(
                id: "build-server", credentialID: UUID(), displayName: "Build Server",
                host: "build.example.internal", port: 22, username: "alice",
                authentication: .openSSHConfig, identityFile: nil, certificateFile: nil,
                hasPassword: false, hasKeyPassphrase: false, enabled: true),
            SSHDeviceConfiguration(
                id: "test-server", credentialID: UUID(), displayName: "Test Server",
                host: "test.example.internal", port: 22, username: "alice",
                authentication: .openSSHConfig, identityFile: nil, certificateFile: nil,
                hasPassword: false, hasKeyPassphrase: false, enabled: true),
            SSHDeviceConfiguration(
                id: "laptop", credentialID: UUID(), displayName: "Laptop",
                host: "laptop.example.internal", port: 22, username: "alice",
                authentication: .openSSHConfig, identityFile: nil, certificateFile: nil,
                hasPassword: false, hasKeyPassphrase: false, enabled: true),
        ]
        try store.save(initial)

        var object = try XCTUnwrap(
            JSONSerialization.jsonObject(with: Data(contentsOf: store.configurationURL)) as? [String: Any])
        var devices = try XCTUnwrap(object["devices"] as? [[String: Any]])
        let preservedIdentifier = try XCTUnwrap(initial.devices.last?.credentialID)
        for index in devices.indices.dropLast() { devices[index].removeValue(forKey: "credentialID") }
        object["devices"] = devices
        let legacyData = try JSONSerialization.data(withJSONObject: object, options: [.prettyPrinted, .sortedKeys])
        try legacyData.write(to: store.configurationURL, options: [.atomic])
        try FileManager.default.setAttributes(
            [.posixPermissions: 0o600],
            ofItemAtPath: store.configurationURL.path)

        let bytesBeforeRecovery = try Data(contentsOf: store.configurationURL)
        let beforeTransactionRecovery = try AppConfigurationStore(home: home).loadOrMigrate(
            controllerLockHeld: true,
            reconcilePending: false)
        XCTAssertTrue(beforeTransactionRecovery.devices.dropLast().allSatisfy { $0.credentialID == nil })
        XCTAssertEqual(beforeTransactionRecovery.devices.last?.credentialID, preservedIdentifier)
        XCTAssertEqual(try Data(contentsOf: store.configurationURL), bytesBeforeRecovery)

        let migrated = try AppConfigurationStore(home: home).loadOrMigrate()
        let identifiers = migrated.devices.compactMap(\.credentialID)
        XCTAssertEqual(identifiers.count, initial.devices.count)
        XCTAssertEqual(Set(identifiers).count, initial.devices.count)
        XCTAssertEqual(migrated.devices.last?.credentialID, preservedIdentifier)
        XCTAssertEqual(
            migrated.devices.map { device in
                var copy = device
                copy.credentialID = nil
                return copy
            },
            initial.devices.map { device in
                var copy = device
                copy.credentialID = nil
                return copy
            })

        let persistedBytes = try Data(contentsOf: store.configurationURL)
        let reloaded = try AppConfigurationStore(home: home).loadOrMigrate()
        XCTAssertEqual(reloaded.devices.compactMap(\.credentialID), identifiers)
        XCTAssertEqual(try Data(contentsOf: store.configurationURL), persistedBytes)
    }

    func testAccountProfileDecodesPrePendingConfigurationAsCommitted() throws {
        let legacy = Data(#"{"id":2,"email":"two@example.com"}"#.utf8)
        let account = try JSONDecoder().decode(AccountProfile.self, from: legacy)

        XCTAssertEqual(account.id, 2)
        XCTAssertEqual(account.email, "two@example.com")
        XCTAssertFalse(account.isPending)
        XCTAssertNil(account.customAlias)
        XCTAssertEqual(account.alias, "two@example.com")
        XCTAssertEqual(account.shortName, "T")
    }

    func testAccountAliasNormalizesPersistsAndStaysWithAccountIdentity() throws {
        let home = FileManager.default.temporaryDirectory
            .appendingPathComponent("CodexSyncBarAccountAlias-\(UUID().uuidString)", isDirectory: true)
        defer { try? FileManager.default.removeItem(at: home) }
        let store = AppConfigurationStore(home: home)
        let first = try XCTUnwrap(try store.loadOrMigrate().accounts.first)
        try store.updateAccountEmail(id: first.id, email: "first@example.com")
        try store.updateAccountAlias(id: first.id, alias: "  업무  ")

        let second = try store.reserveAccount()
        try store.updateAccountEmail(id: second.id, email: "second@example.com")
        let fiveGraphemes = "가나다라👨‍👩‍👧‍👦"
        XCTAssertEqual(fiveGraphemes.count, 5)
        try store.updateAccountAlias(id: second.id, alias: fiveGraphemes)
        try store.reorderAccounts(ids: [second.id, first.id])
        try store.updateAccountEmail(id: first.id, email: "renamed@example.com")

        let reloaded = try store.load()
        XCTAssertEqual(reloaded.accounts.map(\.id), [second.id, first.id])
        XCTAssertEqual(reloaded.accounts.first?.customAlias, fiveGraphemes)
        XCTAssertEqual(reloaded.accounts.first?.shortName, fiveGraphemes)
        XCTAssertEqual(reloaded.accounts.last?.customAlias, "업무")
        XCTAssertEqual(reloaded.accounts.last?.email, "renamed@example.com")
        XCTAssertTrue(String(decoding: try JSONEncoder().encode(reloaded), as: UTF8.self)
            .contains(#""alias":"업무""#))

        XCTAssertThrowsError(try store.updateAccountAlias(id: first.id, alias: "123456"))
        XCTAssertThrowsError(try store.updateAccountAlias(id: first.id, alias: "업무\n용"))
        try store.updateAccountAlias(id: first.id, alias: "   ")
        XCTAssertNil(try store.load().accounts.first(where: { $0.id == first.id })?.customAlias)
    }

    func testAccountGridUsesTwoColumnsAndAddsOneRowPerTwoAccounts() {
        XCTAssertEqual((0 ... 5).map(AccountGridLayout.rowCount), [0, 1, 1, 2, 2, 3])
    }

    func testAccountDropReorderCalculatesBeforeAndAfterDestinations() {
        let ids = [10, 20, 30, 40]
        XCTAssertEqual(
            AccountReorderLayout.destinationIndex(
                ids: ids, draggedID: 10, targetID: 30, placeAfter: false),
            2)
        XCTAssertEqual(
            AccountReorderLayout.destinationIndex(
                ids: ids, draggedID: 10, targetID: 30, placeAfter: true),
            3)
        XCTAssertEqual(
            AccountReorderLayout.destinationIndex(
                ids: ids, draggedID: 40, targetID: 20, placeAfter: false),
            1)
        XCTAssertEqual(
            AccountReorderLayout.destinationIndex(
                ids: ids, draggedID: 40, targetID: 20, placeAfter: true),
            2)
        XCTAssertNil(
            AccountReorderLayout.destinationIndex(
                ids: ids, draggedID: 20, targetID: 20, placeAfter: false))
        XCTAssertNil(
            AccountReorderLayout.destinationIndex(
                ids: ids, draggedID: 99, targetID: 20, placeAfter: false))
    }

    func testProfileAuthenticationStatusPreservesReloginStateDuringRefresh() {
        let previous = UsageSnapshot(
            profileID: 2,
            email: "sub@example.com",
            plan: "Pro",
            session: nil,
            weekly: nil,
            sparkSession: nil,
            sparkWeekly: nil,
            creditBalance: nil,
            unlimitedCredits: false,
            resetCredits: nil,
            resetCreditExpirations: [],
            updatedAt: Date())

        XCTAssertEqual(
            ProfileAuthenticationStatus.resolve(
                usageState: .idle,
                knownReauthenticationRequired: false),
            .checking)
        XCTAssertEqual(
            ProfileAuthenticationStatus.resolve(
                usageState: .loading(previous: previous),
                knownReauthenticationRequired: false),
            .authenticated)
        XCTAssertEqual(
            ProfileAuthenticationStatus.resolve(
                usageState: .failed(previous: nil, message: "offline", loginRequired: false),
                knownReauthenticationRequired: false),
            .unverified)
        XCTAssertEqual(
            ProfileAuthenticationStatus.resolve(
                usageState: .failed(previous: previous, message: "unauthorized", loginRequired: true),
                knownReauthenticationRequired: false),
            .reauthenticationRequired)
        XCTAssertEqual(
            ProfileAuthenticationStatus.resolve(
                usageState: .loading(previous: previous),
                knownReauthenticationRequired: true),
            .reauthenticationRequired)
        XCTAssertTrue(ProfileAuthenticationStatus.reauthenticationRequired.needsReauthentication)
        XCTAssertEqual(ProfileAuthenticationStatus.reauthenticationRequired.title, "재로그인 필요")
    }

    func testAuthenticationFailureClassifierDistinguishesReloginFromTemporaryFailure() {
        XCTAssertTrue(AuthenticationFailureClassifier.requiresReauthentication(
            "401 Unauthorized: ChatGPT login did not make it to this service"))
        XCTAssertTrue(AuthenticationFailureClassifier.requiresReauthentication(
            "ERROR: Failed to refresh token: Invalid 'refresh_token'"))
        XCTAssertTrue(AuthenticationFailureClassifier.requiresReauthentication(
            "login required: signed in to another account"))
        XCTAssertFalse(AuthenticationFailureClassifier.requiresReauthentication(
            "network connection timed out"))
        XCTAssertFalse(AuthenticationFailureClassifier.requiresReauthentication(nil))
        XCTAssertTrue(AuthenticationFailureClassifier.requiresCanonicalReauthentication(
            AppError.loginRequired("canonical refresh failed")))
        XCTAssertTrue(AuthenticationFailureClassifier.requiresCanonicalReauthentication(
            AppError.invalidAuth))
        XCTAssertFalse(AuthenticationFailureClassifier.requiresCanonicalReauthentication(
            AppError.processFailed("401 Unauthorized: models endpoint")))
    }

    func testMenuTitleUsesAliasWithPercentageAndPreservesStatusSignals() {
        let profile = AccountProfile(id: 1, email: "first@example.com", alias: "업무")
        func snapshot(
            sessionUsed: Double = 20,
            weeklyUsed: Double = 1,
            sparkSessionUsed: Double? = 40,
            sparkWeeklyUsed: Double = 5) -> UsageSnapshot
        {
            UsageSnapshot(
                profileID: 1,
                email: profile.email,
                plan: "Pro",
                session: UsageWindow(usedPercent: sessionUsed, resetsAt: nil, durationSeconds: nil),
                weekly: UsageWindow(usedPercent: weeklyUsed, resetsAt: nil, durationSeconds: nil),
                sparkSession: sparkSessionUsed.map {
                    UsageWindow(usedPercent: $0, resetsAt: nil, durationSeconds: nil)
                },
                sparkWeekly: UsageWindow(usedPercent: sparkWeeklyUsed, resetsAt: nil, durationSeconds: nil),
                creditBalance: nil,
                unlimitedCredits: false,
                resetCredits: nil,
                resetCreditExpirations: [],
                updatedAt: Date())
        }

        XCTAssertEqual(MenuTitleFormatter.title(
            profile: profile,
            state: .loaded(snapshot()),
            items: [.codexWeekly, .sparkWeekly],
            isRefreshing: false,
            hasDeviceMismatch: false), "업무 99% · 95%")
        XCTAssertEqual(MenuTitleFormatter.title(
            profile: profile,
            state: .loaded(snapshot()),
            items: [.codexWeekly],
            isRefreshing: false,
            hasDeviceMismatch: false), "업무 99%")
        XCTAssertEqual(MenuTitleFormatter.title(
            profile: profile,
            state: .loaded(snapshot()),
            items: [],
            isRefreshing: false,
            hasDeviceMismatch: false), "업무")
        XCTAssertEqual(MenuTitleFormatter.title(
            profile: profile,
            state: .idle,
            items: [],
            isRefreshing: true,
            hasDeviceMismatch: true), "업무 !")
        XCTAssertEqual(MenuTitleFormatter.title(
            profile: profile,
            state: .loaded(snapshot()),
            items: [.fiveHour, .sparkFiveHour],
            isRefreshing: false,
            hasDeviceMismatch: false), "업무 80% · 60%")
        XCTAssertEqual(MenuTitleFormatter.title(
            profile: profile,
            state: .loaded(snapshot(weeklyUsed: 95)),
            items: [.codexWeekly, .sparkWeekly],
            isRefreshing: false,
            hasDeviceMismatch: false), "⚠ 업무 5% · 95%")
        XCTAssertEqual(MenuTitleFormatter.title(
            profile: profile,
            state: .loaded(snapshot(sparkSessionUsed: nil)),
            items: [.sparkFiveHour, .fiveHour],
            isRefreshing: false,
            hasDeviceMismatch: false), "업무 — · 80%")
        XCTAssertEqual(MenuTitleFormatter.title(
            profile: profile,
            state: .loaded(snapshot()),
            items: [.fiveHour, .codexWeekly],
            isRefreshing: false,
            hasDeviceMismatch: true), "업무 80% · 99% !")
        XCTAssertEqual(MenuTitleFormatter.title(
            profile: profile,
            state: .idle,
            items: [.fiveHour, .codexWeekly],
            isRefreshing: true,
            hasDeviceMismatch: false), "업무 ···")
        XCTAssertEqual(MenuTitleFormatter.title(
            profile: profile,
            state: .failed(previous: nil, message: "expired", loginRequired: true),
            items: [.fiveHour, .codexWeekly],
            isRefreshing: false,
            hasDeviceMismatch: false), "업무 🔒")
    }

    func testPendingAccountReconciliationCommitsValidFullAuthAndRemovesAbandonedReservation() throws {
        let home = FileManager.default.temporaryDirectory
            .appendingPathComponent("CodexSyncBarPendingAccount-\(UUID().uuidString)", isDirectory: true)
        defer { try? FileManager.default.removeItem(at: home) }
        let store = AppConfigurationStore(home: home)
        let initial = try store.loadOrMigrate()
        XCTAssertEqual(initial.accounts.count, 1)
        XCTAssertTrue(initial.accounts[0].isPending)

        let completed = try store.reserveAccount()
        let abandoned = try store.reserveAccount()
        XCTAssertTrue(completed.isPending)
        XCTAssertTrue(abandoned.isPending)

        let profiles = home.appendingPathComponent(".local/share/gpt-switch/profiles", isDirectory: true)
        try FileManager.default.createDirectory(at: profiles, withIntermediateDirectories: true)
        let payload = try JSONSerialization.data(withJSONObject: ["email": "completed@example.com"])
            .base64EncodedString()
            .replacingOccurrences(of: "+", with: "-")
            .replacingOccurrences(of: "/", with: "_")
            .replacingOccurrences(of: "=", with: "")
        let auth = try JSONSerialization.data(withJSONObject: [
            "auth_mode": "chatgpt",
            "tokens": [
                "id_token": "header.\(payload).signature",
                "access_token": "header.payload.signature",
                "refresh_token": "refresh",
                "account_id": "account-completed",
            ],
        ])
        let authURL = profiles.appendingPathComponent("\(completed.id).auth.json")
        try auth.write(to: authURL)
        try FileManager.default.setAttributes([.posixPermissions: 0o600], ofItemAtPath: authURL.path)

        // A fresh store models the next app process after a crash/termination.
        let reconciled = try AppConfigurationStore(home: home).loadOrMigrate()

        XCTAssertEqual(reconciled.accounts.map(\.id), [completed.id])
        XCTAssertEqual(reconciled.accounts.last?.email, "completed@example.com")
        XCTAssertFalse(try XCTUnwrap(reconciled.accounts.last).isPending)
        XCTAssertFalse(reconciled.accounts.contains(where: { $0.id == abandoned.id }))
        XCTAssertFalse(reconciled.accounts.contains(where: { $0.id == 1 }))
    }

    func testPendingReconciliationRetainsOneFreshSlotWhenNoAuthExists() throws {
        let home = FileManager.default.temporaryDirectory
            .appendingPathComponent("CodexSyncBarSinglePending-\(UUID().uuidString)", isDirectory: true)
        defer { try? FileManager.default.removeItem(at: home) }
        let store = AppConfigurationStore(home: home)
        let migrated = try store.loadOrMigrate()

        let reconciled = try store.reconcilePendingAccounts()

        XCTAssertEqual(reconciled.accounts, migrated.accounts)
        XCTAssertEqual(reconciled.accounts.count, 1)
        XCTAssertTrue(reconciled.accounts[0].isPending)
    }

    func testPendingReconciliationWaitsForControllerLock() throws {
        let home = FileManager.default.temporaryDirectory
            .appendingPathComponent("CodexSyncBarPendingLock-\(UUID().uuidString)", isDirectory: true)
        defer { try? FileManager.default.removeItem(at: home) }
        let store = AppConfigurationStore(home: home)
        let initial = try store.loadOrMigrate()
        XCTAssertTrue(try XCTUnwrap(initial.accounts.first).isPending)

        let lock = home.appendingPathComponent(".local/share/gpt-switch/.controller-lock", isDirectory: true)
        try FileManager.default.createDirectory(at: lock, withIntermediateDirectories: false)
        let owner = lock.appendingPathComponent("owner")
        try Data("pid=999999\n".utf8).write(to: owner)
        let whileLocked = try store.reconcilePendingAccounts()
        XCTAssertEqual(whileLocked.accounts, initial.accounts)
    }

    func testDeviceActivationTransactionBlocksOtherConfigurationMutation() throws {
        let home = FileManager.default.temporaryDirectory
            .appendingPathComponent("CodexSyncBarActivationActivity-\(UUID().uuidString)", isDirectory: true)
        defer { try? FileManager.default.removeItem(at: home) }
        let store = AppConfigurationStore(home: home)
        _ = try store.loadOrMigrate()
        let directory = home.appendingPathComponent(
            ".local/share/gpt-switch/device-activation-transactions",
            isDirectory: true)
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: false)
        let intent = directory.appendingPathComponent("intent.json")
        try Data("{}".utf8).write(to: intent)

        XCTAssertTrue(store.hasControllerActivity())
        try FileManager.default.removeItem(at: intent)
        let staging = directory.appendingPathComponent(
            ".01234567-89AB-CDEF-0123-456789ABCDEF.tmp")
        try Data("{}".utf8).write(to: staging)
        XCTAssertTrue(store.hasControllerActivity())

        try FileManager.default.removeItem(at: staging)
        let credentialDirectory = home.appendingPathComponent(
            ".local/share/gpt-switch/credential-transactions",
            isDirectory: true)
        try FileManager.default.createDirectory(at: credentialDirectory, withIntermediateDirectories: false)
        try Data("{}".utf8).write(to: credentialDirectory.appendingPathComponent("intent.json"))
        XCTAssertTrue(store.hasControllerActivity())
    }

    func testStartupDefersPendingReconciliationUntilDurableLoginRecovery() throws {
        let home = FileManager.default.temporaryDirectory
            .appendingPathComponent("CodexSyncBarDeferredPending-\(UUID().uuidString)", isDirectory: true)
        defer { try? FileManager.default.removeItem(at: home) }
        let profiles = home.appendingPathComponent(".local/share/gpt-switch/profiles", isDirectory: true)
        try FileManager.default.createDirectory(at: profiles, withIntermediateDirectories: true)

        func writeAuth(id: Int, email: String) throws -> URL {
            let payload = try JSONSerialization.data(withJSONObject: ["email": email])
                .base64EncodedString()
                .replacingOccurrences(of: "+", with: "-")
                .replacingOccurrences(of: "/", with: "_")
                .replacingOccurrences(of: "=", with: "")
            let data = try JSONSerialization.data(withJSONObject: [
                "auth_mode": "chatgpt",
                "tokens": [
                    "id_token": "header.\(payload).signature",
                    "access_token": "header.payload.signature",
                    "refresh_token": "refresh-\(id)",
                    "account_id": "account-\(id)",
                ],
            ])
            let url = profiles.appendingPathComponent("\(id).auth.json")
            try data.write(to: url)
            try FileManager.default.setAttributes([.posixPermissions: 0o600], ofItemAtPath: url.path)
            return url
        }

        _ = try writeAuth(id: 1, email: "one@example.com")
        let store = AppConfigurationStore(home: home)
        _ = try store.loadOrMigrate()
        let pending = try store.reserveAccount()
        let pendingAuth = try writeAuth(id: pending.id, email: "pending@example.com")
        let transaction = home.appendingPathComponent(
            ".local/share/gpt-switch/login-transactions/\(pending.id).prepared", isDirectory: true)
        try FileManager.default.createDirectory(at: transaction, withIntermediateDirectories: true)
        try Data("prepared".utf8).write(to: transaction.appendingPathComponent("marker"))

        let beforeRecovery = try store.loadOrMigrate(
            controllerLockHeld: true,
            reconcilePending: false)
        XCTAssertTrue(try XCTUnwrap(beforeRecovery.accounts.first(where: { $0.id == pending.id })).isPending)

        // Models recover-controller rolling a prepared import back.
        try FileManager.default.removeItem(at: pendingAuth)
        try FileManager.default.removeItem(at: transaction)
        let reconciled = try store.reconcilePendingAccounts(controllerLockHeld: true)
        XCTAssertEqual(reconciled.accounts.map(\.id), [1])
        XCTAssertEqual(reconciled.accounts.first?.email, "one@example.com")
    }

    func testAuthArtifactDetectionIncludesDanglingSymlink() async throws {
        let home = FileManager.default.temporaryDirectory
            .appendingPathComponent("CodexSyncBarDanglingAuth-\(UUID().uuidString)", isDirectory: true)
        defer { try? FileManager.default.removeItem(at: home) }
        let profiles = home.appendingPathComponent(".local/share/gpt-switch/profiles", isDirectory: true)
        try FileManager.default.createDirectory(at: profiles, withIntermediateDirectories: true)
        try FileManager.default.createSymbolicLink(
            at: profiles.appendingPathComponent("9.auth.json"),
            withDestinationURL: profiles.appendingPathComponent("missing.auth.json"))

        let exists = await AuthStore(home: home).profileArtifactExists(for: 9)
        XCTAssertTrue(exists)
    }

    func testPendingReconciliationFailsClosedForDanglingAuthSymlink() throws {
        let home = FileManager.default.temporaryDirectory
            .appendingPathComponent("CodexSyncBarPendingDangling-\(UUID().uuidString)", isDirectory: true)
        defer { try? FileManager.default.removeItem(at: home) }
        let store = AppConfigurationStore(home: home)
        _ = try store.loadOrMigrate()
        let pending = try store.reserveAccount()
        let profiles = home.appendingPathComponent(".local/share/gpt-switch/profiles", isDirectory: true)
        try FileManager.default.createDirectory(at: profiles, withIntermediateDirectories: true)
        let authURL = profiles.appendingPathComponent("\(pending.id).auth.json")
        try FileManager.default.createSymbolicLink(
            at: authURL,
            withDestinationURL: profiles.appendingPathComponent("missing.auth.json"))
        let before = try Data(contentsOf: store.configurationURL)

        XCTAssertThrowsError(try store.reconcilePendingAccounts())
        XCTAssertEqual(try Data(contentsOf: store.configurationURL), before)
        XCTAssertTrue(try store.load().accounts.contains(where: { $0.id == pending.id && $0.isPending }))
        var info = stat()
        XCTAssertEqual(authURL.path.withCString { lstat($0, &info) }, 0)
        XCTAssertEqual(info.st_mode & S_IFMT, S_IFLNK)
    }

    func testPendingAccountWithMalformedAuthFailsClosedAndPreservesRegistry() throws {
        let home = FileManager.default.temporaryDirectory
            .appendingPathComponent("CodexSyncBarPendingMalformed-\(UUID().uuidString)", isDirectory: true)
        defer { try? FileManager.default.removeItem(at: home) }
        let store = AppConfigurationStore(home: home)
        _ = try store.loadOrMigrate()
        let pending = try store.reserveAccount()
        let profiles = home.appendingPathComponent(".local/share/gpt-switch/profiles", isDirectory: true)
        try FileManager.default.createDirectory(at: profiles, withIntermediateDirectories: true)
        let authURL = profiles.appendingPathComponent("\(pending.id).auth.json")
        try Data(#"{"auth_mode":"chatgpt","tokens":{}}"#.utf8).write(to: authURL)
        try FileManager.default.setAttributes([.posixPermissions: 0o600], ofItemAtPath: authURL.path)
        let before = try Data(contentsOf: store.configurationURL)

        XCTAssertThrowsError(try store.reconcilePendingAccounts())
        XCTAssertEqual(try Data(contentsOf: store.configurationURL), before)
        XCTAssertTrue(try store.load().accounts.contains(where: { $0.id == pending.id && $0.isPending }))
    }

    func testMigrationRefusesToRunBeforeLegacySwapJournalRecovery() throws {
        let home = FileManager.default.temporaryDirectory
            .appendingPathComponent("CodexSyncBarLegacyJournal-\(UUID().uuidString)", isDirectory: true)
        defer { try? FileManager.default.removeItem(at: home) }
        let journalDirectory = home.appendingPathComponent(
            "Library/Application Support/Codex SyncBar", isDirectory: true)
        try FileManager.default.createDirectory(at: journalDirectory, withIntermediateDirectories: true)
        try Data("{}".utf8).write(to: journalDirectory.appendingPathComponent("profile-swap-journal.json"))
        let store = AppConfigurationStore(home: home)

        XCTAssertThrowsError(try store.loadOrMigrate()) { error in
            XCTAssertTrue(error.localizedDescription.contains("복구"), error.localizedDescription)
        }
        XCTAssertFalse(FileManager.default.fileExists(atPath: store.configurationURL.path))
    }

    func testMigrationAlsoRefusesControllerSwapJournal() throws {
        let home = FileManager.default.temporaryDirectory
            .appendingPathComponent("CodexSyncBarControllerJournal-\(UUID().uuidString)", isDirectory: true)
        defer { try? FileManager.default.removeItem(at: home) }
        let stateRoot = home.appendingPathComponent(".local/share/gpt-switch", isDirectory: true)
        try FileManager.default.createDirectory(
            at: stateRoot.appendingPathComponent(".swap-profiles.recovery", isDirectory: true),
            withIntermediateDirectories: true)
        let store = AppConfigurationStore(home: home)

        XCTAssertThrowsError(try store.loadOrMigrate())
        XCTAssertFalse(FileManager.default.fileExists(atPath: store.configurationURL.path))
    }

    func testMalformedConfigurationFailsClosedWithoutRecreatingOrTouchingInventory() throws {
        let home = FileManager.default.temporaryDirectory
            .appendingPathComponent("CodexSyncBarMalformedConfiguration-\(UUID().uuidString)", isDirectory: true)
        defer { try? FileManager.default.removeItem(at: home) }
        let stateRoot = home.appendingPathComponent(".local/share/gpt-switch", isDirectory: true)
        let profiles = stateRoot.appendingPathComponent("profiles", isDirectory: true)
        try FileManager.default.createDirectory(at: profiles, withIntermediateDirectories: true)
        let profileTwo = profiles.appendingPathComponent("2.auth.json")
        let profileBytes = Data("profile-two-must-not-change".utf8)
        try profileBytes.write(to: profileTwo)
        let current = stateRoot.appendingPathComponent("current")
        try Data("2\n".utf8).write(to: current)
        let store = AppConfigurationStore(home: home)
        let malformed = Data(#"{"schemaVersion":1,"accounts":"wrong"}"#.utf8)
        try malformed.write(to: store.configurationURL)
        try FileManager.default.setAttributes([.posixPermissions: 0o600], ofItemAtPath: store.configurationURL.path)

        XCTAssertThrowsError(try store.loadOrMigrate())
        XCTAssertEqual(try Data(contentsOf: store.configurationURL), malformed)
        XCTAssertEqual(try Data(contentsOf: profileTwo), profileBytes)
        XCTAssertEqual(try String(contentsOf: current, encoding: .utf8), "2\n")
    }

    func testConfigurationSupportsThreeAccountsAndReordersOnlyTheRegistry() throws {
        let home = FileManager.default.temporaryDirectory
            .appendingPathComponent("CodexSyncBarConfigurationOrder-\(UUID().uuidString)", isDirectory: true)
        defer { try? FileManager.default.removeItem(at: home) }
        let store = AppConfigurationStore(home: home)
        _ = try store.loadOrMigrate()

        _ = try store.reserveAccount()
        let third = try store.reserveAccount()
        XCTAssertEqual(third.id, 3)
        try store.updateAccountEmail(id: third.id, email: "third@example.com")
        try store.reorderAccounts(ids: [3, 1, 2])
        let reloaded = try store.load()

        XCTAssertEqual(reloaded.accounts.map(\.id), [3, 1, 2])
        XCTAssertEqual(reloaded.accounts.first?.email, "third@example.com")
        XCTAssertFalse(try XCTUnwrap(reloaded.accounts.first).isPending)
        XCTAssertEqual(reloaded.nextAccountID, 4)
    }

    func testSSHDeviceValidationAndEncodingNeverContainsSecrets() throws {
        let device = SSHDeviceConfiguration(
            id: "build-server",
            credentialID: UUID(),
            displayName: "빌드 서버",
            host: "10.0.0.20",
            port: 2222,
            username: "alice",
            authentication: .privateKey,
            identityFile: "/Users/example/.ssh/id_ed25519",
            certificateFile: "/Users/example/.ssh/id_ed25519-cert.pub",
            hasPassword: false,
            hasKeyPassphrase: true,
            enabled: true)

        XCTAssertNoThrow(try device.validate(checkFiles: false))
        let encoded = String(decoding: try JSONEncoder().encode(device), as: UTF8.self)
        XCTAssertFalse(encoded.contains("password-value"))
        XCTAssertFalse(encoded.contains("passphrase-value"))

        var invalid = device
        invalid.host = "host; touch /tmp/injected"
        XCTAssertThrowsError(try invalid.validate(checkFiles: false))

        invalid = device
        invalid.id = "macbook"
        XCTAssertThrowsError(try invalid.validate(checkFiles: false))

        var displayOnly = device
        displayOnly.displayName = "다른 표시 이름"
        XCTAssertTrue(device.hasSameCredentialEndpoint(as: displayOnly))
        XCTAssertFalse(displayOnly.requiresActivationValidation(
            replacing: device,
            secretWasMutated: false))
        XCTAssertTrue(displayOnly.requiresActivationValidation(
            replacing: device,
            secretWasMutated: true))
        var changedEndpoint = device
        changedEndpoint.port = 22
        XCTAssertFalse(device.hasSameCredentialEndpoint(as: changedEndpoint))
        XCTAssertTrue(changedEndpoint.requiresActivationValidation(
            replacing: device,
            secretWasMutated: false))
    }

    func testSSHDeviceCredentialIDIsBackwardCompatibleAndStableAcrossSaves() throws {
        let legacy = Data(#"{"id":"build","displayName":"Build","host":"10.0.0.20","port":22,"username":"alice","authentication":"openSSHConfig","hasPassword":false,"hasKeyPassphrase":false,"enabled":true}"#.utf8)
        let decoded = try JSONDecoder().decode(SSHDeviceConfiguration.self, from: legacy)
        XCTAssertNil(decoded.credentialID)

        let home = FileManager.default.temporaryDirectory
            .appendingPathComponent("CodexSyncBarCredentialID-(UUID().uuidString)", isDirectory: true)
        defer { try? FileManager.default.removeItem(at: home) }
        let store = AppConfigurationStore(home: home)
        _ = try store.loadOrMigrate()
        let first = try store.upsertDevice(decoded)
        let firstID = try XCTUnwrap(first.credentialID)
        var edited = first
        edited.displayName = "Build 2"
        let second = try store.upsertDevice(edited)

        XCTAssertEqual(second.credentialID, firstID)
        XCTAssertTrue(first.hasSameCredentialEndpoint(as: second))
        var movedEndpoint = second
        movedEndpoint.host = "10.0.0.21"
        XCTAssertFalse(second.hasSameCredentialEndpoint(as: movedEndpoint))
        XCTAssertEqual(second.keychainCredentialKey, firstID.uuidString.lowercased())
        XCTAssertEqual(try store.load().devices.first(where: { $0.id == "build" })?.credentialID, firstID)
    }

    func testDeviceActivationCanBeDurablyRolledBackWithoutOverwritingEdits() throws {
        let home = FileManager.default.temporaryDirectory
            .appendingPathComponent("CodexSyncBarDeviceActivation-\(UUID().uuidString)", isDirectory: true)
        defer { try? FileManager.default.removeItem(at: home) }
        let store = AppConfigurationStore(home: home)
        _ = try store.loadOrMigrate()
        let original = try store.upsertDevice(SSHDeviceConfiguration(
            id: "new-node",
            credentialID: UUID(),
            displayName: "새 장치",
            host: "10.0.0.99",
            port: 22,
            username: "alice",
            authentication: .openSSHConfig,
            identityFile: nil,
            certificateFile: nil,
            hasPassword: false,
            hasKeyPassphrase: false,
            enabled: false))

        try store.beginDeviceActivation(original)
        XCTAssertEqual(
            try store.load().devices.first(where: { $0.id == original.id })?.enabled,
            true)
        try store.rollbackDeviceActivation(original)
        XCTAssertEqual(
            try store.load().devices.first(where: { $0.id == original.id }),
            original)

        try store.beginDeviceActivation(original)
        var edited = original
        edited.enabled = true
        edited.displayName = "사용자가 바꾼 이름"
        _ = try store.upsertDevice(edited)
        XCTAssertThrowsError(try store.rollbackDeviceActivation(original))
        XCTAssertEqual(
            try store.load().devices.first(where: { $0.id == original.id })?.displayName,
            edited.displayName)
    }

    func testBootstrapSummaryParserUsesLastStrictSummaryLine() throws {
        let output = """
        warning device=other result=ignored
        remote banner key=value
        device=build-server result=ok active=3 profiles=4 version=2.0.0
        """
        let result = try SwitchService.parseBootstrapResult(
            output,
            expectedDeviceID: "build-server")
        XCTAssertEqual(result.deviceID, "build-server")
        XCTAssertEqual(result.activeProfileID, 3)
        XCTAssertEqual(result.output, output)
    }

    func testBootstrapSummaryParserRejectsDuplicateOrMalformedFields() {
        XCTAssertThrowsError(try SwitchService.parseBootstrapResult(
            "device=build device=build result=ok active=2 profiles=2 version=2.0.0",
            expectedDeviceID: "build"))
        XCTAssertThrowsError(try SwitchService.parseBootstrapResult(
            "device=build result=ok active=0 profiles=2 version=2.0.0",
            expectedDeviceID: "build"))
        XCTAssertThrowsError(try SwitchService.parseBootstrapResult(
            "device=other result=ok active=2 profiles=2 version=2.0.0",
            expectedDeviceID: "build"))
    }

    func testBootstrapActivationRequiresMacAndNewDeviceOnSameProfile() {
        let macbook = DeviceStatus(
            name: "macbook",
            profileID: 3,
            accountFingerprint: "mac",
            authMode: "chatgpt",
            cliState: "logged-in",
            isReachable: true)
        let newNode = DeviceStatus(
            name: "new-node",
            profileID: 3,
            accountFingerprint: "node",
            authMode: "chatgpt",
            cliState: "logged-in",
            isReachable: true)
        XCTAssertTrue(SwitchService.bootstrapActivationIsConsistent(
            statuses: [macbook, newNode],
            deviceID: "new-node",
            activeProfileID: 3))

        let mismatched = DeviceStatus(
            name: "new-node",
            profileID: 2,
            accountFingerprint: "node",
            authMode: "chatgpt",
            cliState: "logged-in",
            isReachable: true)
        XCTAssertFalse(SwitchService.bootstrapActivationIsConsistent(
            statuses: [macbook, mismatched],
            deviceID: "new-node",
            activeProfileID: 3))
        XCTAssertFalse(SwitchService.bootstrapActivationIsConsistent(
            statuses: [newNode],
            deviceID: "new-node",
            activeProfileID: 3))
    }

    func testStatusParserMapsAllNodesAndUnreachableState() {
        let input = """
        NODE      PROFILE  ACCOUNT      MODE   AUTH       CLI
        --------- -------- ------------ ------ ---------- -------------
        macbook   1        abcdef123456 600    chatgpt    logged-in
        ml        2        123456abcdef 600    chatgpt    logged-in
        rogally   2        123456abcdef 600    chatgpt    not-installed
        laptop    unreachable unknown   unknown unknown    unknown
        """

        let devices = SwitchService.parseStatus(input)

        XCTAssertEqual(devices.count, 4)
        XCTAssertEqual(devices[0].profileID, 1)
        XCTAssertEqual(devices[1].displayName, "ML 서버")
        XCTAssertEqual(devices[2].cliState, "not-installed")
        XCTAssertFalse(devices[3].isReachable)
        XCTAssertNil(devices[3].profileID)
    }

    func testStatusJSONParserSupportsDynamicDeviceNamesAndProfiles() throws {
        let input = """
        {"id":"macbook","displayName":"이 MacBook","profileID":3,"accountFingerprint":"abc","authMode":"chatgpt","cliState":"logged-in","isReachable":true}
        {"id":"build-server","displayName":"서울 빌드 서버","profileID":3,"accountFingerprint":"abc","authMode":"chatgpt","cliState":"logged-in","isReachable":true}
        {"id":"offline","displayName":"오프라인 장치","profileID":null,"accountFingerprint":null,"authMode":null,"cliState":null,"isReachable":false}
        """

        let devices = try SwitchService.parseStatusJSON(input)

        XCTAssertEqual(devices.map(\.id), ["macbook", "build-server", "offline"])
        XCTAssertEqual(devices[1].displayName, "서울 빌드 서버")
        XCTAssertEqual(devices[1].profileID, 3)
        XCTAssertFalse(devices[2].isReachable)
    }

    func testPopoverKeepsAccountManagementInSettingsAndOffersSafeRefresh() throws {
        let packageRoot = URL(fileURLWithPath: #filePath)
            .deletingLastPathComponent()
            .deletingLastPathComponent()
            .deletingLastPathComponent()
        let popover = try String(contentsOf: packageRoot
            .appendingPathComponent("Sources/CodexSyncBar/Views/PopoverView.swift"), encoding: .utf8)
        let settings = try String(contentsOf: packageRoot
            .appendingPathComponent("Sources/CodexSyncBar/Views/SettingsView.swift"), encoding: .utf8)
        let appModel = try String(contentsOf: packageRoot
            .appendingPathComponent("Sources/CodexSyncBar/AppModel.swift"), encoding: .utf8)

        XCTAssertFalse(popover.contains("ScrollView"))
        XCTAssertTrue(popover.contains("model.beginLogin(profileID: model.selectedProfileID)"))
        XCTAssertTrue(popover.contains("accessibilityIdentifier(\"reauthentication-button\")"))
        XCTAssertTrue(popover.contains("전용 Chromium에서 Codex 인증을 다시 연결합니다."))
        XCTAssertFalse(appModel.contains("weeklyAnchorRecords[profileID]?.lastError"))
        XCTAssertTrue(appModel.contains("AuthenticationFailureClassifier.requiresCanonicalReauthentication(error)"))
        XCTAssertTrue(appModel.contains("credentialRefresher: { profileID, failedAccessToken in"))
        XCTAssertTrue(appModel.contains("completed.nextResetAt = observedNextResetAt.flatMap"))
        XCTAssertFalse(popover.contains("model.logout"))
        XCTAssertTrue(popover.contains("Task { await model.refreshAll() }"))
        XCTAssertTrue(popover.contains("accessibilityIdentifier(\"refresh-button\")"))
        XCTAssertTrue(popover.contains("사용량과 기기 상태 새로고침"))
        XCTAssertTrue(popover.contains("Image(systemName: \"gearshape\")"))
        XCTAssertTrue(popover.contains("Image(systemName: \"arrow.clockwise\")"))
        XCTAssertFalse(popover.contains("Label(\"설정\", systemImage: \"gearshape\")"))
        XCTAssertFalse(popover.contains("Text(model.isRefreshing ? \"갱신 중\" : \"새로고침\")"))
        XCTAssertFalse(popover.contains("model.toggleLaunchAtLogin"))
        XCTAssertFalse(popover.contains("@Environment(\\.openWindow)"))
        XCTAssertFalse(popover.contains("showSwitchConfirmation"))
        XCTAssertFalse(popover.contains(".alert("))
        XCTAssertFalse(popover.contains("계정 위치 바꾸기"))
        XCTAssertTrue(popover.contains("LazyVGrid"))
        XCTAssertTrue(popover.contains("presentSettings()"))
        XCTAssertFalse(popover.contains("presentSwitchConfirmation"))
        XCTAssertTrue(popover.contains("Task { await model.switchAll(to: profileID) }"))
        XCTAssertTrue(popover.contains("FooterActionButtonStyle"))
        XCTAssertTrue(popover.contains("configuration.isPressed"))
        XCTAssertTrue(popover.contains("scaleEffect(configuration.isPressed"))
        XCTAssertTrue(popover.contains("accessibilityIdentifier(\"quit-button\")"))
        XCTAssertFalse(popover.contains("sessionForDisplay"))
        XCTAssertTrue(popover.contains("visibleUsageMetrics"))
        XCTAssertTrue(popover.contains("window: snapshot?.session"))
        XCTAssertTrue(popover.contains("window: snapshot?.weekly"))
        XCTAssertTrue(popover.contains("window: snapshot?.sparkSession"))
        XCTAssertTrue(popover.contains("window: snapshot?.sparkWeekly"))
        XCTAssertTrue(popover.contains("QuotaUsageRow("))
        XCTAssertFalse(popover.contains("miniUsage("))
        XCTAssertTrue(popover.contains("title: \"선택 계정\""))
        XCTAssertTrue(popover.contains("title: \"전체 기기\""))
        XCTAssertTrue(popover.contains("detail: \"계정 선택과 무관한 공통 정보\""))
        XCTAssertTrue(popover.contains("reauthenticationButton\n                usageCard\n                resetCreditsCard"))
        XCTAssertTrue(popover.contains("resetCreditsCard\n                sectionHeader("))
        XCTAssertTrue(popover.contains("tokenUsageCard\n                switchButton"))
        XCTAssertFalse(popover.contains("private var deviceCard"))
        XCTAssertTrue(popover.contains("CombinedDeviceRow("))
        XCTAssertTrue(popover.contains("Text(\"최근 30일 사용량과 적용 계정\")"))
        XCTAssertTrue(popover.contains("모든 기기 합계 · API 요금 환산 추정치"))
        XCTAssertTrue(popover.contains("API Priority 단가"))
        XCTAssertFalse(popover.contains("Fast 배율 반영"))
        XCTAssertTrue(popover.contains("Text(\"적용\")"))
        XCTAssertTrue(popover.contains("accessibilityIdentifier(\"token-usage-card\")"))
        XCTAssertTrue(popover.contains("private var resetCreditsCard: some View"))
        XCTAssertFalse(popover.contains("resetCreditsRow"))
        XCTAssertTrue(popover.contains("ViewThatFits(in: .horizontal)"))
        XCTAssertTrue(popover.contains("accessibilityIdentifier(\"reset-credit-row\")"))
        XCTAssertTrue(popover.contains("Image(systemName: \"ticket.fill\")"))
        XCTAssertTrue(popover.contains("Text(\"초기화권\")"))
        XCTAssertTrue(popover.contains("Text(\"다음 만료\")"))
        XCTAssertTrue(popover.contains("AppTheme.yellow.opacity(0.24)"))
        XCTAssertFalse(popover.contains("resetCreditExpiryLine"))
        XCTAssertTrue(settings.contains("계정 추가"))
        XCTAssertTrue(settings.contains("장치 추가"))
        XCTAssertTrue(settings.contains("모두 새로고침"))
        XCTAssertTrue(settings.contains("SettingsGroupTitle(\"사용량 표시\")"))
        XCTAssertTrue(settings.contains("usage-display-five-hour-toggle"))
        XCTAssertTrue(settings.contains("usage-display-codex-weekly-toggle"))
        XCTAssertTrue(settings.contains("usage-display-spark-five-hour-toggle"))
        XCTAssertTrue(settings.contains("usage-display-spark-weekly-toggle"))
        XCTAssertTrue(settings.contains("SettingsGroupTitle(\"주간 주기 고정\")"))
        XCTAssertTrue(settings.contains("weekly-anchor-toggle-"))
        XCTAssertTrue(settings.contains("setWeeklyAnchorEnabled"))
        XCTAssertTrue(settings.contains("SettingsGroupTitle(\"상단 메뉴바\")"))
        XCTAssertTrue(settings.contains("menu-bar-item-count-picker"))
        XCTAssertTrue(settings.contains("menu-bar-primary-item-slot"))
        XCTAssertTrue(settings.contains("menu-bar-secondary-item-slot"))
        XCTAssertTrue(settings.contains("로그인 시 Codex SyncBar 자동 실행"))
        XCTAssertTrue(settings.contains("로그아웃"))
        XCTAssertFalse(settings.contains(".onMove(perform: model.moveAccounts)"))
        XCTAssertTrue(settings.contains(".onDrag"))
        XCTAssertTrue(settings.contains(".dropDestination(for: String.self)"))
        XCTAssertFalse(settings.contains("accountToRemove"))
        XCTAssertFalse(settings.contains("Image(systemName: \"ellipsis.circle\")"))
        XCTAssertTrue(settings.contains("line.3.horizontal"))
        XCTAssertTrue(settings.contains("reorder-handle-"))
        XCTAssertTrue(settings.contains("별칭 (최대 5자)"))
        XCTAssertTrue(settings.contains("launch-toggle"))
        XCTAssertTrue(settings.contains(".labelsHidden()"))
        XCTAssertFalse(settings.contains("Text(model.launchAtLoginStatusText)"))
        XCTAssertFalse(popover.contains("height: AppLayout.popoverHeight"))
        XCTAssertFalse(popover.contains("maxHeight: .infinity"))
        XCTAssertTrue(popover.contains(".fixedSize(horizontal: false, vertical: true)"))
        XCTAssertTrue(popover.contains("PopoverHeightPreferenceKey"))
        XCTAssertTrue(popover.contains("ForEach(snapshot.devices)"))
        XCTAssertFalse(popover.contains("devicePreviewLimit"))
        XCTAssertFalse(popover.contains("ellipsis.circle"))
        XCTAssertFalse(popover.contains("설정…"))
        XCTAssertTrue(appModel.contains("showTransientBanner("))
        XCTAssertTrue(appModel.contains("dismissAfterNanoseconds: UInt64 = 4_000_000_000"))
        XCTAssertTrue(appModel.contains("transientBannerID = nextBanner.id"))
        XCTAssertTrue(appModel.contains("guard banner?.id == transientBannerID else { return }"))
        XCTAssertTrue(appModel.contains("SSH 연결과 helper 버전을 확인했습니다."))
    }

    func testSettingsUseSafeSSHDefaultsAndBundleVersion() throws {
        let packageRoot = URL(fileURLWithPath: #filePath)
            .deletingLastPathComponent()
            .deletingLastPathComponent()
            .deletingLastPathComponent()
        let settings = try String(contentsOf: packageRoot
            .appendingPathComponent("Sources/CodexSyncBar/Views/SettingsView.swift"), encoding: .utf8)
        let loginCoordinator = try String(contentsOf: packageRoot
            .appendingPathComponent("Sources/CodexSyncBar/LoginCoordinator.swift"), encoding: .utf8)

        XCTAssertTrue(settings.contains("hasKeyPassphrase: false, enabled: false"))
        XCTAssertTrue(settings.contains("SSHAuthenticationKind.allCases"))
        XCTAssertTrue(settings.contains("사용 안 함"))
        XCTAssertTrue(settings.contains("model.managementActionsDisabled"))
        XCTAssertTrue(settings.contains("AppVersion.current"))
        XCTAssertFalse(settings.contains("Codex SyncBar 2.0.0"))
        XCTAssertTrue(loginCoordinator.contains("AppVersion.current"))
        XCTAssertFalse(loginCoordinator.contains(#""version": "1.3.2""#))
    }

    func testAppBundleIncludesGeneratedIcon() throws {
        let packageRoot = URL(fileURLWithPath: #filePath)
            .deletingLastPathComponent()
            .deletingLastPathComponent()
            .deletingLastPathComponent()
        let info = try String(contentsOf: packageRoot
            .appendingPathComponent("Resources/Info.plist"), encoding: .utf8)
        let buildScript = try String(contentsOf: packageRoot
            .appendingPathComponent("build-app.sh"), encoding: .utf8)
        let iconURL = packageRoot.appendingPathComponent("Resources/AppIcon.icns")

        XCTAssertTrue(info.contains("<key>CFBundleIconFile</key>"))
        XCTAssertTrue(info.contains("<string>AppIcon</string>"))
        XCTAssertTrue(buildScript.contains("Resources/AppIcon.icns"))
        XCTAssertTrue(FileManager.default.fileExists(atPath: iconURL.path))
        XCTAssertGreaterThan(try Data(contentsOf: iconURL).count, 100_000)
    }

    func testSettingsUsesRetainedForegroundWindowAndSwitchIsImmediate() throws {
        let packageRoot = URL(fileURLWithPath: #filePath)
            .deletingLastPathComponent()
            .deletingLastPathComponent()
            .deletingLastPathComponent()
        let app = try String(contentsOf: packageRoot
            .appendingPathComponent("Sources/CodexSyncBar/CodexSyncBarApp.swift"), encoding: .utf8)
        let delegate = try String(contentsOf: packageRoot
            .appendingPathComponent("Sources/CodexSyncBar/AppDelegate.swift"), encoding: .utf8)
        let settingsWindow = try String(contentsOf: packageRoot
            .appendingPathComponent("Sources/CodexSyncBar/SettingsWindowController.swift"), encoding: .utf8)
        let confirmationWindowURL = packageRoot
            .appendingPathComponent("Sources/CodexSyncBar/SwitchConfirmationWindowController.swift")

        XCTAssertFalse(app.contains("Window(\"Codex SyncBar 설정\""))
        XCTAssertTrue(delegate.contains("private var settingsWindowController"))
        XCTAssertFalse(delegate.contains("switchConfirmationWindowController"))
        XCTAssertFalse(delegate.contains("presentSwitchConfirmation"))
        XCTAssertTrue(settingsWindow.contains("window.level = .floating"))
        XCTAssertTrue(settingsWindow.contains("window.orderFrontRegardless()"))
        XCTAssertTrue(settingsWindow.contains("NSApp.activate(ignoringOtherApps: true)"))
        XCTAssertFalse(FileManager.default.fileExists(atPath: confirmationWindowURL.path))
    }

    func testMenuBarShowsTextOnlyWithoutIcon() throws {
        let packageRoot = URL(fileURLWithPath: #filePath)
            .deletingLastPathComponent()
            .deletingLastPathComponent()
            .deletingLastPathComponent()
        let app = try String(contentsOf: packageRoot
            .appendingPathComponent("Sources/CodexSyncBar/CodexSyncBarApp.swift"), encoding: .utf8)

        XCTAssertTrue(app.contains("Text(model.menuTitle)"))
        XCTAssertTrue(app.contains(".monospacedDigit()"))
        XCTAssertFalse(app.contains("MenuBarLabelRenderer"))
        XCTAssertFalse(app.contains("Image(systemName:"))
        XCTAssertFalse(FileManager.default.fileExists(atPath: packageRoot
            .appendingPathComponent("Sources/CodexSyncBar/MenuBarLabelRenderer.swift").path))
    }

    func testAppStartsModelWithoutWaitingForMenuInteraction() throws {
        let packageRoot = URL(fileURLWithPath: #filePath)
            .deletingLastPathComponent()
            .deletingLastPathComponent()
            .deletingLastPathComponent()
        let app = try String(contentsOf: packageRoot
            .appendingPathComponent("Sources/CodexSyncBar/CodexSyncBarApp.swift"), encoding: .utf8)

        XCTAssertTrue(app.contains("init() {"))
        XCTAssertTrue(app.contains("Task { @MainActor in"))
        XCTAssertTrue(app.contains("await model.start()"))
    }

    func testUsageDisplayPreferencesDefaultToVisibleAndPersistEachItem() throws {
        let suiteName = "CodexSyncBarUsageDisplay-\(UUID().uuidString)"
        let defaults = try XCTUnwrap(UserDefaults(suiteName: suiteName))
        defaults.removePersistentDomain(forName: suiteName)
        defer { defaults.removePersistentDomain(forName: suiteName) }
        let store = UsageDisplayPreferencesStore(defaults: defaults)

        XCTAssertEqual(store.load(), .allVisible)
        XCTAssertEqual(UsageDisplayItem.allCases.count, 4)

        for item in UsageDisplayItem.allCases {
            var expected = UsageDisplayPreferences.allVisible
            expected.setVisible(false, for: item)
            store.save(expected)

            let reloaded = store.load()
            XCTAssertFalse(reloaded.isVisible(item))
            for other in UsageDisplayItem.allCases where other != item {
                XCTAssertTrue(reloaded.isVisible(other))
            }

            store.save(.allVisible)
        }
    }

    func testMenuBarUsagePreferencesPersistZeroOneOrTwoUniqueOrderedItems() throws {
        let suiteName = "CodexSyncBarMenuBarUsage-\(UUID().uuidString)"
        let defaults = try XCTUnwrap(UserDefaults(suiteName: suiteName))
        defaults.removePersistentDomain(forName: suiteName)
        defer { defaults.removePersistentDomain(forName: suiteName) }
        let store = MenuBarUsagePreferencesStore(defaults: defaults)

        XCTAssertEqual(store.load().items, [.codexWeekly, .sparkWeekly])

        let chosen = MenuBarUsagePreferences(items: [.fiveHour, .sparkFiveHour])
        store.save(chosen)
        XCTAssertEqual(store.load().items, [.fiveHour, .sparkFiveHour])

        store.save(MenuBarUsagePreferences(items: [.sparkWeekly]))
        XCTAssertEqual(store.load().items, [.sparkWeekly])

        store.save(MenuBarUsagePreferences(items: []))
        XCTAssertEqual(store.load().items, [])

        defaults.set(
            [UsageDisplayItem.fiveHour.rawValue, "unknown", UsageDisplayItem.fiveHour.rawValue],
            forKey: "menuBarUsage.items.v1")
        let repaired = store.load()
        XCTAssertEqual(repaired.items, [.fiveHour])

        let startingWithNone = MenuBarUsagePreferences(items: [])
        XCTAssertEqual(startingWithNone.settingItemCount(0).items, [])
        XCTAssertEqual(startingWithNone.settingItemCount(1).items, [.codexWeekly])
        XCTAssertEqual(startingWithNone.settingItemCount(2).items, [.codexWeekly, .sparkWeekly])
        XCTAssertEqual(startingWithNone.item(at: 1, fallback: .sparkFiveHour), .sparkFiveHour)

        let startingWithOne = MenuBarUsagePreferences(items: [.fiveHour])
        XCTAssertEqual(startingWithOne.settingItemCount(0).items, [])
        XCTAssertEqual(startingWithOne.settingItemCount(1).items, [.fiveHour])
        XCTAssertEqual(startingWithOne.settingItemCount(2).items, [.fiveHour, .codexWeekly])
        XCTAssertEqual(startingWithOne.item(at: 0, fallback: .sparkFiveHour), .fiveHour)

        XCTAssertEqual(
            MenuBarUsagePreferences(items: [.codexWeekly, .sparkWeekly])
                .replacingItem(at: 0, with: .sparkWeekly).items,
            [.sparkWeekly, .codexWeekly])
    }

    func testUsageDisplaySettingsDriveFourSharedQuotaBarsAndUpToTwoMenuItems() throws {
        let packageRoot = URL(fileURLWithPath: #filePath)
            .deletingLastPathComponent()
            .deletingLastPathComponent()
            .deletingLastPathComponent()
        let popover = try String(contentsOf: packageRoot
            .appendingPathComponent("Sources/CodexSyncBar/Views/PopoverView.swift"), encoding: .utf8)
        let settings = try String(contentsOf: packageRoot
            .appendingPathComponent("Sources/CodexSyncBar/Views/SettingsView.swift"), encoding: .utf8)

        XCTAssertTrue(popover.contains("QuotaUsageRow("))
        XCTAssertTrue(popover.contains("UsageDisplayItem.fiveHour"))
        XCTAssertTrue(popover.contains("UsageDisplayItem.codexWeekly"))
        XCTAssertTrue(popover.contains("UsageDisplayItem.sparkFiveHour"))
        XCTAssertTrue(popover.contains("UsageDisplayItem.sparkWeekly"))
        XCTAssertTrue(popover.contains("window: snapshot?.session"))
        XCTAssertTrue(popover.contains("window: snapshot?.weekly"))
        XCTAssertTrue(popover.contains("window: snapshot?.sparkSession"))
        XCTAssertTrue(popover.contains("window: snapshot?.sparkWeekly"))
        XCTAssertFalse(popover.contains("miniUsage("))
        XCTAssertTrue(settings.contains("SettingsGroupTitle(\"사용량 표시\")"))
        XCTAssertTrue(settings.contains("usage-display-five-hour-toggle"))
        XCTAssertTrue(settings.contains("usage-display-codex-weekly-toggle"))
        XCTAssertTrue(settings.contains("usage-display-spark-five-hour-toggle"))
        XCTAssertTrue(settings.contains("usage-display-spark-weekly-toggle"))
        XCTAssertTrue(settings.contains("SettingsGroupTitle(\"상단 메뉴바\")"))
        XCTAssertTrue(settings.contains("menu-bar-item-count-picker"))
        XCTAssertTrue(settings.contains("setMenuBarUsageItemCount"))
        XCTAssertTrue(settings.contains("item(at: index, fallback: item)"))
        XCTAssertTrue(settings.contains("menu-bar-primary-item-slot"))
        XCTAssertTrue(settings.contains("menu-bar-secondary-item-slot"))
    }

    func testAppVersionUsesBundleShortVersionAndDevelopmentFallback() {
        XCTAssertEqual(
            AppVersion.current(infoDictionary: ["CFBundleShortVersionString": "3.4.5"]),
            "3.4.5")
        XCTAssertEqual(AppVersion.current(infoDictionary: [:]), "development")
        XCTAssertEqual(
            AppVersion.current(infoDictionary: ["CFBundleShortVersionString": "  "]),
            "development")
    }

    func testRemainingPercentIsClamped() {
        XCTAssertEqual(UsageWindow(usedPercent: 18, resetsAt: nil, durationSeconds: nil).remainingPercent, 82)
        XCTAssertEqual(UsageWindow(usedPercent: 120, resetsAt: nil, durationSeconds: nil).remainingPercent, 0)
        XCTAssertEqual(UsageWindow(usedPercent: -5, resetsAt: nil, durationSeconds: nil).remainingPercent, 100)
    }

    func testEmailMaskKeepsDomain() {
        XCTAssertEqual(Formatting.maskedEmail("alice@example.com"), "a••••@example.com")
        XCTAssertEqual(Formatting.maskedEmail("a@openai.com"), "a••@openai.com")
    }

    func testResetDescription() {
        let now = Date(timeIntervalSince1970: 1_000_000)
        XCTAssertEqual(
            Formatting.resetDescription(now.addingTimeInterval(2 * 86_400 + 3 * 3_600), relativeTo: now),
            "2일 3시간 후 초기화")
        XCTAssertEqual(
            Formatting.resetDescription(now.addingTimeInterval(2 * 3_600 + 15 * 60), relativeTo: now),
            "2시간 15분 후 초기화")
    }

    func testResetCreditExpiryDescriptionUsesDaysAbove24Hours() {
        let now = Date(timeIntervalSince1970: 1_000_000)
        XCTAssertEqual(
            Formatting.resetCreditExpiryDescription(
                now.addingTimeInterval(2 * 86_400 + 3 * 3_600 + 59 * 60),
                relativeTo: now),
            "2일 3시간")
    }

    func testResetCreditExpiryDescriptionUsesHoursAndMinutesAt24HoursOrLess() {
        let now = Date(timeIntervalSince1970: 1_000_000)
        XCTAssertEqual(
            Formatting.resetCreditExpiryDescription(now.addingTimeInterval(86_400), relativeTo: now),
            "24시간 0분")
        XCTAssertEqual(
            Formatting.resetCreditExpiryDescription(
                now.addingTimeInterval(23 * 3_600 + 5 * 60),
                relativeTo: now),
            "23시간 5분")
        XCTAssertEqual(
            Formatting.resetCreditExpiryDescription(now.addingTimeInterval(-1), relativeTo: now),
            "만료됨")
    }

    func testCompactResetCreditExpiryDescriptionShowsNextAndRemainingCount() {
        let now = Date(timeIntervalSince1970: 1_000_000)
        let expirations = [
            now.addingTimeInterval(4 * 86_400),
            now.addingTimeInterval(2 * 86_400 + 3 * 3_600),
            now.addingTimeInterval(6 * 86_400),
            now.addingTimeInterval(5 * 86_400),
        ]

        XCTAssertEqual(
            Formatting.compactResetCreditExpiryDescription(expirations, relativeTo: now),
            "다음 만료 2일 3시간 · 외 3회")
        XCTAssertNil(Formatting.compactResetCreditExpiryDescription([], relativeTo: now))
    }

    func testResetCreditsPayloadDecodesAndSortsFractionalISO8601Expirations() throws {
        let data = Data("""
        {
          "available_count": 3,
          "credits": [
            {"expires_at": "2026-07-31T20:04:59.980993Z"},
            {"expires_at": "2026-07-18T00:35:57.659412Z"},
            {"expires_at": "2026-07-26T23:50:30.091959Z"}
          ]
        }
        """.utf8)

        let payload = try JSONDecoder().decode(ResetCreditsPayload.self, from: data)

        XCTAssertEqual(payload.availableCount, 3)
        XCTAssertEqual(payload.expirationDates.count, 3)
        XCTAssertEqual(payload.expirationDates, payload.expirationDates.sorted())
    }

    @MainActor
    func testAppServerParserHandlesBrowserLoginLifecycle() {
        let started = LoginCoordinator.parseAppServerLine("""
        {"id":2,"result":{"type":"chatgpt","loginId":"login-123","authUrl":"https://auth.openai.com/oauth/authorize?state=test"}}
        """)
        XCTAssertEqual(
            started,
            .loginStarted(
                url: URL(string: "https://auth.openai.com/oauth/authorize?state=test")!,
                loginID: "login-123"))

        let completed = LoginCoordinator.parseAppServerLine("""
        {"method":"account/login/completed","params":{"loginId":"login-123","success":true,"error":null}}
        """)
        XCTAssertEqual(
            completed,
            .loginCompleted(loginID: "login-123", success: true, error: nil))

        let updated = LoginCoordinator.parseAppServerLine("""
        {"method":"account/updated","params":{"authMode":"chatgpt","planType":"pro"}}
        """)
        XCTAssertEqual(updated, .accountUpdated(authMode: "chatgpt"))

        let accountReady = LoginCoordinator.parseAppServerLine("""
        {"id":3,"result":{"account":{"type":"chatgpt","email":"test@example.com","planType":"pro"},"requiresOpenaiAuth":true}}
        """)
        XCTAssertEqual(accountReady, .accountStateReady)

        let validated = LoginCoordinator.parseAppServerLine("""
        {"id":4,"result":{"rateLimits":{"primary":null,"secondary":null}}}
        """)
        XCTAssertEqual(validated, .accountValidated)
    }

    @MainActor
    func testAccountUpdateWithoutChatGPTModeDoesNotLookReady() {
        let updated = LoginCoordinator.parseAppServerLine("""
        {"method":"account/updated","params":{"authMode":null,"planType":null}}
        """)
        XCTAssertEqual(updated, .accountUpdated(authMode: nil))

        let missingAccount = LoginCoordinator.parseAppServerLine("""
        {"id":3,"result":{"account":null,"requiresOpenaiAuth":true}}
        """)
        XCTAssertEqual(missingAccount, .failed("새 Codex 계정 상태를 확인하지 못했습니다."))
    }

    func testLoginValidationWaitsForBothReadinessSignalsAndRunsOnce() {
        XCTAssertFalse(LoginCoordinator.shouldRequestAccountRead(
            loginCompleted: true,
            accountUpdated: false,
            alreadyRequested: false))
        XCTAssertFalse(LoginCoordinator.shouldRequestAccountRead(
            loginCompleted: false,
            accountUpdated: true,
            alreadyRequested: false))
        XCTAssertTrue(LoginCoordinator.shouldRequestAccountRead(
            loginCompleted: true,
            accountUpdated: true,
            alreadyRequested: false))
        XCTAssertFalse(LoginCoordinator.shouldRequestAccountRead(
            loginCompleted: true,
            accountUpdated: true,
            alreadyRequested: true))
    }

    @MainActor
    func testAppServerParserRejectsUnexpectedAuthenticationHost() {
        let event = LoginCoordinator.parseAppServerLine("""
        {"id":2,"result":{"type":"chatgpt","loginId":"login-123","authUrl":"https://example.com/oauth/authorize"}}
        """)
        XCTAssertEqual(event, .failed("Codex가 올바른 로그인 주소를 반환하지 않았습니다."))
    }

    @MainActor
    func testLoginRateLimitMessageIsActionable() {
        let output = "Error logging in with device code: device code request failed with status 429 Too Many Requests"
        XCTAssertEqual(
            LoginCoordinator.loginFailureMessage(output),
            "로그인 요청이 너무 많습니다. 잠시 후 다시 시도해 주세요.")
    }

    @MainActor
    func testChromiumProfilesUseStableDistinctDirectories() {
        let root = URL(fileURLWithPath: "/tmp/Codex SyncBar Tests", isDirectory: true)
        let first = ChromiumBrowserController.profileDirectory(profileID: 1, applicationSupportURL: root)
        let second = ChromiumBrowserController.profileDirectory(profileID: 2, applicationSupportURL: root)

        XCTAssertNotEqual(first, second)
        XCTAssertEqual(
            first,
            ChromiumBrowserController.profileDirectory(profileID: 1, applicationSupportURL: root))
        XCTAssertTrue(first.path.hasSuffix("ChromeProfiles/profile-1"))
        XCTAssertTrue(second.path.hasSuffix("ChromeProfiles/profile-2"))
    }

    @MainActor
    func testChromiumLaunchUsesPersistentProfileWithoutAutomationFlags() {
        let directory = URL(fileURLWithPath: "/tmp/Codex SyncBar/Profile 2", isDirectory: true)
        let url = URL(string: "https://auth.openai.com/oauth/authorize?state=test")!
        let arguments = ChromiumBrowserController.launchArguments(
            userDataDirectory: directory,
            authenticationURL: url)

        XCTAssertTrue(arguments.contains("--user-data-dir=\(directory.path)"))
        XCTAssertTrue(arguments.contains("--profile-directory=Default"))
        XCTAssertTrue(arguments.contains("--new-window"))
        XCTAssertFalse(arguments.contains(where: { $0 == "--incognito" || $0 == "--guest" }))
        XCTAssertFalse(arguments.contains(where: { $0.hasPrefix("--app=") }))
        XCTAssertFalse(arguments.contains(where: { $0.hasPrefix("--remote-debugging") }))
        XCTAssertTrue(ChromiumBrowserController.processCommand(
            "Google Chrome --user-data-dir=\(directory.path) --new-window",
            usesUserDataDirectory: directory))
        XCTAssertFalse(ChromiumBrowserController.processCommand(
            "Google Chrome --user-data-dir=\(directory.path)0 --new-window",
            usesUserDataDirectory: directory))
    }

    @MainActor
    func testBrowserResetArchivesSessionInsteadOfDeletingIt() async throws {
        let home = FileManager.default.temporaryDirectory
            .appendingPathComponent("CodexSyncBarResetTests-\(UUID().uuidString)", isDirectory: true)
        defer { try? FileManager.default.removeItem(at: home) }

        let controller = ChromiumBrowserController(applicationSupportURL: home)
        let original = controller.profileDirectory(for: 2)
        try FileManager.default.createDirectory(at: original, withIntermediateDirectories: true)
        let marker = original.appendingPathComponent("session-marker")
        try Data("preserved".utf8).write(to: marker)

        try await controller.resetProfile(for: 2)

        XCTAssertTrue(FileManager.default.fileExists(atPath: original.path))
        let backups = home.appendingPathComponent("ChromeProfileBackups", isDirectory: true)
        let archived = try FileManager.default.contentsOfDirectory(
            at: backups,
            includingPropertiesForKeys: nil)
        XCTAssertEqual(archived.count, 1)
        XCTAssertTrue(FileManager.default.fileExists(
            atPath: archived[0].appendingPathComponent("session-marker").path))
    }

    @MainActor
    func testBrowserProfileRejectsSymlinkedStorageRoot() async throws {
        let home = FileManager.default.temporaryDirectory
            .appendingPathComponent("CodexSyncBarSymlinkTests-\(UUID().uuidString)", isDirectory: true)
        defer { try? FileManager.default.removeItem(at: home) }

        let target = home.appendingPathComponent("outside", isDirectory: true)
        let profiles = home.appendingPathComponent("ChromeProfiles", isDirectory: true)
        try FileManager.default.createDirectory(at: target, withIntermediateDirectories: true)
        try FileManager.default.createSymbolicLink(at: profiles, withDestinationURL: target)
        let controller = ChromiumBrowserController(applicationSupportURL: home)

        do {
            try await controller.resetProfile(for: 1)
            XCTFail("A symlinked ChromeProfiles root must be rejected")
        } catch let error as AppError {
            guard case .processFailed = error else {
                return XCTFail("Unexpected error: \(error)")
            }
        }
    }

    @MainActor
    func testBrowserProfileSwapMovesAccountSessionsTogether() async throws {
        let home = FileManager.default.temporaryDirectory
            .appendingPathComponent("CodexSyncBarBrowserSwapTests-\(UUID().uuidString)", isDirectory: true)
        defer { try? FileManager.default.removeItem(at: home) }

        let controller = ChromiumBrowserController(applicationSupportURL: home)
        let first = controller.profileDirectory(for: 1)
        let second = controller.profileDirectory(for: 2)
        try FileManager.default.createDirectory(at: first, withIntermediateDirectories: true)
        try FileManager.default.createDirectory(at: second, withIntermediateDirectories: true)
        try Data("account-a-session".utf8).write(to: first.appendingPathComponent("session-marker"))
        try Data("account-b-session".utf8).write(to: second.appendingPathComponent("session-marker"))
        try controller.prepareSwapMarkers(firstToken: "old-marker-a", secondToken: "old-marker-b")
        try controller.prepareSwapMarkers(firstToken: "marker-a", secondToken: "marker-b")
        XCTAssertEqual(
            try controller.swapMarkerArrangement(firstToken: "marker-a", secondToken: "marker-b"),
            .original)

        try await controller.swapProfiles()

        XCTAssertEqual(
            try controller.swapMarkerArrangement(firstToken: "marker-a", secondToken: "marker-b"),
            .swapped)

        XCTAssertEqual(
            try String(contentsOf: first.appendingPathComponent("session-marker"), encoding: .utf8),
            "account-b-session")
        XCTAssertEqual(
            try String(contentsOf: second.appendingPathComponent("session-marker"), encoding: .utf8),
            "account-a-session")
        let firstMode = try FileManager.default.attributesOfItem(atPath: first.path)[.posixPermissions] as? NSNumber
        let secondMode = try FileManager.default.attributesOfItem(atPath: second.path)[.posixPermissions] as? NSNumber
        XCTAssertEqual(firstMode?.intValue, 0o700)
        XCTAssertEqual(secondMode?.intValue, 0o700)
    }

    @MainActor
    func testBrowserRecoversCrashAfterFirstSwapRename() async throws {
        let home = FileManager.default.temporaryDirectory
            .appendingPathComponent("CodexSyncBarBrowserSwapRecoveryOne-\(UUID().uuidString)", isDirectory: true)
        defer { try? FileManager.default.removeItem(at: home) }

        let controller = ChromiumBrowserController(applicationSupportURL: home)
        let first = controller.profileDirectory(for: 1)
        let second = controller.profileDirectory(for: 2)
        try FileManager.default.createDirectory(at: first, withIntermediateDirectories: true)
        try FileManager.default.createDirectory(at: second, withIntermediateDirectories: true)
        try Data("account-a-session".utf8).write(to: first.appendingPathComponent("session-marker"))
        try Data("account-b-session".utf8).write(to: second.appendingPathComponent("session-marker"))
        let temporary = first.deletingLastPathComponent()
            .appendingPathComponent(".profile-swap-interrupted", isDirectory: true)
        try FileManager.default.moveItem(at: first, to: temporary)

        try await controller.clearProfile(for: 2)

        XCTAssertEqual(
            try String(contentsOf: first.appendingPathComponent("session-marker"), encoding: .utf8),
            "account-a-session")
        XCTAssertFalse(FileManager.default.fileExists(atPath: temporary.path))
    }

    @MainActor
    func testBrowserRecoversCrashAfterSecondSwapRename() async throws {
        let home = FileManager.default.temporaryDirectory
            .appendingPathComponent("CodexSyncBarBrowserSwapRecoveryTwo-\(UUID().uuidString)", isDirectory: true)
        defer { try? FileManager.default.removeItem(at: home) }

        let controller = ChromiumBrowserController(applicationSupportURL: home)
        let first = controller.profileDirectory(for: 1)
        let second = controller.profileDirectory(for: 2)
        try FileManager.default.createDirectory(at: first, withIntermediateDirectories: true)
        try FileManager.default.createDirectory(at: second, withIntermediateDirectories: true)
        try Data("account-a-session".utf8).write(to: first.appendingPathComponent("session-marker"))
        try Data("account-b-session".utf8).write(to: second.appendingPathComponent("session-marker"))
        let temporary = first.deletingLastPathComponent()
            .appendingPathComponent(".profile-swap-interrupted", isDirectory: true)
        try FileManager.default.moveItem(at: first, to: temporary)
        try FileManager.default.moveItem(at: second, to: first)

        try await controller.clearProfile(for: 2)

        XCTAssertEqual(
            try String(contentsOf: first.appendingPathComponent("session-marker"), encoding: .utf8),
            "account-a-session")
        XCTAssertFalse(FileManager.default.fileExists(atPath: temporary.path))
    }

    @MainActor
    func testBrowserLogoutClearsSessionAndRecreatesSecureProfile() async throws {
        let home = FileManager.default.temporaryDirectory
            .appendingPathComponent("CodexSyncBarBrowserLogoutTests-\(UUID().uuidString)", isDirectory: true)
        defer { try? FileManager.default.removeItem(at: home) }

        let controller = ChromiumBrowserController(applicationSupportURL: home)
        let profile = controller.profileDirectory(for: 2)
        try FileManager.default.createDirectory(at: profile, withIntermediateDirectories: true)
        let marker = profile.appendingPathComponent("Cookies")
        try Data("signed-in-session".utf8).write(to: marker)

        try await controller.clearProfile(for: 2)

        XCTAssertTrue(FileManager.default.fileExists(atPath: profile.path))
        XCTAssertFalse(FileManager.default.fileExists(atPath: marker.path))
        let mode = try FileManager.default.attributesOfItem(atPath: profile.path)[.posixPermissions] as? NSNumber
        XCTAssertEqual(mode?.intValue, 0o700)
    }

    func testAuthMaintenanceParserDistinguishesRefreshAndPartialSync() {
        let refreshed = SwitchService.parseAuthMaintenance(
            output: "profile=1 action=refreshed expires_at=1780000000 remaining_seconds=500000 local=ok ml=ok rogally=ok laptop=ok result=ok",
            exitStatus: 0)
        XCTAssertTrue(refreshed.didRefresh)
        XCTAssertFalse(refreshed.didSync)
        XCTAssertFalse(refreshed.isPartial)

        let partial = SwitchService.parseAuthMaintenance(
            output: "profile=2 action=synced expires_at=1780000000 remaining_seconds=500000 local=ok ml=ok rogally=pending laptop=ok result=partial",
            exitStatus: 2)
        XCTAssertFalse(partial.didRefresh)
        XCTAssertTrue(partial.didSync)
        XCTAssertTrue(partial.isPartial)
    }

    func testAuthMaintenanceParserDoesNotTreatSyncedZeroAsAChange() {
        let noop = SwitchService.parseAuthMaintenance(
            output: "profile=3 action=noop synced=0 pending=0 result=ok",
            exitStatus: 0)
        XCTAssertFalse(noop.didSync)

        let changed = SwitchService.parseAuthMaintenance(
            output: "profile=3 action=noop synced=4 pending=0 result=ok",
            exitStatus: 0)
        XCTAssertTrue(changed.didSync)
    }

    func testAuthMaintenanceParserReportsDeferredActiveClientWithoutAttention() {
        let deferred = SwitchService.parseAuthMaintenance(
            output: "profile=1 action=deferred-client-running synced=0 pending=0 result=ok",
            exitStatus: 0)

        XCTAssertTrue(deferred.didDefer)
        XCTAssertFalse(deferred.didRefresh)
        XCTAssertFalse(deferred.didSync)
        XCTAssertFalse(deferred.isPartial)
    }

    func testAuthStoreReadsCanonicalProfileInsteadOfActiveCopy() async throws {
        let home = FileManager.default.temporaryDirectory
            .appendingPathComponent("CodexSyncBarTests-\(UUID().uuidString)", isDirectory: true)
        defer { try? FileManager.default.removeItem(at: home) }

        let profileDirectory = home.appendingPathComponent(".local/share/gpt-switch/profiles", isDirectory: true)
        let codexDirectory = home.appendingPathComponent(".codex", isDirectory: true)
        try FileManager.default.createDirectory(at: profileDirectory, withIntermediateDirectories: true)
        try FileManager.default.createDirectory(at: codexDirectory, withIntermediateDirectories: true)

        func auth(accessToken: String) throws -> Data {
            try JSONSerialization.data(withJSONObject: [
                "auth_mode": "chatgpt",
                "tokens": [
                    "id_token": "header.payload.signature",
                    "access_token": accessToken,
                    "refresh_token": "refresh",
                    "account_id": "same-account",
                ],
            ])
        }

        try auth(accessToken: "canonical-access").write(
            to: profileDirectory.appendingPathComponent("1.auth.json"))
        try auth(accessToken: "active-copy").write(
            to: codexDirectory.appendingPathComponent("auth.json"))

        let credentials = try await AuthStore(home: home).credentials(for: 1)
        XCTAssertEqual(credentials.accessToken, "canonical-access")
        XCTAssertTrue(credentials.isActiveOnMac)
    }

    func testAuthImportPreservesOldAccountUntilReplacementIsExplicit() async throws {
        let home = FileManager.default.temporaryDirectory
            .appendingPathComponent("CodexSyncBarImportTests-\(UUID().uuidString)", isDirectory: true)
        defer { try? FileManager.default.removeItem(at: home) }

        let profileDirectory = home.appendingPathComponent(
            ".local/share/gpt-switch/profiles",
            isDirectory: true)
        let loginDirectory = home.appendingPathComponent("login", isDirectory: true)
        try FileManager.default.createDirectory(at: profileDirectory, withIntermediateDirectories: true)
        try FileManager.default.createDirectory(at: loginDirectory, withIntermediateDirectories: true)

        func auth(accountID: String, accessToken: String) throws -> Data {
            try JSONSerialization.data(withJSONObject: [
                "auth_mode": "chatgpt",
                "tokens": [
                    "id_token": "header.payload.signature",
                    "access_token": accessToken,
                    "refresh_token": "refresh-\(accountID)",
                    "account_id": accountID,
                ],
            ])
        }

        let destination = profileDirectory.appendingPathComponent("2.auth.json")
        let source = loginDirectory.appendingPathComponent("auth.json")
        let helper = home.appendingPathComponent("fake-gpt-switch")
        let helperScript = """
        #!/bin/bash
        set -euo pipefail
        test "$1" = "import-login"
        profile="$2"
        destination="$GPT_SWITCH_STATE_ROOT/profiles/$profile.auth.json"
        mkdir -p "$(dirname "$destination")"
        temporary=$(mktemp "$(dirname "$destination")/.test-import.XXXXXX")
        cat >"$temporary"
        chmod 600 "$temporary"
        mv -f "$temporary" "$destination"
        """
        try Data(helperScript.utf8).write(to: helper)
        try FileManager.default.setAttributes([.posixPermissions: 0o700], ofItemAtPath: helper.path)
        try auth(accountID: "old-account", accessToken: "old-access").write(to: destination)
        try auth(accountID: "new-account", accessToken: "new-access").write(to: source)
        let store = AuthStore(home: home, switchExecutable: helper)

        do {
            try await store.importLoggedInAuth(from: source, for: 2)
            XCTFail("A different account must require explicit replacement")
        } catch let error as AppError {
            guard case .loginRequired = error else {
                return XCTFail("Unexpected error: \(error)")
            }
        }
        let preserved = try await store.credentials(for: 2)
        XCTAssertEqual(preserved.accountID, "old-account")
        XCTAssertEqual(preserved.accessToken, "old-access")

        try await store.importLoggedInAuth(from: source, for: 2, replaceExisting: true)
        let replaced = try await store.credentials(for: 2)
        XCTAssertEqual(replaced.accountID, "new-account")
        XCTAssertEqual(replaced.accessToken, "new-access")

    }

    func testAuthImportRejectsAccountAlreadyAssignedToOtherProfile() async throws {
        let home = FileManager.default.temporaryDirectory
            .appendingPathComponent("CodexSyncBarDuplicateImportTests-\(UUID().uuidString)", isDirectory: true)
        defer { try? FileManager.default.removeItem(at: home) }

        let profileDirectory = home.appendingPathComponent(
            ".local/share/gpt-switch/profiles",
            isDirectory: true)
        let loginDirectory = home.appendingPathComponent("login", isDirectory: true)
        try FileManager.default.createDirectory(at: profileDirectory, withIntermediateDirectories: true)
        try FileManager.default.createDirectory(at: loginDirectory, withIntermediateDirectories: true)

        func auth(accountID: String, accessToken: String) throws -> Data {
            try JSONSerialization.data(withJSONObject: [
                "auth_mode": "chatgpt",
                "tokens": [
                    "id_token": "header.payload.signature",
                    "access_token": accessToken,
                    "refresh_token": "refresh-\(accountID)",
                    "account_id": accountID,
                ],
            ])
        }

        let profileOne = profileDirectory.appendingPathComponent("1.auth.json")
        let profileTwo = profileDirectory.appendingPathComponent("2.auth.json")
        let profileThree = profileDirectory.appendingPathComponent("3.auth.json")
        let source = loginDirectory.appendingPathComponent("auth.json")
        let helper = home.appendingPathComponent("fake-gpt-switch")
        let helperScript = """
        #!/bin/bash
        set -euo pipefail
        profile="$2"
        destination="$GPT_SWITCH_STATE_ROOT/profiles/$profile.auth.json"
        cat >"$destination"
        chmod 600 "$destination"
        """
        try Data(helperScript.utf8).write(to: helper)
        try FileManager.default.setAttributes([.posixPermissions: 0o700], ofItemAtPath: helper.path)
        try auth(accountID: "account-c", accessToken: "access-c").write(to: profileOne)
        try auth(accountID: "account-b", accessToken: "access-b").write(to: profileTwo)
        try auth(accountID: "account-a", accessToken: "access-a").write(to: profileThree)
        try auth(accountID: "account-a", accessToken: "new-access-a").write(to: source)

        let store = AuthStore(home: home, switchExecutable: helper)
        do {
            try await store.importLoggedInAuth(
                from: source,
                for: 2,
                replaceExisting: true)
            XCTFail("The same account must not be assigned to both profiles")
        } catch let error as AppError {
            guard case .loginRequired = error else {
                return XCTFail("Unexpected error: \(error)")
            }
        }

        let preserved = try await store.credentials(for: 2)
        XCTAssertEqual(preserved.accountID, "account-b")
        XCTAssertEqual(preserved.accessToken, "access-b")
    }

    func testControllerRejectsDuplicateAccountBeforeWriting() throws {
        let home = FileManager.default.temporaryDirectory
            .appendingPathComponent("GptSwitchDuplicateImportTests-\(UUID().uuidString)", isDirectory: true)
        defer { try? FileManager.default.removeItem(at: home) }

        let stateRoot = home.appendingPathComponent("state", isDirectory: true)
        let profileDirectory = stateRoot.appendingPathComponent("profiles", isDirectory: true)
        let codexHome = home.appendingPathComponent("codex", isDirectory: true)
        let loginDirectory = home.appendingPathComponent("login", isDirectory: true)
        try FileManager.default.createDirectory(at: profileDirectory, withIntermediateDirectories: true)
        try FileManager.default.createDirectory(at: codexHome, withIntermediateDirectories: true)
        try FileManager.default.createDirectory(at: loginDirectory, withIntermediateDirectories: true)

        func writeAuth(_ url: URL, accountID: String, accessToken: String) throws {
            let data = try JSONSerialization.data(withJSONObject: [
                "auth_mode": "chatgpt",
                "tokens": [
                    "id_token": "header.payload.signature",
                    "access_token": accessToken,
                    "refresh_token": "refresh-\(accountID)",
                    "account_id": accountID,
                ],
            ])
            try data.write(to: url)
            try FileManager.default.setAttributes([.posixPermissions: 0o600], ofItemAtPath: url.path)
        }

        let profileOne = profileDirectory.appendingPathComponent("1.auth.json")
        let profileTwo = profileDirectory.appendingPathComponent("2.auth.json")
        let source = loginDirectory.appendingPathComponent("auth.json")
        try writeAuth(profileOne, accountID: "account-a", accessToken: "access-a")
        try writeAuth(profileTwo, accountID: "account-b", accessToken: "access-b")
        try writeAuth(source, accountID: "account-a", accessToken: "new-access-a")

        let metadataHelper = home.appendingPathComponent("inspect-auth.sh")
        let metadataScript = """
        #!/bin/bash
        set -euo pipefail
        test "$1" = "inspect"
        account=$(jq -r '.tokens.account_id' "$2")
        access=$(jq -r '.tokens.access_token' "$2")
        printf '{"accountFingerprint":"%s","accessFingerprint":"%s","refreshFingerprint":"refresh","remainingSeconds":3600,"expiresAt":4102444800}\n' "$account" "$access"
        """
        try Data(metadataScript.utf8).write(to: metadataHelper)
        try FileManager.default.setAttributes([.posixPermissions: 0o700], ofItemAtPath: metadataHelper.path)

        let packageRoot = URL(fileURLWithPath: #filePath)
            .deletingLastPathComponent()
            .deletingLastPathComponent()
            .deletingLastPathComponent()
        let controller = packageRoot.appendingPathComponent("Support/gpt-switch")
        let input = try FileHandle(forReadingFrom: source)
        defer { try? input.close() }
        let staleControllerLock = stateRoot.appendingPathComponent(".controller-lock", isDirectory: true)
        try FileManager.default.createDirectory(at: staleControllerLock, withIntermediateDirectories: false)
        try FileManager.default.setAttributes(
            [.posixPermissions: 0o700],
            ofItemAtPath: staleControllerLock.path)
        let staleOwner = staleControllerLock.appendingPathComponent("owner")
        try Data("pid=2147483647\n".utf8).write(to: staleOwner)
        try FileManager.default.setAttributes([.posixPermissions: 0o600], ofItemAtPath: staleOwner.path)
        let output = Pipe()
        let process = Process()
        process.executableURL = URL(fileURLWithPath: "/bin/bash")
        process.arguments = [controller.path, "import-login", "2", "--replace-account"]
        var environment = ProcessInfo.processInfo.environment
        environment["HOME"] = home.path
        environment["GPT_SWITCH_STATE_ROOT"] = stateRoot.path
        environment["CODEX_HOME"] = codexHome.path
        environment["GPT_SWITCH_NODE_BIN"] = "/bin/bash"
        environment["GPT_SWITCH_REFRESH_HELPER"] = metadataHelper.path
        environment["PATH"] = "/opt/homebrew/bin:/usr/local/bin:/usr/bin:/bin:/usr/sbin:/sbin"
        process.environment = environment
        process.standardInput = input
        process.standardOutput = output
        process.standardError = output

        try process.run()
        let result = output.fileHandleForReading.readDataToEndOfFile()
        process.waitUntilExit()
        let message = String(decoding: result, as: UTF8.self)
        XCTAssertNotEqual(process.terminationStatus, 0)
        XCTAssertTrue(message.contains("already assigned to profile 1"), message)

        let preserved = try JSONSerialization.jsonObject(with: Data(contentsOf: profileTwo)) as? [String: Any]
        let tokens = preserved?["tokens"] as? [String: Any]
        XCTAssertEqual(tokens?["account_id"] as? String, "account-b")
        XCTAssertEqual(tokens?["access_token"] as? String, "access-b")
    }

    func testControllerNodeSwapLogoutAndMissingProfileReinstall() throws {
        let home = FileManager.default.temporaryDirectory
            .appendingPathComponent("GptSwitchProfileManagementTests-\(UUID().uuidString)", isDirectory: true)
        defer { try? FileManager.default.removeItem(at: home) }

        let stateRoot = home.appendingPathComponent("state", isDirectory: true)
        let profiles = stateRoot.appendingPathComponent("profiles", isDirectory: true)
        let codexHome = home.appendingPathComponent("codex", isDirectory: true)
        try FileManager.default.createDirectory(at: profiles, withIntermediateDirectories: true)
        try FileManager.default.createDirectory(at: codexHome, withIntermediateDirectories: true)

        func auth(account: String, access: String, refresh: String) throws -> Data {
            try JSONSerialization.data(withJSONObject: [
                "auth_mode": "chatgpt",
                "tokens": [
                    "id_token": "header.payload.signature",
                    "access_token": access,
                    "refresh_token": refresh,
                    "account_id": account,
                ],
            ])
        }

        func writeSecure(_ data: Data, to url: URL) throws {
            try data.write(to: url)
            try FileManager.default.setAttributes([.posixPermissions: 0o600], ofItemAtPath: url.path)
        }

        let packageRoot = URL(fileURLWithPath: #filePath)
            .deletingLastPathComponent()
            .deletingLastPathComponent()
            .deletingLastPathComponent()
        let helper = packageRoot.appendingPathComponent("Support/gpt-switch")

        func run(
            _ arguments: [String],
            input: Data? = nil,
            environmentOverrides: [String: String] = [:]) throws -> (Int32, String)
        {
            let process = Process()
            let output = Pipe()
            let inputPipe = input.map { _ in Pipe() }
            process.executableURL = URL(fileURLWithPath: "/bin/bash")
            process.arguments = [helper.path] + arguments
            var environment = ProcessInfo.processInfo.environment
            environment["HOME"] = home.path
            environment["GPT_SWITCH_STATE_ROOT"] = stateRoot.path
            environment["CODEX_HOME"] = codexHome.path
            environment["PATH"] = "/opt/homebrew/bin:/usr/local/bin:/usr/bin:/bin:/usr/sbin:/sbin"
            for (key, value) in environmentOverrides {
                environment[key] = value
            }
            process.environment = environment
            process.standardOutput = output
            process.standardError = output
            process.standardInput = inputPipe ?? FileHandle.nullDevice
            try process.run()
            if let input, let inputPipe {
                try inputPipe.fileHandleForWriting.write(contentsOf: input)
                try inputPipe.fileHandleForWriting.close()
            }
            let data = output.fileHandleForReading.readDataToEndOfFile()
            process.waitUntilExit()
            return (process.terminationStatus, String(decoding: data, as: UTF8.self))
        }

        func field(_ name: String, in output: String) -> String? {
            output.split(whereSeparator: \ .isWhitespace)
                .map(String.init)
                .first(where: { $0.hasPrefix("\(name)=") })?
                .split(separator: "=", maxSplits: 1)
                .last
                .map(String.init)
        }

        let first = profiles.appendingPathComponent("1.auth.json")
        let second = profiles.appendingPathComponent("2.auth.json")
        let active = codexHome.appendingPathComponent("auth.json")
        let current = stateRoot.appendingPathComponent("current")
        let accountA = try auth(account: "account-a", access: "access-a", refresh: "refresh-a")
        let accountB = try auth(account: "account-b", access: "access-b", refresh: "refresh-b")
        try writeSecure(accountA, to: first)
        try writeSecure(accountB, to: second)
        try writeSecure(accountA, to: active)
        try writeSecure(Data("1\n".utf8), to: current)

        let before = try run(["__node", "profile-map"])
        XCTAssertEqual(before.0, 0, before.1)
        let firstFingerprint = try XCTUnwrap(field("profile1_fp", in: before.1))
        let firstAccess = try XCTUnwrap(field("profile1_access", in: before.1))
        let secondFingerprint = try XCTUnwrap(field("profile2_fp", in: before.1))
        let secondAccess = try XCTUnwrap(field("profile2_access", in: before.1))

        let staleNodeLock = stateRoot.appendingPathComponent(".lock", isDirectory: true)
        try FileManager.default.createDirectory(at: staleNodeLock, withIntermediateDirectories: false)
        try FileManager.default.setAttributes(
            [.posixPermissions: 0o700],
            ofItemAtPath: staleNodeLock.path)
        try writeSecure(Data("pid=2147483647\n".utf8), to: staleNodeLock.appendingPathComponent("owner"))

        let swapped = try run(["__node", "swap-profiles"])
        XCTAssertEqual(swapped.0, 0, swapped.1)
        XCTAssertEqual((try JSONDecoder().decode(CodexAuthFile.self, from: Data(contentsOf: first))).tokens.accountID, "account-b")
        XCTAssertEqual((try JSONDecoder().decode(CodexAuthFile.self, from: Data(contentsOf: second))).tokens.accountID, "account-a")
        XCTAssertEqual((try JSONDecoder().decode(CodexAuthFile.self, from: Data(contentsOf: active))).tokens.accountID, "account-a")
        XCTAssertEqual(try String(contentsOf: current, encoding: .utf8), "2\n")

        let restoredSwap = try run(["__node", "swap-profiles"])
        XCTAssertEqual(restoredSwap.0, 0, restoredSwap.1)
        XCTAssertEqual(try String(contentsOf: current, encoding: .utf8), "1\n")

        let operation = "test_logout_2"
        let preflight = try run(["__node", "logout-preflight", "2", "1"])
        XCTAssertEqual(preflight.0, 0, preflight.1)
        let injectedFailure = try run(
            ["__node", "logout-stage", "injected_failure", "2", "1"],
            environmentOverrides: ["GPT_SWITCH_TEST_LOGOUT_STAGE_FAIL_AFTER_REMOVE": "1"])
        XCTAssertNotEqual(injectedFailure.0, 0)
        XCTAssertTrue(FileManager.default.fileExists(atPath: second.path))
        XCTAssertFalse(FileManager.default.fileExists(
            atPath: stateRoot.appendingPathComponent("logout-transactions/injected_failure").path))

        let staged = try run(["__node", "logout-stage", operation, "2", "1"])
        XCTAssertEqual(staged.0, 0, staged.1)
        XCTAssertFalse(FileManager.default.fileExists(atPath: second.path))
        let verified = try run(["__node", "logout-verify", operation, "2", "1"])
        XCTAssertEqual(verified.0, 0, verified.1)
        let rollback = try run(["__node", "logout-restore", operation, "2"])
        XCTAssertEqual(rollback.0, 0, rollback.1)
        XCTAssertEqual((try JSONDecoder().decode(CodexAuthFile.self, from: Data(contentsOf: second))).tokens.accountID, "account-b")

        XCTAssertEqual(try run(["__node", "logout-stage", operation, "2", "1"]).0, 0)
        XCTAssertEqual(try run(["__node", "logout-commit", operation, "2", "1"]).0, 0)
        XCTAssertFalse(FileManager.default.fileExists(atPath: second.path))
        XCTAssertEqual((try JSONDecoder().decode(CodexAuthFile.self, from: Data(contentsOf: active))).tokens.accountID, "account-a")
        XCTAssertEqual(try String(contentsOf: current, encoding: .utf8), "1\n")

        let accessOnlyB = try auth(account: "account-b", access: "access-b", refresh: "")
        let reinstalled = try run(
            ["__node", "install-access", "2", secondFingerprint, secondAccess],
            input: accessOnlyB)
        XCTAssertEqual(reinstalled.0, 0, reinstalled.1)
        let reinstalledAuth = try JSONDecoder().decode(CodexAuthFile.self, from: Data(contentsOf: second))
        XCTAssertEqual(reinstalledAuth.tokens.accountID, "account-b")
        XCTAssertTrue(reinstalledAuth.tokens.refreshToken.isEmpty)
        XCTAssertEqual(field("profile1_fp", in: try run(["__node", "profile-map"]).1), firstFingerprint)
        XCTAssertEqual(field("profile1_access", in: try run(["__node", "profile-map"]).1), firstAccess)

        let recoveryJournal = stateRoot.appendingPathComponent(".swap-profiles.recovery", isDirectory: true)
        let staleBuilding = stateRoot.appendingPathComponent(".swap-building.stale", isDirectory: true)
        let preparedBuilding = stateRoot.appendingPathComponent(".swap-building.prepared", isDirectory: true)
        try FileManager.default.createDirectory(at: staleBuilding, withIntermediateDirectories: false)
        try FileManager.default.setAttributes([.posixPermissions: 0o700], ofItemAtPath: staleBuilding.path)
        try writeSecure(
            Data("state=building\npid=2147483647\n".utf8),
            to: staleBuilding.appendingPathComponent("manifest"))
        try writeSecure(accountA, to: staleBuilding.appendingPathComponent("credential-copy"))
        try FileManager.default.createDirectory(at: preparedBuilding, withIntermediateDirectories: false)
        try FileManager.default.setAttributes([.posixPermissions: 0o700], ofItemAtPath: preparedBuilding.path)
        try writeSecure(
            Data("state=prepared\npid=2147483647\n".utf8),
            to: preparedBuilding.appendingPathComponent("manifest"))
        try writeSecure(accountA, to: preparedBuilding.appendingPathComponent("1.auth.json"))
        try writeSecure(accountB, to: preparedBuilding.appendingPathComponent("2.auth.json"))
        try FileManager.default.createDirectory(at: recoveryJournal, withIntermediateDirectories: false)
        try FileManager.default.setAttributes([.posixPermissions: 0o700], ofItemAtPath: recoveryJournal.path)
        try writeSecure(Data(contentsOf: first), to: recoveryJournal.appendingPathComponent("1.auth.json"))
        try writeSecure(Data(contentsOf: second), to: recoveryJournal.appendingPathComponent("2.auth.json"))
        try writeSecure(Data(contentsOf: current), to: recoveryJournal.appendingPathComponent("current"))
        try writeSecure(
            Data("state=prepared\npid=2147483647\n".utf8),
            to: recoveryJournal.appendingPathComponent("manifest"))
        try writeSecure(accountB, to: first)
        try writeSecure(accountB, to: second)
        try writeSecure(Data("2\n".utf8), to: current)

        let recovered = try run(["__node", "profile-map"])
        XCTAssertEqual(recovered.0, 0, recovered.1)
        XCTAssertEqual((try JSONDecoder().decode(CodexAuthFile.self, from: Data(contentsOf: first))).tokens.accountID, "account-a")
        XCTAssertEqual((try JSONDecoder().decode(CodexAuthFile.self, from: Data(contentsOf: second))).tokens.accountID, "account-b")
        XCTAssertEqual(try String(contentsOf: current, encoding: .utf8), "1\n")
        XCTAssertFalse(FileManager.default.fileExists(atPath: recoveryJournal.path))
        XCTAssertFalse(FileManager.default.fileExists(atPath: staleBuilding.path))
        XCTAssertFalse(FileManager.default.fileExists(atPath: preparedBuilding.path))

        XCTAssertEqual(try run(["__node", "logout-stage", "cleanup_retry", "2", "1"]).0, 0)
        let cleanupRetry = try run(["__node", "logout-commit", "cleanup_retry", "2", "1"])
        XCTAssertEqual(cleanupRetry.0, 0, cleanupRetry.1)
        XCTAssertTrue(cleanupRetry.1.contains("state=committed"), cleanupRetry.1)
        XCTAssertFalse(FileManager.default.fileExists(
            atPath: stateRoot.appendingPathComponent("logout-transactions/cleanup_retry").path))
    }

    func testControllerReportsReleaseVersion() throws {
        let packageRoot = URL(fileURLWithPath: #filePath)
            .deletingLastPathComponent()
            .deletingLastPathComponent()
            .deletingLastPathComponent()
        let helper = packageRoot.appendingPathComponent("Support/gpt-switch")
        let process = Process()
        let output = Pipe()
        process.executableURL = URL(fileURLWithPath: "/bin/bash")
        process.arguments = [helper.path, "--version"]
        process.standardOutput = output
        process.standardError = output

        try process.run()
        let data = output.fileHandleForReading.readDataToEndOfFile()
        process.waitUntilExit()

        XCTAssertEqual(process.terminationStatus, 0)
        XCTAssertEqual(String(decoding: data, as: UTF8.self), "2.1.2\n")
    }

    func testControllerRecoveryClassifiesBusyForAutomaticRetry() async throws {
        let root = FileManager.default.temporaryDirectory
            .appendingPathComponent("CodexSyncBarRecoveryBusy-\(UUID().uuidString)", isDirectory: true)
        defer { try? FileManager.default.removeItem(at: root) }
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        let executable = root.appendingPathComponent("gpt-switch")
        try Data("#!/bin/bash\nprintf 'gpt-switch: another controller operation is already running\\n' >&2\nexit 1\n".utf8)
            .write(to: executable)
        try FileManager.default.setAttributes([.posixPermissions: 0o700], ofItemAtPath: executable.path)

        do {
            try await SwitchService(executable: executable).recoverControllerState()
            XCTFail("busy controller recovery unexpectedly succeeded")
        } catch {
            XCTAssertTrue(SwitchService.isControllerBusy(error))
        }
    }

    func testSwitchServiceRunsReadableHelperThroughBash() async throws {
        let root = FileManager.default.temporaryDirectory
            .appendingPathComponent("CodexSyncBarReadableHelper-\(UUID().uuidString)", isDirectory: true)
        defer { try? FileManager.default.removeItem(at: root) }
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        let executable = root.appendingPathComponent("gpt-switch")
        let script = """
        #!/bin/bash
        test "$1" = status-json || exit 64
        printf '{"id":"macbook","displayName":"이 MacBook","profileID":2,"accountFingerprint":null,"authMode":"chatgpt","cliState":"logged-in","isReachable":true}\n'
        """
        try Data(script.utf8).write(to: executable)
        try FileManager.default.setAttributes([.posixPermissions: 0o600], ofItemAtPath: executable.path)

        let statuses = try await SwitchService(executable: executable).fetchStatus()

        XCTAssertEqual(statuses.map(\.profileID), [2])
    }

    func testStatusWaitsForInAppAuthMaintenanceInsteadOfReportingControllerBusy() async throws {
        let root = FileManager.default.temporaryDirectory
            .appendingPathComponent("CodexSyncBarStatusSerialization-\(UUID().uuidString)", isDirectory: true)
        defer { try? FileManager.default.removeItem(at: root) }
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        let executable = root.appendingPathComponent("gpt-switch")
        let overlapMarker = root.appendingPathComponent("overlap")
        let operationLock = root.appendingPathComponent("operation-lock")
        let script = """
        #!/bin/bash
        if ! mkdir "\(operationLock.path)" 2>/dev/null; then
          touch "\(overlapMarker.path)"
          printf 'gpt-switch: another controller operation is already running\n' >&2
          exit 1
        fi
        trap 'rmdir "\(operationLock.path)"' EXIT
        sleep 0.2
        case "$1" in
          refresh-if-needed) printf 'action=noop result=ok\n' ;;
          status-json) printf '{"id":"macbook","displayName":"이 MacBook","profileID":1,"accountFingerprint":null,"authMode":"chatgpt","cliState":"logged-in","isReachable":true}\n' ;;
          *) exit 64 ;;
        esac
        """
        try Data(script.utf8).write(to: executable)
        try FileManager.default.setAttributes([.posixPermissions: 0o700], ofItemAtPath: executable.path)

        let service = SwitchService(executable: executable)
        let maintenance = Task { try await service.refreshAuthIfNeeded() }
        try await Task.sleep(nanoseconds: 50_000_000)
        let statuses = try await service.fetchStatus()
        _ = try await maintenance.value

        XCTAssertEqual(statuses.map(\.name), ["macbook"])
        XCTAssertFalse(FileManager.default.fileExists(atPath: overlapMarker.path))
    }

    func testInAppAuthMaintenanceNeverRequestsDesktopClientRestart() async throws {
        let root = FileManager.default.temporaryDirectory
            .appendingPathComponent("CodexSyncBarNoDesktopRestart-\(UUID().uuidString)", isDirectory: true)
        defer { try? FileManager.default.removeItem(at: root) }
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        let executable = root.appendingPathComponent("gpt-switch")
        let argumentsLog = root.appendingPathComponent("arguments.log")
        let script = """
        #!/bin/bash
        printf '%s\n' "$*" >>"\(argumentsLog.path)"
        printf 'profile=1 action=deferred-client-running synced=0 pending=0 result=ok\n'
        """
        try Data(script.utf8).write(to: executable)
        try FileManager.default.setAttributes([.posixPermissions: 0o700], ofItemAtPath: executable.path)

        let service = SwitchService(executable: executable)
        _ = try await service.refreshAuthIfNeeded(profileID: 1)
        _ = try await service.syncAuth(profileID: 1)
        _ = try await service.forceRefreshAuth(profileID: 1)

        let invocations = try String(contentsOf: argumentsLog, encoding: .utf8)
            .split(whereSeparator: \.isNewline)
            .map(String.init)
        XCTAssertEqual(invocations.count, 3)
        XCTAssertTrue(invocations.allSatisfy { $0.hasSuffix("--no-restart-app") }, invocations.joined(separator: "\n"))
    }

    func testControllerRecoveryClassifiesRecoverablePartialForAutomaticRetry() async throws {
        let root = FileManager.default.temporaryDirectory
            .appendingPathComponent("CodexSyncBarRecoveryPending-\(UUID().uuidString)", isDirectory: true)
        defer { try? FileManager.default.removeItem(at: root) }
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        let executable = root.appendingPathComponent("gpt-switch")
        try Data("#!/bin/bash\nprintf 'login_recovery=ok logout_recovery=pending overall=pending\\n' >&2\nexit 2\n".utf8)
            .write(to: executable)
        try FileManager.default.setAttributes([.posixPermissions: 0o700], ofItemAtPath: executable.path)

        do {
            try await SwitchService(executable: executable).recoverControllerState()
            XCTFail("partial controller recovery unexpectedly succeeded")
        } catch {
            XCTAssertTrue(SwitchService.isRecoveryPending(error))
        }
    }

    func testAmbiguousLoginRecoveryFailsClosedInsteadOfRetryingForever() async throws {
        let root = FileManager.default.temporaryDirectory
            .appendingPathComponent("CodexSyncBarRecoveryAmbiguous-\(UUID().uuidString)", isDirectory: true)
        defer { try? FileManager.default.removeItem(at: root) }
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        let executable = root.appendingPathComponent("gpt-switch")
        try Data("#!/bin/bash\nprintf 'login_recovery=pending logout_recovery=ok overall=pending\\n' >&2\nexit 2\n".utf8)
            .write(to: executable)
        try FileManager.default.setAttributes([.posixPermissions: 0o700], ofItemAtPath: executable.path)

        do {
            try await SwitchService(executable: executable).recoverControllerState()
            XCTFail("ambiguous login recovery unexpectedly succeeded")
        } catch {
            XCTAssertFalse(SwitchService.isRecoveryPending(error))
            XCTAssertFalse(SwitchService.isControllerBusy(error))
            XCTAssertTrue(error.localizedDescription.contains("login_recovery=pending"))
        }
    }

    func testUnknownPartialControllerRecoveryFailsClosed() async throws {
        let root = FileManager.default.temporaryDirectory
            .appendingPathComponent("CodexSyncBarRecoveryUnknown-\(UUID().uuidString)", isDirectory: true)
        defer { try? FileManager.default.removeItem(at: root) }
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        let executable = root.appendingPathComponent("gpt-switch")
        try Data("#!/bin/bash\nprintf 'unexpected partial state\\n' >&2\nexit 2\n".utf8)
            .write(to: executable)
        try FileManager.default.setAttributes([.posixPermissions: 0o700], ofItemAtPath: executable.path)

        do {
            try await SwitchService(executable: executable).recoverControllerState()
            XCTFail("unknown partial recovery unexpectedly succeeded")
        } catch {
            XCTAssertFalse(SwitchService.isRecoveryPending(error))
            XCTAssertFalse(SwitchService.isControllerBusy(error))
            XCTAssertTrue(error.localizedDescription.contains("unexpected partial state"))
        }
    }

    func testDuplicateControllerRecoverySummaryFailsClosed() async throws {
        let root = FileManager.default.temporaryDirectory
            .appendingPathComponent("CodexSyncBarRecoveryDuplicate-\(UUID().uuidString)", isDirectory: true)
        defer { try? FileManager.default.removeItem(at: root) }
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        let executable = root.appendingPathComponent("gpt-switch")
        let script = """
        #!/bin/bash
        printf 'login_recovery=ok login_recovery=bad logout_recovery=pending overall=pending\n' >&2
        exit 2
        """
        try Data(script.utf8).write(to: executable)
        try FileManager.default.setAttributes([.posixPermissions: 0o700], ofItemAtPath: executable.path)

        do {
            try await SwitchService(executable: executable).recoverControllerState()
            XCTFail("duplicate recovery summary unexpectedly entered retry mode")
        } catch {
            XCTAssertFalse(SwitchService.isRecoveryPending(error))
            XCTAssertFalse(SwitchService.isControllerBusy(error))
            XCTAssertTrue(error.localizedDescription.contains("login_recovery=bad"))
        }
    }

    func testLogoutPartialCleanupIsReportedWithoutThrowing() async throws {
        let directory = FileManager.default.temporaryDirectory
            .appendingPathComponent("GptSwitchPartialLogoutTests-\(UUID().uuidString)", isDirectory: true)
        defer { try? FileManager.default.removeItem(at: directory) }
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        let executable = directory.appendingPathComponent("gpt-switch")
        let script = """
        #!/bin/bash
        printf 'profile=1 fallback=2 state=partial-cleanup\n'
        exit 2
        """
        try Data(script.utf8).write(to: executable)
        try FileManager.default.setAttributes([.posixPermissions: 0o700], ofItemAtPath: executable.path)

        let service = SwitchService(executable: executable)
        let result = try await service.logoutProfile(1)
        XCTAssertTrue(result.isPartialCleanup)
        XCTAssertTrue(result.output.contains("state=partial-cleanup"))
    }

    @MainActor
    func testCancelledLoginProcessIsStopped() throws {
        let process = Process()
        process.executableURL = URL(fileURLWithPath: "/bin/sleep")
        process.arguments = ["30"]
        try process.run()

        LoginCoordinator.stop(process)
        let deadline = Date().addingTimeInterval(3)
        while process.isRunning, Date() < deadline {
            RunLoop.current.run(until: Date().addingTimeInterval(0.05))
        }
        XCTAssertFalse(process.isRunning)
    }

    func testLiveProfileOneUsageWhenRequested() async throws {
        guard ProcessInfo.processInfo.environment["CODEX_SYNCBAR_LIVE_TEST"] == "1" else {
            throw XCTSkip("Set CODEX_SYNCBAR_LIVE_TEST=1 to query the real usage boundary.")
        }
        let store = AuthStore()
        let service = UsageService(authStore: store, switchService: SwitchService())
        let snapshot = try await service.fetch(profileID: 1)

        XCTAssertEqual(snapshot.profileID, 1)
        XCTAssertFalse(snapshot.plan.isEmpty)
        XCTAssertNotNil(snapshot.weekly)
        if let remaining = snapshot.weekly?.remainingPercent {
            XCTAssertTrue((0...100).contains(remaining))
            print("LIVE_USAGE profile=1 plan=\(snapshot.plan) weeklyRemaining=\(Int(remaining.rounded()))")
        }
        if let credits = snapshot.resetCredits, credits > 0 {
            XCTAssertEqual(snapshot.resetCreditExpirations.count, credits)
            XCTAssertTrue(snapshot.resetCreditExpirations.allSatisfy { $0 > Date() })
            let expirations = snapshot.resetCreditExpirations.map {
                Formatting.resetCreditExpiryDescription($0)
            }
            print("LIVE_RESET_CREDITS profile=1 count=\(credits) expirations=\(expirations.joined(separator: ","))")
        }
    }

    func testLiveProfileTwoUsageWhenRequested() async throws {
        guard ProcessInfo.processInfo.environment["CODEX_SYNCBAR_LIVE_TEST"] == "1" else {
            throw XCTSkip("Set CODEX_SYNCBAR_LIVE_TEST=1 to query the real usage boundary.")
        }
        let store = AuthStore()
        let service = UsageService(authStore: store, switchService: SwitchService())
        let snapshot = try await service.fetch(profileID: 2)

        XCTAssertEqual(snapshot.profileID, 2)
        XCTAssertFalse(snapshot.plan.isEmpty)
        XCTAssertNotNil(snapshot.weekly)
        if let remaining = snapshot.weekly?.remainingPercent {
            XCTAssertTrue((0...100).contains(remaining))
            print("LIVE_USAGE profile=2 plan=\(snapshot.plan) weeklyRemaining=\(Int(remaining.rounded()))")
        }
        if let credits = snapshot.resetCredits, credits > 0 {
            XCTAssertEqual(snapshot.resetCreditExpirations.count, credits)
            XCTAssertTrue(snapshot.resetCreditExpirations.allSatisfy { $0 > Date() })
            let expirations = snapshot.resetCreditExpirations.map {
                Formatting.resetCreditExpiryDescription($0)
            }
            print("LIVE_RESET_CREDITS profile=2 count=\(credits) expirations=\(expirations.joined(separator: ","))")
        }
    }

    func testLiveProfileTwoWeeklyAnchorSendWhenRequested() async throws {
        guard ProcessInfo.processInfo.environment["CODEX_SYNCBAR_LIVE_ANCHOR_SEND"] == "1" else {
            throw XCTSkip("Set CODEX_SYNCBAR_LIVE_ANCHOR_SEND=1 to send the real weekly anchor message.")
        }
        let store = AuthStore()
        let switcher = SwitchService()
        let service = WeeklyUsageAnchorService(
            authStore: store,
            credentialRefresher: { profileID, failedAccessToken in
                _ = try await switcher.forceRefreshAuth(
                    profileID: profileID,
                    expectedAccessToken: failedAccessToken)
            })
        let response = try await service.send(profileID: 2)

        XCTAssertFalse(response.isEmpty)
        print("LIVE_WEEKLY_ANCHOR profile=2 response=\(response)")
    }
}
