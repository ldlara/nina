package app.nina.ui.babies

import androidx.lifecycle.ViewModel
import androidx.lifecycle.viewModelScope
import app.nina.domain.model.AppError
import app.nina.domain.model.Baby
import app.nina.domain.model.Outcome
import app.nina.domain.repository.AuthRepository
import app.nina.domain.repository.BabyRepository
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.update
import kotlinx.coroutines.launch

data class BabiesUiState(
    val babies: List<Baby> = emptyList(),
    val loading: Boolean = true,
    /** Rede indisponível e exibindo cache local. */
    val offline: Boolean = false,
    val error: AppError? = null,
)

class BabiesViewModel(
    private val babyRepository: BabyRepository,
    private val auth: AuthRepository,
) : ViewModel() {

    private val _state = MutableStateFlow(BabiesUiState())
    val state: StateFlow<BabiesUiState> = _state.asStateFlow()

    init {
        viewModelScope.launch {
            babyRepository.observeBabies().collect { list -> _state.update { it.copy(babies = list) } }
        }
        refresh()
    }

    fun refresh() {
        viewModelScope.launch {
            _state.update { it.copy(loading = true, error = null) }
            when (val r = babyRepository.refreshBabies()) {
                is Outcome.Success -> _state.update { it.copy(loading = false, offline = false) }
                is Outcome.Failure -> _state.update {
                    if (r.error == AppError.Network) it.copy(loading = false, offline = true)
                    else it.copy(loading = false, error = r.error)
                }
            }
        }
    }

    fun logout() {
        viewModelScope.launch { auth.logout() }
    }
}
