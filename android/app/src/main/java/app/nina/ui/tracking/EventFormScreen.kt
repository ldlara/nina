package app.nina.ui.tracking

import android.text.format.DateFormat
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.imePadding
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalConfiguration
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.res.pluralStringResource
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.semantics.heading
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.text.input.KeyboardType
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import app.nina.R
import app.nina.domain.model.AppError
import app.nina.domain.model.BreastSide
import app.nina.domain.model.DiaperType
import app.nina.domain.model.MilkType
import app.nina.domain.model.SleepType
import app.nina.domain.tracking.EventField
import app.nina.domain.tracking.Rejection
import app.nina.domain.tracking.TrackingLimits
import app.nina.ui.components.ChipFlow
import app.nina.ui.components.ChoiceChip
import app.nina.ui.components.DateTimeField
import app.nina.ui.components.ErrorBanner
import app.nina.ui.components.FieldError
import app.nina.ui.components.InfoBanner
import app.nina.ui.components.NinaCheckboxRow
import app.nina.ui.components.NinaDestructiveTextButton
import app.nina.ui.components.NinaPrimaryButton
import app.nina.ui.components.NinaScreen
import app.nina.ui.components.NinaSecondaryButton
import app.nina.ui.components.NinaTextButton
import app.nina.ui.components.NinaTextField
import app.nina.ui.components.SectionTitle
import app.nina.ui.components.ShortcutChip
import app.nina.ui.components.StepperRow
import app.nina.ui.messageRes
import app.nina.ui.theme.NinaDimens
import java.time.Duration
import java.time.ZoneId

private val SHORTCUTS = listOf(5, 10, 15, 30)

@Composable
fun EventFormScreen(
    viewModel: EventFormViewModel,
    onClose: () -> Unit,
    limits: TrackingLimits = TrackingLimits(),
) {
    val state by viewModel.state.collectAsStateWithLifecycle()
    val ctx = LocalContext.current
    val is24 = DateFormat.is24HourFormat(ctx)
    val locale = LocalConfiguration.current.locales[0]
    var confirmDelete by remember { mutableStateOf(false) }
    var addWake by remember { mutableStateOf(false) }

    LaunchedEffect(state.done) { if (state.done) onClose() }

    val title = stringResource(
        when (state.kind) {
            FormKind.SLEEP -> if (state.isEdit) R.string.form_title_edit_sleep else R.string.form_title_new_sleep
            FormKind.BREASTFEEDING -> if (state.isEdit) R.string.form_title_edit_breast else R.string.form_title_new_breast
            FormKind.BOTTLE -> if (state.isEdit) R.string.form_title_edit_bottle else R.string.form_title_new_bottle
            FormKind.PUMPING -> if (state.isEdit) R.string.form_title_edit_pumping else R.string.form_title_new_pumping
            FormKind.DIAPER -> if (state.isEdit) R.string.form_title_edit_diaper else R.string.form_title_new_diaper
        },
    )

    NinaScreen(title = title, onBack = onClose, scrollable = false) { padding ->
        if (state.notFound) {
            Column(Modifier.padding(padding).padding(NinaDimens.gutter)) { InfoBanner(stringResource(R.string.form_not_found)) }
            return@NinaScreen
        }
        Column(Modifier.fillMaxSize().padding(padding).imePadding()) {
            Column(
                Modifier
                    .weight(1f)
                    .verticalScroll(rememberScrollState())
                    .padding(horizontal = NinaDimens.gutter, vertical = NinaDimens.space2),
                verticalArrangement = Arrangement.spacedBy(NinaDimens.space4),
            ) {
                FormBanners(state)
                when (state.kind) {
                    FormKind.SLEEP -> SleepFields(state, viewModel, is24, locale, onAddWake = { addWake = true })
                    FormKind.BREASTFEEDING -> BreastFields(state, viewModel)
                    FormKind.BOTTLE -> BottleFields(state, viewModel, limits)
                    FormKind.PUMPING -> PumpingFields(state, viewModel, limits)
                    FormKind.DIAPER -> DiaperFields(state, viewModel)
                }
                if (state.kind != FormKind.PUMPING) {
                    NinaTextField(
                        value = state.notes, onValueChange = viewModel::setNotes, label = stringResource(R.string.field_notes),
                        singleLine = false,
                        errorText = state.issue(EventField.NOTES)?.let { stringResource(it.messageRes(), limits.notesMaxLength) },
                    )
                }
            }
            // Ações na base da tela (zona do polegar).
            Column(
                Modifier.fillMaxWidth().padding(horizontal = NinaDimens.gutter, vertical = NinaDimens.space2),
                verticalArrangement = Arrangement.spacedBy(NinaDimens.space1),
            ) {
                NinaPrimaryButton(
                    text = stringResource(R.string.form_save),
                    onClick = viewModel::save,
                    enabled = state.canWrite && !state.loading,
                    loading = state.saving,
                )
                if (state.isEdit && state.canWrite) {
                    NinaDestructiveTextButton(stringResource(R.string.form_delete), onClick = { confirmDelete = true }, modifier = Modifier.fillMaxWidth())
                }
            }
        }
    }

    if (confirmDelete) {
        // RF-020-A6: confirmação antes de excluir.
        AlertDialog(
            onDismissRequest = { confirmDelete = false },
            title = { Text(stringResource(R.string.delete_confirm_title)) },
            text = { Text(stringResource(R.string.delete_confirm_body)) },
            confirmButton = {
                NinaDestructiveTextButton(stringResource(R.string.delete_confirm_yes), onClick = { confirmDelete = false; viewModel.delete() })
            },
            dismissButton = { NinaTextButton(stringResource(R.string.common_cancel), onClick = { confirmDelete = false }) },
        )
    }
    if (addWake) {
        AddWakeDialog(
            state = state,
            onDismiss = { addWake = false },
            onConfirm = { start, minutes ->
                addWake = false
                viewModel.addWake(start, start.plus(Duration.ofMinutes(minutes.toLong())))
            },
        )
    }
}

