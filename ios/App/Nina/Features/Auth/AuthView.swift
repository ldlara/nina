import SwiftUI
import NinaCore

/// Hospeda o `AuthViewModel` (estado preservado ao alternar entre entrar e criar conta).
struct AuthScreen: View {
    let container: AppContainer
    @State private var viewModel: AuthViewModel

    init(container: AppContainer, initialMode: AuthMode) {
        self.container = container
        let session = container.session
        let timezone = TimeZone.current.identifier
        let locale = Locale.current.identifier.replacingOccurrences(of: "_", with: "-")
        _viewModel = State(initialValue: AuthViewModel(
            mode: initialMode, auth: container.authRepository, consents: container.consentRepository,
            socialProviders: container.socialProviders, locale: locale, timezone: timezone,
            onAuthenticated: { session.didAuthenticate($0) },
            onVerificationRequired: { session.beginEmailVerification($0) }))
    }

    var body: some View {
        AuthView(viewModel: viewModel, notice: container.session.notice)
    }
}

struct AuthView: View {
    @Bindable var viewModel: AuthViewModel
    let notice: UserMessage?
    @FocusState private var focus: AuthViewModel.Field?
    @State private var showingReset = false

    private var isConsentStep: Bool { viewModel.mode == .register && viewModel.step == .consents }

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: NinaMetrics.space5) {
                header
                if let notice { MessageBanner(message: notice, style: .info) }
                if let banner = viewModel.banner { MessageBanner(message: banner) }

                if viewModel.socialConsentRequired {
                    socialConsentSection
                } else if isConsentStep {
                    consentSection
                } else {
                    credentialsSection
                }
            }
            .padding(.horizontal, NinaMetrics.gutter)
            .padding(.vertical, NinaMetrics.space6)
        }
        .scrollDismissesKeyboard(.interactively)
        .ninaAnimation(value: viewModel.step)
        .sheet(isPresented: $showingReset) {
            PasswordResetRequestView(viewModel: viewModel)
        }
    }

    // MARK: - Cabeçalho

    @ViewBuilder private var header: some View {
        VStack(alignment: .leading, spacing: NinaMetrics.space2) {
            if isConsentStep {
                Button {
                    viewModel.goBackToCredentials()
                } label: {
                    Label("common.back", systemImage: "chevron.left")
                }
                .buttonStyle(.nina(.text))
                .accessibilityHint(Text("a11y.back_keeps_input"))
            }
            Text(titleKey)
                .font(.ninaTitle1)
                .foregroundStyle(Color(.textPrimary))
                .accessibilityAddTraits(.isHeader)
            if isConsentStep {
                Text("auth.consents.step").font(.ninaCallout).foregroundStyle(Color(.textSecondary))
            }
        }
    }

    private var titleKey: LocalizedStringKey {
        if viewModel.socialConsentRequired { return "auth.consents.title" }
        switch (viewModel.mode, viewModel.step) {
        case (.login, _): return "auth.login.title"
        case (.register, .credentials): return "auth.register.title"
        case (.register, .consents): return "auth.consents.title"
        }
    }

    // MARK: - Credenciais

    private var credentialsSection: some View {
        VStack(alignment: .leading, spacing: NinaMetrics.space4) {
            if !viewModel.availableSocialProviders.isEmpty {
                SocialButtons(viewModel: viewModel)
                HStack(spacing: NinaMetrics.space3) {
                    Rectangle().fill(Color(.borderSubtle)).frame(height: 1)
                    Text("auth.or").font(.ninaCaption).foregroundStyle(Color(.textSecondary))
                    Rectangle().fill(Color(.borderSubtle)).frame(height: 1)
                }
                .accessibilityHidden(true)
            }

            if viewModel.mode == .register {
                NinaTextField(title: "auth.field.name", text: $viewModel.displayName, contentType: .givenName,
                              autocapitalization: .words)
                    .focused($focus, equals: .displayName)
            }
            NinaTextField(title: "auth.field.email", text: $viewModel.email, error: viewModel.fieldErrors[.email],
                          contentType: viewModel.mode == .login ? .username : .emailAddress, keyboard: .emailAddress,
                          onSubmit: { focus = .password })
                .focused($focus, equals: .email)
            NinaTextField(title: "auth.field.password", text: $viewModel.password, error: viewModel.fieldErrors[.password],
                          isSecure: true, contentType: viewModel.mode == .login ? .password : .newPassword,
                          submitLabel: .go, onSubmit: { Task { await viewModel.submit() } })
                .focused($focus, equals: .password)

            if viewModel.mode == .register { PasswordStrengthView(strength: viewModel.passwordStrength) }

            Button {
                focus = nil
                Task { await viewModel.submit() }
            } label: {
                Text(LocalizedStringKey(viewModel.mode == .login ? "auth.login.submit" : "common.continue"))
            }
            .buttonStyle(.nina(.primary, isLoading: viewModel.isBusy))
            .disabled(viewModel.isBusy)

            if viewModel.mode == .login {
                Button("auth.forgot_password") { showingReset = true }
                    .buttonStyle(.nina(.text))
            }

            Button {
                viewModel.setMode(viewModel.mode == .login ? .register : .login)
            } label: {
                Text(LocalizedStringKey(viewModel.mode == .login ? "auth.switch_to_register" : "auth.switch_to_login"))
            }
            .buttonStyle(.nina(.secondary))
        }
    }

    // MARK: - Consentimentos (E3)

    private var consentSection: some View {
        VStack(alignment: .leading, spacing: NinaMetrics.space4) {
            LegalConsentFields(viewModel: viewModel)
            Button {
                Task { await viewModel.submit() }
            } label: {
                Text("auth.register.submit")
            }
            .buttonStyle(.nina(.primary, isLoading: viewModel.isBusy))
            .disabled(viewModel.isBusy)
        }
    }

    private var socialConsentSection: some View {
        VStack(alignment: .leading, spacing: NinaMetrics.space4) {
            LegalConsentFields(viewModel: viewModel, showsOptionals: false)
            Button {
                Task { await viewModel.confirmSocialConsent() }
            } label: {
                Text("common.continue")
            }
            .buttonStyle(.nina(.primary, isLoading: viewModel.isBusy))
            .disabled(viewModel.isBusy)
            Button("common.cancel") { viewModel.cancelSocialConsent() }
                .buttonStyle(.nina(.text))
        }
    }
}

