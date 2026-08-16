import Foundation

enum CursorModelGroup: String, CaseIterable, Codable, Hashable, Identifiable, Sendable {
    case automatic
    case cursor
    case openAIGPT = "openai-gpt"
    case openAICodex = "openai-codex"
    case anthropicClaude = "anthropic-claude"
    case googleGemini = "google-gemini"
    case kimi
    case glm
    case other

    var id: String { rawValue }

    var displayName: String {
        switch self {
        case .automatic: "자동 선택 (Cursor 구독)"
        case .cursor: "Cursor 모델"
        case .openAIGPT: "OpenAI GPT (Cursor 구독)"
        case .openAICodex: "OpenAI Codex (Cursor 구독)"
        case .anthropicClaude: "Anthropic Claude (Cursor 구독)"
        case .googleGemini: "Google Gemini (Cursor 구독)"
        case .kimi: "Kimi (Cursor 구독)"
        case .glm: "GLM (Cursor 구독)"
        case .other: "기타 모델 (Cursor 구독)"
        }
    }
}

enum CursorModelEffort: String, CaseIterable, Codable, Hashable, Identifiable, Sendable {
    case none
    case minimal
    case low
    case medium
    case `default`
    case high
    case xhigh
    case max

    var id: String { rawValue }

    var displayName: String {
        switch self {
        case .none: "None"
        case .minimal: "Minimal"
        case .low: "Low"
        case .medium: "Medium"
        case .default: "Default"
        case .high: "High"
        case .xhigh: "Extra High"
        case .max: "Max"
        }
    }
}

struct CursorModelSelection: Codable, Hashable, Sendable {
    let baseSlug: String
    let effort: CursorModelEffort
    let fast: Bool
    let thinking: Bool

    init(
        baseSlug: String,
        effort: CursorModelEffort = .default,
        fast: Bool = false,
        thinking: Bool = false)
    {
        self.baseSlug = baseSlug
        self.effort = effort
        self.fast = fast
        self.thinking = thinking
    }
}

struct CursorModelVariant: Codable, Hashable, Identifiable, Sendable {
    let slug: String
    let displayName: String
    let baseSlug: String
    let context: String?
    let effort: CursorModelEffort
    let fast: Bool
    let thinking: Bool

    init(
        slug: String,
        displayName: String,
        baseSlug: String,
        context: String? = nil,
        effort: CursorModelEffort,
        fast: Bool,
        thinking: Bool)
    {
        self.slug = slug
        self.displayName = displayName
        self.baseSlug = baseSlug
        self.context = context
        self.effort = effort
        self.fast = fast
        self.thinking = thinking
    }

    var id: String { slug }

    var selection: CursorModelSelection {
        CursorModelSelection(
            baseSlug: baseSlug,
            effort: effort,
            fast: fast,
            thinking: thinking)
    }
}

struct CursorACPModelParameters: Codable, Equatable, Sendable {
    let model: String
    let context: String?
    let effort: CursorModelEffort?
    let fast: Bool
    let thinking: Bool
}

struct CursorCodexModelRouteVariant: Codable, Equatable, Sendable {
    let slug: String
    let effort: CursorModelEffort?
    let fast: Bool

    private enum CodingKeys: String, CodingKey {
        case slug
        case effort
        case fast
    }

    func encode(to encoder: Encoder) throws {
        var container = encoder.container(keyedBy: CodingKeys.self)
        try container.encode(slug, forKey: .slug)
        if let effort {
            try container.encode(effort, forKey: .effort)
        } else {
            try container.encodeNil(forKey: .effort)
        }
        try container.encode(fast, forKey: .fast)
    }
}

struct CursorCodexModelRoute: Codable, Equatable, Sendable {
    let baseSlug: String
    let thinking: Bool
    let defaultEffort: CursorModelEffort
    let variants: [CursorCodexModelRouteVariant]

    var supportedEfforts: [CursorModelEffort] {
        let normal = Set(variants.lazy
            .filter { !$0.fast }
            .compactMap { $0.effort ?? defaultEffort })
        let fast = Set(variants.lazy
            .filter(\.fast)
            .compactMap { $0.effort ?? defaultEffort })
        let paired = normal.intersection(fast)
        let available = paired.isEmpty ? normal : paired
        return CursorModelEffort.allCases
            .filter { $0 != .default && available.contains($0) }
    }

