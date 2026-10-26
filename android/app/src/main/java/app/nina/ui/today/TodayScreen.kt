package app.nina.ui.today

import android.text.format.DateFormat
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.ModalBottomSheet
import androidx.compose.material3.SnackbarDuration
import androidx.compose.material3.SnackbarHostState
import androidx.compose.material3.SnackbarResult
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.material3.rememberModalBottomSheetState
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalConfiguration
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.semantics.contentDescription
import androidx.compose.ui.semantics.heading
import androidx.compose.ui.semantics.semantics
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import app.nina.R
import app.nina.domain.model.SleepType
import app.nina.domain.tracking.SleepEvent
import app.nina.ui.components.ChipFlow
import app.nina.ui.components.ChoiceChip
import app.nina.ui.components.ErrorBanner
import app.nina.ui.components.InfoBanner
import app.nina.ui.components.NinaCard
import app.nina.ui.components.NinaDestructiveTextButton
import app.nina.ui.components.NinaPrimaryButton
import app.nina.ui.components.NinaSecondaryButton
import app.nina.ui.components.NinaTextButton
import app.nina.ui.components.NinaTextField
import app.nina.ui.components.ShortcutChip
import app.nina.ui.components.SyncIndicatorBar
import app.nina.ui.components.hiddenFromTalkBack
import app.nina.ui.messageRes
import app.nina.ui.theme.NinaDimens
import app.nina.ui.tracking.BabyTab
import app.nina.ui.tracking.BabyTabsScaffold
import app.nina.ui.tracking.FormKind
import app.nina.ui.tracking.QuickActionSheet
import app.nina.ui.tracking.durationLabel
import app.nina.ui.tracking.formatClock
import app.nina.ui.tracking.formatStopwatch
import app.nina.ui.tracking.labelRes
import app.nina.ui.tracking.spokenDuration
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch
import java.time.Duration
import java.time.Instant
import java.time.ZoneId

private val SHORTCUT_MINUTES = listOf(5, 10, 15, 30)

