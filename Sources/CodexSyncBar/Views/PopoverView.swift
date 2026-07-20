import SwiftUI

enum AccountGridLayout {
    static let columnCount = 2

    static func rowCount(_ accountCount: Int) -> Int {
        guard accountCount > 0 else { return 0 }
        return (accountCount + columnCount - 1) / columnCount
    }

}

struct PopoverHeightPreferenceKey: PreferenceKey {
    static var defaultValue: CGFloat = 0

    static func reduce(value: inout CGFloat, nextValue: () -> CGFloat) {
        value = nextValue()
    }
}

private struct UsageQuotaMetric: Identifiable {
    let item: UsageDisplayItem
    let window: UsageWindow?
    let baseTint: Color

    var id: String { item.id }
}

struct PopoverView: View {
    @ObservedObject var model: AppModel
    let presentSettings: () -> Void
    let onContentHeightChange: ((CGFloat) -> Void)?
    @State private var lastReportedContentHeight: CGFloat = 0

    init(
        model: AppModel,
        presentSettings: @escaping () -> Void,
        onContentHeightChange: ((CGFloat) -> Void)? = nil)
    {
        self.model = model
        self.presentSettings = presentSettings
        self.onContentHeightChange = onContentHeightChange
    }

    private var state: UsageState {
        model.usageStates[model.selectedProfileID] ?? .idle
    }

    private var snapshot: UsageSnapshot? { state.snapshot }

    private var visibleUsageMetrics: [UsageQuotaMetric] {
        let preferences = model.usageDisplayPreferences
        var metrics: [UsageQuotaMetric] = []
        if preferences.isVisible(UsageDisplayItem.fiveHour) {
            metrics.append(UsageQuotaMetric(
                item: UsageDisplayItem.fiveHour,
                window: snapshot?.session,
                baseTint: AppTheme.cyan))
        }
        if preferences.isVisible(UsageDisplayItem.codexWeekly) {
            metrics.append(UsageQuotaMetric(
                item: UsageDisplayItem.codexWeekly,
                window: snapshot?.weekly,
                baseTint: AppTheme.cyan))
        }
        if preferences.isVisible(UsageDisplayItem.sparkFiveHour) {
            metrics.append(UsageQuotaMetric(
                item: UsageDisplayItem.sparkFiveHour,
                window: snapshot?.sparkSession,
                baseTint: AppTheme.blue))
        }
        if preferences.isVisible(UsageDisplayItem.sparkWeekly) {
            metrics.append(UsageQuotaMetric(
                item: UsageDisplayItem.sparkWeekly,
                window: snapshot?.sparkWeekly,
                baseTint: AppTheme.blue))
        }
        return metrics
    }

    var body: some View {
        VStack(spacing: 0) {
            VStack(spacing: 8) {
                accountPicker
                if let banner = model.banner {
                    BannerView(banner: banner)
                        .transition(.move(edge: .top).combined(with: .opacity))
                }
                sectionHeader(
                    title: "선택 계정",
                    detail: model.selectedProfile.email,
                    tint: AppTheme.blue)
                accountSummary
                reauthenticationButton
                usageCard
                resetCreditsCard
                sectionHeader(
                    title: "전체 기기",
                    detail: "계정 선택과 무관한 공통 정보",
                    tint: AppTheme.cyan)
                tokenUsageCard
                switchButton
            }
            .padding(.horizontal, 13)
            .padding(.top, 10)
            .padding(.bottom, 7)
            .frame(maxWidth: .infinity, alignment: .top)

            footer
        }
        .frame(width: AppLayout.popoverWidth)
        .fixedSize(horizontal: false, vertical: true)
        .background(AppTheme.panel)
        .background {
            GeometryReader { proxy in
                Color.clear.preference(
                    key: PopoverHeightPreferenceKey.self,
                    value: proxy.size.height)
            }
        }
        .onPreferenceChange(PopoverHeightPreferenceKey.self) { height in
            guard let onContentHeightChange,
                  height > 0,
                  abs(height - lastReportedContentHeight) > 0.5
            else { return }
            lastReportedContentHeight = height
            onContentHeightChange(height)
        }
        .preferredColorScheme(.dark)
        .task { await model.start() }
    }

