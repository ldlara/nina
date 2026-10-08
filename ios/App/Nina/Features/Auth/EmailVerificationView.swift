import SwiftUI
import NinaCore

struct EmailVerificationScreen: View {
    let container: AppContainer
    @State private var viewModel: EmailVerificationViewModel

    init(container: AppContainer, pending: PendingVerification) {
        self.container = container
        let session = container.session
        _viewModel = State(initialValue: EmailVerificationViewModel(
            pending: pending, auth: container.authRepository, onAuthenticated: { session.didAuthenticate($0) }))
    }

    var body: some View {
        EmailVerificationView(viewModel: viewModel, onBack: { container.session.backToRegistration() })
    }
}

/// Confirmação de e-mail (ADR-0009): a conta só fica ativa e a sessão só nasce depois deste passo.
struct EmailVerificationView: View {
    @Bindable var viewModel: EmailVerificationViewModel
    let onBack: () -> Void

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: NinaMetrics.space5) {
                Button(action: onBack) { Label("common.back", systemImage: "chevron.left") }
                    .buttonStyle(.nina(.text))

                Text("verify.title").font(.ninaTitle1).foregroundStyle(Color(.textPrimary))
                    .accessibilityAddTraits(.isHeader)
                Text("verify.subtitle \(viewModel.email)")
                    .font(.ninaBody).foregroundStyle(Color(.textSecondary))
                    .fixedSize(horizontal: false, vertical: true)

                if let message = viewModel.message { MessageBanner(message: message) }
                if viewModel.didResend { MessageBanner(message: UserMessage("verify.resent"), style: .info) }

                NinaTextField(title: "verify.field.code", text: $viewModel.code, isSecure: false,
                              contentType: .oneTimeCode, keyboard: .numbersAndPunctuation,
                              submitLabel: .go, onSubmit: { Task { await viewModel.verify() } })

                Button {
                    Task { await viewModel.verify() }
                } label: {
                    Text("verify.submit")
                }
                .buttonStyle(.nina(.primary, isLoading: viewModel.isBusy))
                .disabled(!viewModel.canSubmit)

                if viewModel.canResendRequest {
                    // Contagem regressiva sem anunciar a cada segundo: o texto muda, mas só o botão é um alvo.
                    TimelineView(.periodic(from: .now, by: 1)) { context in
                        let remaining = viewModel.secondsUntilResend(at: context.date)
                        Button {
                            Task { await viewModel.resend() }
                        } label: {
                            if remaining > 0 {
                                Text("verify.resend_in \(remaining)")
                            } else {
                                Text("verify.resend")
                            }
                        }
                        .buttonStyle(.nina(.secondary))
                        .disabled(!viewModel.canResend(at: context.date))
                    }
                }
            }
            .padding(.horizontal, NinaMetrics.gutter)
            .padding(.vertical, NinaMetrics.space6)
        }
        .scrollDismissesKeyboard(.interactively)
    }
}