@Composable
fun TodayScreen(
    viewModel: TodayViewModel,
    onBack: () -> Unit,
    onOpenTimeline: () -> Unit,
    onNewEvent: (FormKind) -> Unit,
    onEditEvent: (String) -> Unit,
) {
    val state by viewModel.state.collectAsStateWithLifecycle()
    val snackbar = remember { SnackbarHostState() }
    val ctx = LocalContext.current
    val res = ctx.resources
    val locale = LocalConfiguration.current.locales[0]
    val is24 = DateFormat.is24HourFormat(ctx)

    var showQuick by remember { mutableStateOf(false) }
    var showEarlier by remember { mutableStateOf(false) }
    var confirmCancel by remember { mutableStateOf(false) }

    // Relógio da tela: a cada 30 s (cronômetro por minuto para o TalkBack; segundos só visuais).
    LaunchedEffect(Unit) {
        while (true) {
            viewModel.tick()
            delay(30_000)
        }
    }

    // Mensagens efêmeras. "Desfazer" fica 8 s e é anunciado (ux-spec 2 e 10.1).
    val message = state.message
    LaunchedEffect(message) {
        val m = message ?: return@LaunchedEffect
        val (text, action) = when (m) {
            is TodayMessage.DiaperSaved -> res.getString(R.string.msg_diaper_saved) to res.getString(R.string.msg_undo)
            TodayMessage.SleepStarted -> res.getString(R.string.msg_sleep_started) to null
            TodayMessage.OpenSleepExists -> res.getString(R.string.msg_open_sleep_exists) to null
            TodayMessage.ReadOnly -> res.getString(R.string.msg_read_only) to null
            TodayMessage.SyncNotAvailable -> res.getString(R.string.msg_sync_unavailable) to null
            TodayMessage.SyncOffline -> res.getString(R.string.msg_sync_offline) to null
            is TodayMessage.Failed -> (m.error?.let { res.getString(it.messageRes()) } ?: res.getString(R.string.msg_save_failed)) to null
        }
        val timeout = launch { delay(8_000); snackbar.currentSnackbarData?.dismiss() }
        val result = snackbar.showSnackbar(text, actionLabel = action, duration = SnackbarDuration.Indefinite)
        timeout.cancel()
        if (result == SnackbarResult.ActionPerformed && m is TodayMessage.DiaperSaved) viewModel.undoCreated(m.eventId)
        viewModel.dismissMessage()
    }

    val open = state.openSleep
    val title = state.baby?.displayName?.let { stringResource(R.string.today_title, it) } ?: stringResource(R.string.tab_today)
    BabyTabsScaffold(
        title = title,
        selected = BabyTab.TODAY,
        onSelect = { if (it == BabyTab.TIMELINE) onOpenTimeline() },
        onBack = onBack,
        snackbarHost = snackbar,
    ) { padding ->
        Column(Modifier.fillMaxSize().padding(padding)) {
            Column(
                Modifier
                    .weight(1f)
                    .verticalScroll(rememberScrollState())
                    .padding(horizontal = NinaDimens.gutter, vertical = NinaDimens.space2),
                verticalArrangement = Arrangement.spacedBy(NinaDimens.space3),
            ) {
                SyncIndicatorBar(state.sync, onTryNow = viewModel::syncNow)
                state.error?.let { ErrorBanner(stringResource(it.messageRes())) }
                if (!state.loading && state.baby == null) InfoBanner(stringResource(R.string.today_baby_missing))
                if (state.baby != null && !state.canRecord) InfoBanner(stringResource(R.string.today_read_only))

                if (state.longSleepPrompt) LongSleepCard(onStillAsleep = viewModel::dismissLongSleepPrompt, onAwake = { viewModel.stopSleep() })

                if (open != null) {
                    SleepingCard(
                        open = open, now = state.now, zone = state.zone, is24 = is24, locale = locale,
                        canEdit = state.canRecord,
                        onMoveStart = viewModel::moveSleepStartEarlier,
                        onType = viewModel::setSleepType,
                        onCancel = { confirmCancel = true },
                    )
                } else {
                    AwakeCard(state, is24, locale)
                }

                SummaryCard(state, is24, locale)

                if (state.noRecordsToday && open == null && state.baby != null) {
                    Column(verticalArrangement = Arrangement.spacedBy(NinaDimens.space1)) {
                        Text(stringResource(R.string.today_empty_title), style = MaterialTheme.typography.titleLarge, modifier = Modifier.semantics { heading() })
                        Text(
                            stringResource(R.string.today_empty_body, state.baby?.displayName.orEmpty()),
                            style = MaterialTheme.typography.bodyLarge, color = MaterialTheme.colorScheme.onSurfaceVariant,
                        )
                    }
                }
            }

            // Ações na zona do polegar (metade inferior): botão primário de 56 dp (ux-spec 2, princípio 1).
            if (state.canRecord) {
                Column(
                    Modifier.fillMaxWidth().padding(horizontal = NinaDimens.gutter, vertical = NinaDimens.space2),
                    verticalArrangement = Arrangement.spacedBy(NinaDimens.space2),
                ) {
                    if (open == null) {
                        NinaPrimaryButton(stringResource(R.string.action_sleep_start), onClick = { viewModel.startSleep() })
                        Row(horizontalArrangement = Arrangement.spacedBy(NinaDimens.space2)) {
                            NinaSecondaryButton(stringResource(R.string.action_sleep_earlier), onClick = { showEarlier = true }, modifier = Modifier.weight(1f))
                            NinaSecondaryButton(stringResource(R.string.action_register_other), onClick = { showQuick = true }, modifier = Modifier.weight(1f))
                        }
                    } else {
                        NinaPrimaryButton(stringResource(R.string.action_sleep_stop), onClick = { viewModel.stopSleep() })
                        NinaSecondaryButton(stringResource(R.string.action_register_other), onClick = { showQuick = true })
                    }
                }
            }
        }
    }

    if (showQuick) {
        QuickActionSheet(
            onDismiss = { showQuick = false },
            onPick = { showQuick = false; onNewEvent(it) },
            onDiaper = { showQuick = false; viewModel.quickDiaper(it) },
        )
    }
    if (showEarlier) {
        EarlierSheet(
            onDismiss = { showEarlier = false },
            onPick = { showEarlier = false; viewModel.startSleep(it) },
            onOther = { showEarlier = false; onNewEvent(FormKind.SLEEP) },
        )
    }
    state.justStopped?.let { stopped ->
        StoppedSheet(
            sleep = stopped, zone = state.zone, is24 = is24, locale = locale,
            onAdjustEnd = viewModel::adjustStoppedEnd,
            onSave = viewModel::saveStoppedDetails,
            onUndo = viewModel::undoStop,
            onEdit = { viewModel.dismissStopped(); onEditEvent(stopped.id) },
            onDismiss = viewModel::dismissStopped,
        )
    }
    if (confirmCancel) {
        AlertDialog(
            onDismissRequest = { confirmCancel = false },
            title = { Text(stringResource(R.string.cancel_sleep_title)) },
            text = { Text(stringResource(R.string.cancel_sleep_body)) },
            confirmButton = {
                NinaDestructiveTextButton(stringResource(R.string.cancel_sleep_confirm), onClick = { confirmCancel = false; viewModel.cancelOpenSleep() })
            },
            dismissButton = { NinaTextButton(stringResource(R.string.cancel_sleep_keep), onClick = { confirmCancel = false }) },
        )
    }
}

