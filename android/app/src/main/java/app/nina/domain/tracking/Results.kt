package app.nina.domain.tracking

import app.nina.domain.model.AppError

/** Motivo de recusa local (antes de gravar). */
sealed interface Rejection {
    /** Papel `READ_ONLY` não registra (INV-13, RF-008-A4). */
    data object ReadOnly : Rejection
    data object BabyNotFound : Rejection
    data object EventNotFound : Rejection

    /** Já existe um sono em andamento neste bebê; o usuário é levado a ele (RF-009-A3, INV-02). */
    data class OpenSleepExists(val sleepId: String) : Rejection

    /** O tipo do rascunho não corresponde ao do evento (ex.: trocar mamada por mamadeira exige excluir e recriar). */
    data object TypeMismatch : Rejection
}

sealed interface SaveResult<out T> {
    data class Saved<T>(val value: T) : SaveResult<T>
    data class Invalid(val issues: List<EventIssue>) : SaveResult<Nothing>
    data class Rejected(val reason: Rejection) : SaveResult<Nothing>
    data class Failed(val error: AppError) : SaveResult<Nothing>
}
