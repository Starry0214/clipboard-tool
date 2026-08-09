package com.starry.clipboardtool.net

import org.json.JSONObject

data class AuthResult(val deviceId: Long, val token: String)

data class SyncMessage(
    val type: String,
    val originDeviceId: Long,
    val seq: Long,
    val ts: Long,
    val text: String?,
    val mediaId: String?,
    val name: String?,
    val size: Long,
)

fun parseSyncMessage(json: String): SyncMessage {
    val o = JSONObject(json)
    var text: String? = null
    var mediaId: String? = null
    var name: String? = null
    var size = 0L
    if (o.has("payload")) {
        val p = o.optJSONObject("payload")
        if (p != null) {
            if (p.has("text")) text = p.getString("text")
            if (p.has("mediaId")) mediaId = p.getString("mediaId")
            if (p.has("name")) name = p.getString("name")
            if (p.has("size")) size = p.getLong("size")
        }
    }
    return SyncMessage(
        type = o.optString("type", ""),
        originDeviceId = o.optLong("originDeviceId", 0),
        seq = o.optLong("seq", 0),
        ts = o.optLong("ts", 0),
        text = text, mediaId = mediaId, name = name, size = size)
}
