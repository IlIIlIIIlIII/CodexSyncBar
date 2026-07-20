import AppKit
import Darwin
import Foundation
import ServiceManagement
import SwiftUI

private struct ProfileSwapJournal: Codable {
    let firstFingerprint: String
    let secondFingerprint: String
    let firstBrowserMarker: String
    let secondBrowserMarker: String
}

private enum ProfileSlotArrangement: Equatable {
    case original
    case swapped
    case unknown
}

private struct SSHCredentialMutationIntent: Codable {
    let id: UUID
    let deviceID: String
    let oldCredentialID: String?
    let newCredentialID: String
}

private struct SSHDeviceActivationIntent: Codable {
    let id: UUID
    let originalDevice: SSHDeviceConfiguration
}

@MainActor
final class AppModel: ObservableObject {
    @Published private(set) var profiles: [AccountProfile]
    let authStore: AuthStore
    let loginCoordinator: LoginCoordinator

    @Published var selectedProfileID: Int
    @Published private(set) var usageStates: [Int: UsageState]
    @Published private(set) var usageDisplayPreferences: UsageDisplayPreferences
    @Published private(set) var menuBarUsagePreferences: MenuBarUsagePreferences
    @Published private(set) var weeklyAnchorPreferences: WeeklyAnchorPreferences
    @Published private(set) var weeklyAnchorRecords: [Int: WeeklyAnchorRecord]
    @Published private(set) var weeklyAnchorRunningProfileIDs: Set<Int> = []
    @Published private(set) var configuredDevices: [SSHDeviceConfiguration]
    @Published private(set) var devices: [DeviceStatus] = []
    @Published private(set) var tokenUsageSnapshot: TokenUsageSnapshot?
    @Published private(set) var tokenUsageError: String?
    @Published private(set) var isCollectingTokenUsage = false
    @Published private(set) var isRefreshing = false
    @Published private(set) var isSwitching = false
    @Published private(set) var isManagingProfiles = false
    @Published private(set) var isMaintainingAuth = false
    @Published private(set) var loginRequiredProfileIDs: Set<Int> = []
    @Published private(set) var browserCleanupPendingProfileIDs: Set<Int> = []
    @Published private(set) var profileManagementRecoveryNeeded = false
    @Published private(set) var authMaintenanceSummary = "중앙 인증 확인 전"
    @Published private(set) var authMaintenanceNeedsAttention = false
    @Published private(set) var configurationError: String?
    @Published var banner: AppBanner?
    @Published var launchAtLogin = false
    @Published private(set) var launchAtLoginRequiresApproval = false
    @Published private(set) var launchAtLoginStatusText = "확인 중…"
    @Published private(set) var isReadmeDemo = false

    private let usageService: UsageService
    private let switchService: SwitchService
    private let configurationStore: AppConfigurationStore
    private let usageDisplayPreferencesStore: UsageDisplayPreferencesStore
    private let menuBarUsagePreferencesStore: MenuBarUsagePreferencesStore
    private let weeklyAnchorStore: WeeklyAnchorStore
    private let weeklyAnchorService: WeeklyUsageAnchorService
    private let secretStore: SSHSecretStoring
    private let controllerMutationLock = ControllerMutationLock()
    private var usagePollingTask: Task<Void, Never>?
    private var maintenanceTask: Task<Void, Never>?
    private var devicePollingTask: Task<Void, Never>?
    private var controllerReconciliationTask: Task<Void, Never>?
    private var bannerDismissTask: Task<Void, Never>?
    private var weeklyAnchorTasks: [Int: Task<Void, Never>] = [:]
    private var loginWindowController: LoginWindowController?
    private var loginProfileID: Int?
    private var pendingNewProfileIDs: Set<Int> = []
    private var authWarningBannerID: UUID?
    private var pendingForcedSyncProfileIDs: Set<Int> = []
    private var pendingForcedSyncAll = false
    private var hasStarted = false
    private var activeUsageRefreshCount = 0

    private let fullSyncDefaultsKey = "lastFullAuthSyncAt"
    private let pendingSecretCleanupDefaultsKey = "pendingSSHSecretCleanupIdentifiers"
    private let maintenanceInterval: UInt64 = 3_600_000_000_000
    private let deviceStatusInterval: UInt64 = 1_800_000_000_000
    private let usageInterval: UInt64 = 300_000_000_000
    private let fullSyncInterval: TimeInterval = 6 * 60 * 60

    init(readmeDemoFixture: ReadmeDemoFixture? = nil) {
        let store = AuthStore()
        let switcher = SwitchService()
        let configurationStore = AppConfigurationStore()
        let usageDisplayPreferencesStore = UsageDisplayPreferencesStore()
        let menuBarUsagePreferencesStore = MenuBarUsagePreferencesStore()
        let weeklyAnchorStore = WeeklyAnchorStore()
        let loadedConfiguration: AppConfiguration?
        let initialConfigurationError: String?
        if let readmeDemoFixture {
            loadedConfiguration = AppConfiguration(
                schemaVersion: AppConfiguration.schemaVersion,
                nextAccountID: (readmeDemoFixture.profiles.map(\.id).max() ?? 0) + 1,
                accounts: readmeDemoFixture.profiles,
                devices: readmeDemoFixture.configuredDevices)
            initialConfigurationError = nil
        } else if FileManager.default.fileExists(atPath: configurationStore.configurationURL.path) {
            do {
                loadedConfiguration = try configurationStore.load()
                initialConfigurationError = nil
            } catch {
                loadedConfiguration = nil
                initialConfigurationError = error.localizedDescription
            }
        } else {
            loadedConfiguration = nil
            initialConfigurationError = nil
        }
        let loadedProfiles = loadedConfiguration?.accounts ?? []
        profiles = loadedProfiles
        configuredDevices = loadedConfiguration?.devices ?? []
        usageStates = readmeDemoFixture?.usageStates
            ?? Dictionary(uniqueKeysWithValues: loadedProfiles.map { ($0.id, UsageState.idle) })
        usageDisplayPreferences = readmeDemoFixture == nil
            ? usageDisplayPreferencesStore.load()
            : .allVisible
        menuBarUsagePreferences = readmeDemoFixture == nil
            ? menuBarUsagePreferencesStore.load()
            : MenuBarUsagePreferences(items: [.codexWeekly, .sparkWeekly])
        weeklyAnchorPreferences = readmeDemoFixture == nil
            ? weeklyAnchorStore.loadPreferences()
            : .disabled
        weeklyAnchorRecords = readmeDemoFixture == nil ? weeklyAnchorStore.loadRecords() : [:]
        configurationError = initialConfigurationError
        authStore = store
        switchService = switcher
        self.configurationStore = configurationStore
        self.usageDisplayPreferencesStore = usageDisplayPreferencesStore
        self.menuBarUsagePreferencesStore = menuBarUsagePreferencesStore
        self.weeklyAnchorStore = weeklyAnchorStore
        weeklyAnchorService = WeeklyUsageAnchorService(
            authStore: store,
            credentialRefresher: { profileID, failedAccessToken in
                _ = try await switcher.forceRefreshAuth(
                    profileID: profileID,
                    expectedAccessToken: failedAccessToken)
            })
        secretStore = SystemKeychainStore()
        usageService = UsageService(authStore: store, switchService: switcher)
        loginCoordinator = LoginCoordinator(authStore: store)
        if let readmeDemoFixture {
            selectedProfileID = readmeDemoFixture.selectedProfileID
            browserCleanupPendingProfileIDs = []
        } else {
            let stored = UserDefaults.standard.integer(forKey: "selectedProfileID")
            selectedProfileID = loadedProfiles.contains(where: { $0.id == stored })
                ? stored
                : (loadedProfiles.first?.id ?? 0)
            browserCleanupPendingProfileIDs = Set(
                profiles.map(\.id).filter {
                    UserDefaults.standard.bool(forKey: browserCleanupDefaultsKey(profileID: $0))
                })
        }

        loginCoordinator.onCompletion = { [weak self] result in
            guard let self else { return }
            switch result {
            case .success:
                let profileID = self.loginProfileID ?? self.selectedProfileID
                self.loginProfileID = nil
                self.loginWindowController?.window?.close()
                Task {
                    do {
                        let credentials = try await self.authStore.credentials(for: profileID)
                        self.loginRequiredProfileIDs.remove(profileID)
                        try self.withControllerMutationLock {
                            try self.configurationStore.updateAccountEmail(id: profileID, email: credentials.email)
                        }
                        self.pendingNewProfileIDs.remove(profileID)
                        guard self.reloadConfiguration() else { return }
                        self.banner = AppBanner(style: .success, message: "로그인이 완료되었습니다.")
                    } catch {
                        self.configurationError = "로그인 계정 설정 확정 실패: \(error.localizedDescription)"
                        self.banner = AppBanner(
                            style: .error,
                            message: "인증은 보존했지만 계정 설정을 확정하지 못했습니다. 앱을 다시 열어 복구해 주세요: \(error.localizedDescription)")
                        return
                    }
                    await self.maintainAuthIfNeeded(forceSync: true, profileID: profileID)
                    await self.refresh(profileID: profileID)
                }
            case let .failure(error):
                if case AppError.loginCancelled = error {
                    if let profileID = self.loginProfileID {
                        self.loginProfileID = nil
                        self.removeEmptyReservedAccount(profileID)
                    }
                    return
                }
                self.banner = AppBanner(style: .error, message: error.localizedDescription)
            }
        }

        if let readmeDemoFixture {
            devices = readmeDemoFixture.devices
            tokenUsageSnapshot = readmeDemoFixture.tokenUsageSnapshot
            authMaintenanceSummary = "데모 인증 정상"
            launchAtLoginStatusText = "데모 모드"
            isReadmeDemo = true
            hasStarted = true
        }
    }

