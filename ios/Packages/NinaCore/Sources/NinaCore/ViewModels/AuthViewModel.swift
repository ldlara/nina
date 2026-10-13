import Foundation
import Observation

@Observable
@MainActor
public final class AuthViewModel {
    public enum Field: Hashable, Sendable { case email, password, displayName, terms }
    public enum Step: Equatable, Sendable { case credentials, consents }

    public var mode: AuthMode
    public var email = ""
    public var password = ""
    public var displayName = ""
    /// Um único aceite cobre Termos e Política (UX 4.1 E3); nunca pré-marcado.
    public var acceptsLegal = false
    public var analyticsOptIn = false
    public var marketingOptIn = false

    public private(set) var step: Step = .credentials
    public private(set) var isBusy = false
    public private(set) var banner: UserMessage?
    public private(set) var fieldErrors: [Field: UserMessage] = [:]
    public private(set) var legalDocuments: [LegalDocument] = []
    /// `true` quando o login social criaria conta nova e o servidor exigiu o aceite (403 CONSENT_REQUIRED).
    public private(set) var socialConsentRequired = false
    /// `true` depois de pedir a recuperação de senha (resposta idêntica exista ou não a conta, RF-002-A1).
    public private(set) var passwordResetRequested = false

    @ObservationIgnored private let auth: AuthRepository
    @ObservationIgnored private let consentRepository: ConsentRepository
    @ObservationIgnored private let socialProviders: [IdentityProvider: SocialSignInProvider]
    @ObservationIgnored private let locale: String
    @ObservationIgnored private let timezone: String?
    @ObservationIgnored private let onAuthenticated: @MainActor (User) -> Void
    @ObservationIgnored private let onVerificationRequired: @MainActor (PendingVerification) -> Void
    @ObservationIgnored private var pendingSocial: (credential: SocialCredential, provider: IdentityProvider)?

    public init(mode: AuthMode, auth: AuthRepository, consents: ConsentRepository,
                socialProviders: [SocialSignInProvider], locale: String, timezone: String?,
                onAuthenticated: @escaping @MainActor (User) -> Void,
                onVerificationRequired: @escaping @MainActor (PendingVerification) -> Void) {
        self.mode = mode
        self.auth = auth
        self.consentRepository = consents
        self.socialProviders = Dictionary(socialProviders.map { ($0.provider, $0) },
                                          uniquingKeysWith: { first, _ in first })
        self.locale = locale
        self.timezone = timezone
        self.onAuthenticated = onAuthenticated
        self.onVerificationRequired = onVerificationRequired
    }

    // MARK: - Derivados

    public var passwordStrength: Validators.PasswordStrength { Validators.passwordStrength(password) }
    public var availableSocialProviders: [IdentityProvider] {
        [IdentityProvider.apple, .google].filter { socialProviders[$0] != nil }
    }
    public func legalURL(for purpose: PurposeKey) -> URL? {
        legalDocuments.first { $0.purposeKey == purpose }?.url
    }

    public func setMode(_ newMode: AuthMode) {
        mode = newMode
        step = .credentials
        banner = nil
        fieldErrors = [:]
    }

    public func goBackToCredentials() {
        step = .credentials
        banner = nil
    }

    // MARK: - Ações

    public func loadLegalDocuments() async {
        guard legalDocuments.isEmpty else { return }
        legalDocuments = (try? await consentRepository.legalDocuments()) ?? []
    }

    /// Botão principal: login, ou (cadastro) avançar para consentimentos e depois enviar.
    public func submit() async {
        banner = nil
        fieldErrors = [:]
        switch mode {
        case .login: await submitLogin()
        case .register:
            switch step {
            case .credentials: await advanceToConsents()
            case .consents: await submitRegistration()
            }
        }
    }

    public func requestPasswordReset() async {
        banner = nil
        fieldErrors = [:]
        let trimmed = email.trimmingCharacters(in: .whitespacesAndNewlines)
        guard Validators.isPlausibleEmail(trimmed) else {
            fieldErrors[.email] = UserMessage("validation.email_invalid")
            return
        }
        await run {
            try await self.auth.requestPasswordReset(email: trimmed)
            self.passwordResetRequested = true
        }
    }

    private func submitLogin() async {
        let trimmed = email.trimmingCharacters(in: .whitespacesAndNewlines)
        if !Validators.isPlausibleEmail(trimmed) { fieldErrors[.email] = UserMessage("validation.email_invalid") }
        if password.isEmpty { fieldErrors[.password] = UserMessage("validation.required") }
        guard fieldErrors.isEmpty else { return }
        await run {
            let user = try await self.auth.login(email: trimmed, password: self.password)
            self.onAuthenticated(user)
        }
    }

