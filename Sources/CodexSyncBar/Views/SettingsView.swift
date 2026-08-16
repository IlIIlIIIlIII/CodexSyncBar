import AppKit
import SwiftUI

private enum SettingsSection: String, CaseIterable, Identifiable {
    case accounts = "계정"
    case devices = "장치"
    case models = "모델"
    case general = "일반"

    var id: String { rawValue }

    var icon: String {
        switch self {
        case .accounts: "person.2.fill"
        case .devices: "network"
        case .models: "cpu.fill"
        case .general: "gearshape.fill"
        }
    }
}

enum AccountReorderLayout {
    static let rowMidpoint: CGFloat = 34

    static func destinationIndex(
        ids: [Int],
        draggedID: Int,
        targetID: Int,
        placeAfter: Bool) -> Int?
    {
        guard draggedID != targetID,
              ids.contains(draggedID),
              let targetIndex = ids.firstIndex(of: targetID)
        else { return nil }
        return targetIndex + (placeAfter ? 1 : 0)
    }
}

struct SettingsView: View {
    @ObservedObject var model: AppModel
    let readmeDetailOnly: Bool
    @Environment(\.scenePhase) private var scenePhase
    @State private var selection: SettingsSection? = .accounts
    @State private var accountToLogout: AccountProfile?
    @State private var accountDropTargetID: Int?
    @State private var draggedAccountID: Int?
    @State private var accountDragToken: String?
    @State private var deviceDraft: SSHDeviceConfiguration?
    @State private var deviceToRemove: SSHDeviceConfiguration?
    @State private var cursorModelDraft: String
    @State private var cursorPortDraft: String
    @State private var cursorAgentPathDraft: String
    @State private var cursorExposedModelIDsDraft: Set<String>
    @State private var cursorExposureDraftInitialized = false
    @State private var cursorAPIKeyDraft = ""

    init(model: AppModel, readmeDetailOnly: Bool = false) {
        self.model = model
        self.readmeDetailOnly = readmeDetailOnly
        _cursorModelDraft = State(initialValue: model.cursorBridgePreferences.model)
        _cursorPortDraft = State(initialValue: String(model.cursorBridgePreferences.port))
        _cursorAgentPathDraft = State(initialValue: model.cursorBridgePreferences.agentPath ?? "")
        _cursorExposedModelIDsDraft = State(initialValue: Set(
            model.cursorBridgePreferences.exposedModelIDs ?? []))
    }

    private var settingsMutationDisabled: Bool {
        model.managementActionsDisabled
            || model.isManagingProfiles
            || model.isManagingCursorProvider
            || model.isSwitching
            || model.isMaintainingAuth
            || model.profileManagementRecoveryNeeded
    }

    private var accountActionDisabled: Bool {
        settingsMutationDisabled || model.isRefreshing
    }

    var body: some View {
        rootContent
        .frame(minWidth: 720, minHeight: readmeDetailOnly ? 0 : 540)
        .accessibilityIdentifier("settings-root")
        .preferredColorScheme(.dark)
        .task { await model.start() }
        .onDisappear {
            model.dismissTransientBannerAfterFocusLoss()
        }
        .onAppear {
            if !model.isReadmeDemo { model.refreshLaunchAtLoginState() }
        }
        .onChange(of: scenePhase) { phase in
            if phase == .active {
                if !model.isReadmeDemo { model.refreshLaunchAtLoginState() }
            } else {
                model.dismissTransientBannerAfterFocusLoss()
            }
        }
        .sheet(item: $deviceDraft) { draft in
            DeviceEditorView(model: model, initial: draft)
        }
        .alert("계정에서 로그아웃할까요?", isPresented: Binding(
            get: { accountToLogout != nil },
            set: { if !$0 { accountToLogout = nil } }))
        {
            Button("취소", role: .cancel) { accountToLogout = nil }
            Button("모든 장비에서 로그아웃", role: .destructive) {
                guard let account = accountToLogout else { return }
                accountToLogout = nil
                Task { await model.logout(profileID: account.id) }
            }
        } message: {
            Text("다른 로그인 계정을 안전한 폴백으로 사용한 뒤 인증과 전용 Chromium 세션을 정리합니다.")
        }
        .alert("SSH 장치를 제거할까요?", isPresented: Binding(
            get: { deviceToRemove != nil },
            set: { if !$0 { deviceToRemove = nil } }))
        {
            Button("취소", role: .cancel) { deviceToRemove = nil }
            Button("제거", role: .destructive) {
                guard let device = deviceToRemove else { return }
                deviceToRemove = nil
                model.removeDevice(id: device.id)
            }
        } message: {
            Text("이 Mac의 장치 설정과 Keychain 비밀을 삭제합니다. 원격 장치의 Codex 파일은 변경하지 않습니다.")
        }
    }

    @ViewBuilder
    private var rootContent: some View {
        if readmeDetailOnly {
            settingsDetail
        } else {
            NavigationSplitView {
                List(SettingsSection.allCases, selection: $selection) { section in
                    Label(section.rawValue, systemImage: section.icon)
                        .tag(section)
                }
                .navigationSplitViewColumnWidth(min: 170, ideal: 190)
            } detail: {
                settingsDetail
            }
        }
    }

    private var settingsDetail: some View {
        ZStack {
            AppTheme.panel.ignoresSafeArea()
            Group {
                switch selection ?? .accounts {
                case .accounts: accountsPage
                case .devices: devicesPage
                case .models: modelsPage
                case .general: generalPage
                }
            }
        }
    }

