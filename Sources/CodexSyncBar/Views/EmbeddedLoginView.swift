import SwiftUI

struct EmbeddedLoginView: View {
    @ObservedObject var coordinator: LoginCoordinator
    let profile: AccountProfile
    let onClose: () -> Void

    @State private var showsAccountResetConfirmation = false
    @State private var resetTask: Task<Void, Never>?

    var body: some View {
        VStack(spacing: 0) {
            header
            Divider().overlay(Color.white.opacity(0.08))

            VStack(spacing: 18) {
                browserIdentity
                statusCard
                persistenceCard
                actions
            }
            .padding(.horizontal, 24)
            .padding(.top, 24)
            .padding(.bottom, 20)
        }
        .background(
            LinearGradient(
                colors: [AppTheme.panel, Color(red: 0.055, green: 0.061, blue: 0.076)],
                startPoint: .top,
                endPoint: .bottom))
        .preferredColorScheme(.dark)
        .frame(width: 560, height: 480)
        .alert("다른 계정으로 연결할까요?", isPresented: $showsAccountResetConfirmation) {
            Button("취소", role: .cancel) {}
            Button("새 Chrome 프로필로 로그인") {
                resetTask?.cancel()
                resetTask = Task { @MainActor in
                    await coordinator.restartWithFreshBrowserProfile(profileID: profile.id)
                    resetTask = nil
                }
            }
        } message: {
            Text("현재 Chrome 로그인 데이터는 삭제하지 않고 백업합니다. 새 로그인이 검증되기 전까지 기존 auth.json도 그대로 유지됩니다.")
        }
        .onDisappear {
            resetTask?.cancel()
            resetTask = nil
        }
    }

    private var header: some View {
        HStack(spacing: 12) {
            ZStack {
                RoundedRectangle(cornerRadius: 11, style: .continuous)
                    .fill(AppTheme.blue.opacity(0.17))
                    .frame(width: 42, height: 42)
                Image(systemName: "lock.shield.fill")
                    .font(.system(size: 18, weight: .semibold))
                    .foregroundStyle(AppTheme.blue)
            }

            VStack(alignment: .leading, spacing: 3) {
                Text("\(profile.alias) 계정 보안 로그인")
                    .font(.system(size: 16, weight: .semibold))
                Text("앱이 관리하는 Chromium 전용 프로필")
                    .font(.system(size: 11))
                    .foregroundStyle(AppTheme.muted)
            }

            Spacer()

            Button {
                coordinator.cancel()
                onClose()
            } label: {
                Image(systemName: "xmark")
                    .font(.system(size: 11, weight: .bold))
                    .frame(width: 28, height: 28)
            }
            .buttonStyle(.borderless)
            .disabled(coordinator.state == .importing)
            .help(coordinator.state == .importing
                ? "인증 파일을 원자적으로 반영하는 동안에는 창을 닫을 수 없습니다."
                : "로그인 창 닫기")
            .accessibilityLabel("로그인 창 닫기")
        }
        .padding(.horizontal, 18)
        .padding(.vertical, 13)
    }

    private var browserIdentity: some View {
        HStack(spacing: 15) {
            ZStack {
                Circle()
                    .fill(AngularGradient(
                        colors: [
                            Color(red: 0.92, green: 0.25, blue: 0.20),
                            Color(red: 0.98, green: 0.74, blue: 0.12),
                            Color(red: 0.18, green: 0.67, blue: 0.32),
                            Color(red: 0.92, green: 0.25, blue: 0.20),
                        ],
                        center: .center))
                    .frame(width: 56, height: 56)
                Circle()
                    .fill(Color(red: 0.16, green: 0.47, blue: 0.91))
                    .frame(width: 27, height: 27)
                    .overlay(Circle().stroke(Color.white.opacity(0.88), lineWidth: 3))
            }

            VStack(alignment: .leading, spacing: 4) {
                Text(coordinator.browserDisplayName)
                    .font(.system(size: 17, weight: .semibold))
                Text("Google 로그인 · 패스키 · Touch ID 지원")
                    .font(.system(size: 12))
                    .foregroundStyle(AppTheme.muted)
            }

            Spacer()

            Text("PROFILE \(profile.id)")
                .font(.system(size: 10, weight: .bold, design: .monospaced))
                .foregroundStyle(AppTheme.cyan)
                .padding(.horizontal, 9)
                .padding(.vertical, 5)
                .background(AppTheme.cyan.opacity(0.10), in: Capsule())
        }
    }