    var supportsFast: Bool {
        !supportedFastEfforts.isEmpty
    }

    func resolve(effort: CursorModelEffort? = nil, fast: Bool = false) -> String? {
        let requestedEffort = effort ?? defaultEffort
        return variants.first(where: { variant in
            (variant.effort ?? defaultEffort) == requestedEffort
                && variant.fast == fast
        })?.slug
    }

    private var supportedFastEfforts: Set<CursorModelEffort> {
        let normal = Set(variants.lazy
            .filter { !$0.fast }
            .compactMap { $0.effort ?? defaultEffort })
        let fast = Set(variants.lazy
            .filter(\.fast)
            .compactMap { $0.effort ?? defaultEffort })
        return normal.intersection(fast)
    }
}

struct CursorCodexPickerPreset: Equatable, Identifiable, Sendable {
    let id: String
    let baseSlug: String
    let thinking: Bool
    let defaultEffort: CursorModelEffort
    let variants: [CursorModelVariant]

    var route: CursorCodexModelRoute {
        CursorCodexModelRoute(
            baseSlug: baseSlug,
            thinking: thinking,
            defaultEffort: defaultEffort,
            variants: variants.map { variant in
                CursorCodexModelRouteVariant(
                    slug: variant.slug,
                    effort: variant.effort == .default ? nil : variant.effort,
                    fast: variant.fast)
            })
    }
}

struct CursorModelFamily: Codable, Equatable, Identifiable, Sendable {
    let baseSlug: String
    let displayName: String
    let group: CursorModelGroup
    let variants: [CursorModelVariant]

    var id: String { baseSlug }

    var availableEfforts: [CursorModelEffort] {
        let available = Set(variants.map(\.effort))
        return CursorModelEffort.allCases.filter(available.contains)
    }

    var supportsFast: Bool {
        variants.contains(where: \.fast)
    }

    var supportsThinking: Bool {
        variants.contains(where: \.thinking)
    }

    var preferredVariant: CursorModelVariant? {
        let preferredEfforts: [CursorModelEffort] = [
            .default, .medium, .high, .low, .none, .minimal, .xhigh, .max,
        ]
        for effort in preferredEfforts {
            if let variant = variants.first(where: {
                $0.effort == effort && !$0.fast && !$0.thinking
            }) {
                return variant
            }
        }
        return variants.first(where: { !$0.fast && !$0.thinking }) ?? variants.first
    }

    func availableEfforts(fast: Bool, thinking: Bool) -> [CursorModelEffort] {
        let available = Set(variants.lazy
            .filter { $0.fast == fast && $0.thinking == thinking }
            .map(\.effort))
        return CursorModelEffort.allCases.filter(available.contains)
    }

    func resolve(
        effort: CursorModelEffort = .default,
        fast: Bool = false,
        thinking: Bool = false) -> String?
    {
        variants.first {
            $0.effort == effort && $0.fast == fast && $0.thinking == thinking
        }?.slug
    }

    func resolve(_ selection: CursorModelSelection) -> String? {
        guard selection.baseSlug == baseSlug else { return nil }
        return resolve(
            effort: selection.effort,
            fast: selection.fast,
            thinking: selection.thinking)
    }
}

struct CursorModelSection: Equatable, Identifiable, Sendable {
    let group: CursorModelGroup
    let families: [CursorModelFamily]

    var id: CursorModelGroup { group }
}

struct CursorModelCatalog: Equatable, Sendable {
    let variants: [CursorModelVariant]
    let families: [CursorModelFamily]

