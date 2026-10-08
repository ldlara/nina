package app.nina.di

import android.content.Context
import android.os.Build
import androidx.room.Room
import app.nina.BuildConfig
import app.nina.data.local.NinaDatabase
import app.nina.data.remote.ApiTokenRefresher
import app.nina.data.remote.HeadersInterceptor
import app.nina.data.remote.NetworkFactory
import app.nina.data.remote.TokenAuthenticator
import app.nina.data.remote.dto.DeviceInfoDto
import app.nina.data.repository.DefaultAuthRepository
import app.nina.data.repository.DefaultBabyRepository
import app.nina.data.repository.DefaultCaregiverRepository
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
        Room.databaseBuilder(appContext, NinaDatabase::class.java, "nina.db").build()

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

    private val baseUrl = BuildConfig.API_BASE_URL.let { if (it.endsWith("/")) it else "$it/" }
    private val headers = HeadersInterceptor(sessionManager)

    // Cliente sem Authenticator, usado apenas para rotacionar o refresh token (evita recursão).
    private val refreshApi = NetworkFactory.api(baseUrl, NetworkFactory.okHttp(headers))
    private val api = NetworkFactory.api(
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

    /** Implementação stub; substituir pela real (Credential Manager / Sign in with Apple) em tarefa própria. */
    val socialAuthProvider: SocialAuthProvider = StubSocialAuthProvider()
}
