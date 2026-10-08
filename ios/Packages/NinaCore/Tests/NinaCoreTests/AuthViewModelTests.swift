import XCTest
@testable import NinaCore

@MainActor
final class AuthViewModelTests: XCTestCase {
    private var authenticated: [User] = []
    private var pendings: [PendingVerification] = []

    private func makeVM(mode: AuthMode, auth: FakeAuthRepository = FakeAuthRepository(),
                        consents: FakeConsentRepository = FakeConsentRepository(),
                        social: [SocialSignInProvider] = []) -> AuthViewModel {
        authenticated = []
        pendings = []
        return AuthViewModel(mode: mode, auth: auth, consents: consents, socialProviders: social,
                             locale: "pt-BR", timezone: "America/Sao_Paulo",
                             onAuthenticated: { [unowned self] in self.authenticated.append($0) },
                             onVerificationRequired: { [unowned self] in self.pendings.append($0) })
    }

    func testLoginValidatesBeforeCallingServer() async {
        let auth = FakeAuthRepository()
        let vm = makeVM(mode: .login, auth: auth)
        vm.email = "nao-e-email"
        await vm.submit()
        XCTAssertEqual(vm.fieldErrors[.email]?.key, "validation.email_invalid")
        XCTAssertEqual(vm.fieldErrors[.password]?.key, "validation.required")
        XCTAssertTrue(auth.loginCalls.isEmpty)
    }

    func testLoginSuccessNotifiesAndTrimsEmail() async {
        let auth = FakeAuthRepository()
        let vm = makeVM(mode: .login, auth: auth)
        vm.email = "  ana@example.org "
        vm.password = "senha"
        await vm.submit()
        XCTAssertEqual(auth.loginCalls.first?.0, "ana@example.org")
        XCTAssertEqual(authenticated.count, 1)
        XCTAssertNil(vm.banner)
        XCTAssertFalse(vm.isBusy)
    }

    func testLoginInvalidCredentialsShowsGenericLocalizedMessage() async {
        let auth = FakeAuthRepository()
        auth.loginResult = .failure(APIError.problem(Problem(code: "INVALID_CREDENTIALS", status: 401), httpStatus: 401))
        let vm = makeVM(mode: .login, auth: auth)
        vm.email = "ana@example.org"
        vm.password = "errada"
        await vm.submit()
        XCTAssertEqual(vm.banner?.key, "error.invalid_credentials")
        XCTAssertTrue(authenticated.isEmpty)
    }

    func testLoginOfflineShowsOfflineMessage() async {
        let auth = FakeAuthRepository()
        auth.loginResult = .failure(APIError.network(URLError(.notConnectedToInternet)))
        let vm = makeVM(mode: .login, auth: auth)
        vm.email = "ana@example.org"
        vm.password = "x"
        await vm.submit()
        XCTAssertEqual(vm.banner, .offline)
    }

    func testRegisterIsTwoStepsAndRequiresExplicitLegalConsent() async {
        let auth = FakeAuthRepository()
        let vm = makeVM(mode: .register, auth: auth)
        vm.email = "ana@example.org"
        vm.password = "curta"
        await vm.submit()
        XCTAssertEqual(vm.step, .credentials)
        XCTAssertEqual(vm.fieldErrors[.password]?.key, "validation.password_too_short")

        vm.password = "senha-bem-longa-123"
        await vm.submit()
        XCTAssertEqual(vm.step, .consents)
        XCTAssertFalse(vm.acceptsLegal, "aceite nunca vem pré-marcado")

        await vm.submit()
        XCTAssertEqual(vm.fieldErrors[.terms]?.key, "validation.terms_required")
        XCTAssertTrue(auth.registerCalls.isEmpty)
    }

    func testRegisterSendsVersionedConsentsAndOnlyCheckedOptionals() async throws {
        let auth = FakeAuthRepository()
        let vm = makeVM(mode: .register, auth: auth)
        vm.email = "ana@example.org"
        vm.password = "senha-bem-longa-123"
        vm.displayName = "Ana"
        await vm.submit()
        vm.acceptsLegal = true
        vm.marketingOptIn = true
        await vm.submit()

        let request = try XCTUnwrap(auth.registerCalls.first)
        XCTAssertEqual(request.consents.map(\.purposeKey), [.termsOfUse, .privacyPolicy, .marketingEmail])
        XCTAssertEqual(request.consents[0].documentVersion, "1.2.0")
        XCTAssertEqual(request.consents[1].documentVersion, "1.1.0")
        XCTAssertEqual(request.locale, "pt-BR")
        XCTAssertEqual(request.displayName, "Ana")
        XCTAssertEqual(pendings.first?.email, "ana@example.org")
        XCTAssertNotNil(pendings.first?.resendRequest)
        XCTAssertTrue(authenticated.isEmpty, "conta só fica ativa após confirmar o e-mail")
    }

