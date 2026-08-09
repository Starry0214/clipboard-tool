package com.starry.clipboardtool

import android.os.Bundle
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.compose.material3.MaterialTheme
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import com.starry.clipboardtool.ui.HistoryScreen
import com.starry.clipboardtool.ui.LoginScreen
import com.starry.clipboardtool.ui.SettingsScreen

class MainActivity : ComponentActivity() {
    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        setContent {
            MaterialTheme {
                var screen by remember { mutableStateOf("main") } // main | settings
                var loggedIn by remember { mutableStateOf(AppState.token.isNotEmpty()) }
                if (!loggedIn) {
                    LoginScreen(onLoggedIn = { loggedIn = true })
                } else when (screen) {
                    "main" -> HistoryScreen(onOpenSettings = { screen = "settings" })
                    else -> SettingsScreen(
                        onBack = { screen = "main" },
                        onLogout = { loggedIn = false; screen = "main" })
                }
            }
        }
    }
}