@Composable
private fun FormBanners(state: EventFormState) {
    if (!state.canWrite && !state.loading) InfoBanner(stringResource(R.string.form_read_only))
    state.error?.let { ErrorBanner(stringResource(it.messageRes())) }
    state.rejection?.let { r ->
        val res = when (r) {
            Rejection.ReadOnly -> R.string.msg_read_only
            is Rejection.OpenSleepExists -> R.string.msg_open_sleep_exists
            Rejection.EventNotFound -> R.string.msg_not_found
            Rejection.TypeMismatch -> R.string.msg_type_mismatch
            Rejection.BabyNotFound -> R.string.today_baby_missing
        }
        ErrorBanner(stringResource(res))
    }
    // Resumo anunciado dos erros depois de uma tentativa de salvar (cada campo também mostra o seu).
    val first = if (state.attempted) state.issues.firstOrNull() else null
    if (first != null) {
        val limit = TrackingLimits()
        val msg = when (first.field) {
            EventField.VOLUME -> stringResource(first.messageRes(), limit.bottleVolumeMlMax)
            EventField.NOTES -> stringResource(first.messageRes(), limit.notesMaxLength)
            EventField.METHOD -> stringResource(first.messageRes(), limit.methodMaxLength)
            else -> stringResource(first.messageRes())
        }
        ErrorBanner(msg)
    }
}

@Composable
private fun app.nina.domain.tracking.EventIssue.messageRes(): Int = issueMessageRes(field, code)

@Composable
private fun TimeShortcuts(onPick: (Int) -> Unit) {
    Text(stringResource(R.string.field_shortcuts), style = MaterialTheme.typography.labelMedium, color = MaterialTheme.colorScheme.onSurfaceVariant)
    ChipFlow {
        SHORTCUTS.forEach { m ->
            ShortcutChip(stringResource(R.string.chip_minutes_ago, m), stringResource(R.string.chip_minutes_ago, m), onClick = { onPick(m) })
        }
    }
}

