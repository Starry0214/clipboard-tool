package com.starry.clipboardtool

import android.content.Context
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.platform.LocalContext

/**
 * 自动更新检查与安装编排：检查最新版 → 拉取更新日志 → 弹窗确认 → 下载 → 拉起系统安装器。
 * 自动检查节流（同一天只提示一次），手动检查（设置页按钮）不受限。
 */
object UpdateChecker {
    private const val KEY_LAST_PROMPT = "UpdateLastPromptDate"

    /** 静默检查：有新版且今天未提示过 → 弹窗。无新版/网络失败静默。 */
    fun checkSilent(context: Context, onShowDialog: (latest: String, changelog: String?) -> Unit) {
        val today = java.text.SimpleDateFormat("yyyy-MM-dd", java.util.Locale.US).format(java.util.Date())
        val prefs = context.getSharedPreferences("update", Context.MODE_PRIVATE)
        if (prefs.getString(KEY_LAST_PROMPT, "") == today) return
        prefs.edit().putString(KEY_LAST_PROMPT, today).apply()

        Updater.checkLatest(context) { latest ->
            if (latest == null) return@checkLatest
            val current = Updater.currentVersion(context)
            if (!Updater.isNewer(latest, current)) return@checkLatest
            Updater.fetchChangelog { changelog ->
                val relevant = if (changelog == null) null else Updater.changelogForNewer(changelog, current)
                onShowDialog(latest, relevant)
            }
        }
    }

    /** 手动检查（设置页按钮）：始终提示结果。 */
    fun checkManual(context: Context, onResult: (String, String?) -> Unit) {
        Updater.checkLatest(context) { latest ->
            val current = Updater.currentVersion(context)
            if (latest == null) {
                onResult("检查更新失败：无法连接更新服务器", null)
                return@checkLatest
            }
            if (!Updater.isNewer(latest, current)) {
                onResult("当前已是最新版本（v$current）", null)
                return@checkLatest
            }
            Updater.fetchChangelog { changelog ->
                val relevant = if (changelog == null) null else Updater.changelogForNewer(changelog, current)
                onResult(latest, relevant)
            }
        }
    }

    /** 确认后下载并安装 APK。 */
    fun downloadAndInstall(context: Context) {
        Updater.downloadApk(context) { apk ->
            if (apk != null && apk.exists()) Updater.install(context, apk)
        }
    }
}

/** 更新提示弹窗：显示最新版本号与更新日志（全部相关版本），确认后下载安装。 */
@Composable
fun UpdateDialog(latest: String, changelog: String?, onDismiss: () -> Unit) {
    val context = LocalContext.current
    var downloading by remember { mutableStateOf(false) }
    AlertDialog(
        onDismissRequest = onDismiss,
        title = { Text("发现新版本 v$latest") },
        text = {
            val body = buildString {
                if (changelog != null) append(changelog)
                if (downloading) {
                    if (isNotEmpty()) append("\n\n")
                    append("正在下载…")
                }
            }
            Text(if (body.isEmpty()) "（暂无更新日志）" else body,
                style = MaterialTheme.typography.bodyMedium)
        },
        confirmButton = {
            TextButton(onClick = {
                downloading = true
                UpdateChecker.downloadAndInstall(context)
            }) { Text("更新") }
        },
        dismissButton = {
            TextButton(onClick = onDismiss) { Text("稍后") }
        })
}
