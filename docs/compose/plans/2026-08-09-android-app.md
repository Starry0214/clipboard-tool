# Android 端多端同步 App 实施计划 — M3

> **For agentic workers:** REQUIRED SUB-SKILL: Use compose:subagent (recommended) or compose:execute to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 新建 Android 剪贴板 App（`Android/`，Kotlin + Jetpack Compose）：无障碍服务监听剪贴板（无常驻通知）、账号登录、与 SyncServer 双向同步（复用 M1 协议）、App 内历史列表（来源标签/类型筛选/点击回填），真机（小米 14 Pro）联调通过。

**Architecture:** 与 M2 完全对等的客户端架构：`SyncClient`（OkHttp WS+HTTP，双镜像回退）→ `SyncService`（编排：本地捕获上传、远端消息入库+写剪贴板、lastSeq 持久化去重、防回环）→ `LocalStore`（原生 SQLite，表结构与 Windows 端对齐）。UI 为登录态驱动切换：未登录→登录页，已登录→历史列表+设置页。剪贴板监听走 AccessibilityService（无障碍豁免 Android 10+ 后台剪贴板读限制），不挂前台服务、无常驻通知。

**Tech Stack:** Kotlin 1.9.24、AGP 8.2.2、Gradle 8.6、JDK 18、Compose BOM 2024.04.01、OkHttp 4.12.0、原生 SQLite（不引 Room/KSP/Navigation）、minSdk 26 / targetSdk 34 / compileSdk 34、包名 `com.starry.clipboardtool`。

## Global Constraints

- 所有构建命令在 `Android/` 目录执行；Gradle 用系统安装（`C:\gradle\gradle-8.6\bin\gradle.bat`），仓库全走阿里云镜像（已验证 200）：`maven.aliyun.com/repository/google` / `central` / `gradle-plugin`。
- SDK 装于 `C:\Android`（ANDROID_HOME），`Android/local.properties` 写 `sdk.dir`。
- 服务器协议与 M1/M2 **完全一致**（勿改）：注册/登录返回 `{deviceId, token}`；WS 上行 `{"type","payload"}`、下行 `{"type","originDeviceId","seq","ts","payload"}`；`POST/GET /api/media`、`GET /api/history?since=`；单文件 ≤50MB。
- 双镜像：`https://sync.starry0214.one` 优先 + `https://107.175.228.83:8081` IP 直连兜底（OkHttp 自定义 TrustManager 跳过证书校验）；设置页"服务器地址"输入框非空时只用它（联调用 `http://127.0.0.1:8082` + adb reverse）。
- 时间戳：本地库用**秒**（与 Windows 一致），服务器 ts 毫秒→`/1000`。
- 去重：`lastSeq` 持久化（SharedPreferences）；图片按像素字节 SHA-256 哈希；文本按内容哈希；重复 Add 返回 false（调用方清理残留文件）。
- 防回环：写剪贴板后记录内容哈希+时间戳，监听回调命中则跳过上传。
- **回放只入库不写剪贴板**（实时消息才写剪贴板）——否则每次启动把 7 天前内容写进剪贴板。
- Android 端历史不加置顶（spec S5 未要求）；本地条目 source=`local`，远端 source=`pc`，UI 标签显示"电脑"。
- 不引入 Room/KSP/Navigation/kotlinx-serialization（手写 SQL、状态切换、org.json 解析）。
- 提交信息风格 `feat:`/`fix:`/`docs:`；不修改 `ClipboardTool/`、`SyncServer/`、`Launcher/`。

---

### Task 1: Android 环境搭建与项目骨架

**Covers:** S5（脚手架）

**Files:**
- Create: `Android/` 全部骨架（settings.gradle.kts、build.gradle.kts、gradle.properties、app/build.gradle.kts、AndroidManifest.xml、MainActivity.kt、local.properties 不入库）
- Create: `Android/app/src/main/res/values/strings.xml`、`themes.xml`、图标最小集（用 adaptive icon 占位）

**Interfaces:**
- Produces: `gradle.bat :app:assembleDebug` 产出 APK；`MainActivity` 显示 Compose "Hello ClipboardSync"

- [ ] **Step 1: 下载并安装 Android SDK**

```powershell
# 1) commandline-tools
$zip = "C:\Android\cmdline-tools.zip"
Invoke-WebRequest "https://dl.google.com/android/repository/commandlinetools-win-11076708_latest.zip" -OutFile $zip
New-Item -ItemType Directory -Path "C:\Android\cmdline-tools" -Force | Out-Null
Expand-Archive $zip "C:\Android\cmdline-tools\staging"
Move-Item "C:\Android\cmdline-tools\staging\cmdline-tools" "C:\Android\cmdline-tools\latest"
Remove-Item "C:\Android\cmdline-tools\staging" -Recurse -Force

# 2) 安装 platform-tools / platforms;android-34 / build-tools;34.0.0
$env:ANDROID_HOME = "C:\Android"
"y" | & "C:\Android\cmdline-tools\latest\bin\sdkmanager.bat" --licenses --sdk_root=C:\Android
& "C:\Android\cmdline-tools\latest\bin\sdkmanager.bat" "platform-tools" "platforms;android-34" "build-tools;34.0.0" --sdk_root=C:\Android

# 3) 用户级环境变量 + adb 可用
[Environment]::SetEnvironmentVariable("ANDROID_HOME","C:\Android","User")
& "C:\Android\platform-tools\adb.exe" version
```

Expected: adb 打印版本；`C:\Android\platforms\android-34` 存在。

- [ ] **Step 2: 下载并安装 Gradle 8.6**

```powershell
Invoke-WebRequest "https://services.gradle.org/distributions/gradle-8.6-bin.zip" -OutFile "C:\gradle-8.6-bin.zip"
New-Item -ItemType Directory -Path "C:\gradle" -Force | Out-Null
Expand-Archive "C:\gradle-8.6-bin.zip" "C:\gradle"
& "C:\gradle\gradle-8.6\bin\gradle.bat" --version | Select-Object -First 5
```

Expected: Gradle 8.6（JVM 18）。

- [ ] **Step 3: 写项目骨架文件**

`Android/settings.gradle.kts`：

```kotlin
pluginManagement {
    repositories {
        maven("https://maven.aliyun.com/repository/google")
        maven("https://maven.aliyun.com/repository/central")
        maven("https://maven.aliyun.com/repository/gradle-plugin")
        google()
        mavenCentral()
        gradlePluginPortal()
    }
}
dependencyResolutionManagement {
    repositoriesMode.set(RepositoriesMode.FAIL_ON_PROJECT_REPOS)
    repositories {
        maven("https://maven.aliyun.com/repository/google")
        maven("https://maven.aliyun.com/repository/central")
        google()
        mavenCentral()
    }
}
rootProject.name = "ClipboardTool"
include(":app")
```

`Android/build.gradle.kts`：

```kotlin
plugins {
    id("com.android.application") version "8.2.2" apply false
    id("org.jetbrains.kotlin.android") version "1.9.24" apply false
}
```

`Android/gradle.properties`：

```properties
org.gradle.jvmargs=-Xmx2g -Dfile.encoding=UTF-8
android.useAndroidX=true
kotlin.code.style=official
org.gradle.daemon=true
```