@Composable
private fun SleepFields(state: EventFormState, vm: EventFormViewModel, is24: Boolean, locale: java.util.Locale, onAddWake: () -> Unit) {
    Column(verticalArrangement = Arrangement.spacedBy(NinaDimens.space2)) {
        Text(stringResource(R.string.field_sleep_type), style = MaterialTheme.typography.labelMedium, color = MaterialTheme.colorScheme.onSurfaceVariant)
        ChipFlow {
            ChoiceChip(stringResource(R.string.sleep_type_nap), state.sleepType == SleepType.NAP, onClick = { vm.setSleepType(SleepType.NAP) })
            ChoiceChip(stringResource(R.string.sleep_type_night), state.sleepType == SleepType.NIGHT, onClick = { vm.setSleepType(SleepType.NIGHT) })
        }
    }
    DateTimeField(
        label = stringResource(R.string.field_start), instant = state.start, zone = state.zone,
        onPicked = vm::setStart, errorText = state.issue(EventField.START)?.let { stringResource(it.messageRes()) },
    )
    NinaCheckboxRow(checked = state.end == null, onCheckedChange = vm::setOngoing, text = stringResource(R.string.field_ongoing))
    state.end?.let { end ->
        DateTimeField(
            label = stringResource(R.string.field_end), instant = end, zone = state.zone,
            onPicked = vm::setEnd, errorText = state.issue(EventField.END)?.let { stringResource(it.messageRes()) },
        )
    }
    if (state.overlapWarning) InfoBanner(stringResource(R.string.warn_sleep_overlap))
    NinaTextField(
        value = state.methodOrPlace, onValueChange = vm::setMethod, label = stringResource(R.string.field_method),
        errorText = state.issue(EventField.METHOD)?.let { stringResource(it.messageRes(), 80) },
    )
    if (state.isEdit && state.end != null) WakeSection(state, vm, is24, locale, onAddWake)
}

@Composable
private fun WakeSection(state: EventFormState, vm: EventFormViewModel, is24: Boolean, locale: java.util.Locale, onAdd: () -> Unit) {
    val res = LocalContext.current.resources
    Column(verticalArrangement = Arrangement.spacedBy(NinaDimens.space2)) {
        SectionTitle(stringResource(R.string.wake_title))
        if (state.sleepType == SleepType.NIGHT) {
            Text(
                state.nightAwakenings?.let { pluralStringResource(R.plurals.wake_night_awakenings, it, it) } ?: stringResource(R.string.wake_insufficient),
                style = MaterialTheme.typography.bodyMedium, color = MaterialTheme.colorScheme.onSurfaceVariant,
            )
        }
        if (state.wakes.isEmpty()) Text(stringResource(R.string.wake_none), style = MaterialTheme.typography.bodyMedium)
        state.wakes.forEach { w ->
            Row(Modifier.fillMaxWidth().heightIn(min = NinaDimens.minTouch), verticalAlignment = androidx.compose.ui.Alignment.CenterVertically) {
                Text(
                    stringResource(
                        R.string.wake_item,
                        formatClock(w.startedAt, state.zone, is24, locale), formatClock(w.endedAt, state.zone, is24, locale), res.durationLabel(w.duration),
                    ),
                    modifier = Modifier.weight(1f),
                )
                NinaTextButton(stringResource(R.string.wake_remove), onClick = { vm.removeWake(w.id) })
            }
        }
        NinaSecondaryButton(stringResource(R.string.wake_add), onClick = onAdd)
    }
}