    init(cliOutput: String) {
        var parsedVariants: [CursorModelVariant] = []
        var seenSlugs = Set<String>()

        for rawLine in cliOutput.split(whereSeparator: \.isNewline) {
            guard let variant = Self.parseLine(String(rawLine)),
                  seenSlugs.insert(variant.slug).inserted
            else {
                continue
            }
            parsedVariants.append(variant)
        }

        variants = parsedVariants

        var baseSlugs: [String] = []
        var variantsByBase: [String: [CursorModelVariant]] = [:]
        for variant in parsedVariants {
            if variantsByBase[variant.baseSlug] == nil {
                baseSlugs.append(variant.baseSlug)
            }
            variantsByBase[variant.baseSlug, default: []].append(variant)
        }

        families = baseSlugs.compactMap { baseSlug in
            guard let familyVariants = variantsByBase[baseSlug],
                  let firstVariant = familyVariants.first
            else {
                return nil
            }
            return CursorModelFamily(
                baseSlug: baseSlug,
                displayName: Self.baseDisplayName(for: firstVariant),
                group: Self.group(for: baseSlug),
                variants: familyVariants)
        }
    }

    var sections: [CursorModelSection] {
        CursorModelGroup.allCases.compactMap { group in
            let matchingFamilies = families.filter { $0.group == group }
            guard !matchingFamilies.isEmpty else { return nil }
            return CursorModelSection(group: group, families: matchingFamilies)
        }
    }

    func family(baseSlug: String) -> CursorModelFamily? {
        families.first { $0.baseSlug == baseSlug }
    }

    func family(containingSlug slug: String) -> CursorModelFamily? {
        guard let baseSlug = variants.first(where: { $0.slug == slug })?.baseSlug else {
            return nil
        }
        return family(baseSlug: baseSlug)
    }

    func selection(forSlug slug: String) -> CursorModelSelection? {
        variants.first { $0.slug == slug }?.selection
    }

    var pickerPresets: [CursorCodexPickerPreset] {
        var presets: [CursorCodexPickerPreset] = []
        for family in families {
            for thinking in [false, true] {
                let matchingVariants = family.variants.filter { $0.thinking == thinking }
                guard !matchingVariants.isEmpty,
                      let defaultVariant = Self.preferredCodexVariant(
                          in: matchingVariants,
                          requiringFastPair: true)
                        ?? Self.preferredCodexVariant(in: matchingVariants)
                else {
                    continue
                }
                let defaultEffort = Self.codexEffort(for: defaultVariant.effort)
                presets.append(CursorCodexPickerPreset(
                    id: Self.codexModelID(
                        baseSlug: family.baseSlug,
                        thinking: thinking),
                    baseSlug: family.baseSlug,
                    thinking: thinking,
                    defaultEffort: defaultEffort,
                    variants: matchingVariants))
            }
        }
        return presets
    }

    var codexModelRoutes: [String: CursorCodexModelRoute] {
        Dictionary(uniqueKeysWithValues: pickerPresets.map { preset in
            (preset.id, preset.route)
        })
    }

    func preferredPickerModelID(forFlatSlug slug: String) -> String? {
        guard let variant = variants.first(where: { $0.slug == slug }) else {
            return nil
        }
        return Self.codexModelID(
            baseSlug: variant.baseSlug,
            thinking: variant.thinking)
    }

    func cursorRouteJSON() throws -> String {
        var object: [String: Any] = [:]
        for preset in pickerPresets {
            guard Self.isValidCodexModelID(preset.id) else {
                throw AppError.processFailed(
                    "Codex에 표시할 Cursor 모델 ID가 너무 길거나 올바르지 않습니다: \(preset.id)")
            }
            let usesDefaultSentinel = preset.variants.allSatisfy {
                $0.effort == .default
            }
            let wireDefaultEffort: CursorModelEffort = usesDefaultSentinel
                ? .default
                : preset.defaultEffort
            var variantsByEffort: [String: [String: String]] = [:]
            for variant in preset.route.variants {
                let effort = variant.effort ?? wireDefaultEffort
                let tier = variant.fast ? "fast" : "standard"
                if variantsByEffort[effort.rawValue]?[tier] != nil {
                    throw AppError.processFailed(
                        "Cursor 모델 경로가 중복됩니다: \(preset.id) \(effort.rawValue) \(tier)")
                }
                variantsByEffort[effort.rawValue, default: [:]][tier] = variant.slug
            }
            object[preset.id] = [
                "default_effort": wireDefaultEffort.rawValue,
                "variants": variantsByEffort,
            ]
        }
        let data = try JSONSerialization.data(
            withJSONObject: object,
            options: [.sortedKeys, .withoutEscapingSlashes])
        return String(decoding: data, as: UTF8.self)
    }

