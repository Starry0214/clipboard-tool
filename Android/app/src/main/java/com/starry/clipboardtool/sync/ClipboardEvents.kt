package com.starry.clipboardtool.sync

import android.content.ClipData
import android.content.ClipboardManager
import android.content.Context
import android.net.Uri
import android.provider.OpenableColumns
import androidx.core.content.FileProvider
import com.starry.clipboardtool.data.Entry
import com.starry.clipboardtool.data.LocalStore
import java.io.File
import java.security.MessageDigest

object ClipboardEvents {
    /** 写剪贴板后置位的内容哈希（防回环：监听回调命中则跳过上传）。 */
    @Volatile var suppressHash: String? = null

    fun contentHash(text: String): String =
        MessageDigest.getInstance("SHA-256").digest(text.toByteArray())
            .joinToString("") { "%02x".format(it) }

    /** 读取当前剪贴板，构造可入库条目；失败返回 null。图片存文件+缩略图。 */
    fun readClip(context: Context, clipboard: ClipboardManager, store: LocalStore): Entry? {
        val clip = clipboard.primaryClip ?: return null
        if (clip.itemCount == 0) return null
        val item = clip.getItemAt(0)
        val now = System.currentTimeMillis() / 1000

        item.text?.toString()?.takeIf { it.isNotBlank() }?.let { text ->
            return Entry(type = "text", content = text, source = "local", createdAt = now)
        }

        item.uri?.let { uri ->
            val isImage = uri.toString().contains("image") || clip.description.getMimeType(0)?.startsWith("image/") == true
            val bytes = readUriBytes(context, uri) ?: return null
            if (isImage || isPngBytes(bytes)) {
                val path = store.saveImageFile(bytes)
                return Entry(
                    type = "image", content = path, source = "local",
                    thumb = store.makeThumb(bytes), createdAt = now)
            }
            val name = queryName(context, uri) ?: "clip_$now.bin"
            val path = store.saveRemoteFile(name, bytes)
            return Entry(type = "file", content = path, source = "local", createdAt = now)
        }

        item.coerceToText(context).toString().takeIf { it.isNotBlank() }?.let { text ->
            return Entry(type = "text", content = text, source = "local", createdAt = now)
        }
        return null
    }

    private fun readUriBytes(context: Context, uri: Uri): ByteArray? = runCatching {
        context.contentResolver.openInputStream(uri)?.use { it.readBytes() }
    }.getOrNull()

    private fun isPngBytes(bytes: ByteArray): Boolean =
        bytes.size > 8 && bytes[0] == 0x89.toByte() && bytes[1] == 0x50.toByte() &&
            bytes[2] == 0x4E.toByte() && bytes[3] == 0x47.toByte()

    private fun queryName(context: Context, uri: Uri): String? = runCatching {
        context.contentResolver.query(uri, arrayOf(OpenableColumns.DISPLAY_NAME), null, null, null)
            ?.use { c -> if (c.moveToFirst()) c.getString(0) else null }
    }.getOrNull()

    /** 把历史条目写回系统剪贴板；图片/文件经 FileProvider 暴露，可在任意 App 粘贴。 */
    fun writeClip(context: Context, entry: Entry, store: LocalStore) {
        val clipboard = context.getSystemService(Context.CLIPBOARD_SERVICE) as ClipboardManager
        val clip = when (entry.type) {
            "text" -> ClipData.newPlainText("clip", entry.content)
            "image", "file" -> {
                val f = File(entry.content)
                if (!f.exists()) return
                val uri = FileProvider.getUriForFile(
                    context, "${context.packageName}.fileprovider", f)
                ClipData.newUri(context.contentResolver, "clip", uri)
            }
            else -> return
        }
        suppressHash = contentHash(entry.content)
        clipboard.setPrimaryClip(clip)
    }
}
