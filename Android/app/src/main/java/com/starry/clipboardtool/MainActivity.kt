package com.starry.clipboardtool

import android.Manifest
import android.content.Intent
import android.content.pm.PackageManager
import android.os.Build
import android.os.Bundle
import android.widget.Toast
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import com.starry.clipboardtool.sync.ClipboardEvents
import com.starry.clipboardtool.ui.HistoryScreen
import com.starry.clipboardtool.ui.LoginScreen
import com.starry.clipboardtool.ui.SettingsScreen
import com.starry.clipboardtool.ui.theme.AppTheme

class MainActivity : ComponentActivity() {
    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        // 分享接收：入库 + 转后台服务同步，不渲染主界面/进度界面
        if (handleShareIntent(intent)) {
            finish()
            return
        }
        setContent {
            AppTheme {
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

    override fun onNewIntent(intent: Intent) {
        super.onNewIntent(intent)
        setIntent(intent)
        if (handleShareIntent(intent)) finish()
    }

    override fun onResume() {
        super.onResume()
        // 第一次正常打开 App 时请求通知权限（Android 13+，仅一次）；之后由设置页权限管理控制
        if (!AppState.prefs.getBoolean("NotifPermAsked", false) &&
            Build.VERSION.SDK_INT >= 33 &&
            checkSelfPermission(Manifest.permission.POST_NOTIFICATIONS) != PackageManager.PERMISSION_GRANTED) {
            AppState.prefs.edit().putBoolean("NotifPermAsked", true).apply()
            requestPermissions(arrayOf(Manifest.permission.POST_NOTIFICATIONS), 100)
        }
    }

    /** 分享接收：解析入库，转 ShareUploadService 后台同步（通知栏进度）；返回 true 表示已处理（调用方 finish 回原 App）。 */
    private fun handleShareIntent(intent: Intent?): Boolean {
        if (intent?.action != Intent.ACTION_SEND && intent?.action != Intent.ACTION_SEND_MULTIPLE) return false
        val entries = ClipboardEvents.readShare(this, intent, AppState.store)
        if (entries.isEmpty()) {
            Toast.makeText(this, "无法读取分享内容", Toast.LENGTH_SHORT).show()
            return true
        }
        val added = AppState.syncService?.addShareEntries(entries) ?: emptyList()
        if (added.isEmpty()) {
            Toast.makeText(this, "内容已在剪贴板历史中", Toast.LENGTH_SHORT).show()
            return true
        }
        if (AppState.token.isEmpty()) {
            Toast.makeText(this, "内容已保存到本地（未登录不同步）", Toast.LENGTH_SHORT).show()
            return true
        }
        ShareUploadService.start(this, added)
        Toast.makeText(this, "已接收，正在后台同步（通知栏查看进度）", Toast.LENGTH_SHORT).show()
        return true
    }
}
