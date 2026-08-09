package com.starry.clipboardtool.ui

import android.content.Intent
import android.provider.Settings
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.material3.Button
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedTextField
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
import com.starry.clipboardtool.sync.ClipboardListener

@Composable
fun SettingsScreen(onBack: () -> Unit, onLogout: () -> Unit) {
    val context = LocalContext.current
    var accessEnabled by remember { mutableStateOf(ClipboardListener.isEnabled(context)) }

    Column(modifier = Modifier.fillMaxSize().padding(16.dp)) {
        Text("设置", style = MaterialTheme.typography.headlineSmall)
        Spacer(Modifier.height(16.dp))

        Text("账号", style = MaterialTheme.typography.titleSmall)
        Text("已登录：${AppState.username}（${AppState.deviceName}）",
            style = MaterialTheme.typography.bodyMedium)
        Spacer(Modifier.height(8.dp))
        Button(onClick = { AppState.syncService?.logout(); onLogout() }) { Text("退出登录") }

        Spacer(Modifier.height(24.dp))
        Text("无障碍服务", style = MaterialTheme.typography.titleSmall)
        Text(if (accessEnabled) "已开启：剪贴板监听运行中" else "未开启：请开启以监听剪贴板",
            style = MaterialTheme.typography.bodyMedium)
        Spacer(Modifier.height(8.dp))
        Button(onClick = {
            context.startActivity(Intent(Settings.ACTION_ACCESSIBILITY_SETTINGS))
            accessEnabled = ClipboardListener.isEnabled(context)
        }) { Text(if (accessEnabled) "重新检查" else "去开启") }

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
