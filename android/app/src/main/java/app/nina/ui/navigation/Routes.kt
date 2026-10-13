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
    const val TODAY = "baby/{babyId}/today"
    const val TIMELINE = "baby/{babyId}/timeline"
    const val EVENT_NEW = "baby/{babyId}/log/{kind}"
    const val EVENT_EDIT = "baby/{babyId}/events/{eventId}"

    fun verify(email: String, resendAfterSeconds: Int?) = "verify/${Uri.encode(email)}?resend=${resendAfterSeconds ?: -1}"
    fun baby(id: String) = "baby/${Uri.encode(id)}"
    fun caregivers(id: String) = "baby/${Uri.encode(id)}/caregivers"
    fun today(id: String) = "baby/${Uri.encode(id)}/today"
    fun timeline(id: String) = "baby/${Uri.encode(id)}/timeline"
    fun eventNew(babyId: String, kind: String) = "baby/${Uri.encode(babyId)}/log/${Uri.encode(kind)}"
    fun eventEdit(babyId: String, eventId: String) = "baby/${Uri.encode(babyId)}/events/${Uri.encode(eventId)}"

    /** Rotas acessíveis sem sessão. */
    fun isPublic(route: String?): Boolean =
        route == ONBOARDING || route == AUTH || route == VERIFY
}
