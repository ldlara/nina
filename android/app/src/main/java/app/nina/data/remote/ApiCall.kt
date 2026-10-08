package app.nina.data.remote

import app.nina.data.remote.dto.ProblemDto
import app.nina.domain.model.AppError
import app.nina.domain.model.FieldError
import app.nina.domain.model.Outcome
import kotlinx.coroutines.CancellationException
import kotlinx.serialization.SerializationException
import retrofit2.HttpException
import java.io.IOException

/** Executa uma chamada Retrofit e converte exceções em [Outcome.Failure] (nunca lança, exceto cancelamento). */
suspend inline fun <T> apiCall(crossinline block: suspend () -> T): Outcome<T> =
    try {
        Outcome.Success(block())
    } catch (e: CancellationException) {
        throw e
    } catch (e: HttpException) {
        Outcome.Failure(e.toAppError())
    } catch (e: IOException) {
        Outcome.Failure(AppError.Network)
    } catch (e: SerializationException) {
        Outcome.Failure(AppError.Unexpected("Resposta fora do contrato"))
    } catch (e: IllegalArgumentException) {
        Outcome.Failure(AppError.Unexpected(e.message))
    }

fun HttpException.toAppError(): AppError.Api {
    val body = runCatching { response()?.errorBody()?.string() }.getOrNull()
    return parseProblem(code(), body, response()?.headers()?.get("Retry-After")?.toIntOrNull())
}

/** Interpreta um corpo `application/problem+json`; sem corpo válido, usa `HTTP_<status>` como code. */
fun parseProblem(httpStatus: Int, body: String?, retryAfterHeader: Int? = null): AppError.Api {
    val problem = body?.takeIf { it.isNotBlank() }?.let {
        runCatching { NinaJson.decodeFromString(ProblemDto.serializer(), it) }.getOrNull()
    }
    return AppError.Api(
        httpStatus = httpStatus,
        code = problem?.code ?: "HTTP_$httpStatus",
        requestId = problem?.requestId,
        fieldErrors = problem?.errors.orEmpty().map { FieldError(it.field, it.code) },
        retryAfterSeconds = problem?.retryAfterSeconds ?: retryAfterHeader,
    )
}
