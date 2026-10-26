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
    // IOS-002: tracking offline-first
    let trackingStore: TrackingStore
    let eventRepository: EventRepository
    let syncEngine: SyncEngine
    let feedingTimerStore: FeedingTimerStore
    let network: NetworkMonitor
    private let actorBox: CurrentActorBox
    private var trackingModels: [String: TrackingViewModel] = [:]

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

        // Banco local de eventos + fila de mutações (SwiftData atrás de `TrackingStore`). O motor de rede é um stub
        // (Onda 5): os registros ficam "aguardando envio".
        let trackingStore = SwiftDataTrackingStore(modelContainer: modelContainer)
        self.trackingStore = trackingStore
        let actorBox = CurrentActorBox()
        self.actorBox = actorBox
        eventRepository = DefaultEventRepository(store: trackingStore, deviceId: deviceProvider.info.deviceId,
                                                 author: { actorBox.current })
        syncEngine = StubSyncEngine()
        feedingTimerStore = UserDefaultsFeedingTimerStore()
        network = NetworkMonitor()

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
                                      preferences: UserDefaultsPreferenceStore(),
                                      localCleaners: [eventRepository])
        babies = BabiesViewModel(repository: babyRepository)
        relay.handler = { [weak session] reason in session?.sessionEnded(reason) }

        // Mutações que ficaram "em voo" por queda do processo voltam à fila (o reenvio é idempotente por mutation_id).
        Task { try? await trackingStore.recoverInFlight() }
    }

    /// Autor das próximas escritas locais; chamado quando o usuário da sessão muda.
    func updateCurrentUser(_ user: User?) {
        actorBox.set(user)
    }

    /// ViewModel de tracking do bebê, compartilhado por Hoje e Linha do tempo. É recriado quando mudam papel,
    /// fuso ou versão do bebê (o contexto de escrita é imutável).
    func trackingViewModel(for baby: Baby) -> TrackingViewModel {
        let key = [baby.id.uuidString, String(baby.version), baby.timezone, baby.myRole.rawValue].joined(separator: "|")
        if let existing = trackingModels[key] { return existing }
        let model = TrackingViewModel(baby: baby, repository: eventRepository, sync: syncEngine)
        trackingModels = trackingModels.filter { !$0.key.hasPrefix(baby.id.uuidString) }
        trackingModels[key] = model
        return model
    }
}

/// Ponte entre o callback `@Sendable` do cliente HTTP e a main actor (criada antes do ViewModel de sessão).
final class SessionEndRelay: @unchecked Sendable {
    @MainActor var handler: (@MainActor (SessionEndReason) -> Void)?
}
