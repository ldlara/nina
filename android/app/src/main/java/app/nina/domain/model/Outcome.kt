package app.nina.domain.model

/** Erro de domínio. A mensagem exibida é localizada pela UI a partir de [code] (RF-050-A5), nunca de `title/detail`. */
sealed interface AppError {
    /** Erro RFC 7807 do servidor. */
    data class Api(
        val httpStatus: Int,
        val code: String,
        val requestId: String? = null,
        val fieldErrors: List<FieldError> = emptyList(),
        val retryAfterSeconds: Int? = null,
    ) : AppError

    /** Sem rede / timeout / TLS. */
    data object Network : AppError

    /** Resposta inesperada ou falha local. */
    data class Unexpected(val message: String? = null) : AppError

    /** Provedor social indisponível ou cancelado pelo usuário. */
    data object SocialUnavailable : AppError
}

data class FieldError(val field: String, val code: String)

val AppError.apiCode: String? get() = (this as? AppError.Api)?.code

sealed interface Outcome<out T> {
    data class Success<T>(val value: T) : Outcome<T>
    data class Failure(val error: AppError) : Outcome<Nothing>
}

inline fun <T, R> Outcome<T>.map(transform: (T) -> R): Outcome<R> = when (this) {
    is Outcome.Success -> Outcome.Success(transform(value))
    is Outcome.Failure -> this
}

fun <T> Outcome<T>.getOrNull(): T? = (this as? Outcome.Success)?.value
