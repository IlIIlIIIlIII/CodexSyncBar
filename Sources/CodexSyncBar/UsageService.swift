import Foundation

actor UsageService {
    private let authStore: AuthStore
    private let switchService: SwitchService
    private let decoder = JSONDecoder()
    private var recoveryBusy = false
    private var recoveryWaiters: [CheckedContinuation<Void, Never>] = []

    init(authStore: AuthStore, switchService: SwitchService) {
        self.authStore = authStore
        self.switchService = switchService
    }

    func fetch(profileID: Int) async throws -> UsageSnapshot {
        var credentials = try await authStore.credentials(for: profileID)

        do {
            return try await fetchSnapshot(credentials: credentials)
        } catch HTTPError.unauthorized {
            do {
                credentials = try await recoverCredentials(
                    profileID: profileID,
                    failedAccessToken: credentials.accessToken)
                return try await fetchSnapshot(credentials: credentials)
            } catch HTTPError.unauthorized {
                throw AppError.loginRequired("인증을 갱신한 후에도 접속할 수 없습니다. 다시 로그인해 주세요.")
            } catch let error as AppError {
                throw error
            } catch {
                throw AppError.network(error.localizedDescription)
            }
        } catch let error as AppError {
            throw error
        } catch {
            throw AppError.network(error.localizedDescription)
        }
    }

    private func recoverCredentials(
        profileID: Int,
        failedAccessToken: String) async throws -> ProfileCredentials
    {
        await acquireRecoverySlot()
        defer { releaseRecoverySlot() }

        var latest = try await authStore.credentials(for: profileID)
        if latest.accessToken != failedAccessToken { return latest }

        _ = try await switchService.forceRefreshAuth(
            profileID: profileID,
            expectedAccessToken: failedAccessToken)
        latest = try await authStore.credentials(for: profileID)
        return latest
    }

    private func acquireRecoverySlot() async {
        if !recoveryBusy {
            recoveryBusy = true
            return
        }
        await withCheckedContinuation { continuation in
            recoveryWaiters.append(continuation)
        }
    }

    private func releaseRecoverySlot() {
        guard !recoveryWaiters.isEmpty else {
            recoveryBusy = false
            return
        }
        recoveryWaiters.removeFirst().resume()
    }

    private func fetchSnapshot(credentials: ProfileCredentials) async throws -> UsageSnapshot {
        let payload: UsagePayload = try await request(
            url: URL(string: "https://chatgpt.com/backend-api/wham/usage")!,
            credentials: credentials,
            extraHeaders: [:])

        let resetCredits: ResetCreditsPayload? = try? await request(
            url: URL(string: "https://chatgpt.com/backend-api/wham/rate-limit-reset-credits")!,
            credentials: credentials,
            extraHeaders: [
                "OpenAI-Beta": "codex-1",
                "originator": "Codex Desktop",
            ],
            timeout: 8)

        let spark = payload.additionalRateLimits?.first(where: { item in
            let text = "\(item.limitName ?? "") \(item.meteredFeature ?? "")".lowercased()
            return text.contains("spark") || text.contains("bengalfox")
        })
        let mainWindows = payload.rateLimit?.normalizedWindows
        let sparkWindows = spark?.rateLimit?.normalizedWindows

        return UsageSnapshot(
            profileID: credentials.profileID,
            email: credentials.email,
            plan: payload.planType?.capitalized ?? "ChatGPT",
            session: mainWindows?.session?.model,
            weekly: mainWindows?.weekly?.model,
            sparkSession: sparkWindows?.session?.model,
            sparkWeekly: sparkWindows?.weekly?.model,
            creditBalance: payload.credits?.balance,
            unlimitedCredits: payload.credits?.unlimited ?? false,
            resetCredits: payload.rateLimitResetCredits?.availableCount ?? resetCredits?.availableCount,
            resetCreditExpirations: resetCredits?.expirationDates
                ?? payload.rateLimitResetCredits?.expirationDates
                ?? [],
            updatedAt: Date())
    }

    private func request<T: Decodable>(
        url: URL,
        credentials: ProfileCredentials,
        extraHeaders: [String: String],
        timeout: TimeInterval = 25) async throws -> T
    {
        var request = URLRequest(url: url, cachePolicy: .reloadIgnoringLocalCacheData, timeoutInterval: timeout)
        request.httpMethod = "GET"
        request.setValue("Bearer \(credentials.accessToken)", forHTTPHeaderField: "Authorization")
        request.setValue(credentials.accountID, forHTTPHeaderField: "ChatGPT-Account-Id")
        request.setValue("application/json", forHTTPHeaderField: "Accept")
        request.setValue("CodexSyncBar/1.0 (macOS)", forHTTPHeaderField: "User-Agent")
        for (key, value) in extraHeaders { request.setValue(value, forHTTPHeaderField: key) }

        let data: Data
        let response: URLResponse
        do {
            (data, response) = try await URLSession.shared.data(for: request)
        } catch {
            throw AppError.network(error.localizedDescription)
        }
        guard let http = response as? HTTPURLResponse else { throw AppError.invalidResponse }
        switch http.statusCode {
        case 200...299:
            do { return try decoder.decode(T.self, from: data) }
            catch { throw AppError.invalidResponse }
        case 401, 403:
            throw HTTPError.unauthorized
        default:
            throw AppError.network("사용량 서버 응답 HTTP \(http.statusCode)")
        }
    }
}

