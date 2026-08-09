package com.starry.clipboardtool

import android.content.BroadcastReceiver
import android.content.ClipData
import android.content.ClipboardManager
import android.content.Context
import android.content.Intent

/** 调试钩子：adb 广播注入剪贴板内容，触发真实监听链路（联调用）。 */
class DebugClipReceiver : BroadcastReceiver() {
    override fun onReceive(context: Context, intent: Intent) {
        if (intent.action == ACTION_GET_CLIP) {
            val cm = context.getSystemService(Context.CLIPBOARD_SERVICE) as ClipboardManager
            val text = cm.primaryClip?.getItemAt(0)?.text?.toString()
            android.util.Log.d("ClipSync", "debug get clip: ${text?.take(40)}")
            return
        }
        val text = intent.getStringExtra("text")
        android.util.Log.d("ClipSync", "debug receiver got text=${text?.take(40)}")
        if (text == null) return
        val cm = context.getSystemService(Context.CLIPBOARD_SERVICE) as ClipboardManager
        cm.setPrimaryClip(ClipData.newPlainText("debug", text))
        android.util.Log.d("ClipSync", "clip set")
    }

    companion object {
        const val ACTION_GET_CLIP = "com.starry.clipboardtool.DEBUG_GET_CLIP"
    }
}
