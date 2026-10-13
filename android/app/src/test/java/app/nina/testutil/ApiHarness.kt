package app.nina.testutil

import app.nina.data.remote.ApiTokenRefresher
import app.nina.data.remote.HeadersInterceptor
import app.nina.data.remote.NetworkFactory
import app.nina.data.remote.NinaApi
import app.nina.data.remote.TokenAuthenticator
import app.nina.data.remote.dto.DeviceInfoDto
import app.nina.data.repository.DefaultAuthRepository
import app.nina.data.repository.DefaultBabyRepository
import app.nina.data.repository.DefaultCaregiverRepository
import app.nina.data.repository.Environment
import app.nina.data.session.SessionManager
import app.nina.data.session.StoredSession
import app.nina.domain.model.Platform
import okhttp3.mockwebserver.MockResponse
import okhttp3.mockwebserver.MockWebServer
import java.util.Locale
import java.util.concurrent.atomic.AtomicInteger

/**
 * Monta a pilha real (OkHttp + Retrofit + kotlinx.serialization + repositórios) sobre um [MockWebServer].
 * Nenhuma chamada de rede real: o servidor fake responde na máquina local.
 */
class ApiHarness(signedIn: Boolean = false) {
    val server = MockWebServer().apply { start() }
    val tokenStore = FakeTokenStore(
        if (signedIn) StoredSession("access-old", "refresh-old", "2026-11-07T18:00:00Z", "u-1", "ana@example.org", "Ana") else null,
    )
    val session = SessionManager(tokenStore)
    val babyDao = FakeBabyDao()
    val membershipDao = FakeMembershipDao()
    var clearedLocalData = 0

    private val ids = AtomicInteger()
    val environment = Environment(
        deviceInfo = { DeviceInfoDto("dev-1", Platform.ANDROID, "Pixel", "0.1.0", "15") },
        localeTag = { "pt-BR" },
        timezoneId = { "America/Sao_Paulo" },
        newId = { "id-${ids.incrementAndGet()}" },
    )

    private val baseUrl = server.url("/v1/").toString()
    private val headers = HeadersInterceptor(session) { Locale.forLanguageTag("pt-BR") }
    private val refreshApi: NinaApi = NetworkFactory.api(baseUrl, NetworkFactory.okHttp(headers))
    val api: NinaApi = NetworkFactory.api(
        baseUrl,
        NetworkFactory.okHttp(headers, TokenAuthenticator(session, ApiTokenRefresher(refreshApi, session) { "dev-1" })),
    )

    val auth = DefaultAuthRepository(api, session, environment) { clearedLocalData++ }
    val babies = DefaultBabyRepository(api, babyDao, membershipDao) { "id-${ids.incrementAndGet()}" }
    val caregivers = DefaultCaregiverRepository(api, membershipDao, babyDao) { "id-${ids.incrementAndGet()}" }

    fun enqueue(status: Int, body: String = "", contentType: String = "application/json") {
        server.enqueue(MockResponse().setResponseCode(status).setHeader("Content-Type", contentType).setBody(body))
    }

    fun enqueueProblem(status: Int, code: String, errors: String = "[]") =
        enqueue(status, Samples.problem(status, code, errors), "application/problem+json")

    fun shutdown() = server.shutdown()
}