    private func sectionHeader(title: String, detail: String, tint: Color) -> some View {
        HStack(spacing: 7) {
            RoundedRectangle(cornerRadius: 2, style: .continuous)
                .fill(tint)
                .frame(width: 3, height: 13)
            Text(title)
                .font(.system(size: 10, weight: .bold))
                .foregroundStyle(Color.white.opacity(0.84))
            Spacer(minLength: 8)
            Text(detail)
                .font(.system(size: 8.5, weight: .medium))
                .foregroundStyle(Color.white.opacity(0.38))
                .lineLimit(1)
                .truncationMode(.middle)
        }
        .padding(.horizontal, 2)
        .padding(.top, 3)
        .accessibilityElement(children: .combine)
        .accessibilityAddTraits(.isHeader)
    }

    private var accountPicker: some View {
        let columnCount = model.profiles.count == 1 ? 1 : AccountGridLayout.columnCount
        let columns = Array(
            repeating: GridItem(.flexible(), spacing: 6),
            count: columnCount)
        return LazyVGrid(columns: columns, spacing: 6) {
            ForEach(model.profiles) { profile in
                Button {
                    withAnimation(.easeOut(duration: 0.16)) { model.selectProfile(profile.id) }
                } label: {
                    accountButtonLabel(profile)
                }
                .buttonStyle(.plain)
                .help(profile.email)
                .accessibilityLabel("계정 선택, \(profile.alias), \(profile.email)")
                .accessibilityIdentifier("account-button-\(profile.id)")
            }
        }
        .accessibilityIdentifier("account-grid")
    }

    private func accountButtonLabel(_ profile: AccountProfile) -> some View {
        let isSelected = model.selectedProfileID == profile.id
        let isActive = model.activeProfileID == profile.id
        let remaining = model.usageStates[profile.id]?.snapshot?.menuRemainingPercent
        let authentication = model.authenticationStatus(for: profile.id)
        return HStack(spacing: 7) {
            ZStack {
                Circle()
                    .fill(isSelected ? Color.white.opacity(0.18) : AppTheme.card.opacity(0.9))
                    .frame(width: 25, height: 25)
                Text(String(profile.alias.prefix(1)).uppercased())
                    .font(.system(size: 10, weight: .bold, design: .rounded))
            }
            VStack(alignment: .leading, spacing: 1) {
                Text(profile.alias)
                    .font(.system(
                        size: profile.customAlias == nil ? 9 : 10,
                        weight: .semibold))
                    .lineLimit(1)
                    .truncationMode(profile.customAlias == nil ? .middle : .tail)
                if profile.customAlias != nil {
                    Text(profile.email)
                        .font(.system(size: 8))
                        .foregroundStyle(isSelected ? Color.white.opacity(0.72) : AppTheme.muted)
                        .lineLimit(1)
                        .truncationMode(.middle)
                }
                HStack(spacing: 3) {
                    Image(systemName: authentication.systemImage)
                    Text(authentication.shortTitle)
                }
                .font(.system(size: 7.5, weight: .semibold))
                .foregroundStyle(authenticationColor(authentication, selected: isSelected))
                .lineLimit(1)
            }
            Spacer(minLength: 1)
            if let remaining {
                Text("\(remaining)%")
                    .font(.system(size: 9, weight: .bold, design: .rounded))
                    .monospacedDigit()
            }
            if isActive {
                Image(systemName: "checkmark.circle.fill")
                    .font(.system(size: 10, weight: .semibold))
                    .foregroundStyle(isSelected ? Color.white : AppTheme.green)
            }
        }
        .padding(.horizontal, 9)
        .frame(maxWidth: .infinity, minHeight: profile.customAlias == nil ? 42 : 48)
        .background(
            isSelected ? AppTheme.blue : AppTheme.card,
            in: RoundedRectangle(cornerRadius: 10, style: .continuous))
        .overlay(
            RoundedRectangle(cornerRadius: 10, style: .continuous)
                .stroke(isSelected ? AppTheme.blue : AppTheme.border, lineWidth: 1))
    }

