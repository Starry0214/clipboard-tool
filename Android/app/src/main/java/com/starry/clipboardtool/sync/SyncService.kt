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
import kotlinx.coroutines.Job
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.delay
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import java.io.File
import java.util.concurrent.atomic.AtomicLong

class SyncService(private val context: Context) {
    val mirrors = listOf("https://code.starry0214.one/sync", "https://107.175.228.83:8081")
    var onStatus: (String) -> Unit = {}
    var onHistoryChanged: () -> Unit = {}
    private var client: SyncClient? = null
    /** WS 断开期间 HTTP 增量轮询兜底任务。 */
    private var pollingJob: Job? = null
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
            // 先建 WS 连接再回放历史：connect() 内部自行协程，立即返回；
            // 若先 fetchHistory（HTTP 在慢网络可能 10s+）会阻塞 connect 调用，导致冷启动分享的 waitWsReady 等不到握手
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
            // 回放：只入库不写剪贴板（seq 去重）；delete 为彻底删除，任何同步都应用
            val history = c.fetchHistory(0)
            history?.forEach { m ->
                if (m.seq <= AppState.lastSeq) return@forEach
                applyRemote(m, writeClipboard = false)
                if (m.seq > AppState.lastSeq) AppState.lastSeq = m.seq
            }
            main.post { onHistoryChanged() }
        }
        // WS 断开期间 HTTP 增量轮询兜底：境外中继对长连接不稳，WS 可能长时间连不上，
        // 轮询保证消息不因 WS 断线而丢失（WS 恢复后实时推送接管，轮询自动跳过）
        pollingJob?.cancel()
        pollingJob = scope.launch {
            while (isActive) {
                delay(30_000)
                val cl = client ?: continue
                if (cl.isConnected()) continue
                val hist = cl.fetchHistory(AppState.lastSeq) ?: continue
                hist.forEach { m ->
                    if (m.seq > 0 && m.seq <= AppState.lastSeq) return@forEach
                    applyRemote(m, writeClipboard = false)
                    if (m.seq > AppState.lastSeq) AppState.lastSeq = m.seq
                }
                main.post { onHistoryChanged() }
            }
        }
    }

    fun stop() {
        running = false
        pollingJob?.cancel()
        pollingJob = null
        client?.close()
        client = null
    }

    /** 手动同步服务器到本地：全量拉取最近 7 天入库（不按 seq 过滤——本地删除后可从服务器找回；store 哈希去重兜底）。
     * onProgress(doneBytes, totalBytes) 按内容字节累计推进（main 线程回调），文本按内容字节、图片/文件按 media 大小。 */
    fun syncFromServer(onDone: (String) -> Unit, onProgress: (Long, Long) -> Unit = { _, _ -> }) {
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
            val totalBytes = history.sumOf { msgBytes(it) }.coerceAtLeast(1)
            var doneBytes = 0L
            history.forEach { m ->
                applyRemote(m, writeClipboard = false)
                if (m.seq > maxSeq) maxSeq = m.seq
                n++
                doneBytes += msgBytes(m)
                main.post { onProgress(doneBytes, totalBytes) }
            }
            AppState.lastSeq = maxSeq
            main.post {
                onHistoryChanged()
                onProgress(totalBytes, totalBytes)
                onDone("同步完成（处理 $n 条）")
            }
        }
    }

    /** 条目同步字节量：文本按内容字节，图片/文件按服务器 media 大小（下载量），delete 等为 0。 */
    private fun msgBytes(m: SyncMessage): Long = when (m.type) {
        "clip_text" -> m.text?.toByteArray()?.size?.toLong() ?: 0L
        "clip_image", "clip_file" -> m.size
        else -> 0L
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
            "image", "file" -> AppState.store.hashForSync(entry) // 按文件内容字节，与路径无关
            else -> null
        }
        if (h != null && h == lastSyncedHash) {
            android.util.Log.d("ClipSync", "skip: same as last synced")
            return
        }
        // 剪贴板内容与上次获焦时相同则跳过：弹窗开合/切后台再回来会触发获焦，
        // 若每次都对残留内容 add→去重 touch，条目时间被反复刷新并顶到列表最前（"列表重排"现象）
        if (h != null && h == lastClipHash) {
            // 图片/文件每次 readClip 都会落盘新文件，跳过时清理避免残留
            if (entry.type == "image" || entry.type == "file") File(entry.content).delete()
            android.util.Log.d("ClipSync", "skip: clipboard unchanged")
            return
        }
        lastClipHash = h
        // 防回环：App 自己写入剪贴板的内容不上传
        if (entry.type == "text" && ClipboardEvents.suppressHash != null &&
            ClipboardEvents.contentHash(entry.content) == ClipboardEvents.suppressHash
        ) {
            ClipboardEvents.suppressHash = null
            return
        }
        AppState.store.add(entry)
        if (entry.type == "text") {
            scope.launch {
                if (upload(entry)) {
                    // 上传成功才记录，失败不标记——WS 未就绪/断线时获焦重试不会因 hash 去重被永久跳过
                    lastSyncedHash = h
                } else {
                    android.util.Log.w("ClipSync", "upload failed, will retry on next focus")
                }
            }
        } else if (h != null) {
            // 图片/文件不上传服务器，入库即视为已处理（防删除后剪贴板内容仍在时获焦加回）
            lastSyncedHash = h
        }
        main.post { onHistoryChanged() }
    }

    /** 分享接收：入库去重，返回实际新增的条目；重复的图片/文件清理新落盘文件避免孤儿。 */
    fun addShareEntries(entries: List<Entry>): List<Entry> {
        val added = entries.filter { entry ->
            if (AppState.store.add(entry)) true
            else {
                if (entry.type == "image" || entry.type == "file") File(entry.content).delete()
                false
            }
        }
        if (added.isNotEmpty()) main.post { onHistoryChanged() }
        return added
    }

    /** 等待 WS 握手（≤10s）后逐个上传条目；分享后台 Service 调用。
     * onProgress 可能来自 OkHttp 写请求体线程，调用方需自行切主线程。 */
    suspend fun uploadEntries(entries: List<Entry>, onProgress: (Float) -> Unit = {}): Boolean {
        if (!waitWsReady(10_000)) return false
        var okAll = true
        val totalBytes = entries.sumOf { entryBytes(it) }.coerceAtLeast(1)
        val doneBytes = AtomicLong(0)
        for (entry in entries) {
            val ok = if (entry.type == "text") upload(entry)
            else uploadMediaEntry(entry) { up, _ ->
                val d = doneBytes.get() + up
                onProgress((d.toFloat() / totalBytes).coerceIn(0f, 1f))
            }
            if (ok) doneBytes.addAndGet(entryBytes(entry)) else okAll = false
            onProgress((doneBytes.get().toFloat() / totalBytes).coerceIn(0f, 1f))
        }
        return okAll
    }

    /** 条目上传字节量（文本按内容字节，图片/文件按文件大小）。 */
    private fun entryBytes(entry: Entry): Long =
        if (entry.type == "text") entry.content.toByteArray().size.toLong()
        else File(entry.content).takeIf { it.exists() }?.length() ?: 0L

    /** 阻塞等待 WS 完成握手（最多 timeoutMs 毫秒），期间每 100ms 轮询一次。 */
    private fun waitWsReady(timeoutMs: Long): Boolean {
        val deadline = System.currentTimeMillis() + timeoutMs
        while (System.currentTimeMillis() < deadline) {
            val c = client
            if (c != null && c.isConnected()) return true
            Thread.sleep(100)
        }
        return false
    }

    /** 上传图片/文件条目：字节存服务器媒体库 → WS 广播 clip_image/clip_file（Windows 端下载媒体入库粘贴）。
     * onProgress(uploaded,total) 转发自 OkHttp 写请求体回调（用于分享进度条实时显示）。 */
    private suspend fun uploadMediaEntry(entry: Entry,
                                         onProgress: (Long, Long) -> Unit = { _, _ -> }): Boolean {
        val c = client ?: return false
        if (entry.type != "image" && entry.type != "file") return false
        val f = File(entry.content)
        if (!f.exists()) return false
        val bytes = f.readBytes()
        repeat(3) { attempt ->
            val mediaId = c.uploadMedia(bytes, onProgress)
            if (mediaId == null) {
                android.util.Log.w("ClipSync", "uploadMedia attempt ${attempt + 1} failed")
                delay(3000)
                return@repeat
            }
            val name = if (entry.type == "image") "剪贴板_${entry.id}.png"
            else f.name
            if (c.sendClipMedia(if (entry.type == "image") "clip_image" else "clip_file",
                    mediaId, name, bytes.size.toLong())) return true
            delay(3000)
        }
        return false
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

    /** 上次获焦时剪贴板内容的哈希（内存去重：剪贴板未变则跳过，防弹窗/回前台反复 touch 移顶）。 */
    private var lastClipHash: String? = null

    /** 上传手机剪贴板文本到服务器；WS 未连上时延迟重试 3 次，全部失败返回 false（调用方不标记已同步）。 */
    private suspend fun upload(entry: Entry): Boolean {
        val c = client ?: return false
        // 手机端只同步文字；图片/文件仅存本地历史（用户场景：电脑端复制图片/文件同步过来）
        if (entry.type != "text") return false
        // 启动窗口期 WS 未连上时延迟重试，避免入库但丢上传
        repeat(3) { attempt ->
            if (c.sendClipText(entry.content)) return true
            android.util.Log.w("ClipSync", "upload attempt ${attempt + 1} failed (ws not ready?)")
            delay(3000)
        }
        return false
    }

    /** 删除条目：本地删除或彻底删除（本地按 id 删保证删掉；服务器按内容哈希删并广播到其他设备）。 */
    fun deleteEntry(entry: Entry, fully: Boolean) {
        val store = AppState.store
        store.delete(entry.id)
        if (fully) {
            val hash = store.hashForSync(entry)
            scope.launch {
                val c = client ?: return@launch
                repeat(3) { attempt ->
                    if (c.sendDelete(hash)) return@launch
                    delay(3000)
                }
            }
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
                store.addIfAbsent(entry)
                if (writeClipboard) ClipboardEvents.writeClip(context, entry, store)
            }
            "clip_image" -> {
                val id = m.mediaId?.toLongOrNull() ?: return
                val bytes = c.downloadMedia(id) ?: return
                val path = store.saveImageFile(bytes)
                val entry = Entry(type = "image", content = path, source = "pc",
                    thumb = store.makeThumb(bytes),
                    createdAt = if (m.ts > 0) m.ts / 1000 else System.currentTimeMillis() / 1000)
                if (!store.addIfAbsent(entry)) File(path).delete()
                if (writeClipboard) ClipboardEvents.writeClip(context, entry, store)
            }
            "clip_file" -> {
                val id = m.mediaId?.toLongOrNull() ?: return
                val bytes = c.downloadMedia(id) ?: return
                val path = store.saveRemoteFile(m.name ?: "file.bin", bytes)
                val entry = Entry(type = "file", content = path, source = "pc",
                    createdAt = if (m.ts > 0) m.ts / 1000 else System.currentTimeMillis() / 1000)
                if (!store.addIfAbsent(entry)) File(path).delete()
                if (writeClipboard) ClipboardEvents.writeClip(context, entry, store)
            }
        }
    }
}
