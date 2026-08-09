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
        // 已登录则自动启动同步（重启后无需重新登录）
        if (token.isNotEmpty()) syncService?.start()
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
