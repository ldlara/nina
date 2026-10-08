import Foundation

// Tokens do Nina DS v0 (ux-spec §6). Ficam no core, sem SwiftUI, para poderem ser auditados por teste
// (contraste WCAG 2.2 AA, ux-spec §10.2). O app mapeia estes valores para `Color`.

public struct RGB: Equatable, Hashable, Sendable {
    public let red: UInt8
    public let green: UInt8
    public let blue: UInt8

    public init(hex: UInt32) {
        red = UInt8((hex >> 16) & 0xFF)
        green = UInt8((hex >> 8) & 0xFF)
        blue = UInt8(hex & 0xFF)
    }

    private static func linear(_ component: UInt8) -> Double {
        let c = Double(component) / 255
        return c <= 0.03928 ? c / 12.92 : pow((c + 0.055) / 1.055, 2.4)
    }

    /// Luminância relativa (WCAG).
    public var luminance: Double {
        0.2126 * Self.linear(red) + 0.7152 * Self.linear(green) + 0.0722 * Self.linear(blue)
    }

    /// Razão de contraste WCAG entre duas cores (1...21).
    public static func contrast(_ a: RGB, _ b: RGB) -> Double {
        let l1 = max(a.luminance, b.luminance)
        let l2 = min(a.luminance, b.luminance)
        return (l1 + 0.05) / (l2 + 0.05)
    }
}

public enum NinaColorToken: String, CaseIterable, Sendable {
    case bgApp, bgSurface, bgSurfaceRaised
    case textPrimary, textSecondary, textOnAccent
    case accentPrimary, accentPrimaryPressed
    case eventSleep, eventFeeding, eventDiaper, eventPumping
    case stateSuccess, stateWarning, stateError
    case borderSubtle, focusRing

    public var light: RGB {
        switch self {
        case .bgApp: return RGB(hex: 0xFAF8F5)
        case .bgSurface: return RGB(hex: 0xFFFFFF)
        case .bgSurfaceRaised: return RGB(hex: 0xF1EEF7)
        case .textPrimary: return RGB(hex: 0x1F2430)
        case .textSecondary: return RGB(hex: 0x555C6E)
        case .textOnAccent: return RGB(hex: 0xFFFFFF)
        case .accentPrimary: return RGB(hex: 0x3B5BA9)
        case .accentPrimaryPressed: return RGB(hex: 0x2F4A8E)
        case .eventSleep: return RGB(hex: 0x5B4B9A)
        case .eventFeeding: return RGB(hex: 0xA5541A)
        case .eventDiaper: return RGB(hex: 0x2B7A66)
        case .eventPumping: return RGB(hex: 0x9A3F6B)
        case .stateSuccess: return RGB(hex: 0x2E7D4F)
        case .stateWarning: return RGB(hex: 0x8A5A00)
        case .stateError: return RGB(hex: 0xB3261E)
        case .borderSubtle: return RGB(hex: 0xDDD8E3)
        case .focusRing: return RGB(hex: 0x1F6FEB)
        }
    }

    /// Modo escuro "Noite": nunca `#000` nem branco puro (ux-spec §6.1).
    public var dark: RGB {
        switch self {
        case .bgApp: return RGB(hex: 0x12141C)
        case .bgSurface: return RGB(hex: 0x1B1F2B)
        case .bgSurfaceRaised: return RGB(hex: 0x242938)
        case .textPrimary: return RGB(hex: 0xE8EAF0)
        case .textSecondary: return RGB(hex: 0xB3B9C9)
        case .textOnAccent: return RGB(hex: 0x10131A)
        case .accentPrimary: return RGB(hex: 0x9DB4F0)
        case .accentPrimaryPressed: return RGB(hex: 0xB7C8F5)
        case .eventSleep: return RGB(hex: 0xB6A8F0)
        case .eventFeeding: return RGB(hex: 0xF0B27E)
        case .eventDiaper: return RGB(hex: 0x7FD0B8)
        case .eventPumping: return RGB(hex: 0xEBA0C4)
        case .stateSuccess: return RGB(hex: 0x7FD39E)
        case .stateWarning: return RGB(hex: 0xF2C14E)
        case .stateError: return RGB(hex: 0xFF9A93)
        case .borderSubtle: return RGB(hex: 0x2F3547)
        case .focusRing: return RGB(hex: 0x8CB4FF)
        }
    }
}

/// Espaçamento em grade de 4 pt, raios e alvos mínimos (ux-spec §6.3).
public enum NinaMetrics {
    public static let space1: Double = 4
    public static let space2: Double = 8
    public static let space3: Double = 12
    public static let space4: Double = 16
    public static let space5: Double = 20
    public static let space6: Double = 24
    public static let space8: Double = 32
    public static let gutter: Double = 16
    public static let radiusSmall: Double = 8
    public static let radiusMedium: Double = 14
    public static let radiusLarge: Double = 20
    public static let minTouchTarget: Double = 48
    public static let primaryTouchTarget: Double = 56
}
