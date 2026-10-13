package app.nina.ui.tracking

import androidx.compose.foundation.BorderStroke
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.navigationBarsPadding
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.ArrowBack
import androidx.compose.material.icons.automirrored.filled.List
import androidx.compose.material.icons.filled.Close
import androidx.compose.material.icons.filled.Home
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.ModalBottomSheet
import androidx.compose.material3.NavigationBar
import androidx.compose.material3.NavigationBarItem
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.Scaffold
import androidx.compose.material3.SnackbarHost
import androidx.compose.material3.SnackbarHostState
import androidx.compose.material3.Text
import androidx.compose.material3.TopAppBar
import androidx.compose.material3.TopAppBarDefaults
import androidx.compose.material3.rememberModalBottomSheetState
import androidx.compose.runtime.Composable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.res.painterResource
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.semantics.heading
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.unit.dp
import app.nina.R
import app.nina.domain.model.DiaperType
import app.nina.ui.components.ChipFlow
import app.nina.ui.components.NinaTextButton
import app.nina.ui.theme.NinaDimens
import app.nina.ui.theme.NinaTheme

enum class BabyTab { TODAY, TIMELINE }

/** Estrutura das telas do bebê: barra superior, abas Hoje/Linha do tempo e snackbar. */
@Composable
fun BabyTabsScaffold(
    title: String,
    selected: BabyTab,
    onSelect: (BabyTab) -> Unit,
    onBack: () -> Unit,
    snackbarHost: SnackbarHostState,
    modifier: Modifier = Modifier,
    floatingActionButton: @Composable () -> Unit = {},
    content: @Composable (PaddingValues) -> Unit,
) {
    Scaffold(
        modifier = modifier,
        containerColor = MaterialTheme.colorScheme.background,
        topBar = {
            TopAppBar(
                title = { Text(title, modifier = Modifier.semantics { heading() }) },
                navigationIcon = {
                    IconButton(onClick = onBack) {
                        Icon(Icons.AutoMirrored.Filled.ArrowBack, contentDescription = stringResource(R.string.common_back))
                    }
                },
                colors = TopAppBarDefaults.topAppBarColors(containerColor = MaterialTheme.colorScheme.background),
            )
        },
        bottomBar = {
            NavigationBar(containerColor = MaterialTheme.colorScheme.surface) {
                NavigationBarItem(
                    selected = selected == BabyTab.TODAY,
                    onClick = { onSelect(BabyTab.TODAY) },
                    icon = { Icon(Icons.Filled.Home, contentDescription = null) },
                    label = { Text(stringResource(R.string.tab_today)) },
                    alwaysShowLabel = true,
                )
                NavigationBarItem(
                    selected = selected == BabyTab.TIMELINE,
                    onClick = { onSelect(BabyTab.TIMELINE) },
                    icon = { Icon(Icons.AutoMirrored.Filled.List, contentDescription = null) },
                    label = { Text(stringResource(R.string.tab_timeline)) },
                    alwaysShowLabel = true,
                )
            }
        },
        snackbarHost = { SnackbarHost(snackbarHost) },
        floatingActionButton = floatingActionButton,
        content = content,
    )
}

/**
 * Sheet de ação rápida (ux-spec 5.1): Sono, Mamada, Mamadeira, Extração e Fralda. A fralda salva com **um toque no
 * tipo** (2 toques desde a Home: "Registrar outro" -> tipo). Sempre há o botão Fechar (alternativa ao gesto).
 */
@Composable
fun QuickActionSheet(
    onDismiss: () -> Unit,
    onPick: (FormKind) -> Unit,
    onDiaper: (DiaperType) -> Unit,
) {
    ModalBottomSheet(onDismissRequest = onDismiss, sheetState = rememberModalBottomSheetState(skipPartiallyExpanded = true)) {
        Column(
            Modifier
                .verticalScroll(rememberScrollState())
                .navigationBarsPadding()
                .padding(horizontal = NinaDimens.gutter, vertical = NinaDimens.space2),
            verticalArrangement = Arrangement.spacedBy(NinaDimens.space3),
        ) {
            Row(Modifier.fillMaxWidth(), verticalAlignment = Alignment.CenterVertically) {
                Text(
                    stringResource(R.string.quick_title),
                    style = MaterialTheme.typography.titleLarge,
                    modifier = Modifier.weight(1f).semantics { heading() },
                )
                IconButton(onClick = onDismiss) {
                    Icon(Icons.Filled.Close, contentDescription = stringResource(R.string.quick_close))
                }
            }
            ChipFlow {
                QuickButton(EventVisual.SLEEP, R.string.quick_sleep) { onPick(FormKind.SLEEP) }
                QuickButton(EventVisual.BREAST, R.string.quick_breast) { onPick(FormKind.BREASTFEEDING) }
                QuickButton(EventVisual.BOTTLE, R.string.quick_bottle) { onPick(FormKind.BOTTLE) }
                QuickButton(EventVisual.PUMPING, R.string.quick_pumping) { onPick(FormKind.PUMPING) }
            }
            Column(verticalArrangement = Arrangement.spacedBy(NinaDimens.space2)) {
                Row(verticalAlignment = Alignment.CenterVertically) {
                    Icon(
                        painterResource(EventVisual.DIAPER.icon), contentDescription = null,
                        tint = NinaTheme.colors.eventDiaper, modifier = Modifier.size(24.dp),
                    )
                    Text(
                        stringResource(R.string.quick_diaper_hint),
                        style = MaterialTheme.typography.titleMedium,
                        modifier = Modifier.padding(start = NinaDimens.space2).semantics { heading() },
                    )
                }
                ChipFlow {
                    // Um toque no tipo salva a fralda agora.
                    listOf(DiaperType.WET, DiaperType.DIRTY, DiaperType.MIXED).forEach { type ->
                        OutlinedButton(
                            onClick = { onDiaper(type) },
                            modifier = Modifier.heightIn(min = NinaDimens.primaryTouch),
                            shape = MaterialTheme.shapes.medium,
                            border = BorderStroke(1.dp, MaterialTheme.colorScheme.outline),
                        ) { Text(stringResource(type.labelRes()), style = MaterialTheme.typography.labelLarge) }
                    }
                }
                NinaTextButton(stringResource(R.string.form_title_new_diaper), onClick = { onPick(FormKind.DIAPER) })
            }
        }
    }
}

@Composable
private fun QuickButton(visual: EventVisual, label: Int, onClick: () -> Unit) {
    val tint = when (visual) {
        EventVisual.SLEEP -> NinaTheme.colors.eventSleep
        EventVisual.DIAPER -> NinaTheme.colors.eventDiaper
        EventVisual.PUMPING -> NinaTheme.colors.eventPumping
        else -> NinaTheme.colors.eventFeeding
    }
    OutlinedButton(
        onClick = onClick,
        modifier = Modifier.heightIn(min = NinaDimens.primaryTouch),
        shape = MaterialTheme.shapes.medium,
        border = BorderStroke(1.dp, MaterialTheme.colorScheme.outline),
    ) {
        Icon(painterResource(visual.icon), contentDescription = null, tint = tint, modifier = Modifier.size(24.dp))
        Spacer(Modifier.width(NinaDimens.space2))
        Text(stringResource(label), style = MaterialTheme.typography.labelLarge)
    }
}