@Composable
private fun AddWakeDialog(state: EventFormState, onDismiss: () -> Unit, onConfirm: (java.time.Instant, Int) -> Unit) {
    // Padrão: início = metade da sessão, 10 min. O usuário ajusta hora e duração.
    val sleepStart = state.start
    val sleepEnd = state.end ?: state.now
    var start by remember { mutableStateOf(sleepStart.plus(Duration.between(sleepStart, sleepEnd).dividedBy(2))) }
    var minutes by remember { mutableIntStateOf(10) }
    AlertDialog(
        onDismissRequest = onDismiss,
        title = { Text(stringResource(R.string.wake_add_title)) },
        text = {
            Column(verticalArrangement = Arrangement.spacedBy(NinaDimens.space3), modifier = Modifier.verticalScroll(rememberScrollState())) {
                DateTimeField(
                    label = stringResource(R.string.wake_start), instant = start, zone = state.zone,
                    onPicked = { d, t -> start = app.nina.domain.tracking.resolveLocal(d, t, state.zone) },
                )
                StepperRow(
                    valueText = stringResource(R.string.duration_min, minutes),
                    valueDescription = stringResource(R.string.wake_duration, minutes),
                    steps = listOf(
                        Triple("−5", stringResource(R.string.duration_decrease_5), { minutes = (minutes - 5).coerceAtLeast(1) }),
                        Triple("−1", stringResource(R.string.duration_decrease_1), { minutes = (minutes - 1).coerceAtLeast(1) }),
                        Triple("+1", stringResource(R.string.duration_increase_1), { minutes += 1 }),
                        Triple("+5", stringResource(R.string.duration_increase_5), { minutes += 5 }),
                    ),
                )
            }
        },
        confirmButton = { NinaTextButton(stringResource(R.string.picker_ok), onClick = { onConfirm(start, minutes) }) },
        dismissButton = { NinaTextButton(stringResource(R.string.picker_cancel), onClick = onDismiss) },
    )
}

@Composable
private fun SideChips(state: EventFormState, vm: EventFormViewModel, required: Boolean) {
    Column(verticalArrangement = Arrangement.spacedBy(NinaDimens.space2)) {
        Text(stringResource(R.string.field_side), style = MaterialTheme.typography.labelMedium, color = MaterialTheme.colorScheme.onSurfaceVariant)
        ChipFlow {
            listOf(BreastSide.LEFT, BreastSide.RIGHT, BreastSide.BOTH).forEach { s ->
                ChoiceChip(stringResource(s.labelRes()), state.side == s, onClick = { vm.setSide(s) })
            }
        }
        if (required) state.issue(EventField.SIDE)?.let { FieldError(stringResource(it.messageRes())) }
    }
}

@Composable
private fun DurationBlock(state: EventFormState, vm: EventFormViewModel) {
    val minutes = state.durationMinutes
    Column(verticalArrangement = Arrangement.spacedBy(NinaDimens.space2)) {
        Text(stringResource(R.string.field_duration), style = MaterialTheme.typography.labelMedium, color = MaterialTheme.colorScheme.onSurfaceVariant)
        StepperRow(
            valueText = stringResource(R.string.duration_min, minutes),
            valueDescription = stringResource(R.string.duration_stepper_label, minutes),
            steps = listOf(
                Triple("−5", stringResource(R.string.duration_decrease_5), { vm.setDurationMinutes(minutes - 5) }),
                Triple("−1", stringResource(R.string.duration_decrease_1), { vm.setDurationMinutes(minutes - 1) }),
                Triple("+1", stringResource(R.string.duration_increase_1), { vm.setDurationMinutes(minutes + 1) }),
                Triple("+5", stringResource(R.string.duration_increase_5), { vm.setDurationMinutes(minutes + 5) }),
            ),
        )
    }
}

/** Mamada: lado, fim (obrigatório; padrão agora) e duração. O início é derivado (fim − duração). */
@Composable
private fun BreastFields(state: EventFormState, vm: EventFormViewModel) {
    SideChips(state, vm, required = true)
    DateTimeField(
        label = stringResource(R.string.field_end), instant = state.end ?: state.now, zone = state.zone,
        onPicked = vm::setEnd, errorText = state.issue(EventField.END)?.let { stringResource(it.messageRes()) },
    )
    TimeShortcuts(vm::setMinutesAgo)
    DurationBlock(state, vm)
    state.issue(EventField.START)?.let { FieldError(stringResource(it.messageRes())) }
}

