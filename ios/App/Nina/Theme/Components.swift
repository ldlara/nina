import SwiftUI
import NinaCore

// MARK: - Botões (ux-spec §6.4): alvo mínimo 48 pt (primário 56 pt), texto quebra em vez de truncar.

enum NinaButtonKind {
    case primary, secondary, text, destructive
}

struct NinaButtonStyle: ButtonStyle {
    let kind: NinaButtonKind
    var isLoading = false
    @Environment(\.isEnabled) private var isEnabled

    func makeBody(configuration: Configuration) -> some View {
        HStack(spacing: NinaMetrics.space2) {
            if isLoading { ProgressView().tint(foreground(configuration)) }
            configuration.label
                .font(.ninaBodyStrong)
                .multilineTextAlignment(.center)
                .fixedSize(horizontal: false, vertical: true)
        }
        .foregroundStyle(foreground(configuration))
        .padding(.horizontal, NinaMetrics.space4)
        .padding(.vertical, NinaMetrics.space3)
        .frame(maxWidth: kind == .text ? nil : .infinity,
               minHeight: kind == .primary ? NinaMetrics.primaryTouchTarget : NinaMetrics.minTouchTarget)
        .background(background(configuration), in: RoundedRectangle(cornerRadius: NinaMetrics.radiusMedium))
        .overlay {
            if kind == .secondary || kind == .destructive {
                RoundedRectangle(cornerRadius: NinaMetrics.radiusMedium)
                    .stroke(foreground(configuration), lineWidth: 1.5)
            }
        }
        .opacity(isEnabled ? 1 : 0.5)
        .contentShape(Rectangle())
    }

    private func foreground(_ configuration: Configuration) -> Color {
        switch kind {
        case .primary: return Color(.textOnAccent)
        case .secondary, .text: return Color(configuration.isPressed ? NinaColorToken.accentPrimaryPressed : NinaColorToken.accentPrimary)
        case .destructive: return Color(.stateError)
        }
    }

    private func background(_ configuration: Configuration) -> Color {
        switch kind {
        case .primary: return Color(configuration.isPressed ? NinaColorToken.accentPrimaryPressed : NinaColorToken.accentPrimary)
        case .secondary, .text, .destructive: return .clear
        }
    }
}

extension ButtonStyle where Self == NinaButtonStyle {
    static func nina(_ kind: NinaButtonKind, isLoading: Bool = false) -> NinaButtonStyle {
        NinaButtonStyle(kind: kind, isLoading: isLoading)
    }
}

// MARK: - Campo de formulário com rótulo persistente e erro (ícone + texto, nunca só cor)

struct NinaTextField: View {
    let title: LocalizedStringKey
    @Binding var text: String
    var error: UserMessage?
    var isSecure = false
    var contentType: UITextContentType?
    var keyboard: UIKeyboardType = .default
    var autocapitalization: TextInputAutocapitalization = .never
    var submitLabel: SubmitLabel = .next
    var onSubmit: (() -> Void)?

    var body: some View {
        VStack(alignment: .leading, spacing: NinaMetrics.space1) {
            Text(title)
                .font(.ninaCallout)
                .foregroundStyle(Color(.textSecondary))
                .accessibilityHidden(true)
            field
                .textContentType(contentType)
                .keyboardType(keyboard)
                .textInputAutocapitalization(autocapitalization)
                .autocorrectionDisabled()
                .submitLabel(submitLabel)
                .onSubmit { onSubmit?() }
                .font(.ninaBody)
                .foregroundStyle(Color(.textPrimary))
                .padding(.horizontal, NinaMetrics.space3)
                .frame(minHeight: NinaMetrics.minTouchTarget)
                .background(Color(.bgSurface), in: RoundedRectangle(cornerRadius: NinaMetrics.radiusSmall))
                .overlay(
                    // Borda de campo com contraste >= 3:1 (textSecondary); `borderSubtle` é só divisor.
                    RoundedRectangle(cornerRadius: NinaMetrics.radiusSmall)
                        .stroke(Color(error == nil ? NinaColorToken.textSecondary : NinaColorToken.stateError), lineWidth: error == nil ? 1 : 2)
                )
                .accessibilityLabel(Text(title))
                .accessibilityHint(error.map { Text(LocalizedMessage.text(for: $0)) } ?? Text(""))
            if let error {
                FieldErrorLabel(message: error)
            }
        }
    }

