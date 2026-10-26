package app.nina.ui.timeline

import android.text.format.DateFormat
import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Add
import androidx.compose.material3.FloatingActionButton
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.SnackbarHostState
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.platform.LocalConfiguration
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.res.painterResource
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.semantics.Role
import androidx.compose.ui.semantics.contentDescription
import androidx.compose.ui.semantics.heading
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.unit.dp
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import app.nina.R
import app.nina.domain.tracking.SyncRunResult
import app.nina.domain.tracking.SyncState
import app.nina.ui.components.ChipFlow
import app.nina.ui.components.ChoiceChip
import app.nina.ui.components.ErrorBanner
import app.nina.ui.components.InfoBanner
import app.nina.ui.components.NinaSecondaryButton
import app.nina.ui.components.NinaTextButton
import app.nina.ui.components.SyncIndicatorBar
import app.nina.ui.theme.NinaDimens
import app.nina.ui.theme.NinaTheme
import app.nina.ui.tracking.BabyTab
import app.nina.ui.tracking.BabyTabsScaffold
import app.nina.ui.tracking.EventRowUi
import app.nina.ui.tracking.EventVisual
import app.nina.ui.tracking.FormKind
import app.nina.ui.tracking.QuickActionSheet
import app.nina.ui.tracking.describeEvent
import app.nina.ui.tracking.durationLabel
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch
import java.time.ZoneId
import java.time.format.DateTimeFormatter

@Composable
fun TimelineScreen(
    viewModel: TimelineViewModel,
    onBack: () -> Unit,
    onOpenToday: () -> Unit,
    onNewEvent: (FormKind) -> Unit,
    onEditEvent: (String) -> Unit,
) {
    val state by viewModel.state.collectAsStateWithLifecycle()
    val snackbar = remember { SnackbarHostState() }
    val ctx = LocalContext.current
    val locale = LocalConfiguration.current.locales[0]
    val is24 = DateFormat.is24HourFormat(ctx)
    var showQuick by remember { mutableStateOf(false) }

    val syncMessage = state.syncMessage
    LaunchedEffect(syncMessage) {
        val m = syncMessage ?: return@LaunchedEffect
        val text = when (m) {
            SyncRunResult.NotAvailable -> ctx.getString(R.string.msg_sync_unavailable)
            SyncRunResult.Offline -> ctx.getString(R.string.msg_sync_offline)
            is SyncRunResult.Failed -> ctx.getString(R.string.msg_save_failed)
            SyncRunResult.Done -> null
        }
        if (text != null) {
            val t = launch { delay(8_000); snackbar.currentSnackbarData?.dismiss() }
            snackbar.showSnackbar(text, duration = androidx.compose.material3.SnackbarDuration.Indefinite)
            t.cancel()
        }
        viewModel.dismissSyncMessage()
    }

    val diaperSaved = state.diaperSavedId
    val saveFailed = state.saveError
    LaunchedEffect(diaperSaved, saveFailed) {
        if (diaperSaved == null && !saveFailed) return@LaunchedEffect
        val t = launch { delay(8_000); snackbar.currentSnackbarData?.dismiss() }
        val text = if (diaperSaved != null) ctx.getString(R.string.msg_diaper_saved) else ctx.getString(R.string.msg_save_failed)
        val result = snackbar.showSnackbar(
            text,
            actionLabel = if (diaperSaved != null) ctx.getString(R.string.msg_undo) else null,
            duration = androidx.compose.material3.SnackbarDuration.Indefinite,
        )
        t.cancel()
        if (result == androidx.compose.material3.SnackbarResult.ActionPerformed) viewModel.undoDiaper() else viewModel.dismissDiaperMessage()
    }

    BabyTabsScaffold(
        title = stringResource(R.string.timeline_title),
        selected = BabyTab.TIMELINE,
        onSelect = { if (it == BabyTab.TODAY) onOpenToday() },
        onBack = onBack,
        snackbarHost = snackbar,
        floatingActionButton = {
            if (state.canRecord) {
                FloatingActionButton(onClick = { showQuick = true }, modifier = Modifier.size(NinaDimens.primaryTouch)) {
                    Icon(Icons.Filled.Add, contentDescription = stringResource(R.string.timeline_add))
                }
            }
        },
    ) { padding ->
        TimelineContent(
            padding = padding, state = state, is24 = is24, locale = locale,
            onFilter = viewModel::setFilter, onLoadMore = viewModel::loadMore, onRetry = viewModel::retry,
            onTryNow = viewModel::syncNow, onAdd = { showQuick = true }, onEdit = onEditEvent,
        )
    }
    if (showQuick) {
        QuickActionSheet(
            onDismiss = { showQuick = false },
            onPick = { showQuick = false; onNewEvent(it) },
            onDiaper = { showQuick = false; viewModel.quickDiaper(it) },
        )
    }
}