    func codexModelRoutesJSON() throws -> String {
        let encoder = JSONEncoder()
        encoder.outputFormatting = [.sortedKeys]
        return String(
            decoding: try encoder.encode(codexModelRoutes),
            as: UTF8.self)
    }

    static func codexModelID(baseSlug: String, thinking: Bool) -> String {
        let suffix = thinking ? "/thinking" : ""
        return "syncbar-cursor/\(baseSlug)\(suffix)"
    }

    static func isValidCodexModelID(_ modelID: String) -> Bool {
        modelID.utf8.count <= 128
            && modelID.range(
                of: #"^[A-Za-z0-9][A-Za-z0-9._:/-]{0,127}$"#,
                options: .regularExpression) != nil
    }

    var acpModelParametersBySlug: [String: CursorACPModelParameters] {
        Dictionary(uniqueKeysWithValues: variants.map { variant in
            (
                variant.slug,
                CursorACPModelParameters(
                    model: Self.acpModelID(forBaseSlug: variant.baseSlug),
                    context: variant.context,
                    effort: variant.effort == .default ? nil : variant.effort,
                    fast: variant.fast,
                    thinking: variant.thinking))
        })
    }

    static func acpModelID(forBaseSlug baseSlug: String) -> String {
        switch baseSlug {
        case "auto": "default"
        case "cursor-grok-4.6": "grok-4.6"
        case "cursor-grok-4.5": "grok-4.5"
        case "claude-4.6-sonnet": "claude-sonnet-4-6"
        case "claude-4.6-opus": "claude-opus-4-6"
        case "claude-4.5-opus": "claude-opus-4-5"
        case "claude-4.5-sonnet": "claude-sonnet-4-5"
        case "claude-4-sonnet": "claude-sonnet-4"
        default: baseSlug
        }
    }

    private static func preferredCodexVariant(
        in variants: [CursorModelVariant],
        requiringFastPair: Bool = false) -> CursorModelVariant?
    {
        let fastEfforts = Set(variants.lazy
            .filter(\.fast)
            .map { codexEffort(for: $0.effort) })
        let preferredEfforts: [CursorModelEffort] = [
            .default, .medium, .high, .low, .none, .minimal, .xhigh, .max,
        ]
        for effort in preferredEfforts {
            if let variant = variants.first(where: {
                $0.effort == effort && !$0.fast
                    && (!requiringFastPair
                        || fastEfforts.contains(codexEffort(for: $0.effort)))
            }) {
                return variant
            }
        }
        if requiringFastPair { return nil }
        return variants.first(where: { !$0.fast }) ?? variants.first
    }

    private static func codexEffort(for effort: CursorModelEffort) -> CursorModelEffort {
        effort == .default ? .medium : effort
    }

    func acpModelParametersJSON() throws -> String {
        let encoder = JSONEncoder()
        encoder.outputFormatting = [.sortedKeys]
        return String(
            decoding: try encoder.encode(acpModelParametersBySlug),
            as: UTF8.self)
    }

    func resolve(_ selection: CursorModelSelection) -> String? {
        family(baseSlug: selection.baseSlug)?.resolve(selection)
    }

    func resolve(
        baseSlug: String,
        effort: CursorModelEffort = .default,
        fast: Bool = false,
        thinking: Bool = false) -> String?
    {
        resolve(CursorModelSelection(
            baseSlug: baseSlug,
            effort: effort,
            fast: fast,
            thinking: thinking))
    }

