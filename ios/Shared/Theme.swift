import SwiftUI
import UIKit

extension Color {
    /// 品牌色（和 Windows 版一致）
    static let brand = Color(uiColor: UIColor { traits in
        traits.userInterfaceStyle == .dark
            ? UIColor(red: 0.482, green: 0.576, blue: 1.0, alpha: 1)
            : UIColor(red: 0.298, green: 0.431, blue: 0.961, alpha: 1)
    })

    static let overdue = Color(uiColor: UIColor { traits in
        traits.userInterfaceStyle == .dark
            ? UIColor(red: 1.0, green: 0.482, blue: 0.482, alpha: 1)
            : UIColor(red: 0.851, green: 0.212, blue: 0.212, alpha: 1)
    })

    static let done = Color(uiColor: UIColor { traits in
        traits.userInterfaceStyle == .dark
            ? UIColor(red: 0.373, green: 0.816, blue: 0.545, alpha: 1)
            : UIColor(red: 0.118, green: 0.62, blue: 0.353, alpha: 1)
    })

    init(hex: String) {
        var s = hex.trimmingCharacters(in: .whitespacesAndNewlines)
        if s.hasPrefix("#") { s.removeFirst() }
        if s.count >= 6, let v = UInt64(String(s.prefix(6)), radix: 16) {
            self.init(.sRGB,
                      red: Double((v >> 16) & 0xFF) / 255,
                      green: Double((v >> 8) & 0xFF) / 255,
                      blue: Double(v & 0xFF) / 255,
                      opacity: 1)
        } else {
            self.init(.sRGB, red: 0.56, green: 0.56, blue: 0.58, opacity: 1)
        }
    }
}
