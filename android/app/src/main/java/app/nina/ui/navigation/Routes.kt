package app.nina.ui.navigation

import android.net.Uri

/** Rotas do app. Argumentos de texto são codificados com [Uri.encode]. */
object Routes {
    const val ONBOARDING = "onboarding"
    const val AUTH = "auth"
    const val VERIFY = "verify/{email}?resend={resend}"
    const val BABIES = "babies"
    const val BABY_NEW = "baby/new"
    const val BABY = "baby/{babyId}"
    const val CAREGIVERS = "baby/{babyId}/caregivers"
    const val ACCEPT_INVITE = "invite/accept"

    fun verify(email: String, resendAfterSeconds: Int?) = "verify/${Uri.encode(email)}?resend=${resendAfterSeconds ?: -1}"
    fun baby(id: String) = "baby/${Uri.encode(id)}"
    fun caregivers(id: String) = "baby/${Uri.encode(id)}/caregivers"

    /** Rotas acessíveis sem sessão. */
    fun isPublic(route: String?): Boolean =
        route == ONBOARDING || route == AUTH || route == VERIFY
}
