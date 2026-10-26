package app.nina.data.session

import app.nina.domain.model.SessionState
import app.nina.domain.model.SessionUser
import app.nina.domain.model.SignedOutReason
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow

/** Fonte única do estado de sessão; guarda o par de tokens no [TokenStore] e um cache em memória do access token. */
class SessionManager(private val store: TokenStore) {
    @Volatile private var cached: StoredSession? = store.read()

    private val _state = MutableStateFlow<SessionState>(cached.toState())
    val state: StateFlow<SessionState> = _state.asStateFlow()

    fun accessToken(): String? = cached?.accessToken
    fun current(): StoredSession? = cached

    @Synchronized
    fun save(session: StoredSession) {
        store.write(session)
        cached = session
        _state.value = session.toState()
    }

    /** Logout voluntário. */
    @Synchronized
    fun clear() {
        store.clear()
        cached = null
        _state.value = SessionState.SignedOut(SignedOutReason.NONE)
    }

    /** Sessão revogada/expirada pelo servidor: remove tokens mas mantém dados locais (RF-001-A4). */
    @Synchronized
    fun expire() {
        store.clear()
        cached = null
        _state.value = SessionState.SignedOut(SignedOutReason.EXPIRED)
    }

    private fun StoredSession?.toState(): SessionState =
        if (this == null) SessionState.SignedOut(SignedOutReason.NONE)
        else SessionState.SignedIn(SessionUser(userId, email, displayName))
}