/// Aceite obrigatório (Termos + Política, sem pré-marcação) e opcionais separados e desmarcados (RF-003).
private struct LegalConsentFields: View {
    @Bindable var viewModel: AuthViewModel
    var showsOptionals = true

    var body: some View {
        VStack(alignment: .leading, spacing: NinaMetrics.space4) {
            NinaCheckbox(isOn: $viewModel.acceptsLegal) {
                Text("auth.consents.legal")
            }
            if let error = viewModel.fieldErrors[.terms] { FieldErrorLabel(message: error) }

            VStack(alignment: .leading, spacing: NinaMetrics.space2) {
                if let terms = viewModel.legalURL(for: .termsOfUse) {
                    Link("auth.consents.read_terms", destination: terms)
                }
                if let privacy = viewModel.legalURL(for: .privacyPolicy) {
                    Link("auth.consents.read_privacy", destination: privacy)
                }
            }
            .font(.ninaCallout)
            .frame(minHeight: NinaMetrics.minTouchTarget, alignment: .leading)

            if showsOptionals {
                Text("auth.consents.optional_title").ninaHeading()
                Text("auth.consents.optional_hint").font(.ninaCallout).foregroundStyle(Color(.textSecondary))
                NinaCheckbox(isOn: $viewModel.analyticsOptIn) { Text("auth.consents.analytics") }
                NinaCheckbox(isOn: $viewModel.marketingOptIn) { Text("auth.consents.marketing") }
            }
        }
    }
}

private struct PasswordStrengthView: View {
    let strength: Validators.PasswordStrength

    var body: some View {
        if strength != .empty {
            HStack(spacing: NinaMetrics.space2) {
                Image(systemName: symbol).accessibilityHidden(true)
                Text(label)
            }
            .font(.ninaCaption)
            .foregroundStyle(Color(.textSecondary))
            .accessibilityElement(children: .combine)
        }
    }

    private var symbol: String {
        switch strength {
        case .empty, .weak: return "circle"
        case .fair: return "circle.lefthalf.filled"
        case .strong: return "circle.fill"
        }
    }

    private var label: LocalizedStringKey {
        switch strength {
        case .empty, .weak: return "auth.password.weak"
        case .fair: return "auth.password.fair"
        case .strong: return "auth.password.strong"
        }
    }
}

private struct SocialButtons: View {
    let viewModel: AuthViewModel

    var body: some View {
        VStack(spacing: NinaMetrics.space3) {
            ForEach(viewModel.availableSocialProviders, id: \.self) { provider in
                Button {
                    Task { await viewModel.signInWithSocial(provider) }
                } label: {
                    HStack(spacing: NinaMetrics.space2) {
                        Image(systemName: provider == .apple ? "apple.logo" : "g.circle.fill").accessibilityHidden(true)
                        Text(LocalizedStringKey(provider == .apple ? "auth.social.apple" : "auth.social.google"))
                    }
                }
                .buttonStyle(.nina(.secondary))
                .disabled(viewModel.isBusy)
            }
        }
    }
}

private struct PasswordResetRequestView: View {
    @Bindable var viewModel: AuthViewModel
    @Environment(\.dismiss) private var dismiss

    var body: some View {
        NavigationStack {
            ScrollView {
                VStack(alignment: .leading, spacing: NinaMetrics.space4) {
                    Text("auth.reset.hint").font(.ninaBody).foregroundStyle(Color(.textSecondary))
                    if let banner = viewModel.banner { MessageBanner(message: banner) }
                    if viewModel.passwordResetRequested {
                        MessageBanner(message: UserMessage("auth.reset.sent"), style: .info)
                    }
                    NinaTextField(title: "auth.field.email", text: $viewModel.email, error: viewModel.fieldErrors[.email],
                                  contentType: .emailAddress, keyboard: .emailAddress, submitLabel: .send,
                                  onSubmit: { Task { await viewModel.requestPasswordReset() } })
                    Button("auth.reset.submit") { Task { await viewModel.requestPasswordReset() } }
                        .buttonStyle(.nina(.primary, isLoading: viewModel.isBusy))
                        .disabled(viewModel.isBusy)
                }
                .padding(NinaMetrics.gutter)
            }
            .navigationTitle(Text("auth.reset.title"))
            .navigationBarTitleDisplayMode(.inline)
            .toolbar {
                ToolbarItem(placement: .cancellationAction) { Button("common.close") { dismiss() } }
            }
            .ninaScreenBackground()
        }
    }
}