`Android/local.properties`（不入库）：

```properties
sdk.dir=C\:\\Android
```

`Android/app/build.gradle.kts`：

```kotlin
plugins {
    id("com.android.application")
    id("org.jetbrains.kotlin.android")
}

android {
    namespace = "com.starry.clipboardtool"
    compileSdk = 34
    defaultConfig {
        applicationId = "com.starry.clipboardtool"
        minSdk = 26
        targetSdk = 34
        versionCode = 1
        versionName = "1.0.0"
    }
    buildTypes {
        release {
            isMinifyEnabled = false
        }
    }
    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
    }
    kotlinOptions { jvmTarget = "17" }
    buildFeatures { compose = true }
    composeOptions { kotlinCompilerExtensionVersion = "1.5.11" }
}

dependencies {
    implementation("androidx.core:core-ktx:1.13.1")
    implementation("androidx.activity:activity-compose:1.9.0")
    implementation("androidx.lifecycle:lifecycle-runtime-ktx:2.7.0")
    implementation(platform("androidx.compose:compose-bom:2024.04.01"))
    implementation("androidx.compose.ui:ui")
    implementation("androidx.compose.material3:material3")
    implementation("androidx.compose.ui:ui-tooling-preview")
    implementation("androidx.compose.material:material-icons-core")
    implementation("com.squareup.okhttp3:okhttp:4.12.0")
    implementation("org.jetbrains.kotlinx:kotlinx-coroutines-android:1.8.1")
    debugImplementation("androidx.compose.ui:ui-tooling")
    testImplementation("junit:junit:4.13.2")
    testImplementation("org.json:json:20240303")
}
```

（`kotlinCompilerExtensionVersion 1.5.11` 对应 Kotlin 1.9.24；Compose BOM 2024.04.01 兼容。）

`Android/app/src/main/AndroidManifest.xml`：

```xml
<?xml version="1.0" encoding="utf-8"?>
<manifest xmlns:android="http://schemas.android.com/apk/res/android">
    <uses-permission android:name="android.permission.INTERNET"/>
    <application
        android:label="@string/app_name"
        android:icon="@mipmap/ic_launcher"
        android:theme="@style/Theme.ClipboardTool"
        android:allowBackup="true">
        <activity android:name=".MainActivity" android:exported="true">
            <intent-filter>
                <action android:name="android.intent.action.MAIN"/>
                <category android:name="android.intent.category.LAUNCHER"/>
            </intent-filter>
        </activity>
    </application>
</manifest>
```

`Android/app/src/main/java/com/starry/clipboardtool/MainActivity.kt`：

```kotlin
package com.starry.clipboardtool

import android.os.Bundle
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text

class MainActivity : ComponentActivity() {
    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        setContent { MaterialTheme { Text("Hello ClipboardSync") } }
    }
}
```

`strings.xml`：`app_name=剪贴板同步`；`themes.xml`（values/themes.xml）：

```xml
<resources>
    <style name="Theme.ClipboardTool" parent="android:Theme.Material.Light.NoActionBar"/>
</resources>
```

图标：`res/mipmap-anydpi-v26/ic_launcher.xml` 自适应图标（background 纯色 + foreground 空），或直接引用 `@android:drawable/sym_def_app_icon` 占位（临时）：

```xml
<resources>
    <style name="Theme.ClipboardTool" parent="android:Theme.Material.Light.NoActionBar"/>
    <item name="android:icon" tools:targetApi="n"/>
</resources>
```

简化做法：manifest 的 `android:icon` 用 `@android:drawable/sym_def_app_icon`（占位，正式图标后续替换）。

- [ ] **Step 4: 首次构建**

```powershell
& "C:\gradle\gradle-8.6\bin\gradle.bat" -p "C:\Users\Starry\OneDrive\Desktop\agent周事项\剪切板工具开发\Android" :app:assembleDebug 2>&1 | Select-String "BUILD|error|FAILED"
```

Expected: `BUILD SUCCESSFUL`；`app/build/outputs/apk/debug/app-debug.apk` 生成。（首次下载依赖较慢，超时则加长。）

- [ ] **Step 5: 提交**

```bash
git add Android/
git commit -m "feat: Android 项目骨架（Compose + Gradle 阿里云镜像）"
```

---

### Task 2: 本地存储层（SQLite）

**Covers:** S5

**Files:**
- Create: `Android/app/src/main/java/com/starry/clipboardtool/data/Entry.kt`
- Create: `Android/app/src/main/java/com/starry/clipboardtool/data/LocalStore.kt`

**Interfaces:**
- Produces:
  - `data class Entry(id: Long, type: String, content: String, thumb: ByteArray?, source: String, createdAt: Long)`（type: text|image|file；source: local|pc）
  - `class LocalStore(context: Context)`：`add(e: Entry): Boolean`（哈希去重：图片按 thumb/原图像素字节、其余按 `type\0content` SHA-256；重复则 Touch 移顶返回 false）、`query(search: String?, type: String?, source: String?): List<Entry>`、`getById(id: Long): Entry?`（image 条目从文件读字节）、`delete(id: Long)`（删文件）、`clear()`（含 images/files 清理）
  - 数据目录：`context.filesDir/images/`、`context.filesDir/files/`（Content=绝对路径，与 Windows 对齐）；`saveImageFile(bytes): String`、`saveRemoteFile(name, bytes): String`
  - DB：`context.getDatabasePath("clipboard.db")`，表结构与 Windows 端对齐

- [ ] **Step 1: 实现 Entry.kt 与 LocalStore.kt**

`Entry.kt`：

```kotlin
package com.starry.clipboardtool.data

data class Entry(
    val id: Long = 0,
    val type: String = "text", // text | image | file
    val content: String = "",
    val thumb: ByteArray? = null,
    val source: String = "local", // local | pc
    val createdAt: Long = 0,
)
```

`LocalStore.kt`（关键部分，完整实现）：

```kotlin
package com.starry.clipboardtool.data

import android.content.Context
import android.database.sqlite.SQLiteDatabase
import android.graphics.Bitmap
import android.graphics.BitmapFactory
import java.io.ByteArrayOutputStream
import java.io.File
import java.security.MessageDigest

class LocalStore(context: Context) {
    private val appDir = context.filesDir
    private val imagesDir = File(appDir, "images").apply { mkdirs() }
    private val filesDir = File(appDir, "files").apply { mkdirs() }
    private val db: SQLiteDatabase =
        SQLiteDatabase.openOrCreateDatabase(context.getDatabasePath("clipboard.db"), null)

    init {
        db.execSQL("""
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
        File(imagesDir, "${java.util.UUID.randomUUID()}.png").also { it.writeBytes(bytes) }.absolutePath

    fun saveRemoteFile(name: String, bytes: ByteArray): String {
        val safe = name.replace(Regex("[\\\\/:*?\"<>|]"), "_")
        return File(filesDir, "${java.util.UUID.randomUUID()}_$safe").also { it.writeBytes(bytes) }.absolutePath
    }

    fun add(e: Entry): Boolean {
        val hash = hashOf(e)
        if (exists(hash)) { touch(hash); return false }
        db.execSQL(
            "INSERT INTO entries (type, content, hash, thumb, source, created_at) VALUES (?,?,?,?,?,?)",
            arrayOf(e.type, e.content, hash, e.thumb, e.source, e.createdAt))
        return true
    }

    private fun hashOf(e: Entry): String {
        val bytes = if (e.type == "image" && e.thumb != null) {
            // 用原图字节哈希需传原图；此处约定：图片条目 hash 用 content 路径对应文件的字节
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

    fun imageBytes(id: Long): ByteArray? {
        val e = getById(id) ?: return null
        val f = File(e.content)
        return if (f.exists()) f.readBytes() else null
    }

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
```