    deinit {
        usagePollingTask?.cancel()
        maintenanceTask?.cancel()
        devicePollingTask?.cancel()
        controllerReconciliationTask?.cancel()
        bannerDismissTask?.cancel()
        for task in weeklyAnchorTasks.values { task.cancel() }
    }

    var selectedProfile: AccountProfile {
        profiles.first(where: { $0.id == selectedProfileID })
            ?? profiles.first
            ?? AccountProfile(id: 0, email: "설정 복구 필요")
    }

    var activeProfileID: Int? {
        devices.first(where: { $0.name == "macbook" })?.profileID
    }

    var menuProfile: AccountProfile {
        profiles.first(where: { $0.id == activeProfileID }) ?? selectedProfile
    }

    var menuUsageState: UsageState {
        usageStates[menuProfile.id] ?? .idle
    }

    var menuTitle: String {
        let mismatch = devices.contains(where: { !$0.isReachable || $0.profileID != activeProfileID })
        return MenuTitleFormatter.title(
            profile: menuProfile,
            state: menuUsageState,
            items: menuBarUsagePreferences.items,
            isRefreshing: isRefreshing,
            hasDeviceMismatch: mismatch)
    }

    var configuredNodeCount: Int {
        1 + configuredDevices.filter(\.enabled).count
    }

    func authenticationStatus(for profileID: Int) -> ProfileAuthenticationStatus {
        ProfileAuthenticationStatus.resolve(
            usageState: usageStates[profileID] ?? .idle,
            knownReauthenticationRequired: loginRequiredProfileIDs.contains(profileID))
    }

    var profilesRequiringLogin: [AccountProfile] {
        profiles.filter { authenticationStatus(for: $0.id).needsReauthentication }
    }

    var managementActionsDisabled: Bool {
        configurationError != nil || profileManagementRecoveryNeeded || hasControllerTransaction
    }

    func start() async {
        guard !hasStarted else { return }
        do {
            try BundledHelperInstaller.installFromMainBundleIfPresent()
        } catch {
            banner = AppBanner(style: .error, message: "helper 설치 실패: \(error.localizedDescription)")
            return
        }
        hasStarted = true
        refreshLaunchAtLoginState()
        // A pre-2.0 installation has no versioned configuration, so legacy
        // node/swap recovery must run before migration. Once config.json
        // exists, durable login/logout recovery always takes priority because
        // legacy node status can otherwise mutate canonical auth mid-login.
        let isLegacyMigration = !pathEntryExists(configurationStore.configurationURL)
        if isLegacyMigration {
            do {
                try await recoverLegacyLocalState()
            } catch {
                profileManagementRecoveryNeeded = true
                banner = AppBanner(
                    style: .error,
                    message: "중단된 계정 작업 복구 필요: \(error.localizedDescription)")
                return
            }
        }

        var controllerStateReady = false
        do {
            try await recoverControllerStateAndReload()
            controllerStateReady = true
        } catch {
            if shouldRetryControllerRecovery(after: error) {
                configurationError = nil
                banner = AppBanner(
                    style: .info,
                    message: "진행 중인 로그인 또는 계정 작업이 끝나면 설정을 자동으로 복구합니다.")
                scheduleControllerReconciliation()
            } else {
                failClosedForConfiguration(error)
            }
        }

        if controllerStateReady {
            do {
                if !isLegacyMigration { try await recoverLegacyLocalState() }
                cleanupPendingSecretNamespaces()
                await maintainAuthIfNeeded()
                await refreshAll()
            } catch {
                profileManagementRecoveryNeeded = true
                banner = AppBanner(
                    style: .error,
                    message: "중단된 계정 작업 복구 필요: \(error.localizedDescription)")
            }
        }

        usagePollingTask?.cancel()
        usagePollingTask = Task { [weak self] in
            while !Task.isCancelled {
                guard let self else { return }
                try? await Task.sleep(nanoseconds: self.usageInterval)
                guard !Task.isCancelled else { return }
                await self.refreshUsageOnly()
            }
        }

        maintenanceTask?.cancel()
        maintenanceTask = Task { [weak self] in
            while !Task.isCancelled {
                guard let self else { return }
                try? await Task.sleep(nanoseconds: self.maintenanceInterval)
                guard !Task.isCancelled else { return }
                await self.maintainAuthIfNeeded()
            }
        }

        devicePollingTask?.cancel()
        devicePollingTask = Task { [weak self] in
            while !Task.isCancelled {
                guard let self else { return }
                try? await Task.sleep(nanoseconds: self.deviceStatusInterval)
                guard !Task.isCancelled else { return }
                await self.refreshDeviceStatus()
                await self.refreshTokenUsage()
            }
        }
    }

    func selectProfile(_ profileID: Int) {
        guard configurationError == nil else { return }
        guard profiles.contains(where: { $0.id == profileID }) else { return }
        selectedProfileID = profileID
        UserDefaults.standard.set(profileID, forKey: "selectedProfileID")
        if case .idle = usageStates[profileID] {
            Task { await refresh(profileID: profileID) }
        }
    }

    func setUsageDisplay(_ item: UsageDisplayItem, isVisible: Bool) {
        guard usageDisplayPreferences.isVisible(item) != isVisible else { return }
        var updated = usageDisplayPreferences
        updated.setVisible(isVisible, for: item)
        usageDisplayPreferencesStore.save(updated)
        usageDisplayPreferences = updated
    }

    func setMenuBarUsageItem(_ item: UsageDisplayItem, at index: Int) {
        let updated = menuBarUsagePreferences.replacingItem(at: index, with: item)
        guard updated != menuBarUsagePreferences else { return }
        menuBarUsagePreferencesStore.save(updated)
        menuBarUsagePreferences = updated
    }

    func setMenuBarUsageItemCount(_ count: Int) {
        let updated = menuBarUsagePreferences.settingItemCount(count)
        guard updated != menuBarUsagePreferences else { return }
        menuBarUsagePreferencesStore.save(updated)
        menuBarUsagePreferences = updated
    }

    func isWeeklyAnchorEnabled(profileID: Int) -> Bool {
        weeklyAnchorPreferences.isEnabled(for: profileID)
    }

    func setWeeklyAnchorEnabled(_ enabled: Bool, profileID: Int) {
        guard profiles.contains(where: { $0.id == profileID }) else { return }
        var updated = weeklyAnchorPreferences
        updated.setEnabled(enabled, for: profileID)
        guard updated != weeklyAnchorPreferences else { return }
        weeklyAnchorStore.savePreferences(updated)
        weeklyAnchorPreferences = updated

        if enabled, let snapshot = usageStates[profileID]?.snapshot {
            evaluateWeeklyAnchor(snapshot)
        }
    }

    func weeklyAnchorStatusText(profileID: Int, relativeTo now: Date = Date()) -> String {
        guard isWeeklyAnchorEnabled(profileID: profileID) else { return "사용 안 함" }
        if weeklyAnchorRunningProfileIDs.contains(profileID) { return "메시지 전송 중…" }
        let record = weeklyAnchorRecords[profileID] ?? .empty
        if record.lastError != nil {
            if let lastAttemptAt = record.lastAttemptAt {
                let retryAt = lastAttemptAt.addingTimeInterval(WeeklyAnchorDecisionEngine.retryInterval)
                if retryAt > now {
                    return "실행 실패 · \(Formatting.resetCreditExpiryDescription(retryAt, relativeTo: now)) 후 재시도"
                }
            }
            return "실행 실패 · 다음 확인 때 재시도"
        }
        if (record.resetDriftObservationCount ?? 0) > 0 {
            return "초기화 시각 변경 확인 중…"
        }
        if let nextResetAt = record.nextResetAt, nextResetAt > now {
            return "\(Formatting.resetCreditExpiryDescription(nextResetAt, relativeTo: now)) 후 자동 실행"
        }
        if let lastSuccessAt = record.lastSuccessAt {
            return "최근 실행 \(lastSuccessAt.formatted(date: .omitted, time: .shortened))"
        }
        return "주간 사용량 확인 대기"
    }

    func refreshAll(allowDuringProfileManagement: Bool = false) async {
        guard configurationError == nil else { return }
        guard !isRefreshing, allowDuringProfileManagement || !isManagingProfiles else { return }
        beginUsageRefresh()
        defer { endUsageRefresh() }

        await withTaskGroup(of: Void.self) { group in
            group.addTask { await self.refreshDeviceStatus() }
            group.addTask { await self.refreshTokenUsage() }
            for profile in profiles {
                group.addTask {
                    await self.refresh(
                        profileID: profile.id,
                        manageSpinner: false,
                        allowDuringProfileManagement: allowDuringProfileManagement)
                }
            }
        }

        if let activeProfileID, !profiles.contains(where: { $0.id == selectedProfileID }) {
            selectProfile(activeProfileID)
        }
    }