@Composable
private fun LongSleepCard(onStillAsleep: () -> Unit, onAwake: () -> Unit) {
    NinaCard {
        Text(stringResource(R.string.long_sleep_prompt), style = MaterialTheme.typography.titleMedium)
        Row(horizontalArrangement = Arrangement.spacedBy(NinaDimens.space2)) {
            NinaSecondaryButton(stringResource(R.string.long_sleep_yes), onClick = onStillAsleep, modifier = Modifier.weight(1f))
            NinaSecondaryButton(stringResource(R.string.long_sleep_no), onClick = onAwake, modifier = Modifier.weight(1f))
        }
    }
}

@Composable
private fun SleepingCard(
    open: SleepEvent,
    now: Instant,
    zone: ZoneId,
    is24: Boolean,
    locale: java.util.Locale,
    canEdit: Boolean,
    onMoveStart: (Int) -> Unit,
    onType: (SleepType) -> Unit,
    onCancel: () -> Unit,
) {
    val res = LocalContext.current.resources
    val elapsed = Duration.between(open.startAt, now).let { if (it.isNegative) Duration.ZERO else it }
    val spokenElapsed = stringResource(R.string.today_sleeping_for, res.spokenDuration(elapsed))
    val since = stringResource(R.string.today_sleeping_since, formatClock(open.startAt, zone, is24, locale), stringResource(open.sleepType.labelRes()))
    // O cartão é um único foco: fala "Dormindo há 42 minutos" (atualiza a cada tick de 30 s, não por segundo).
    NinaCard(description = "$spokenElapsed. $since") {
        Text(stringResource(R.string.today_in_progress), style = MaterialTheme.typography.labelMedium, color = MaterialTheme.colorScheme.onSurfaceVariant)
        Text(
            formatStopwatch(elapsed),
            style = MaterialTheme.typography.displayMedium.copy(fontFeatureSettings = "tnum"),
            color = MaterialTheme.colorScheme.onSurface,
            modifier = Modifier.hiddenFromTalkBack(),
        )
        Text(since, style = MaterialTheme.typography.bodyLarge, color = MaterialTheme.colorScheme.onSurfaceVariant, modifier = Modifier.hiddenFromTalkBack())
    }
    if (canEdit) {
        Text(stringResource(R.string.sleep_started_before), style = MaterialTheme.typography.labelMedium, color = MaterialTheme.colorScheme.onSurfaceVariant)
        ChipFlow {
            SHORTCUT_MINUTES.forEach { m ->
                ShortcutChip(stringResource(R.string.chip_minus_minutes, m), stringResource(R.string.chip_minus_minutes_start_a11y, m), onClick = { onMoveStart(m) })
            }
        }
        ChipFlow {
            ChoiceChip(stringResource(R.string.sleep_type_nap), open.sleepType == SleepType.NAP, onClick = { onType(SleepType.NAP) })
            ChoiceChip(stringResource(R.string.sleep_type_night), open.sleepType == SleepType.NIGHT, onClick = { onType(SleepType.NIGHT) })
        }
        NinaTextButton(stringResource(R.string.action_cancel_recording), onClick = onCancel)
    }
}

