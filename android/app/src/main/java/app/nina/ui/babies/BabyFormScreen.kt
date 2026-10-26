package app.nina.ui.babies

import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.selection.selectable
import androidx.compose.foundation.selection.selectableGroup
import androidx.compose.material3.DatePicker
import androidx.compose.material3.DatePickerDialog
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.RadioButton
import androidx.compose.material3.SelectableDates
import androidx.compose.material3.SnackbarHostState
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.material3.rememberDatePickerState
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.semantics.Role
import androidx.compose.ui.semantics.contentDescription
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.unit.dp
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import app.nina.R
import app.nina.domain.model.Sex
import app.nina.ui.components.ErrorBanner
import app.nina.ui.components.InfoBanner
import app.nina.ui.components.LoadingBox
import app.nina.ui.components.NinaCard
import app.nina.ui.components.NinaCheckboxRow
import app.nina.ui.components.NinaPrimaryButton
import app.nina.ui.components.NinaScreen
import app.nina.ui.components.NinaSecondaryButton
import app.nina.ui.components.NinaTextField
import app.nina.ui.components.ScrollableColumn
import app.nina.ui.components.SectionTitle
import app.nina.ui.formatAgeDays
import app.nina.ui.formatDate
import app.nina.ui.messageRes
import app.nina.ui.theme.NinaDimens
import java.time.Instant
import java.time.LocalDate
import java.time.ZoneOffset

@Composable
fun BabyFormScreen(
    viewModel: BabyFormViewModel,
    onBack: () -> Unit,
    onCreated: (String) -> Unit,
) {
    val state by viewModel.state.collectAsStateWithLifecycle()
    val snackbar = remember { SnackbarHostState() }
    val savedText = stringResource(R.string.baby_saved)
    LaunchedEffect(viewModel) {
        viewModel.events.collect { event ->
            when (event) {
                is BabyFormEvent.Created -> onCreated(event.babyId)
            }
        }
    }
    LaunchedEffect(state.saved) { if (state.saved) snackbar.showSnackbar(savedText) }

    NinaScreen(
        title = stringResource(if (state.isCreate) R.string.baby_form_title_create else R.string.baby_form_title_edit),
        onBack = onBack,
        snackbarHost = snackbar,
    ) { padding ->
        if (state.loading && state.baby == null) {
            LoadingBox(Modifier.padding(padding))
            return@NinaScreen
        }
        ScrollableColumn(padding) {
            state.error?.let { ErrorBanner(stringResource(it.messageRes())) }
            if (!state.canEdit) InfoBanner(stringResource(R.string.baby_read_only_notice))

            NinaTextField(
                value = state.name,
                onValueChange = viewModel::onName,
                label = stringResource(R.string.baby_name),
                enabled = state.canEdit,
                errorText = state.nameIssue?.let {
                    if (it == app.nina.ui.FormIssue.NAME_TOO_LONG) stringResource(it.messageRes, app.nina.ui.BABY_NAME_MAX_LENGTH)
                    else stringResource(it.messageRes)
                },
            )
            DateField(
                label = stringResource(R.string.baby_birth_date),
                value = state.birthDate,
                enabled = state.canEdit,
                errorText = state.birthIssue?.let { stringResource(it.messageRes) },
                maxDate = LocalDate.now(),
                onPicked = viewModel::onBirthDate,
            )
            DateField(
                label = stringResource(R.string.baby_due_date),
                value = state.dueDate,
                enabled = state.canEdit,
                supportingText = stringResource(R.string.baby_due_date_help),
                onPicked = { viewModel.onDueDate(it) },
                onClear = if (state.dueDate != null) ({ viewModel.onDueDate(null) }) else null,
            )
            SexSelector(state.sex, state.canEdit, viewModel::onSex)
            Text(
                stringResource(R.string.baby_timezone) + ": " + state.timezone,
                style = MaterialTheme.typography.bodyMedium,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
            )

            if (state.isCreate && !state.guardianGranted) {
                NinaCheckboxRow(
                    checked = state.guardianChecked,
                    onCheckedChange = viewModel::onGuardian,
                    text = stringResource(R.string.baby_guardian_consent),
                    errorText = if (state.guardianMissing) stringResource(R.string.baby_guardian_consent_required) else null,
                )
            }

            if (state.canEdit) {
                NinaPrimaryButton(
                    text = stringResource(if (state.isCreate) R.string.baby_save_create else R.string.baby_save_edit),
                    onClick = viewModel::save,
                    loading = state.saving,
                )
            }

            state.baby?.let { baby -> AgeSection(baby, state.correctedWindowMonths) }
        }
    }
}

