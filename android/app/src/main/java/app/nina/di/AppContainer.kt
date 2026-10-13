package app.nina.di

import android.content.Context
import android.os.Build
import androidx.room.Room
import app.nina.BuildConfig
import app.nina.data.local.MIGRATION_1_2
import app.nina.data.local.NinaDatabase
import app.nina.data.remote.ApiTokenRefresher
import app.nina.data.remote.HeadersInterceptor
import app.nina.data.remote.NetworkFactory
import app.nina.data.remote.NinaApi
import app.nina.data.remote.TokenAuthenticator
import app.nina.data.remote.dto.DeviceInfoDto
import app.nina.data.repository.DefaultAuthRepository
import app.nina.data.repository.DefaultBabyRepository
import app.nina.data.repository.DefaultCaregiverRepository
import app.nina.data.repository.DefaultTrackingRepository
import app.nina.data.repository.RoomMutationQueue
import app.nina.data.sync.AndroidConnectivityMonitor
import app.nina.data.repository.Environment
import app.nina.data.session.AppPrefs
import app.nina.data.session.EncryptedTokenStore
import app.nina.data.session.SessionManager
import app.nina.data.session.SharedAppPrefs
import app.nina.domain.auth.SocialAuthProvider
import app.nina.domain.auth.StubSocialAuthProvider
import app.nina.domain.model.Platform
import app.nina.domain.repository.AuthRepository
import app.nina.domain.repository.BabyRepository
import app.nina.domain.repository.CaregiverRepository
import app.nina.domain.tracking.ConnectivityMonitor
import app.nina.domain.tracking.EventValidator
import app.nina.domain.tracking.MutationQueue
import app.nina.domain.tracking.StubSyncEngine
import app.nina.domain.tracking.SyncEngine
import app.nina.domain.tracking.SyncIndicatorSource
import app.nina.domain.tracking.TrackingLimits
import app.nina.domain.tracking.TrackingRepository
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.launch
import java.time.Clock
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import java.time.ZoneId
import java.util.Locale

/** Injeção manual de dependências (suficiente para o escopo atual; sem framework de DI). */
class AppContainer(context: Context) {
    private val appContext = context.applicationContext

    val appPrefs: AppPrefs = SharedAppPrefs(appContext)
    private val sessionManager = SessionManager(EncryptedTokenStore(appContext))

    private val database: NinaDatabase =
        Room.databaseBuilder(appContext, NinaDatabase::class.java, "nina.db")
            .addMigrations(MIGRATION_1_2)
            .build()

    private val environment = Environment(
        deviceInfo = {
            DeviceInfoDto(
                deviceId = appPrefs.deviceId,
                platform = Platform.ANDROID,
                deviceLabel = Build.MODEL?.take(80),
                appVersion = BuildConfig.VERSION_NAME,
                osVersion = Build.VERSION.RELEASE,
            )
        },
        localeTag = { Locale.getDefault().toLanguageTag() },
        timezoneId = { ZoneId.systemDefault().id },
    )

    private val baseUrl: String = BuildConfig.API_BASE_URL.let { if (it.endsWith("/")) it else "$it/" }
    private val headers: HeadersInterceptor = HeadersInterceptor(sessionManager)

    // Cliente sem Authenticator, usado apenas para rotacionar o refresh token (evita recursão).
    private val refreshApi: NinaApi = NetworkFactory.api(baseUrl, NetworkFactory.okHttp(headers))
    private val api: NinaApi = NetworkFactory.api(
        baseUrl,
        NetworkFactory.okHttp(
            headers,
            TokenAuthenticator(sessionManager, ApiTokenRefresher(refreshApi, sessionManager) { appPrefs.deviceId }),
        ),
    )

    val authRepository: AuthRepository = DefaultAuthRepository(api, sessionManager, environment) {
        withContext(Dispatchers.IO) { database.clearAllTables() }
    }
    val babyRepository: BabyRepository =
        DefaultBabyRepository(api, database.babyDao(), database.membershipDao())
    val caregiverRepository: CaregiverRepository =
        DefaultCaregiverRepository(api, database.membershipDao(), database.babyDao())

    // ---- Tracking offline-first (AND-002) ----
    val clock: Clock = Clock.systemUTC()
    val trackingLimits = TrackingLimits()
    private val appScope = CoroutineScope(SupervisorJob() + Dispatchers.IO)

    /** Motor de sync: STUB até a Onda 5 (não envia nada; a fila já acumula as mutações). */
    val syncEngine: SyncEngine = StubSyncEngine()
    val connectivity: ConnectivityMonitor = AndroidConnectivityMonitor(appContext)
    val mutationQueue: MutationQueue = RoomMutationQueue(database, database.mutationDao(), database.trackingDao())
    val trackingRepository: TrackingRepository = DefaultTrackingRepository(
        database = database,
        dao = database.trackingDao(),
        queue = database.mutationDao(),
        babies = database.babyDao(),
        clock = clock,
        deviceId = { appPrefs.deviceId },
        validator = EventValidator(clock, trackingLimits),
        onLocalWrite = syncEngine::requestSync,
    )
    val syncIndicator = SyncIndicatorSource(connectivity, syncEngine, mutationQueue)

    init {
        // Reinício do processo: o que estava "em voo" volta para a fila (RF-046-A4/A5).
        appScope.launch { mutationQueue.releaseInFlight() }
    }

    /** Implementação stub; substituir pela real (Credential Manager / Sign in with Apple) em tarefa própria. */
    val socialAuthProvider: SocialAuthProvider = StubSocialAuthProvider()
}
