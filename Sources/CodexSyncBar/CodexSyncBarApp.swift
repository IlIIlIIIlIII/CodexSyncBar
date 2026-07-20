import SwiftUI

@main
struct CodexSyncBarApp: App {
    @NSApplicationDelegateAdaptor(AppDelegate.self) private var appDelegate
    @StateObject private var model: AppModel

    init() {
        let model = ReadmeDemoCommand.parse(arguments: CommandLine.arguments) == nil
            ? AppModel()
            : AppModel(readmeDemoFixture: .standard)
        _model = StateObject(wrappedValue: model)
        guard !Self.isSpecialLaunch else { return }
        Task { @MainActor in
            await model.start()
        }
    }

    var body: some Scene {
        MenuBarExtra {
            PopoverView(
                model: model,
                presentSettings: { appDelegate.presentSettings(model: model) })
        } label: {
            Text(model.menuTitle)
                .monospacedDigit()
                .font(.system(size: 11, weight: .semibold, design: .rounded))
                .frame(height: 16, alignment: .center)
                .accessibilityLabel("Codex SyncBar, \(model.menuTitle)")
                .task {
                    guard !Self.isSpecialLaunch else { return }
                    await model.start()
                }
        }
        .menuBarExtraStyle(.window)
    }

    private static var isSpecialLaunch: Bool {
        CommandLine.arguments.contains("--preview-window")
            || CommandLine.arguments.contains(where: { $0.hasPrefix("--login-profile=") })
            || CommandLine.arguments.contains(where: { $0.hasPrefix("--readme-demo=") })
    }
}
