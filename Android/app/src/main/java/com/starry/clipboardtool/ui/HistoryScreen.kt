package com.starry.clipboardtool.ui

import androidx.compose.foundation.Image
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.combinedClickable
import androidx.compose.foundation.isSystemInDarkTheme
import androidx.compose.foundation.layout.Arrangement
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
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.ArrowDropDown
import androidx.compose.material.icons.filled.Check
import androidx.compose.material.icons.filled.Close
import androidx.compose.material.icons.filled.Search
import androidx.compose.material.icons.filled.Settings
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.DropdownMenu
import androidx.compose.material3.DropdownMenuItem
import androidx.compose.material3.FilterChip
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Scaffold
import androidx.compose.material3.SnackbarHost
import androidx.compose.material3.SnackbarHostState
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.DisposableEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.asImageBitmap
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.starry.clipboardtool.AppState
import com.starry.clipboardtool.data.Entry
import com.starry.clipboardtool.sync.ClipboardEvents
import com.starry.clipboardtool.ui.theme.ContentPasteIcon
import com.starry.clipboardtool.ui.theme.PcBadge
import com.starry.clipboardtool.ui.theme.PcBadgeDark
import com.starry.clipboardtool.ui.theme.PhoneBadge
import com.starry.clipboardtool.ui.theme.PhoneBadgeDark
import com.starry.clipboardtool.util.MediaSaver
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import java.text.SimpleDateFormat
import java.util.Date
import java.util.Locale