@Composable
private fun AwakeCard(state: TodayUiState, is24: Boolean, locale: java.util.Locale) {
    val res = LocalContext.current.resources
    val last = state.lastClosedSleep
    val endAt = last?.endAt
    val awakeFor = endAt?.let { Duration.between(it, state.now) }?.takeIf { !it.isNegative }
    NinaCard(
        description = listOfNotNull(
            awakeFor?.let { stringResource(R.string.today_awake_for, res.spokenDuration(it)) },
            if (last != null && endAt != null) stringResource(R.string.today_last_sleep, formatClock(last.startAt, state.zone, is24, locale), formatClock(endAt, state.zone, is24, locale)) else null,
        ).joinToString(". ").ifEmpty { null },
    ) {
        Text(stringResource(R.string.today_now_label), style = MaterialTheme.typography.labelMedium, color = MaterialTheme.colorScheme.onSurfaceVariant)
        Text(
            awakeFor?.let { stringResource(R.string.today_awake_for, res.durationLabel(it)) } ?: stringResource(R.string.today_awake_unknown),
            style = MaterialTheme.typography.titleLarge,
        )
        if (last != null && endAt != null) {
            Text(
                stringResource(R.string.today_last_sleep, formatClock(last.startAt, state.zone, is24, locale), formatClock(endAt, state.zone, is24, locale)),
                style = MaterialTheme.typography.bodyMedium, color = MaterialTheme.colorScheme.onSurfaceVariant,
            )
        }
    }
}

@Composable
private fun SummaryCard(state: TodayUiState, is24: Boolean, locale: java.util.Locale) {
    val res = LocalContext.current.resources
    val s = state.summary
    val sleep = stringResource(R.string.today_summary_sleep, res.durationLabel(s.sleepTotal))
    val sleepSpoken = stringResource(R.string.today_summary_sleep, res.spokenDuration(s.sleepTotal))
    val feedings = stringResource(R.string.today_summary_feedings, s.feedingCount)
    val diapers = stringResource(R.string.today_summary_diapers, s.diaperCount)
    val naps = stringResource(R.string.today_summary_naps, s.napCount)
    val lastFeeding = state.lastFeedingAt?.let { at ->
        Duration.between(at, state.now).takeIf { !it.isNegative }?.let { stringResource(R.string.today_last_feeding, res.durationLabel(it)) }
    }
    val spoken = listOfNotNull(sleepSpoken, naps, feedings, diapers, if (s.pumpingCount > 0) stringResource(R.string.today_summary_pumpings, s.pumpingCount) else null, lastFeeding).joinToString(". ")
    NinaCard(description = stringResource(R.string.today_summary_title) + ". " + spoken) {
        Text(stringResource(R.string.today_summary_title), style = MaterialTheme.typography.labelMedium, color = MaterialTheme.colorScheme.onSurfaceVariant)
        Text("$sleep · $naps", style = MaterialTheme.typography.titleMedium)
        Text("$feedings · $diapers" + if (s.pumpingCount > 0) " · " + stringResource(R.string.today_summary_pumpings, s.pumpingCount) else "", style = MaterialTheme.typography.titleMedium)
        if (lastFeeding != null) Text(lastFeeding, style = MaterialTheme.typography.bodyMedium, color = MaterialTheme.colorScheme.onSurfaceVariant)
    }
}

