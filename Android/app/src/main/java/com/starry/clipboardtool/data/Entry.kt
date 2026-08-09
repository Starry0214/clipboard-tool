package com.starry.clipboardtool.data

data class Entry(
    val id: Long = 0,
    val type: String = "text", // text | image | file
    val content: String = "",
    val thumb: ByteArray? = null,
    val source: String = "local", // local | pc
    val createdAt: Long = 0,
)
