# 清空二级选项 + 置顶多端同步 + 服务器 7 天保留（置顶除外）+ 删除不碰源文件

日期：2026-08-09
范围：Windows 主程序（ClipboardTool）、Android（clipboard-tool android-dev）、Go 同步服务器（SyncServer）

## [S1] 问题与需求

现状问题：

1. **清空历史只有本机清空**：Windows 主窗口"清空"按钮与托盘"清空历史"只执行本地 `ClipboardStore.Clear()`（保留置顶、不同步）；没有任何多端清空能力。
2. **置顶是纯本机状态**：Windows 有 `Pinned` 字段（不同步），Android 端根本没有置顶功能。
3. **服务器 7 天保留无置顶例外**：`cleanup.go` 对所有 `messages`/`media` 超过 7 天一律删除，置顶内容也会被清掉。
4. **删除会误删源文件（bug）**：Windows 捕获本机复制文件时条目 `Content = 源文件路径`（`ClipboardMonitor.cs:136`），`TryDeleteFile` 会对该路径执行 `File.Delete`——删除历史里本机复制的文件条目会真的删除磁盘上的源文件。

用户需求（2026-08-09 确认）：

- **R1 清空二级选项**：清空历史提供"本机清空 / 彻底清空（多端清空）"两个选项。Windows 主窗口按钮 + 托盘菜单均改；Android 复用现有多选删除（补齐彻底删除按钮，解除文件条目限制）。
- **R2 置顶多端同步**：置顶状态跨端同步；Android 端新增置顶 UI 与置顶优先排序。
- **R3 服务器保留策略**：服务器最多保留 7 天记录（含 media 文件），**置顶条目除外**（置顶内容永久保留）。
- **R4 删除语义**：本机删除/清空只删本机数据目录内的副本与记录，**永不触碰数据目录外的源文件**；**彻底删除/彻底清空要同时删除服务器上对应的消息记录与 media 数据**。

## [S2] 方案总览

- 扩展同步消息协议：新增 `pin`、`clear` 两种消息类型；`delete` 消息语义升级为"服务器同时删除该 hash 的内容消息与 media"。
- 服务器：`messages` 表加 `hash` 列、新增 `pins` 表；`ws.go` 对 `delete`/`clear`/`pin` 做特殊处理；`Cleanup` 跳过置顶内容；media 上传时计算字节 hash。
- Windows：清空入口改二级；`SetPinned`/删除/清空发同步消息；`TryDeleteFile` 只删数据目录内文件；`ApplyRemote` 增加 `pin`/`clear` 分支。
- Android：新增置顶 UI 与排序（entries 表加 pinned 列并迁移）；多选删除补齐彻底删除；`applyRemote` 增加 `pin`/`clear` 分支。

服务器为通用消息转发器（任何 type 入库 + 广播 + fetchHistory 回放），协议扩展不需要改广播机制。

## [S3] 同步消息协议

### S3.1 消息类型

| type | payload | 方向 | 语义 |
|---|---|---|---|
| `clip_text` / `clip_image` / `clip_file` | 现有 | 任意端→服务器 | 内容消息（现状不变） |
| `delete` | `{hash}` | 任意端→服务器 | 彻底删除标记：服务器取消该 hash 的置顶标记（pins），落 delete 记录并广播；对端按 hash 删本地；服务器内容由 cleanup 按标记清除（S4.3） |
| `pin` | `{hash, pinned}` | 任意端→服务器 | 置顶/取消置顶；服务器 upsert pins 表，落 pin 记录并广播；对端按 hash 设置本地置顶 |
| `clear` | `{}` | 任意端→服务器 | 彻底清空标记：落 clear 记录并广播；对端本地清空（保留置顶）；服务器在 clear 时间点之前的所有非置顶内容由 cleanup 清除（S4.3） |

**删除策略（用户 2026-08-09 决定）**：服务器对内容消息与 media **不做立即物理删除**，只落标记（delete/clear 记录）；真正清除由 7 天 cleanup 按标记执行（软删除，可恢复，见 S4.3）。

### S3.2 幂等与回放

- 服务器 `messages` 表按 seq 顺序回放；`delete`/`clear` 幂等（重复执行无副作用：删不存在的 hash 无害）。
- 对端 `fetchHistory` 按 seq 应用：`clear` 之前的旧内容先入库再被 `clear` 清掉、`clear` 之后的内容保留；`delete` 同理（先加回再删）。
- 服务器保留所有类型消息记录（含 delete/pin/clear），清理规则见 S4.3。

## [S4] 服务器改动（Go / SyncServer）

### S4.1 Schema 迁移（store.go `migrate()`）