@Composable
fun HistoryScreen(onOpenSettings: () -> Unit) {
    val context = LocalContext.current
    val scope = rememberCoroutineScope()
    val snackbar = remember { SnackbarHostState() }
    var refresh by remember { mutableIntStateOf(0) }
    var search by remember { mutableStateOf("") }
    var typeFilter by remember { mutableStateOf("") } // "" | text | image | file
    var sourceFilter by remember { mutableStateOf("") } // "" | local | pc
    var sourceMenuOpen by remember { mutableStateOf(false) }
    var deleteTarget by remember { mutableStateOf<Entry?>(null) }
    var selecting by remember { mutableStateOf(false) } // 多选模式
    var selectedIds by remember { mutableStateOf(setOf<Long>()) }
    var batchDelete by remember { mutableStateOf(false) } // 多选删除弹窗

    val toggle: (Long) -> Unit = { id ->
        selectedIds = if (id in selectedIds) selectedIds - id else selectedIds + id
    }
    val exitSelection: () -> Unit = {
        selecting = false
        selectedIds = emptySet()
        batchDelete = false
        refresh++
    }

    DisposableEffect(Unit) {
        AppState.syncService?.onHistoryChanged = { refresh++ }
        onDispose { AppState.syncService?.onHistoryChanged = {} }
    }

    val entries = remember(refresh, search, typeFilter, sourceFilter) {
        AppState.store.query(search.ifBlank { null }, typeFilter.ifEmpty { null }, sourceFilter.ifEmpty { null })
    }

    Scaffold(snackbarHost = { SnackbarHost(snackbar) }) { padding ->
        Column(modifier = Modifier.fillMaxSize().padding(padding)) {
            Row(modifier = Modifier.fillMaxWidth().padding(horizontal = 16.dp, vertical = 8.dp),
                verticalAlignment = Alignment.CenterVertically) {
                if (selecting) {
                    Text("已选 ${selectedIds.size} 条", style = MaterialTheme.typography.titleLarge,
                        modifier = Modifier.weight(1f))
                    TextButton(onClick = {
                        selectedIds = if (selectedIds.size == entries.size) emptySet()
                        else entries.map { it.id }.toSet()
                    }) { Text("全选") }
                    TextButton(onClick = {
                        selecting = false
                        selectedIds = emptySet()
                    }) { Text("取消") }
                } else {
                    Text("剪贴板历史", style = MaterialTheme.typography.titleLarge,
                        modifier = Modifier.weight(1f))
                    Box {
                        val label = when (sourceFilter) {
                            "local" -> "手机"
                            "pc" -> "电脑"
                            else -> "全部来源"
                        }
                        TextButton(onClick = { sourceMenuOpen = true }) {
                            Text(label)
                            Icon(Icons.Filled.ArrowDropDown, contentDescription = "切换来源筛选")
                        }
                        DropdownMenu(expanded = sourceMenuOpen,
                            onDismissRequest = { sourceMenuOpen = false }) {
                            listOf("全部来源" to "", "手机" to "local", "电脑" to "pc").forEach { (l, v) ->
                                DropdownMenuItem(text = { Text(l) },
                                    onClick = { sourceFilter = v; sourceMenuOpen = false })
                            }
                        }
                    }
                    TextButton(onClick = { selecting = true }) { Text("选择") }
                    IconButton(onClick = onOpenSettings) {
                        Icon(Icons.Filled.Settings, contentDescription = "设置")
                    }
                }
            }
            OutlinedTextField(
                value = search,
                onValueChange = { search = it },
                placeholder = { Text("搜索历史（文本/文件内容）") },
                leadingIcon = { Icon(Icons.Filled.Search, contentDescription = null) },
                trailingIcon = {
                    if (search.isNotEmpty())
                        IconButton(onClick = { search = "" }) {
                            Icon(Icons.Filled.Close, contentDescription = "清除搜索")
                        }
                },
                singleLine = true,
                shape = RoundedCornerShape(12.dp),
                modifier = Modifier.fillMaxWidth().padding(horizontal = 16.dp))
            Row(modifier = Modifier.fillMaxWidth().padding(horizontal = 16.dp)) {
                listOf("全部" to "", "文本" to "text", "图片" to "image", "文件" to "file").forEach { (label, t) ->
                    FilterChip(
                        selected = typeFilter == t,
                        onClick = { typeFilter = t },
                        label = { Text(label) },
                        modifier = Modifier.padding(end = 8.dp))
                }
            }
            Spacer(Modifier.height(8.dp))
            LazyColumn(modifier = Modifier.weight(1f).fillMaxWidth()) {
                val grouped = entries.groupBy { groupLabel(it.createdAt) }
                grouped.forEach { (label, list) ->
                    item(key = "h_$label") {
                        Text(label, style = MaterialTheme.typography.labelMedium,
                            color = MaterialTheme.colorScheme.onSurfaceVariant,
                            modifier = Modifier.padding(horizontal = 20.dp, vertical = 8.dp))
                    }
                    items(list, key = { it.id }) { entry ->
                        if (selecting) {
                            val checked = entry.id in selectedIds
                            EntryCard(entry,
                                trailing = {
                                    Box(
                                        Modifier.size(22.dp)
                                            .border(1.5.dp,
                                                if (checked) MaterialTheme.colorScheme.primary else Color(0xFFB0B0B0),
                                                CircleShape)
                                            .clickable { toggle(entry.id) },
                                        contentAlignment = Alignment.Center) {
                                        if (checked)
                                            Icon(Icons.Filled.Check, contentDescription = null,
                                                tint = MaterialTheme.colorScheme.primary,
                                                modifier = Modifier.size(16.dp))
                                    }
                                },
                                onClick = { toggle(entry.id) },
                                onLongClick = { toggle(entry.id) })
                        } else {
                            EntryCard(entry, onClick = {
                                ClipboardEvents.writeClip(context, entry, AppState.store)
                                scope.launch { snackbar.showSnackbar("已写入剪贴板") }
                            }, onLongClick = {
                                deleteTarget = entry
                            })
                        }
                    }
                }
                if (entries.isEmpty()) {
                    item {
                        Column(modifier = Modifier.fillMaxWidth().padding(top = 64.dp),
                            horizontalAlignment = Alignment.CenterHorizontally) {
                            Icon(ContentPasteIcon, contentDescription = null,
                                tint = MaterialTheme.colorScheme.onSurfaceVariant,
                                modifier = Modifier.size(48.dp))
                            Spacer(Modifier.height(12.dp))
                            Text("暂无历史", style = MaterialTheme.typography.titleMedium,
                                color = MaterialTheme.colorScheme.onSurfaceVariant)
                            Spacer(Modifier.height(4.dp))
                            Text("复制任意内容后将出现在这里",
                                style = MaterialTheme.typography.bodySmall,
                                color = MaterialTheme.colorScheme.onSurfaceVariant)
                        }
                    }
                }
            }
            // 多选模式底部操作栏
            if (selecting) {
                Row(modifier = Modifier.fillMaxWidth().padding(16.dp),
                    horizontalArrangement = Arrangement.End,
                    verticalAlignment = Alignment.CenterVertically) {
                    TextButton(
                        onClick = { batchDelete = true },
                        enabled = selectedIds.isNotEmpty()) { Text("删除") }
                }
            }
        }
    }

    // 单条长按对话框：保存到下载（图片/文件）/ 本地删除 / 彻底删除（同步删除服务器与其他设备）
    deleteTarget?.let { target ->
        AlertDialog(
            onDismissRequest = { deleteTarget = null },
            title = { Text("条目操作") },
            text = {
                Column {
                    Text(if (target.type == "file")
                        "文件条目仅支持本地删除（文件无法跨端彻底删除）。"
                    else
                        "本地删除：仅移除本机记录。\n彻底删除：同步移除服务器与其他设备上的相同内容。")
                    if (target.type == "image" || target.type == "file") {
                        Spacer(Modifier.height(12.dp))
                        OutlinedButton(
                            onClick = {
                                val t = target
                                deleteTarget = null
                                scope.launch {
                                    val err = withContext(Dispatchers.IO) {
                                        MediaSaver.saveToDownloads(context, t)
                                    }
                                    snackbar.showSnackbar(err ?: "已保存到下载目录：${if (t.type == "image") "剪贴板_${t.id}.png" else t.content.substringAfterLast('/')}")
                                }
                            },
                            modifier = Modifier.fillMaxWidth()) {
                            Text("保存到下载目录")
                        }
                    }
                }
            },
            confirmButton = {
                Row {
                    if (target.type != "file") {
                        TextButton(onClick = {
                            AppState.syncService?.deleteEntry(target, fully = true)
                            deleteTarget = null
                        }) { Text("彻底删除") }
                    }
                    TextButton(onClick = {
                        AppState.syncService?.deleteEntry(target, fully = false)
                        deleteTarget = null
                    }) { Text("本地删除") }
                    TextButton(onClick = { deleteTarget = null }) { Text("取消") }
                }
            })
    }

    // 多选批量删除对话框
    if (batchDelete) {
        val selected = entries.filter { it.id in selectedIds }
        val hasFile = selected.any { it.type == "file" }
        AlertDialog(
            onDismissRequest = { batchDelete = false },
            title = { Text("删除 ${selected.size} 条记录") },
            text = {
                Text(if (hasFile)
                    "本地删除：仅移除本机记录。\n彻底删除：文本/图片同步移除服务器与其他设备上的相同内容（文件条目仅本地删除）。"
                else
                    "本地删除：仅移除本机记录。\n彻底删除：同步移除服务器与其他设备上的相同内容。")
            },
            confirmButton = {
                Row {
                    if (!hasFile) {
                        TextButton(onClick = {
                            selected.forEach { AppState.syncService?.deleteEntry(it, fully = true) }
                            exitSelection()
                        }) { Text("彻底删除") }
                    }
                    TextButton(onClick = {
                        selected.forEach { entry ->
                            // 文件条目无法跨端彻底删除，固定本地删除
                            AppState.syncService?.deleteEntry(entry, fully = entry.type != "file")
                        }
                        exitSelection()
                    }) { Text("本地删除") }
                    TextButton(onClick = { batchDelete = false }) { Text("取消") }
                }
            })
    }
}

