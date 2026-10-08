package app.nina.domain.tracking

import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.combine
import java.time.Duration
import java.time.Instant

/**
 * Implementação **stub** do [SyncEngine]: o motor de rede é da Onda 5. Não envia nada, não altera a fila e
 * responde honestamente [SyncRunResult.NotAvailable]; os registros continuam seguros no aparelho.
 */
class StubSyncEngine : SyncEngine {
    private val _status = MutableStateFlow<SyncStatus>(SyncStatus.Idle)
    override val status: StateFlow<SyncStatus> = _status.asStateFlow()
    override fun requestSync() = Unit
    override suspend fun syncNow(): SyncRunResult = SyncRunResult.NotAvailable
}

/** Backoff exponencial para reenvio (5 s, 10 s, 20 s... teto 15 min); respeita `Retry-After` quando houver. */
object RetryBackoff {
    private val BASE = Duration.ofSeconds(5)
    private val CAP = Duration.ofMinutes(15)

    /** [attemptsSoFar] = tentativas já feitas (0 = primeira falha). */
    fun delayFor(attemptsSoFar: Int, retryAfter: Duration? = null): Duration {
        val exp = BASE.multipliedBy(1L shl attemptsSoFar.coerceIn(0, 20))
        val computed = if (exp > CAP) CAP else exp
        return if (retryAfter != null && retryAfter > computed) retryAfter else computed
    }

    fun nextAttemptAt(now: Instant, attemptsSoFar: Int, retryAfter: Duration? = null): Instant =
        now.plus(delayFor(attemptsSoFar, retryAfter))
}

/** Combina conectividade, motor e fila no indicador único de sync (ux-spec 4.13). */
class SyncIndicatorSource(
    private val connectivity: ConnectivityMonitor,
    private val engine: SyncEngine,
    private val queue: MutationQueue,
) {
    val indicator: Flow<SyncIndicator> = combine(
        connectivity.isOnline,
        engine.status,
        queue.observePendingCount(),
        queue.observeRejectedCount(),
    ) { online, status, pending, rejected -> deriveSyncIndicator(online, status, pending, rejected) }
}
