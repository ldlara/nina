import XCTest
@testable import NinaCore

@MainActor
final class SessionViewModelTests: XCTestCase {
    func testFirstLaunchShowsOnboardingThenAuth() {
        let prefs = InMemoryPreferenceStore()
        let vm = AppSessionViewModel(auth: FakeAuthRepository(), babies: FakeBabyRepository(), preferences: prefs)
        XCTAssertEqual(vm.route, .onboarding)
        vm.finishOnboarding(hasAccount: false)
        XCTAssertEqual(vm.route, .auth(.register))
        XCTAssertTrue(prefs.bool(forKey: AppSessionViewModel.onboardingCompletedKey))

        let again = AppSessionViewModel(auth: FakeAuthRepository(), babies: FakeBabyRepository(), preferences: prefs)
        XCTAssertEqual(again.route, .auth(.login), "onboarding só aparece uma vez")
    }

    func testHasAccountGoesToLogin() {
        let vm = AppSessionViewModel(auth: FakeAuthRepository(), babies: FakeBabyRepository(), preferences: InMemoryPreferenceStore())
        vm.finishOnboarding(hasAccount: true)
        XCTAssertEqual(vm.route, .auth(.login))
    }

    func testStoredSessionGoesStraightToMain() {
        let auth = FakeAuthRepository()
        auth.stored = true
        let vm = AppSessionViewModel(auth: auth, babies: FakeBabyRepository(), preferences: InMemoryPreferenceStore())
        XCTAssertEqual(vm.route, .main)
    }

    func testRevokedSessionReturnsToLoginWithNotice() {
        let auth = FakeAuthRepository()
        auth.stored = true
        let vm = AppSessionViewModel(auth: auth, babies: FakeBabyRepository(), preferences: InMemoryPreferenceStore())
        vm.sessionEnded(.revoked)
        XCTAssertEqual(vm.route, .auth(.login))
        XCTAssertEqual(vm.notice?.key, "error.session_revoked")
    }

    func testRegistrationLeadsToVerificationThenMain() {
        let vm = AppSessionViewModel(auth: FakeAuthRepository(), babies: FakeBabyRepository(), preferences: InMemoryPreferenceStore())
        let pending = PendingVerification(email: "ana@example.org", resendRequest: nil)
        vm.beginEmailVerification(pending)
        XCTAssertEqual(vm.route, .verifyEmail(pending))
        vm.didAuthenticate(Fixtures.user())
        XCTAssertEqual(vm.route, .main)
        XCTAssertEqual(vm.currentUser?.email, "ana@example.org")
    }

    func testLogoutClearsSessionAndLocalBabyData() async {
        let auth = FakeAuthRepository()
        auth.stored = true
        let babies = FakeBabyRepository()
        let vm = AppSessionViewModel(auth: auth, babies: babies, preferences: InMemoryPreferenceStore())
        await vm.logout()
        XCTAssertTrue(auth.loggedOut)
        XCTAssertTrue(babies.cleared)
        XCTAssertEqual(vm.route, .auth(.login))
    }

    // MARK: - Verificação de e-mail

    func testVerifyEmailAuthenticates() async {
        let auth = FakeAuthRepository()
        var users: [User] = []
        let vm = EmailVerificationViewModel(pending: PendingVerification(email: "ana@example.org", resendRequest: nil),
                                            auth: auth, onAuthenticated: { users.append($0) })
        vm.code = "12 34 56"
        XCTAssertEqual(vm.sanitizedCode, "123456")
        XCTAssertTrue(vm.canSubmit)
        await vm.verify()
        XCTAssertEqual(auth.verifyCalls.first?.1, "123456")
        XCTAssertEqual(users.count, 1)
    }

    func testVerifyEmailWrongCodeShowsMessage() async {
        let auth = FakeAuthRepository()
        auth.verifyResult = .failure(APIError.problem(Problem(code: "VALIDATION_FAILED", status: 400), httpStatus: 400))
        let vm = EmailVerificationViewModel(pending: PendingVerification(email: "a@b.co", resendRequest: nil),
                                            auth: auth, onAuthenticated: { _ in })
        vm.code = "000000"
        await vm.verify()
        XCTAssertEqual(vm.message?.key, "error.validation_failed")
    }

    func testResendRespectsCountdown() async {
        let auth = FakeAuthRepository()
        let request = RegisterRequest(email: "a@b.co", password: "p", displayName: nil, locale: "pt-BR", timezone: nil, consents: [])
        let start = Date(timeIntervalSince1970: 1_790_000_000)
        let clock = MutableClock(start)
        let vm = EmailVerificationViewModel(pending: PendingVerification(email: "a@b.co", resendRequest: request, resendAfterSeconds: 60),
                                            auth: auth, now: { clock.current }, onAuthenticated: { _ in })
        XCTAssertFalse(vm.canResend(at: start))
        XCTAssertEqual(vm.secondsUntilResend(at: start.addingTimeInterval(10)), 50)
        await vm.resend()
        XCTAssertTrue(auth.registerCalls.isEmpty, "ainda em contagem regressiva")
        clock.current = start.addingTimeInterval(61)
        XCTAssertTrue(vm.canResend(at: clock.current))
        await vm.resend()
        XCTAssertEqual(auth.registerCalls.count, 1)
        XCTAssertTrue(vm.didResend)
        XCTAssertFalse(vm.canResend(at: clock.current), "nova contagem após reenviar")
    }

    func testResendUnavailableWithoutRequest() {
        let vm = EmailVerificationViewModel(pending: PendingVerification(email: "a@b.co", resendRequest: nil, resendAfterSeconds: 0),
                                            auth: FakeAuthRepository(), onAuthenticated: { _ in })
        XCTAssertFalse(vm.canResend(at: Date().addingTimeInterval(1000)))
    }
}

final class MutableClock: @unchecked Sendable {
    var current: Date
    init(_ date: Date) { current = date }
}
