package app.nina.ui.babies

import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.semantics.Role
import androidx.compose.ui.unit.dp
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import app.nina.R
import app.nina.domain.model.Baby
import app.nina.ui.components.ErrorBanner
import app.nina.ui.components.InfoBanner
import app.nina.ui.components.LoadingBox
import app.nina.ui.components.NinaCard
import app.nina.ui.components.NinaPrimaryButton
import app.nina.ui.components.NinaScreen
import app.nina.ui.components.NinaSecondaryButton
import app.nina.ui.components.NinaTextButton
import app.nina.ui.formatAgeDays
import app.nina.ui.labelRes
import app.nina.ui.messageRes
import app.nina.ui.theme.NinaDimens

@Composable
fun BabiesScreen(
    viewModel: BabiesViewModel,
    onAddBaby: () -> Unit,
    onOpenBaby: (String) -> Unit,
    onOpenTracking: (String) -> Unit,
    onCaregivers: (String) -> Unit,
    onAcceptInvite: () -> Unit,
) {
    val state by viewModel.state.collectAsStateWithLifecycle()
    NinaScreen(
        title = stringResource(R.string.babies_title),
        onBack = null,
        actions = { NinaTextButton(stringResource(R.string.babies_logout), onClick = viewModel::logout) },
    ) { padding ->
        if (state.loading && state.babies.isEmpty()) {
            LoadingBox(Modifier.padding(padding))
            return@NinaScreen
        }
        LazyColumn(
            Modifier
                .fillMaxSize()
                .padding(padding),
            contentPadding = androidx.compose.foundation.layout.PaddingValues(NinaDimens.gutter),
            verticalArrangement = Arrangement.spacedBy(NinaDimens.space3),
        ) {
            if (state.offline) item { InfoBanner(stringResource(R.string.babies_offline_cached)) }
            state.error?.let { item { ErrorBanner(stringResource(it.messageRes())) } }
            if (state.babies.isEmpty()) {
                item {
                    Column(verticalArrangement = Arrangement.spacedBy(NinaDimens.space2)) {
                        Text(
                            stringResource(R.string.babies_empty_title),
                            style = MaterialTheme.typography.titleLarge,
                            color = MaterialTheme.colorScheme.onBackground,
                        )
                        Text(
                            stringResource(R.string.babies_empty_body),
                            style = MaterialTheme.typography.bodyLarge,
                            color = MaterialTheme.colorScheme.onSurfaceVariant,
                        )
                    }
                }
            }
            items(state.babies, key = { it.id }) { baby ->
                BabyCard(baby, onOpen = { onOpenBaby(baby.id) }, onTracking = { onOpenTracking(baby.id) }, onCaregivers = { onCaregivers(baby.id) })
            }
            item { NinaPrimaryButton(stringResource(R.string.babies_add), onClick = onAddBaby) }
            item { NinaSecondaryButton(stringResource(R.string.babies_accept_invite), onClick = onAcceptInvite) }
        }
    }
}

@Composable
private fun BabyCard(baby: Baby, onOpen: () -> Unit, onTracking: () -> Unit, onCaregivers: () -> Unit) {
    val context = LocalContext.current
    val age = baby.ageCalculation?.let { formatAgeDays(context, it.chronologicalDays) }.orEmpty()
    val role = stringResource(baby.myRole.labelRes())
    NinaCard(description = stringResource(R.string.babies_item_description, baby.displayName, age, role)) {
        Text(baby.displayName, style = MaterialTheme.typography.titleLarge, color = MaterialTheme.colorScheme.onSurface)
        if (age.isNotEmpty()) Text(age, style = MaterialTheme.typography.bodyMedium, color = MaterialTheme.colorScheme.onSurfaceVariant)
        Text(role, style = MaterialTheme.typography.bodyMedium, color = MaterialTheme.colorScheme.onSurfaceVariant)
        NinaPrimaryButton(stringResource(R.string.babies_open_tracking), onClick = onTracking)
        Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.spacedBy(NinaDimens.space2)) {
            NinaTextButton(stringResource(R.string.babies_open_profile), onClick = onOpen)
            NinaTextButton(stringResource(R.string.babies_caregivers), onClick = onCaregivers)
        }
    }
}