    func testRegisterBlocksWhenLegalVersionsUnavailable() async {
        let auth = FakeAuthRepository()
        let consents = FakeConsentRepository()
        consents.documents = []
        let vm = makeVM(mode: .register, auth: auth, consents: consents)
        vm.email = "ana@example.org"
        vm.password = "senha-bem-longa-123"
        await vm.submit()
        vm.acceptsLegal = true
        await vm.submit()
        XCTAssertEqual(vm.banner?.key, "error.legal_unavailable")
        XCTAssertTrue(auth.registerCalls.isEmpty)
    }

    func testBackFromConsentsPreservesInput() async {
        let vm = makeVM(mode: .register)
        vm.email = "ana@example.org"
        vm.password = "senha-bem-longa-123"
        await vm.submit()
        vm.goBackToCredentials()
        XCTAssertEqual(vm.step, .credentials)
        XCTAssertEqual(vm.email, "ana@example.org")
        XCTAssertEqual(vm.password, "senha-bem-longa-123")
    }

    func testServerFieldErrorsAreMappedToFields() async {
        let auth = FakeAuthRepository()
        auth.registerResult = .failure(APIError.problem(
            Problem(code: "VALIDATION_FAILED", status: 400, errors: [FieldError(field: "password", code: "TOO_COMMON")]),
            httpStatus: 400))
        let vm = makeVM(mode: .register, auth: auth)
        vm.email = "ana@example.org"
        vm.password = "senha-bem-longa-123"
        await vm.submit()
        vm.acceptsLegal = true
        await vm.submit()
        XCTAssertEqual(vm.fieldErrors[.password]?.key, "validation.too_common")
        XCTAssertEqual(vm.banner?.key, "error.validation_failed")
    }

    func testSocialLoginCancelledIsSilent() async {
        let stub = StubSocialSignInProvider(provider: .apple, result: .failure(.cancelled))
        let vm = makeVM(mode: .login, social: [stub])
        await vm.signInWithSocial(.apple)
        XCTAssertNil(vm.banner)
        XCTAssertTrue(authenticated.isEmpty)
    }

    func testSocialProviderNotConfiguredShowsMessage() async {
        let stub = StubSocialSignInProvider(provider: .google)
        let vm = makeVM(mode: .login, social: [stub])
        await vm.signInWithSocial(.google)
        XCTAssertEqual(vm.banner?.key, "error.social_not_configured")
        XCTAssertEqual(vm.availableSocialProviders, [.google])
    }

    func testSocialLoginForNewAccountRequestsConsentThenRetries() async {
        let auth = FakeAuthRepository()
        auth.signInResults = [
            .failure(APIError.problem(Problem(code: "CONSENT_REQUIRED", status: 403), httpStatus: 403)),
            .success(Fixtures.user())
        ]
        let credential = SocialCredential(idToken: "t", nonce: "n", givenName: "Ana")
        let vm = makeVM(mode: .login, auth: auth, social: [StubSocialSignInProvider(provider: .apple, result: .success(credential))])

        await vm.signInWithSocial(.apple)
        XCTAssertTrue(vm.socialConsentRequired)
        XCTAssertTrue(authenticated.isEmpty)
        XCTAssertNil(auth.signInConsents[0], "primeira tentativa não envia consentimentos")

        await vm.confirmSocialConsent()
        XCTAssertEqual(vm.fieldErrors[.terms]?.key, "validation.terms_required")
        XCTAssertEqual(auth.signInConsents.count, 1)

        vm.acceptsLegal = true
        await vm.confirmSocialConsent()
        XCTAssertEqual(auth.signInConsents.count, 2)
        XCTAssertEqual(auth.signInConsents[1]?.count, 2)
        XCTAssertEqual(authenticated.count, 1)
        XCTAssertFalse(vm.socialConsentRequired)
    }

    func testPasswordResetRequestValidatesEmailAndConfirms() async {
        let vm = makeVM(mode: .login)
        vm.email = "x"
        await vm.requestPasswordReset()
        XCTAssertFalse(vm.passwordResetRequested)
        XCTAssertEqual(vm.fieldErrors[.email]?.key, "validation.email_invalid")
        vm.email = "ana@example.org"
        await vm.requestPasswordReset()
        XCTAssertTrue(vm.passwordResetRequested)
    }

    func testPasswordStrengthMeter() {
        XCTAssertEqual(Validators.passwordStrength(""), .empty)
        XCTAssertEqual(Validators.passwordStrength("abc"), .weak)
        XCTAssertEqual(Validators.passwordStrength("abcdefgh"), .fair)
        XCTAssertEqual(Validators.passwordStrength("Abcdefg1!"), .strong)
        XCTAssertTrue(Validators.isPlausibleEmail("ana@example.org"))
        XCTAssertFalse(Validators.isPlausibleEmail("ana@example"))
        XCTAssertFalse(Validators.isPlausibleEmail("ana @example.org"))
    }
}
