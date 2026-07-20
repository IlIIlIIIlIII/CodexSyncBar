import AppKit
import SwiftUI

@MainActor
final class LoginWindowController: NSWindowController, NSWindowDelegate {
    private let coordinator: LoginCoordinator
    private var onDismiss: (() -> Void)?

    init(
        coordinator: LoginCoordinator,
        profile: AccountProfile,
        onDismiss: @escaping () -> Void)
    {
        self.coordinator = coordinator
        self.onDismiss = onDismiss

        let window = NSWindow(
            contentRect: NSRect(x: 0, y: 0, width: 560, height: 480),
            styleMask: [.titled, .closable, .miniaturizable, .fullSizeContentView],
            backing: .buffered,
            defer: false)
        window.title = "Codex SyncBar 로그인"
        window.titleVisibility = .hidden
        window.titlebarAppearsTransparent = true
        window.isMovableByWindowBackground = true
        window.isReleasedWhenClosed = false
        window.hidesOnDeactivate = false
        window.level = .floating
        window.collectionBehavior = [.moveToActiveSpace, .fullScreenAuxiliary]
        window.minSize = NSSize(width: 560, height: 480)
        window.maxSize = NSSize(width: 560, height: 480)
        window.backgroundColor = NSColor(
            red: 0.075,
            green: 0.078,
            blue: 0.088,
            alpha: 1)

        super.init(window: window)
        window.delegate = self
        window.contentView = NSHostingView(rootView: EmbeddedLoginView(
            coordinator: coordinator,
            profile: profile,
            onClose: { [weak self] in self?.window?.performClose(nil) }))
        window.center()
    }

    @available(*, unavailable)
    required init?(coder: NSCoder) {
        fatalError("init(coder:) has not been implemented")
    }

    func present() {
        showWindow(nil)
        window?.makeKeyAndOrderFront(nil)
        NSApp.activate(ignoringOtherApps: true)
    }

    func windowWillClose(_ notification: Notification) {
        if coordinator.state != .completed {
            coordinator.cancel(silent: true)
        }
        let callback = onDismiss
        onDismiss = nil
        callback?()
    }

    func windowShouldClose(_ sender: NSWindow) -> Bool {
        coordinator.state != .importing
    }
}