（说明：图片条目 `hashOf` 直接读原图文件字节——与 Windows 端"按像素字节哈希"对齐，跨端同一图片去重语义一致。缩略图存 thumb BLOB。）

- [ ] **Step 2: 构建验证**

Run: `gradle.bat -p Android :app:compileDebugKotlin 2>&1 | Select-String "BUILD|error|FAILED"`
Expected: BUILD SUCCESSFUL。

- [ ] **Step 3: 提交**

```bash
git add Android/app/src/main/java/com/starry/clipboardtool/data/
git commit -m "feat: 本地存储层（SQLite 历史库，哈希去重与 Windows 对齐）"
```

---

### Task 3: SyncClient 网络层（OkHttp）

**Covers:** S4, S5

**Files:**
- Create: `Android/app/src/main/java/com/starry/clipboardtool/net/SyncModels.kt`
- Create: `Android/app/src/main/java/com/starry/clipboardtool/net/SyncClient.kt`
- Create: `Android/app/src/test/java/com/starry/clipboardtool/net/SyncModelsTest.kt`

**Interfaces:**
- Produces:
  - `data class AuthResult(deviceId: Long, token: String)`
  - `data class SyncMessage(type: String, originDeviceId: Long, seq: Long, ts: Long, text: String?, mediaId: String?, name: String?, size: Long)` + `fun parseSyncMessage(json: String): SyncMessage`（org.json）
  - `class SyncClient(baseUrl: String, token: String)`：`suspend fun register/login(username, password, deviceName): AuthResult?`、`suspend fun uploadMedia(bytes): Long?`、`suspend fun downloadMedia(id): ByteArray?`、`suspend fun fetchHistory(since): List<SyncMessage>?`、`fun connect(onMessage: (SyncMessage) -> Unit, onStatus: (String) -> Unit)`（OkHttp WebSocket + 指数退避重连 1s→60s）、`fun sendClipText(text)`、`fun sendClipMedia(type, mediaId, name, size)`、`fun close()`
  - OkHttpClient 构建：TrustManager 跳过证书校验（IP 直连镜像）

- [ ] **Step 1: 写协议解析单测（TDD）**

`SyncModelsTest.kt`：

```kotlin
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
```

- [ ] **Step 2: 运行确认失败**

Run: `gradle.bat -p Android :app:testDebugUnitTest --tests "com.starry.clipboardtool.net.SyncModelsTest" 2>&1 | Select-String "FAILED|error|BUILD"`
Expected: 编译失败（类不存在）。

- [ ] **Step 3: 实现 SyncModels.kt**

```kotlin
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
```

- [ ] **Step 4: 运行单测确认通过**

Run: 同 Step 2 命令
Expected: BUILD SUCCESSFUL，3 个测试 PASS。

- [ ] **Step 5: 实现 SyncClient.kt**

```kotlin
package com.starry.clipboardtool.net

import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch
import okhttp3.MediaType.Companion.toMediaType
import okhttp3.OkHttpClient
import okhttp3.Request
import okhttp3.RequestBody.Companion.toRequestBody
import okhttp3.Response
import okhttp3.WebSocket
import okhttp3.WebSocketListener
import org.json.JSONObject
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

    private fun buildHttpClient(): OkHttpClient {
        val trustAll = object : X509TrustManager {
            override fun checkClientTrusted(chain: Array<out X509Certificate>?, authType: String?) {}
            override fun checkServerTrusted(chain: Array<out X509Certificate>?, authType: String?) {}
            override fun getAcceptedIssuers(): Array<X509Certificate> = arrayOf()
        }
        val ssl = SSLContext.getInstance("TLS").apply {
            init(null, arrayOf<TrustManager>(trustAll), java.security.SecureRandom())
        }
        return OkHttpClient.Builder()
            .connectTimeout(15, TimeUnit.SECONDS)
            .readTimeout(30, TimeUnit.SECONDS)
            .sslSocketFactory(ssl.socketFactory, trustAll)
            .hostnameVerifier { _, _ -> true }
            .pingInterval(30, TimeUnit.SECONDS) // 服务端 30s ping；客户端 ping 保持双向活跃
            .build()
    }

    private fun wsUrl(): String =
        (if (baseUrl.startsWith("https", ignoreCase = true)) "wss" else "ws") +
            "://" + baseUrl.substringAfter("://") + "/ws?token=" + token

    private suspend fun auth(endpoint: String, username: String, password: String, deviceName: String): AuthResult? =
        runCatching {
            val body = JSONObject().put("username", username).put("password", password)
                .put("deviceName", deviceName).toString()
            http.newCall(Request.Builder()
                .url(baseUrl.trimEnd('/') + endpoint)
                .post(body.toRequestBody("application/json".toMediaType()))
                .build()).execute().use { resp ->
                if (resp.code != 200 && resp.code != 201) return@use null
                val o = JSONObject(resp.body?.string() ?: return@use null)
                AuthResult(o.getLong("deviceId"), o.getString("token"))
            }
        }.getOrNull()

    suspend fun register(username: String, password: String, deviceName: String): AuthResult? =
        auth("/api/auth/register", username, password, deviceName)

    suspend fun login(username: String, password: String, deviceName: String): AuthResult? =
        auth("/api/auth/login", username, password, deviceName)

    suspend fun uploadMedia(bytes: ByteArray): Long? = runCatching {
        http.newCall(Request.Builder()
            .url(baseUrl.trimEnd('/') + "/api/media")
            .post(bytes.toRequestBody("application/octet-stream".toMediaType()))
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

    fun connect(onMessage: (SyncMessage) -> Unit, onStatus: (String) -> Unit) {
        running = true
        scope.launch {
            var delayMs = 1000L
            while (running) {
                val ok = runCatching {
                    val req = Request.Builder().url(wsUrl()).build()
                    val listener = object : WebSocketListener() {
                        override fun onMessage(webSocket: WebSocket, text: String) {
                            onMessage(parseSyncMessage(text))
                        }
                        override fun onFailure(webSocket: WebSocket, t: Throwable, response: Response?) {
                            onStatus("连接断开，重连中…")
                            synchronized(this@SyncClient) { ws = null }
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
                    onStatus("已连接")
                    // OkHttp 连接生命周期由回调驱动；这里等待断开信号
                    while (running && ws != null) delay(1000)
                }
            }
        }
    }

    fun sendClipText(text: String) {
        send("""{"type":"clip_text","payload":{"text":${JSONObject.quote(text)}}}""")
    }

    fun sendClipMedia(type: String, mediaId: Long, name: String, size: Long) {
        val payload = JSONObject().put("mediaId", mediaId).put("name", name).put("size", size)
        send("""{"type":"$type","payload":$payload}""")
    }

    private fun send(json: String) {
        synchronized(this) { ws }?.send(json)
    }

    fun close() {
        running = false
        synchronized(this) { ws }?.close(1000, null)
    }
}
```

