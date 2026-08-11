package com.starry.clipboardtool.ui

import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.Button
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.text.input.PasswordVisualTransformation
import androidx.compose.ui.unit.dp
import com.starry.clipboardtool.AppState
import com.starry.clipboardtool.ui.theme.ContentPasteIcon
import kotlinx.coroutines.launch

@Composable
fun LoginScreen(onLoggedIn: () -> Unit, onSkip: () -> Unit = {}) {
    val scope = rememberCoroutineScope()
    var username by remember { mutableStateOf("") }
    var password by remember { mutableStateOf("") }
    var deviceName by remember { mutableStateOf(android.os.Build.MODEL) }
    var status by remember { mutableStateOf("") }
    var busy by remember { mutableStateOf(false) }

    Column(
        modifier = Modifier.fillMaxSize().padding(24.dp),
        horizontalAlignment = Alignment.CenterHorizontally,
        verticalArrangement = Arrangement.Center) {
        Surface(shape = RoundedCornerShape(24.dp),
            color = MaterialTheme.colorScheme.primaryContainer) {
            Icon(ContentPasteIcon, contentDescription = null,
                tint = MaterialTheme.colorScheme.primary,
                modifier = Modifier.padding(16.dp).size(40.dp))
        }
        Spacer(Modifier.height(16.dp))
        Text("剪贴板同步", style = MaterialTheme.typography.headlineSmall)
        Spacer(Modifier.height(4.dp))
        Text("手机与电脑之间无缝传递剪贴内容",
            style = MaterialTheme.typography.bodySmall,
            color = MaterialTheme.colorScheme.onSurfaceVariant)
        Spacer(Modifier.height(24.dp))
        Surface(shape = RoundedCornerShape(20.dp),
            color = MaterialTheme.colorScheme.surfaceContainer,
            modifier = Modifier.fillMaxWidth()) {
            Column(modifier = Modifier.padding(16.dp)) {
                OutlinedTextField(username, { username = it }, label = { Text("账号") },
                    singleLine = true, modifier = Modifier.fillMaxWidth())
                Spacer(Modifier.height(8.dp))
                OutlinedTextField(password, { password = it }, label = { Text("密码") },
                    singleLine = true, visualTransformation = PasswordVisualTransformation(),
                    modifier = Modifier.fillMaxWidth())
                Spacer(Modifier.height(8.dp))
                OutlinedTextField(deviceName, { deviceName = it }, label = { Text("设备名称") },
                    singleLine = true, modifier = Modifier.fillMaxWidth())
                Spacer(Modifier.height(16.dp))
                if (busy) {
                    CircularProgressIndicator(modifier = Modifier.align(Alignment.CenterHorizontally))
                } else {
                    Row(modifier = Modifier.fillMaxWidth(),
                        horizontalArrangement = Arrangement.SpaceEvenly) {
                        Button(onClick = {
                            busy = true; status = ""
                            scope.launch {
                                val err = AppState.syncService?.login(username.trim(), password, deviceName.trim())
                                status = err ?: "已登录"
                                busy = false
                                if (err == null) { AppState.syncService?.start(); onLoggedIn() }
                            }
                        }, enabled = username.isNotBlank() && password.length >= 6) { Text("登录") }
                        OutlinedButton(onClick = {
                            busy = true; status = ""
                            scope.launch {
                                val err = AppState.syncService?.register(username.trim(), password, deviceName.trim())
                                status = err ?: "已注册并登录"
                                busy = false
                                if (err == null) { AppState.syncService?.start(); onLoggedIn() }
                            }
                        }, enabled = username.length >= 4 && password.length >= 6) { Text("注册") }
                    }
                }
                if (status.isNotEmpty()) {
                    Spacer(Modifier.height(12.dp))
                    Text(status, color = MaterialTheme.colorScheme.error,
                        modifier = Modifier.align(Alignment.CenterHorizontally))
                }
            }
        }
        Spacer(Modifier.height(12.dp))
        Text("开启同步后，本机复制的内容会同步到电脑，电脑复制的内容会出现在这里并自动写入剪贴板。",
            style = MaterialTheme.typography.bodySmall,
            color = MaterialTheme.colorScheme.onSurfaceVariant)
        Spacer(Modifier.height(16.dp))
        TextButton(onClick = onSkip) { Text("跳过，本地使用") }
    }
}
