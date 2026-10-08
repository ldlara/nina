package app.nina.data.remote

import app.nina.data.session.SessionManager
import okhttp3.Authenticator
import okhttp3.Interceptor
import okhttp3.Request
import okhttp3.Response
import okhttp3.Route
import java.util.Locale

/** Adiciona `Authorization: Bearer` (exceto endpoints marcados como públicos) e `Accept-Language`. */
class HeadersInterceptor(
    private val sessionManager: SessionManager,
    private val localeProvider: () -> Locale = { Locale.getDefault() },
) : Interceptor {
    override fun intercept(chain: Interceptor.Chain): Response {
        val original = chain.request()
        val isPublic = original.header(NO_AUTH_HEADER) != null
        val builder = original.newBuilder().removeHeader(NO_AUTH_HEADER)
        builder.header("Accept-Language", localeProvider().toLanguageTag())
        if (!isPublic) {
            sessionManager.accessToken()?.let { builder.header("Authorization", "Bearer $it") }
        }
        return chain.proceed(builder.build())
    }
}

/**
 * Em 401, tenta uma única rotação do refresh token (uso único, AD-02) e repete a requisição. Se o refresh for
 * recusado (sessão revogada, token reutilizado), encerra a sessão local sem apagar dados já salvos (RF-001-A4).
 */
class TokenAuthenticator(
    private val sessionManager: SessionManager,
    private val refresher: TokenRefresher,
) : Authenticator {
    override fun authenticate(route: Route?, response: Response): Request? {
        val sentToken = response.request.header("Authorization")?.removePrefix("Bearer ")?.trim()
            ?: return null // requisição pública: 401 é resposta final (ex.: credenciais inválidas)
        if (responseCount(response) >= 2) return null
        return synchronized(sessionManager) {
            val current = sessionManager.accessToken()
            when {
                current == null -> null
                current != sentToken -> response.request.withToken(current) // outra thread já renovou
                else -> when (refresher.refresh()) {
                    RefreshResult.Refreshed -> sessionManager.accessToken()?.let { response.request.withToken(it) }
                    RefreshResult.Rejected -> {
                        sessionManager.expire()
                        null
                    }
                    RefreshResult.TransientFailure -> null
                }
            }
        }
    }

    private fun Request.withToken(token: String): Request =
        newBuilder().header("Authorization", "Bearer $token").build()

    private fun responseCount(response: Response): Int {
        var count = 1
        var prior = response.priorResponse
        while (prior != null) {
            count++
            prior = prior.priorResponse
        }
        return count
    }
}
