import SwiftUI

enum AppLayout {
    static let popoverWidth: CGFloat = 368
    static let popoverPreviewInitialHeight: CGFloat = 640
}

enum AppTheme {
    static let panel = Color(red: 0.075, green: 0.078, blue: 0.088)
    static let card = Color(red: 0.125, green: 0.133, blue: 0.153)
    static let cardRaised = Color(red: 0.155, green: 0.165, blue: 0.188)
    static let border = Color.white.opacity(0.09)
    static let muted = Color.white.opacity(0.58)
    static let cyan = Color(red: 0.31, green: 0.76, blue: 0.82)
    static let blue = Color(red: 0.20, green: 0.52, blue: 1.0)
    static let green = Color(red: 0.25, green: 0.82, blue: 0.48)
    static let yellow = Color(red: 1.0, green: 0.80, blue: 0.24)
    static let red = Color(red: 1.0, green: 0.32, blue: 0.30)
}

struct CardModifier: ViewModifier {
    func body(content: Content) -> some View {
        content
            .padding(13)
            .background(
                RoundedRectangle(cornerRadius: 14, style: .continuous)
                    .fill(AppTheme.card)
                    .overlay(
                        RoundedRectangle(cornerRadius: 14, style: .continuous)
                            .stroke(AppTheme.border, lineWidth: 1)))
    }
}

extension View {
    func appCard() -> some View {
        modifier(CardModifier())
    }
}