@Composable
private fun BottleFields(state: EventFormState, vm: EventFormViewModel, limits: TrackingLimits) {
    DateTimeField(
        label = stringResource(R.string.field_time), instant = state.start, zone = state.zone,
        onPicked = vm::setStart, errorText = state.issue(EventField.START)?.let { stringResource(it.messageRes()) },
    )
    TimeShortcuts(vm::setMinutesAgo)
    VolumeBlock(state, vm, limits.bottleVolumeMlMax, optional = false)
    // `milk_type` só existe em mamadeira (ADR-0009).
    Column(verticalArrangement = Arrangement.spacedBy(NinaDimens.space2)) {
        Text(stringResource(R.string.field_milk_type), style = MaterialTheme.typography.labelMedium, color = MaterialTheme.colorScheme.onSurfaceVariant)
        ChipFlow {
            ChoiceChip(stringResource(R.string.field_milk_none), state.milkType == null, onClick = { vm.setMilkType(null) })
            listOf(MilkType.BREAST_MILK, MilkType.FORMULA, MilkType.MIXED, MilkType.OTHER).forEach { m ->
                ChoiceChip(stringResource(m.labelRes()), state.milkType == m, onClick = { vm.setMilkType(m) })
            }
        }
    }
}

@Composable
private fun VolumeBlock(state: EventFormState, vm: EventFormViewModel, max: Int, optional: Boolean) {
    val ml = state.volumeMl
    Column(verticalArrangement = Arrangement.spacedBy(NinaDimens.space2)) {
        Text(
            stringResource(if (optional) R.string.field_volume_optional else R.string.field_volume),
            style = MaterialTheme.typography.labelMedium, color = MaterialTheme.colorScheme.onSurfaceVariant,
        )
        StepperRow(
            valueText = ml?.let { stringResource(R.string.volume_ml, it) } ?: "—",
            valueDescription = ml?.let { stringResource(R.string.volume_stepper_label, it) } ?: stringResource(R.string.field_volume),
            steps = listOf(
                Triple("−10", stringResource(R.string.volume_decrease), { vm.adjustVolume(-10, max).also { if (optional && (state.volumeMl ?: 0) <= 10) vm.setVolume(null) } }),
                Triple("+10", stringResource(R.string.volume_increase), { vm.adjustVolume(10, max) }),
            ),
        )
        // Entrada numérica alternativa ao stepper (acessibilidade e precisão).
        NinaTextField(
            value = ml?.toString().orEmpty(),
            onValueChange = { text -> vm.setVolume(text.filter(Char::isDigit).take(4).toIntOrNull()) },
            label = stringResource(if (optional) R.string.field_volume_optional else R.string.field_volume),
            keyboardType = KeyboardType.Number,
            errorText = state.issue(EventField.VOLUME)?.let { stringResource(it.messageRes(), max) },
        )
    }
}

@Composable
private fun PumpingFields(state: EventFormState, vm: EventFormViewModel, limits: TrackingLimits) {
    DateTimeField(
        label = stringResource(R.string.field_end), instant = state.end ?: state.now, zone = state.zone,
        onPicked = vm::setEnd, errorText = state.issue(EventField.END)?.let { stringResource(it.messageRes()) },
    )
    TimeShortcuts(vm::setMinutesAgo)
    DurationBlock(state, vm)
    VolumeBlock(state, vm, limits.pumpingVolumeMlMax, optional = true)
    SideChips(state, vm, required = false)
}

@Composable
private fun DiaperFields(state: EventFormState, vm: EventFormViewModel) {
    Column(verticalArrangement = Arrangement.spacedBy(NinaDimens.space2)) {
        Text(stringResource(R.string.field_diaper_type), style = MaterialTheme.typography.labelMedium, color = MaterialTheme.colorScheme.onSurfaceVariant)
        ChipFlow {
            listOf(DiaperType.WET, DiaperType.DIRTY, DiaperType.MIXED, DiaperType.DRY, DiaperType.UNSPECIFIED).forEach { t ->
                ChoiceChip(stringResource(t.labelRes()), state.diaperType == t, onClick = { vm.setDiaperType(t) })
            }
        }
        state.issue(EventField.DIAPER_TYPE)?.let { FieldError(stringResource(it.messageRes())) }
    }
    DateTimeField(
        label = stringResource(R.string.field_time), instant = state.start, zone = state.zone,
        onPicked = vm::setStart, errorText = state.issue(EventField.START)?.let { stringResource(it.messageRes()) },
    )
    TimeShortcuts(vm::setMinutesAgo)
}