    func refreshUsageOnly() async {
        guard configurationError == nil else { return }
        guard !isRefreshing, !isManagingProfiles, !isSwitching else { return }
        beginUsageRefresh()
        defer { endUsageRefresh() }

        await withTaskGroup(of: Void.self) { group in
            for profile in profiles {
                group.addTask { await self.refresh(profileID: profile.id, manageSpinner: false) }
            }
        }
    }

    func maintainAuthIfNeeded(forceSync: Bool = false, profileID: Int? = nil) async {
        guard configurationError == nil else { return }
        guard !isMaintainingAuth, !isSwitching, !isManagingProfiles else {
            if forceSync {
                if let profileID {
                    pendingForcedSyncProfileIDs.insert(profileID)
                } else {
                    pendingForcedSyncAll = true
                    pendingForcedSyncProfileIDs.removeAll()
                }
            }
            return
        }
        isMaintainingAuth = true
        authMaintenanceSummary = "중앙 인증 확인 중…"
        defer {
            isMaintainingAuth = false
            scheduleQueuedForcedSyncIfPossible()
        }

        do {
            let refreshResult = try await switchService.refreshAuthIfNeeded(profileID: profileID)
            var partial = refreshResult.isPartial
            var changed = refreshResult.didRefresh
            var deferred = refreshResult.didDefer

            let lastFullSync = UserDefaults.standard.object(forKey: fullSyncDefaultsKey) as? Date
            let fullSyncDue = lastFullSync.map { Date().timeIntervalSince($0) >= fullSyncInterval } ?? true
            if forceSync || (profileID == nil && fullSyncDue) {
                let syncResult = try await switchService.syncAuth(profileID: profileID)
                partial = partial || syncResult.isPartial
                changed = changed || syncResult.didSync
                deferred = deferred || syncResult.didDefer
                if !syncResult.isPartial, profileID == nil {
                    UserDefaults.standard.set(Date(), forKey: fullSyncDefaultsKey)
                }
            }

            authMaintenanceNeedsAttention = partial
            if partial {
                authMaintenanceSummary = "일부 기기 동기화 대기 · 자동 재시도"
                let warning = AppBanner(style: .warning, message: "중앙 인증은 안전하지만 일부 기기 동기화를 다시 시도할 예정입니다.")
                banner = warning
                authWarningBannerID = warning.id
            } else if deferred {
                authMaintenanceSummary = "Codex 사용 중 · 자동 갱신 대기"
                if banner?.id == authWarningBannerID { banner = nil }
                authWarningBannerID = nil
            } else {
                authMaintenanceSummary = "인증 동기화 정상 · \(Date().formatted(date: .omitted, time: .shortened))"
                if banner?.id == authWarningBannerID { banner = nil }
                authWarningBannerID = nil
            }

            if changed {
                await refreshDeviceStatus()
                await refreshUsageOnly()
            }
        } catch {
            authMaintenanceNeedsAttention = true
            authMaintenanceSummary = "인증 자동 갱신 확인 필요"
            let warning = AppBanner(style: .warning, message: "인증 유지 실패: \(error.localizedDescription)")
            banner = warning
            authWarningBannerID = warning.id
            // A command may have safely promoted one profile before another
            // profile or replica failed. Always reload the observable state.
            await refreshDeviceStatus()
            await refreshUsageOnly()
        }
    }

    func refresh(
        profileID: Int,
        manageSpinner: Bool = true,
        allowDuringProfileManagement: Bool = false) async
    {
        guard configurationError == nil, profileID > 0 else { return }
        guard allowDuringProfileManagement || !isManagingProfiles else { return }
        if manageSpinner { beginUsageRefresh() }
        defer { if manageSpinner { endUsageRefresh() } }
        let previous = usageStates[profileID]?.snapshot
        usageStates[profileID] = .loading(previous: previous)
        do {
            let snapshot = try await usageService.fetch(profileID: profileID)
            loginRequiredProfileIDs.remove(profileID)
            usageStates[profileID] = .loaded(snapshot)
            evaluateWeeklyAnchor(snapshot)
        } catch let error as AppError {
            let required: Bool
            switch error {
            case .loginRequired, .missingAuth, .invalidAuth:
                required = true
            default:
                required = false
            }
            usageStates[profileID] = .failed(
                previous: previous,
                message: error.localizedDescription,
                loginRequired: required)
            if required {
                let isNewWarning = loginRequiredProfileIDs.insert(profileID).inserted
                if isNewWarning, let profile = profile(for: profileID) {
                    banner = AppBanner(
                        style: .warning,
                        message: "\(profile.alias) 계정은 재로그인이 필요합니다. 설정 > 계정에서 로그인해 주세요.")
                }
            }
        } catch {
            usageStates[profileID] = .failed(
                previous: previous,
                message: error.localizedDescription,
                loginRequired: false)
        }
    }

    func refreshDeviceStatus() async {
        guard configurationError == nil else { return }
        do {
            devices = try await switchService.fetchStatus()
            if let active = activeProfileID,
               UserDefaults.standard.object(forKey: "selectedProfileID") == nil
            {
                selectedProfileID = active
            }
        } catch {
            banner = AppBanner(style: .warning, message: "장비 상태 확인 실패: \(error.localizedDescription)")
        }
    }

    func refreshTokenUsage() async {
        guard configurationError == nil, !isCollectingTokenUsage else { return }
        isCollectingTokenUsage = true
        defer { isCollectingTokenUsage = false }
        do {
            tokenUsageSnapshot = try await switchService.fetchTokenUsage()
            tokenUsageError = nil
        } catch {
            tokenUsageError = error.localizedDescription
        }
    }

    private func beginUsageRefresh() {
        activeUsageRefreshCount += 1
        isRefreshing = true
    }

    private func endUsageRefresh() {
        activeUsageRefreshCount = max(0, activeUsageRefreshCount - 1)
        isRefreshing = activeUsageRefreshCount > 0
    }

    private func evaluateWeeklyAnchor(_ snapshot: UsageSnapshot, now: Date = Date()) {
        let profileID = snapshot.profileID
        guard profileID > 0, profiles.contains(where: { $0.id == profileID }) else { return }
        var record = weeklyAnchorRecords[profileID] ?? .empty
        switch WeeklyAnchorDecisionEngine.decision(
            enabled: isWeeklyAnchorEnabled(profileID: profileID),
            window: snapshot.weekly,
            record: record,
            now: now)
        {
        case .none:
            return
        case let .observe(nextResetAt):
            let needsSave = record.nextResetAt != nextResetAt
                || record.resetDriftCandidateAt != nil
                || record.resetDriftObservationCount != nil
            guard needsSave else { return }
            record.nextResetAt = nextResetAt
            record.resetDriftCandidateAt = nil
            record.resetDriftObservationCount = nil
            record.lastError = nil
            saveWeeklyAnchorRecord(record, profileID: profileID)
        case let .confirmResetDrift(observedResetAt):
            record.resetDriftCandidateAt = observedResetAt
            record.resetDriftObservationCount = (record.resetDriftObservationCount ?? 0) + 1
            record.lastError = nil
            saveWeeklyAnchorRecord(record, profileID: profileID)
        case let .alreadyActive(nextResetAt):
            record.lastHandledResetAt = record.nextResetAt
            record.nextResetAt = nextResetAt
            record.resetDriftCandidateAt = nil
            record.resetDriftObservationCount = nil
            record.lastError = nil
            saveWeeklyAnchorRecord(record, profileID: profileID)
        case let .trigger(expectedResetAt):
            startWeeklyAnchor(
                profileID: profileID,
                expectedResetAt: expectedResetAt,
                now: now)
        }
    }

    private func saveWeeklyAnchorRecord(_ record: WeeklyAnchorRecord, profileID: Int) {
        weeklyAnchorRecords[profileID] = record
        weeklyAnchorStore.saveRecords(weeklyAnchorRecords)
    }