/** Idade vinda da API (`age_calculation`); o app não calcula nem persiste idade corrigida como dado do bebê. */
@Composable
private fun AgeSection(baby: app.nina.domain.model.Baby, correctedWindowMonths: Int?) {
    val context = LocalContext.current
    val calc = baby.ageCalculation ?: return
    SectionTitle(stringResource(R.string.age_section_title))
    NinaCard {
        Text(
            stringResource(R.string.age_chronological, formatAgeDays(context, calc.chronologicalDays)),
            style = MaterialTheme.typography.bodyLarge,
            color = MaterialTheme.colorScheme.onSurface,
        )
        val corrected = calc.correctedDays
        Text(
            if (corrected != null) stringResource(R.string.age_corrected, formatAgeDays(context, corrected))
            else stringResource(R.string.age_corrected_not_applicable),
            style = MaterialTheme.typography.bodyLarge,
            color = MaterialTheme.colorScheme.onSurface,
        )
        Text(
            stringResource(R.string.age_source_note),
            style = MaterialTheme.typography.labelMedium,
            color = MaterialTheme.colorScheme.onSurfaceVariant,
        )
        baby.ageAsOf?.let {
            Text(
                stringResource(R.string.age_as_of, formatDate(it)),
                style = MaterialTheme.typography.labelMedium,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
            )
        }
        correctedWindowMonths?.let {
            Text(
                stringResource(R.string.baby_corrected_window_hint, it),
                style = MaterialTheme.typography.labelMedium,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
            )
        }
    }
}

@Composable
private fun DateField(
    label: String,
    value: LocalDate?,
    enabled: Boolean,
    onPicked: (LocalDate) -> Unit,
    modifier: Modifier = Modifier,
    errorText: String? = null,
    supportingText: String? = null,
    maxDate: LocalDate? = null,
    onClear: (() -> Unit)? = null,
) {
    var open by remember { mutableStateOf(false) }
    val text = value?.let { formatDate(it) } ?: stringResource(R.string.baby_date_not_set)
    Column(modifier, verticalArrangement = Arrangement.spacedBy(NinaDimens.space1)) {
        Text(label, style = MaterialTheme.typography.labelMedium, color = MaterialTheme.colorScheme.onSurfaceVariant)
        Row(Modifier.fillMaxWidth(), verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(NinaDimens.space2)) {
            NinaSecondaryButton(
                text = text,
                onClick = { open = true },
                enabled = enabled,
                modifier = Modifier
                    .weight(1f)
                    .semantics { contentDescription = "$label: $text" },
            )
        }
        if (onClear != null && enabled) {
            TextButton(onClick = onClear, modifier = Modifier.heightIn(min = NinaDimens.minTouch)) {
                Text(stringResource(R.string.baby_clear_due_date))
            }
        }
        val helper = errorText ?: supportingText
        if (helper != null) {
            Text(
                helper,
                style = MaterialTheme.typography.labelMedium,
                color = if (errorText != null) MaterialTheme.colorScheme.error else MaterialTheme.colorScheme.onSurfaceVariant,
            )
        }
    }
    if (open) {
        val selectable = remember(maxDate) {
            object : SelectableDates {
                override fun isSelectableDate(utcTimeMillis: Long): Boolean =
                    maxDate == null || !Instant.ofEpochMilli(utcTimeMillis).atZone(ZoneOffset.UTC).toLocalDate().isAfter(maxDate)
            }
        }
        val pickerState = rememberDatePickerState(
            initialSelectedDateMillis = value?.atStartOfDay(ZoneOffset.UTC)?.toInstant()?.toEpochMilli(),
            selectableDates = selectable,
        )
        DatePickerDialog(
            onDismissRequest = { open = false },
            confirmButton = {
                TextButton(
                    onClick = {
                        pickerState.selectedDateMillis?.let {
                            onPicked(Instant.ofEpochMilli(it).atZone(ZoneOffset.UTC).toLocalDate())
                        }
                        open = false
                    },
                ) { Text(stringResource(R.string.baby_date_picker_confirm)) }
            },
            dismissButton = { TextButton(onClick = { open = false }) { Text(stringResource(R.string.common_cancel)) } },
        ) {
            DatePicker(state = pickerState)
        }
    }
}

@Composable
private fun SexSelector(selected: Sex?, enabled: Boolean, onSelect: (Sex?) -> Unit) {
    val options = listOf(
        null to R.string.sex_unspecified,
        Sex.FEMALE to R.string.sex_female,
        Sex.MALE to R.string.sex_male,
        Sex.OTHER to R.string.sex_other,
    )
    Column(Modifier.selectableGroup()) {
        Text(stringResource(R.string.baby_sex), style = MaterialTheme.typography.labelMedium, color = MaterialTheme.colorScheme.onSurfaceVariant)
        options.forEach { (sex, label) ->
            Row(
                Modifier
                    .fillMaxWidth()
                    .heightIn(min = NinaDimens.minTouch)
                    .selectable(selected = selected == sex, enabled = enabled, role = Role.RadioButton, onClick = { onSelect(sex) }),
                verticalAlignment = Alignment.CenterVertically,
            ) {
                RadioButton(selected = selected == sex, onClick = null, enabled = enabled)
                Text(stringResource(label), Modifier.padding(start = NinaDimens.space3), style = MaterialTheme.typography.bodyLarge)
            }
        }
    }
}