    private var accountsPage: some View {
        SettingsPage(title: "계정", subtitle: "전체 이메일을 확인하고 드래그하여 표시 순서를 바꿉니다.") {
            if let banner = model.banner { BannerView(banner: banner) }
            if !model.profilesRequiringLogin.isEmpty {
                HStack(spacing: 8) {
                    Image(systemName: "person.crop.circle.badge.exclamationmark")
                    Text("재로그인 필요")
                        .fontWeight(.semibold)
                    Text(model.profilesRequiringLogin.map(\.alias).joined(separator: ", "))
                        .lineLimit(1)
                    Spacer()
                    Text("아래 재로그인 버튼을 눌러 주세요.")
                        .foregroundStyle(AppTheme.muted)
                }
                .font(.system(size: 11))
                .foregroundStyle(AppTheme.yellow)
                .padding(10)
                .background(AppTheme.yellow.opacity(0.10), in: RoundedRectangle(cornerRadius: 10))
                .overlay(RoundedRectangle(cornerRadius: 10).stroke(AppTheme.yellow.opacity(0.24)))
                .accessibilityIdentifier("reauthentication-required-summary")
            }
            Label("왼쪽 손잡이를 드래그해 표시 순서를 변경하세요.", systemImage: "line.3.horizontal")
                .font(.system(size: 11, weight: .medium))
                .foregroundStyle(AppTheme.muted)
            VStack(spacing: 0) {
                ForEach(Array(model.profiles.enumerated()), id: \.element.id) { index, profile in
                    accountRow(profile, position: index + 1)
                        .padding(.horizontal, 12)
                        .background(
                            accountDropTargetID == profile.id
                                ? AppTheme.blue.opacity(0.12)
                                : Color.clear)
                        .overlay {
                            if accountDropTargetID == profile.id {
                                RoundedRectangle(cornerRadius: 8, style: .continuous)
                                    .stroke(AppTheme.blue.opacity(0.75), lineWidth: 1.5)
                            }
                        }
                        .dropDestination(for: String.self) { accountIDs, location in
                            accountDropTargetID = nil
                            let activeDraggedID = draggedAccountID
                            let activeToken = accountDragToken
                            draggedAccountID = nil
                            accountDragToken = nil
                            guard !settingsMutationDisabled,
                                  let payload = accountIDs.first,
                                  let separator = payload.firstIndex(of: ":"),
                                  let draggedID = Int(payload[..<separator]),
                                  String(payload[payload.index(after: separator)...]) == activeToken,
                                  draggedID == activeDraggedID,
                                  let sourceIndex = model.profiles.firstIndex(where: {
                                      $0.id == draggedID
                                  }),
                                  let destination = AccountReorderLayout.destinationIndex(
                                      ids: model.profiles.map(\.id),
                                      draggedID: draggedID,
                                      targetID: profile.id,
                                      placeAfter: location.y >= AccountReorderLayout.rowMidpoint)
                            else { return false }
                            model.moveAccounts(
                                from: IndexSet(integer: sourceIndex),
                                to: destination)
                            return true
                        } isTargeted: { targeted in
                            if targeted {
                                accountDropTargetID = profile.id
                            } else if accountDropTargetID == profile.id {
                                accountDropTargetID = nil
                            }
                        }
                    if index < model.profiles.count - 1 {
                        Divider().overlay(AppTheme.border)
                    }
                }
            }
            .background(AppTheme.card, in: RoundedRectangle(cornerRadius: 12, style: .continuous))
            .overlay(
                RoundedRectangle(cornerRadius: 12, style: .continuous)
                    .stroke(AppTheme.border, lineWidth: 1))

            HStack {
                Label("순서를 바꿔도 인증 파일과 브라우저 세션은 이동하지 않습니다.", systemImage: "lock.shield.fill")
                    .font(.system(size: 11))
                    .foregroundStyle(AppTheme.muted)
                Spacer()
                Button {
                    model.addAccount()
                } label: {
                    Label("계정 추가", systemImage: "plus")
                }
                .buttonStyle(.borderedProminent)
                .disabled(accountActionDisabled)
            }
        }
    }

    private func accountRow(_ profile: AccountProfile, position: Int) -> some View {
        let usage = model.usageStates[profile.id] ?? .idle
        let authentication = model.authenticationStatus(for: profile.id)
        let cleanupPending = model.isBrowserCleanupPending(profileID: profile.id)
        return HStack(spacing: 9) {
            VStack(spacing: 1) {
                Image(systemName: "line.3.horizontal")
                    .font(.system(size: 12, weight: .semibold))
                Text("\(position)")
                    .font(.system(size: 8, weight: .bold, design: .rounded))
            }
            .foregroundStyle(AppTheme.muted)
            .frame(width: 22)
            .help("드래그하여 계정 순서 변경")
            .accessibilityLabel("순서 변경 핸들, 현재 \(position)번째")
            .accessibilityIdentifier("reorder-handle-\(profile.id)")
            .onDrag {
                let token = UUID().uuidString
                draggedAccountID = profile.id
                accountDragToken = token
                return NSItemProvider(object: "\(profile.id):\(token)" as NSString)
            } preview: {
                Label(profile.alias, systemImage: "line.3.horizontal")
                    .font(.system(size: 11, weight: .semibold))
                    .padding(8)
                    .background(AppTheme.cardRaised, in: RoundedRectangle(cornerRadius: 8))
            }

            Image(systemName: model.activeProfileID == profile.id
                ? "checkmark.circle.fill" : "person.crop.circle.fill")
                .font(.system(size: 22))
                .foregroundStyle(model.activeProfileID == profile.id ? AppTheme.green : AppTheme.blue)
            VStack(alignment: .leading, spacing: 4) {
                Text(profile.email)
                    .font(.system(size: 13, weight: .semibold))
                    .textSelection(.enabled)
                HStack(spacing: 4) {
                    Image(systemName: authentication.systemImage)
                    Text(authentication.title)
                    Text("·")
                    Text(model.activeProfileID == profile.id ? "현재 사용 중" : "계정 ID \(profile.id)")
                }
                .font(.system(size: 10, weight: authentication.needsReauthentication ? .semibold : .regular))
                .foregroundStyle(authenticationColor(authentication))
            }
            Spacer()
            AccountAliasEditor(profile: profile, disabled: settingsMutationDisabled) { alias in
                model.updateAccountAlias(profileID: profile.id, alias: alias)
            }

            Button {
                Task { await model.refresh(profileID: profile.id) }
            } label: {
                Image(systemName: "arrow.clockwise")
            }
            .help("사용량 새로고침")
            .disabled(accountActionDisabled)

            if cleanupPending {
                Button("브라우저 정리 재시도") {
                    Task { await model.logout(profileID: profile.id) }
                }
                .buttonStyle(.borderedProminent)
                .tint(AppTheme.yellow)
                .disabled(accountActionDisabled)
            } else if authentication.needsReauthentication || usage.needsLogin {
                Button("재로그인") { model.beginLogin(profileID: profile.id) }
                    .buttonStyle(.borderedProminent)
                    .tint(AppTheme.yellow)
                    .disabled(accountActionDisabled)
            } else {
                Button("로그아웃", role: .destructive) { accountToLogout = profile }
                    .disabled(model.profiles.count < 2 || accountActionDisabled)
            }

        }
        .padding(.vertical, 7)
    }

    private func authenticationColor(_ status: ProfileAuthenticationStatus) -> Color {
        switch status {
        case .checking: AppTheme.muted
        case .authenticated: AppTheme.green
        case .reauthenticationRequired: AppTheme.yellow
        case .unverified: AppTheme.red
        }
    }