    private func startWeeklyAnchor(
        profileID: Int,
        expectedResetAt: Date?,
        now: Date)
    {
        guard weeklyAnchorTasks[profileID] == nil,
              isWeeklyAnchorEnabled(profileID: profileID)
        else { return }

        var record = weeklyAnchorRecords[profileID] ?? .empty
        record.lastAttemptAt = now
        record.lastError = nil
        saveWeeklyAnchorRecord(record, profileID: profileID)
        weeklyAnchorRunningProfileIDs.insert(profileID)

        weeklyAnchorTasks[profileID] = Task { [weak self] in
            guard let self else { return }
            do {
                _ = try await self.weeklyAnchorService.send(profileID: profileID)
                let completedAt = Date()
                var completed = self.weeklyAnchorRecords[profileID] ?? .empty
                let observedNextResetAt = self.usageStates[profileID]?.snapshot?.weekly?.resetsAt
                completed.lastHandledResetAt = expectedResetAt
                completed.lastSuccessAt = completedAt
                completed.nextResetAt = observedNextResetAt.flatMap { $0 > completedAt ? $0 : nil }
                completed.resetDriftCandidateAt = nil
                completed.resetDriftObservationCount = nil
                completed.lastError = nil
                self.saveWeeklyAnchorRecord(completed, profileID: profileID)
                self.loginRequiredProfileIDs.remove(profileID)
                self.weeklyAnchorRunningProfileIDs.remove(profileID)
                self.weeklyAnchorTasks[profileID] = nil
                let alias = self.profiles.first(where: { $0.id == profileID })?.alias ?? "계정 \(profileID)"
                self.showTransientBanner(
                    style: .success,
                    message: "\(alias) 주간 주기 시작 메시지를 보냈습니다.")
                try? await Task.sleep(nanoseconds: 2_000_000_000)
                guard !Task.isCancelled else { return }
                await self.refresh(profileID: profileID)
            } catch {
                var failed = self.weeklyAnchorRecords[profileID] ?? .empty
                failed.lastError = error.localizedDescription
                self.saveWeeklyAnchorRecord(failed, profileID: profileID)
                self.weeklyAnchorRunningProfileIDs.remove(profileID)
                self.weeklyAnchorTasks[profileID] = nil
                if AuthenticationFailureClassifier.requiresCanonicalReauthentication(error) {
                    self.loginRequiredProfileIDs.insert(profileID)
                    let alias = self.profiles.first(where: { $0.id == profileID })?.alias ?? "계정 \(profileID)"
                    self.showTransientBanner(
                        style: .warning,
                        message: "\(alias) 계정은 재로그인이 필요합니다. 설정 > 계정에서 로그인해 주세요.",
                        dismissAfterNanoseconds: 8_000_000_000)
                } else {
                    self.showTransientBanner(
                        style: .warning,
                        message: "주간 주기 시작 메시지 전송 실패: \(error.localizedDescription)",
                        dismissAfterNanoseconds: 6_000_000_000)
                }
            }
        }
    }

    func beginLogin(profileID: Int? = nil) {
        guard configurationError == nil, !hasControllerTransaction else {
            banner = AppBanner(style: .error, message: configurationMutationBlockMessage)
            return
        }
        if let profileID { selectProfile(profileID) }
        guard !profileManagementRecoveryNeeded else {
            banner = AppBanner(style: .error, message: "중단된 계정 위치 변경을 먼저 복구해야 합니다.")
            return
        }
        if let loginWindowController {
            if loginProfileID == selectedProfileID {
                loginWindowController.present()
                return
            }
            loginCoordinator.cancel(silent: true)
            loginWindowController.window?.close()
            self.loginWindowController = nil
            loginProfileID = nil
        }

        let profile = selectedProfile
        loginProfileID = profile.id
        let controller = LoginWindowController(
            coordinator: loginCoordinator,
            profile: profile,
            onDismiss: { [weak self] in
                guard let self else { return }
                self.loginWindowController = nil
                if self.loginProfileID == profile.id {
                    self.loginProfileID = nil
                    self.removeEmptyReservedAccount(profile.id)
                }
            })
        loginWindowController = controller
        controller.present()
        loginCoordinator.start(profileID: profile.id)
    }

    func addAccount() {
        guard configurationError == nil, !profileManagementRecoveryNeeded,
              !hasControllerTransaction,
              !isManagingProfiles, !isSwitching, !isMaintainingAuth,
              loginWindowController == nil
        else {
            banner = AppBanner(style: .warning, message: configurationMutationBlockMessage)
            return
        }
        do {
            let account = try withControllerMutationLock {
                try configurationStore.reserveAccount()
            }
            pendingNewProfileIDs.insert(account.id)
            reloadConfiguration(selecting: account.id)
            beginLogin(profileID: account.id)
        } catch {
            banner = AppBanner(style: .error, message: "계정 추가 실패: \(error.localizedDescription)")
        }
    }

    func moveAccounts(from source: IndexSet, to destination: Int) {
        guard configurationError == nil, !profileManagementRecoveryNeeded,
              !hasControllerTransaction,
              !isManagingProfiles, !isSwitching, !isMaintainingAuth
        else {
            banner = AppBanner(style: .warning, message: configurationMutationBlockMessage)
            return
        }
        var reordered = profiles
        reordered.move(fromOffsets: source, toOffset: destination)
        do {
            try withControllerMutationLock {
                try configurationStore.reorderAccounts(ids: reordered.map(\.id))
            }
            _ = reloadConfiguration()
        } catch {
            banner = AppBanner(style: .error, message: "계정 순서 저장 실패: \(error.localizedDescription)")
        }
    }

    func updateAccountAlias(profileID: Int, alias: String) {
        guard configurationError == nil, !profileManagementRecoveryNeeded,
              !hasControllerTransaction,
              !isManagingProfiles, !isSwitching, !isMaintainingAuth
        else {
            banner = AppBanner(style: .warning, message: configurationMutationBlockMessage)
            return
        }
        do {
            try withControllerMutationLock {
                try configurationStore.updateAccountAlias(id: profileID, alias: alias)
            }
            _ = reloadConfiguration()
            banner = AppBanner(style: .success, message: "계정 별칭을 저장했습니다.")
        } catch {
            banner = AppBanner(style: .error, message: error.localizedDescription)
        }
    }

    @discardableResult
    private func reloadConfiguration(selecting profileID: Int? = nil) -> Bool {
        do {
            let configuration = try configurationStore.load()
            guard !configuration.accounts.isEmpty else {
                throw AppError.processFailed("등록 계정이 하나도 없습니다.")
            }
            profiles = configuration.accounts
            configuredDevices = configuration.devices
            configurationError = nil
            for profile in profiles where usageStates[profile.id] == nil {
                usageStates[profile.id] = .idle
            }
            usageStates = usageStates.filter { id, _ in profiles.contains(where: { $0.id == id }) }
            let validProfileIDs = Set(profiles.map(\.id))
            loginRequiredProfileIDs.formIntersection(validProfileIDs)
            var normalizedPreferences = weeklyAnchorPreferences
            normalizedPreferences.enabledProfileIDs.formIntersection(validProfileIDs)
            if normalizedPreferences != weeklyAnchorPreferences {
                weeklyAnchorPreferences = normalizedPreferences
                weeklyAnchorStore.savePreferences(normalizedPreferences)
            }
            let normalizedRecords = weeklyAnchorRecords.filter { validProfileIDs.contains($0.key) }
            if normalizedRecords != weeklyAnchorRecords {
                weeklyAnchorRecords = normalizedRecords
                weeklyAnchorStore.saveRecords(normalizedRecords)
            }
            for id in weeklyAnchorTasks.keys.filter({ !validProfileIDs.contains($0) }) {
                weeklyAnchorTasks[id]?.cancel()
                weeklyAnchorTasks[id] = nil
                weeklyAnchorRunningProfileIDs.remove(id)
            }
            let desired = profileID ?? selectedProfileID
            selectedProfileID = profiles.contains(where: { $0.id == desired })
                ? desired
                : (profiles.first?.id ?? 0)
            if selectedProfileID > 0 {
                UserDefaults.standard.set(selectedProfileID, forKey: "selectedProfileID")
            }
            return true
        } catch {
            failClosedForConfiguration(error)
            return false
        }
    }

    private func removeEmptyReservedAccount(_ profileID: Int) {
        guard pendingNewProfileIDs.contains(profileID) else { return }
        Task {
            if hasControllerTransaction {
                scheduleControllerReconciliation()
                return
            }
            pendingNewProfileIDs.remove(profileID)
            if !(await authStore.profileArtifactExists(for: profileID)) {
                do {
                    try withControllerMutationLock {
                        try configurationStore.removeAccount(id: profileID)
                    }
                    _ = reloadConfiguration()
                } catch {
                    scheduleControllerReconciliation()
                }
            }
        }
    }

    func switchAll(to profileID: Int) async {
        guard configurationError == nil, !hasControllerTransaction,
              !isSwitching, !isMaintainingAuth, !isManagingProfiles,
              !profileManagementRecoveryNeeded
        else {
            banner = AppBanner(style: .warning, message: configurationMutationBlockMessage)
            return
        }
        isSwitching = true
        banner = AppBanner(style: .info, message: "\(configuredNodeCount)대 장비를 안전하게 전환하고 있습니다…")
        do {
            _ = try await switchService.switchAll(to: profileID)
            await refreshDeviceStatus()
            await refresh(profileID: profileID)
            let email = profiles.first(where: { $0.id == profileID })?.email ?? "계정 \(profileID)"
            banner = AppBanner(style: .success, message: "\(configuredNodeCount)대 장비가 모두 \(email) 계정으로 전환되었습니다.")
        } catch {
            await refreshDeviceStatus()
            banner = AppBanner(style: .error, message: "전환 실패: \(error.localizedDescription)")
        }
        isSwitching = false
        scheduleQueuedForcedSyncIfPossible()
    }

