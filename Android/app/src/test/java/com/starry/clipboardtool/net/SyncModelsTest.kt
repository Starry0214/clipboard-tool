package com.starry.clipboardtool.net

import org.junit.Assert.assertEquals
import org.junit.Test

class SyncModelsTest {
    @Test
    fun parseTextMessage() {
        val m = parseSyncMessage(
            """{"type":"clip_text","originDeviceId":2,"seq":1,"ts":1754700000000,"payload":{"text":"hello 世界"}}""")
        assertEquals("clip_text", m.type)
        assertEquals(2L, m.originDeviceId)
        assertEquals(1L, m.seq)
        assertEquals("hello 世界", m.text)
    }

    @Test
    fun parseMediaMessage() {
        val m = parseSyncMessage(
            """{"type":"clip_image","originDeviceId":2,"seq":3,"ts":1754700000000,"payload":{"mediaId":"12","name":"a.png","size":100}}""")
        assertEquals("clip_image", m.type)
        assertEquals("12", m.mediaId)
        assertEquals("a.png", m.name)
        assertEquals(100L, m.size)
    }

    @Test
    fun parseUnknownPayloadKeepsFields() {
        val m = parseSyncMessage("""{"type":"clip_file","originDeviceId":9,"seq":4,"ts":1,"payload":{}}""")
        assertEquals("clip_file", m.type)
        assertEquals(9L, m.originDeviceId)
        assertEquals(null, m.mediaId)
    }
}
