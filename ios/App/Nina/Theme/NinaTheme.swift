import SwiftUI
import NinaCore

// Nina DS v0 (ux-spec §6). Cores vêm de `NinaColorToken` (auditadas por teste de contraste no NinaCore).

extension UIColor {
    convenience init(rgb: RGB) {
        self.init(red: CGFloat(rgb.red) / 255, green: CGFloat(rgb.green) / 255, blue: CGFloat(rgb.blue) / 255, alpha: 1)
    }
}

extension Color {
    /// Cor semântica que troca sozinha entre claro e "Noite" conforme o tema efetivo da interface.
    init(_ token: NinaColorToken) {
        self.init(uiColor: UIColor { traits in
            UIColor(rgb: traits.userInterfaceStyle == .dark ? token.dark : token.light)
        })
    }
}

/// Preferência de tema (ux-spec §8): sistema, claro ou "Noite".
enum ThemePreference: String, CaseIterable, Identifiable {
    case system, light, night
    var id: String { rawValue }

    var colorScheme: ColorScheme? {
        switch self {
        case .system: return nil
        case .light: return .light
        case .night: return .dark
        }
    }

    var titleKey: LocalizedStringKey {
        switch self {
        case .system: return "theme.system"
        case .light: return "theme.light"
        case .night: return "theme.night"
        }
    }

    static let storageKey = "theme.preference"
}

/// Estilos de texto: sempre baseados em *text styles* para escalar com Dynamic Type até AX5.
extension Font {
    static var ninaDisplay: Font { .largeTitle.weight(.semibold).monospacedDigit() }
    static var ninaTitle1: Font { .title.weight(.semibold) }
    static var ninaTitle2: Font { .title2.weight(.semibold) }
    static var ninaBody: Font { .body }
    static var ninaBodyStrong: Font { .body.weight(.semibold) }
    static var ninaCallout: Font { .callout }
    static var ninaCaption: Font { .caption }
}

extension View {
    /// Fundo de tela do app (`bg/app`).
    func ninaScreenBackground() -> some View {
        background(Color(.bgApp).ignoresSafeArea())
    }

    /// Cartão (`bg/surface`, raio médio). No escuro a hierarquia vem da superfície, não de sombra.
    func ninaCard() -> some View {
        padding(NinaMetrics.space4)
            .frame(maxWidth: .infinity, alignment: .leading)
            .background(Color(.bgSurface), in: RoundedRectangle(cornerRadius: NinaMetrics.radiusMedium))
            .overlay(RoundedRectangle(cornerRadius: NinaMetrics.radiusMedium).stroke(Color(.borderSubtle), lineWidth: 1))
    }

    /// Título de seção: marcado como cabeçalho para o rotor do VoiceOver.
    func ninaHeading() -> some View {
        font(.ninaTitle2)
            .foregroundStyle(Color(.textPrimary))
            .accessibilityAddTraits(.isHeader)
    }
}

/// Anima só se "Reduzir movimento" estiver desligado (ux-spec §10.1).
struct MotionAwareAnimation: ViewModifier {
    @Environment(\.accessibilityReduceMotion) private var reduceMotion
    let value: AnyHashable

    func body(content: Content) -> some View {
        content.animation(reduceMotion ? nil : .easeOut(duration: 0.2), value: value)
    }
}

extension View {
    func ninaAnimation<V: Hashable>(value: V) -> some View {
        modifier(MotionAwareAnimation(value: AnyHashable(value)))
    }
}
