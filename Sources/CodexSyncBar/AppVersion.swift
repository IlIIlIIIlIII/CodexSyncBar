import Foundation

enum AppVersion {
    static var current: String {
        current(infoDictionary: Bundle.main.infoDictionary)
    }

    static func current(infoDictionary: [String: Any]?) -> String {
        guard let value = infoDictionary?["CFBundleShortVersionString"] as? String else {
            return "development"
        }
        let normalized = value.trimmingCharacters(in: .whitespacesAndNewlines)
        return normalized.isEmpty ? "development" : normalized
    }
}
