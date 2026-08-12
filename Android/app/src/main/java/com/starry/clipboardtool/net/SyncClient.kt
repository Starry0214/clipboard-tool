package com.starry.clipboardtool.net

import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch
import okhttp3.MediaType
import okhttp3.MediaType.Companion.toMediaType
import okhttp3.OkHttpClient
import okhttp3.Request
import okhttp3.RequestBody
import okhttp3.RequestBody.Companion.toRequestBody
import okhttp3.Response
import okhttp3.WebSocket
import okhttp3.WebSocketListener
import okio.BufferedSink
import org.json.JSONObject
import java.security.SecureRandom
import java.security.cert.X509Certificate
import java.util.concurrent.TimeUnit
import javax.net.ssl.SSLContext
import javax.net.ssl.TrustManager
import javax.net.ssl.X509TrustManager

class SyncClient(private val baseUrl: String, private val token: String) {
    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.IO)
    private val http: OkHttpClient = buildHttpClient()
    private var ws: WebSocket? = null
    private var running = false
    /** WS 是否完成握手（onOpen 才为 true）；未握手时 send 会假成功丢消息，必须以此为准。 */
    @Volatile
    private var connected = false

    fun isConnected(): Boolean = connected

    private fun buildHttpClient(): OkHttpClient {
        val trustAll = object : X509TrustManager {
            override fun checkClientTrusted(chain: Array<out X509Certificate>?, authType: String?) {}
            override fun checkServerTrusted(chain: Array<out X509Certificate>?, authType: String?) {}
            override fun getAcceptedIssuers(): Array<X509Certificate> = arrayOf()
        }
        val ssl = SSLContext.getInstance("TLS").apply {
            init(null, arrayOf<TrustManager>(trustAll), SecureRandom())
        }
        return OkHttpClient.Builder()
            .connectTimeout(15, TimeUnit.SECONDS)
            .readTimeout(30, TimeUnit.SECONDS)
            .sslSocketFactory(ssl.socketFactory, trustAll)
            .hostnameVerifier { _, _ -> true }
            .pingInterval(30, TimeUnit.SECONDS)
            .build()
    }

    private fun wsUrl(): String =
        (if (baseUrl.startsWith("https", ignoreCase = true)) "wss" else "ws") +
            "://" + baseUrl.substringAfter("://") + "/ws?token=" + token

    private suspend fun auth(endpoint: String, username: String, password: String, deviceName: String): AuthResult? =
        try {
            val body = JSONObject().put("username", username).put("password", password)
                .put("deviceName", deviceName).toString()
            http.newCall(Request.Builder()
                .url(baseUrl.trimEnd('/') + endpoint)
                .post(body.toRequestBody("application/json".toMediaType()))
                .build()).execute().use { resp ->
                if (resp.code != 200 && resp.code != 201) {
                    android.util.Log.e("ClipSync", "auth($endpoint) status=${resp.code} body=${resp.body?.string()}")
                    return@use null
                }
                val o = JSONObject(resp.body?.string() ?: return@use null)
                AuthResult(o.getLong("deviceId"), o.getString("token"))
            }
        } catch (e: Exception) {
            android.util.Log.e("ClipSync", "auth($endpoint) failed: ${e.javaClass.simpleName}: ${e.message}")
            null
        }

    suspend fun register(username: String, password: String, deviceName: String): AuthResult? =
        auth("/api/auth/register", username, password, deviceName)

    suspend fun login(username: String, password: String, deviceName: String): AuthResult? =
        auth("/api/auth/login", username, password, deviceName)

    /** 上传媒体字节；onProgress(uploaded, total) 在写请求体时实时回调（64KB 块）。 */
    suspend fun uploadMedia(bytes: ByteArray, onProgress: (Long, Long) -> Unit = { _, _ -> }): Long? = runCatching {
        http.newCall(Request.Builder()
            .url(baseUrl.trimEnd('/') + "/api/media")
            .post(object : RequestBody() {
                override fun contentType(): MediaType? = "application/octet-stream".toMediaType()
                override fun contentLength(): Long = bytes.size.toLong()
                override fun writeTo(sink: BufferedSink) {
                    var uploaded = 0L
                    val buf = ByteArray(64 * 1024)
                    while (uploaded < bytes.size) {
                        val n = minOf(buf.size.toLong(), bytes.size - uploaded).toInt()
                        System.arraycopy(bytes, uploaded.toInt(), buf, 0, n)
                        sink.write(buf, 0, n)
                        uploaded += n
                        onProgress(uploaded, bytes.size.toLong())
                    }
                }
            })
            .header("Authorization", "Bearer $token")
            .build()).execute().use { resp ->
            if (resp.code != 201) null
            else JSONObject(resp.body?.string() ?: "").getLong("mediaId")
        }
    }.getOrNull()

    suspend fun downloadMedia(id: Long): ByteArray? = runCatching {
        http.newCall(Request.Builder()
            .url(baseUrl.trimEnd('/') + "/api/media/$id")
            .header("Authorization", "Bearer $token")
            .build()).execute().use { resp ->
            if (resp.code != 200) null else resp.body?.bytes()
        }
    }.getOrNull()

    suspend fun fetchHistory(since: Long): List<SyncMessage>? = runCatching {
        http.newCall(Request.Builder()
            .url(baseUrl.trimEnd('/') + "/api/history?since=$since")
            .header("Authorization", "Bearer $token")
            .build()).execute().use { resp ->
            if (resp.code != 200) return@use null
            val arr = JSONObject(resp.body?.string() ?: return@use null).getJSONArray("messages")
            (0 until arr.length()).map { parseSyncMessage(arr.getJSONObject(it).toString()) }
        }
    }.getOrNull()

    /** 建立 WS 长连接；断开后指数退避重连（1s→60s）直到 close()。 */
    fun connect(onMessage: (SyncMessage) -> Unit, onStatus: (String) -> Unit) {
        running = true
        scope.launch {
            var delayMs = 1000L
            while (running) {
                val ok = runCatching {
                    val req = Request.Builder().url(wsUrl()).build()
                    val listener = object : WebSocketListener() {
                        override fun onOpen(webSocket: WebSocket, response: Response) {
                            connected = true
                            onStatus("已连接")
                        }
                        override fun onMessage(webSocket: WebSocket, text: String) {
                            onMessage(parseSyncMessage(text))
                        }
                        override fun onFailure(webSocket: WebSocket, t: Throwable, response: Response?) {
                            connected = false
                            synchronized(this@SyncClient) { ws = null }
                            onStatus("连接断开，重连中…")
                        }
                        override fun onClosed(webSocket: WebSocket, code: Int, reason: String) {
                            connected = false
                            synchronized(this@SyncClient) { ws = null }
                            onStatus("连接断开，重连中…")
                        }
                    }
                    synchronized(this@SyncClient) { ws = http.newWebSocket(req, listener) }
                    true
                }.getOrElse { false }
                if (!ok) {
                    onStatus("连接失败，重连中…")
                    delay(delayMs)
                    delayMs = (delayMs * 2).coerceAtMost(60_000)
                } else {
                    delayMs = 1000L
                    while (running && ws != null) delay(1000)
                }
            }
        }
    }

    fun sendClipText(text: String): Boolean {
        val payload = JSONObject().put("text", text)
        return send("""{"type":"clip_text","payload":$payload}""")
    }

    fun sendClipMedia(type: String, mediaId: Long, name: String, size: Long): Boolean {
        val payload = JSONObject().put("mediaId", mediaId).put("name", name).put("size", size)
        return send("""{"type":"$type","payload":$payload}""")
    }

    fun sendDelete(hash: String): Boolean {
        val payload = JSONObject().put("hash", hash)
        return send("""{"type":"delete","payload":$payload}""")
    }

    private fun send(json: String): Boolean {
        if (!connected) return false
        val ws = synchronized(this) { ws }
        if (ws == null) return false
        return try {
            ws.send(json)
            true
        } catch (e: Exception) {
            false
        }
    }

    fun close() {
        running = false
        connected = false
        synchronized(this) { ws }?.close(1000, null)
    }
}