@Composable
private fun TimelineContent(
    padding: PaddingValues,
    state: TimelineUiState,
    is24: Boolean,
    locale: java.util.Locale,
    onFilter: (TimelineFilter) -> Unit,
    onLoadMore: () -> Unit,
    onRetry: () -> Unit,
    onTryNow: () -> Unit,
    onAdd: () -> Unit,
    onEdit: (String) -> Unit,
) {
    val ctx = LocalContext.current
    val res = ctx.resources
    val multiCaregiver = state.sections.any { s -> s.items.any { it.lastModifiedByName != null } }
    LazyColumn(
        Modifier.fillMaxSize().padding(padding),
        contentPadding = PaddingValues(start = NinaDimens.gutter, end = NinaDimens.gutter, top = NinaDimens.space2, bottom = 88.dp),
        verticalArrangement = Arrangement.spacedBy(NinaDimens.space2),
    ) {
        item(key = "sync") { SyncIndicatorBar(state.sync, onTryNow = onTryNow) }
        state.error?.let { _ ->
            item(key = "error") {
                Column(verticalArrangement = Arrangement.spacedBy(NinaDimens.space2)) {
                    ErrorBanner(stringResource(R.string.timeline_error))
                    NinaSecondaryButton(stringResource(R.string.common_retry), onClick = onRetry)
                }
            }
        }
        item(key = "filters") {
            val group = stringResource(R.string.filter_group)
            ChipFlow(Modifier.semantics { contentDescription = group }) {
                TimelineFilter.entries.forEach { f ->
                    ChoiceChip(stringResource(f.labelRes()), selected = state.filter == f, onClick = { onFilter(f) })
                }
            }
        }
        // RF-019-A8: horários no fuso do bebê, com aviso quando difere do aparelho.
        if (state.zone != ZoneId.systemDefault() && state.baby != null) {
            item(key = "zone") { InfoBanner(stringResource(R.string.timeline_zone_note, state.zone.id)) }
        }
        if (state.loading) {
            items(3, key = { "skeleton-$it" }) { SkeletonRow() }
            return@LazyColumn
        }
        state.openSleep?.let { open ->
            item(key = "open") {
                SectionHeader(stringResource(R.string.timeline_in_progress), null)
                EventRow(describeEvent(ctx, open, state.zone, is24, locale, multiCaregiver), onClick = { onEdit(open.id) })
            }
        }
        if (state.noEventsAtAll && state.openSleep == null) {
            item(key = "empty") {
                Column(Modifier.padding(vertical = NinaDimens.space4), verticalArrangement = Arrangement.spacedBy(NinaDimens.space2)) {
                    Text(stringResource(R.string.timeline_empty_title), style = MaterialTheme.typography.titleLarge, modifier = Modifier.semantics { heading() })
                    Text(stringResource(R.string.timeline_empty_body), style = MaterialTheme.typography.bodyLarge, color = MaterialTheme.colorScheme.onSurfaceVariant)
                }
            }
        }
        for (section in state.sections) {
            item(key = "h-${section.date}") {
                val label = dayLabel(section, locale)
                val totals = section.summary?.let {
                    stringResource(R.string.day_totals, res.durationLabel(it.sleepTotal), it.feedingCount, it.diaperCount)
                }
                SectionHeader(label, totals)
            }
            if (section.items.isEmpty()) {
                item(key = "e-${section.date}") {
                    Column(verticalArrangement = Arrangement.spacedBy(NinaDimens.space1)) {
                        Text(
                            stringResource(if (state.filter == TimelineFilter.ALL) R.string.timeline_day_empty else R.string.timeline_filter_empty),
                            style = MaterialTheme.typography.bodyLarge, color = MaterialTheme.colorScheme.onSurfaceVariant,
                        )
                        if (state.canRecord && state.filter == TimelineFilter.ALL) NinaTextButton(stringResource(R.string.timeline_add), onClick = onAdd)
                    }
                }
            } else {
                items(section.items, key = { it.id }) { e ->
                    EventRow(describeEvent(ctx, e, state.zone, is24, locale, multiCaregiver), onClick = { onEdit(e.id) })
                }
            }
        }
        if (state.hasMore) {
            item(key = "more") {
                // Rolagem infinita: ao chegar aqui carrega mais do banco local; o botão é a alternativa sem rolagem.
                LaunchedEffect(Unit) { onLoadMore() }
                NinaSecondaryButton(stringResource(R.string.timeline_load_more), onClick = onLoadMore)
            }
        }
    }
}