    func logout(profileID: Int) async {
        guard configurationError == nil, !hasControllerTransaction,
              !isRefreshing, !isSwitching, !isMaintainingAuth, !isManagingProfiles,
              !profileManagementRecoveryNeeded, loginWindowController == nil
        else {
            banner = AppBanner(style: .warning, message: configurationMutationBlockMessage)
            return
        }
        isManagingProfiles = true
        guard let profile = profile(for: profileID) else {
            banner = AppBanner(style: .error, message: "등록되지 않은 계정은 로그아웃할 수 없습니다.")
            isManagingProfiles = false
            return
        }
        var fallbackProfileID: Int?
        for candidate in profiles where candidate.id != profileID {
            if (try? await authStore.credentials(for: candidate.id)) != nil {
                fallbackProfileID = candidate.id
                break
            }
        }
        guard let fallbackProfileID else {
            banner = AppBanner(style: .warning, message: "다른 로그인 계정이 있어야 안전하게 로그아웃할 수 있습니다.")
            isManagingProfiles = false
            return
        }
        if browserCleanupPendingProfileIDs.contains(profileID) {
            let stillHasCredential = (try? await authStore.credentials(for: profileID)) != nil
            if !stillHasCredential {
                banner = AppBanner(style: .info, message: "\(profile.alias) Chromium 로그인 데이터를 다시 정리하고 있습니다…")
                do {
                    try await loginCoordinator.clearBrowserProfile(profileID: profileID)
                    setBrowserCleanupPending(false, profileID: profileID)
                    banner = AppBanner(style: .success, message: "\(profile.alias) Chromium 로그인 데이터를 정리했습니다.")
                } catch {
                    setBrowserCleanupPending(true, profileID: profileID)
                    banner = AppBanner(style: .warning, message: "Chromium 데이터 정리 재시도 실패: \(error.localizedDescription)")
                }
                isManagingProfiles = false
                scheduleQueuedForcedSyncIfPossible()
                return
            }
        }
        setBrowserCleanupPending(true, profileID: profileID)
        banner = AppBanner(
            style: .info,
            message: "\(configuredNodeCount)대 장비를 다른 계정으로 전환한 뒤 \(profile.email) 연결을 해제하고 있습니다…")
        do {
            let logoutResult = try await switchService.logoutProfile(
                profileID,
                fallbackProfileID: fallbackProfileID)
            var browserWarning: String?
            do {
                try await loginCoordinator.clearBrowserProfile(profileID: profileID)
                setBrowserCleanupPending(false, profileID: profileID)
            } catch {
                browserWarning = error.localizedDescription
                setBrowserCleanupPending(true, profileID: profileID)
            }
            usageStates[profileID] = .failed(
                previous: nil,
                message: "로그아웃되었습니다.",
                loginRequired: true)
            loginRequiredProfileIDs.insert(profileID)
            await refreshDeviceStatus()
            await refresh(
                profileID: profileID,
                allowDuringProfileManagement: true)
            if let browserWarning {
                banner = AppBanner(
                    style: .warning,
                    message: "인증 연결은 해제했지만 Chromium 데이터 정리가 필요합니다: \(browserWarning)")
            } else if logoutResult.isPartialCleanup {
                banner = AppBanner(
                    style: .warning,
                    message: "로그아웃은 완료했습니다. 연결되지 않은 장비의 보호된 임시 백업 정리는 다음 인증 점검에서 자동 재시도합니다.")
            } else {
                banner = AppBanner(
                    style: .success,
                    message: "\(profile.email) 계정을 \(configuredNodeCount)대 장비에서 로그아웃했습니다.")
            }
        } catch {
            if (try? await authStore.credentials(for: profileID)) != nil {
                setBrowserCleanupPending(false, profileID: profileID)
            }
            await refreshDeviceStatus()
            banner = AppBanner(style: .error, message: "로그아웃 실패: \(error.localizedDescription)")
        }
        isManagingProfiles = false
        scheduleQueuedForcedSyncIfPossible()
    }

    func removeAccount(profileID: Int) async {
        guard configurationError == nil, !profileManagementRecoveryNeeded,
              !hasControllerTransaction, !isManagingProfiles
        else {
            banner = AppBanner(style: .warning, message: configurationMutationBlockMessage)
            return
        }
        if await authStore.profileArtifactExists(for: profileID) {
            banner = AppBanner(style: .warning, message: "설정에서 먼저 이 계정을 로그아웃해 주세요.")
            return
        }
        isManagingProfiles = true
        defer { isManagingProfiles = false }
        do {
            try await loginCoordinator.clearBrowserProfile(profileID: profileID)
            try withControllerMutationLock {
                try configurationStore.removeAccount(id: profileID)
            }
            _ = reloadConfiguration()
            banner = AppBanner(style: .success, message: "계정 항목을 제거했습니다.")
        } catch {
            banner = AppBanner(style: .error, message: "계정 제거 실패: \(error.localizedDescription)")
        }
    }

    func saveDevice(
        _ draft: SSHDeviceConfiguration,
        password: String,
        passphrase: String,
        clearPassword: Bool = false,
        clearPassphrase: Bool = false) throws
    {
        guard configurationError == nil, !profileManagementRecoveryNeeded,
              !hasControllerTransaction,
              !isManagingProfiles, !isSwitching, !isMaintainingAuth else {
            throw AppError.processFailed("진행 중인 계정 작업이 끝난 뒤 장치 설정을 저장해 주세요.")
        }
        try withControllerMutationLock {
        let existing = configuredDevices.first(where: { $0.id == draft.id })
        var device = draft
        if existing == nil { device.enabled = false }

        let oldCredentialID = existing.map(secretNamespace(for:))
        let endpointChanged = existing.map { old in
            !old.hasSameCredentialEndpoint(as: device)
        } ?? true
        let secretMutation = !password.isEmpty
            || !passphrase.isEmpty
            || clearPassword
            || clearPassphrase
        let requiresReactivation = existing.map {
            device.requiresActivationValidation(
                replacing: $0,
                secretWasMutated: secretMutation)
        } ?? false
        if existing?.enabled == true, requiresReactivation {
            // A new host/auth/key/secret is a new trust boundary. Persist it
            // disabled so the only route back into sync/logout rosters is the
            // bootstrap + post-enable verification transaction.
            device.enabled = false
        }
        if existing?.enabled == false, device.enabled {
            throw AppError.processFailed("장치 목록의 ‘설치 및 활성화’로 먼저 연결을 검증해 주세요.")
        }
        if endpointChanged,
           existing?.hasKeyPassphrase == true,
           device.authentication == .privateKey,
           passphrase.isEmpty,
           !clearPassphrase
        {
            throw AppError.processFailed(
                "SSH 엔드포인트나 키를 변경할 때는 키 암호를 다시 입력하거나 저장된 키 암호 삭제를 선택해 주세요.")
        }
        let rotateCredential = existing == nil
            || existing?.credentialID == nil
            || endpointChanged
            || !password.isEmpty
            || !passphrase.isEmpty
            || clearPassword
            || clearPassphrase
        if rotateCredential {
            device.credentialID = UUID()
        } else {
            // Never trust a stale editor snapshot after another secret
            // rotation; the live registry owns the active namespace.
            device.credentialID = existing?.credentialID
        }
        guard let credentialID = device.credentialID?.uuidString else {
            throw AppError.processFailed("SSH Keychain 식별자를 만들지 못했습니다.")
        }

        let canReuseOldSecrets = !endpointChanged && !clearPassword && !clearPassphrase
        let resolvedPassword: String?
        if clearPassword || device.authentication != .password {
            resolvedPassword = nil
        } else if !password.isEmpty {
            resolvedPassword = password
        } else if canReuseOldSecrets, let oldCredentialID {
            resolvedPassword = try secretStore.read(credentialID: oldCredentialID, kind: .password)
        } else {
            resolvedPassword = nil
        }

        let resolvedPassphrase: String?
        if clearPassphrase || device.authentication != .privateKey {
            resolvedPassphrase = nil
        } else if !passphrase.isEmpty {
            resolvedPassphrase = passphrase
        } else if canReuseOldSecrets, let oldCredentialID {
            resolvedPassphrase = try secretStore.read(credentialID: oldCredentialID, kind: .passphrase)
        } else {
            resolvedPassphrase = nil
        }

        device.hasPassword = resolvedPassword != nil
        device.hasKeyPassphrase = resolvedPassphrase != nil
        try device.validate()

        var stagedCredential = false
        let credentialIntentURL: URL?
        if rotateCredential {
            credentialIntentURL = try writeCredentialMutationIntent(
                deviceID: device.id,
                oldCredentialID: oldCredentialID,
                newCredentialID: credentialID)
        } else {
            credentialIntentURL = nil
        }
        do {
            if rotateCredential {
                stagedCredential = true
                if let resolvedPassword {
                    try secretStore.save(resolvedPassword, credentialID: credentialID, kind: .password)
                }
                if let resolvedPassphrase {
                    try secretStore.save(resolvedPassphrase, credentialID: credentialID, kind: .passphrase)
                }
            }
            _ = try configurationStore.upsertDevice(device)
        } catch {
            var stagedCleanupSucceeded = true
            if stagedCredential {
                do {
                    try deleteSecretNamespace(credentialID)
                } catch {
                    stagedCleanupSucceeded = false
                }
            }
            if stagedCleanupSucceeded, let credentialIntentURL {
                try? FileManager.default.removeItem(at: credentialIntentURL)
            }
            throw error
        }

        var cleanupWarning = false
        if let oldCredentialID, oldCredentialID != credentialID {
            queueSecretCleanup(oldCredentialID)
            do {
                try deleteSecretNamespace(oldCredentialID)
                dequeueSecretCleanup(oldCredentialID)
            } catch {
                cleanupWarning = true
            }
        }
        if !cleanupWarning, let credentialIntentURL {
            try? FileManager.default.removeItem(at: credentialIntentURL)
        }
        _ = reloadConfiguration()
        banner = AppBanner(
            style: cleanupWarning ? .warning : .success,
            message: cleanupWarning
                ? "SSH 설정은 저장했습니다. 이전 Keychain 항목은 다음 실행 때 다시 정리합니다."
                : (requiresReactivation
                    ? "SSH 장치 설정을 저장하고 비활성화했습니다. ‘설치 및 활성화’로 새 연결을 검증해 주세요."
                    : "SSH 장치 설정을 저장했습니다."))
        }
    }