    @ViewBuilder private var field: some View {
        if isSecure {
            SecureField("", text: $text)
        } else {
            TextField("", text: $text)
        }
    }
}

struct FieldErrorLabel: View {
    let message: UserMessage

    var body: some View {
        Label {
            Text(LocalizedMessage.text(for: message))
        } icon: {
            Image(systemName: "exclamationmark.circle.fill")
        }
        .font(.ninaCaption)
        .foregroundStyle(Color(.stateError))
        .accessibilityElement(children: .combine)
    }
}

// MARK: - Banners

struct MessageBanner: View {
    enum Style { case error, warning, info }

    let message: UserMessage
    var style: Style = .error

    private var color: Color {
        switch style {
        case .error: return Color(.stateError)
        case .warning: return Color(.stateWarning)
        case .info: return Color(.accentPrimary)
        }
    }

    private var symbol: String {
        switch style {
        case .error: return "exclamationmark.triangle.fill"
        case .warning: return "wifi.slash"
        case .info: return "info.circle.fill"
        }
    }

    var body: some View {
        HStack(alignment: .firstTextBaseline, spacing: NinaMetrics.space3) {
            Image(systemName: symbol).foregroundStyle(color).accessibilityHidden(true)
            Text(LocalizedMessage.text(for: message))
                .font(.ninaCallout)
                .foregroundStyle(Color(.textPrimary))
                .fixedSize(horizontal: false, vertical: true)
            Spacer(minLength: 0)
        }
        .padding(NinaMetrics.space3)
        .background(Color(.bgSurfaceRaised), in: RoundedRectangle(cornerRadius: NinaMetrics.radiusSmall))
        .overlay(RoundedRectangle(cornerRadius: NinaMetrics.radiusSmall).stroke(color, lineWidth: 1))
        .accessibilityElement(children: .combine)
        .accessibilityAddTraits(.updatesFrequently)
    }
}

// MARK: - Caixa de seleção (alvo >= 44 pt, estado falado)

struct NinaCheckbox<Label: View>: View {
    @Binding var isOn: Bool
    @ViewBuilder var label: Label

    var body: some View {
        Button {
            isOn.toggle()
        } label: {
            HStack(alignment: .top, spacing: NinaMetrics.space3) {
                Image(systemName: isOn ? "checkmark.square.fill" : "square")
                    .font(.title2)
                    .foregroundStyle(Color(isOn ? NinaColorToken.accentPrimary : NinaColorToken.textSecondary))
                    .frame(minWidth: 28)
                    .accessibilityHidden(true)
                label
                    .font(.ninaBody)
                    .foregroundStyle(Color(.textPrimary))
                    .multilineTextAlignment(.leading)
                    .fixedSize(horizontal: false, vertical: true)
                Spacer(minLength: 0)
            }
            .frame(minHeight: NinaMetrics.minTouchTarget, alignment: .top)
            .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .accessibilityAddTraits(isOn ? [.isSelected] : [])
        .accessibilityValue(Text(LocalizedStringKey(isOn ? "a11y.checked" : "a11y.unchecked")))
    }
}

// MARK: - Layout que vira coluna em tamanhos de acessibilidade

struct AdaptiveStack<Content: View>: View {
    @Environment(\.dynamicTypeSize) private var typeSize
    var spacing: CGFloat = NinaMetrics.space3
    @ViewBuilder var content: Content

    var body: some View {
        if typeSize.isAccessibilitySize {
            VStack(alignment: .leading, spacing: spacing) { content }
        } else {
            HStack(spacing: spacing) { content }
        }
    }
}

// MARK: - Avatar de letra (forma + inicial; nunca só cor)

struct InitialAvatar: View {
    let name: String
    var size: CGFloat = 40

    var body: some View {
        Text(String(name.trimmingCharacters(in: .whitespaces).first.map { String($0).uppercased() } ?? "?"))
            .font(.ninaBodyStrong)
            .foregroundStyle(Color(.accentPrimary))
            .frame(width: size, height: size)
            .background(Color(.bgSurfaceRaised), in: Circle())
            .overlay(Circle().stroke(Color(.accentPrimary), lineWidth: 1.5))
            .accessibilityHidden(true)
    }
}
