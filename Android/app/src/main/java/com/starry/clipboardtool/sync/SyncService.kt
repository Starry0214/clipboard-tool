package com.starry.clipboardtool.sync

import android.content.ClipboardManager
import android.content.Context
import android.os.Handler
import android.os.Looper
import com.starry.clipboardtool.AppState
import com.starry.clipboardtool.data.Entry
import com.starry.clipboardtool.net.SyncClient
import com.starry.clipboardtool.net.SyncMessage
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.launch
import java.io.File

class SyncService(private val context: Context) {
    val mirrors = listOf("https://sync.starry0214.one", "https://107.175.228.83:8081")
    var onStatus: (String) -> Unit = {}
    var onHistoryChanged: () -> Unit = {}
    private var client: SyncClient? = null
    private val main = Handler(Looper.getMainLooper())
    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.IO)

    val isActive: Boolean
        get() = AppState.token.isNotEmpty() && running

    private var running = false

    private fun baseUrl(): String =
        AppState.serverOverride.ifEmpty { mirrors.first() }

    suspend fun login(username: String, password: String, deviceName: String): String? {
        val auth = SyncClient(baseUrl(), "").login(username, password, deviceName)
        return if (auth == null) "登录失败：账号不存在或密码错误"
        else {
            AppState.token = auth.token
            AppState.username = username
            AppState.deviceName = deviceName
            AppState.deviceId = auth.deviceId
            null
        }
    }

    suspend fun register(username: String, password: String, deviceName: String): String? {
        val auth = SyncClient(baseUrl(), "").register(username, password, deviceName)
        return if (auth == null) "注册失败：无法连接服务器"
        else {
            AppState.token = auth.token
            AppState.username = username
            AppState.deviceName = deviceName
            AppState.deviceId = auth.deviceId
            null
        }
    }

    fun logout() {
        stop()
        AppState.token = ""
        AppState.lastSeq = 0
        onStatus("未登录")
    }

    fun start() {
        if (running || AppState.token.isEmpty()) return
        running = true
        onStatus("连接中…")
        val c = SyncClient(baseUrl(), AppState.token)
        client = c
        scope.launch {
            // 回放：只入库不写剪贴板（seq 去重）
            val history = c.fetchHistory(0)
            history?.forEach { m ->
                if (m.seq <= AppState.lastSeq) return@forEach
                applyRemote(m, writeClipboard = false)
                if (m.seq > AppState.lastSeq) AppState.lastSeq = m.seq
            }
            main.post { onHistoryChanged() }
            c.connect(
                onMessage = { m ->
                    if (!running) return@connect
                    if (m.seq > 0 && m.seq <= AppState.lastSeq) return@connect
                    scope.launch {
                        applyRemote(m, writeClipboard = true)
                        if (m.seq > AppState.lastSeq) AppState.lastSeq = m.seq
                        main.post { onHistoryChanged() }
                    }
                },
                onStatus = { s -> main.post { onStatus(s) } })
        }
    }

    fun stop() {
        running = false
        client?.close()
        client = null
    }

    /** 剪贴板监听回调：读剪贴板 → 入库 → 上传。 */
    fun onLocalClip() {
        if (!running) return
        val clipboard = context.getSystemService(Context.CLIPBOARD_SERVICE) as ClipboardManager
        val entry = ClipboardEvents.readClip(context, clipboard, AppState.store) ?: return
        val added = AppState.store.add(entry)
        if (!added) return
        scope.launch {
            upload(entry)
        }
        main.post { onHistoryChanged() }
    }

    private suspend fun upload(entry: Entry) {
        val c = client ?: return
        when (entry.type) {
            "text" -> c.sendClipText(entry.content)
            "image", "file" -> {
                val f = File(entry.content)
                if (!f.exists()) return
                val bytes = f.readBytes()
                val mediaId = c.uploadMedia(bytes) ?: return
                val name = f.name
                c.sendClipMedia(if (entry.type == "image") "clip_image" else "clip_file", mediaId, name, bytes.size.toLong())
            }
        }
    }

    private suspend fun applyRemote(m: SyncMessage, writeClipboard: Boolean) {
        val store = AppState.store
        val c = client ?: return
        when (m.type) {
            "clip_text" -> {
                val text = m.text ?: return
                val entry = Entry(type = "text", content = text, source = "pc",
                    createdAt = if (m.ts > 0) m.ts / 1000 else System.currentTimeMillis() / 1000)
                store.add(entry)
                if (writeClipboard) ClipboardEvents.writeClip(context, entry, store)
            }
            "clip_image" -> {
                val id = m.mediaId?.toLongOrNull() ?: return
                val bytes = c.downloadMedia(id) ?: return
                val path = store.saveImageFile(bytes)
                val entry = Entry(type = "image", content = path, source = "pc",
                    thumb = store.makeThumb(bytes),
                    createdAt = if (m.ts > 0) m.ts / 1000 else System.currentTimeMillis() / 1000)
                if (!store.add(entry)) File(path).delete()
                if (writeClipboard) ClipboardEvents.writeClip(context, entry, store)
            }
            "clip_file" -> {
                val id = m.mediaId?.toLongOrNull() ?: return
                val bytes = c.downloadMedia(id) ?: return
                val path = store.saveRemoteFile(m.name ?: "file.bin", bytes)
                val entry = Entry(type = "file", content = path, source = "pc",
                    createdAt = if (m.ts > 0) m.ts / 1000 else System.currentTimeMillis() / 1000)
                if (!store.add(entry)) File(path).delete()
                if (writeClipboard) ClipboardEvents.writeClip(context, entry, store)
            }
        }
    }
}