    private static func parseLine(_ rawLine: String) -> CursorModelVariant? {
        let line = rawLine.trimmingCharacters(in: .whitespacesAndNewlines)
        guard let separator = line.range(of: " - ") else { return nil }

        let slug = String(line[..<separator.lowerBound])
            .trimmingCharacters(in: .whitespacesAndNewlines)
        let displayName = String(line[separator.upperBound...])
            .trimmingCharacters(in: .whitespacesAndNewlines)
        guard !displayName.isEmpty,
              slug.range(
                  of: #"^[A-Za-z0-9][A-Za-z0-9._:/-]{0,127}$"#,
                  options: .regularExpression) != nil
        else {
            return nil
        }

        let components = decompose(slug: slug)
        return CursorModelVariant(
            slug: slug,
            displayName: displayName,
            baseSlug: components.baseSlug,
            context: context(displayName: displayName),
            effort: components.effort,
            fast: components.fast,
            thinking: components.thinking)
    }

    private static func context(displayName: String) -> String? {
        let hasOneMillionContext = displayName
            .split(whereSeparator: \.isWhitespace)
            .contains { $0.lowercased() == "1m" }
        return hasOneMillionContext ? "1m" : nil
    }

    private static func decompose(slug: String) -> (
        baseSlug: String,
        effort: CursorModelEffort,
        fast: Bool,
        thinking: Bool)
    {
        var baseSlug = slug
        var effort: CursorModelEffort?
        var fast = false
        var thinking = false

        func stripSuffix(_ suffix: String) -> Bool {
            guard baseSlug.lowercased().hasSuffix(suffix) else { return false }
            baseSlug.removeLast(suffix.count)
            return true
        }

        let effortSuffixes: [(String, CursorModelEffort)] = [
            ("-extra-high", .xhigh),
            ("-minimal", .minimal),
            ("-default", .default),
            ("-medium", .medium),
            ("-xhigh", .xhigh),
            ("-none", .none),
            ("-high", .high),
            ("-low", .low),
            ("-max", .max),
        ]

        var changed = true
        while changed {
            changed = false

            if !fast, stripSuffix("-fast") {
                fast = true
                changed = true
            }
            if !thinking, stripSuffix("-thinking") {
                thinking = true
                changed = true
            }
            if effort == nil {
                for (suffix, candidate) in effortSuffixes where stripSuffix(suffix) {
                    effort = candidate
                    changed = true
                    break
                }
            }
        }

        return (baseSlug, effort ?? .default, fast, thinking)
    }

    private static func group(for baseSlug: String) -> CursorModelGroup {
        let normalized = baseSlug.lowercased()
        if normalized == "auto" {
            return .automatic
        }
        if normalized.hasPrefix("gpt-") {
            let components = normalized.split(separator: "-")
            return components.contains("codex") ? .openAICodex : .openAIGPT
        }
        if normalized.hasPrefix("cursor-") || normalized.hasPrefix("composer-") {
            return .cursor
        }
        if normalized.hasPrefix("claude-") {
            return .anthropicClaude
        }
        if normalized.hasPrefix("gemini-") {
            return .googleGemini
        }
        if normalized.hasPrefix("kimi-") {
            return .kimi
        }
        if normalized.hasPrefix("glm-") {
            return .glm
        }
        return .other
    }

    private static func baseDisplayName(for variant: CursorModelVariant) -> String {
        var displayName = variant.displayName
            .replacingOccurrences(of: " (NO ZDR)", with: "")
            .replacingOccurrences(of: "(NO ZDR)", with: "")

        var excludedWords = Set(["1m", "(default)"])
        if variant.fast {
            excludedWords.insert("fast")
        }
        if variant.thinking {
            excludedWords.insert("thinking")
        }
        switch variant.effort {
        case .none:
            excludedWords.insert("none")
        case .minimal:
            excludedWords.insert("minimal")
        case .low:
            excludedWords.insert("low")
        case .medium:
            excludedWords.insert("medium")
        case .default:
            break
        case .high:
            excludedWords.insert("high")
        case .xhigh:
            excludedWords.formUnion(["extra", "high", "xhigh"])
        case .max:
            excludedWords.insert("max")
        }

        let words = displayName.split(whereSeparator: \.isWhitespace)
            .map(String.init)
            .filter { !excludedWords.contains($0.lowercased()) }
        displayName = words.joined(separator: " ")
        return displayName.isEmpty ? variant.baseSlug : displayName
    }
}
