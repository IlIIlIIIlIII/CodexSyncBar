import Foundation

enum UsageDisplayItem: String, CaseIterable, Identifiable, Sendable {
    case fiveHour
    case codexWeekly
    case sparkFiveHour
    case sparkWeekly

    var id: String { rawValue }

    var title: String {
        switch self {
        case .fiveHour: "5시간"
        case .codexWeekly: "주간"
        case .sparkFiveHour: "Spark 5시간"
        case .sparkWeekly: "Spark 주간"
        }
    }

    var icon: String {
        switch self {
        case .fiveHour: "clock.fill"
        case .codexWeekly: "calendar"
        case .sparkFiveHour, .sparkWeekly: "bolt.fill"
        }
    }

    var isSpark: Bool {
        switch self {
        case .fiveHour, .codexWeekly: false
        case .sparkFiveHour, .sparkWeekly: true
        }
    }

    func window(in snapshot: UsageSnapshot) -> UsageWindow? {
        switch self {
        case .fiveHour: snapshot.session
        case .codexWeekly: snapshot.weekly
        case .sparkFiveHour: snapshot.sparkSession
        case .sparkWeekly: snapshot.sparkWeekly
        }
    }

    fileprivate var defaultsKey: String {
        "usageDisplay.\(rawValue)"
    }
}

struct UsageDisplayPreferences: Equatable, Sendable {
    var fiveHour: Bool
    var codexWeekly: Bool
    var sparkFiveHour: Bool
    var sparkWeekly: Bool

    static let allVisible = UsageDisplayPreferences(
        fiveHour: true,
        codexWeekly: true,
        sparkFiveHour: true,
        sparkWeekly: true)

    func isVisible(_ item: UsageDisplayItem) -> Bool {
        switch item {
        case .fiveHour: fiveHour
        case .codexWeekly: codexWeekly
        case .sparkFiveHour: sparkFiveHour
        case .sparkWeekly: sparkWeekly
        }
    }

    mutating func setVisible(_ visible: Bool, for item: UsageDisplayItem) {
        switch item {
        case .fiveHour: fiveHour = visible
        case .codexWeekly: codexWeekly = visible
        case .sparkFiveHour: sparkFiveHour = visible
        case .sparkWeekly: sparkWeekly = visible
        }
    }
}

struct MenuBarUsagePreferences: Equatable, Sendable {
    static let maximumItemCount = 2
    static let defaultItems: [UsageDisplayItem] = [.codexWeekly, .sparkWeekly]

    let items: [UsageDisplayItem]

    init(items requestedItems: [UsageDisplayItem]) {
        var normalized: [UsageDisplayItem] = []
        for item in requestedItems where !normalized.contains(item) {
            normalized.append(item)
            if normalized.count == Self.maximumItemCount { break }
        }
        items = normalized
    }

    static let `default` = MenuBarUsagePreferences(items: defaultItems)

    func item(at index: Int, fallback: UsageDisplayItem) -> UsageDisplayItem {
        items.indices.contains(index) ? items[index] : fallback
    }

    func replacingItem(at index: Int, with item: UsageDisplayItem) -> MenuBarUsagePreferences {
        guard items.indices.contains(index) else { return self }
        var updated = items
        if let otherIndex = updated.firstIndex(of: item), otherIndex != index {
            updated.swapAt(index, otherIndex)
        } else {
            updated[index] = item
        }
        return MenuBarUsagePreferences(items: updated)
    }

    func settingItemCount(_ requestedCount: Int) -> MenuBarUsagePreferences {
        let itemCount = min(max(requestedCount, 0), Self.maximumItemCount)
        var updated = Array(items.prefix(itemCount))

        if updated.count < itemCount {
            for candidate in Self.defaultItems + UsageDisplayItem.allCases
                where !updated.contains(candidate)
            {
                updated.append(candidate)
                if updated.count == itemCount { break }
            }
        }

        return MenuBarUsagePreferences(items: updated)
    }
}

struct MenuBarUsagePreferencesStore {
    private let defaults: UserDefaults
    private let defaultsKey = "menuBarUsage.items.v1"

    init(defaults: UserDefaults = .standard) {
        self.defaults = defaults
    }

    func load() -> MenuBarUsagePreferences {
        guard defaults.object(forKey: defaultsKey) != nil else { return .default }
        let rawItems = defaults.stringArray(forKey: defaultsKey) ?? []
        return MenuBarUsagePreferences(items: rawItems.compactMap(UsageDisplayItem.init(rawValue:)))
    }

    func save(_ preferences: MenuBarUsagePreferences) {
        defaults.set(preferences.items.map(\.rawValue), forKey: defaultsKey)
    }
}

struct UsageDisplayPreferencesStore {
    private let defaults: UserDefaults

    init(defaults: UserDefaults = .standard) {
        self.defaults = defaults
    }

    func load() -> UsageDisplayPreferences {
        var preferences = UsageDisplayPreferences.allVisible
        for item in UsageDisplayItem.allCases {
            let visible = defaults.object(forKey: item.defaultsKey) as? Bool ?? true
            preferences.setVisible(visible, for: item)
        }
        return preferences
    }

    func save(_ preferences: UsageDisplayPreferences) {
        for item in UsageDisplayItem.allCases {
            defaults.set(preferences.isVisible(item), forKey: item.defaultsKey)
        }
    }
}
