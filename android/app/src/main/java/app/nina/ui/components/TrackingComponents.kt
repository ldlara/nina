package app.nina.ui.components

import android.text.format.DateFormat
import androidx.compose.foundation.BorderStroke
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.ExperimentalLayoutApi
import androidx.compose.foundation.layout.FlowRow
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.layout.widthIn
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.selection.selectable
import androidx.compose.foundation.selection.selectableGroup
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Check
import androidx.compose.material.icons.filled.Info
import androidx.compose.material.icons.filled.Refresh
import androidx.compose.material.icons.filled.Warning
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.DatePicker
import androidx.compose.material3.DatePickerDialog
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.material3.TimePicker
import androidx.compose.material3.rememberDatePickerState
import androidx.compose.material3.rememberTimePickerState
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.platform.LocalConfiguration
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.res.pluralStringResource
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.semantics.LiveRegionMode
import androidx.compose.ui.semantics.Role
import androidx.compose.ui.semantics.clearAndSetSemantics
import androidx.compose.ui.semantics.contentDescription
import androidx.compose.ui.semantics.liveRegion
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.unit.dp
import app.nina.R
import app.nina.domain.tracking.SyncIndicator
import app.nina.ui.theme.NinaDimens
import app.nina.ui.theme.NinaTheme
import app.nina.ui.tracking.formatClock
import java.time.Instant
import java.time.LocalDate
import java.time.LocalTime
import java.time.ZoneId
import java.time.ZoneOffset
import java.time.format.DateTimeFormatter

/**
 * Indicador único de sincronização (ux-spec 4.13): ícone + texto, discreto, nunca modal. O estado "Sincronizado" é
 * só uma linha neutra; offline e erro usam a faixa âmbar (ícone + texto, não só cor).
 */
@Composable
fun SyncIndicatorBar(indicator: SyncIndicator, onTryNow: () -> Unit, modifier: Modifier = Modifier, showWhenSynced: Boolean = true) {
    val warning = NinaTheme.colors.warning
    val text: String
    val attention: Boolean
    var canRetry = false
    when (indicator) {
        SyncIndicator.Synced -> {
            if (!showWhenSynced) return
            text = stringResource(R.string.sync_synced); attention = false
        }
        SyncIndicator.Syncing -> { text = stringResource(R.string.sync_syncing); attention = false }
        is SyncIndicator.Pending -> {
            text = pluralStringResource(R.plurals.sync_pending, indicator.count, indicator.count); attention = false; canRetry = true
        }
        is SyncIndicator.Offline -> {
            text = if (indicator.pending > 0) stringResource(R.string.sync_offline_pending, indicator.pending) else stringResource(R.string.sync_offline)
            attention = true
        }
        is SyncIndicator.Error -> {
            val ctx = LocalContext.current
            val since = formatClock(indicator.since, ZoneId.systemDefault(), DateFormat.is24HourFormat(ctx), LocalConfiguration.current.locales[0])
            text = stringResource(R.string.sync_error, since); attention = true; canRetry = true
        }
        is SyncIndicator.Rejected -> {
            text = pluralStringResource(R.plurals.sync_rejected, indicator.count, indicator.count); attention = true; canRetry = true
        }
    }
    Surface(
        modifier = modifier
            .fillMaxWidth()
            .semantics(mergeDescendants = true) { liveRegion = LiveRegionMode.Polite },
        shape = MaterialTheme.shapes.medium,
        color = MaterialTheme.colorScheme.surfaceVariant,
        border = if (attention) BorderStroke(1.dp, warning) else null,
    ) {
        Row(
            Modifier.padding(horizontal = NinaDimens.space3, vertical = NinaDimens.space1),
            verticalAlignment = Alignment.CenterVertically,
        ) {
            when {
                indicator == SyncIndicator.Syncing ->
                    CircularProgressIndicator(Modifier.size(18.dp).semantics { contentDescription = "" }, strokeWidth = 2.dp)
                attention -> Icon(Icons.Filled.Warning, contentDescription = null, tint = warning, modifier = Modifier.size(18.dp))
                indicator == SyncIndicator.Synced -> Icon(Icons.Filled.Check, contentDescription = null, modifier = Modifier.size(18.dp))
                else -> Icon(Icons.Filled.Info, contentDescription = null, modifier = Modifier.size(18.dp))
            }
            Spacer(Modifier.width(NinaDimens.space2))
            Text(
                text,
                style = MaterialTheme.typography.labelMedium,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
                modifier = Modifier.weight(1f).padding(vertical = NinaDimens.space2),
            )
            if (canRetry) {
                TextButton(onClick = onTryNow, modifier = Modifier.heightIn(min = NinaDimens.minTouch)) {
                    Icon(Icons.Filled.Refresh, contentDescription = null, modifier = Modifier.size(18.dp))
                    Spacer(Modifier.width(NinaDimens.space1))
                    Text(stringResource(R.string.sync_try_now), style = MaterialTheme.typography.labelMedium)
                }
            }
        }
    }
}

