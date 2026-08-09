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
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import java.io.File

class SyncService(private val context: Context) {
    val mirrors = listOf("https://code.starry0214.one/sync", "https://107.175.228.83:8081")
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

    suspend fun login(username: String, password: String, deviceName: String): String? =
        withContext(Dispatchers.IO) {
            val auth = SyncClient(baseUrl(), "").login(username, password, deviceName)
            if (auth == null) "登录失败：账号不存在或密码错误"
            else {
                AppState.token = auth.token
                AppState.username = username
                AppState.deviceName = deviceName
                AppState.deviceId = auth.deviceId
                null
            }
        }

    suspend fun register(username: String, password: String, deviceName: String): String? =
        withContext(Dispatchers.IO) {
            val auth = SyncClient(baseUrl(), "").register(username, password, deviceName)
            if (auth == null) "注册失败：无法连接服务器"
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
            // 回放：只入库不写剪贴板（seq 去重）；delete 为彻底删除，任何同步都应用
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

    /** 手动同步服务器到本地：全量拉取最近 7 天入库（不按 seq 过滤——本地删除后可从服务器找回；store 哈希去重兜底）。 */
    fun syncFromServer(onDone: (String) -> Unit) {
        val c = client
        if (c == null) {
            onDone("未连接，无法同步")
            return
        }
        scope.launch {
            var n = 0
            var maxSeq = AppState.lastSeq
            val history = c.fetchHistory(0)
            if (history == null) {
                main.post { onDone("同步失败：无法连接服务器") }
                return@launch
            }
            history.forEach { m ->
                applyRemote(m, writeClipboard = false)
                if (m.seq > maxSeq) maxSeq = m.seq
                n++
            }
            AppState.lastSeq = maxSeq
            main.post {
                onHistoryChanged()
                onDone("同步完成（处理 $n 条）")
            }
        }
    }

    /** 剪贴板监听回调：读剪贴板 → 入库 → 上传（去重与上传解耦，已入库未上传的内容可补传）。 */
    fun onLocalClip() {
        android.util.Log.d("ClipSync", "onLocalClip running=$running token=${AppState.token.isNotEmpty()}")
        if (!running) return
        val clipboard = context.getSystemService(Context.CLIPBOARD_SERVICE) as ClipboardManager
        val entry = ClipboardEvents.readClip(context, clipboard, AppState.store)
        android.util.Log.d("ClipSync", "readClip -> ${entry?.type} ${entry?.content?.take(30)}")
        if (entry == null) return
        // 同内容只同步一次（持久化）：删除条目后剪贴板内容仍在时，获焦补同步不会把它加回
        val h = when (entry.type) {
            "text" -> ClipboardEvents.contentHash(entry.content)
            "image" -> AppState.store.hashForSync(entry)
            else -> null
        }
        if (h != null) {
            if (h == lastSyncedHash) {
                android.util.Log.d("ClipSync", "skip: same as last synced")
                return
            }
            lastSyncedHash = h
        }
        // 防回环：App 自己写入剪贴板的内容不上传
        if (entry.type == "text" && ClipboardEvents.suppressHash != null &&
            ClipboardEvents.contentHash(entry.content) == ClipboardEvents.suppressHash
        ) {
            ClipboardEvents.suppressHash = null
            return
        }
        AppState.store.add(entry)
        scope.launch {
            upload(entry)
        }
        main.post { onHistoryChanged() }
    }

    /** App 获焦（打开/回前台）：上传手机当前剪贴板 + 增量拉取服务器新消息（补 WS 离线期间错过的电脑端内容）。
     * delete 只来自彻底删除，任何同步（自动/手动）都应用。 */
    fun onAppForeground() {
        onLocalClip()
        val c = client ?: return
        scope.launch {
            val history = c.fetchHistory(AppState.lastSeq) ?: return@launch
            history.forEach { m ->
                if (m.seq > 0 && m.seq <= AppState.lastSeq) return@forEach
                applyRemote(m, writeClipboard = true)
                if (m.seq > AppState.lastSeq) AppState.lastSeq = m.seq
            }
            main.post { onHistoryChanged() }
        }
    }

    /** 最近一次同步的内容哈希（持久化，删除后防获焦补同步加回）。 */
    private var lastSyncedHash: String?
        get() = AppState.prefs.getString("LastSyncedHash", null)
        set(v) = AppState.prefs.edit().putString("LastSyncedHash", v).apply()

    private suspend fun upload(entry: Entry) {
        val c = client ?: return
        // 手机端只同步文字；图片/文件仅存本地历史（用户场景：电脑端复制图片/文件同步过来）
        if (entry.type != "text") return
        // 启动窗口期 WS 未连上时延迟重试，避免入库但丢上传
        repeat(3) { attempt ->
            if (c.sendClipText(entry.content)) return
            delay(3000)
        }
    }

    /** 删除条目：本地删除或彻底删除（同步删除服务器消息并广播到其他设备）。 */
    fun deleteEntry(entry: Entry, fully: Boolean) {
        val store = AppState.store
        if (fully) {
            val hash = store.hashForSync(entry)
            store.deleteByHash(hash)
            scope.launch {
                val c = client ?: return@launch
                repeat(3) { attempt ->
                    if (c.sendDelete(hash)) return@launch
                    delay(3000)
                }
            }
        } else {
            store.delete(entry.id)
        }
        main.post { onHistoryChanged() }
    }

    private suspend fun applyRemote(m: SyncMessage, writeClipboard: Boolean) {
        android.util.Log.d("ClipSync", "applyRemote ${m.type} seq=${m.seq} writeClip=$writeClipboard")
        val store = AppState.store
        val c = client ?: return
        when (m.type) {
            "delete" -> {
                val hash = m.hash ?: return
                store.deleteByHash(hash)
                main.post { onHistoryChanged() }
            }
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
