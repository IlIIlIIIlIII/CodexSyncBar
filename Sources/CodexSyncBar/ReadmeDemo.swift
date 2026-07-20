import Foundation

enum ReadmeDemoScreen: String, Equatable {
    case popover
    case settings
}

struct ReadmeDemoCommand: Equatable {
    let screen: ReadmeDemoScreen
    let outputURL: URL

    static func parse(arguments: [String]) -> Self? {
        let screenValues = arguments.compactMap { argument -> String? in
            guard argument.hasPrefix("--readme-demo=") else { return nil }
            return String(argument.dropFirst("--readme-demo=".count))
        }
        let outputValues = arguments.compactMap { argument -> String? in
            guard argument.hasPrefix("--readme-output=") else { return nil }
            return String(argument.dropFirst("--readme-output=".count))
        }
        guard screenValues.count == 1,
              outputValues.count == 1,
              let screen = ReadmeDemoScreen(rawValue: screenValues[0]),
              outputValues[0].hasPrefix("/")
        else { return nil }
        return Self(
            screen: screen,
            outputURL: URL(fileURLWithPath: outputValues[0]).standardizedFileURL)
    }
}

struct ReadmeDemoFixture {
    let referenceDate: Date
    let selectedProfileID: Int
    let profiles: [AccountProfile]
    let usageStates: [Int: UsageState]
    let configuredDevices: [SSHDeviceConfiguration]
    let devices: [DeviceStatus]
    let tokenUsageSnapshot: TokenUsageSnapshot

    static let standard: Self = {
        let referenceDate = Date(timeIntervalSince1970: 1_784_552_400)
        let resetBaseDate = Date()
        let profiles = [
            AccountProfile(id: 1, email: "demo.main@example.com", alias: "메인"),
            AccountProfile(id: 2, email: "demo.sub@example.com", alias: "서브"),
        ]
        let configuredDevices = [
            SSHDeviceConfiguration(
                id: "workstation",
                credentialID: UUID(uuidString: "11111111-1111-4111-8111-111111111111"),
                displayName: "작업 서버",
                host: "workstation.example.net",
                port: 22,
                username: "demo",
                authentication: .openSSHConfig,
                identityFile: nil,
                certificateFile: nil,
                hasPassword: false,
                hasKeyPassphrase: false,
                enabled: true),
            SSHDeviceConfiguration(
                id: "build-server",
                credentialID: UUID(uuidString: "22222222-2222-4222-8222-222222222222"),
                displayName: "빌드 서버",
                host: "build.example.net",
                port: 22,
                username: "builder",
                authentication: .openSSHConfig,
                identityFile: nil,
                certificateFile: nil,
                hasPassword: false,
                hasKeyPassphrase: false,
                enabled: true),
        ]
        let devices = [
            DeviceStatus(
                name: "macbook",
                profileID: 1,
                accountFingerprint: "demo-main",
                authMode: "access-only",
                cliState: "running",
                isReachable: true),
            DeviceStatus(
                name: "workstation",
                configuredDisplayName: "작업 서버",
                profileID: 1,
                accountFingerprint: "demo-main",
                authMode: "access-only",
                cliState: "running",
                isReachable: true),
            DeviceStatus(
                name: "build-server",
                configuredDisplayName: "빌드 서버",
                profileID: 1,
                accountFingerprint: "demo-main",
                authMode: "access-only",
                cliState: "running",
                isReachable: true),
        ]
        let usageStates: [Int: UsageState] = [
            1: .loaded(usageSnapshot(
                profileID: 1,
                email: profiles[0].email,
                referenceDate: referenceDate,
                resetBaseDate: resetBaseDate,
                fiveHourUsed: 18,
                weeklyUsed: 32,
                sparkFiveHourUsed: 40,
                sparkWeeklyUsed: 5)),
            2: .loaded(usageSnapshot(
                profileID: 2,
                email: profiles[1].email,
                referenceDate: referenceDate,
                resetBaseDate: resetBaseDate,
                fiveHourUsed: 27,
                weeklyUsed: 58,
                sparkFiveHourUsed: 12,
                sparkWeeklyUsed: 24)),
        ]
        let tokenDevices = [
            tokenUsageDevice(
                id: "macbook",
                displayName: "이 MacBook",
                totalTokens: 5_000_000,
                generatedAt: referenceDate),
            tokenUsageDevice(
                id: "workstation",
                displayName: "작업 서버",
                totalTokens: 4_345_678,
                generatedAt: referenceDate),
            tokenUsageDevice(
                id: "build-server",
                displayName: "빌드 서버",
                totalTokens: 3_000_000,
                generatedAt: referenceDate),
        ]
        return Self(
            referenceDate: referenceDate,
            selectedProfileID: 1,
            profiles: profiles,
            usageStates: usageStates,
            configuredDevices: configuredDevices,
            devices: devices,
            tokenUsageSnapshot: TokenUsageSnapshot(
                devices: tokenDevices,
                collectedAt: referenceDate))
    }()

