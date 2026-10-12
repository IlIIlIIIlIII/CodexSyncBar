import Foundation

enum UsageDisplayItem: String, CaseIterable, Identifiable, Sendable {
    case codexWeekly
    case sparkWeekly

    var id: String { rawValue }

    var title: String {
        switch self {
        case .codexWeekly: "주간"
        case .sparkWeekly: "Spark 주간"
        }
    }

    var icon: String {
        switch self {
        case .codexWeekly: "calendar"
        case .sparkWeekly: "bolt.fill"
        }
    }

    var isSpark: Bool {
        switch self {
        case .codexWeekly: false
        case .sparkWeekly: true
        }
    }

    func window(in snapshot: UsageSnapshot) -> UsageWindow? {
        switch self {
        case .codexWeekly: snapshot.weekly
        case .sparkWeekly: snapshot.sparkWeekly
        }
    }

    fileprivate var defaultsKey: String {
        "usageDisplay.\(rawValue)"
    }
}

struct UsageDisplayPreferences: Equatable, Sendable {
    var codexWeekly: Bool
    var sparkWeekly: Bool

    static let allVisible = UsageDisplayPreferences(
        codexWeekly: true,
        sparkWeekly: true)

    func isVisible(_ item: UsageDisplayItem) -> Bool {
        switch item {
        case .codexWeekly: codexWeekly
        case .sparkWeekly: sparkWeekly
        }
    }

    mutating func setVisible(_ visible: Bool, for item: UsageDisplayItem) {
        switch item {
        case .codexWeekly: codexWeekly = visible
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
    private static let supportedDefaults = MenuBarUsagePreferences.defaultItems

    init(defaults: UserDefaults = .standard) {
        self.defaults = defaults
    }

    func load() -> MenuBarUsagePreferences {
        guard defaults.object(forKey: defaultsKey) != nil else { return .default }
        let rawItems = defaults.stringArray(forKey: defaultsKey) ?? []
        let supported = rawItems.compactMap(UsageDisplayItem.init(rawValue:))
        let preferences = MenuBarUsagePreferences(items: supported.isEmpty && !rawItems.isEmpty ? Self.supportedDefaults : supported)
        save(preferences)
        return preferences
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

    private func removeRetiredSettings() {
        for key in ["usageDisplay.fiveHour", "usageDisplay.sparkFiveHour"] {
            defaults.removeObject(forKey: key)
        }
    }

    func load() -> UsageDisplayPreferences {
        removeRetiredSettings()
        var preferences = UsageDisplayPreferences.allVisible
        for item in UsageDisplayItem.allCases {
            let visible = defaults.object(forKey: item.defaultsKey) as? Bool ?? true
            preferences.setVisible(visible, for: item)
        }
        return preferences
    }

    func save(_ preferences: UsageDisplayPreferences) {
        removeRetiredSettings()
        for item in UsageDisplayItem.allCases {
            defaults.set(preferences.isVisible(item), forKey: item.defaultsKey)
        }
    }
}
