import Foundation
import SwiftData
import NinaCore

/// Raiz de composição: cria cliente HTTP, repositórios e ViewModels de vida longa.
@MainActor
final class AppContainer {
    let config: AppConfig
    let session: AppSessionViewModel
    let babies: BabiesViewModel
    let authRepository: AuthRepository
    let consentRepository: ConsentRepository
    let babyRepository: BabyRepository
    let caregiverRepository: CaregiverRepository
    let socialProviders: [SocialSignInProvider]
    let modelContainer: ModelContainer

    init(config: AppConfig = .load(), tokenStore: TokenStore = KeychainTokenStore(),
         transport: HTTPTransport = URLSessionTransport(), inMemoryStore: Bool = false) {
        self.config = config
        let deviceProvider = StaticDeviceInfoProvider(info: AppDeviceInfo.make())

        let relay = SessionEndRelay()
        let client = APIClient(baseURL: config.apiBaseURL, transport: transport, tokenStore: tokenStore,
                               deviceProvider: deviceProvider,
                               onSessionEnded: { reason in
                                   Task { @MainActor in relay.handler?(reason) }
                               })

        modelContainer = LocalStore.makeContainer(inMemory: inMemoryStore)
        let cache = SwiftDataBabyCache(modelContainer: modelContainer)

        authRepository = DefaultAuthRepository(client: client, tokenStore: tokenStore, deviceProvider: deviceProvider)
        consentRepository = DefaultConsentRepository(client: client)
        babyRepository = DefaultBabyRepository(client: client, cache: cache)
        caregiverRepository = DefaultCaregiverRepository(client: client)

        var providers: [SocialSignInProvider] = [AppleSignInProvider()]
        if config.enableStubGoogle {
            // SDK do Google ainda não integrado (ver README). Em DEBUG o botão aparece e informa "não configurado".
            providers.append(StubSocialSignInProvider(provider: .google))
        }
        socialProviders = providers

        session = AppSessionViewModel(auth: authRepository, babies: babyRepository,
                                      preferences: UserDefaultsPreferenceStore())
        babies = BabiesViewModel(repository: babyRepository)
        relay.handler = { [weak session] reason in session?.sessionEnded(reason) }
    }
}

/// Ponte entre o callback `@Sendable` do cliente HTTP e a main actor (criada antes do ViewModel de sessão).
final class SessionEndRelay: @unchecked Sendable {
    @MainActor var handler: (@MainActor (SessionEndReason) -> Void)?
}
