package com.starry.clipboardtool.ui

import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Button
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.unit.dp
import com.starry.clipboardtool.AppState
import com.starry.clipboardtool.UpdateChecker
import com.starry.clipboardtool.Updater

import java.util.Locale

private fun formatSize(bytes: Long): String {
    if (bytes < 1024 * 1024) return "${bytes / 1024} KB"
    return String.format(Locale.CHINA, "%.1f MB", bytes / 1024.0 / 1024.0)
}

@Composable
fun SettingsScreen(onBack: () -> Unit, onLogin: () -> Unit = {}, onLogout: () -> Unit) {
    var syncResult by remember { mutableStateOf("") }
    var updateResult by remember { mutableStateOf<String?>(null) }
    var updateChangelog by remember { mutableStateOf<String?>(null) }
    val context = LocalContext.current
    val loggedIn = AppState.token.isNotEmpty()

    Column(modifier = Modifier.fillMaxSize().padding(16.dp)) {
        Text("设置", style = MaterialTheme.typography.headlineSmall)
        Spacer(Modifier.height(16.dp))

        Text("账号", style = MaterialTheme.typography.titleSmall)
        if (loggedIn) {
            Text("已登录：${AppState.username}（${AppState.deviceName}）",
                style = MaterialTheme.typography.bodyMedium)
            Spacer(Modifier.height(8.dp))
            Button(onClick = { AppState.syncService?.logout(); onLogout() }) { Text("退出登录") }
        } else {
            Text("未登录（当前为本地使用模式）", style = MaterialTheme.typography.bodyMedium)
            Spacer(Modifier.height(8.dp))
            Button(onClick = onLogin) { Text("登录账号") }
        }

        Spacer(Modifier.height(24.dp))
        Text("数据", style = MaterialTheme.typography.titleSmall)
        val storage = remember { AppState.store.storageUsage() }
        Text("存储占用：${formatSize(storage)}", style = MaterialTheme.typography.bodyMedium)
        Spacer(Modifier.height(8.dp))
        Button(onClick = {
            syncResult = "同步中…"
            AppState.syncService?.syncFromServer { syncResult = it }
        }, enabled = loggedIn) { Text("同步服务器到本地") }
        if (syncResult.isNotEmpty()) {
            Text(syncResult, style = MaterialTheme.typography.bodyMedium)
        }
        Text(if (loggedIn)
            "将服务器上最近 7 天的内容拉取到本机历史（重复内容自动跳过）。"
        else
            "未登录时仅可使用本地功能，登录后可多端同步。",
            style = MaterialTheme.typography.bodySmall,
            color = MaterialTheme.colorScheme.onSurfaceVariant)

        Spacer(Modifier.height(24.dp))
        Text("关于", style = MaterialTheme.typography.titleSmall)
        Text("当前版本：v${Updater.currentVersion(context)}",
            style = MaterialTheme.typography.bodyMedium)
        Spacer(Modifier.height(8.dp))
        Button(onClick = {
            updateResult = "检查中…"
            updateChangelog = null
            UpdateChecker.checkManual(context) { latest, changelog ->
                updateResult = latest
                updateChangelog = changelog
            }
        }) { Text("检查更新") }
        val isNewVersion = updateResult?.matches(Regex("^\\d+\\.\\d+\\.\\d+$")) == true
        if (updateResult != null && !isNewVersion && updateResult != "检查中…") {
            Text(updateResult!!, style = MaterialTheme.typography.bodyMedium)
        }

        if (loggedIn) {
            Spacer(Modifier.height(24.dp))
            Text("同步机制", style = MaterialTheme.typography.titleSmall)
            Text("打开本 App 时会自动同步当前剪贴板内容。由于小米系统限制，App 在后台时无法监听剪贴板，请复制后打开本 App 完成同步。",
                style = MaterialTheme.typography.bodyMedium)
        }

        Spacer(Modifier.height(16.dp))
        Text("提示：为避免 MIUI 后台清理，请在系统设置中允许本应用自启动与后台运行。",
            style = MaterialTheme.typography.bodySmall,
            color = MaterialTheme.colorScheme.onSurfaceVariant)

        Spacer(Modifier.weight(1f))
        TextButton(onClick = onBack) { Text("返回") }
    }

    // 手动检查更新结果弹窗：有新版展示更新日志，无新版提示已最新
    updateResult?.let { latest ->
        if (latest.matches(Regex("^\\d+\\.\\d+\\.\\d+$"))) {
            AlertDialog(
                onDismissRequest = { updateResult = null },
                title = { Text("发现新版本 v$latest") },
                text = {
                    Text(updateChangelog ?: "（暂无更新日志）",
                        style = MaterialTheme.typography.bodyMedium)
                },
                confirmButton = {
                    TextButton(onClick = {
                        updateResult = null
                        UpdateChecker.downloadAndInstall(context)
                    }) { Text("更新") }
                },
                dismissButton = {
                    TextButton(onClick = { updateResult = null }) { Text("稍后") }
                })
        }
    }
}