    private func advanceToConsents() async {
        let trimmed = email.trimmingCharacters(in: .whitespacesAndNewlines)
        if !Validators.isPlausibleEmail(trimmed) { fieldErrors[.email] = UserMessage("validation.email_invalid") }
        if password.count < Validators.minimumPasswordLength {
            fieldErrors[.password] = UserMessage("validation.password_too_short",
                                                 arguments: [String(Validators.minimumPasswordLength)])
        }
        guard fieldErrors.isEmpty else { return }
        step = .consents
        await loadLegalDocuments()
    }

    private func submitRegistration() async {
        guard acceptsLegal else {
            fieldErrors[.terms] = UserMessage("validation.terms_required")
            return
        }
        await loadLegalDocuments()
        guard let consents = consentAcceptances() else {
            banner = UserMessage("error.legal_unavailable")
            return
        }
        let name = displayName.trimmingCharacters(in: .whitespacesAndNewlines)
        let request = RegisterRequest(email: email.trimmingCharacters(in: .whitespacesAndNewlines),
                                      password: password, displayName: name.isEmpty ? nil : name,
                                      locale: locale, timezone: timezone, consents: consents)
        await run {
            let pending = try await self.auth.register(request)
            self.onVerificationRequired(PendingVerification(email: request.email, resendRequest: request,
                                                            resendAfterSeconds: pending.resendAfterSeconds ?? 60))
        }
    }

    /// Termos e Política são obrigatórios; opcionais só entram se marcados. `nil` = versões indisponíveis.
    func consentAcceptances() -> [ConsentAcceptance]? {
        guard let terms = legalDocuments.first(where: { $0.purposeKey == .termsOfUse }),
              let privacy = legalDocuments.first(where: { $0.purposeKey == .privacyPolicy }) else { return nil }
        var list = [ConsentAcceptance(purposeKey: .termsOfUse, documentVersion: terms.version),
                    ConsentAcceptance(purposeKey: .privacyPolicy, documentVersion: privacy.version)]
        // Se o catálogo não trouxer documento próprio da finalidade opcional, usa a versão da Política de
        // Privacidade (que descreve esses usos). A confirmar com o backend (ver README).
        func version(for purpose: PurposeKey) -> String {
            legalDocuments.first(where: { $0.purposeKey == purpose })?.version ?? privacy.version
        }
        if analyticsOptIn { list.append(ConsentAcceptance(purposeKey: .analyticsProduct, documentVersion: version(for: .analyticsProduct))) }
        if marketingOptIn { list.append(ConsentAcceptance(purposeKey: .marketingEmail, documentVersion: version(for: .marketingEmail))) }
        return list
    }

    public func signInWithSocial(_ provider: IdentityProvider) async {
        banner = nil
        guard let socialProvider = socialProviders[provider] else {
            banner = UserMessage("error.social_not_configured")
            return
        }
        isBusy = true
        defer { isBusy = false }
        do {
            let credential = try await socialProvider.authenticate()
            await completeSocial(credential: credential, provider: provider)
        } catch SocialSignInError.cancelled {
            // Cancelar não é erro: nenhuma mensagem.
        } catch {
            banner = ErrorMapper.message(for: error)
        }
    }

    /// Segunda etapa do login social quando a conta é nova e falta o aceite.
    public func confirmSocialConsent() async {
        guard let pending = pendingSocial else { return }
        guard acceptsLegal else {
            fieldErrors[.terms] = UserMessage("validation.terms_required")
            return
        }
        isBusy = true
        defer { isBusy = false }
        await loadLegalDocuments()
        await completeSocial(credential: pending.credential, provider: pending.provider)
    }

    public func cancelSocialConsent() {
        pendingSocial = nil
        socialConsentRequired = false
    }

    private func completeSocial(credential: SocialCredential, provider: IdentityProvider) async {
        let consents = acceptsLegal ? consentAcceptances() : nil
        do {
            let user = try await auth.signIn(with: credential, provider: provider, consents: consents,
                                             locale: locale, timezone: timezone)
            pendingSocial = nil
            socialConsentRequired = false
            onAuthenticated(user)
        } catch let error as APIError where error.problemCode == ProblemCode.consentRequired {
            pendingSocial = (credential, provider)
            socialConsentRequired = true
            await loadLegalDocuments()
        } catch {
            banner = ErrorMapper.message(for: error)
        }
    }

    private func run(_ work: @escaping @MainActor () async throws -> Void) async {
        isBusy = true
        defer { isBusy = false }
        do {
            try await work()
        } catch {
            banner = ErrorMapper.message(for: error)
            let serverFields = ErrorMapper.fieldMessages(for: error)
            if let message = serverFields["email"] { fieldErrors[.email] = message }
            if let message = serverFields["password"] { fieldErrors[.password] = message }
        }
    }
}
