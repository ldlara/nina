package app.nina.ui.babies

import androidx.lifecycle.ViewModel
import androidx.lifecycle.viewModelScope
import app.nina.domain.model.AppError
import app.nina.domain.model.Baby
import app.nina.domain.model.BabyDraft
import app.nina.domain.model.Outcome
import app.nina.domain.model.Sex
import app.nina.domain.model.apiCode
import app.nina.domain.repository.AuthRepository
import app.nina.domain.repository.BabyRepository
import app.nina.ui.BABY_NAME_MAX_LENGTH
import app.nina.ui.FormIssue
import app.nina.ui.fieldError
import kotlinx.coroutines.channels.Channel
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.receiveAsFlow
import kotlinx.coroutines.flow.update
import kotlinx.coroutines.launch
import java.time.Clock
import java.time.LocalDate
import java.time.ZoneId

data class BabyFormUiState(
    val isCreate: Boolean,
    val loading: Boolean = false,
    val name: String = "",
    val birthDate: LocalDate? = null,
    val dueDate: LocalDate? = null,
    val sex: Sex? = null,
    val timezone: String,
    /** Último bebê conhecido (servidor/cache): fonte de `age_calculation` e `version`. */
    val baby: Baby? = null,
    /** Somente o Owner edita o perfil (ADR-0009, decisão 6). Na criação é sempre verdadeiro. */
    val canEdit: Boolean = true,
    val guardianGranted: Boolean = false,
    val guardianChecked: Boolean = false,
    val nameIssue: FormIssue? = null,
    val birthIssue: FormIssue? = null,
    val guardianMissing: Boolean = false,
    val correctedWindowMonths: Int? = null,
    val saving: Boolean = false,
    val saved: Boolean = false,
    val error: AppError? = null,
)

sealed interface BabyFormEvent {
    /** Bebê criado: abrir o perfil, onde a idade calculada pela API é exibida. */
    data class Created(val babyId: String) : BabyFormEvent
}

