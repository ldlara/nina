package app.nina.data.session

import android.content.Context
import androidx.core.content.edit
import java.util.UUID

/** Preferências não sensíveis (onboarding visto, id estável da instalação). Nada de token aqui. */
interface AppPrefs {
    var onboardingDone: Boolean
    val deviceId: String
}

class SharedAppPrefs(context: Context) : AppPrefs {
    private val prefs = context.getSharedPreferences("nina_prefs", Context.MODE_PRIVATE)

    override var onboardingDone: Boolean
        get() = prefs.getBoolean("onboarding_done", false)
        set(value) = prefs.edit { putBoolean("onboarding_done", value) }

    override val deviceId: String
        get() = prefs.getString("device_id", null) ?: UUID.randomUUID().toString().also {
            prefs.edit { putString("device_id", it) }
        }
}
