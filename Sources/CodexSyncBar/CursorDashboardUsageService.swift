import Foundation
import WebKit

struct CursorMonthlyUsageSnapshot: Equatable, Sendable {
    let billingCycleStart: Date
    let billingCycleEnd: Date
    let membershipType: String
    let cursorModelsUsedPercent: Double
    let otherModelsUsedPercent: Double
    let totalUsedPercent: Double

    var cursorModelsRemainingPercent: Int {
        Self.remainingPercent(afterUsing: cursorModelsUsedPercent)
    }

    var otherModelsRemainingPercent: Int {
        Self.remainingPercent(afterUsing: otherModelsUsedPercent)
    }

    var totalRemainingPercent: Int {
        Self.remainingPercent(afterUsing: totalUsedPercent)
    }

    private static func remainingPercent(afterUsing usedPercent: Double) -> Int {
        Int(max(0, min(100, 100 - usedPercent)).rounded())
    }
}

enum CursorMonthlyUsageState: Equatable, Sendable {
    case unknown
    case loading
    case signedOut
    case loaded(CursorMonthlyUsageSnapshot)
    case failed(String)

    var snapshot: CursorMonthlyUsageSnapshot? {
        guard case let .loaded(snapshot) = self else { return nil }
        return snapshot
    }
}

enum CursorDashboardUsageDecoder {
    private struct Payload: Decodable {
        struct IndividualUsage: Decodable {
            struct Plan: Decodable {
                let enabled: Bool
                let used: Double
                let limit: Double
                let remaining: Double
                let autoPercentUsed: Double
                let apiPercentUsed: Double
                let totalPercentUsed: Double
            }

            let plan: Plan
        }

        let billingCycleStart: String
        let billingCycleEnd: String
        let membershipType: String
        let individualUsage: IndividualUsage
    }

    static func decode(_ data: Data) throws -> CursorMonthlyUsageSnapshot {
        let payload: Payload
        do {
            payload = try JSONDecoder().decode(Payload.self, from: data)
        } catch {
            throw CursorDashboardUsageError.invalidResponse
        }
        let formatter = ISO8601DateFormatter()
        formatter.formatOptions = [.withInternetDateTime, .withFractionalSeconds]
        guard let cycleStart = formatter.date(from: payload.billingCycleStart),
              let cycleEnd = formatter.date(from: payload.billingCycleEnd),
              cycleEnd > cycleStart,
              !payload.membershipType.isEmpty,
              payload.membershipType.utf8.count <= 64
        else {
            throw CursorDashboardUsageError.invalidResponse
        }
        let plan = payload.individualUsage.plan
        let percentages = [
            plan.autoPercentUsed,
            plan.apiPercentUsed,
            plan.totalPercentUsed,
        ]
        guard plan.enabled,
              plan.used.isFinite,
              plan.limit.isFinite,
              plan.remaining.isFinite,
              plan.used >= 0,
              plan.limit > 0,
              plan.remaining >= 0,
              percentages.allSatisfy({ $0.isFinite && $0 >= 0 && $0 <= 10_000 })
        else {
            throw CursorDashboardUsageError.invalidResponse
        }
        return CursorMonthlyUsageSnapshot(
            billingCycleStart: cycleStart,
            billingCycleEnd: cycleEnd,
            membershipType: payload.membershipType,
            cursorModelsUsedPercent: plan.autoPercentUsed,
            otherModelsUsedPercent: plan.apiPercentUsed,
            totalUsedPercent: plan.totalPercentUsed)
    }
}

enum CursorDashboardUsageError: LocalizedError {
    case invalidResponse
    case requestFailed(Int)
    case timedOut

    var errorDescription: String? {
        switch self {
        case .invalidResponse:
            "Cursor 사용량 응답 형식이 올바르지 않습니다."
        case let .requestFailed(status):
            "Cursor 사용량 조회에 실패했습니다. (HTTP \(status))"
        case .timedOut:
            "Cursor 사용량 조회 시간이 초과되었습니다."
        }
    }
}

@MainActor
final class CursorDashboardUsageService {
    let dataStore: WKWebsiteDataStore
    private var probes: [UUID: CursorDashboardUsageProbe] = [:]

    init() {
        dataStore = .default()
    }

    init(dataStore: WKWebsiteDataStore) {
        self.dataStore = dataStore
    }

    func fetchSnapshot() async throws -> CursorMonthlyUsageSnapshot? {
        let id = UUID()
        return try await withCheckedThrowingContinuation { continuation in
            let probe = CursorDashboardUsageProbe(
                dataStore: dataStore,
                completion: { [weak self] result in
                    self?.probes[id] = nil
                    continuation.resume(with: result)
                })
            probes[id] = probe
            probe.start()
        }
    }