/** Opção de escolha única com a linha inteira tocável (≥ 48 dp), ícone de seleção e papel de RadioButton. */
@Composable
fun ChoiceChip(text: String, selected: Boolean, onClick: () -> Unit, modifier: Modifier = Modifier) {
    val shape = MaterialTheme.shapes.medium
    val scheme = MaterialTheme.colorScheme
    Row(
        modifier = modifier
            .heightIn(min = NinaDimens.minTouch)
            .widthIn(min = NinaDimens.minTouch)
            .clip(shape)
            .selectable(selected = selected, role = Role.RadioButton, onClick = onClick),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        Surface(
            shape = shape,
            color = if (selected) scheme.primaryContainer else scheme.surface,
            border = BorderStroke(if (selected) 2.dp else 1.dp, if (selected) scheme.primary else scheme.outline),
        ) {
            Row(
                Modifier.heightIn(min = NinaDimens.minTouch).padding(horizontal = NinaDimens.space4),
                verticalAlignment = Alignment.CenterVertically,
                horizontalArrangement = Arrangement.Center,
            ) {
                if (selected) {
                    Icon(Icons.Filled.Check, contentDescription = null, modifier = Modifier.size(18.dp), tint = scheme.onPrimaryContainer)
                    Spacer(Modifier.width(NinaDimens.space2))
                }
                Text(text, style = MaterialTheme.typography.labelLarge, color = if (selected) scheme.onPrimaryContainer else scheme.onSurface)
            }
        }
    }
}

/** Atalho de ação (sem estado de seleção): "−5 min", "Há 10 min". */
@Composable
fun ShortcutChip(text: String, description: String, onClick: () -> Unit, modifier: Modifier = Modifier) {
    OutlinedButton(
        onClick = onClick,
        modifier = modifier
            .heightIn(min = NinaDimens.minTouch)
            .widthIn(min = NinaDimens.minTouch)
            .semantics { contentDescription = description },
        shape = MaterialTheme.shapes.medium,
        border = BorderStroke(1.dp, MaterialTheme.colorScheme.outline),
    ) {
        Text(text, style = MaterialTheme.typography.labelLarge)
    }
}

@OptIn(ExperimentalLayoutApi::class)
@Composable
fun ChipFlow(modifier: Modifier = Modifier, content: @Composable () -> Unit) {
    FlowRow(
        modifier = modifier.fillMaxWidth().selectableGroup(),
        horizontalArrangement = Arrangement.spacedBy(NinaDimens.space2),
        verticalArrangement = Arrangement.spacedBy(NinaDimens.space2),
    ) { content() }
}

/** Passo (stepper) com botões de 48 dp e valor falado; os botões também têm rótulo próprio. */
@Composable
fun StepperRow(
    valueText: String,
    valueDescription: String,
    steps: List<Triple<String, String, () -> Unit>>,
    modifier: Modifier = Modifier,
) {
    // steps: (texto, descrição acessível, ação), na ordem visual; o valor fica entre os negativos e os positivos.
    val half = steps.size / 2
    Row(modifier.fillMaxWidth(), verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(NinaDimens.space1)) {
        steps.take(half).forEach { (t, d, a) -> ShortcutChip(t, d, a) }
        Text(
            valueText,
            style = MaterialTheme.typography.titleLarge,
            color = MaterialTheme.colorScheme.onSurface,
            modifier = Modifier
                .weight(1f)
                .padding(horizontal = NinaDimens.space2)
                .semantics { contentDescription = valueDescription; liveRegion = LiveRegionMode.Polite },
            textAlign = androidx.compose.ui.text.style.TextAlign.Center,
        )
        steps.drop(half).forEach { (t, d, a) -> ShortcutChip(t, d, a) }
    }
}

private val DATE_FORMAT_PATTERN = "EEE, d MMM"

