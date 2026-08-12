package com.starry.clipboardtool.util

import android.content.ContentValues
import android.content.Context
import android.os.Build
import android.os.Environment
import android.provider.MediaStore
import com.starry.clipboardtool.data.Entry
import java.io.File

/** 把历史条目原文件导出到系统 Download 目录（MediaStore，Android 10+ 免权限）。 */
object MediaSaver {
    /** 成功返回 null，失败返回错误说明。 */
    fun saveToDownloads(context: Context, entry: Entry): String? {
        val src = File(entry.content)
        if (!src.exists()) return "原文件已不存在，无法保存"
        val name = if (entry.type == "image")
            "剪贴板_${entry.id}.png"
        else
            src.name
        return try {
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q) {
                val values = ContentValues().apply {
                    put(MediaStore.MediaColumns.DISPLAY_NAME, name)
                    put(MediaStore.MediaColumns.MIME_TYPE,
                        if (entry.type == "image") "image/png" else "application/octet-stream")
                    put(MediaStore.MediaColumns.RELATIVE_PATH, Environment.DIRECTORY_DOWNLOADS)
                }
                val uri = context.contentResolver.insert(
                    MediaStore.Downloads.EXTERNAL_CONTENT_URI, values)
                    ?: return "保存失败：无法创建文件"
                context.contentResolver.openOutputStream(uri)?.use { out ->
                    src.inputStream().use { it.copyTo(out) }
                } ?: return "保存失败：无法写入文件"
            } else {
                // Android 8-9：传统文件写入（需存储权限，失败返回错误）
                val dir = File(Environment.getExternalStoragePublicDirectory(Environment.DIRECTORY_DOWNLOADS), "ClipboardTool")
                dir.mkdirs()
                File(dir, name).also { dst -> src.copyTo(dst, overwrite = true) }
            }
            null
        } catch (e: Exception) {
            "保存失败：${e.message}"
        }
    }
}
