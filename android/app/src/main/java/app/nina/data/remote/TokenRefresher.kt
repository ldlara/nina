package app.nina.data.remote

import app.nina.data.remote.dto.RefreshRequestDto
import app.nina.data.session.SessionManager
import app.nina.data.session.StoredSession
import kotlinx.coroutines.runBlocking
import retrofit2.HttpException
import java.io.IOException

enum class RefreshResult { Refreshed, Rejected, TransientFailure }

fun interface TokenRefresher {
    /** Bloqueante (chamada pelo Authenticator do OkHttp, fora da main thread). */
    fun refresh(): RefreshResult
}

/** Usa um [NinaApi] sem Authenticator para evitar recursão. Persiste o novo par de tokens (rotativo). */
class ApiTokenRefresher(
    private val refreshApi: NinaApi,
    private val sessionManager: SessionManager,
    private val deviceId: () -> String,
) : TokenRefresher {
    override fun refresh(): RefreshResult {
        val stored = sessionManager.current() ?: return RefreshResult.Rejected
        return try {
            val tokens = runBlocking {
                refreshApi.refresh(RefreshRequestDto(stored.refreshToken, deviceId()))
            }
            sessionManager.save(
                StoredSession(
                    accessToken = tokens.accessToken,
                    refreshToken = tokens.refreshToken,
                    refreshExpiresAt = tokens.refreshExpiresAt,
                    userId = tokens.user.id,
                    email = tokens.user.email,
                    displayName = tokens.user.displayName,
                ),
            )
            RefreshResult.Refreshed
        } catch (e: HttpException) {
            // 401 (TOKEN/SESSION_REVOKED/REFRESH_TOKEN_REUSED) ou 400: sessão perdida. 429/5xx: tentar depois.
            if (e.code() == 401 || e.code() == 400) RefreshResult.Rejected else RefreshResult.TransientFailure
        } catch (e: IOException) {
            RefreshResult.TransientFailure
        }
    }
}