说明：`connect` 中 WebSocket 断开检测——`onFailure` 回调置 `ws=null`，外层协程循环检测到后按指数退避重连（1s→60s）；`http.newWebSocket` 同步返回 WebSocket 实例并赋值 `ws`。

- [ ] **Step 6: 单测 + 构建验证**

Run: `gradle.bat -p Android :app:testDebugUnitTest :app:compileDebugKotlin 2>&1 | Select-String "BUILD|FAILED|error"`
Expected: BUILD SUCCESSFUL。

- [ ] **Step 7: 提交**

```bash
git add Android/app/src/main/java/com/starry/clipboardtool/net/ Android/app/src/test/
git commit -m "feat: SyncClient 网络层（OkHttp WS/HTTP，双镜像兼容，协议解析单测）"
```

---

### Task 4: 无障碍剪贴板监听与写入

**Covers:** S5

**Files:**
- Create: `Android/app/src/main/java/com/starry/clipboardtool/sync/ClipboardListener.kt`（AccessibilityService）
- Create: `Android/app/src/main/java/com/starry/clipboardtool/sync/ClipboardEvents.kt`（ClipboardReader/ClipboardWriter 纯逻辑）
- Create: `Android/app/src/main/res/xml/accessibility_service_config.xml`
- Modify: `Android/app/src/main/AndroidManifest.xml`（注册 service + BIND_ACCESSIBILITY_SERVICE permission）

**Interfaces:**
- Produces:
  - `object ClipboardEvents`：
    - `fun readClip(clipboard: ClipboardManager, store: LocalStore): Entry?`（文本/图片/文件优先级捕获，图片存原图文件+缩略图，返回可入库 Entry；失败返回 null）
    - `fun writeClip(clipboard: ClipboardManager, entry: Entry, store: LocalStore, onWritten: () -> Unit)`（写回剪贴板；图片/文件从本地文件读字节构造 ClipData）
    - `var suppressHash: String?`（写剪贴板后置内容哈希，监听回调命中则跳过——防回环）
  - `class ClipboardListener : AccessibilityService()`：onServiceConnected 注册 `OnPrimaryClipChangedListener`，回调里读剪贴板 → 调 `SyncService.onLocalClip(entry)`（T5 提供）；`isEnabled(context)` 静态检测；无常驻通知

- [ ] **Step 1: 无障碍配置与 manifest**

`res/xml/accessibility_service_config.xml`：

```xml
<?xml version="1.0" encoding="utf-8"?>
<accessibility-service xmlns:android="http://schemas.android.com/apk/res/android"
    android:accessibilityEventTypes="typeWindowStateChanged"
    android:accessibilityFeedbackType="feedbackGeneric"
    android:canRetrieveWindowContent="true"
    android:description="@string/accessibility_desc"
    android:notificationTimeout="100"/>
```

`strings.xml` 追加：`accessibility_desc=用于监听剪贴板变化，实现剪贴板历史与多端同步。`

Manifest 追加（application 内）：

```xml
        <service
            android:name=".sync.ClipboardListener"
            android:exported="false"
            android:label="@string/app_name"
            android:permission="android.permission.BIND_ACCESSIBILITY_SERVICE">
            <intent-filter>
                <action android:name="android.accessibilityservice.AccessibilityService"/>
            </intent-filter>
            <meta-data
                android:name="android.accessibilityservice"
                android:resource="@xml/accessibility_service_config"/>
        </service>
```

- [ ] **Step 2: 实现 ClipboardEvents.kt**

```kotlin
package com.starry.clipboardtool.sync

import android.content.ClipData
import android.content.ClipboardManager
import android.content.Context
import android.content.Intent
import android.graphics.Bitmap
import android.graphics.BitmapFactory
import android.net.Uri
import android.provider.OpenableColumns
import com.starry.clipboardtool.data.Entry
import com.starry.clipboardtool.data.LocalStore
import java.security.MessageDigest

object ClipboardEvents {
    /** 写剪贴板后置位的内容哈希（防回环：监听回调命中则跳过上传）。 */
    @Volatile var suppressHash: String? = null

    fun contentHash(text: String): String =
        MessageDigest.getInstance("SHA-256").digest(text.toByteArray())
            .joinToString("") { "%02x".format(it) }

    /** 读取当前剪贴板，构造可入库条目；失败返回 null。图片存文件+缩略图。 */
    fun readClip(clipboard: ClipboardManager, store: LocalStore): Entry? {
        val clip = clipboard.primaryClip ?: return null
        if (clip.itemCount == 0) return null
        val item = clip.getItemAt(0)
        val now = System.currentTimeMillis() / 1000

        item.text?.takeIf { it.isNotBlank() }?.let { text ->
            return Entry(type = "text", content = text, source = "local", createdAt = now)
        }

        item.uri?.let { uri ->
            val isImage = uri.toString().startsWith("content://") &&
                (uri.toString().contains("image") || item.mimeType?.startsWith("image/") == true)
            val bytes = readUriBytes(clipboard.context, uri) ?: return null
            if (isImage || isPngBytes(bytes)) {
                val path = store.saveImageFile(bytes)
                return Entry(
                    type = "image", content = path, source = "local",
                    thumb = store.makeThumb(bytes), createdAt = now)
            }
            val name = queryName(clipboard.context, uri) ?: "clip_${now}.bin"
            val path = store.saveRemoteFile(name, bytes)
            return Entry(type = "file", content = path, source = "local", createdAt = now)
        }

        // 无 text/uri（如 Bitmap 直接剪贴）时尝试 Bitmap
        item.coerceToText(clipboard.context).toString().takeIf { it.isNotBlank() }?.let { text ->
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

    /** 把历史条目写回系统剪贴板；图片/文件从本地文件读字节。 */
    fun writeClip(context: Context, entry: Entry, store: LocalStore) {
        val clipboard = context.getSystemService(Context.CLIPBOARD_SERVICE) as ClipboardManager
        val clip = when (entry.type) {
            "text" -> ClipData.newPlainText("clip", entry.content)
            "image" -> {
                val bytes = store.imageBytes(entry.id) ?: return
                val bmp = BitmapFactory.decodeByteArray(bytes, 0, bytes.size) ?: return
                ClipData.newUri(context.contentResolver, "clip", saveTempImage(context, bmp))
            }
            else -> {
                val f = java.io.File(entry.content)
                if (!f.exists()) return
                ClipData.newUri(context.contentResolver, "clip", Uri.fromFile(f))
            }
        }
        suppressHash = contentHash(entry.content)
        clipboard.setPrimaryClip(clip)
    }

    private fun saveTempImage(context: Context, bmp: Bitmap): Uri {
        val f = java.io.File(context.cacheDir, "clip_${System.currentTimeMillis()}.png")
        f.outputStream().use { bmp.compress(Bitmap.CompressFormat.PNG, 100, it) }
        return Uri.fromFile(f)
    }
}
```