    private var devicesPage: some View {
        SettingsPage(title: "SSH 장치", subtitle: "비밀번호와 키 암호는 macOS Keychain에만 저장됩니다.") {
            if let banner = model.banner { BannerView(banner: banner) }

            VStack(spacing: 0) {
                HStack(spacing: 12) {
                    Image(systemName: "laptopcomputer")
                        .font(.system(size: 20))
                        .foregroundStyle(AppTheme.cyan)
                    VStack(alignment: .leading, spacing: 3) {
                        Text("이 MacBook").font(.system(size: 13, weight: .semibold))
                        Text("로컬 인증 컨트롤러 · 제거할 수 없음")
                            .font(.system(size: 10)).foregroundStyle(AppTheme.muted)
                    }
                    Spacer()
                    statusIndicator(for: "macbook")
                }
                .padding(14)

                Divider().overlay(AppTheme.border)

                ForEach(model.configuredDevices) { device in
                    HStack(spacing: 12) {
                        Image(systemName: "server.rack")
                            .font(.system(size: 18))
                            .foregroundStyle(device.enabled ? AppTheme.blue : AppTheme.muted)
                        VStack(alignment: .leading, spacing: 3) {
                            Text(device.displayName).font(.system(size: 13, weight: .semibold))
                            Text("\(device.username)@\(device.host):\(device.port) · \(device.authentication.displayName)")
                                .font(.system(size: 10)).foregroundStyle(AppTheme.muted)
                                .lineLimit(1)
                        }
                        Spacer()
                        statusIndicator(for: device.id, enabled: device.enabled)
                        if device.enabled {
                            Button("테스트") { Task { await model.testDevice(id: device.id) } }
                                .disabled(settingsMutationDisabled)
                        } else {
                            Button("설치 및 활성화") {
                                Task { await model.bootstrapDevice(id: device.id) }
                            }
                            .buttonStyle(.borderedProminent)
                            .disabled(settingsMutationDisabled)
                        }
                        Button { deviceDraft = device } label: { Image(systemName: "pencil") }
                            .disabled(settingsMutationDisabled)
                        Button(role: .destructive) { deviceToRemove = device } label: { Image(systemName: "trash") }
                            .disabled(settingsMutationDisabled)
                    }
                    .padding(14)
                    Divider().overlay(AppTheme.border)
                }
            }
            .background(AppTheme.card, in: RoundedRectangle(cornerRadius: 14, style: .continuous))
            .overlay(RoundedRectangle(cornerRadius: 14).stroke(AppTheme.border))

            HStack {
                Text("등록 장치 \(model.configuredDevices.count)대")
                    .font(.system(size: 11)).foregroundStyle(AppTheme.muted)
                Spacer()
                Button {
                    deviceDraft = SSHDeviceConfiguration(
                        id: "device-\(UUID().uuidString.prefix(8).lowercased())",
                        displayName: "새 SSH 장치", host: "", port: 22, username: "",
                        authentication: .privateKey, identityFile: nil, certificateFile: nil,
                        hasPassword: false, hasKeyPassphrase: false, enabled: false)
                } label: {
                    Label("장치 추가", systemImage: "plus")
                }
                .buttonStyle(.borderedProminent)
                .disabled(settingsMutationDisabled)
            }
        }
    }

    @ViewBuilder
    private func statusIndicator(for id: String, enabled: Bool = true) -> some View {
        if !enabled {
            Label("사용 안 함", systemImage: "pause.circle.fill")
                .font(.system(size: 10, weight: .semibold))
                .foregroundStyle(AppTheme.muted)
        } else {
            let status = model.devices.first(where: { $0.id == id })
            Label(
                status?.isReachable == true ? "연결됨" : "확인 필요",
                systemImage: status?.isReachable == true ? "checkmark.circle.fill" : "exclamationmark.circle.fill")
                .font(.system(size: 10, weight: .semibold))
                .foregroundStyle(status?.isReachable == true ? AppTheme.green : AppTheme.yellow)
        }
    }