    private var accountSummary: some View {
        let authentication = model.authenticationStatus(for: model.selectedProfileID)
        return HStack(spacing: 12) {
            ZStack {
                RoundedRectangle(cornerRadius: 13, style: .continuous)
                    .fill(
                        LinearGradient(
                            colors: [AppTheme.cyan.opacity(0.95), AppTheme.blue.opacity(0.95)],
                            startPoint: .topLeading,
                            endPoint: .bottomTrailing))
                    .frame(width: 46, height: 46)
                Image(systemName: "sparkles")
                    .font(.system(size: 18, weight: .semibold))
                    .foregroundStyle(.white)
            }
            VStack(alignment: .leading, spacing: 3) {
                HStack(spacing: 6) {
                    Text(model.selectedProfile.alias)
                        .font(.system(size: 16, weight: .bold))
                        .lineLimit(1)
                        .minimumScaleFactor(0.72)
                    if model.activeProfileID == model.selectedProfileID {
                        Text("현재 사용 중")
                            .font(.system(size: 9, weight: .bold))
                            .foregroundStyle(AppTheme.green)
                            .padding(.horizontal, 6)
                            .padding(.vertical, 3)
                            .background(AppTheme.green.opacity(0.12), in: Capsule())
                    }
                }
                Text(snapshot?.email ?? model.selectedProfile.email)
                    .font(.system(size: 11))
                    .foregroundStyle(AppTheme.muted)
                    .lineLimit(1)
                HStack(spacing: 4) {
                    Image(systemName: authentication.systemImage)
                    Text(authentication.title)
                        .lineLimit(1)
                }
                .font(.system(size: 9, weight: .medium))
                .foregroundStyle(authenticationColor(authentication))
            }
            Spacer()
            VStack(alignment: .trailing, spacing: 3) {
                Text(snapshot?.plan ?? "Codex")
                    .font(.system(size: 12, weight: .semibold))
                statusBadge
            }
        }
        .appCard()
    }

    @ViewBuilder
    private var statusBadge: some View {
        let authentication = model.authenticationStatus(for: model.selectedProfileID)
        if authentication == .checking {
            HStack(spacing: 4) {
                ProgressView().controlSize(.mini)
                Text("인증 확인")
            }
            .font(.system(size: 10, weight: .medium))
            .foregroundStyle(AppTheme.muted)
        } else {
            Label(authentication.shortTitle, systemImage: authentication.systemImage)
                .font(.system(size: 10, weight: .semibold))
                .foregroundStyle(authenticationColor(authentication))
        }
    }

    private func authenticationColor(
        _ status: ProfileAuthenticationStatus,
        selected: Bool = false) -> Color
    {
        switch status {
        case .checking:
            return selected ? Color.white.opacity(0.72) : AppTheme.muted
        case .authenticated:
            return selected ? Color.white.opacity(0.78) : AppTheme.green
        case .reauthenticationRequired:
            return AppTheme.yellow
        case .unverified:
            return AppTheme.red
        }
    }

