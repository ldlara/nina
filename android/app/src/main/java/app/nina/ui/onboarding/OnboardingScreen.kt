package app.nina.ui.onboarding

import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.semantics.LiveRegionMode
import androidx.compose.ui.semantics.heading
import androidx.compose.ui.semantics.liveRegion
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.unit.dp
import app.nina.R
import app.nina.ui.components.NinaPrimaryButton
import app.nina.ui.components.NinaTextButton
import app.nina.ui.theme.NinaDimens

private data class OnboardingPage(val title: Int, val body: Int)

private val pages = listOf(
    OnboardingPage(R.string.onboarding_title_1, R.string.onboarding_body_1),
    OnboardingPage(R.string.onboarding_title_2, R.string.onboarding_body_2),
    OnboardingPage(R.string.onboarding_title_3, R.string.onboarding_body_3),
)

/**
 * Onboarding (ux-spec 4.1, E1): valor em poucas frases. Navegação por botões (sem depender de gesto de deslizar).
 */
@Composable
fun OnboardingScreen(onStart: () -> Unit, onHaveAccount: () -> Unit) {
    var index by rememberSaveable { mutableIntStateOf(0) }
    val page = pages[index]
    val last = index == pages.lastIndex

    Column(
        Modifier
            .fillMaxSize()
            .verticalScroll(rememberScrollState())
            .padding(horizontal = NinaDimens.gutter, vertical = NinaDimens.space6),
        verticalArrangement = Arrangement.SpaceBetween,
    ) {
        Column(verticalArrangement = Arrangement.spacedBy(NinaDimens.space4)) {
            Spacer(Modifier.height(NinaDimens.space8))
            Text(
                stringResource(R.string.onboarding_page_indicator, index + 1, pages.size),
                style = MaterialTheme.typography.labelMedium,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
                modifier = Modifier.semantics { liveRegion = LiveRegionMode.Polite },
            )
            Text(
                stringResource(page.title),
                style = MaterialTheme.typography.headlineSmall,
                color = MaterialTheme.colorScheme.onBackground,
                modifier = Modifier.semantics { heading() },
            )
            Text(
                stringResource(page.body),
                style = MaterialTheme.typography.bodyLarge,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
            )
        }
        Column(
            Modifier.padding(top = NinaDimens.space8),
            verticalArrangement = Arrangement.spacedBy(NinaDimens.space2),
        ) {
            if (last) {
                NinaPrimaryButton(stringResource(R.string.onboarding_start), onClick = onStart)
            } else {
                NinaPrimaryButton(stringResource(R.string.onboarding_next), onClick = { index += 1 })
            }
            Row(horizontalArrangement = Arrangement.SpaceBetween, verticalAlignment = Alignment.CenterVertically) {
                if (!last) NinaTextButton(stringResource(R.string.onboarding_skip), onClick = onStart)
                NinaTextButton(stringResource(R.string.onboarding_have_account), onClick = onHaveAccount)
            }
        }
    }
}