@Composable
private fun dayLabel(section: DaySection, locale: java.util.Locale): String {
    val date = DateTimeFormatter.ofPattern("EEE, d MMM", locale).format(section.date)
    return if (section.isToday) stringResource(R.string.timeline_today) + ", " + date else date
}

private fun TimelineFilter.labelRes(): Int = when (this) {
    TimelineFilter.ALL -> R.string.filter_all
    TimelineFilter.SLEEP -> R.string.filter_sleep
    TimelineFilter.FEEDING -> R.string.filter_feeding
    TimelineFilter.DIAPER -> R.string.filter_diaper
    TimelineFilter.PUMPING -> R.string.filter_pumping
}

@Composable
private fun SectionHeader(title: String, totals: String?) {
    Column(
        Modifier
            .fillMaxWidth()
            .padding(top = NinaDimens.space3)
            .semantics(mergeDescendants = true) { heading() },
    ) {
        Text(title, style = MaterialTheme.typography.titleMedium, color = MaterialTheme.colorScheme.onBackground)
        if (totals != null) Text(totals, style = MaterialTheme.typography.bodyMedium, color = MaterialTheme.colorScheme.onSurfaceVariant)
    }
}

@Composable
private fun EventRow(row: EventRowUi, onClick: () -> Unit) {
    val tint = when (row.visual) {
        EventVisual.SLEEP -> NinaTheme.colors.eventSleep
        EventVisual.DIAPER -> NinaTheme.colors.eventDiaper
        EventVisual.PUMPING -> NinaTheme.colors.eventPumping
        else -> NinaTheme.colors.eventFeeding
    }
    val hint = stringResource(R.string.a11y_edit_hint)
    Row(
        Modifier
            .fillMaxWidth()
            .heightIn(min = NinaDimens.minTouch)
            .clip(MaterialTheme.shapes.medium)
            .clickable(onClickLabel = hint, role = Role.Button, onClick = onClick)
            // Uma única fala por item: tipo, horário, duração, autoria e estado de envio (RF-019-A6).
            .semantics(mergeDescendants = true) { contentDescription = row.contentDescription }
            .padding(vertical = NinaDimens.space2),
        verticalAlignment = Alignment.Top,
    ) {
        Text(row.timeLabel, style = MaterialTheme.typography.labelLarge.copy(fontFeatureSettings = "tnum"), modifier = Modifier.width(64.dp))
        Icon(painterResource(row.visual.icon), contentDescription = null, tint = tint, modifier = Modifier.size(24.dp))
        Spacer(Modifier.width(NinaDimens.space3))
        Column(Modifier.weight(1f)) {
            Text(row.title, style = MaterialTheme.typography.bodyLarge, color = MaterialTheme.colorScheme.onSurface)
            row.detail?.let { Text(it, style = MaterialTheme.typography.bodyMedium, color = MaterialTheme.colorScheme.onSurfaceVariant) }
            row.editedBy?.let { Text(it, style = MaterialTheme.typography.labelMedium, color = MaterialTheme.colorScheme.onSurfaceVariant) }
            row.syncMarker?.let { marker ->
                val color = if (row.syncState == SyncState.FAILED) MaterialTheme.colorScheme.error else NinaTheme.colors.warning
                Text(marker, style = MaterialTheme.typography.labelMedium, color = color)
            }
        }
    }
}

@Composable
private fun SkeletonRow() {
    Box(
        Modifier
            .fillMaxWidth()
            .heightIn(min = NinaDimens.minTouch)
            .clip(MaterialTheme.shapes.medium)
            .background(MaterialTheme.colorScheme.surfaceVariant)
            .semantics { contentDescription = "" },
    )
}