    @ViewBuilder
    private var reauthenticationButton: some View {
        let authentication = model.authenticationStatus(for: model.selectedProfileID)
        if authentication.needsReauthentication {
            Button {
                model.beginLogin(profileID: model.selectedProfileID)
            } label: {
                HStack(spacing: 9) {
                    Image(systemName: "person.crop.circle.badge.exclamationmark")
                        .font(.system(size: 14, weight: .semibold))
                    VStack(alignment: .leading, spacing: 2) {
                        Text("\(model.selectedProfile.alias) 계정 재로그인")
                            .font(.system(size: 11, weight: .bold))
                        Text("전용 Chromium에서 Codex 인증을 다시 연결합니다.")
                            .font(.system(size: 8.5, weight: .medium))
                            .foregroundStyle(Color.white.opacity(0.58))
                    }
                    Spacer(minLength: 4)
                    Text("재로그인")
                        .font(.system(size: 10, weight: .bold))
                    Image(systemName: "chevron.right")
                        .font(.system(size: 9, weight: .bold))
                }
                .foregroundStyle(AppTheme.yellow)
                .padding(.horizontal, 12)
                .frame(maxWidth: .infinity, minHeight: 42)
                .background(
                    AppTheme.yellow.opacity(0.11),
                    in: RoundedRectangle(cornerRadius: 11, style: .continuous))
                .overlay(
                    RoundedRectangle(cornerRadius: 11, style: .continuous)
                        .stroke(AppTheme.yellow.opacity(0.30), lineWidth: 1))
            }
            .buttonStyle(.plain)
            .disabled(
                model.managementActionsDisabled || model.isRefreshing || model.isSwitching
                    || model.isManagingProfiles || model.isMaintainingAuth)
            .accessibilityLabel("\(model.selectedProfile.alias) 계정 재로그인")
            .accessibilityHint("전용 Chromium 로그인 창을 엽니다")
            .accessibilityIdentifier("reauthentication-button")
        }
    }

    @ViewBuilder
    private var usageCard: some View {
        let metrics = visibleUsageMetrics
        VStack(alignment: .leading, spacing: 10) {
            if snapshot == nil, case let .failed(_, message, loginRequired) = state {
                HStack(spacing: 8) {
                    Image(systemName: loginRequired ? "lock.fill" : "exclamationmark.triangle.fill")
                        .font(.system(size: 10, weight: .semibold))
                    Text(loginRequired ? "재로그인이 필요합니다." : message)
                        .font(.system(size: 10, weight: .medium))
                        .lineLimit(1)
                        .truncationMode(.tail)
                    Spacer(minLength: 2)
                    if loginRequired { Text("설정에서 로그인").font(.system(size: 9, weight: .semibold)) }
                }
                .frame(height: 28)
                .foregroundStyle(loginRequired ? AppTheme.yellow : AppTheme.red)
                .help(message)
            } else if snapshot == nil {
                HStack(spacing: 7) {
                    ProgressView().controlSize(.small)
                    Text("사용량 확인 중…")
                        .font(.system(size: 10, weight: .medium))
                        .foregroundStyle(AppTheme.muted)
                    Spacer()
                }
            }

            ForEach(Array(metrics.enumerated()), id: \.element.id) { index, metric in
                if index > 0 {
                    Divider().overlay(Color.white.opacity(0.06))
                }
                QuotaUsageRow(
                    item: metric.item,
                    window: metric.window,
                    baseTint: metric.baseTint)
            }

            if let updatedAt = snapshot?.updatedAt {
                Text("마지막 갱신 \(updatedAt.formatted(date: .omitted, time: .shortened))")
                    .font(.system(size: 9))
                    .foregroundStyle(Color.white.opacity(0.36))
                    .frame(maxWidth: .infinity, alignment: .trailing)
            }
        }
        .appCard()
    }

    private var resetCreditsCard: some View {
        resetCreditsCardContent(
            count: snapshot?.resetCredits,
            expirations: snapshot?.resetCreditExpirations ?? [])
    }