/** Botão que mostra data+hora no fuso do bebê e abre seletores de data e hora (alternativa por toque, sem gesto). */
@OptIn(androidx.compose.material3.ExperimentalMaterial3Api::class)
@Composable
fun DateTimeField(
    label: String,
    instant: Instant,
    zone: ZoneId,
    onPicked: (LocalDate, LocalTime) -> Unit,
    modifier: Modifier = Modifier,
    errorText: String? = null,
) {
    val ctx = LocalContext.current
    val locale = LocalConfiguration.current.locales[0]
    val is24 = DateFormat.is24HourFormat(ctx)
    val zoned = instant.atZone(zone)
    val shown = DateTimeFormatter.ofPattern(DATE_FORMAT_PATTERN, locale).format(zoned) + ", " + formatClock(instant, zone, is24, locale)
    var step by remember { mutableStateOf(0) } // 0 fechado, 1 data, 2 hora
    var pickedDate by remember { mutableStateOf(zoned.toLocalDate()) }

    androidx.compose.foundation.layout.Column(modifier.fillMaxWidth(), verticalArrangement = Arrangement.spacedBy(NinaDimens.space1)) {
        Text(label, style = MaterialTheme.typography.labelMedium, color = MaterialTheme.colorScheme.onSurfaceVariant)
        val description = stringResource(R.string.field_time_button, label, shown)
        OutlinedButton(
            onClick = { pickedDate = zoned.toLocalDate(); step = 1 },
            modifier = Modifier
                .fillMaxWidth()
                .heightIn(min = NinaDimens.minTouch)
                .semantics { contentDescription = description },
            shape = MaterialTheme.shapes.medium,
            border = BorderStroke(1.dp, if (errorText != null) MaterialTheme.colorScheme.error else MaterialTheme.colorScheme.outline),
        ) {
            Text(shown, style = MaterialTheme.typography.bodyLarge)
        }
        if (errorText != null) FieldError(errorText)
    }

    if (step == 1) {
        val state = rememberDatePickerState(initialSelectedDateMillis = zoned.toLocalDate().atStartOfDay(ZoneOffset.UTC).toInstant().toEpochMilli())
        DatePickerDialog(
            onDismissRequest = { step = 0 },
            confirmButton = {
                TextButton(onClick = {
                    state.selectedDateMillis?.let { pickedDate = Instant.ofEpochMilli(it).atZone(ZoneOffset.UTC).toLocalDate() }
                    step = 2
                }, modifier = Modifier.heightIn(min = NinaDimens.minTouch)) { Text(stringResource(R.string.picker_ok)) }
            },
            dismissButton = {
                TextButton(onClick = { step = 0 }, modifier = Modifier.heightIn(min = NinaDimens.minTouch)) { Text(stringResource(R.string.picker_cancel)) }
            },
        ) { DatePicker(state = state, title = { Text(stringResource(R.string.picker_date_title), Modifier.padding(NinaDimens.space4)) }) }
    }
    if (step == 2) {
        val state = rememberTimePickerState(initialHour = zoned.hour, initialMinute = zoned.minute, is24Hour = is24)
        AlertDialog(
            onDismissRequest = { step = 0 },
            title = { Text(stringResource(R.string.picker_time_title)) },
            text = { Box(Modifier.verticalScroll(rememberScrollState())) { TimePicker(state = state) } },
            confirmButton = {
                TextButton(onClick = {
                    step = 0
                    onPicked(pickedDate, LocalTime.of(state.hour, state.minute))
                }, modifier = Modifier.heightIn(min = NinaDimens.minTouch)) { Text(stringResource(R.string.picker_ok)) }
            },
            dismissButton = {
                TextButton(onClick = { step = 0 }, modifier = Modifier.heightIn(min = NinaDimens.minTouch)) { Text(stringResource(R.string.picker_cancel)) }
            },
        )
    }
}

/** Erro de campo: ícone + texto, anunciado (não depende só de cor). */
@Composable
fun FieldError(text: String, modifier: Modifier = Modifier) {
    Row(
        modifier.semantics(mergeDescendants = true) { liveRegion = LiveRegionMode.Polite },
        verticalAlignment = Alignment.CenterVertically,
    ) {
        Icon(Icons.Filled.Warning, contentDescription = null, tint = MaterialTheme.colorScheme.error, modifier = Modifier.size(16.dp))
        Spacer(Modifier.width(NinaDimens.space1))
        Text(text, color = MaterialTheme.colorScheme.error, style = MaterialTheme.typography.labelMedium)
    }
}

/** Texto decorativo escondido do leitor de tela (o cronômetro é falado por minuto, não por segundo). */
fun Modifier.hiddenFromTalkBack(): Modifier = clearAndSetSemantics { }
