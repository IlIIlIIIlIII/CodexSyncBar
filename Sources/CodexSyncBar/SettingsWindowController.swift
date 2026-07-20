import AppKit
import SwiftUI

@MainActor
final class SettingsWindowController: NSWindowController {
    init(model: AppModel) {
        let window = NSWindow(
            contentRect: NSRect(x: 0, y: 0, width: 760, height: 580),
            styleMask: [.titled, .closable, .miniaturizable, .resizable, .fullSizeContentView],
            backing: .buffered,
            defer: false)
        window.identifier = NSUserInterfaceItemIdentifier("settings")
        window.title = "Codex SyncBar 설정"
        window.titlebarAppearsTransparent = true
        window.isReleasedWhenClosed = false
        window.hidesOnDeactivate = false
        window.level = .floating
        window.collectionBehavior = [.moveToActiveSpace, .fullScreenAuxiliary]
        window.minSize = NSSize(width: 720, height: 540)
        window.backgroundColor = NSColor(
            red: 0.075,
            green: 0.078,
            blue: 0.088,
            alpha: 1)
        window.contentView = NSHostingView(rootView: SettingsView(model: model))
        window.setFrameAutosaveName("CodexSyncBarSettingsWindow")

        super.init(window: window)
        if !window.setFrameUsingName("CodexSyncBarSettingsWindow") { window.center() }
    }

    @available(*, unavailable)
    required init?(coder: NSCoder) {
        fatalError("init(coder:) has not been implemented")
    }

    func present() {
        guard let window else { return }
        window.level = .floating
        window.hidesOnDeactivate = false
        NSApp.activate(ignoringOtherApps: true)
        showWindow(nil)
        window.makeKeyAndOrderFront(nil)
        window.orderFrontRegardless()
    }
}