    private var modelsPage: some View {
        SettingsPage(
            title: "모델",
            subtitle: "Cursor 구독 모델을 로컬 및 SSH 원격 Codex에 연결합니다.")
        {
            if let banner = model.banner { BannerView(banner: banner) }

            VStack(alignment: .leading, spacing: 14) {
                HStack(alignment: .top, spacing: 12) {
                    Image(systemName: "testtube.2")
                        .font(.system(size: 18, weight: .semibold))
                        .foregroundStyle(AppTheme.yellow)
                    VStack(alignment: .leading, spacing: 4) {
                        Text("실험 기능")
                            .font(.system(size: 13, weight: .bold))
                        Text("Cursor의 공식 raw API가 아니라 로그인된 Cursor CLI agent를 사용합니다. 호출은 Cursor 구독의 agent workflow로 계산되며 Codex/OpenAI 사용량과 합산되지 않습니다.")
                            .font(.system(size: 10))
                            .foregroundStyle(AppTheme.muted)
                        Text("Cursor CLI에는 실제 저장소 대신 전용 빈 작업공간과 deny 정책을 전달합니다. 다만 사용자 전역 rules·hooks·MCP와 모든 native 도구를 완전히 격리한다고 보장할 수는 없습니다.")
                            .font(.system(size: 10))
                            .foregroundStyle(AppTheme.muted)
                    }
                }
                .padding(12)
                .background(AppTheme.yellow.opacity(0.08), in: RoundedRectangle(cornerRadius: 10))
                .overlay(RoundedRectangle(cornerRadius: 10).stroke(AppTheme.yellow.opacity(0.22)))

                SettingsGroupTitle("연결 상태")
                VStack(spacing: 0) {
                    HStack(spacing: 10) {
                        Image(systemName: cursorStatusIcon)
                            .foregroundStyle(cursorStatusColor)
                        VStack(alignment: .leading, spacing: 3) {
                            Text(model.cursorBridgeStatus.title)
                                .font(.system(size: 13, weight: .semibold))
                            Text(model.cursorBridgeEndpoint)
                                .font(.system(size: 10, design: .monospaced))
                                .foregroundStyle(AppTheme.muted)
                                .textSelection(.enabled)
                        }
                        Spacer()
                        if model.isCursorProviderActive {
                            Label("Codex 기본 provider", systemImage: "checkmark.circle.fill")
                                .font(.system(size: 10, weight: .semibold))
                                .foregroundStyle(AppTheme.green)
                        }
                        Button("연결 확인") {
                            Task { await model.checkCursorBridge() }
                        }
                        .disabled(settingsMutationDisabled)
                    }
                    .padding(.vertical, 8)

                    Divider().overlay(AppTheme.border)

                    HStack {
                        VStack(alignment: .leading, spacing: 3) {
                            Text("Node.js")
                                .font(.system(size: 11, weight: .semibold))
                            Text(model.cursorResolvedNodePath ?? "찾지 못함")
                                .font(.system(size: 9, design: .monospaced))
                                .foregroundStyle(AppTheme.muted)
                                .lineLimit(1)
                        }
                        Spacer()
                        VStack(alignment: .trailing, spacing: 3) {
                            Text("Cursor CLI")
                                .font(.system(size: 11, weight: .semibold))
                            Text(model.cursorResolvedAgentPath ?? "찾지 못함")
                                .font(.system(size: 9, design: .monospaced))
                                .foregroundStyle(AppTheme.muted)
                                .lineLimit(1)
                        }
                    }
                    .padding(.vertical, 8)

                    Divider().overlay(AppTheme.border)

                    HStack {
                        Text("Codex 설정")
                            .font(.system(size: 11, weight: .semibold))
                        Spacer()
                        Text(model.cursorCodexConfigurationPath)
                            .font(.system(size: 9, design: .monospaced))
                            .foregroundStyle(AppTheme.muted)
                            .lineLimit(1)
                            .textSelection(.enabled)
                    }
                    .padding(.vertical, 8)
                }

                Divider().overlay(AppTheme.border)
                SettingsGroupTitle("Cursor provider")
                VStack(alignment: .leading, spacing: 10) {
                    HStack(spacing: 10) {
                        Text("모델")
                            .font(.system(size: 11, weight: .semibold))
                            .frame(width: 74, alignment: .leading)
                        if model.cursorModelCatalog.families.isEmpty {
                            TextField("auto 또는 cursor-agent --list-models의 slug", text: $cursorModelDraft)
                                .textFieldStyle(.roundedBorder)
                                .accessibilityIdentifier("cursor-model-field")
                        } else {
                            Picker("", selection: cursorBaseModelBinding) {
                                if selectedCursorFamily == nil {
                                    Text("직접 지정 · \(cursorModelDraft)")
                                        .tag(cursorModelDraft)
                                }
                                ForEach(model.cursorModelCatalog.sections) { section in
                                    Section(section.group.displayName) {
                                        ForEach(section.families) { family in
                                            Text(family.displayName).tag(family.baseSlug)
                                        }
                                    }
                                }
                            }
                            .labelsHidden()
                            .accessibilityLabel("Cursor 모델 계열")
                            .accessibilityIdentifier("cursor-model-picker")
                        }
                        if model.isLoadingCursorModels {
                            ProgressView().controlSize(.small)
                        } else {
                            Button {
                                Task { await model.refreshCursorModels(agentPath: cursorAgentPathDraft) }
                            } label: {
                                Image(systemName: "arrow.clockwise")
                            }
                            .help("Cursor 모델 목록 새로고침")
                            .disabled(settingsMutationDisabled)
                            .accessibilityIdentifier("cursor-model-refresh-button")
                        }
                    }

                    if let variant = selectedCursorVariant
                    {
                        HStack(spacing: 12) {
                            Text("옵션")
                                .font(.system(size: 11, weight: .semibold))
                                .frame(width: 74, alignment: .leading)
                            Toggle("Thinking", isOn: cursorThinkingBinding)
                                .toggleStyle(.checkbox)
                                .disabled(!canToggleCursorThinking)
                                .accessibilityIdentifier("cursor-thinking-toggle")
                            Text("추론 강도와 Fast는 Codex 앱에서 선택")
                                .font(.system(size: 9))
                                .foregroundStyle(AppTheme.muted)
                            Spacer()
                        }

                        HStack(spacing: 8) {
                            Text("실행 slug")
                                .font(.system(size: 9, weight: .semibold))
                                .foregroundStyle(AppTheme.muted)
                            Text(variant.slug)
                                .font(.system(size: 9, design: .monospaced))
                                .foregroundStyle(AppTheme.muted)
                                .textSelection(.enabled)
                            Spacer()
                        }
                    }

                    if !model.cursorModelCatalog.pickerPresets.isEmpty {
                        HStack(spacing: 10) {
                            Text("Codex 노출")
                                .font(.system(size: 11, weight: .semibold))
                                .frame(width: 74, alignment: .leading)
                            Menu {
                                Button("전체 선택") {
                                    cursorExposedModelIDsDraft = availableCursorExposureIDs
                                }
                                Button("기본 모델만") {
                                    if let modelID = selectedCursorPickerID {
                                        cursorExposedModelIDsDraft = [modelID]
                                    }
                                }
                                Divider()
                                ForEach(model.cursorModelCatalog.sections) { section in
                                    Section(section.group.displayName) {
                                        ForEach(section.families) { family in
                                            if let modelID = cursorExposureModelID(
                                                for: family,
                                                thinking: false)
                                            {
                                                Toggle(
                                                    family.displayName,
                                                    isOn: cursorExposureBinding(for: modelID))
                                                    .disabled(modelID == selectedCursorPickerID)
                                            }
                                            if let modelID = cursorExposureModelID(
                                                for: family,
                                                thinking: true)
                                            {
                                                Toggle(
                                                    "\(family.displayName) · Thinking",
                                                    isOn: cursorExposureBinding(for: modelID))
                                                    .disabled(modelID == selectedCursorPickerID)
                                            }
                                        }
                                    }
                                }
                            } label: {
                                HStack(spacing: 6) {
                                    Text("\(cursorExposedModelIDsDraft.count)/\(availableCursorExposureIDs.count)개 표시")
                                    Image(systemName: "chevron.down")
                                        .font(.system(size: 8, weight: .semibold))
                                }
                                .frame(maxWidth: .infinity, alignment: .leading)
                            }
                            .accessibilityLabel("Codex에 표시할 Cursor 모델")
                            .accessibilityIdentifier("cursor-exposed-models-menu")
                        }
                        Text("기본 모델은 항상 표시됩니다. 기존 OpenAI 모델은 이 설정과 관계없이 유지됩니다.")
                            .font(.system(size: 9))
                            .foregroundStyle(AppTheme.muted)
                            .padding(.leading, 84)
                    }

                    HStack(spacing: 10) {
                        Text("포트")
                            .font(.system(size: 11, weight: .semibold))
                            .frame(width: 74, alignment: .leading)
                        TextField("32125", text: $cursorPortDraft)
                            .textFieldStyle(.roundedBorder)
                            .frame(width: 76)
                            .accessibilityIdentifier("cursor-port-field")
                    }
                    HStack(spacing: 10) {
                        Text("agent 경로")
                            .font(.system(size: 11, weight: .semibold))
                            .frame(width: 74, alignment: .leading)
                        TextField("비워 두면 자동 탐색", text: $cursorAgentPathDraft)
                            .textFieldStyle(.roundedBorder)
                            .font(.system(size: 10, design: .monospaced))
                            .accessibilityIdentifier("cursor-agent-path-field")
                    }
                    if let catalogError = model.cursorModelCatalogError {
                        Label(catalogError, systemImage: "exclamationmark.triangle.fill")
                            .font(.system(size: 9))
                            .foregroundStyle(AppTheme.yellow)
                            .textSelection(.enabled)
                    }
                    Text("현재 Cursor 계정의 `cursor-agent --list-models` 결과를 사용합니다. 선택한 Cursor 모델만 Codex 모델 선택기에 추가되고, 추론 강도와 Fast는 Codex 앱의 기존 선택창에서 고르면 실제 Cursor variant로 변환됩니다. Thinking은 별도 Cursor 모델 항목으로 선택할 수 있습니다. 이미지와 Computer Use 스크린샷은 Cursor ACP로 전달됩니다.")
                        .font(.system(size: 9))
                        .foregroundStyle(AppTheme.muted)
                }

                if let error = model.cursorBridgeError {
                    Label(error, systemImage: "exclamationmark.triangle.fill")
                        .font(.system(size: 10))
                        .foregroundStyle(AppTheme.yellow)
                        .textSelection(.enabled)
                }

                Divider().overlay(AppTheme.border)
                SettingsGroupTitle("SSH 원격 Cursor")
                VStack(alignment: .leading, spacing: 10) {
                    Text("macOS Cursor 로그인 저장소는 Linux로 복사할 수 없습니다. Cursor User API Key는 이 Mac의 Keychain에 저장되며, SSH stdin으로 전송된 뒤 각 원격 장치의 ~/.local/share/gpt-switch/cursor-remote-runtime.json과 전용 cursor-remote-xdg/에 소유자 전용으로 저장됩니다. Cursor provider 전체 해제가 성공한 원격 장치에서는 두 저장소를 함께 제거합니다.")
                        .font(.system(size: 9))
                        .foregroundStyle(AppTheme.muted)
                    HStack(spacing: 10) {
                        SecureField(
                            model.hasCursorAPIKey ? "저장됨 · 새 값으로 교체" : "Cursor User API Key",
                            text: $cursorAPIKeyDraft)
                            .textFieldStyle(.roundedBorder)
                            .accessibilityIdentifier("cursor-api-key-field")
                        Button(model.hasCursorAPIKey ? "교체 및 동기화" : "저장 및 동기화") {
                            let key = cursorAPIKeyDraft
                            Task {
                                if await model.saveCursorAPIKeyAndSync(key) {
                                    cursorAPIKeyDraft = ""
                                }
                            }
                        }
                        .disabled(settingsMutationDisabled || cursorAPIKeyDraft.isEmpty)
                        if model.hasCursorAPIKey {
                            Button("이 Mac에서 삭제", role: .destructive) {
                                model.deleteCursorAPIKey()
                            }
                            .disabled(settingsMutationDisabled)
                        }
                    }
                    HStack {
                        Label(
                            model.hasCursorAPIKey ? "Keychain에 저장됨" : "API key가 저장되지 않음",
                            systemImage: model.hasCursorAPIKey ? "key.fill" : "key.slash")
                            .font(.system(size: 9, weight: .semibold))
                            .foregroundStyle(model.hasCursorAPIKey ? AppTheme.green : AppTheme.muted)
                        Spacer()
                        Button("활성 SSH 장치에 동기화") {
                            Task { await model.syncCursorProviderToSSHDevices() }
                        }
                        .disabled(
                            settingsMutationDisabled
                                || !model.hasCursorAPIKey
                                || !model.isCursorProviderActive)
                        .accessibilityIdentifier("cursor-ssh-sync-button")
                    }
                }

                HStack(spacing: 8) {
                    Button("CLI 설치 안내") { model.openCursorCLIInstallationGuide() }
                    Button("cursor-agent login 복사") { model.copyCursorLoginCommand() }
                    Spacer()
                    if model.isCursorProviderActive {
                        Button("이전 Codex 모델로 복구", role: .destructive) {
                            Task { await model.disableCursorProvider() }
                        }
                        .disabled(settingsMutationDisabled)
                    }
                    Button(model.isCursorProviderActive ? "설정 업데이트" : "Codex 기본 모델로 사용") {
                        guard let port = Int(cursorPortDraft) else { return }
                        Task {
                            let exposedModelIDs = model.cursorModelCatalog.pickerPresets.isEmpty
                                ? nil
                                : cursorExposedModelIDsDraft.sorted()
                            await model.enableCursorProvider(
                                model: cursorModelDraft,
                                port: port,
                                agentPath: cursorAgentPathDraft,
                                exposedModelIDs: exposedModelIDs)
                        }
                    }
                    .buttonStyle(.borderedProminent)
                    .disabled(
                        settingsMutationDisabled
                            || Int(cursorPortDraft) == nil
                            || !cursorExposureSelectionIsValid)
                    .accessibilityIdentifier("cursor-provider-enable-button")
                }
            }
            .appCard()
        }
        .onAppear {
            cursorModelDraft = model.cursorBridgePreferences.model
            cursorPortDraft = String(model.cursorBridgePreferences.port)
            cursorAgentPathDraft = model.cursorBridgePreferences.agentPath ?? ""
            synchronizeCursorExposureDraft(reset: true)
        }
        .onChange(of: model.cursorModelCatalog) { _ in
            synchronizeCursorExposureDraft()
        }
        .onChange(of: cursorModelDraft) { _ in
            if let modelID = selectedCursorPickerID {
                cursorExposedModelIDsDraft.insert(modelID)
            }
        }
        .task {
            if model.cursorModelCatalog.families.isEmpty {
                await model.refreshCursorModels(agentPath: cursorAgentPathDraft)
            }
        }
    }

