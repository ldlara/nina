package app.nina.ui.theme

import androidx.compose.foundation.isSystemInDarkTheme
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Shapes
import androidx.compose.material3.Typography
import androidx.compose.material3.darkColorScheme
import androidx.compose.material3.lightColorScheme
import androidx.compose.runtime.Composable
import androidx.compose.runtime.CompositionLocalProvider
import androidx.compose.runtime.Immutable
import androidx.compose.runtime.ReadOnlyComposable
import androidx.compose.runtime.staticCompositionLocalOf
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.text.TextStyle
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp

/*
 * Nina DS v0 (ux-spec §6): identidade original "crepúsculo e leite morno" (índigo suave, damasco, verde-sálvia).
 * Tokens do spec; pares de contraste a auditar antes do congelamento do DS v1 (§10.2).
 * Respeita o tema do sistema; o modo escuro ("Noite") nunca usa #000 nem branco puro.
 */

/** Tokens extras além do ColorScheme do Material 3. */
@Immutable
data class NinaColors(
    val eventSleep: Color,
    val eventFeeding: Color,
    val eventDiaper: Color,
    val eventPumping: Color,
    val success: Color,
    val warning: Color,
    val focusRing: Color,
    val borderSubtle: Color,
)

private val LightNina = NinaColors(
    eventSleep = Color(0xFF5B4B9A),
    eventFeeding = Color(0xFFA5541A),
    eventDiaper = Color(0xFF2B7A66),
    eventPumping = Color(0xFF9A3F6B),
    success = Color(0xFF2E7D4F),
    warning = Color(0xFF8A5A00),
    focusRing = Color(0xFF1F6FEB),
    borderSubtle = Color(0xFFDDD8E3),
)

private val DarkNina = NinaColors(
    eventSleep = Color(0xFFB6A8F0),
    eventFeeding = Color(0xFFF0B27E),
    eventDiaper = Color(0xFF7FD0B8),
    eventPumping = Color(0xFFEBA0C4),
    success = Color(0xFF7FD39E),
    warning = Color(0xFFF2C14E),
    focusRing = Color(0xFF8CB4FF),
    borderSubtle = Color(0xFF2F3547),
)

private val LightScheme = lightColorScheme(
    primary = Color(0xFF3B5BA9),
    onPrimary = Color(0xFFFFFFFF),
    primaryContainer = Color(0xFFDCE4F7),
    onPrimaryContainer = Color(0xFF1F2430),
    secondary = Color(0xFF5B4B9A),
    onSecondary = Color(0xFFFFFFFF),
    background = Color(0xFFFAF8F5),
    onBackground = Color(0xFF1F2430),
    surface = Color(0xFFFFFFFF),
    onSurface = Color(0xFF1F2430),
    surfaceVariant = Color(0xFFF1EEF7),
    onSurfaceVariant = Color(0xFF555C6E),
    surfaceContainer = Color(0xFFF1EEF7),
    surfaceContainerHigh = Color(0xFFF1EEF7),
    outline = Color(0xFF6E7385),
    outlineVariant = Color(0xFFDDD8E3),
    error = Color(0xFFB3261E),
    onError = Color(0xFFFFFFFF),
)

private val DarkScheme = darkColorScheme(
    primary = Color(0xFF9DB4F0),
    onPrimary = Color(0xFF10131A),
    primaryContainer = Color(0xFF2F3A5C),
    onPrimaryContainer = Color(0xFFE8EAF0),
    secondary = Color(0xFFB6A8F0),
    onSecondary = Color(0xFF10131A),
    background = Color(0xFF12141C),
    onBackground = Color(0xFFE8EAF0),
    surface = Color(0xFF1B1F2B),
    onSurface = Color(0xFFE8EAF0),
    surfaceVariant = Color(0xFF242938),
    onSurfaceVariant = Color(0xFFB3B9C9),
    surfaceContainer = Color(0xFF242938),
    surfaceContainerHigh = Color(0xFF242938),
    outline = Color(0xFF8C93A8),
    outlineVariant = Color(0xFF2F3547),
    error = Color(0xFFFF9A93),
    onError = Color(0xFF10131A),
)

// Fonte do sistema (ux-spec 6.2); tamanhos em sp escalam com a fonte do usuário (até 200%).
private val System = FontFamily.Default
private val NinaTypography = Typography(
    displayMedium = TextStyle(fontFamily = System, fontSize = 34.sp, lineHeight = 40.sp, fontWeight = FontWeight.SemiBold),
    headlineSmall = TextStyle(fontFamily = System, fontSize = 24.sp, lineHeight = 30.sp, fontWeight = FontWeight.SemiBold),
    titleLarge = TextStyle(fontFamily = System, fontSize = 20.sp, lineHeight = 26.sp, fontWeight = FontWeight.SemiBold),
    titleMedium = TextStyle(fontFamily = System, fontSize = 17.sp, lineHeight = 24.sp, fontWeight = FontWeight.SemiBold),
    bodyLarge = TextStyle(fontFamily = System, fontSize = 17.sp, lineHeight = 24.sp, fontWeight = FontWeight.Normal),
    bodyMedium = TextStyle(fontFamily = System, fontSize = 15.sp, lineHeight = 22.sp, fontWeight = FontWeight.Normal),
    labelLarge = TextStyle(fontFamily = System, fontSize = 17.sp, lineHeight = 22.sp, fontWeight = FontWeight.SemiBold),
    labelMedium = TextStyle(fontFamily = System, fontSize = 13.sp, lineHeight = 18.sp, fontWeight = FontWeight.Normal),
)

private val NinaShapes = Shapes(
    extraSmall = androidx.compose.foundation.shape.RoundedCornerShape(8.dp),
    small = androidx.compose.foundation.shape.RoundedCornerShape(8.dp),
    medium = androidx.compose.foundation.shape.RoundedCornerShape(14.dp),
    large = androidx.compose.foundation.shape.RoundedCornerShape(20.dp),
)

/** Espaçamento em grade de 4 dp e alvos de toque (ux-spec 6.3). */
object NinaDimens {
    val space1 = 4.dp
    val space2 = 8.dp
    val space3 = 12.dp
    val space4 = 16.dp
    val space5 = 20.dp
    val space6 = 24.dp
    val space8 = 32.dp
    val gutter = 16.dp
    val minTouch = 48.dp
    val primaryTouch = 56.dp
}

private val LocalNinaColors = staticCompositionLocalOf { LightNina }

object NinaTheme {
    val colors: NinaColors
        @Composable @ReadOnlyComposable get() = LocalNinaColors.current
}

@Composable
fun NinaTheme(darkTheme: Boolean = isSystemInDarkTheme(), content: @Composable () -> Unit) {
    CompositionLocalProvider(LocalNinaColors provides if (darkTheme) DarkNina else LightNina) {
        MaterialTheme(
            colorScheme = if (darkTheme) DarkScheme else LightScheme,
            typography = NinaTypography,
            shapes = NinaShapes,
            content = content,
        )
    }
}