    private func resetCreditsCardContent(count: Int?, expirations: [Date]) -> some View {
        TimelineView(.periodic(from: .now, by: 60)) { context in
            let sortedExpirations = expirations.sorted()
            let descriptions = sortedExpirations.map {
                Formatting.resetCreditExpiryDescription($0, relativeTo: context.date)
            }
            let remainingCount = max(0, sortedExpirations.count - 1)
            let remainingSuffix = remainingCount > 0 ? " · 외 \(remainingCount)회" : ""

            HStack(spacing: 11) {
                ZStack {
                    Circle()
                        .fill(AppTheme.yellow.opacity(0.16))
                        .frame(width: 32, height: 32)
                    Image(systemName: "ticket.fill")
                        .font(.system(size: 13, weight: .semibold))
                        .foregroundStyle(AppTheme.yellow)
                }

                VStack(alignment: .leading, spacing: 2) {
                    Text("초기화권")
                        .font(.system(size: 9, weight: .semibold))
                        .foregroundStyle(Color.white.opacity(0.66))
                    HStack(alignment: .firstTextBaseline, spacing: 3) {
                        Text(count.map { "\($0)회" } ?? "—")
                            .font(.system(size: 16, weight: .bold, design: .rounded))
                            .foregroundStyle(AppTheme.yellow)
                            .monospacedDigit()
                        Text(count == nil ? "확인 중" : "보유")
                            .font(.system(size: 9, weight: .semibold))
                            .foregroundStyle(AppTheme.muted)
                    }
                }
                .layoutPriority(1)

                Spacer(minLength: 4)

                VStack(alignment: .trailing, spacing: 3) {
                    Text("다음 만료")
                        .font(.system(size: 8, weight: .semibold))
                        .foregroundStyle(Color.white.opacity(0.42))

                    if let nextDescription = descriptions.first {
                        ViewThatFits(in: .horizontal) {
                            Text(nextDescription + remainingSuffix)
                            Text(nextDescription)
                        }
                        .font(.system(size: 10, weight: .semibold, design: .rounded))
                        .foregroundStyle(.primary)
                        .monospacedDigit()
                        .lineLimit(1)
                    } else {
                        Text(count == nil ? "확인 중" : "만료 정보 없음")
                            .font(.system(size: 9, weight: .medium))
                            .foregroundStyle(AppTheme.muted)
                    }
                }
            }
            .padding(.horizontal, 11)
            .padding(.vertical, 9)
            .frame(maxWidth: .infinity)
            .background(
                LinearGradient(
                    colors: [AppTheme.yellow.opacity(0.12), AppTheme.yellow.opacity(0.035)],
                    startPoint: .leading,
                    endPoint: .trailing),
                in: RoundedRectangle(cornerRadius: 11, style: .continuous))
            .overlay(
                RoundedRectangle(cornerRadius: 11, style: .continuous)
                    .stroke(AppTheme.yellow.opacity(0.24), lineWidth: 1))
            .shadow(color: AppTheme.yellow.opacity(0.07), radius: 7, y: 2)
            .accessibilityElement(children: .combine)
            .accessibilityLabel(
                "초기화권 \(count.map(String.init) ?? "미확인")회, 만료 \(descriptions.joined(separator: ", "))")
            .accessibilityIdentifier("reset-credit-row")
        }
    }