    func clearSession() async {
        let cookieStore = dataStore.httpCookieStore
        let cookies: [HTTPCookie] = await withCheckedContinuation { continuation in
            cookieStore.getAllCookies { continuation.resume(returning: $0) }
        }
        for cookie in cookies where Self.isCursorDomain(cookie.domain) {
            await withCheckedContinuation { continuation in
                cookieStore.delete(cookie) { continuation.resume() }
            }
        }

        let dataTypes = WKWebsiteDataStore.allWebsiteDataTypes()
        let records: [WKWebsiteDataRecord] = await withCheckedContinuation { continuation in
            dataStore.fetchDataRecords(ofTypes: dataTypes) {
                continuation.resume(returning: $0)
            }
        }
        let cursorRecords = records.filter { Self.isCursorDomain($0.displayName) }
        guard !cursorRecords.isEmpty else { return }
        await withCheckedContinuation { continuation in
            dataStore.removeData(ofTypes: dataTypes, for: cursorRecords) {
                continuation.resume()
            }
        }
    }

    func evaluateUsage(
        in webView: WKWebView,
        completion: @escaping (Result<CursorMonthlyUsageSnapshot?, Error>) -> Void)
    {
        webView.callAsyncJavaScript(
            """
            const response = await fetch('/api/usage-summary', {
              credentials: 'include',
              headers: { 'Accept': 'application/json' }
            });
            return JSON.stringify({ status: response.status, body: await response.text() });
            """,
            arguments: [:],
            in: nil,
            in: .page)
        { result in
            switch result {
            case let .failure(error):
                completion(.failure(error))
            case let .success(value):
                guard let string = value as? String,
                      let wrapperData = string.data(using: .utf8),
                      let wrapper = try? JSONSerialization.jsonObject(with: wrapperData) as? [String: Any],
                      let status = wrapper["status"] as? Int,
                      let body = wrapper["body"] as? String
                else {
                    completion(.failure(CursorDashboardUsageError.invalidResponse))
                    return
                }
                if status == 401 || status == 403 {
                    completion(.success(nil))
                    return
                }
                guard status == 200, let data = body.data(using: .utf8) else {
                    completion(.failure(CursorDashboardUsageError.requestFailed(status)))
                    return
                }
                do {
                    completion(.success(try CursorDashboardUsageDecoder.decode(data)))
                } catch {
                    completion(.failure(error))
                }
            }
        }
    }

    private static func isCursorDomain(_ value: String) -> Bool {
        let normalized = value.lowercased().trimmingCharacters(in: CharacterSet(charactersIn: "."))
        return normalized == "cursor.com"
            || normalized.hasSuffix(".cursor.com")
            || normalized == "cursor.sh"
            || normalized.hasSuffix(".cursor.sh")
    }
}

@MainActor
private final class CursorDashboardUsageProbe: NSObject, WKNavigationDelegate {
    private let service: CursorDashboardUsageService
    private let webView: WKWebView
    private var completion: ((Result<CursorMonthlyUsageSnapshot?, Error>) -> Void)?
    private var timeoutTask: Task<Void, Never>?
    private var isEvaluating = false

    init(
        dataStore: WKWebsiteDataStore,
        completion: @escaping (Result<CursorMonthlyUsageSnapshot?, Error>) -> Void)
    {
        service = CursorDashboardUsageService(dataStore: dataStore)
        let configuration = WKWebViewConfiguration()
        configuration.websiteDataStore = dataStore
        webView = WKWebView(frame: .zero, configuration: configuration)
        self.completion = completion
        super.init()
        webView.navigationDelegate = self
    }

    func start() {
        guard let url = URL(string: "https://cursor.com/dashboard/spending") else {
            finish(.failure(CursorDashboardUsageError.invalidResponse))
            return
        }
        webView.load(URLRequest(url: url))
        timeoutTask = Task { [weak self] in
            try? await Task.sleep(nanoseconds: 20_000_000_000)
            guard !Task.isCancelled else { return }
            self?.finish(.failure(CursorDashboardUsageError.timedOut))
        }
    }

    func webView(_ webView: WKWebView, didFinish navigation: WKNavigation!) {
        guard !isEvaluating else { return }
        guard let host = webView.url?.host?.lowercased(),
              host == "cursor.com" || host.hasSuffix(".cursor.com"),
              webView.url?.path.hasPrefix("/dashboard/spending") == true
        else {
            finish(.success(nil))
            return
        }
        isEvaluating = true
        service.evaluateUsage(in: webView) { [weak self] result in
            self?.finish(result)
        }
    }

    func webView(
        _ webView: WKWebView,
        didFail navigation: WKNavigation!,
        withError error: Error)
    {
        finish(.failure(error))
    }

    func webView(
        _ webView: WKWebView,
        didFailProvisionalNavigation navigation: WKNavigation!,
        withError error: Error)
    {
        finish(.failure(error))
    }

    private func finish(_ result: Result<CursorMonthlyUsageSnapshot?, Error>) {
        guard let completion else { return }
        self.completion = nil
        timeoutTask?.cancel()
        webView.stopLoading()
        completion(result)
    }
}
