package com.starry.clipboardtool.ui

import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
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
import androidx.compose.ui.unit.dp
import com.starry.clipboardtool.AppState

@Composable
fun SettingsScreen(onBack: () -> Unit, onLogout: () -> Unit) {
    var syncResult by remember { mutableStateOf("") }

    Column(modifier = Modifier.fillMaxSize().padding(16.dp)) {
        Text("设置", style = MaterialTheme.typography.headlineSmall)
        Spacer(Modifier.height(16.dp))

        Text("账号", style = MaterialTheme.typography.titleSmall)
        Text("已登录：${AppState.username}（${AppState.deviceName}）",
            style = MaterialTheme.typography.bodyMedium)
        Spacer(Modifier.height(8.dp))
        Button(onClick = { AppState.syncService?.logout(); onLogout() }) { Text("退出登录") }

        Spacer(Modifier.height(24.dp))
        Text("数据", style = MaterialTheme.typography.titleSmall)
        Button(onClick = {
            syncResult = "同步中…"
            AppState.syncService?.syncFromServer { syncResult = it }
        }) { Text("同步服务器到本地") }
        if (syncResult.isNotEmpty()) {
            Text(syncResult, style = MaterialTheme.typography.bodyMedium)
        }
        Text("将服务器上最近 7 天的内容拉取到本机历史（重复内容自动跳过）。",
            style = MaterialTheme.typography.bodySmall,
            color = MaterialTheme.colorScheme.onSurfaceVariant)

        Spacer(Modifier.height(24.dp))
        Text("同步机制", style = MaterialTheme.typography.titleSmall)
        Text("打开本 App 时会自动同步当前剪贴板内容。由于小米系统限制，App 在后台时无法监听剪贴板，请复制后打开本 App 完成同步。",
            style = MaterialTheme.typography.bodyMedium)

        Spacer(Modifier.height(24.dp))
        Text("同步状态：${if (AppState.token.isNotEmpty()) "已登录" else "未登录"}",
            style = MaterialTheme.typography.bodyMedium)

        Spacer(Modifier.height(16.dp))
        Text("提示：为避免 MIUI 后台清理，请在系统设置中允许本应用自启动与后台运行。",
            style = MaterialTheme.typography.bodySmall,
            color = MaterialTheme.colorScheme.onSurfaceVariant)

        Spacer(Modifier.weight(1f))
        TextButton(onClick = onBack) { Text("返回") }
    }
}