    private var tokenUsageCard: some View {
        let snapshot = model.tokenUsageSnapshot
        let counts = snapshot?.counts ?? TokenCounts()
        let hasUnpriced = (snapshot?.unpricedTokens ?? 0) > 0
        return VStack(alignment: .leading, spacing: 9) {
            HStack(alignment: .firstTextBaseline) {
                VStack(alignment: .leading, spacing: 2) {
                    Text("최근 30일 사용량과 적용 계정")
                        .font(.system(size: 13, weight: .semibold))
                    Text("모든 기기 합계 · API 요금 환산 추정치")
                        .font(.system(size: 9))
                        .foregroundStyle(AppTheme.muted)
                }
                Spacer()
                if model.isCollectingTokenUsage {
                    ProgressView().controlSize(.small)
                } else if let snapshot {
                    Text("\(snapshot.reachableDeviceCount)/\(snapshot.totalDeviceCount)대")
                        .font(.system(size: 9, weight: .semibold, design: .rounded))
                        .foregroundStyle(
                            snapshot.reachableDeviceCount == snapshot.totalDeviceCount
                                ? AppTheme.green : AppTheme.yellow)
                }
            }

            HStack(alignment: .firstTextBaseline, spacing: 7) {
                Text(TokenUsageFormatting.tokens(counts.totalTokens))
                    .font(.system(size: 22, weight: .bold, design: .rounded))
                    .monospacedDigit()
                Text("토큰")
                    .font(.system(size: 10, weight: .semibold))
                    .foregroundStyle(AppTheme.muted)
                Spacer()
                Text(snapshot.map { TokenUsageFormatting.dollars($0.estimatedCostUSD) + (hasUnpriced ? "+" : "") } ?? "—")
                    .font(.system(size: 17, weight: .bold, design: .rounded))
                    .foregroundStyle(AppTheme.cyan)
                    .monospacedDigit()
            }

            if let snapshot {
                Divider().overlay(Color.white.opacity(0.06))

                VStack(spacing: 7) {
                    ForEach(snapshot.devices) { device in
                        let status = model.devices.first(where: { $0.id == device.id })
                        CombinedDeviceRow(
                            usage: device,
                            status: status,
                            profile: status?.profileID.flatMap(model.profile(for:)))
                    }
                }

                HStack(spacing: 6) {
                    if snapshot.priorityPricedTokens > 0 {
                        Label("API Priority 단가", systemImage: "bolt.fill")
                            .foregroundStyle(AppTheme.yellow)
                    }
                    if hasUnpriced {
                        Label(
                            "미공개 가격 \(TokenUsageFormatting.tokens(snapshot.unpricedTokens))",
                            systemImage: "exclamationmark.triangle.fill")
                            .foregroundStyle(AppTheme.yellow)
                    }
                    Spacer()
                    Text(snapshot.collectedAt.formatted(date: .omitted, time: .shortened))
                        .foregroundStyle(AppTheme.muted)
                }
                .font(.system(size: 8, weight: .semibold))
            } else if let error = model.tokenUsageError {
                Text("사용량 수집 실패: \(error)")
                    .font(.system(size: 9, weight: .medium))
                    .foregroundStyle(AppTheme.red)
                    .lineLimit(2)
            } else {
                Text("기기 세션 로그를 처음 집계하고 있습니다…")
                    .font(.system(size: 9, weight: .medium))
                    .foregroundStyle(AppTheme.muted)
            }
        }
        .appCard()
        .help("최근 30일의 세션 이벤트만 합산한 추정치입니다. 모델별 공개 API 단가를 적용하고, priority/fast 세션에는 해당 모델의 API Priority 단가를 적용하며, 미공개 모델은 제외합니다.")
        .accessibilityIdentifier("token-usage-card")
    }

    private var switchButton: some View {
        let matching = model.devices.filter { $0.isReachable && $0.profileID == model.selectedProfileID }.count
        let total = model.configuredNodeCount
        return Button {
            let profileID = model.selectedProfileID
            Task { await model.switchAll(to: profileID) }
        } label: {
            HStack(spacing: 9) {
                if model.isSwitching {
                    ProgressView().controlSize(.small)
                } else {
                    Image(systemName: matching == total ? "checkmark.circle.fill" : "arrow.triangle.2.circlepath")
                }
                Text(model.isSwitching
                    ? "\(total)대 장비 전환 중…"
                    : matching == total
                        ? "이미 모든 장비에 적용됨"
                        : "\(total)대 모두 \(model.selectedProfile.alias) 계정으로 전환")
                    .font(.system(size: 12, weight: .semibold))
            }
            .frame(maxWidth: .infinity, minHeight: 38)
        }
        .buttonStyle(.borderedProminent)
        .tint(matching == total ? AppTheme.green.opacity(0.65) : AppTheme.blue)
        .disabled(
            model.managementActionsDisabled || model.selectedProfileID <= 0 || model.isSwitching
                || model.isMaintainingAuth || matching == total || state.needsLogin)
        .accessibilityHint("이 Mac과 설정에 등록된 SSH 장비의 계정을 함께 전환합니다")
        .accessibilityIdentifier("switch-button")
    }

