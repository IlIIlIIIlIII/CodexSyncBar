import AppKit
import WebKit

@MainActor
final class CursorUsageLoginWindowController: NSWindowController, NSWindowDelegate, WKNavigationDelegate, WKUIDelegate {
    private let usageService: CursorDashboardUsageService
    private let webView: WKWebView
    private var onUsageLoaded: ((CursorMonthlyUsageSnapshot) -> Void)?
    private var onDismiss: ((Bool) -> Void)?
    private var isEvaluating = false
    private var didLoadUsage = false

    init(
        usageService: CursorDashboardUsageService,
        onUsageLoaded: @escaping (CursorMonthlyUsageSnapshot) -> Void,
        onDismiss: @escaping (Bool) -> Void)
    {
        self.usageService = usageService
        self.onUsageLoaded = onUsageLoaded
        self.onDismiss = onDismiss

        let configuration = WKWebViewConfiguration()
        configuration.websiteDataStore = usageService.dataStore
        webView = WKWebView(frame: .zero, configuration: configuration)

        let window = NSWindow(
            contentRect: NSRect(x: 0, y: 0, width: 920, height: 720),
            styleMask: [.titled, .closable, .miniaturizable, .resizable],
            backing: .buffered,
            defer: false)
        window.title = "Cursor 사용량 로그인"
        window.isReleasedWhenClosed = false
        window.hidesOnDeactivate = false
        window.level = .floating
        window.collectionBehavior = [.moveToActiveSpace, .fullScreenAuxiliary]
        window.minSize = NSSize(width: 700, height: 560)
        window.contentView = webView

        super.init(window: window)
        window.delegate = self
        webView.navigationDelegate = self
        webView.uiDelegate = self
        window.center()
    }

    @available(*, unavailable)
    required init?(coder: NSCoder) {
        fatalError("init(coder:) has not been implemented")
    }

    func present() {
        if webView.url == nil, let url = URL(string: "https://cursor.com/dashboard/spending") {
            webView.load(URLRequest(url: url))
        }
        showWindow(nil)
        window?.makeKeyAndOrderFront(nil)
        NSApp.activate(ignoringOtherApps: true)
    }

    func webView(_ webView: WKWebView, didFinish navigation: WKNavigation!) {
        guard !isEvaluating,
              let host = webView.url?.host?.lowercased(),
              host == "cursor.com" || host.hasSuffix(".cursor.com"),
              webView.url?.path.hasPrefix("/dashboard/spending") == true
        else { return }
        isEvaluating = true
        usageService.evaluateUsage(in: webView) { [weak self] result in
            guard let self else { return }
            self.isEvaluating = false
            guard case let .success(snapshot?) = result else { return }
            self.didLoadUsage = true
            self.onUsageLoaded?(snapshot)
            self.window?.performClose(nil)
        }
    }

    func webView(
        _ webView: WKWebView,
        createWebViewWith configuration: WKWebViewConfiguration,
        for navigationAction: WKNavigationAction,
        windowFeatures: WKWindowFeatures) -> WKWebView?
    {
        if navigationAction.targetFrame == nil {
            webView.load(navigationAction.request)
        }
        return nil
    }

    func windowWillClose(_ notification: Notification) {
        webView.stopLoading()
        let dismiss = onDismiss
        onDismiss = nil
        dismiss?(didLoadUsage)
        onUsageLoaded = nil
    }
}