    private var selectedCursorFamily: CursorModelFamily? {
        model.cursorModelCatalog.family(containingSlug: cursorModelDraft)
    }

    private var selectedCursorVariant: CursorModelVariant? {
        model.cursorModelCatalog.variants.first { $0.slug == cursorModelDraft }
    }

    private var cursorBaseModelBinding: Binding<String> {
        Binding(
            get: { selectedCursorFamily?.baseSlug ?? cursorModelDraft },
            set: { baseSlug in
                guard let family = model.cursorModelCatalog.family(baseSlug: baseSlug),
                      let variant = family.preferredVariant
                else { return }
                cursorModelDraft = variant.slug
            })
    }

    private var cursorThinkingBinding: Binding<Bool> {
        Binding(
            get: { selectedCursorVariant?.thinking ?? false },
            set: { thinking in
                guard let family = selectedCursorFamily,
                      let variant = selectedCursorVariant,
                      let slug = family.resolve(
                          effort: variant.effort,
                          fast: variant.fast,
                          thinking: thinking)
                else { return }
                cursorModelDraft = slug
            })
    }

    private var canToggleCursorThinking: Bool {
        guard let family = selectedCursorFamily,
              let variant = selectedCursorVariant
        else { return false }
        return family.resolve(
            effort: variant.effort,
            fast: variant.fast,
            thinking: !variant.thinking) != nil
    }

    private var availableCursorExposureIDs: Set<String> {
        Set(model.cursorModelCatalog.pickerPresets.map(\.id))
    }

    private var selectedCursorPickerID: String? {
        model.cursorModelCatalog.preferredPickerModelID(forFlatSlug: cursorModelDraft)
    }

    private var cursorExposureSelectionIsValid: Bool {
        let available = availableCursorExposureIDs
        guard !available.isEmpty else { return true }
        guard !cursorExposedModelIDsDraft.isEmpty,
              cursorExposedModelIDsDraft.isSubset(of: available)
        else { return false }
        guard let selectedCursorPickerID else { return false }
        return cursorExposedModelIDsDraft.contains(selectedCursorPickerID)
    }

    private func cursorExposureModelID(
        for family: CursorModelFamily,
        thinking: Bool) -> String?
    {
        let modelID = CursorModelCatalog.codexModelID(
            baseSlug: family.baseSlug,
            thinking: thinking)
        return availableCursorExposureIDs.contains(modelID) ? modelID : nil
    }