（注意：图片经 `Uri.fromFile` 写剪贴板——同一 App 内可读；若需跨 App 粘贴图片需 FileProvider，真机联调 T8 验证后按需补。`readClip` 的 `item.text` 分支先于 uri，文本优先与 Windows 一致。）

- [ ] **Step 3: 实现 ClipboardListener.kt**

```kotlin
package com.starry.clipboardtool.sync

import android.accessibilityservice.AccessibilityService
import android.content.ClipboardManager
import android.content.Context
import android.view.accessibility.AccessibilityEvent

/** 无障碍服务：后台监听剪贴板变化（Android 10+ 后台读剪贴板豁免）。无常驻通知。 */
class ClipboardListener : AccessibilityService() {

    private val clipboardListener = ClipboardManager.OnPrimaryClipChangedListener { onClipChanged() }

    private fun onClipChanged() {
        val sync = AppState.syncService ?: return
        if (!sync.isActive) return
        // 防回环：刚由本 App 写入的剪贴板内容跳过（内容哈希命中则跳过并清除标记）
        val clip = getSystemService(ClipboardManager::class.java)
        val text = clip.primaryClip?.getItemAt(0)?.text?.toString()
        if (text != null) {
            val h = ClipboardEvents.contentHash(text)
            if (h == ClipboardEvents.suppressHash) {
                ClipboardEvents.suppressHash = null
                return
            }
        }
        sync.onLocalClip()
    }

    override fun onServiceConnected() {
        super.onServiceConnected()
        getSystemService(ClipboardManager::class.java)
            .addPrimaryClipChangedListener(clipboardListener)
    }

    override fun onAccessibilityEvent(event: AccessibilityEvent?) {}

    override fun onInterrupt() {}

    override fun onDestroy() {
        runCatching {
            getSystemService(ClipboardManager::class.java)
                .removePrimaryClipChangedListener(clipboardListener)
        }
        super.onDestroy()
    }

    companion object {
        fun isEnabled(context: Context): Boolean {
            val expected = context.packageName + "/" + ClipboardListener::class.java.name
            val enabled = android.provider.Settings.Secure.getString(
                context.contentResolver, android.provider.Settings.Secure.ENABLED_ACCESSIBILITY_SERVICES)
            return enabled?.split(':')?.contains(expected) == true
        }
    }
}
```

（`AppState.syncService` 与 `sync.onLocalClip()` 由 T5 提供；防回环判断：文本内容哈希命中 `suppressHash` 则跳过并清除标记。）

- [ ] **Step 4: 构建验证**

Run: `gradle.bat -p Android :app:compileDebugKotlin 2>&1 | Select-String "BUILD|error|FAILED"`
Expected: BUILD SUCCESSFUL（T5 的 AppState 暂以 TODO 占位会导致编译失败——**改为先实现 T5 的最小 AppState 骨架再构建**，或本步构建失败属预期，T5 完成后统一构建。建议：本任务只写文件不构建，T5 完成后统一 `compileDebugKotlin`）。

- [ ] **Step 5: 提交**

```bash
git add Android/app/src/main/java/com/starry/clipboardtool/sync/ Android/app/src/main/AndroidManifest.xml Android/app/src/main/res/xml/
git commit -m "feat: 无障碍剪贴板监听与读写（防回环标记）"
```

---

### Task 5: SyncService 业务编排

**Covers:** S4, S5

**Files:**
- Create: `Android/app/src/main/java/com/starry/clipboardtool/AppState.kt`（Application 级单例：settings 持久化 + syncService + store）
- Create: `Android/app/src/main/java/com/starry/clipboardtool/sync/SyncService.kt`
- Modify: `Android/app/src/main/AndroidManifest.xml`（注册 Application：`android:name=".AppState"`）

**Interfaces:**
- Consumes: `SyncClient`、`LocalStore`、`ClipboardEvents`、`ClipboardListener`
- Produces:
  - `object AppState`：`lateinit var context: Context`、`val store: LocalStore`、`val prefs: SharedPreferences`、`var syncService: SyncService?`；settings 读写：`token/deviceId/username/deviceName/serverOverride/lastSeq`（key 名与 Windows 对齐）
  - `class SyncService(context)`：
    - `val isActive: Boolean`（token 非空且已启动）
    - `suspend fun login(username, password, deviceName): String?`（错误信息或 null=成功；登录失败尝试注册失败则返回错误文本）
    - `suspend fun register(username, password, deviceName): String?`
    - `fun logout()`
    - `fun start()`（已登录才启动：fetchHistory 回放（seq 去重，只入库**不写剪贴板**）→ WS connect）
    - `fun stop()`
    - `fun onLocalClip()`（读剪贴板 → store.add → 上传；图片/文件先 uploadMedia）
    - `onMessage`（远端消息：入库 + **实时消息写剪贴板**（文本/图片）；lastSeq 持久化）
  - 服务器地址：`serverOverride` 非空用之，否则双镜像 `https://sync.starry0214.one` 优先 + `https://107.175.228.83:8081` 兜底（SyncClient 层需支持双镜像列表——用第一个可达的：尝试顺序连接）

- [ ] **Step 1: 实现 AppState.kt**

```kotlin
package com.starry.clipboardtool

import android.app.Application
import android.content.Context
import android.content.SharedPreferences
import com.starry.clipboardtool.data.LocalStore
import com.starry.clipboardtool.sync.SyncService

class AppState : Application() {
    override fun onCreate() {
        super.onCreate()
        instance = this
        store = LocalStore(this)
        prefs = getSharedPreferences("sync_settings", Context.MODE_PRIVATE)
        syncService = SyncService(this)
    }

    companion object {
        lateinit var instance: AppState
        lateinit var store: LocalStore
        lateinit var prefs: SharedPreferences
        var syncService: SyncService? = null

        // settings 读写（与 Windows 端字段对齐）
        var token: String
            get() = prefs.getString("SyncToken", "") ?: ""
            set(v) = prefs.edit().putString("SyncToken", v).apply()
        var username: String
            get() = prefs.getString("SyncUsername", "") ?: ""
            set(v) = prefs.edit().putString("SyncUsername", v).apply()
        var deviceName: String
            get() = prefs.getString("SyncDeviceName", "") ?: ""
            set(v) = prefs.edit().putString("SyncDeviceName", v).apply()
        var serverOverride: String
            get() = prefs.getString("SyncServerOverride", "") ?: ""
            set(v) = prefs.edit().putString("SyncServerOverride", v).apply()
        var lastSeq: Long
            get() = prefs.getLong("SyncLastSeq", 0)
            set(v) = prefs.edit().putLong("SyncLastSeq", v).apply()
        var deviceId: Long
            get() = prefs.getLong("SyncDeviceId", 0)
            set(v) = prefs.edit().putLong("SyncDeviceId", v).apply()
    }
}
```

Manifest：`<application android:name=".AppState" ...>`

- [ ] **Step 2: 实现 SyncService.kt**

