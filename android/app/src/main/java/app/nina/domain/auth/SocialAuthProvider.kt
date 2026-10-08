package app.nina.domain.auth

import app.nina.domain.model.IdentityProvider

/** Credencial entregue pelo provedor; o servidor valida assinatura, `aud`, `iss`, expiração e `nonce` (ADR-0007). */
data class SocialCredential(
    val provider: IdentityProvider,
    val idToken: String,
    val nonce: String,
    val givenName: String? = null,
    val familyName: String? = null,
)

sealed interface SocialAuthResult {
    data class Success(val credential: SocialCredential) : SocialAuthResult
    /** Provedor não configurado/implementado neste build. */
    data object Unavailable : SocialAuthResult
    data object Cancelled : SocialAuthResult
}

/**
 * Fronteira para "Entrar com Google/Apple". A UI e o ViewModel dependem só desta interface; a implementação real
 * (Credential Manager / Sign in with Apple via web) entra em outra tarefa. [StubSocialAuthProvider] é o padrão.
 */
interface SocialAuthProvider {
    suspend fun authenticate(provider: IdentityProvider, nonce: String): SocialAuthResult
}

/** Stub: nunca devolve token. Não fabrica credenciais falsas que o servidor teria de rejeitar. */
class StubSocialAuthProvider : SocialAuthProvider {
    override suspend fun authenticate(provider: IdentityProvider, nonce: String): SocialAuthResult =
        SocialAuthResult.Unavailable
}