- `messages` 表新增列 `hash TEXT`（内容哈希，可空；delete/pin/clear 消息为 payload 中的 hash 或 NULL）。
- 新增表 `pins`：
  ```sql
  CREATE TABLE IF NOT EXISTS pins (
    user_id INTEGER NOT NULL,
    hash TEXT NOT NULL,
    pinned INTEGER NOT NULL DEFAULT 0,
    updated_at INTEGER NOT NULL,
    PRIMARY KEY (user_id, hash)
  );
  ```
- `media` 表新增列 `hash TEXT`（上传时按 data 字节计算；SQLite `ALTER TABLE ... ADD COLUMN` 需在 migrate 里做存在性检查）。

### S4.2 消息处理（ws.go `readLoop` / store.go）——软删除

- `InsertMessage` 计算并存储 hash（`messages.hash` 列）：
  - `clip_text`：`SHA256("text\0" + payload.text)`（与两端算法一致，hex 小写）。
  - `clip_image` / `clip_file`：payload 有 `mediaId`，从 media 表读取该行 `hash`（上传时已算好）填入。
  - `delete` / `pin`：解析 payload 中的 `hash` 存入（内容哈希语义统一，供 cleanup 标记匹配）。
  - `clear` 及其余类型：hash 为 NULL。
- `pin` 消息到达（ws.go 在 InsertMessage 之前判断）：解析 `{hash, pinned}`，`UpsertPin`（`INSERT ... ON CONFLICT(user_id,hash) DO UPDATE`），再走通用 InsertMessage(`pin`) 落记录并广播。
- `delete` 消息到达：解析 payload.hash，`DeletePin`（`DELETE FROM pins WHERE user_id=? AND hash=?`）——彻底删除置顶条目时取消其置顶标记；**不删除任何内容消息**，落 delete 记录并广播，内容清除交给 Cleanup（S4.3）。
- `clear` 消息到达：无特殊处理（纯标记），走通用 InsertMessage(`clear`) 落记录并广播。
- media 上传接口（media.go）：计算 `SHA256(data)` 存入 media 表的 `hash` 列。

### S4.3 Cleanup（cleanup.go / store.go Cleanup）——软删除执行

- `retentionMs` 仍为 7 天；`startCleanup` 遍历用户执行（`AllUserIDs`），所有 SQL 带 `user_id`（用户隔离）。
- 每个用户按序执行：
  1. **delete 标记删除**：`DELETE FROM messages WHERE user_id=? AND type IN ('clip_text','clip_image','clip_file') AND hash IN (SELECT hash FROM messages WHERE user_id=? AND type='delete')`——被 delete 记录标记的内容消息清除。
  2. **clear 标记删除**：`DELETE FROM messages WHERE user_id=? AND type IN ('clip_text','clip_image','clip_file') AND ts <= (SELECT COALESCE(MAX(ts),0) FROM messages WHERE user_id=? AND type='clear') AND (hash IS NULL OR hash NOT IN (SELECT hash FROM pins WHERE user_id=? AND pinned=1))`——最近一次彻底清空时间点之前的非置顶内容清除。
  3. **超期消息兜底**：`DELETE FROM messages WHERE user_id=? AND ts < ? AND (hash IS NULL OR hash NOT IN (SELECT hash FROM pins WHERE user_id=? AND pinned=1))`——delete/pin/clear 控制消息与非置顶内容超 7 天清除；**置顶内容消息永久保留**。
  4. **无引用超期 media**：`DELETE FROM media WHERE user_id=? AND created_at < ? AND id NOT IN (SELECT DISTINCT CAST(json_extract(payload,'$.mediaId') AS INTEGER) FROM messages WHERE user_id=? AND type IN ('clip_image','clip_file'))`——内容消息已删（含被 delete/clear 标记）→ media 无引用、超 7 天即清；置顶 clip_image/clip_file 消息引用的 media 永不清。
- 语义保证：彻底删除/彻底清空"7 天内自动清除服务器数据"；误删可在清除前从服务器拉回；置顶内容永不清理。

### S4.4 部署

- 重新编译 syncserver（`C:\Android\clipboard-tool\SyncServer` 交叉编译 linux/amd64），scp 到 `/opt/syncserver/`，重启服务（systemd，需确认服务名）。
- 迁移对存量库生效（migrate 加列带存在性检查）。

## [S5] Windows 端改动（ClipboardTool）

### S5.1 清空二级选项