    func testDevice(id: String) async {
        guard configurationError == nil else {
            banner = AppBanner(style: .error, message: configurationMutationBlockMessage)
            return
        }
        do {
            _ = try await switchService.testDevice(id: id)
            showTransientBanner(
                style: .success,
                message: "SSH 연결과 helper 버전을 확인했습니다.")
            await refreshDeviceStatus()
        } catch {
            banner = AppBanner(style: .error, message: "SSH 연결 테스트 실패: \(error.localizedDescription)")
        }
    }

    private func showTransientBanner(
        style: BannerStyle,
        message: String,
        dismissAfterNanoseconds: UInt64 = 4_000_000_000)
    {
        let nextBanner = AppBanner(style: style, message: message)
        bannerDismissTask?.cancel()
        banner = nextBanner
        bannerDismissTask = Task { [weak self] in
            try? await Task.sleep(nanoseconds: dismissAfterNanoseconds)
            guard !Task.isCancelled, self?.banner?.id == nextBanner.id else { return }
            withAnimation(.easeOut(duration: 0.18)) { self?.banner = nil }
        }
    }

    func bootstrapDevice(id: String) async {
        guard configurationError == nil, !profileManagementRecoveryNeeded,
              !hasControllerTransaction,
              !isManagingProfiles, !isSwitching, !isMaintainingAuth,
              let device = configuredDevices.first(where: { $0.id == id })
        else {
            banner = AppBanner(style: .warning, message: configurationMutationBlockMessage)
            return
        }
        isManagingProfiles = true
        banner = AppBanner(
            style: .info,
            message: "\(device.displayName)에 helper와 등록 계정을 안전하게 설치하고 있습니다…")
        let expectedActiveProfileID = activeProfileID
        defer {
            isManagingProfiles = false
            scheduleQueuedForcedSyncIfPossible()
        }
        var activationIntentURL: URL?
        do {
            let bootstrap = try await switchService.bootstrapDevice(id: id)
            guard expectedActiveProfileID == nil
                    || bootstrap.activeProfileID == expectedActiveProfileID
            else {
                throw AppError.processFailed("활성화 전 장치 계정 상태 검증에 실패했습니다.")
            }

            activationIntentURL = try withControllerMutationLock {
                let intentURL = try writeDeviceActivationIntent(originalDevice: device)
                do {
                    try configurationStore.beginDeviceActivation(device)
                    return intentURL
                } catch {
                    try? FileManager.default.removeItem(at: intentURL)
                    throw error
                }
            }
            guard reloadConfiguration() else {
                throw AppError.processFailed("활성화된 장치 설정을 다시 읽지 못했습니다.")
            }

            let verifiedStatuses = try await switchService.fetchStatus()
            guard SwitchService.bootstrapActivationIsConsistent(
                statuses: verifiedStatuses,
                deviceID: id,
                activeProfileID: bootstrap.activeProfileID) else {
                throw AppError.processFailed("활성화 후 SSH 장치 상태를 확인하지 못했습니다.")
            }
            devices = verifiedStatuses
            try withControllerMutationLock {
                guard let activationIntentURL else {
                    throw AppError.processFailed("SSH 장치 활성화 복구 기록을 찾지 못했습니다.")
                }
                try FileManager.default.removeItem(at: activationIntentURL)
            }
            activationIntentURL = nil
            banner = AppBanner(
                style: .success,
                message: "\(device.displayName) 설치와 계정 동기화를 확인하고 활성화했습니다.")
        } catch {
            var recoveryError: Error?
            if activationIntentURL != nil {
                do {
                    try withControllerMutationLock {
                        try configurationStore.rollbackDeviceActivation(device)
                        if let activationIntentURL {
                            try FileManager.default.removeItem(at: activationIntentURL)
                        }
                    }
                    activationIntentURL = nil
                    _ = reloadConfiguration()
                } catch {
                    recoveryError = error
                }
            }
            if let recoveryError {
                failClosedForConfiguration(recoveryError)
                banner = AppBanner(
                    style: .error,
                    message: "장치 활성화 복구가 대기 중입니다. 앱을 다시 열어 복구해 주세요: \(recoveryError.localizedDescription)")
            } else {
                banner = AppBanner(
                    style: .error,
                    message: "장치 설치 실패 — 설정은 비활성 상태로 유지했습니다: \(error.localizedDescription)")
            }
        }
    }

    func removeDevice(id: String) {
        guard configurationError == nil, !profileManagementRecoveryNeeded,
              !hasControllerTransaction,
              !isManagingProfiles, !isSwitching, !isMaintainingAuth else {
            banner = AppBanner(style: .warning, message: "진행 중인 계정 작업이 끝난 뒤 장치를 제거해 주세요.")
            return
        }
        do {
            try withControllerMutationLock {
                let credentialID = configuredDevices
                    .first(where: { $0.id == id })
                    .map(secretNamespace(for:))
                let intentURL = try credentialID.map {
                    try writeCredentialMutationIntent(
                        deviceID: id,
                        oldCredentialID: nil,
                        newCredentialID: $0)
                }
                do {
                    try configurationStore.removeDevice(id: id)
                } catch {
                    if let intentURL { try? FileManager.default.removeItem(at: intentURL) }
                    throw error
                }
                var cleanupWarning = false
                if let credentialID {
                    do {
                        try deleteSecretNamespace(credentialID)
                        if let intentURL { try FileManager.default.removeItem(at: intentURL) }
                    } catch {
                        cleanupWarning = true
                    }
                }
                _ = reloadConfiguration()
                banner = AppBanner(
                    style: cleanupWarning ? .warning : .success,
                    message: cleanupWarning
                        ? "SSH 장치를 제거했습니다. Keychain 항목은 다음 실행 때 다시 정리합니다."
                        : "SSH 장치를 제거했습니다.")
            }
        } catch {
            banner = AppBanner(style: .error, message: "SSH 장치 제거 실패: \(error.localizedDescription)")
        }
    }

    func profile(for id: Int) -> AccountProfile? {
        profiles.first(where: { $0.id == id })
    }

    func isBrowserCleanupPending(profileID: Int) -> Bool {
        browserCleanupPendingProfileIDs.contains(profileID)
    }

    private func setBrowserCleanupPending(_ pending: Bool, profileID: Int) {
        if pending {
            browserCleanupPendingProfileIDs.insert(profileID)
        } else {
            browserCleanupPendingProfileIDs.remove(profileID)
        }
        UserDefaults.standard.set(
            pending,
            forKey: browserCleanupDefaultsKey(profileID: profileID))
    }

    private func browserCleanupDefaultsKey(profileID: Int) -> String {
        "browserCleanupPending.profile\(profileID)"
    }

    private func secretNamespace(for device: SSHDeviceConfiguration) -> String {
        device.credentialID?.uuidString ?? device.id
    }

    private func deleteSecretNamespace(_ identifier: String) throws {
        try secretStore.delete(credentialID: identifier, kind: .password)
        try secretStore.delete(credentialID: identifier, kind: .passphrase)
    }

    private func queueSecretCleanup(_ identifier: String) {
        var queued = Set(UserDefaults.standard.stringArray(
            forKey: pendingSecretCleanupDefaultsKey) ?? [])
        queued.insert(identifier)
        UserDefaults.standard.set(queued.sorted(), forKey: pendingSecretCleanupDefaultsKey)
    }

    private func dequeueSecretCleanup(_ identifier: String) {
        var queued = Set(UserDefaults.standard.stringArray(
            forKey: pendingSecretCleanupDefaultsKey) ?? [])
        queued.remove(identifier)
        UserDefaults.standard.set(queued.sorted(), forKey: pendingSecretCleanupDefaultsKey)
    }

    private func cleanupPendingSecretNamespaces() {
        let queued = UserDefaults.standard.stringArray(
            forKey: pendingSecretCleanupDefaultsKey) ?? []
        for identifier in queued {
            do {
                try deleteSecretNamespace(identifier)
                dequeueSecretCleanup(identifier)
            } catch {
                // The identifier stays queued for the next application start.
            }
        }
    }

    private var credentialMutationDirectory: URL {
        FileManager.default.homeDirectoryForCurrentUser
            .appendingPathComponent(".local/share/gpt-switch/credential-transactions", isDirectory: true)
    }

    private var deviceActivationDirectory: URL {
        FileManager.default.homeDirectoryForCurrentUser
            .appendingPathComponent(".local/share/gpt-switch/device-activation-transactions", isDirectory: true)
    }