    private func cursorExposureBinding(for modelID: String) -> Binding<Bool> {
        Binding(
            get: { cursorExposedModelIDsDraft.contains(modelID) },
            set: { isExposed in
                if isExposed {
                    cursorExposedModelIDsDraft.insert(modelID)
                } else if modelID != selectedCursorPickerID {
                    cursorExposedModelIDsDraft.remove(modelID)
                }
            })
    }

    private func synchronizeCursorExposureDraft(reset: Bool = false) {
        let available = availableCursorExposureIDs
        guard !available.isEmpty else { return }
        if reset || !cursorExposureDraftInitialized {
            if let stored = model.cursorBridgePreferences.exposedModelIDs {
                cursorExposedModelIDsDraft = Set(stored).intersection(available)
            } else {
                cursorExposedModelIDsDraft = available
            }
            cursorExposureDraftInitialized = true
        } else {
            cursorExposedModelIDsDraft.formIntersection(available)
        }
        if let modelID = selectedCursorPickerID {
            cursorExposedModelIDsDraft.insert(modelID)
        } else if cursorExposedModelIDsDraft.isEmpty,
                  let first = model.cursorModelCatalog.pickerPresets.first?.id
        {
            cursorExposedModelIDsDraft.insert(first)
        }
    }

    private var cursorStatusIcon: String {
        switch model.cursorBridgeStatus {
        case .healthy: "checkmark.circle.fill"
        case .starting: "clock.fill"
        case .stopped: "pause.circle.fill"
        case .missingNode, .missingAgent, .unauthenticated, .portConflict, .failed:
            "exclamationmark.triangle.fill"
        }
    }

    private var cursorStatusColor: Color {
        switch model.cursorBridgeStatus {
        case .healthy: AppTheme.green
        case .starting: AppTheme.blue
        case .stopped: AppTheme.muted
        case .missingNode, .missingAgent, .unauthenticated, .portConflict, .failed:
            AppTheme.yellow
        }
    }

    private var generalPage: some View {
        SettingsPage(title: "일반", subtitle: "사용량 표시, 새로고침, 로그인 유지, 자동 시작을 관리합니다.") {
            if let banner = model.banner { BannerView(banner: banner) }

            VStack(alignment: .leading, spacing: 14) {
                SettingsGroupTitle("사용량 표시")
                VStack(spacing: 0) {
                    usageDisplayToggle(
                        UsageDisplayItem.fiveHour,
                        detail: "기본 Codex 단기 한도",
                        identifier: "usage-display-five-hour-toggle")
                    Divider().overlay(AppTheme.border)
                    usageDisplayToggle(
                        UsageDisplayItem.codexWeekly,
                        detail: "기본 Codex 주간 한도",
                        identifier: "usage-display-codex-weekly-toggle")
                    Divider().overlay(AppTheme.border)
                    usageDisplayToggle(
                        UsageDisplayItem.sparkFiveHour,
                        detail: "Codex Spark 단기 한도",
                        identifier: "usage-display-spark-five-hour-toggle")
                    Divider().overlay(AppTheme.border)
                    usageDisplayToggle(
                        UsageDisplayItem.sparkWeekly,
                        detail: "Codex Spark 주간 한도",
                        identifier: "usage-display-spark-weekly-toggle")
                }

                Divider().overlay(AppTheme.border)
                SettingsGroupTitle("주간 주기 고정")
                VStack(alignment: .leading, spacing: 10) {
                    Text("켜는 시점에 주간 사용량이 100%면 즉시 한 번 실행합니다. 이후에는 초기화 뒤 사용량이 그대로이거나 예정 시각이 계속 밀리면 이를 확인한 뒤 계정별로 짧은 읽기 전용 Codex 요청을 보내 다음 주기를 시작합니다.")
                        .font(.system(size: 10))
                        .foregroundStyle(AppTheme.muted)
                    Text("자동 실행에는 refresh token을 전달하지 않습니다. Mac이 잠든 동안 놓친 실행은 깨어난 뒤 처리합니다.")
                        .font(.system(size: 9))
                        .foregroundStyle(AppTheme.muted)
                    VStack(spacing: 0) {
                        ForEach(Array(model.profiles.enumerated()), id: \.element.id) { index, profile in
                            HStack(spacing: 10) {
                                Image(systemName: "calendar.badge.clock")
                                    .font(.system(size: 12, weight: .semibold))
                                    .foregroundStyle(AppTheme.blue)
                                    .frame(width: 20)
                                VStack(alignment: .leading, spacing: 2) {
                                    Text(profile.alias)
                                        .font(.system(size: 12, weight: .semibold))
                                    if profile.alias != profile.email {
                                        Text(profile.email)
                                            .font(.system(size: 9))
                                            .foregroundStyle(AppTheme.muted)
                                    }
                                    Text(model.weeklyAnchorStatusText(profileID: profile.id))
                                        .font(.system(size: 9))
                                        .foregroundStyle(AppTheme.muted)
                                }
                                Spacer()
                                Toggle("", isOn: Binding(
                                    get: { model.isWeeklyAnchorEnabled(profileID: profile.id) },
                                    set: { model.setWeeklyAnchorEnabled($0, profileID: profile.id) }))
                                    .labelsHidden()
                                    .toggleStyle(.switch)
                                    .accessibilityLabel("\(profile.alias) 주간 주기 자동 시작")
                                    .accessibilityIdentifier("weekly-anchor-toggle-\(profile.id)")
                            }
                            .padding(.vertical, 3)
                            if index < model.profiles.count - 1 {
                                Divider().overlay(AppTheme.border)
                            }
                        }
                    }
                }

                Divider().overlay(AppTheme.border)
                SettingsGroupTitle("상단 메뉴바")
                VStack(alignment: .leading, spacing: 8) {
                    Text("계정 별칭 뒤에 표시할 사용량 개수와 항목을 선택합니다.")
                        .font(.system(size: 10))
                        .foregroundStyle(AppTheme.muted)
                    HStack(spacing: 12) {
                        Text("표시 개수")
                            .font(.system(size: 11, weight: .semibold))
                        Spacer()
                        Picker("", selection: Binding(
                            get: { model.menuBarUsagePreferences.items.count },
                            set: { model.setMenuBarUsageItemCount($0) }))
                        {
                            Text("0개").tag(0)
                            Text("1개").tag(1)
                            Text("2개").tag(2)
                        }
                        .labelsHidden()
                        .pickerStyle(.segmented)
                        .frame(width: 180)
                        .accessibilityLabel("상단 메뉴바 사용량 표시 개수")
                        .accessibilityIdentifier("menu-bar-item-count-picker")
                    }
                    if !model.menuBarUsagePreferences.items.isEmpty {
                        HStack(spacing: 8) {
                            ForEach(
                                Array(model.menuBarUsagePreferences.items.enumerated()),
                                id: \.element.id)
                            { index, item in
                                menuBarUsageSlot(
                                    item: item,
                                    index: index,
                                    identifier: index == 0
                                        ? "menu-bar-primary-item-slot"
                                        : "menu-bar-secondary-item-slot")
                            }
                        }
                    }
                    HStack(spacing: 5) {
                        Text("미리보기")
                            .foregroundStyle(AppTheme.muted)
                        Text(model.menuTitle)
                            .fontWeight(.semibold)
                            .monospacedDigit()
                    }
                    .font(.system(size: 10))
                    .padding(.top, 2)
                }

                Divider().overlay(AppTheme.border)
                SettingsGroupTitle("새로고침")
                HStack {
                    VStack(alignment: .leading, spacing: 3) {
                        Text("사용량과 장치 상태")
                            .font(.system(size: 13, weight: .semibold))
                        Text(model.authMaintenanceSummary)
                            .font(.system(size: 10)).foregroundStyle(AppTheme.muted)
                    }
                    Spacer()
                    Button("선택 계정") { Task { await model.refresh(profileID: model.selectedProfileID) } }
                        .disabled(accountActionDisabled)
                    Button("모두 새로고침") { Task { await model.refreshAll() } }
                        .buttonStyle(.borderedProminent)
                        .disabled(accountActionDisabled)
                }

                Divider().overlay(AppTheme.border)
                SettingsGroupTitle("로그인 시 실행")
                HStack {
                    Text("Mac 로그인 시 Codex SyncBar 자동 실행")
                        .font(.system(size: 13, weight: .semibold))
                    Spacer()
                    Toggle("", isOn: Binding(
                        get: { model.launchAtLogin },
                        set: { _ in model.toggleLaunchAtLogin() }))
                        .labelsHidden()
                        .toggleStyle(.switch)
                        .accessibilityLabel("Mac 로그인 시 Codex SyncBar 자동 실행")
                        .accessibilityIdentifier("launch-toggle")
                }
                if model.launchAtLoginRequiresApproval {
                    HStack {
                        Text("macOS 시스템 설정에서 로그인을 허용해 주세요.")
                            .font(.system(size: 10))
                            .foregroundStyle(AppTheme.muted)
                        Spacer()
                        Button("로그인 항목 설정 열기") { model.openLoginItemsSettings() }
                            .buttonStyle(.borderedProminent)
                    }
                }

                Divider().overlay(AppTheme.border)
                SettingsGroupTitle("인증 유지")
                HStack {
                    Label(model.authMaintenanceNeedsAttention ? "확인 필요" : "정상",
                          systemImage: model.authMaintenanceNeedsAttention
                            ? "exclamationmark.triangle.fill" : "checkmark.shield.fill")
                        .foregroundStyle(model.authMaintenanceNeedsAttention ? AppTheme.yellow : AppTheme.green)
                    Spacer()
                    Button("지금 동기화") {
                        Task { await model.maintainAuthIfNeeded(forceSync: true) }
                    }
                    .disabled(accountActionDisabled)
                }
                .font(.system(size: 12, weight: .semibold))
            }
            .appCard()

            HStack {
                Text("Codex SyncBar \(AppVersion.current)")
                Spacer()
                Button("종료") { model.quit() }
            }
            .font(.system(size: 11))
            .foregroundStyle(AppTheme.muted)
        }
    }

