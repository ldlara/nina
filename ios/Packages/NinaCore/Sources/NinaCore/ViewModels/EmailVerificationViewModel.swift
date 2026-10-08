import Foundation
import Observation

@Observable
@MainActor
public final class EmailVerificationViewModel {
    public var code = ""
    public private(set) var isBusy = false
    public private(set) var message: UserMessage?
    public private(set) var resendAvailableAt: Date
    public private(set) var didResend = false

    public let email: String

    @ObservationIgnored private let pending: PendingVerification
    @ObservationIgnored private let auth: AuthRepository
    @ObservationIgnored private let now: @Sendable () -> Date
    @ObservationIgnored private let onAuthenticated: @MainActor (User) -> Void

    public init(pending: PendingVerification, auth: AuthRepository,
                now: @escaping @Sendable () -> Date = { Date() },
                onAuthenticated: @escaping @MainActor (User) -> Void) {
        self.pending = pending
        self.email = pending.email
        self.auth = auth
        self.now = now
        self.resendAvailableAt = now().addingTimeInterval(TimeInterval(pending.resendAfterSeconds))
        self.onAuthenticated = onAuthenticated
    }

    /// Código aceito sem espaços; o formato exato é definido pelo servidor (validação final lá).
    public var sanitizedCode: String { code.filter { !$0.isWhitespace } }
    public var canSubmit: Bool { sanitizedCode.count >= 4 && !isBusy }
    public var canResendRequest: Bool { pending.resendRequest != nil }

    public func canResend(at date: Date) -> Bool {
        canResendRequest && !isBusy && date >= resendAvailableAt
    }

    public func secondsUntilResend(at date: Date) -> Int {
        max(0, Int(resendAvailableAt.timeIntervalSince(date).rounded(.up)))
    }

    public func verify() async {
        guard canSubmit else { return }
        isBusy = true
        message = nil
        defer { isBusy = false }
        do {
            let user = try await auth.verifyEmail(email: email, code: sanitizedCode)
            onAuthenticated(user)
        } catch {
            message = ErrorMapper.message(for: error)
        }
    }

    public func resend() async {
        guard let request = pending.resendRequest, canResend(at: now()) else { return }
        isBusy = true
        message = nil
        defer { isBusy = false }
        do {
            let result = try await auth.register(request)
            resendAvailableAt = now().addingTimeInterval(TimeInterval(result.resendAfterSeconds ?? 60))
            didResend = true
        } catch {
            message = ErrorMapper.message(for: error)
        }
    }
}
