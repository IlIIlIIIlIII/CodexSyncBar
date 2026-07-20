import Foundation

struct TokenCounts: Codable, Equatable, Sendable {
    var inputTokens: Int64 = 0
    var cachedInputTokens: Int64 = 0
    var cacheWriteInputTokens: Int64 = 0
    var outputTokens: Int64 = 0
    var reasoningOutputTokens: Int64 = 0
    var totalTokens: Int64 = 0

    static func + (lhs: Self, rhs: Self) -> Self {
        Self(
            inputTokens: lhs.inputTokens + rhs.inputTokens,
            cachedInputTokens: lhs.cachedInputTokens + rhs.cachedInputTokens,
            cacheWriteInputTokens: lhs.cacheWriteInputTokens + rhs.cacheWriteInputTokens,
            outputTokens: lhs.outputTokens + rhs.outputTokens,
            reasoningOutputTokens: lhs.reasoningOutputTokens + rhs.reasoningOutputTokens,
            totalTokens: lhs.totalTokens + rhs.totalTokens)
    }
}

struct ModelTokenUsage: Codable, Equatable, Sendable, Identifiable {
    let model: String
    let serviceTier: String
    let inputTokens: Int64
    let cachedInputTokens: Int64
    let cacheWriteInputTokens: Int64
    let outputTokens: Int64
    let reasoningOutputTokens: Int64
    let totalTokens: Int64
    let requests: Int64

    var id: String { "\(model)\u{1f}\(serviceTier)" }
    var counts: TokenCounts {
        TokenCounts(
            inputTokens: inputTokens,
            cachedInputTokens: cachedInputTokens,
            cacheWriteInputTokens: cacheWriteInputTokens,
            outputTokens: outputTokens,
            reasoningOutputTokens: reasoningOutputTokens,
            totalTokens: totalTokens)
    }
}

struct DeviceTokenUsageSummary: Codable, Equatable, Sendable {
    let schemaVersion: Int
    let generatedAt: String
    let scannedFiles: Int
    let requests: Int64
    let inputTokens: Int64
    let cachedInputTokens: Int64
    let cacheWriteInputTokens: Int64
    let outputTokens: Int64
    let reasoningOutputTokens: Int64
    let totalTokens: Int64
    let buckets: [ModelTokenUsage]
    let errors: [String]

    var counts: TokenCounts {
        TokenCounts(
            inputTokens: inputTokens,
            cachedInputTokens: cachedInputTokens,
            cacheWriteInputTokens: cacheWriteInputTokens,
            outputTokens: outputTokens,
            reasoningOutputTokens: reasoningOutputTokens,
            totalTokens: totalTokens)
    }
}

struct DeviceTokenUsage: Codable, Equatable, Sendable, Identifiable {
    let id: String
    let displayName: String
    let isReachable: Bool
    let summary: DeviceTokenUsageSummary?
    let error: String?

    var estimatedCostUSD: Decimal {
        summary?.buckets.reduce(Decimal.zero) { $0 + TokenUsagePricing.estimateUSD(for: $1).pricedUSD } ?? 0
    }

    var unpricedTokens: Int64 {
        summary?.buckets.reduce(0) { result, bucket in
            result + (TokenUsagePricing.estimateUSD(for: bucket).isPriced ? 0 : bucket.totalTokens)
        } ?? 0
    }
}

struct TokenUsageSnapshot: Equatable, Sendable {
    let devices: [DeviceTokenUsage]
    let collectedAt: Date

    var reachableDeviceCount: Int { devices.filter { $0.isReachable && $0.summary != nil }.count }
    var totalDeviceCount: Int { devices.count }
    var counts: TokenCounts {
        devices.compactMap(\.summary?.counts).reduce(TokenCounts(), +)
    }
    var estimatedCostUSD: Decimal {
        devices.reduce(Decimal.zero) { $0 + $1.estimatedCostUSD }
    }
    var unpricedTokens: Int64 { devices.reduce(0) { $0 + $1.unpricedTokens } }
    var priorityPricedTokens: Int64 {
        devices.compactMap(\.summary).flatMap(\.buckets)
            .filter { TokenUsagePricing.isPriorityPricedServiceTier($0.serviceTier) }
            .reduce(0) { $0 + $1.totalTokens }
    }
}

struct TokenCostEstimate: Equatable, Sendable {
    let pricedUSD: Decimal
    let isPriced: Bool
    let canonicalModel: String?
    let multiplier: Decimal
}