    private var footer: some View {
        HStack(spacing: 5) {
            Button {
                presentSettings()
            } label: {
                Image(systemName: "gearshape")
            }
            .help("설정")
            .accessibilityLabel("설정")
            .accessibilityIdentifier("settings-button")
            .buttonStyle(FooterActionButtonStyle())

            Button {
                Task { await model.refreshAll() }
            } label: {
                if model.isRefreshing {
                    ProgressView().controlSize(.small)
                } else {
                    Image(systemName: "arrow.clockwise")
                }
            }
            .help("사용량과 기기 상태 새로고침")
            .accessibilityLabel("사용량과 기기 상태 새로고침")
            .accessibilityIdentifier("refresh-button")
            .buttonStyle(FooterActionButtonStyle())
            .disabled(
                model.isRefreshing || model.isSwitching || model.isManagingProfiles
                    || model.managementActionsDisabled)

            Spacer()

            Button {
                model.quit()
            } label: {
                Image(systemName: "power")
            }
            .help("Codex SyncBar 종료")
            .accessibilityIdentifier("quit-button")
            .buttonStyle(FooterActionButtonStyle(tint: AppTheme.red))
        }
        .font(.system(size: 10, weight: .medium))
        .padding(.horizontal, 14)
        .frame(height: 42)
        .background(AppTheme.card.opacity(0.86))
        .overlay(alignment: .top) { Divider().overlay(Color.white.opacity(0.08)) }
    }

}

private struct FooterActionButtonStyle: ButtonStyle {
    var tint: Color = AppTheme.blue

    func makeBody(configuration: Configuration) -> some View {
        configuration.label
            .foregroundStyle(configuration.isPressed ? Color.white : AppTheme.muted)
            .padding(.horizontal, 10)
            .frame(minHeight: 30)
            .background(
                RoundedRectangle(cornerRadius: 8, style: .continuous)
                    .fill(configuration.isPressed ? tint.opacity(0.24) : Color.clear))
            .overlay(
                RoundedRectangle(cornerRadius: 8, style: .continuous)
                    .stroke(configuration.isPressed ? tint.opacity(0.45) : Color.clear, lineWidth: 1))
            .scaleEffect(configuration.isPressed ? 0.94 : 1)
            .opacity(configuration.isPressed ? 0.86 : 1)
            .animation(.easeOut(duration: 0.08), value: configuration.isPressed)
            .contentShape(RoundedRectangle(cornerRadius: 8, style: .continuous))
    }
}

private struct QuotaUsageRow: View {
    let item: UsageDisplayItem
    let window: UsageWindow?
    let baseTint: Color

    private var tint: Color {
        guard let remaining = window?.remainingPercent else { return AppTheme.muted }
        if remaining <= 10 { return AppTheme.red }
        if remaining <= 25 { return AppTheme.yellow }
        return baseTint
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 6) {
            HStack(alignment: .firstTextBaseline) {
                Label(item.title, systemImage: item.icon)
                    .font(.system(size: 11, weight: .semibold))
                    .foregroundStyle(AppTheme.muted)
                Spacer()
                Text(window.map { Formatting.resetDescription($0.resetsAt) } ?? "한도 정보 없음")
                    .font(.system(size: 9, weight: .medium))
                    .foregroundStyle(AppTheme.muted)
                    .lineLimit(1)
            }

            Text(window.map { "\(Int($0.remainingPercent.rounded()))% 남음" } ?? "—")
                .font(.system(size: 17, weight: .bold, design: .rounded))
                .foregroundStyle(window == nil ? AppTheme.muted : Color.primary)
                .monospacedDigit()

            QuotaProgressBar(
                title: item.title,
                window: window,
                tint: tint)
        }
        .accessibilityIdentifier("quota-\(item.rawValue)")
    }
}

private struct QuotaProgressBar: View {
    let title: String
    let window: UsageWindow?
    let tint: Color

    var body: some View {
        GeometryReader { proxy in
            ZStack(alignment: .leading) {
                Capsule().fill(Color.white.opacity(0.075))
                if let window {
                    Capsule()
                        .fill(
                            LinearGradient(
                                colors: [tint.opacity(0.72), tint],
                                startPoint: .leading,
                                endPoint: .trailing))
                        .frame(width: max(4, proxy.size.width * window.remainingPercent / 100))
                }
            }
        }
        .frame(height: 8)
        .accessibilityElement()
        .accessibilityLabel(window.map {
            "\(title), \(Int($0.remainingPercent.rounded()))퍼센트 남음, \(Formatting.resetDescription($0.resetsAt))"
        } ?? "\(title), 한도 정보 없음")
    }
}

