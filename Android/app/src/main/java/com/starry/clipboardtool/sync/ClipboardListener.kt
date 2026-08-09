package com.starry.clipboardtool.sync

import android.accessibilityservice.AccessibilityService
import android.content.ClipboardManager
import android.content.Context
import android.os.Handler
import android.os.Looper
import android.view.accessibility.AccessibilityEvent
import com.starry.clipboardtool.AppState

/**
 * 无障碍服务：监听剪贴板变化（Android 10+ 后台读剪贴板豁免）。
 * 小米系统后台不派发剪贴板回调（即使服务已绑定），故以 3 秒轮询为兜底；
 * 前台回调与轮询并存，内容哈希去重防重复。无常驻通知。
 */
class ClipboardListener : AccessibilityService() {

    private val clipboardListener = ClipboardManager.OnPrimaryClipChangedListener { onClipChanged() }
    private val pollHandler = Handler(Looper.getMainLooper())
    private var lastSeenHash: String? = null

    private val pollRunnable = object : Runnable {
        override fun run() {
            pollClip()
            pollHandler.postDelayed(this, POLL_INTERVAL_MS)
        }
    }

    private fun onClipChanged() {
        val sync = AppState.syncService
        android.util.Log.d("ClipSync", "clip changed, sync=${sync != null}, active=${sync?.isActive}")
        if (sync == null || !sync.isActive) return
        val text = clipText()
        android.util.Log.d("ClipSync", "clip text=${text?.take(30)}")
        if (text != null) {
            lastSeenHash = ClipboardEvents.contentHash(text)
            if (skipSelfWritten(text)) return
        }
        sync.onLocalClip()
    }

    /** 轮询兜底：后台时系统不派发回调，定时主动读剪贴板。 */
    private fun pollClip() {
        val sync = AppState.syncService
        if (sync == null || !sync.isActive) return
        val text = clipText()
        android.util.Log.d("ClipSync", "poll read: ${text?.take(20)} lastSeen=${lastSeenHash?.take(8)}")
        if (text == null) return
        val h = ClipboardEvents.contentHash(text)
        if (h == lastSeenHash) return
        lastSeenHash = h
        if (skipSelfWritten(text)) return
        android.util.Log.d("ClipSync", "poll detect clip: ${text.take(30)}")
        sync.onLocalClip()
    }

    private fun clipText(): String? =
        getSystemService(ClipboardManager::class.java)
            .primaryClip?.getItemAt(0)?.text?.toString()

    /** 防回环：刚由本 App 写入的剪贴板内容跳过（命中则清除标记）。 */
    private fun skipSelfWritten(text: String): Boolean {
        if (text.isEmpty()) return true
        val h = ClipboardEvents.contentHash(text)
        if (h == ClipboardEvents.suppressHash) {
            ClipboardEvents.suppressHash = null
            return true
        }
        return false
    }

    override fun onServiceConnected() {
        super.onServiceConnected()
        android.util.Log.d("ClipSync", "accessibility service connected")
        // 初始化基线：当前剪贴板内容视为已见，避免轮询误报
        lastSeenHash = clipText()?.let { ClipboardEvents.contentHash(it) }
        getSystemService(ClipboardManager::class.java)
            .addPrimaryClipChangedListener(clipboardListener)
        pollHandler.post(pollRunnable)
        // App 打开/回前台：同步一次当前剪贴板（小米后台无法监听，打开即补同步；去重兜底）
        val sync = AppState.syncService
        if (sync != null && sync.isActive) sync.onLocalClip()
    }

    override fun onAccessibilityEvent(event: AccessibilityEvent?) {}

    override fun onInterrupt() {}

    override fun onDestroy() {
        pollHandler.removeCallbacks(pollRunnable)
        runCatching {
            getSystemService(ClipboardManager::class.java)
                .removePrimaryClipChangedListener(clipboardListener)
        }
        super.onDestroy()
    }

    companion object {
        private const val POLL_INTERVAL_MS = 3000L

        fun isEnabled(context: Context): Boolean {
            val expected = context.packageName + "/" + ClipboardListener::class.java.name
            val enabled = android.provider.Settings.Secure.getString(
                context.contentResolver, android.provider.Settings.Secure.ENABLED_ACCESSIBILITY_SERVICES)
            return enabled?.split(':')?.contains(expected) == true
        }
    }
}