```kotlin
package com.starry.clipboardtool.sync

import android.content.Context
import android.os.Handler
import android.os.Looper
import com.starry.clipboardtool.AppState
import com.starry.clipboardtool.data.Entry
import com.starry.clipboardtool.net.SyncClient
import com.starry.clipboardtool.net.SyncMessage
import java.io.File

class SyncService(private val context: Context) {
    val mirrors = listOf("https://sync.starry0214.one", "https://107.175.228.83:8081")
    var onStatus: (String) -> Unit = {}
    var onHistoryChanged: () -> Unit = {}
    private var client: SyncClient? = null
    private val main = Handler(Looper.getMainLooper())

    val isActive: Boolean
        get() = AppState.token.isNotEmpty() && running

    private var running = false

    private fun baseUrl(): String =
        AppState.serverOverride.ifEmpty { mirrors.first() }

    suspend fun login(username: String, password: String, deviceName: String): String? {
        val base = baseUrl()
        val auth = SyncClient(base, "").login(username, password, deviceName)
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
        Thread {
            val history = c.fetchHistory(0)
            history?.forEach { m ->
                if (m.seq <= AppState.lastSeq) return@forEach
                applyRemote(m, writeClipboard = false)
                if (m.seq > AppState.lastSeq) { AppState.lastSeq = m.seq }
            }
            main.post { onHistoryChanged() }
            c.connect(
                onMessage = { m ->
                    if (!running) return@connect
                    if (m.seq > 0 && m.seq <= AppState.lastSeq) return@connect
                    applyRemote(m, writeClipboard = true)
                    if (m.seq > AppState.lastSeq) { AppState.lastSeq = m.seq }
                    main.post { onHistoryChanged() }
                },
                onStatus = { s -> main.post { onStatus(s) } })
        }.start()
    }

    fun stop() {
        running = false
        client?.close()
        client = null
    }

    /** 剪贴板监听回调：读剪贴板 → 入库 → 上传。 */
    fun onLocalClip() {
        if (!running) return
        val entry = ClipboardEvents.readClip(AppState.instance, AppState.store) ?: return
        val added = AppState.store.add(entry)
        if (!added) return
        Thread {
            upload(entry)
        }.start()
        main.post { onHistoryChanged() }
    }

    private fun upload(entry: Entry) {
        val c = client ?: return
        when (entry.type) {
            "text" -> c.sendClipText(entry.content)
            "image", "file" -> {
                val bytes = File(entry.content).takeIf { it.exists() }?.readBytes() ?: return
                val mediaId = c.uploadMedia(bytes) ?: return
                val name = File(entry.content).name
                c.sendClipMedia(if (entry.type == "image") "clip_image" else "clip_file", mediaId, name, bytes.size)
            }
        }
    }

    private fun applyRemote(m: SyncMessage, writeClipboard: Boolean) {
        val store = AppState.store
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
```

（注意：`applyRemote` 引用 `c`（`client`）——改为在 `applyRemote` 内取 `client ?: return`；`ClipboardEvents.writeClip` 对图片条目用 `store.imageBytes(entry.id)`，但远端图片的 id 在 add 后才有效——`writeClip` 需在 add 后调用且 `entry.id` 已回填？`Entry` 是 data class（id 默认 0），add 后 id 未回填。**修正**：`LocalStore.add` 返回后，调用方用 `getById` 或让 `writeClip` 接受 content 路径直接读文件。简化：`writeClip` 的图片分支改为直接读 `entry.content` 文件（不依赖 id）：）

```kotlin
            "image" -> {
                val f = java.io.File(entry.content)
                if (!f.exists()) return
                val bmp = BitmapFactory.decodeFile(f.absolutePath) ?: return
                ClipData.newUri(context.contentResolver, "clip", saveTempImage(context, bmp))
            }
```

（T4 的 `ClipboardEvents.writeClip` 同步改为读 `entry.content` 文件，删除 `store.imageBytes` 依赖；`LocalStore.imageBytes` 保留备用。）

- [ ] **Step 3: 统一构建验证**

Run: `gradle.bat -p Android :app:compileDebugKotlin 2>&1 | Select-String "BUILD|error|FAILED"`
Expected: BUILD SUCCESSFUL（含 T4 文件）。

- [ ] **Step 4: 提交**

```bash
git add Android/app/src/main/java/com/starry/clipboardtool/
git commit -m "feat: SyncService 编排（回放/实时/防回环/seq 去重）与 AppState 持久化"
```

---

### Task 6: 登录页与设置页 UI

**Covers:** S5

**Files:**
- Create: `Android/app/src/main/java/com/starry/clipboardtool/ui/LoginScreen.kt`
- Create: `Android/app/src/main/java/com/starry/clipboardtool/ui/SettingsScreen.kt`
- Modify: `Android/app/src/main/java/com/starry/clipboardtool/MainActivity.kt`（登录态路由 + 设置入口）

**Interfaces:**
- Consumes: `AppState`、`SyncService`
- Produces: 登录页（用户名/密码/设备名 + 登录/注册 + 状态文本）；设置页（账号信息/退出、服务器地址、无障碍引导按钮、同步状态）；MainActivity 顶层路由：未登录→LoginScreen，已登录→HistoryScreen + 顶部设置按钮

- [ ] **Step 1: 实现 LoginScreen.kt**

```kotlin
package com.starry.clipboardtool.ui

import androidx.compose.foundation.layout.*
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.unit.dp
import com.starry.clipboardtool.AppState
import com.starry.clipboardtool.sync.SyncService
import kotlinx.coroutines.launch

@Composable
fun LoginScreen(onLoggedIn: () -> Unit) {
    val scope = rememberCoroutineScope()
    var username by remember { mutableStateOf("") }
    var password by remember { mutableStateOf("") }
    var deviceName by remember { mutableStateOf(android.os.Build.MODEL) }
    var status by remember { mutableStateOf("") }
    var busy by remember { mutableStateOf(false) }

    Column(
        modifier = Modifier.fillMaxSize().padding(24.dp),
        horizontalAlignment = Alignment.CenterHorizontally,
        verticalArrangement = Arrangement.Center) {
        Text("剪贴板同步", style = MaterialTheme.typography.headlineMedium)
        Spacer(Modifier.height(24.dp))
        OutlinedTextField(username, { username = it }, label = { Text("账号") },
            singleLine = true, modifier = Modifier.fillMaxWidth())
        Spacer(Modifier.height(8.dp))
        OutlinedTextField(password, { password = it }, label = { Text("密码") },
            singleLine = true, visualTransformation = androidx.compose.ui.text.input.PasswordVisualTransformation(),
            modifier = Modifier.fillMaxWidth())
        Spacer(Modifier.height(8.dp))
        OutlinedTextField(deviceName, { deviceName = it }, label = { Text("设备名称") },
            singleLine = true, modifier = Modifier.fillMaxWidth())
        Spacer(Modifier.height(16.dp))
        if (busy) {
            CircularProgressIndicator()
        } else {
            Row {
                Button(onClick = {
                    busy = true; status = ""
                    scope.launch {
                        val err = AppState.syncService?.login(username.trim(), password, deviceName.trim())
                        status = err ?: "已登录"
                        busy = false
                        if (err == null) { AppState.syncService?.start(); onLoggedIn() }
                    }
                }, enabled = username.isNotBlank() && password.length >= 6) { Text("登录") }
                Spacer(Modifier.width(12.dp))
                OutlinedButton(onClick = {
                    busy = true; status = ""
                    scope.launch {
                        val err = AppState.syncService?.register(username.trim(), password, deviceName.trim())
                        status = err ?: "已注册并登录"
                        busy = false
                        if (err == null) { AppState.syncService?.start(); onLoggedIn() }
                    }
                }, enabled = username.length >= 4 && password.length >= 6) { Text("注册") }
            }
        }
        Spacer(Modifier.height(12.dp))
        Text(status, color = MaterialTheme.colorScheme.error)
        Text("开启同步后，本机复制的内容会同步到电脑，电脑复制的内容会出现在这里并自动写入剪贴板。",
            style = MaterialTheme.typography.bodySmall,
            color = MaterialTheme.colorScheme.onSurfaceVariant,
            modifier = Modifier.padding(top = 16.dp))
    }
}
```

