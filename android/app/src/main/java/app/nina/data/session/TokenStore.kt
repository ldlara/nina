package app.nina.data.session

import android.content.Context
import android.content.SharedPreferences
import androidx.core.content.edit
import androidx.security.crypto.EncryptedSharedPreferences
import androidx.security.crypto.MasterKey

/** Sessão persistida. Contém segredos: só pode ficar em armazenamento seguro (SEC-012). */
data class StoredSession(
    val accessToken: String,
    val refreshToken: String,
    val refreshExpiresAt: String,
    val userId: String,
    val email: String,
    val displayName: String?,
)

interface TokenStore {
    fun read(): StoredSession?
    fun write(session: StoredSession)
    fun clear()
}

/**
 * Tokens em [EncryptedSharedPreferences] (AES-256, chave no Android Keystore). Se o arquivo/chave estiver corrompido
 * (restauração, troca de lock screen em alguns aparelhos), descarta e recria: o usuário apenas entra de novo.
 */
class EncryptedTokenStore(private val context: Context) : TokenStore {
    private val prefs: SharedPreferences by lazy { openPrefs() }

    private fun openPrefs(): SharedPreferences = try {
        create()
    } catch (e: Exception) {
        context.deleteSharedPreferences(FILE)
        create()
    }

    private fun create(): SharedPreferences {
        val masterKey = MasterKey.Builder(context)
            .setKeyScheme(MasterKey.KeyScheme.AES256_GCM)
            .build()
        return EncryptedSharedPreferences.create(
            context,
            FILE,
            masterKey,
            EncryptedSharedPreferences.PrefKeyEncryptionScheme.AES256_SIV,
            EncryptedSharedPreferences.PrefValueEncryptionScheme.AES256_GCM,
        )
    }

    @Synchronized
    override fun read(): StoredSession? {
        val access = prefs.getString(K_ACCESS, null) ?: return null
        val refresh = prefs.getString(K_REFRESH, null) ?: return null
        return StoredSession(
            accessToken = access,
            refreshToken = refresh,
            refreshExpiresAt = prefs.getString(K_REFRESH_EXP, "").orEmpty(),
            userId = prefs.getString(K_USER_ID, "").orEmpty(),
            email = prefs.getString(K_EMAIL, "").orEmpty(),
            displayName = prefs.getString(K_NAME, null),
        )
    }

    @Synchronized
    override fun write(session: StoredSession) {
        prefs.edit {
            putString(K_ACCESS, session.accessToken)
            putString(K_REFRESH, session.refreshToken)
            putString(K_REFRESH_EXP, session.refreshExpiresAt)
            putString(K_USER_ID, session.userId)
            putString(K_EMAIL, session.email)
            putString(K_NAME, session.displayName)
        }
    }

    @Synchronized
    override fun clear() {
        prefs.edit { clear() }
    }

    private companion object {
        const val FILE = "nina_secure_session"
        const val K_ACCESS = "access_token"
        const val K_REFRESH = "refresh_token"
        const val K_REFRESH_EXP = "refresh_expires_at"
        const val K_USER_ID = "user_id"
        const val K_EMAIL = "email"
        const val K_NAME = "display_name"
    }
}
