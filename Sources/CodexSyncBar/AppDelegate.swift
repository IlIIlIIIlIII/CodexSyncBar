import AppKit
import SwiftUI

@MainActor
final class AppDelegate: NSObject, NSApplicationDelegate {
    private var previewWindow: NSWindow?
    private var previewModel: AppModel?
    private var settingsWindowController: SettingsWindowController?
    private var readmeCaptureController: ReadmeCaptureController?

    func presentSettings(model: AppModel) {
        model.refreshLaunchAtLoginState()
        let controller: SettingsWindowController
        if let settingsWindowController {
            controller = settingsWindowController
        } else {
            controller = SettingsWindowController(model: model)
            settingsWindowController = controller
        }
        controller.present()
    }

    func applicationDidFinishLaunching(_ notification: Notification) {
        if let command = ReadmeDemoCommand.parse(arguments: CommandLine.arguments) {
            let model = AppModel(readmeDemoFixture: .standard)
            let controller = ReadmeCaptureController(command: command)
            readmeCaptureController = controller
            previewModel = model
            controller.present(model: model)
            return
        }
        if CommandLine.arguments.contains(where: {
            $0.hasPrefix("--readme-demo=") || $0.hasPrefix("--readme-output=")
        }) {
            FileHandle.standardError.write(Data("README UI 캡처 인자가 올바르지 않습니다.\n".utf8))
            exit(EXIT_FAILURE)
        }
        do {
            try BundledHelperInstaller.installFromMainBundleIfPresent()
        } catch {
            let alert = NSAlert()
            alert.alertStyle = .critical
            alert.messageText = "Codex SyncBar helper 설치 실패"
            alert.informativeText = error.localizedDescription
            alert.runModal()
            return
        }
        if let profileArgument = CommandLine.arguments.first(where: { $0.hasPrefix("--login-profile=") }),
           let profileID = Int(profileArgument.split(separator: "=").last ?? ""),
           profileID > 0
        {
            let model = AppModel()
            guard model.profiles.contains(where: { $0.id == profileID }) else { return }
            model.selectProfile(profileID)
            previewModel = model
            model.beginLogin()
            return
        }

        guard CommandLine.arguments.contains("--preview-window") else { return }
        let model = AppModel()
        let content = PopoverView(
            model: model,
            presentSettings: { [weak self] in self?.presentSettings(model: model) },
            onContentHeightChange: { [weak self] height in
                self?.resizePreviewWindow(toContentHeight: height)
            })
        let window = NSWindow(
            contentRect: NSRect(
                x: 0,
                y: 0,
                width: AppLayout.popoverWidth,
                height: AppLayout.popoverPreviewInitialHeight),
            styleMask: [.titled, .closable, .miniaturizable, .fullSizeContentView],
            backing: .buffered,
            defer: false)
        window.title = "Codex SyncBar QA"
        window.titlebarAppearsTransparent = true
        window.isReleasedWhenClosed = false
        previewWindow = window
        window.contentView = NSHostingView(rootView: content)
        window.center()
        window.makeKeyAndOrderFront(nil)
        NSApp.activate(ignoringOtherApps: true)
        previewModel = model
    }

    private func resizePreviewWindow(toContentHeight height: CGFloat) {
        guard let window = previewWindow else { return }
        let oldFrame = window.frame
        let contentRect = NSRect(
            x: 0,
            y: 0,
            width: AppLayout.popoverWidth,
            height: ceil(height))
        var newFrame = window.frameRect(forContentRect: contentRect)
        newFrame.origin.x = oldFrame.origin.x
        newFrame.origin.y = oldFrame.maxY - newFrame.height
        guard abs(newFrame.height - oldFrame.height) > 0.5 else { return }
        window.setFrame(newFrame, display: true, animate: false)
    }
}