- [ ] **Step 2: 实现 SettingsScreen.kt**

```kotlin
package com.starry.clipboardtool.ui

import android.content.Intent
import android.provider.Settings
import androidx.compose.foundation.layout.*
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.unit.dp
import com.starry.clipboardtool.AppState
import com.starry.clipboardtool.sync.ClipboardListener

@Composable
fun SettingsScreen(onBack: () -> Unit, onLogout: () -> Unit) {
    val context = LocalContext.current
    var server by remember { mutableStateOf(AppState.serverOverride) }
    var status by remember { mutableStateOf("") }

    Column(modifier = Modifier.fillMaxSize().padding(16.dp)) {
        Text("设置", style = MaterialTheme.typography.headlineSmall)
        Spacer(Modifier.height(16.dp))

        Text("账号", style = MaterialTheme.typography.titleSmall)
        Text("已登录：${AppState.username}（${AppState.deviceName}）",
            style = MaterialTheme.typography.bodyMedium)
        Spacer(Modifier.height(8.dp))
        Button(onClick = { AppState.syncService?.logout(); onLogout() }) { Text("退出登录") }

        Spacer(Modifier.height(24.dp))
        Text("服务器地址（留空用默认服务器）", style = MaterialTheme.typography.titleSmall)
        OutlinedTextField(server, { server = it }, singleLine = true,
            modifier = Modifier.fillMaxWidth(),
            placeholder = { Text("如 http://127.0.0.1:8082（联调）") })
        Spacer(Modifier.height(8.dp))
        Button(onClick = {
            AppState.serverOverride = server.trim()
            status = "已保存，重启同步生效"
        }) { Text("保存") }

        Spacer(Modifier.height(24.dp))
        Text("无障碍服务", style = MaterialTheme.typography.titleSmall)
        val enabled = remember { ClipboardListener.isEnabled(context) }
        Text(if (enabled) "已开启：剪贴板监听运行中" else "未开启：请开启以监听剪贴板",
            style = MaterialTheme.typography.bodyMedium)
        Spacer(Modifier.height(8.dp))
        Button(onClick = {
            context.startActivity(Intent(Settings.ACTION_ACCESSIBILITY_SETTINGS))
        }) { Text(if (enabled) "重新检查" else "去开启") }

        Spacer(Modifier.height(24.dp))
        Text("同步状态：${status.ifEmpty { AppState.syncService?.let { "已连接" } ?: "未启动" }}",
            style = MaterialTheme.typography.bodyMedium)

        Spacer(Modifier.weight(1f))
        TextButton(onClick = onBack) { Text("返回") }
    }
}
```

（状态显示简化：真实连接状态由 `SyncService.onStatus` 回调驱动，UI 上如需精确显示可后续增强。）

- [ ] **Step 3: 改造 MainActivity 路由**

```kotlin
package com.starry.clipboardtool

import android.os.Bundle
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.compose.foundation.layout.padding
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Modifier
import com.starry.clipboardtool.ui.HistoryScreen
import com.starry.clipboardtool.ui.LoginScreen
import com.starry.clipboardtool.ui.SettingsScreen

class MainActivity : ComponentActivity() {
    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        setContent {
            MaterialTheme {
                var screen by remember { mutableStateOf("main") } // main | settings
                val loggedIn = AppState.token.isNotEmpty()
                if (!loggedIn) {
                    LoginScreen(onLoggedIn = { })
                } else when (screen) {
                    "main" -> HistoryScreen(onOpenSettings = { screen = "settings" })
                    else -> SettingsScreen(
                        onBack = { screen = "main" },
                        onLogout = { screen = "main" })
                }
            }
        }
    }
}
```

- [ ] **Step 4: 构建验证**

Run: `gradle.bat -p Android :app:compileDebugKotlin 2>&1 | Select-String "BUILD|error|FAILED"`
Expected: BUILD SUCCESSFUL（`HistoryScreen` 由 T7 提供——**本任务先建 HistoryScreen 空壳**：`fun HistoryScreen(onOpenSettings: () -> Unit) {}`，T7 填充）。

- [ ] **Step 5: 提交**

```bash
git add Android/app/src/main/java/com/starry/clipboardtool/ui/ Android/app/src/main/java/com/starry/clipboardtool/MainActivity.kt
git commit -m "feat: 登录页与设置页（无障碍引导/服务器地址/退出登录）"
```

---

### Task 7: 历史列表 UI

**Covers:** S5

**Files:**
- Create: `Android/app/src/main/java/com/starry/clipboardtool/ui/HistoryScreen.kt`（替换 T6 空壳）

**Interfaces:**
- Consumes: `AppState.store.query(...)`、`ClipboardEvents.writeClip`、`SyncService.onHistoryChanged`
- Produces: LazyColumn 历史列表：时间分组（今天/昨天/更早）、条目（文本预览/图片缩略图/文件图标+名）、来源标签（pc→"电脑"蓝色角标）、顶部类型筛选（全部/文本/图片/文件）、点击条目写回剪贴板 + Toast、长按删除；顶部设置按钮

- [ ] **Step 1: 实现 HistoryScreen.kt**