    private var statusCard: some View {
        HStack(spacing: 12) {
            statusSymbol

            VStack(alignment: .leading, spacing: 4) {
                Text(statusTitle)
                    .font(.system(size: 13, weight: .semibold))
                Text(coordinator.rawStatus)
                    .font(.system(size: 11))
                    .foregroundStyle(statusIsFailure ? AppTheme.red.opacity(0.92) : AppTheme.muted)
                    .lineLimit(2)
                    .fixedSize(horizontal: false, vertical: true)
            }
            Spacer(minLength: 0)
        }
        .padding(14)
        .frame(maxWidth: .infinity, alignment: .leading)
        .background(AppTheme.card, in: RoundedRectangle(cornerRadius: 14, style: .continuous))
        .overlay(
            RoundedRectangle(cornerRadius: 14, style: .continuous)
                .stroke(statusIsFailure ? AppTheme.red.opacity(0.28) : AppTheme.border, lineWidth: 1))
    }

    @ViewBuilder
    private var statusSymbol: some View {
        switch coordinator.state {
        case .starting, .resetting, .validating, .importing:
            ProgressView()
                .controlSize(.small)
                .frame(width: 24, height: 24)
        case .waiting:
            Image(systemName: "arrow.up.forward.app.fill")
                .font(.system(size: 19))
                .foregroundStyle(AppTheme.blue)
                .frame(width: 24, height: 24)
        case .completed:
            Image(systemName: "checkmark.circle.fill")
                .font(.system(size: 20))
                .foregroundStyle(AppTheme.green)
                .frame(width: 24, height: 24)
        case .failed:
            Image(systemName: "exclamationmark.triangle.fill")
                .font(.system(size: 19))
                .foregroundStyle(AppTheme.red)
                .frame(width: 24, height: 24)
        case .idle:
            Image(systemName: "lock.circle.fill")
                .font(.system(size: 20))
                .foregroundStyle(AppTheme.muted)
                .frame(width: 24, height: 24)
        }
    }

    private var persistenceCard: some View {
        VStack(alignment: .leading, spacing: 10) {
            guaranteeRow(icon: "person.crop.circle.badge.checkmark", text: "계정별 Chrome 쿠키와 패스키 세션을 영구 분리")
            guaranteeRow(icon: "arrow.triangle.2.circlepath", text: "앱 업데이트 후에도 같은 브라우저 프로필 재사용")
            guaranteeRow(icon: "externaldrive.badge.checkmark", text: "새 로그인 검증 전에는 기존 auth.json을 변경하지 않음")

            HStack(spacing: 6) {
                Image(systemName: "folder.fill")
                Text(coordinator.browserProfileDisplayPath(profileID: profile.id))
                    .lineLimit(1)
                    .truncationMode(.middle)
            }
            .font(.system(size: 9, design: .monospaced))
            .foregroundStyle(Color.white.opacity(0.40))
            .padding(.top, 2)
        }
        .padding(.horizontal, 14)
        .padding(.vertical, 12)
        .frame(maxWidth: .infinity, alignment: .leading)
        .background(Color.white.opacity(0.035), in: RoundedRectangle(cornerRadius: 13, style: .continuous))
    }

    private func guaranteeRow(icon: String, text: String) -> some View {
        HStack(spacing: 9) {
            Image(systemName: icon)
                .font(.system(size: 12, weight: .semibold))
                .foregroundStyle(AppTheme.green)
                .frame(width: 17)
            Text(text)
                .font(.system(size: 11))
                .foregroundStyle(Color.white.opacity(0.78))
        }
    }

    private var actions: some View {
        HStack(spacing: 10) {
            Button {
                showsAccountResetConfirmation = true
            } label: {
                Label("다른 계정으로 로그인", systemImage: "person.2.badge.gearshape")
                    .frame(maxWidth: .infinity)
            }
            .buttonStyle(.bordered)
            .controlSize(.large)
            .disabled(isBusy)

            Button {
                if statusIsFailure {
                    coordinator.retry()
                } else {
                    coordinator.reopenBrowser()
                }
            } label: {
                Label(primaryButtonTitle, systemImage: statusIsFailure ? "arrow.clockwise" : "arrow.up.forward.app")
                    .frame(maxWidth: .infinity)
            }
            .buttonStyle(.borderedProminent)
            .controlSize(.large)
            .disabled(isBusy || (!statusIsFailure && coordinator.authenticationURL == nil))
        }
    }

    private var isBusy: Bool {
        switch coordinator.state {
        case .starting, .resetting, .validating, .importing:
            true
        default:
            false
        }
    }

    private var statusIsFailure: Bool {
        if case .failed = coordinator.state { return true }
        return false
    }

    private var statusTitle: String {
        switch coordinator.state {
        case .idle: "로그인 대기"
        case .starting: "보안 연결 준비 중"
        case .resetting: "새 Chrome 프로필 준비 중"
        case .waiting: "Chrome 로그인 창이 열렸습니다"
        case .validating: "Codex 서버 인증 확인 중"
        case .importing: "인증 정보 검증 중"
        case .completed: "연결 완료"
        case .failed: "로그인에 실패했습니다"
        }
    }

    private var primaryButtonTitle: String {
        if statusIsFailure { return "다시 시도" }
        return "Chrome 창 다시 열기"
    }
}
