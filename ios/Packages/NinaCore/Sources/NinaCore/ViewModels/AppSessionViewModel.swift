import Foundation
import Observation

public enum AuthMode: Equatable, Sendable {
    case register, login
}

/// Estado de uma verificação de e-mail em andamento. O pedido de cadastro fica só em memória (nunca em disco)
/// para permitir "reenviar código": o contrato não tem endpoint de reenvio, então o reenvio repete o `register`
/// (resposta uniforme 202).
public struct PendingVerification: Equatable, Sendable {
    public var email: String
    public var resendRequest: RegisterRequest?
    public var resendAfterSeconds: Int

    public init(email: String, resendRequest: RegisterRequest?, resendAfterSeconds: Int = 60) {
        self.email = email
        self.resendRequest = resendRequest
        self.resendAfterSeconds = resendAfterSeconds
    }
}

public enum AppRoute: Equatable, Sendable {
    case onboarding
    case auth(AuthMode)
    case verifyEmail(PendingVerification)
    case main
}

/// Decide a raiz do app (onboarding, autenticação, verificação de e-mail, conteúdo).
@Observable
@MainActor
public final class AppSessionViewModel {
    public static let onboardingCompletedKey = "onboarding.completed"

    public private(set) var route: AppRoute
    /// Aviso mostrado na tela de login depois de uma sessão encerrada pelo servidor.
    public private(set) var notice: UserMessage?
    public private(set) var currentUser: User?

    @ObservationIgnored private let auth: AuthRepository
    @ObservationIgnored private let babies: BabyRepository
    @ObservationIgnored private let preferences: PreferenceStore

    public init(auth: AuthRepository, babies: BabyRepository, preferences: PreferenceStore) {
        self.auth = auth
        self.babies = babies
        self.preferences = preferences
        if auth.hasStoredSession() {
            route = .main
        } else if preferences.bool(forKey: Self.onboardingCompletedKey) {
            route = .auth(.login)
        } else {
            route = .onboarding
        }
    }

    /// Com sessão guardada, busca o usuário. Falha de rede não derruba a sessão (uso offline).
    public func start() async {
        guard route == .main else { return }
        do {
            currentUser = try await auth.currentUser()
        } catch {
            // Offline ou erro transitório: continua com a sessão local. 401 terminal chega por `sessionEnded`.
        }
    }

    public func finishOnboarding(hasAccount: Bool) {
        preferences.set(true, forKey: Self.onboardingCompletedKey)
        route = .auth(hasAccount ? .login : .register)
    }

    public func switchAuthMode(_ mode: AuthMode) {
        notice = nil
        route = .auth(mode)
    }

    public func beginEmailVerification(_ pending: PendingVerification) {
        route = .verifyEmail(pending)
    }

    public func backToRegistration() {
        route = .auth(.register)
    }

    public func didAuthenticate(_ user: User) {
        currentUser = user
        notice = nil
        route = .main
    }

    /// Chamado quando o cliente HTTP encerra a sessão (revogada). Dados locais e fila pendente ficam.
    public func sessionEnded(_ reason: SessionEndReason) {
        currentUser = nil
        if reason == .revoked { notice = UserMessage("error.session_revoked") }
        route = .auth(.login)
    }

    public func logout() async {
        await auth.logout()
        // Dados do bebê em cache saem junto com a sessão (privacidade em aparelho compartilhado).
        await babies.clearLocalData()
        currentUser = nil
        notice = nil
        route = .auth(.login)
    }
}