private struct CombinedDeviceRow: View {
    let usage: DeviceTokenUsage
    let status: DeviceStatus?
    let profile: AccountProfile?

    private var icon: String {
        switch usage.id {
        case "macbook": "laptopcomputer"
        case "ml": "server.rack"
        case "rogally": "gamecontroller.fill"
        default: "desktopcomputer"
        }
    }

    private var isReachable: Bool {
        usage.isReachable || status?.isReachable == true
    }

    private var accountDescription: String {
        guard let status else { return "적용 계정 확인 중" }
        guard status.isReachable else { return "연결 안 됨" }
        return profile?.email ?? status.profileID.map { "미등록 계정 \($0)" } ?? "적용 계정 미확인"
    }

    private var accountTint: Color {
        guard let status else { return AppTheme.muted }
        guard status.isReachable else { return AppTheme.red }
        return profile == nil ? AppTheme.yellow : AppTheme.green
    }

    private var accountIcon: String {
        status?.isReachable == true && profile != nil
            ? "checkmark.circle.fill"
            : "exclamationmark.circle.fill"
    }

    var body: some View {
        HStack(alignment: .top, spacing: 8) {
            Image(systemName: icon)
                .font(.system(size: 10, weight: .medium))
                .foregroundStyle(isReachable ? AppTheme.muted : AppTheme.red)
                .frame(width: 16, height: 16)

            VStack(alignment: .leading, spacing: 2) {
                HStack(spacing: 6) {
                    Text(usage.displayName)
                        .font(.system(size: 10, weight: .semibold))
                        .lineLimit(1)
                    Spacer(minLength: 6)
                    if let summary = usage.summary {
                        Text(TokenUsageFormatting.tokens(summary.totalTokens))
                        Text(TokenUsageFormatting.dollars(usage.estimatedCostUSD) + (usage.unpricedTokens > 0 ? "+" : ""))
                            .foregroundStyle(AppTheme.muted)
                    } else {
                        Text("사용량 수집 실패")
                            .foregroundStyle(AppTheme.red)
                    }
                }
                .font(.system(size: 9, weight: .semibold, design: .rounded))
                .monospacedDigit()

                HStack(spacing: 4) {
                    Text("적용")
                        .foregroundStyle(Color.white.opacity(0.34))
                    Text(accountDescription)
                        .foregroundStyle(accountTint)
                        .lineLimit(1)
                        .truncationMode(.middle)
                    Spacer(minLength: 4)
                    Image(systemName: accountIcon)
                        .foregroundStyle(accountTint)
                }
                .font(.system(size: 8.5, weight: .medium))
            }
        }
        .frame(minHeight: 30)
        .accessibilityElement(children: .combine)
    }
}

struct BannerView: View {
    let banner: AppBanner

    private var tint: Color {
        switch banner.style {
        case .info: AppTheme.blue
        case .success: AppTheme.green
        case .warning: AppTheme.yellow
        case .error: AppTheme.red
        }
    }

    private var icon: String {
        switch banner.style {
        case .info: "info.circle.fill"
        case .success: "checkmark.circle.fill"
        case .warning: "exclamationmark.triangle.fill"
        case .error: "xmark.octagon.fill"
        }
    }

    var body: some View {
        HStack(alignment: .top, spacing: 8) {
            Image(systemName: icon)
                .foregroundStyle(tint)
            Text(banner.message)
                .font(.system(size: 10, weight: .medium))
                .fixedSize(horizontal: false, vertical: true)
            Spacer(minLength: 0)
        }
        .padding(10)
        .background(tint.opacity(0.10), in: RoundedRectangle(cornerRadius: 10, style: .continuous))
        .overlay(
            RoundedRectangle(cornerRadius: 10, style: .continuous)
                .stroke(tint.opacity(0.20), lineWidth: 1))
    }
}
