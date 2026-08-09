package com.starry.clipboardtool.data

import android.content.Context
import android.database.sqlite.SQLiteDatabase
import android.graphics.Bitmap
import android.graphics.BitmapFactory
import java.io.ByteArrayOutputStream
import java.io.File
import java.security.MessageDigest
import java.util.UUID

class LocalStore(context: Context) {
    private val imagesDir = File(context.filesDir, "images").apply { mkdirs() }
    private val filesDir = File(context.filesDir, "files").apply { mkdirs() }
    private val db: SQLiteDatabase =
        SQLiteDatabase.openOrCreateDatabase(context.getDatabasePath("clipboard.db"), null)

    init {
        db.execSQL(
            """
            CREATE TABLE IF NOT EXISTS entries (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                type TEXT NOT NULL,
                content TEXT NOT NULL DEFAULT '',
                hash TEXT NOT NULL DEFAULT '',
                thumb BLOB NULL,
                source TEXT NOT NULL DEFAULT 'local',
                created_at INTEGER NOT NULL
            );
            """.trimIndent())
    }

    fun saveImageFile(bytes: ByteArray): String =
        File(imagesDir, "${UUID.randomUUID()}.png").also { it.writeBytes(bytes) }.absolutePath

    fun saveRemoteFile(name: String, bytes: ByteArray): String {
        val safe = name.replace(Regex("[\\\\/:*?\"<>|]"), "_")
        return File(filesDir, "${UUID.randomUUID()}_$safe").also { it.writeBytes(bytes) }.absolutePath
    }

    /** 新增条目，按哈希去重（图片按原图字节、其余按 type\0content）；重复则刷新时间并返回 false。 */
    fun add(e: Entry): Boolean {
        val hash = hashOf(e)
        if (exists(hash)) {
            touch(hash)
            return false
        }
        db.execSQL(
            "INSERT INTO entries (type, content, hash, thumb, source, created_at) VALUES (?,?,?,?,?,?)",
            arrayOf(e.type, e.content, hash, e.thumb, e.source, e.createdAt))
        return true
    }

    /** 跨端同步用内容哈希（与服务器/Windows 算法一致：文本 type\0content、图片字节）。 */
    fun hashForSync(e: Entry): String = hashOf(e)

    /** 按内容哈希删除条目（同步删除用），并清理图片文件。 */
    fun deleteByHash(hash: String) {
        db.rawQuery("SELECT type, content FROM entries WHERE hash = ?", arrayOf(hash)).use { c ->
            while (c.moveToNext()) {
                val type = c.getString(0)
                val content = c.getString(1)
                if (type == "image" || type == "file") File(content).delete()
            }
        }
        db.execSQL("DELETE FROM entries WHERE hash = ?", arrayOf(hash))
    }

    private fun hashOf(e: Entry): String {
        val bytes = if (e.type == "image") {
            val f = File(e.content)
            if (f.exists()) f.readBytes() else e.content.toByteArray()
        } else {
            "${e.type}\u0000${e.content}".toByteArray()
        }
        return MessageDigest.getInstance("SHA-256").digest(bytes).joinToString("") { "%02x".format(it) }
    }

    private fun exists(hash: String): Boolean {
        db.rawQuery("SELECT COUNT(*) FROM entries WHERE hash = ?", arrayOf(hash)).use {
            return it.moveToFirst() && it.getLong(0) > 0
        }
    }

    private fun touch(hash: String) {
        db.execSQL("UPDATE entries SET created_at = ? WHERE hash = ?",
            arrayOf(System.currentTimeMillis() / 1000, hash))
    }

    fun query(search: String?, type: String?, source: String?): List<Entry> {
        val where = mutableListOf<String>()
        val args = mutableListOf<String>()
        if (!search.isNullOrBlank()) {
            where += "type != 'image' AND content LIKE ? ESCAPE '\\'"
            args += "%${search.replace("\\", "\\\\").replace("%", "\\%").replace("_", "\\_")}%"
        }
        if (!type.isNullOrEmpty()) { where += "type = ?"; args += type }
        if (!source.isNullOrEmpty()) { where += "source = ?"; args += source }
        val sql = "SELECT id, type, content, thumb, source, created_at FROM entries" +
            (if (where.isEmpty()) "" else " WHERE ${where.joinToString(" AND ")}") +
            " ORDER BY created_at DESC"
        val list = mutableListOf<Entry>()
        db.rawQuery(sql, args.toTypedArray()).use { c ->
            while (c.moveToNext()) {
                list += Entry(
                    id = c.getLong(0), type = c.getString(1), content = c.getString(2),
                    thumb = c.getBlob(3), source = c.getString(4), createdAt = c.getLong(5))
            }
        }
        return list
    }

    fun getById(id: Long): Entry? {
        db.rawQuery("SELECT id, type, content, thumb, source, created_at FROM entries WHERE id = ?",
            arrayOf(id.toString())).use { c ->
            if (!c.moveToFirst()) return null
            return Entry(c.getLong(0), c.getString(1), c.getString(2), c.getBlob(3), c.getString(4), c.getLong(5))
        }
    }

    fun delete(id: Long) {
        val e = getById(id) ?: return
        db.execSQL("DELETE FROM entries WHERE id = ?", arrayOf(id.toString()))
        if (e.type == "image" || e.type == "file") File(e.content).delete()
    }

    fun clear() {
        db.execSQL("DELETE FROM entries")
        imagesDir.listFiles()?.forEach { it.delete() }
        filesDir.listFiles()?.forEach { it.delete() }
    }

    /** 图片原图字节（Content 指向的文件）。 */
    fun imageBytes(id: Long): ByteArray? {
        val e = getById(id) ?: return null
        val f = File(e.content)
        return if (f.exists()) f.readBytes() else null
    }

    /** 生成 ≤max 最长边的 PNG 缩略图。 */
    fun makeThumb(bytes: ByteArray, max: Int = 200): ByteArray? {
        val bmp = BitmapFactory.decodeByteArray(bytes, 0, bytes.size) ?: return null
        val scale = max.toFloat() / maxOf(bmp.width, bmp.height)
        val scaled = if (scale < 1f)
            Bitmap.createScaledBitmap(bmp, (bmp.width * scale).toInt().coerceAtLeast(1), (bmp.height * scale).toInt().coerceAtLeast(1), true)
        else bmp
        val out = ByteArrayOutputStream()
        scaled.compress(Bitmap.CompressFormat.PNG, 100, out)
        return out.toByteArray()
    }
}