```kotlin
package com.starry.clipboardtool.ui

import android.widget.Toast
import androidx.compose.foundation.Image
import androidx.compose.foundation.clickable
import androidx.compose.foundation.combinedClickable
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Settings
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.asImageBitmap
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import com.starry.clipboardtool.AppState
import com.starry.clipboardtool.data.Entry
import com.starry.clipboardtool.sync.ClipboardEvents
import java.io.File
import java.text.SimpleDateFormat
import java.util.Date
import java.util.Locale

@Composable
fun HistoryScreen(onOpenSettings: () -> Unit) {
    val context = LocalContext.current
    var refresh by remember { mutableStateOf(0) }
    var typeFilter by remember { mutableStateOf("") } // "" | text | image | file

    DisposableEffect(Unit) {
        AppState.syncService?.onHistoryChanged = { refresh++ }
        onDispose { AppState.syncService?.onHistoryChanged = {} }
    }

    val entries = remember(refresh, typeFilter) {
        AppState.store.query(null, typeFilter.ifEmpty { null }, null)
    }

    Column(modifier = Modifier.fillMaxSize()) {
        // 顶部：标题 + 设置 + 类型筛选
        Row(modifier = Modifier.fillMaxWidth().padding(horizontal = 16.dp, vertical = 8.dp),
            verticalAlignment = Alignment.CenterVertically) {
            Text("剪贴板历史", style = MaterialTheme.typography.titleLarge, modifier = Modifier.weight(1f))
            IconButton(onClick = onOpenSettings) {
                Icon(Icons.Filled.Settings, contentDescription = "设置")
            }
        }
        Row(modifier = Modifier.fillMaxWidth().padding(horizontal = 16.dp)) {
            listOf("全部" to "", "文本" to "text", "图片" to "image", "文件" to "file").forEach { (label, t) ->
                FilterChip(
                    selected = typeFilter == t,
                    onClick = { typeFilter = t },
                    label = { Text(label) },
                    modifier = Modifier.padding(end = 8.dp))
            }
        }
        Spacer(Modifier.height(4.dp))
        LazyColumn(modifier = Modifier.fillMaxSize()) {
            val grouped = entries.groupBy { groupLabel(it.createdAt) }
            grouped.forEach { (label, list) ->
                item(key = "h_$label") {
                    Text(label, style = MaterialTheme.typography.labelMedium,
                        color = MaterialTheme.colorScheme.onSurfaceVariant,
                        modifier = Modifier.padding(horizontal = 16.dp, vertical = 4.dp))
                }
                items(list, key = { it.id }) { entry ->
                    EntryRow(entry, onClick = {
                        ClipboardEvents.writeClip(context, entry, AppState.store)
                        Toast.makeText(context, "已写入剪贴板", Toast.LENGTH_SHORT).show()
                    }, onLongClick = {
                        AppState.store.delete(entry.id)
                        refresh++
                        Toast.makeText(context, "已删除", Toast.LENGTH_SHORT).show()
                    })
                }
            }
            if (entries.isEmpty()) {
                item { Text("暂无历史", modifier = Modifier.padding(24.dp),
                    color = MaterialTheme.colorScheme.onSurfaceVariant) }
            }
        }
    }
}

@Composable
private fun EntryRow(entry: Entry, onClick: () -> Unit, onLongClick: () -> Unit) {
    Row(
        modifier = Modifier.fillMaxWidth()
            .combinedClickable(onClick = onClick, onLongClick = onLongClick)
            .padding(horizontal = 16.dp, vertical = 8.dp),
        verticalAlignment = Alignment.CenterVertically) {
        when (entry.type) {
            "image" -> {
                val bmp = remember(entry.id) {
                    entry.thumb?.let { android.graphics.BitmapFactory.decodeByteArray(it, 0, it.size) }
                }
                if (bmp != null) {
                    Image(bmp.asImageBitmap(), contentDescription = null,
                        modifier = Modifier.size(48.dp))
                } else {
                    Box(Modifier.size(48.dp), contentAlignment = Alignment.Center) { Text("图") }
                }
            }
            "file" -> {
                Box(Modifier.size(48.dp), contentAlignment = Alignment.Center) {
                    Text("📄", style = MaterialTheme.typography.headlineSmall)
                }
            }
            else -> {}
        }
        Spacer(Modifier.width(12.dp))
        Column(modifier = Modifier.weight(1f)) {
            Text(entry.content, maxLines = 2, overflow = TextOverflow.Ellipsis,
                style = MaterialTheme.typography.bodyMedium)
            if (entry.source == "pc") {
                Text("电脑", color = Color(0xFF0078D4), style = MaterialTheme.typography.labelSmall)
            }
        }
    }
}

private fun groupLabel(ts: Long): String {
    val now = System.currentTimeMillis() / 1000
    val day = 86400L
    val date = SimpleDateFormat("M月d日", Locale.CHINA).format(Date(ts * 1000))
    return when {
        now - ts < day -> "今天"
        now - ts < 2 * day -> "昨天"
        else -> date
    }
}
```

（`remember(entry.id)` 内 decode 缩略图；长按删除。图片/文件条目 content 是本地路径，文本条目显示全文截断。）

- [ ] **Step 2: 构建验证**

Run: `gradle.bat -p Android :app:assembleDebug 2>&1 | Select-String "BUILD|error|FAILED"`
Expected: BUILD SUCCESSFUL，`app-debug.apk` 产出。

- [ ] **Step 3: 提交**

```bash
git add Android/app/src/main/java/com/starry/clipboardtool/ui/HistoryScreen.kt
git commit -m "feat: 历史列表 UI（分组/来源标签/筛选/点击回填/长按删除）"
```

---

### Task 8: 真机联调验证（小米 14 Pro）

**Covers:** S4, S5

**Files:**
- Create: `docs/compose/reports/` 更新（最终报告）
- 无产品代码改动（除非联调发现 bug）

**Interfaces:**
- Consumes: 全部 M3 组件 + 本地 SyncServer（M1）+ Windows exe（M2）+ adb reverse

- [ ] **Step 1: 连接真机并安装**

```powershell
& "C:\Android\platform-tools\adb.exe" devices
# 手机开启开发者模式 + USB 调试，确认设备出现（小米：设置→我的设备→全部参数→连点版本号→开发者选项→USB调试）
& "C:\Android\platform-tools\adb.exe" install -r "Android\app\build\outputs\apk\debug\app-debug.apk"
```

Expected: `Success`。

- [ ] **Step 2: 启动本地服务器 + adb reverse**

```powershell
# SyncServer/ 目录
Start-Process go -ArgumentList "run",".","-addr","127.0.0.1:8082","-db","C:\Users\Starry\AppData\Local\Temp\sync_m3.db" -WindowStyle Hidden
Start-Sleep -Seconds 40
& "C:\Android\platform-tools\adb.exe" reverse tcp:8082 tcp:8082
```

Expected: reverse 成功（手机 127.0.0.1:8082 → 电脑 127.0.0.1:8082）。

- [ ] **Step 3: App 内配置与验证矩阵**

1. 打开 App → 登录页 → 账号 `m3test`/密码/设备名"小米14Pro" → 注册 → 进入历史页
2. 设置 → 服务器地址填 `http://127.0.0.1:8082` → 保存 → 重启 App（或退出登录重登）
3. 设置 → 无障碍服务 → 去开启"剪贴板同步"服务
4. 手机复制文本 → App 历史出现条目（本机）→ 电脑端（Windows exe 或 phone-sim 监听）收到
5. 电脑端 phone-sim 发文本/图片/文件 → 手机 App 历史出现"电脑"标签条目 → 剪贴板自动写入（在任意输入框粘贴验证）
6. 重启 App → 历史回放不重复（lastSeq 去重）
7. 防回环：手机自动写入的剪贴板内容不会再次上传（服务器 history 无重复 origin）

验证命令（电脑端监听）：

```powershell
cd SyncServer
go run ./cmd/phone-sim -base http://127.0.0.1:8082 -user m3test -pass secret123 -device pc-sim -kind text -text "来自电脑的测试"   # 手机端应收到并写剪贴板
```

- [ ] **Step 4: 异常路径抽查**

- 关闭 App（从最近任务划掉）→ 无障碍服务被杀后监听停止（已知限制）→ 重新打开 App 引导开启
- 服务器停掉 → App 显示"连接断开，重连中…"，不崩溃；服务器恢复 → 自动重连
- 退出登录 → 历史保留（本地数据不清空），同步停止

- [ ] **Step 5: 更新最终报告并提交**

```bash
git add docs/compose/ Android/
git commit -m "docs: M3 完成，更新最终报告"
```