    private func usageDisplayToggle(
        _ item: UsageDisplayItem,
        detail: String,
        identifier: String) -> some View
    {
        HStack(spacing: 10) {
            Image(systemName: item.icon)
                .font(.system(size: 12, weight: .semibold))
                .foregroundStyle(AppTheme.blue)
                .frame(width: 20)
            VStack(alignment: .leading, spacing: 2) {
                Text(item.title)
                    .font(.system(size: 12, weight: .semibold))
                Text(detail)
                    .font(.system(size: 9))
                    .foregroundStyle(AppTheme.muted)
            }
            Spacer()
            Toggle("", isOn: Binding(
                get: { model.usageDisplayPreferences.isVisible(item) },
                set: { model.setUsageDisplay(item, isVisible: $0) }))
                .labelsHidden()
                .toggleStyle(.switch)
                .accessibilityLabel("\(item.title) 표시")
                .accessibilityIdentifier(identifier)
        }
        .padding(.vertical, 7)
    }

    private func menuBarUsageSlot(
        item: UsageDisplayItem,
        index: Int,
        identifier: String) -> some View
    {
        let selectedItems = model.menuBarUsagePreferences.items
        let otherItem = selectedItems.first(where: { $0 != item })
        return HStack(spacing: 7) {
            Text("\(index + 1)")
                .font(.system(size: 10, weight: .bold, design: .rounded))
                .foregroundStyle(AppTheme.blue)
                .frame(width: 22, height: 22)
                .background(AppTheme.blue.opacity(0.14), in: Circle())
            Picker("", selection: Binding(
                get: { model.menuBarUsagePreferences.item(at: index, fallback: item) },
                set: { model.setMenuBarUsageItem($0, at: index) }))
            {
                ForEach(UsageDisplayItem.allCases.filter { candidate in
                    otherItem.map { candidate != $0 } ?? true
                }) { item in
                    Text(item.title).tag(item)
                }
            }
            .labelsHidden()
            .pickerStyle(.menu)
            .controlSize(.small)
            .frame(maxWidth: .infinity, alignment: .leading)
            .accessibilityLabel("\(index == 0 ? "첫 번째" : "두 번째") 메뉴바 사용량")
            Text(menuBarUsageValue(item))
                .font(.system(size: 12, weight: .bold, design: .rounded))
                .monospacedDigit()
        }
        .foregroundStyle(.primary)
        .padding(.horizontal, 9)
        .frame(width: 220, height: 48)
        .background(Color.white.opacity(0.035), in: RoundedRectangle(cornerRadius: 9))
        .overlay {
            RoundedRectangle(cornerRadius: 9)
                .stroke(AppTheme.border, lineWidth: 1)
        }
        .contentShape(RoundedRectangle(cornerRadius: 9))
        .accessibilityIdentifier(identifier)
    }

    private func menuBarUsageValue(_ item: UsageDisplayItem) -> String {
        guard let snapshot = model.menuUsageState.snapshot,
              let window = item.window(in: snapshot)
        else { return "—" }
        return "\(Int(window.remainingPercent.rounded()))%"
    }
}

private struct AccountAliasEditor: View {
    let profile: AccountProfile
    let disabled: Bool
    let onSave: (String) -> Void
    @State private var draft: String

    init(profile: AccountProfile, disabled: Bool, onSave: @escaping (String) -> Void) {
        self.profile = profile
        self.disabled = disabled
        self.onSave = onSave
        _draft = State(initialValue: profile.customAlias ?? "")
    }

    private var normalizedDraft: String? {
        try? AccountProfile.normalizedAlias(draft)
    }

    private var hasChanges: Bool {
        normalizedDraft != profile.customAlias
    }

