package com.starry.clipboardtool.ui.theme

import androidx.compose.foundation.isSystemInDarkTheme
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.darkColorScheme
import androidx.compose.material3.lightColorScheme
import androidx.compose.runtime.Composable
import androidx.compose.ui.graphics.Color

private val LightColors = lightColorScheme(
    primary = BrandBlue,
    onPrimary = Color.White,
    primaryContainer = BrandBlueContainer,
    onPrimaryContainer = Color(0xFF003258),
    background = AppBackground,
    onBackground = Color(0xFF1A1C1E),
    surface = AppCard,
    onSurface = Color(0xFF1A1C1E),
    surfaceContainer = AppCard,
)

private val DarkColors = darkColorScheme(
    primary = BrandBlueDark,
    onPrimary = Color(0xFF003258),
    primaryContainer = BrandBlueContainerDark,
    onPrimaryContainer = Color(0xFFD3E4FF),
    background = AppBackgroundDark,
    onBackground = Color(0xFFE2E2E6),
    surface = AppCardDark,
    onSurface = Color(0xFFE2E2E6),
    surfaceContainer = AppCardDark,
)

@Composable
fun AppTheme(darkTheme: Boolean = isSystemInDarkTheme(), content: @Composable () -> Unit) {
    MaterialTheme(
        colorScheme = if (darkTheme) DarkColors else LightColors,
        content = content)
}
