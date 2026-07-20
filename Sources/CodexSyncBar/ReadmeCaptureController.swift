import AppKit
import Darwin
import SwiftUI

enum ReadmeCaptureOutputValidator {
    static func validatedOutputURL(
        _ requestedURL: URL,
        fileManager: FileManager = .default) throws -> URL
    {
        let outputURL = requestedURL.standardizedFileURL
        guard outputURL.isFileURL,
              outputURL.pathExtension.lowercased() == "png"
        else {
            throw AppError.processFailed("README 캡처 출력은 절대 경로의 PNG 파일이어야 합니다.")
        }

        let parentURL = outputURL.deletingLastPathComponent()
        let parentValues = try parentURL.resourceValues(forKeys: [
            .isDirectoryKey,
            .isSymbolicLinkKey,
        ])
        guard parentValues.isDirectory == true,
              parentValues.isSymbolicLink != true
        else {
            throw AppError.processFailed("README 캡처 출력 디렉터리가 안전하지 않습니다.")
        }

        if pathEntryExists(outputURL) {
            let outputValues = try outputURL.resourceValues(forKeys: [
                .isRegularFileKey,
                .isSymbolicLinkKey,
            ])
            guard outputValues.isRegularFile == true,
                  outputValues.isSymbolicLink != true
            else {
                throw AppError.processFailed("README 캡처 출력 파일이 안전하지 않습니다.")
            }
        }
        return outputURL
    }

    private static func pathEntryExists(_ url: URL) -> Bool {
        var metadata = stat()
        return lstat(url.path, &metadata) == 0
    }
}

@MainActor
final class ReadmeCaptureController {
    private let command: ReadmeDemoCommand
    private var window: NSWindow?
    private var captureTask: Task<Void, Never>?

    init(command: ReadmeDemoCommand) {
        self.command = command
    }

    deinit {
        captureTask?.cancel()
    }

    func present(model: AppModel) {
        let hostingView: NSView
        let contentSize: NSSize
        switch command.screen {
        case .popover:
            let view = PopoverView(model: model, presentSettings: {})
            let hosting = NSHostingView(rootView: view)
            hosting.frame = NSRect(x: 0, y: 0, width: AppLayout.popoverWidth, height: 1_600)
            hosting.layoutSubtreeIfNeeded()
            let fittingHeight = max(1, ceil(hosting.fittingSize.height))
            contentSize = NSSize(width: AppLayout.popoverWidth, height: fittingHeight)
            hostingView = hosting
        case .settings:
            let size = NSSize(width: 720, height: 300)
            let hosting = NSHostingView(rootView: SettingsView(
                model: model,
                readmeDetailOnly: true))
            hosting.frame = NSRect(origin: .zero, size: size)
            contentSize = size
            hostingView = hosting
        }

        let window = NSWindow(
            contentRect: NSRect(origin: .zero, size: contentSize),
            styleMask: [.borderless],
            backing: .buffered,
            defer: false)
        window.isReleasedWhenClosed = false
        window.appearance = NSAppearance(named: .darkAqua)
        window.backgroundColor = NSColor(calibratedWhite: 0.055, alpha: 1)
        window.isOpaque = true
        window.contentView = hostingView
        window.setContentSize(contentSize)
        window.center()
        window.makeKeyAndOrderFront(nil)
        NSApp.activate(ignoringOtherApps: true)
        self.window = window

        captureTask = Task { [weak self] in
            try? await Task.sleep(nanoseconds: 900_000_000)
            guard !Task.isCancelled, let self else { return }
            do {
                try self.capturePNG()
                NSApp.terminate(nil)
            } catch {
                FileHandle.standardError.write(
                    Data("README UI 캡처 실패: \(error.localizedDescription)\n".utf8))
                exit(EXIT_FAILURE)
            }
        }
    }

    private func capturePNG() throws {
        guard let contentView = window?.contentView else {
            throw AppError.processFailed("README 캡처 창을 찾지 못했습니다.")
        }
        contentView.layoutSubtreeIfNeeded()
        let bounds = contentView.bounds
        guard bounds.width > 0,
              bounds.height > 0,
              let representation = contentView.bitmapImageRepForCachingDisplay(in: bounds)
        else {
            throw AppError.processFailed("README 캡처 화면 크기가 올바르지 않습니다.")
        }
        contentView.cacheDisplay(in: bounds, to: representation)
        guard let data = representation.representation(using: .png, properties: [:]) else {
            throw AppError.processFailed("README 캡처 PNG를 인코딩하지 못했습니다.")
        }
        let outputURL = try ReadmeCaptureOutputValidator.validatedOutputURL(command.outputURL)
        try data.write(to: outputURL, options: .atomic)
    }
}