    private static func usageSnapshot(
        profileID: Int,
        email: String,
        referenceDate: Date,
        resetBaseDate: Date,
        fiveHourUsed: Double,
        weeklyUsed: Double,
        sparkFiveHourUsed: Double,
        sparkWeeklyUsed: Double) -> UsageSnapshot
    {
        UsageSnapshot(
            profileID: profileID,
            email: email,
            plan: "Pro",
            session: UsageWindow(
                usedPercent: fiveHourUsed,
                resetsAt: resetBaseDate.addingTimeInterval(2 * 60 * 60 + 35 * 60),
                durationSeconds: 5 * 60 * 60),
            weekly: UsageWindow(
                usedPercent: weeklyUsed,
                resetsAt: resetBaseDate.addingTimeInterval(5 * 24 * 60 * 60 + 4 * 60 * 60),
                durationSeconds: 7 * 24 * 60 * 60),
            sparkSession: UsageWindow(
                usedPercent: sparkFiveHourUsed,
                resetsAt: resetBaseDate.addingTimeInterval(3 * 60 * 60 + 10 * 60),
                durationSeconds: 5 * 60 * 60),
            sparkWeekly: UsageWindow(
                usedPercent: sparkWeeklyUsed,
                resetsAt: resetBaseDate.addingTimeInterval(6 * 24 * 60 * 60 + 8 * 60 * 60),
                durationSeconds: 7 * 24 * 60 * 60),
            creditBalance: nil,
            unlimitedCredits: false,
            resetCredits: 3,
            resetCreditExpirations: [
                resetBaseDate.addingTimeInterval(2 * 24 * 60 * 60 + 8 * 60 * 60),
                resetBaseDate.addingTimeInterval(9 * 24 * 60 * 60 + 12 * 60 * 60),
                resetBaseDate.addingTimeInterval(21 * 24 * 60 * 60),
            ],
            updatedAt: referenceDate)
    }

    private static func tokenUsageDevice(
        id: String,
        displayName: String,
        totalTokens: Int64,
        generatedAt: Date) -> DeviceTokenUsage
    {
        let inputTokens = totalTokens * 3 / 4
        let outputTokens = totalTokens - inputTokens
        let bucket = ModelTokenUsage(
            model: "gpt-5.6-terra",
            serviceTier: "default",
            inputTokens: inputTokens,
            cachedInputTokens: inputTokens / 4,
            cacheWriteInputTokens: 0,
            outputTokens: outputTokens,
            reasoningOutputTokens: outputTokens / 3,
            totalTokens: totalTokens,
            requests: 24)
        let formatter = ISO8601DateFormatter()
        return DeviceTokenUsage(
            id: id,
            displayName: displayName,
            isReachable: true,
            summary: DeviceTokenUsageSummary(
                schemaVersion: 4,
                generatedAt: formatter.string(from: generatedAt),
                scannedFiles: 12,
                requests: bucket.requests,
                inputTokens: bucket.inputTokens,
                cachedInputTokens: bucket.cachedInputTokens,
                cacheWriteInputTokens: bucket.cacheWriteInputTokens,
                outputTokens: bucket.outputTokens,
                reasoningOutputTokens: bucket.reasoningOutputTokens,
                totalTokens: bucket.totalTokens,
                buckets: [bucket],
                errors: []),
            error: nil)
    }
}