    private func writeDeviceActivationIntent(
        originalDevice: SSHDeviceConfiguration) throws -> URL
    {
        let fileManager = FileManager.default
        let directory = deviceActivationDirectory
        try ensureSecureTransactionDirectory(directory, label: "SSH 장치 활성화")
        let intent = SSHDeviceActivationIntent(id: UUID(), originalDevice: originalDevice)
        let destination = directory.appendingPathComponent("\(intent.id.uuidString).json")
        let temporary = directory.appendingPathComponent(".\(intent.id.uuidString).tmp")
        defer { try? fileManager.removeItem(at: temporary) }
        // This is an unpublished staging path inside a mode-0700 directory;
        // atomicity is provided by the final rename. Avoid a second hidden
        // Foundation temp file that could survive a process crash.
        try JSONEncoder().encode(intent).write(to: temporary, options: [.withoutOverwriting])
        try fileManager.setAttributes([.posixPermissions: 0o600], ofItemAtPath: temporary.path)
        try fileManager.moveItem(at: temporary, to: destination)
        return destination
    }

    private func recoverDeviceActivationIntents() throws {
        let fileManager = FileManager.default
        let directory = deviceActivationDirectory
        guard pathEntryExists(directory) else { return }
        try ensureSecureTransactionDirectory(directory, label: "SSH 장치 활성화")
        let entries = try fileManager.contentsOfDirectory(
            at: directory,
            includingPropertiesForKeys: [.isRegularFileKey, .isSymbolicLinkKey],
            options: [])
        for entry in entries {
            if entry.lastPathComponent.hasPrefix(".") {
                let pattern = #"^\.[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}\.tmp$"#
                let values = try entry.resourceValues(forKeys: [.isRegularFileKey, .isSymbolicLinkKey])
                let attributes = try fileManager.attributesOfItem(atPath: entry.path)
                guard entry.lastPathComponent.range(of: pattern, options: .regularExpression) != nil,
                      values.isRegularFile == true,
                      values.isSymbolicLink != true,
                      (attributes[.ownerAccountID] as? NSNumber)?.uint32Value == getuid()
                else {
                    throw AppError.processFailed("안전하지 않은 SSH 장치 활성화 임시 파일이 있습니다.")
                }
                // The configuration is not enabled until after this staging
                // file is renamed to a visible intent, so a leftover temp is
                // always safe to discard during startup recovery.
                try fileManager.removeItem(at: entry)
                continue
            }
            guard entry.pathExtension == "json" else {
                throw AppError.processFailed("알 수 없는 SSH 장치 활성화 트랜잭션 파일이 있습니다.")
            }
            try validateSecureTransactionFile(entry, label: "SSH 장치 활성화")
            let intent = try JSONDecoder().decode(
                SSHDeviceActivationIntent.self,
                from: Data(contentsOf: entry))
            try configurationStore.rollbackDeviceActivation(intent.originalDevice)
            try fileManager.removeItem(at: entry)
        }
    }

    private func ensureSecureTransactionDirectory(_ directory: URL, label: String) throws {
        let fileManager = FileManager.default
        if pathEntryExists(directory) {
            let values = try directory.resourceValues(forKeys: [.isDirectoryKey, .isSymbolicLinkKey])
            let attributes = try fileManager.attributesOfItem(atPath: directory.path)
            guard values.isDirectory == true,
                  values.isSymbolicLink != true,
                  (attributes[.ownerAccountID] as? NSNumber)?.uint32Value == getuid()
            else {
                throw AppError.processFailed("\(label) 트랜잭션 경로가 안전하지 않습니다.")
            }
            try fileManager.setAttributes([.posixPermissions: 0o700], ofItemAtPath: directory.path)
        } else {
            try fileManager.createDirectory(
                at: directory,
                withIntermediateDirectories: true,
                attributes: [.posixPermissions: 0o700])
        }
    }

    private func validateSecureTransactionFile(_ entry: URL, label: String) throws {
        let values = try entry.resourceValues(forKeys: [.isRegularFileKey, .isSymbolicLinkKey])
        let attributes = try FileManager.default.attributesOfItem(atPath: entry.path)
        guard values.isRegularFile == true,
              values.isSymbolicLink != true,
              (attributes[.posixPermissions] as? NSNumber)?.intValue == 0o600,
              (attributes[.ownerAccountID] as? NSNumber)?.uint32Value == getuid()
        else {
            throw AppError.processFailed("안전하지 않은 \(label) 트랜잭션 파일이 있습니다.")
        }
    }

    private func pathEntryExists(_ url: URL) -> Bool {
        var info = stat()
        return url.path.withCString { lstat($0, &info) } == 0
    }

    private func writeCredentialMutationIntent(
        deviceID: String,
        oldCredentialID: String?,
        newCredentialID: String) throws -> URL
    {
        let fileManager = FileManager.default
        let directory = credentialMutationDirectory
        if fileManager.fileExists(atPath: directory.path) {
            let values = try directory.resourceValues(forKeys: [.isDirectoryKey, .isSymbolicLinkKey])
            guard values.isDirectory == true, values.isSymbolicLink != true else {
                throw AppError.processFailed("SSH credential 트랜잭션 경로가 안전하지 않습니다.")
            }
        } else {
            try fileManager.createDirectory(
                at: directory,
                withIntermediateDirectories: true,
                attributes: [.posixPermissions: 0o700])
        }
        let intent = SSHCredentialMutationIntent(
            id: UUID(),
            deviceID: deviceID,
            oldCredentialID: oldCredentialID,
            newCredentialID: newCredentialID)
        let destination = directory.appendingPathComponent("\(intent.id.uuidString).json")
        let temporary = directory.appendingPathComponent(".\(intent.id.uuidString).tmp")
        defer { try? fileManager.removeItem(at: temporary) }
        try JSONEncoder().encode(intent).write(to: temporary, options: [.atomic])
        try fileManager.setAttributes([.posixPermissions: 0o600], ofItemAtPath: temporary.path)
        try fileManager.moveItem(at: temporary, to: destination)
        return destination
    }

    private func recoverCredentialMutationIntents() throws {
        let fileManager = FileManager.default
        let directory = credentialMutationDirectory
        guard fileManager.fileExists(atPath: directory.path) else { return }
        let directoryValues = try directory.resourceValues(forKeys: [.isDirectoryKey, .isSymbolicLinkKey])
        guard directoryValues.isDirectory == true, directoryValues.isSymbolicLink != true else {
            throw AppError.processFailed("SSH credential 트랜잭션 경로가 안전하지 않습니다.")
        }
        let configuration = try configurationStore.load()
        let entries = try fileManager.contentsOfDirectory(
            at: directory,
            includingPropertiesForKeys: [.isRegularFileKey, .isSymbolicLinkKey],
            options: [.skipsHiddenFiles])
        for entry in entries where entry.pathExtension == "json" {
            let values = try entry.resourceValues(forKeys: [.isRegularFileKey, .isSymbolicLinkKey])
            guard values.isRegularFile == true, values.isSymbolicLink != true else {
                throw AppError.processFailed("안전하지 않은 SSH credential 트랜잭션 파일이 있습니다.")
            }
            let attributes = try fileManager.attributesOfItem(atPath: entry.path)
            guard (attributes[.posixPermissions] as? NSNumber)?.intValue == 0o600,
                  (attributes[.ownerAccountID] as? NSNumber)?.uint32Value == getuid()
            else {
                throw AppError.processFailed("SSH credential 트랜잭션 파일 권한이 안전하지 않습니다.")
            }
            let intent = try JSONDecoder().decode(
                SSHCredentialMutationIntent.self,
                from: Data(contentsOf: entry))
            let activeCredentialID = configuration.devices
                .first(where: { $0.id == intent.deviceID })
                .map(secretNamespace(for:))
            if activeCredentialID == intent.newCredentialID {
                if let oldCredentialID = intent.oldCredentialID,
                   oldCredentialID != intent.newCredentialID
                {
                    try deleteSecretNamespace(oldCredentialID)
                }
            } else {
                try deleteSecretNamespace(intent.newCredentialID)
            }
            try fileManager.removeItem(at: entry)
        }
    }

    private var profileSwapJournalURL: URL {
        FileManager.default.homeDirectoryForCurrentUser
            .appendingPathComponent("Library/Application Support/Codex SyncBar", isDirectory: true)
            .appendingPathComponent("profile-swap-journal.json")
    }

    private func writeProfileSwapJournal(_ journal: ProfileSwapJournal) throws {
        let fileManager = FileManager.default
        let directory = profileSwapJournalURL.deletingLastPathComponent()
        if fileManager.fileExists(atPath: directory.path) {
            let values = try directory.resourceValues(forKeys: [.isSymbolicLinkKey, .isDirectoryKey])
            guard values.isSymbolicLink != true, values.isDirectory == true else {
                throw AppError.processFailed("계정 위치 변경 저널 저장소가 올바르지 않습니다.")
            }
        } else {
            try fileManager.createDirectory(
                at: directory,
                withIntermediateDirectories: true,
                attributes: [.posixPermissions: 0o700])
        }
        if fileManager.fileExists(atPath: profileSwapJournalURL.path) {
            let values = try profileSwapJournalURL.resourceValues(forKeys: [.isSymbolicLinkKey, .isRegularFileKey])
            guard values.isSymbolicLink != true, values.isRegularFile == true else {
                throw AppError.processFailed("계정 위치 변경 저널 파일이 올바르지 않습니다.")
            }
        }
        try JSONEncoder().encode(journal).write(to: profileSwapJournalURL, options: .atomic)
        try fileManager.setAttributes([.posixPermissions: 0o600], ofItemAtPath: profileSwapJournalURL.path)
    }