private enum HTTPError: Error {
    case unauthorized
}

private struct UsagePayload: Decodable {
    let planType: String?
    let rateLimit: RateLimitPayload?
    let credits: CreditsPayload?
    let additionalRateLimits: [AdditionalRateLimitPayload]?
    let rateLimitResetCredits: ResetCreditsPayload?

    enum CodingKeys: String, CodingKey {
        case planType = "plan_type"
        case rateLimit = "rate_limit"
        case credits
        case additionalRateLimits = "additional_rate_limits"
        case rateLimitResetCredits = "rate_limit_reset_credits"
    }
}

private struct RateLimitPayload: Decodable {
    let primaryWindow: WindowPayload?
    let secondaryWindow: WindowPayload?

    enum CodingKeys: String, CodingKey {
        case primaryWindow = "primary_window"
        case secondaryWindow = "secondary_window"
    }

    /// The backend has used both primary/secondary semantics and a single
    /// duration-labelled window. Map by duration so a seven-day primary window
    /// is not accidentally presented as the five-hour allowance.
    var normalizedWindows: (session: WindowPayload?, weekly: WindowPayload?) {
        let windows = [primaryWindow, secondaryWindow].compactMap { $0 }
        guard !windows.isEmpty else { return (nil, nil) }
        if windows.count == 1, let only = windows.first {
            if let seconds = only.limitWindowSeconds, seconds >= 86_400 {
                return (nil, only)
            }
            return (only, nil)
        }

        if windows.allSatisfy({ $0.limitWindowSeconds != nil }) {
            let sorted = windows.sorted { ($0.limitWindowSeconds ?? 0) < ($1.limitWindowSeconds ?? 0) }
            return (sorted.first, sorted.last)
        }
        return (primaryWindow, secondaryWindow)
    }
}

private struct AdditionalRateLimitPayload: Decodable {
    let limitName: String?
    let meteredFeature: String?
    let rateLimit: RateLimitPayload?

    enum CodingKeys: String, CodingKey {
        case limitName = "limit_name"
        case meteredFeature = "metered_feature"
        case rateLimit = "rate_limit"
    }
}

private struct WindowPayload: Decodable {
    let usedPercent: Double
    let resetAt: Int?
    let limitWindowSeconds: Int?

    enum CodingKeys: String, CodingKey {
        case usedPercent = "used_percent"
        case resetAt = "reset_at"
        case limitWindowSeconds = "limit_window_seconds"
    }

    init(from decoder: Decoder) throws {
        let container = try decoder.container(keyedBy: CodingKeys.self)
        if let value = try? container.decode(Double.self, forKey: .usedPercent) {
            usedPercent = value
        } else if let value = try? container.decode(Int.self, forKey: .usedPercent) {
            usedPercent = Double(value)
        } else {
            throw DecodingError.dataCorruptedError(
                forKey: .usedPercent,
                in: container,
                debugDescription: "used_percent is missing")
        }
        resetAt = (try? container.decodeIfPresent(Int.self, forKey: .resetAt)) ?? nil
        limitWindowSeconds = (try? container.decodeIfPresent(Int.self, forKey: .limitWindowSeconds)) ?? nil
    }

    var model: UsageWindow {
        UsageWindow(
            usedPercent: usedPercent,
            resetsAt: resetAt.map { Date(timeIntervalSince1970: TimeInterval($0)) },
            durationSeconds: limitWindowSeconds)
    }
}

private struct CreditsPayload: Decodable {
    let unlimited: Bool?
    let balance: Double?

    enum CodingKeys: String, CodingKey { case unlimited, balance }

    init(from decoder: Decoder) throws {
        let container = try decoder.container(keyedBy: CodingKeys.self)
        unlimited = try? container.decodeIfPresent(Bool.self, forKey: .unlimited)
        if let value = try? container.decodeIfPresent(Double.self, forKey: .balance) {
            balance = value
        } else if let string = try? container.decodeIfPresent(String.self, forKey: .balance) {
            balance = Double(string)
        } else {
            balance = nil
        }
    }
}

struct ResetCreditsPayload: Decodable {
    let availableCount: Int
    let credits: [ResetCreditPayload]?

    enum CodingKeys: String, CodingKey {
        case availableCount = "available_count"
        case credits
    }

    var expirationDates: [Date] {
        (credits ?? []).compactMap(\.expirationDate).sorted()
    }
}

struct ResetCreditPayload: Decodable {
    let expiresAt: String?

    enum CodingKeys: String, CodingKey {
        case expiresAt = "expires_at"
    }

    var expirationDate: Date? {
        guard let expiresAt else { return nil }

        let fractional = ISO8601DateFormatter()
        fractional.formatOptions = [.withInternetDateTime, .withFractionalSeconds]
        if let date = fractional.date(from: expiresAt) { return date }

        let standard = ISO8601DateFormatter()
        standard.formatOptions = [.withInternetDateTime]
        return standard.date(from: expiresAt)
    }
}
