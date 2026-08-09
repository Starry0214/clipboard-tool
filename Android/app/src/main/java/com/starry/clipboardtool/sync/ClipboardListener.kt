package com.starry.clipboardtool.sync

import android.accessibilityservice.AccessibilityService
import android.content.ClipboardManager
import android.content.Context
import android.view.accessibility.AccessibilityEvent
import com.starry.clipboardtool.AppState

/** 无障碍服务：后台监听剪贴板变化（Android 10+ 后台读剪贴板豁免）。无常驻通知。 */
class ClipboardListener : AccessibilityService() {

    private val clipboardListener = ClipboardManager.OnPrimaryClipChangedListener { onClipChanged() }

    private fun onClipChanged() {
        val sync = AppState.syncService
        android.util.Log.d("ClipSync", "clip changed, sync=${sync != null}, active=${sync?.isActive}")
        if (sync == null || !sync.isActive) return
        // 防回环：刚由本 App 写入的剪贴板内容跳过（内容哈希命中则跳过并清除标记）
        val clip = getSystemService(ClipboardManager::class.java)
        val text = clip.primaryClip?.getItemAt(0)?.text?.toString()
        android.util.Log.d("ClipSync", "clip text=${text?.take(30)}")
        if (text != null) {
            val h = ClipboardEvents.contentHash(text)
            if (h == ClipboardEvents.suppressHash) {
                ClipboardEvents.suppressHash = null
                return
            }
        }
        sync.onLocalClip()
    }

    override fun onServiceConnected() {
        super.onServiceConnected()
        android.util.Log.d("ClipSync", "accessibility service connected")
        getSystemService(ClipboardManager::class.java)
            .addPrimaryClipChangedListener(clipboardListener)
    }

    override fun onAccessibilityEvent(event: AccessibilityEvent?) {}

    override fun onInterrupt() {}

    override fun onDestroy() {
        runCatching {
            getSystemService(ClipboardManager::class.java)
                .removePrimaryClipChangedListener(clipboardListener)
        }
        super.onDestroy()
    }

    companion object {
        fun isEnabled(context: Context): Boolean {
            val expected = context.packageName + "/" + ClipboardListener::class.java.name
            val enabled = android.provider.Settings.Secure.getString(
                context.contentResolver, android.provider.Settings.Secure.ENABLED_ACCESSIBILITY_SERVICES)
            return enabled?.split(':')?.contains(expected) == true
        }
    }
}
