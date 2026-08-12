package com.starry.clipboardtool

import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.app.Service
import android.content.Context
import android.content.Intent
import android.os.Build
import android.os.Handler
import android.os.IBinder
import android.os.Looper
import androidx.core.app.NotificationCompat
import androidx.core.content.ContextCompat
import com.starry.clipboardtool.data.Entry
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import kotlinx.coroutines.launch

/** 分享同步前台服务：分享时上传未完成则驻留后台继续，通知栏显示实时进度。 */
class ShareUploadService : Service() {
    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.IO)
    private val main = Handler(Looper.getMainLooper())

    companion object {
        private const val CHANNEL_ID = "share_upload"
        private const val NOTIF_ID = 1001
        private const val EXTRA_TYPES = "types"
        private const val EXTRA_CONTENTS = "contents"

        /** 启动后台同步服务（条目已入库，content 为本地文件路径）。 */
        fun start(context: Context, entries: List<Entry>) {
            val intent = Intent(context, ShareUploadService::class.java).apply {
                putExtra(EXTRA_TYPES, entries.map { it.type }.toTypedArray())
                putExtra(EXTRA_CONTENTS, entries.map { it.content }.toTypedArray())
            }
            ContextCompat.startForegroundService(context, intent)
        }
    }

    override fun onCreate() {
        super.onCreate()
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
            val channel = NotificationChannel(
                CHANNEL_ID, "分享同步", NotificationManager.IMPORTANCE_LOW)
            getSystemService(NotificationManager::class.java).createNotificationChannel(channel)
        }
    }

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        val types = intent?.getStringArrayExtra(EXTRA_TYPES) ?: emptyArray()
        val contents = intent?.getStringArrayExtra(EXTRA_CONTENTS) ?: emptyArray()
        if (types.isEmpty() || types.size != contents.size) {
            stopSelf()
            return START_NOT_STICKY
        }
        val entries = types.indices.map { Entry(type = types[it], content = contents[it], source = "local") }
        startForeground(NOTIF_ID, buildNotification(0f, "正在同步到电脑端…"))
        scope.launch {
            val ok = AppState.syncService?.uploadEntries(entries) { p ->
                main.post { updateNotification(p, "正在同步到电脑端…") }
            } ?: false
            main.post {
                if (ok) updateNotification(1f, "已同步到电脑端", done = true)
                else updateNotification(0f, "同步失败，可打开 App 重试", done = true)
                Handler(Looper.getMainLooper()).postDelayed({ stopSelf() }, 4_000)
            }
        }
        return START_NOT_STICKY
    }

    override fun onBind(intent: Intent?): IBinder? = null

    private fun notifyManager() = getSystemService(NotificationManager::class.java)

    private fun buildNotification(progress: Float, text: String, done: Boolean = false): Notification {
        val openApp = PendingIntent.getActivity(this, 0,
            Intent(this, MainActivity::class.java), PendingIntent.FLAG_IMMUTABLE)
        val b = NotificationCompat.Builder(this, CHANNEL_ID)
            .setSmallIcon(android.R.drawable.stat_sys_upload)
            .setContentTitle("剪贴板助手")
            .setContentText(text)
            .setContentIntent(openApp)
            .setOngoing(!done)
            .setOnlyAlertOnce(true)
        if (!done) b.setProgress(100, (progress * 100).toInt(), false)
        else b.setProgress(0, 0, false)
        return b.build()
    }

    private fun updateNotification(progress: Float, text: String, done: Boolean = false) {
        notifyManager().notify(NOTIF_ID, buildNotification(progress, text, done))
    }

    override fun onDestroy() {
        scope.coroutineContext.cancel()
        super.onDestroy()
    }
}
