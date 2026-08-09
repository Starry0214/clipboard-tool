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
                var updatePrompt by remember { mutableStateOf<Pair<String, String?>?>(null) }
                var screen by remember { mutableStateOf("main") } // main | settings | login
                var loggedIn by remember { mutableStateOf(AppState.token.isNotEmpty()) }
                var skippedLogin by remember { mutableStateOf(false) } // 跳过登录本地使用
                updatePrompt?.let { (latest, changelog) ->
                    UpdateDialog(latest, changelog, onDismiss = { updatePrompt = null })
                }
                // 启动静默检查更新（今天未提示过才弹窗）
                androidx.compose.runtime.LaunchedEffect(Unit) {
                    UpdateChecker.checkSilent(this@MainActivity) { latest, changelog ->
                        updatePrompt = latest to changelog
                    }
                }
                if (!loggedIn && !skippedLogin && screen != "login") {
                    LoginScreen(onLoggedIn = { loggedIn = true; screen = "main" },
                        onSkip = { skippedLogin = true; screen = "main" })
                } else when (screen) {
                    "main" -> HistoryScreen(onOpenSettings = { screen = "settings" })
                    "settings" -> SettingsScreen(
                        onBack = { screen = "main" },
                        onLogin = { screen = "login" },
                        onLogout = { loggedIn = false; skippedLogin = false; screen = "main" })
                    else -> LoginScreen(onLoggedIn = { loggedIn = true; screen = "main" },
                        onSkip = { skippedLogin = true; screen = "main" })
                }
            }
        }
    }

    override fun onWindowFocusChanged(hasFocus: Boolean) {
        super.onWindowFocusChanged(hasFocus)
        // 打开/回前台（获焦后）即同步：上传手机剪贴板 + 拉取电脑端新内容（小米后台无法监听，此为补同步；store 去重兜底）
        if (hasFocus) {
            AppState.syncService?.onAppForeground()
        }
    }
}
