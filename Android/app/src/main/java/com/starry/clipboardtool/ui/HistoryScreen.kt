package com.starry.clipboardtool.ui

import android.widget.Toast
import androidx.compose.foundation.Image
import androidx.compose.foundation.combinedClickable
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Settings
import androidx.compose.material3.FilterChip
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.DisposableEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.asImageBitmap
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import com.starry.clipboardtool.AppState
import com.starry.clipboardtool.data.Entry
import com.starry.clipboardtool.sync.ClipboardEvents
import java.text.SimpleDateFormat
import java.util.Date
import java.util.Locale

@Composable
fun HistoryScreen(onOpenSettings: () -> Unit) {
    val context = LocalContext.current
    var refresh by remember { mutableIntStateOf(0) }
    var typeFilter by remember { mutableStateOf("") } // "" | text | image | file

    DisposableEffect(Unit) {
        AppState.syncService?.onHistoryChanged = { refresh++ }
        onDispose { AppState.syncService?.onHistoryChanged = {} }
    }

    val entries = remember(refresh, typeFilter) {
        AppState.store.query(null, typeFilter.ifEmpty { null }, null)
    }

    Column(modifier = Modifier.fillMaxSize()) {
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
                item {
                    Text("暂无历史（复制任意内容后将出现在这里）", modifier = Modifier.padding(24.dp),
                        color = MaterialTheme.colorScheme.onSurfaceVariant)
                }
            }
        }
    }
}

@OptIn(androidx.compose.foundation.ExperimentalFoundationApi::class)
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