    private func readProfileSwapJournal() throws -> ProfileSwapJournal? {
        guard FileManager.default.fileExists(atPath: profileSwapJournalURL.path) else { return nil }
        let values = try profileSwapJournalURL.resourceValues(forKeys: [.isSymbolicLinkKey, .isRegularFileKey])
        guard values.isSymbolicLink != true, values.isRegularFile == true else {
            throw AppError.processFailed("계정 위치 변경 저널 파일이 올바르지 않습니다.")
        }
        return try JSONDecoder().decode(
            ProfileSwapJournal.self,
            from: Data(contentsOf: profileSwapJournalURL))
    }

    private func recoverProfileSwapJournalIfNeeded() async throws -> Bool {
        guard let journal = try readProfileSwapJournal() else { return false }
        try await switchService.reconcileProfileSwap(originalMap: ProfileSlotMap(
            firstFingerprint: journal.firstFingerprint,
            secondFingerprint: journal.secondFingerprint))
        let map = try await switchService.fetchLocalProfileMap()
        let authArrangement: ProfileSlotArrangement
        if map.firstFingerprint == journal.firstFingerprint,
           map.secondFingerprint == journal.secondFingerprint
        {
            authArrangement = .original
        } else if map.firstFingerprint == journal.secondFingerprint,
                  map.secondFingerprint == journal.firstFingerprint
        {
            authArrangement = .swapped
        } else {
            authArrangement = .unknown
        }
        let browserArrangement = try loginCoordinator.browserSwapArrangement(
            firstToken: journal.firstBrowserMarker,
            secondToken: journal.secondBrowserMarker)

        switch (authArrangement, browserArrangement) {
        case (.original, .original), (.swapped, .swapped):
            break
        case (.original, .swapped), (.swapped, .original):
            try await loginCoordinator.swapBrowserProfiles()
            let expected: BrowserProfileArrangement = authArrangement == .original ? .original : .swapped
            guard try loginCoordinator.browserSwapArrangement(
                firstToken: journal.firstBrowserMarker,
                secondToken: journal.secondBrowserMarker) == expected
            else { throw AppError.processFailed("Chromium 계정 위치 복구 검증에 실패했습니다.") }
        case (.original, .unknown):
            // A crash while the two markers were being prepared occurs before
            // any browser directory or auth slot can be exchanged.
            break
        default:
            throw AppError.processFailed("인증 슬롯과 Chromium 세션의 중단 상태를 자동 판별하지 못했습니다.")
        }
        try FileManager.default.removeItem(at: profileSwapJournalURL)
        return true
    }

    func toggleLaunchAtLogin() {
        do {
            if launchAtLogin {
                try SMAppService.mainApp.unregister()
            } else {
                try SMAppService.mainApp.register()
            }
            refreshLaunchAtLoginState()
        } catch {
            banner = AppBanner(style: .warning, message: "자동 시작 설정 실패: \(error.localizedDescription)")
            refreshLaunchAtLoginState()
        }
    }

    func refreshLaunchAtLoginState() {
        switch SMAppService.mainApp.status {
        case .enabled:
            launchAtLogin = true
            launchAtLoginRequiresApproval = false
            launchAtLoginStatusText = "현재 켜져 있습니다."
        case .requiresApproval:
            launchAtLogin = false
            launchAtLoginRequiresApproval = true
            launchAtLoginStatusText = "시스템 설정에서 사용자의 승인이 필요합니다."
        case .notFound:
            launchAtLogin = false
            launchAtLoginRequiresApproval = false
            launchAtLoginStatusText = "설치된 앱에서만 자동 실행을 켤 수 있습니다."
        case .notRegistered:
            launchAtLogin = false
            launchAtLoginRequiresApproval = false
            launchAtLoginStatusText = "현재 꺼져 있습니다."
        @unknown default:
            launchAtLogin = false
            launchAtLoginRequiresApproval = false
            launchAtLoginStatusText = "자동 실행 상태를 확인하지 못했습니다."
        }
    }

    func openLoginItemsSettings() {
        guard let url = URL(string: "x-apple.systempreferences:com.apple.LoginItems-Settings.extension") else { return }
        NSWorkspace.shared.open(url)
    }

    private var hasControllerTransaction: Bool {
        configurationStore.hasControllerActivity()
    }

    private func recoverControllerStateAndReload() async throws {
        try withControllerMutationLock {
            // A prepared login transaction may have published its auth file
            // before crashing. Do not commit/remove pending rows until the
            // helper has rolled that durable transaction forward or back.
            _ = try configurationStore.loadOrMigrate(
                controllerLockHeld: true,
                reconcilePending: false)
        }
        guard reloadConfiguration() else {
            throw AppError.processFailed(configurationError ?? "설정을 다시 읽지 못했습니다.")
        }

        // This helper command owns the same cross-process lock while rolling
        // back durable login transactions and completing logout intents.
        try await switchService.recoverControllerState()

        try withControllerMutationLock {
            // Durable helper recovery must see the same enabled roster that
            // existed when a logout/login transaction was staged. Only after
            // that recovery finishes may an interrupted device activation be
            // rolled back to disabled.
            try recoverDeviceActivationIntents()
            try recoverCredentialMutationIntents()
            // Credential namespaces are backfilled only after both recovery
            // formats have compared against the exact pre-crash device rows.
            _ = try configurationStore.loadOrMigrate(controllerLockHeld: true)
        }
        guard reloadConfiguration() else {
            throw AppError.processFailed(configurationError ?? "복구된 설정을 다시 읽지 못했습니다.")
        }
    }

    private func recoverLegacyLocalState() async throws {
        if try await recoverProfileSwapJournalIfNeeded() {
            banner = AppBanner(style: .success, message: "중단된 기존 계정 작업을 안전하게 복구했습니다.")
        }
        try await switchService.recoverLocalState()
        profileManagementRecoveryNeeded = false
    }

    private func shouldRetryControllerRecovery(after error: Error) -> Bool {
        ControllerMutationLock.isBusy(error)
            || SwitchService.isControllerBusy(error)
            || SwitchService.isRecoveryPending(error)
    }

    private func withControllerMutationLock<T>(_ operation: () throws -> T) throws -> T {
        try controllerMutationLock.withLock(operation)
    }

    private func scheduleControllerReconciliation() {
        controllerReconciliationTask?.cancel()
        controllerReconciliationTask = Task { [weak self] in
            guard let self else { return }
            for _ in 0 ..< 120 {
                guard !Task.isCancelled else { return }
                do {
                    try await self.recoverControllerStateAndReload()
                    try await self.recoverLegacyLocalState()
                    self.cleanupPendingSecretNamespaces()
                    await self.maintainAuthIfNeeded()
                    await self.refreshAll()
                    return
                } catch {
                    if !self.shouldRetryControllerRecovery(after: error) {
                        self.failClosedForConfiguration(error)
                        return
                    }
                }
                try? await Task.sleep(nanoseconds: 500_000_000)
            }
            self.banner = AppBanner(
                style: .warning,
                message: "로그인 또는 계정 작업이 오래 진행 중입니다. 완료 후 앱을 다시 열면 안전하게 복구합니다.")
        }
    }

    private var configurationMutationBlockMessage: String {
        if let configurationError {
            return "설정을 안전하게 읽지 못해 변경 작업을 차단했습니다: \(configurationError)"
        }
        if profileManagementRecoveryNeeded {
            return "중단된 계정 작업을 먼저 복구해야 합니다."
        }
        if hasControllerTransaction {
            return "진행 중이거나 복구 대기 중인 계정 작업이 있어 설정 변경을 잠시 차단했습니다."
        }
        return "진행 중인 작업이 끝난 뒤 다시 시도해 주세요."
    }

    private func failClosedForConfiguration(_ error: Error) {
        configurationError = error.localizedDescription
        profiles = []
        configuredDevices = []
        usageStates = [:]
        selectedProfileID = 0
        banner = AppBanner(
            style: .error,
            message: "설정을 안전하게 읽지 못해 계정·장치 변경을 차단했습니다: \(error.localizedDescription)")
    }

    func quit() {
        NSApplication.shared.terminate(nil)
    }

    private func scheduleQueuedForcedSyncIfPossible() {
        guard !isMaintainingAuth, !isSwitching, !isManagingProfiles else { return }
        if pendingForcedSyncAll {
            pendingForcedSyncAll = false
            pendingForcedSyncProfileIDs.removeAll()
            Task { [weak self] in
                await self?.maintainAuthIfNeeded(forceSync: true)
            }
            return
        }

        let queued = pendingForcedSyncProfileIDs.sorted()
        guard !queued.isEmpty else { return }
        pendingForcedSyncProfileIDs.removeAll()
        Task { [weak self] in
            guard let self else { return }
            for profileID in queued {
                await self.maintainAuthIfNeeded(forceSync: true, profileID: profileID)
            }
        }
    }
}