enum TokenUsagePricing {
    private struct Rate {
        let canonicalModel: String
        let input: Decimal
        let cachedInput: Decimal
        let output: Decimal
        let priorityMultiplier: Decimal?
    }

    static func isPriorityPricedServiceTier(_ tier: String) -> Bool {
        ["priority", "fast"].contains(tier.lowercased())
    }

    static func estimateUSD(for usage: ModelTokenUsage) -> TokenCostEstimate {
        guard let rate = rate(for: usage.model) else {
            return TokenCostEstimate(pricedUSD: 0, isPriced: false, canonicalModel: nil, multiplier: 1)
        }
        let multiplier = isPriorityPricedServiceTier(usage.serviceTier)
            ? (rate.priorityMultiplier ?? 1)
            : 1
        let cached = max(0, min(usage.cachedInputTokens, usage.inputTokens))
        let uncached = max(0, usage.inputTokens - cached)
        let million = Decimal(1_000_000)
        let base = (Decimal(uncached) * rate.input
            + Decimal(cached) * rate.cachedInput
            + Decimal(max(0, usage.outputTokens)) * rate.output) / million
        return TokenCostEstimate(
            pricedUSD: base * multiplier,
            isPriced: true,
            canonicalModel: rate.canonicalModel,
            multiplier: multiplier)
    }

    private static func rate(for rawModel: String) -> Rate? {
        let model = rawModel.lowercased()
        if model == "codex-auto-review" || model.contains("gpt-5.3-codex") && !model.contains("spark") {
            return Rate(canonicalModel: "GPT-5.3-Codex", input: 1.75, cachedInput: 0.175, output: 14, priorityMultiplier: 2)
        }
        if model.contains("gpt-5.3-codex-spark") || model.contains("spark") { return nil }
        if model.contains("gpt-5.6-terra") {
            return Rate(canonicalModel: "GPT-5.6 Terra", input: 2.5, cachedInput: 0.25, output: 15, priorityMultiplier: 2)
        }
        if model.contains("gpt-5.6-luna") {
            return Rate(canonicalModel: "GPT-5.6 Luna", input: 1, cachedInput: 0.1, output: 6, priorityMultiplier: 2)
        }
        if model.contains("gpt-5.6") {
            return Rate(canonicalModel: "GPT-5.6 Sol", input: 5, cachedInput: 0.5, output: 30, priorityMultiplier: 2)
        }
        if model.contains("cyber") { return nil }
        if model.contains("gpt-5.5") {
            return Rate(canonicalModel: "GPT-5.5", input: 5, cachedInput: 0.5, output: 30, priorityMultiplier: 2.5)
        }
        if model.contains("gpt-5.4-mini") {
            return Rate(canonicalModel: "GPT-5.4 Mini", input: 0.75, cachedInput: 0.075, output: 4.5, priorityMultiplier: 2)
        }
        if model.contains("gpt-5.4") {
            return Rate(canonicalModel: "GPT-5.4", input: 2.5, cachedInput: 0.25, output: 15, priorityMultiplier: 2)
        }
        if model.contains("gpt-5.2") {
            return Rate(canonicalModel: "GPT-5.2", input: 1.75, cachedInput: 0.175, output: 14, priorityMultiplier: 2)
        }
        return nil
    }
}

enum TokenUsageFormatting {
    static func tokens(_ count: Int64) -> String {
        let value = Double(count)
        if count >= 1_000_000_000 { return String(format: "%.2fB", value / 1_000_000_000) }
        if count >= 1_000_000 { return String(format: "%.2fM", value / 1_000_000) }
        if count >= 1_000 { return String(format: "%.1fK", value / 1_000) }
        return "\(count)"
    }

    static func dollars(_ value: Decimal) -> String {
        let number = NSDecimalNumber(decimal: value)
        let formatter = NumberFormatter()
        formatter.locale = Locale(identifier: "en_US_POSIX")
        formatter.numberStyle = .decimal
        formatter.usesGroupingSeparator = true
        formatter.groupingSeparator = ","
        if number.doubleValue >= 100 {
            formatter.minimumFractionDigits = 0
            formatter.maximumFractionDigits = 0
        } else if number.doubleValue >= 10 {
            formatter.minimumFractionDigits = 1
            formatter.maximumFractionDigits = 1
        } else {
            formatter.minimumFractionDigits = 2
            formatter.maximumFractionDigits = 2
        }
        return "$" + (formatter.string(from: number) ?? "0.00")
    }
}
