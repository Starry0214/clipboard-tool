package com.starry.clipboardtool.ui

import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.width
import androidx.compose.material3.Button
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Text
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
import kotlinx.coroutines.launch

@Composable
fun LoginScreen(onLoggedIn: () -> Unit) {
    val scope = rememberCoroutineScope()
    var username by remember { mutableStateOf("") }
    var password by remember { mutableStateOf("") }
    var deviceName by remember { mutableStateOf(android.os.Build.MODEL) }
    var server by remember { mutableStateOf(AppState.serverOverride) }
    var status by remember { mutableStateOf("") }
    var busy by remember { mutableStateOf(false) }

    Column(
        modifier = Modifier.fillMaxSize().padding(24.dp),
        horizontalAlignment = Alignment.CenterHorizontally,
        verticalArrangement = Arrangement.Center) {
        Text("剪贴板同步", style = MaterialTheme.typography.headlineMedium)
        Spacer(Modifier.height(24.dp))
        OutlinedTextField(username, { username = it }, label = { Text("账号") },
            singleLine = true, modifier = Modifier.fillMaxWidth())
        Spacer(Modifier.height(8.dp))
        OutlinedTextField(password, { password = it }, label = { Text("密码") },
            singleLine = true, visualTransformation = PasswordVisualTransformation(),
            modifier = Modifier.fillMaxWidth())
        Spacer(Modifier.height(8.dp))
        OutlinedTextField(deviceName, { deviceName = it }, label = { Text("设备名称") },
            singleLine = true, modifier = Modifier.fillMaxWidth())
        Spacer(Modifier.height(8.dp))
        OutlinedTextField(server, { server = it }, label = { Text("服务器地址（可选）") },
            singleLine = true, modifier = Modifier.fillMaxWidth(),
            placeholder = { Text("留空用默认服务器") })
        Spacer(Modifier.height(16.dp))
        if (busy) {
            CircularProgressIndicator()
        } else {
            androidx.compose.foundation.layout.Row {
                Button(onClick = {
                    busy = true; status = ""
                    AppState.serverOverride = server.trim()
                    scope.launch {
                        val err = AppState.syncService?.login(username.trim(), password, deviceName.trim())
                        status = err ?: "已登录"
                        busy = false
                        if (err == null) { AppState.syncService?.start(); onLoggedIn() }
                    }
                }, enabled = username.isNotBlank() && password.length >= 6) { Text("登录") }
                Spacer(Modifier.width(12.dp))
                OutlinedButton(onClick = {
                    busy = true; status = ""
                    AppState.serverOverride = server.trim()
                    scope.launch {
                        val err = AppState.syncService?.register(username.trim(), password, deviceName.trim())
                        status = err ?: "已注册并登录"
                        busy = false
                        if (err == null) { AppState.syncService?.start(); onLoggedIn() }
                    }
                }, enabled = username.length >= 4 && password.length >= 6) { Text("注册") }
            }
        }
        Spacer(Modifier.height(12.dp))
        Text(status, color = MaterialTheme.colorScheme.error)
        Text("开启同步后，本机复制的内容会同步到电脑，电脑复制的内容会出现在这里并自动写入剪贴板。",
            style = MaterialTheme.typography.bodySmall,
            color = MaterialTheme.colorScheme.onSurfaceVariant,
            modifier = Modifier.padding(top = 16.dp))
    }
}