class BabyFormViewModel(
    private val babyId: String?,
    private val babies: BabyRepository,
    private val auth: AuthRepository,
    private val defaultTimezone: String = ZoneId.systemDefault().id,
    private val clock: Clock = Clock.systemDefaultZone(),
) : ViewModel() {

    private val _state = MutableStateFlow(BabyFormUiState(isCreate = babyId == null, loading = babyId != null, timezone = defaultTimezone))
    val state: StateFlow<BabyFormUiState> = _state.asStateFlow()

    private val _events = Channel<BabyFormEvent>(Channel.BUFFERED)
    val events = _events.receiveAsFlow()

    /** Depois da primeira edição do usuário, atualizações externas não sobrescrevem os campos. */
    private var dirty = false

    init {
        viewModelScope.launch {
            babies.referenceData().let { r ->
                if (r is Outcome.Success) _state.update { it.copy(correctedWindowMonths = r.value.correctedAgeMaxMonths) }
            }
        }
        if (babyId == null) {
            viewModelScope.launch {
                val r = auth.guardianConsentStatus()
                if (r is Outcome.Success && r.value.alreadyGranted) _state.update { it.copy(guardianGranted = true) }
            }
        } else {
            viewModelScope.launch { babies.observeBaby(babyId).collect { it?.let(::onBabyUpdated) } }
            viewModelScope.launch {
                val r = babies.refreshBaby(babyId)
                _state.update { it.copy(loading = false, error = if (r is Outcome.Failure && r.error != AppError.Network) r.error else null) }
            }
        }
    }

    private fun onBabyUpdated(baby: Baby) {
        _state.update { s ->
            val fill = !dirty
            s.copy(
                baby = baby,
                canEdit = baby.canEdit,
                loading = false,
                name = if (fill) baby.displayName else s.name,
                birthDate = if (fill) baby.birthDate else s.birthDate,
                dueDate = if (fill) baby.dueDate else s.dueDate,
                sex = if (fill) baby.sex else s.sex,
                timezone = if (fill) baby.timezone else s.timezone,
            )
        }
    }

    fun onName(value: String) { dirty = true; _state.update { it.copy(name = value, nameIssue = null, saved = false, error = null) } }
    fun onBirthDate(value: LocalDate) { dirty = true; _state.update { it.copy(birthDate = value, birthIssue = null, saved = false, error = null) } }
    fun onDueDate(value: LocalDate?) { dirty = true; _state.update { it.copy(dueDate = value, saved = false, error = null) } }
    fun onSex(value: Sex?) { dirty = true; _state.update { it.copy(sex = value, saved = false) } }
    fun onGuardian(value: Boolean) = _state.update { it.copy(guardianChecked = value, guardianMissing = false, error = null) }

    fun save() {
        val s = _state.value
        if (s.saving || !s.canEdit) return
        val name = s.name.trim()
        val nameIssue = when {
            name.isEmpty() -> FormIssue.REQUIRED
            name.length > BABY_NAME_MAX_LENGTH -> FormIssue.NAME_TOO_LONG
            else -> null
        }
        val today = LocalDate.now(clock.withZone(runCatching { ZoneId.of(s.timezone) }.getOrDefault(ZoneId.systemDefault())))
        val birth = s.birthDate
        val birthIssue = when {
            birth == null -> FormIssue.REQUIRED
            birth.isAfter(today) -> FormIssue.FUTURE_DATE
            else -> null
        }
        val guardianMissing = s.isCreate && !s.guardianGranted && !s.guardianChecked
        if (nameIssue != null || birthIssue != null || guardianMissing || birth == null) {
            _state.update { it.copy(nameIssue = nameIssue, birthIssue = birthIssue, guardianMissing = guardianMissing) }
            return
        }
        val draft = BabyDraft(name, birth, s.dueDate, s.sex, s.timezone)
        viewModelScope.launch {
            _state.update { it.copy(saving = true, error = null, saved = false) }
            if (s.isCreate) performCreate(draft) else performUpdate(s.baby, draft)
        }
    }

    private suspend fun performCreate(draft: BabyDraft) {
        ensureGuardianConsent()?.let { return fail(it) }
        when (val r = babies.createBaby(draft)) {
            is Outcome.Success -> {
                _state.update { it.copy(saving = false) }
                _events.send(BabyFormEvent.Created(r.value.id))
            }
            is Outcome.Failure -> {
                // 403 CONSENT_REQUIRED: o servidor não tem a declaração; volta a pedir.
                if (r.error.apiCode == "CONSENT_REQUIRED") _state.update { it.copy(guardianGranted = false, guardianChecked = false, guardianMissing = true) }
                fail(r.error)
            }
        }
    }

    private suspend fun performUpdate(current: Baby?, draft: BabyDraft) {
        if (current == null) return fail(AppError.Unexpected("baby not loaded"))
        when (val r = babies.updateBaby(current, draft)) {
            is Outcome.Success -> {
                dirty = false
                onBabyUpdated(r.value)
                _state.update { it.copy(saving = false, saved = true) }
            }
            is Outcome.Failure -> {
                // Em VERSION_CONFLICT o repositório já recarregou o bebê; liberar os campos para refletir o servidor.
                if (r.error.apiCode == "VERSION_CONFLICT") dirty = false
                fail(r.error)
            }
        }
    }

    /** Registra a declaração de responsável legal antes de criar o bebê (o servidor exige, 403 CONSENT_REQUIRED). */
    private suspend fun ensureGuardianConsent(): AppError? {
        if (_state.value.guardianGranted) return null
        val status = auth.guardianConsentStatus()
        if (status is Outcome.Failure) return status.error
        status as Outcome.Success
        if (status.value.alreadyGranted) {
            _state.update { it.copy(guardianGranted = true) }
            return null
        }
        val version = status.value.documentVersion ?: return AppError.Unexpected("guardian document unavailable")
        return when (val g = auth.grantGuardianConsent(version)) {
            is Outcome.Failure -> g.error
            is Outcome.Success -> {
                _state.update { it.copy(guardianGranted = true) }
                null
            }
        }
    }

    private fun fail(error: AppError) = _state.update {
        it.copy(
            saving = false,
            error = error,
            nameIssue = error.fieldError("display_name")?.let { f -> FormIssue.fromServer(f.code) } ?: it.nameIssue,
            birthIssue = error.fieldError("birth_date")?.let { f -> FormIssue.fromServer(f.code) } ?: it.birthIssue,
        )
    }
}