    var body: some View {
        VStack(alignment: .trailing, spacing: 2) {
            HStack(spacing: 4) {
                TextField("별칭", text: $draft)
                    .textFieldStyle(.roundedBorder)
                    .frame(width: 72)
                    .onSubmit(save)
                    .accessibilityLabel("별칭 (최대 5자)")
                    .accessibilityIdentifier("account-alias-\(profile.id)")
                Button(action: save) {
                    Image(systemName: "checkmark")
                }
                .buttonStyle(.borderless)
                .help("별칭 저장")
                .disabled(disabled || !hasChanges)
            }
            Text("별칭 (최대 5자) · \(draft.count)/\(AccountProfile.maximumAliasLength)")
                .font(.system(size: 8))
                .foregroundStyle(AppTheme.muted)
        }
        .onChange(of: draft) { value in
            if value.count > AccountProfile.maximumAliasLength {
                draft = String(value.prefix(AccountProfile.maximumAliasLength))
            }
        }
        .onChange(of: profile.customAlias) { value in
            draft = value ?? ""
        }
    }

    private func save() {
        guard !disabled, hasChanges else { return }
        onSave(draft)
    }
}

private struct SettingsPage<Content: View>: View {
    let title: String
    let subtitle: String
    let content: Content

    init(title: String, subtitle: String, @ViewBuilder content: () -> Content) {
        self.title = title
        self.subtitle = subtitle
        self.content = content()
    }

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 18) {
                VStack(alignment: .leading, spacing: 4) {
                    Text(title).font(.system(size: 24, weight: .bold))
                    Text(subtitle).font(.system(size: 12)).foregroundStyle(AppTheme.muted)
                }
                content
            }
            .padding(24)
            .frame(maxWidth: .infinity, alignment: .leading)
        }
    }
}

private struct SettingsGroupTitle: View {
    let text: String
    init(_ text: String) { self.text = text }
    var body: some View {
        Text(text.uppercased())
            .font(.system(size: 10, weight: .bold))
            .foregroundStyle(AppTheme.muted)
    }
}

private struct DeviceEditorView: View {
    @ObservedObject var model: AppModel
    @Environment(\.dismiss) private var dismiss
    @State private var draft: SSHDeviceConfiguration
    @State private var password = ""
    @State private var passphrase = ""
    @State private var clearPassphrase = false
    @State private var errorMessage: String?
    private let existingID: String?

    init(model: AppModel, initial: SSHDeviceConfiguration) {
        self.model = model
        existingID = model.configuredDevices.contains(where: { $0.id == initial.id }) ? initial.id : nil
        _draft = State(initialValue: initial)
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 18) {
            HStack {
                VStack(alignment: .leading, spacing: 3) {
                    Text("SSH 장치 설정").font(.system(size: 20, weight: .bold))
                    Text("비밀값은 Keychain에 저장되며 다시 표시되지 않습니다.")
                        .font(.system(size: 11)).foregroundStyle(AppTheme.muted)
                }
                Spacer()
                Button { dismiss() } label: { Image(systemName: "xmark") }
                    .buttonStyle(.plain)
            }

            Form {
                TextField("장치 ID", text: $draft.id)
                    .disabled(existingID != nil)
                TextField("표시 이름", text: $draft.displayName)
                TextField("호스트 또는 IP", text: $draft.host)
                TextField("사용자", text: $draft.username)
                TextField("포트", value: $draft.port, format: .number)
                Picker("인증 방식", selection: $draft.authentication) {
                    ForEach(SSHAuthenticationKind.allCases, id: \.rawValue) { authentication in
                        Text(authentication.displayName).tag(authentication)
                    }
                }

                if draft.authentication == .privateKey {
                    fileRow(title: "개인 키", path: draft.identityFile) {
                        draft.identityFile = chooseFile()
                    }
                    fileRow(title: "SSH 인증서 (선택)", path: draft.certificateFile) {
                        draft.certificateFile = chooseFile()
                    } onClear: {
                        draft.certificateFile = nil
                    }
                    SecureField(
                        draft.hasKeyPassphrase
                            ? "키 암호 변경 (엔드포인트 변경 시 다시 입력)"
                            : "키 암호 (선택)",
                        text: $passphrase)
                    if draft.hasKeyPassphrase {
                        Toggle("저장된 키 암호 삭제", isOn: $clearPassphrase)
                    }
                } else if draft.authentication == .password {
                    SecureField(draft.hasPassword ? "비밀번호 변경 (비우면 유지)" : "비밀번호", text: $password)
                }
                Toggle("동기화 대상에 포함", isOn: $draft.enabled)
                    .disabled(existingID == nil || model.configuredDevices
                        .first(where: { $0.id == existingID })?.enabled == false)
                if existingID == nil {
                    Text("저장 후 장치 목록에서 ‘설치 및 활성화’를 실행하면 동기화 대상에 포함됩니다.")
                        .font(.system(size: 10))
                        .foregroundStyle(AppTheme.muted)
                }
            }
            .formStyle(.grouped)

            if let errorMessage {
                Label(errorMessage, systemImage: "exclamationmark.triangle.fill")
                    .font(.system(size: 11)).foregroundStyle(AppTheme.red)
            }

            HStack {
                Spacer()
                Button("취소") { dismiss() }
                Button("저장") {
                    do {
                        if existingID == nil,
                           model.configuredDevices.contains(where: { $0.id == draft.id })
                        {
                            throw AppError.processFailed("이미 사용 중인 장치 ID입니다.")
                        }
                        try model.saveDevice(
                            draft,
                            password: password,
                            passphrase: passphrase,
                            clearPassphrase: clearPassphrase)
                        dismiss()
                    } catch {
                        errorMessage = error.localizedDescription
                    }
                }
                .buttonStyle(.borderedProminent)
                .disabled(
                    model.managementActionsDisabled
                        || model.isManagingProfiles
                        || model.isSwitching
                        || model.isMaintainingAuth
                        || model.profileManagementRecoveryNeeded)
            }
        }
        .padding(24)
        .frame(width: 560, height: 620)
        .background(AppTheme.panel)
        .preferredColorScheme(.dark)
    }

    private func fileRow(
        title: String,
        path: String?,
        choose: @escaping () -> Void,
        onClear: (() -> Void)? = nil) -> some View
    {
        HStack {
            VStack(alignment: .leading, spacing: 2) {
                Text(title)
                Text(path ?? "선택되지 않음")
                    .font(.system(size: 10)).foregroundStyle(AppTheme.muted).lineLimit(1)
            }
            Spacer()
            if path != nil, let onClear {
                Button("제거", role: .destructive, action: onClear)
            }
            Button("선택…", action: choose)
        }
    }

    private func chooseFile() -> String? {
        let panel = NSOpenPanel()
        panel.canChooseDirectories = false
        panel.canChooseFiles = true
        panel.allowsMultipleSelection = false
        panel.resolvesAliases = false
        return panel.runModal() == .OK ? panel.url?.path : nil
    }
}