- **主窗口**（MainWindow.xaml:210 "清空"按钮）：点击弹出二级选择对话框（本机清空 / 彻底清空 / 取消 + 说明文字）。
- **托盘**（TrayIcon.cs:28 "清空历史"）：改为子菜单（本机清空 / 彻底清空）。
- 本机清空：现有确认框文案 + `_store.Clear()`（保留置顶）。
- 彻底清空：确认框（注明"同时清空其他设备、服务器数据一并删除、不可恢复、置顶保留"）→ `_store.Clear()` + 发 `clear` 消息（失败重试 3 次，参照 DeleteEntry 模式）。

### S5.2 置顶同步

- `SyncClient` 新增 `SendPinAsync(hash, pinned)` 与 `SendClearAsync()`（`{"type":"pin","payload":{"hash":..,"pinned":..}}` / `{"type":"clear","payload":{}}`）。
- `SyncService`：
  - 新增 `SetPinned(entry, pinned)`：本地 `_store.SetPinned` + 发 pin 消息（ComputeSyncHash 计算 hash）。
  - 调用路径改造：`MainWindow.xaml.cs:147` 与 `OverlayWindow.xaml.cs:364` 的置顶菜单由直接调 `_store.SetPinned` 改为调 `SyncService.SetPinned`（同步置顶状态）。
  - `DeleteEntry` 已发 delete（S3.1 服务器升级后自动连带删除服务器内容）。
  - 新增 `ClearAll(bool fully)`：fully 时发 clear 消息，然后 `_store.Clear()`。
  - `ApplyRemote` 增加：
    - `case "pin" when hash 非空`：按 hash 找条目 `_store.SetPinnedByHash(hash, pinned)`；
    - `case "clear"`：`_store.Clear()`（保留置顶）。
- `ClipboardStore` 新增 `SetPinnedByHash(hash, pinned)`（`UPDATE entries SET pinned=? WHERE hash=?`）。

### S5.3 删除不碰源文件

- `TryDeleteFile`（ClipboardStore.cs:391）与所有删除路径（`Delete`、`DeleteByHash`、`Clear`、`Trim`、孤儿清理）改为：**仅当路径位于 `App.DataDir` 下（data/images、data/files）才 `File.Delete`**；数据目录外路径（本机复制文件的源路径）只删记录不动文件。
- 单条"彻底删除"对 Windows 本机复制的文件条目：记录删除 + 服务器内容/媒体删除（该内容 hash 在服务器可能有 media 副本），源文件不碰。

## [S6] Android 端改动（clipboard-tool android-dev）

### S6.1 置顶功能（新增）

- `LocalStore`：`entries` 表加 `pinned` 列（`ALTER TABLE` 迁移，含存在性检查）；查询排序改为 `ORDER BY pinned DESC, created_at DESC`；新增 `setPinned(id, pinned)` 与 `setPinnedByHash(hash, pinned)`。
- 长按菜单（HistoryScreen.kt EntryMenuContent）新增"置顶 / 取消置顶"项：`AppState.syncService?.setPinned(entry, !entry.pinned)`。
- `SyncService` 新增 `setPinned(entry, pinned)`：本地 `store.setPinned` + 发 pin 消息（`store.hashForSync(entry)`）；`applyRemote` 增加：
  - `"pin"` → `store.setPinnedByHash(hash, pinned)` + 刷新；
  - `"clear"` → `store.clear()` + 刷新。

### S6.2 多选删除补齐彻底删除

- 多选删除对话框（HistoryScreen.kt:262 batchDelete）：**去掉 `hasFile` 对"彻底删除"按钮的限制**（`if (!hasFile)` 改为始终显示）；文件条目也 `fully = true`（`deleteEntry(entry, fully = true)`，hashForSync 按文件内容字节，两端算法一致）。
- 删除对话框说明文案相应更新（文件条目也可彻底删除）。

### S6.3 本机 clear 语义

- Android `LocalStore.clear()` 现有为全清（无置顶概念冲突：pinned 列默认 0，置顶条目在 clear 时是否保留？——**统一为保留置顶**：`DELETE FROM entries WHERE pinned = 0`，与 Windows 一致）。

## [S7] 验证计划

- **服务器单测**：store_test/ws_test 增加——pin upsert 与回放、delete 删内容+media、clear 删非置顶、Cleanup 跳过置顶、migrate 幂等。
- **Windows**：编译通过；模拟——复制源文件→删除条目→源文件仍在；置顶→手机端置顶状态同步；彻底清空→两端清空且置顶保留、服务器 messages/media 减少。
- **Android**：编译 + adb 安装；长按置顶→PC 端同步置顶；多选含文件条目可彻底删除；收到 clear 后本机清空保留置顶。
- **服务器实机**：上传新二进制重启；检查 `/opt/syncserver/sync.db` 中 messages/media/pins 变化。