/** 卡片式条目：圆角 surface 容器 + 彩色来源徽章 + 相对时间。 */
@OptIn(androidx.compose.foundation.ExperimentalFoundationApi::class)
@Composable
private fun EntryCard(entry: Entry, onClick: () -> Unit, onLongClick: () -> Unit,
                      trailing: (@Composable () -> Unit)? = null) {
    Surface(
        modifier = Modifier.fillMaxWidth().padding(horizontal = 16.dp, vertical = 3.dp),
        shape = RoundedCornerShape(14.dp),
        color = MaterialTheme.colorScheme.surfaceContainer) {
        Row(
            modifier = Modifier.fillMaxWidth()
                .combinedClickable(onClick = onClick, onLongClick = onLongClick)
                .padding(horizontal = 12.dp, vertical = 10.dp),
            verticalAlignment = Alignment.CenterVertically) {
            when (entry.type) {
                "image" -> {
                    val bmp = remember(entry.id) {
                        entry.thumb?.let { android.graphics.BitmapFactory.decodeByteArray(it, 0, it.size) }
                    }
                    if (bmp != null) {
                        Image(bmp.asImageBitmap(), contentDescription = null,
                            modifier = Modifier.size(48.dp)
                                .clip(RoundedCornerShape(10.dp)))
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
                Spacer(Modifier.height(4.dp))
                Row(verticalAlignment = Alignment.CenterVertically) {
                    SourceBadge(entry.source)
                    Spacer(Modifier.width(8.dp))
                    Text(relativeTime(entry.createdAt),
                        color = MaterialTheme.colorScheme.onSurfaceVariant,
                        style = MaterialTheme.typography.labelSmall)
                }
            }
            trailing?.invoke()
        }
    }
}

/** 来源徽章：电脑=Windows 蓝、手机=绿（暗色模式用浅色变体）。 */
@Composable
private fun SourceBadge(source: String) {
    val dark = isSystemInDarkTheme()
    val (bg, fg) = when (source) {
        "pc" -> if (dark) PcBadgeDark to Color(0xFF002B4E) else PcBadge to Color.White
        else -> if (dark) PhoneBadgeDark to Color(0xFF0A3D10) else PhoneBadge to Color.White
    }
    Surface(shape = RoundedCornerShape(6.dp), color = bg) {
        Text(if (source == "pc") "电脑" else "手机",
            color = fg, fontSize = 10.sp, fontWeight = FontWeight.SemiBold,
            modifier = Modifier.padding(horizontal = 6.dp, vertical = 2.dp))
    }
}

/** 相对时间：今天显示 HH:mm，昨天显示"昨天"，更早显示 M月d日。 */
private fun relativeTime(ts: Long): String {
    val now = System.currentTimeMillis() / 1000
    val day = 86400L
    return when {
        now - ts < day -> SimpleDateFormat("HH:mm", Locale.CHINA).format(Date(ts * 1000))
        now - ts < 2 * day -> "昨天"
        else -> SimpleDateFormat("M月d日", Locale.CHINA).format(Date(ts * 1000))
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