@Composable
private fun EarlierSheet(onDismiss: () -> Unit, onPick: (Int) -> Unit, onOther: () -> Unit) {
    ModalBottomSheet(onDismissRequest = onDismiss, sheetState = rememberModalBottomSheetState(skipPartiallyExpanded = true)) {
        Column(
            Modifier.verticalScroll(rememberScrollState()).padding(horizontal = NinaDimens.gutter, vertical = NinaDimens.space2),
            verticalArrangement = Arrangement.spacedBy(NinaDimens.space3),
        ) {
            Text(stringResource(R.string.sleep_earlier_title), style = MaterialTheme.typography.titleLarge, modifier = Modifier.semantics { heading() })
            ChipFlow {
                SHORTCUT_MINUTES.forEach { m ->
                    ShortcutChip(stringResource(R.string.sleep_earlier_option, m), stringResource(R.string.sleep_earlier_option, m), onClick = { onPick(m) })
                }
            }
            NinaSecondaryButton(stringResource(R.string.sleep_earlier_other), onClick = onOther)
            Spacer(Modifier.height(NinaDimens.space4))
        }
    }
}

/** Confirmação leve após "Acordou": o sono já está gravado; aqui só se ajusta, detalha ou desfaz (ux-spec 4.2). */
@Composable
private fun StoppedSheet(
    sleep: SleepEvent,
    zone: ZoneId,
    is24: Boolean,
    locale: java.util.Locale,
    onAdjustEnd: (Int) -> Unit,
    onSave: (String, String) -> Unit,
    onUndo: () -> Unit,
    onEdit: () -> Unit,
    onDismiss: () -> Unit,
) {
    val res = LocalContext.current.resources
    var method by remember(sleep.id) { mutableStateOf(sleep.methodOrPlace.orEmpty()) }
    var notes by remember(sleep.id) { mutableStateOf(sleep.notes.orEmpty()) }
    val end = sleep.endAt ?: return
    val summary = stringResource(
        R.string.stopped_summary,
        formatClock(sleep.startAt, zone, is24, locale), formatClock(end, zone, is24, locale),
        res.durationLabel(Duration.between(sleep.startAt, end)),
    )
    ModalBottomSheet(onDismissRequest = onDismiss, sheetState = rememberModalBottomSheetState(skipPartiallyExpanded = true)) {
        Column(
            Modifier.verticalScroll(rememberScrollState()).padding(horizontal = NinaDimens.gutter, vertical = NinaDimens.space2),
            verticalArrangement = Arrangement.spacedBy(NinaDimens.space3),
        ) {
            Text(stringResource(R.string.stopped_title), style = MaterialTheme.typography.titleLarge, modifier = Modifier.semantics { heading() })
            Text(summary, style = MaterialTheme.typography.bodyLarge)
            Text(stringResource(R.string.stopped_adjust_end), style = MaterialTheme.typography.labelMedium, color = MaterialTheme.colorScheme.onSurfaceVariant)
            ChipFlow {
                SHORTCUT_MINUTES.forEach { m ->
                    ShortcutChip(stringResource(R.string.chip_minus_minutes, m), stringResource(R.string.chip_minus_minutes_end_a11y, m), onClick = { onAdjustEnd(m) })
                }
            }
            Text(stringResource(R.string.stopped_details), style = MaterialTheme.typography.labelMedium, color = MaterialTheme.colorScheme.onSurfaceVariant)
            NinaTextField(method, { method = it }, stringResource(R.string.field_method))
            NinaTextField(notes, { notes = it }, stringResource(R.string.field_notes), singleLine = false)
            NinaPrimaryButton(stringResource(R.string.stopped_save), onClick = { onSave(method, notes) })
            Row(horizontalArrangement = Arrangement.spacedBy(NinaDimens.space2)) {
                NinaSecondaryButton(stringResource(R.string.stopped_undo), onClick = onUndo, modifier = Modifier.weight(1f))
                NinaSecondaryButton(stringResource(R.string.stopped_edit_times), onClick = onEdit, modifier = Modifier.weight(1f))
            }
            Spacer(Modifier.height(NinaDimens.space4))
        }
    }
}
