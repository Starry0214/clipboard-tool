package com.starry.clipboardtool

import android.content.Context
import android.content.Intent
import android.net.Uri
import android.os.Build
import androidx.core.content.FileProvider
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import okhttp3.OkHttpClient
import okhttp3.Request
import java.io.File
import java.util.concurrent.TimeUnit

/**
 * 自动更新：从更新服务器（code.starry0214.one / IP 兜底）检查 Android 最新版本、
 * 拉取更新日志、下载 APK，并经 FileProvider + 系统安装器安装。
 * 版本信息与 Windows 分离（version-android.txt / changelog-android.txt）。
 */
object Updater {
    /** 更新服务器双镜像：域名 HTTPS 优先 + IP HTTP 兜底（与同步服务同模式）。 */
    val mirrors = listOf("https://code.starry0214.one/updates", "http://107.175.228.83:8080")

    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.IO)
    private val http = OkHttpClient.Builder()
        .connectTimeout(15, TimeUnit.SECONDS)
        .readTimeout(30, TimeUnit.SECONDS)
        .build()

    /** 当前 App 版本（来自 AndroidManifest versionName）。 */
    fun currentVersion(context: Context): String =
        try {
            context.packageManager.getPackageInfo(context.packageName, 0).versionName ?: "0.0.0"
        } catch (e: Exception) {
            "0.0.0"
        }

    /** 服务器最新版本号；所有镜像不可达或解析失败返回 null。 */
    fun checkLatest(context: Context, onDone: (String?) -> Unit) {
        scope.launch {
            onDone(fetchLatest())
        }
    }

    private suspend fun fetchLatest(): String? {
        for (base in mirrors) {
            try {
                val text = withContext(Dispatchers.IO) {
                    http.newCall(Request.Builder().url("$base/version-android.txt").build())
                        .execute().use { if (it.isSuccessful) it.body?.string() else null }
                }
                val v = text?.trim()?.trimStart('v')
                if (!v.isNullOrEmpty()) return v
            } catch (e: Exception) {
                // 该镜像不可达，换下一个
            }
        }
        return null
    }

    /** 版本比较：latest > current 返回 true。 */
    fun isNewer(latest: String, current: String): Boolean {
        val a = parseVersion(latest) ?: return false
        val b = parseVersion(current) ?: return false
        return compareVersions(a, b) > 0
    }

    private fun compareVersions(a: Triple<Int, Int, Int>, b: Triple<Int, Int, Int>): Int {
        val m = a.first.compareTo(b.first)
        if (m != 0) return m
        val n = a.second.compareTo(b.second)
        if (n != 0) return n
        return a.third.compareTo(b.third)
    }

    private fun parseVersion(v: String): Triple<Int, Int, Int>? {
        val parts = v.trim().trimStart('v').split(".")
        if (parts.size < 2) return null
        val major = parts[0].toIntOrNull() ?: return null
        val minor = parts[1].toIntOrNull() ?: return null
        val patch = parts.getOrNull(2)?.toIntOrNull() ?: 0
        return Triple(major, minor, patch)
    }

    /** 拉取更新日志（changelog-android.txt），返回当前版本之后所有版本的日志文本；失败返回 null。 */
    fun fetchChangelog(onDone: (String?) -> Unit) {
        scope.launch {
            for (base in mirrors) {
                try {
                    val text = withContext(Dispatchers.IO) {
                        http.newCall(Request.Builder().url("$base/changelog-android.txt").build())
                            .execute().use { if (it.isSuccessful) it.body?.string() else null }
                    }
                    if (!text.isNullOrBlank()) {
                        onDone(text.trim())
                        return@launch
                    }
                } catch (e: Exception) {
                    // 换下一个镜像
                }
            }
            onDone(null)
        }
    }

    /** 从全量 changelog 中提取版本号高于 current 的所有版本条目（倒序，最新在前）。 */
    fun changelogForNewer(full: String, current: String): String {
        val blocks = full.split(Regex("(?m)^(?=v\\d+\\.\\d+\\.\\d+)")).map { it.trim() }
            .filter { it.isNotEmpty() }
        val wanted = blocks.filter { block ->
            val m = Regex("^v(\\d+\\.\\d+\\.\\d+)").find(block)
            m != null && isNewer(m.groupValues[1], current)
        }
        // 保留原始顺序（服务器上最新在前则倒序拼接显示也自然）
        return wanted.joinToString("\n\n")
    }

    /** 下载最新 APK 到 filesDir/updates/，返回本地文件；失败返回 null。 */
    fun downloadApk(context: Context, onDone: (File?) -> Unit) {
        scope.launch {
            val dir = File(context.filesDir, "updates").apply { mkdirs() }
            val dest = File(dir, "ClipboardToolApp.apk")
            for (base in mirrors) {
                try {
                    val ok = withContext(Dispatchers.IO) {
                        val req = Request.Builder().url("$base/ClipboardToolApp.apk").build()
                        http.newCall(req).execute().use { resp ->
                            if (!resp.isSuccessful) return@use false
                            dest.outputStream().use { out -> resp.body?.byteStream()?.copyTo(out) }
                            true
                        }
                    }
                    if (ok) {
                        onDone(dest)
                        return@launch
                    }
                } catch (e: Exception) {
                    // 换下一个镜像
                }
            }
            onDone(null)
        }
    }

    /** 拉起系统安装器安装 APK（Android 8+ 需用户授权"安装未知应用"）。 */
    fun install(context: Context, apk: File) {
        val uri: Uri = if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.N) {
            FileProvider.getUriForFile(context, "${context.packageName}.fileprovider", apk)
        } else {
            Uri.fromFile(apk)
        }
        val intent = Intent(Intent.ACTION_VIEW).apply {
            setDataAndType(uri, "application/vnd.android.package-archive")
            addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION)
            addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)
        }
        context.startActivity(intent)
    }
}
