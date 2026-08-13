# 清空二级选项 + 置顶多端同步 + 服务器软删除保留策略 实现计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use compose:subagent (recommended) or compose:execute to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 清空历史支持"本机清空/彻底清空"二级选项；置顶状态跨端同步；服务器改为软删除（标记 + 7 天 cleanup 清除，置顶除外）；修复 Windows 删除误删源文件 bug。

**Architecture:** 扩展同步消息协议（新增 `pin`、`clear`，`delete` 语义不变）。服务器是通用转发器（任何 type 入库+广播+回放），delete/clear 只落标记，内容清除由 Cleanup 按标记匹配执行（pins 表记录置顶，置顶内容永不清理）。两端本地逻辑：Windows 加置顶/清空同步与源文件保护；Android 新增置顶 UI 与多选彻底删除补齐。

**Tech Stack:** Go（SyncServer，modernc.org/sqlite）、C#/.NET 9 WPF（ClipboardTool）、Kotlin/Compose（Android）、SQLite（两端本地库）。

## Global Constraints

- **用户隔离（用户强规则 2026-08-09）**：服务器所有 SQL（含子查询）必须带 `user_id` 过滤；pins 表主键含 `user_id`；任何用户的删除/清空/置顶/清理不影响其他用户。
- **服务器软删除（用户决定 2026-08-09）**：delete/clear 到达服务器只落标记消息（不立即物理删除）；内容消息与 media 的物理删除由 7 天 Cleanup 按标记执行；**置顶内容（pins.pinned=1）永不清理**。
- **彻底删除语义**：所有触发彻底删除的路径（Windows 单条/多选、Android 单条/多选、两端彻底清空）都必须发 delete/clear 消息；彻底删除置顶条目时，服务器同时清除其 pins 标记。
- **删除不碰源文件**：Windows 所有删除路径（Delete/DeleteByHash/Clear/Trim/孤儿清理）只删 `App.DataDir` 目录内的副本文件；目录外路径（本机复制文件的源路径）只删记录。
- **清空保留置顶**：本机清空与多端清空两端都保留置顶条目及其文件。
- **内容哈希算法（三端一致）**：SHA-256 hex 小写；文本 = `SHA256("text\0" + content)`；图片/文件 = `SHA256(原始字节)`。Windows 已有 `ComputeSyncHash`，Android 已有 `hashOf`，服务器新增 `sha256Hex`。
- **messages.hash 列语义**：内容消息（clip_text/clip_image/clip_file）存内容哈希；delete/pin 消息存 payload 中的 hash（内容哈希语义统一，供 cleanup 标记匹配）；clear 及其余类型为 NULL。
- **消息协议**：`pin` → `{"type":"pin","payload":{"hash":"…","pinned":true}}`；`clear` → `{"type":"clear","payload":{}}`；`delete` → `{"type":"delete","payload":{"hash":"…"}}`（已有）。
- 两端版本号本轮不改（Windows/Launcher csproj 1.4.6；Android versionCode 6 / versionName 1.0.5）。
- Windows 构建：所有 dotnet 命令在 `ClipboardTool/` 目录执行；build 前杀进程 `Get-Process -Name ClipboardTool | Stop-Process -Force`；输出用 `Select-String "error|个错误|个警告"` 检查，禁止截断。
- Android 构建：`C:\gradle\gradle-8.6\bin\gradle.bat -p C:\Android\clipboard-tool\Android :app:assembleDebug --offline --no-daemon > .tools\out.log 2>&1` 后 `Select-String -Path .tools\out.log "error|BUILD"`。
- 服务器：`cd C:\Android\clipboard-tool\SyncServer && go test ./...`；部署 `GOOS=linux GOARCH=amd64 go build -o syncserver .` + scp 到 `root@107.175.228.83:/opt/syncserver/`（端口 1443）+ 重启 systemd 服务。

---
# 第一部分：服务器（Go / SyncServer，TDD）

### Task 1: Schema 迁移（messages.hash / media.hash / pins 表）

**Covers:** [S4.1]

**Files:**
- Modify: `C:\Android\clipboard-tool\SyncServer\store.go`
- Test: `C:\Android\clipboard-tool\SyncServer\store_test.go`

**Interfaces:**
- Produces: `func addColumn(db *sql.DB, table, col, decl string) error`（存在性检查后 ALTER TABLE）

- [ ] **Step 1: 写失败测试**

在 `store_test.go` 追加：

```go
func TestMigrateAddsPinAndHashColumns(t *testing.T) {
	s := newTestStore(t)
	defer s.Close()
	// 新库应含 pinned 所需结构：pins 表存在
	var n int
	if err := s.db.QueryRow(`SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='pins'`).Scan(&n); err != nil {
		t.Fatal(err)
	}
	if n != 1 {
		t.Fatal("pins table missing after migrate")
	}
	// 旧库（无 hash 列）模拟：建一张无 hash 列的 messages 等价表后走 addColumn
	if err := addColumn(s.db, "messages", "hash", "TEXT"); err != nil {
		t.Fatal(err)
	}
	// 幂等：再次调用不报错
	if err := addColumn(s.db, "messages", "hash", "TEXT"); err != nil {
		t.Fatal(err)
	}
}
```

`newTestStore` 若不存在则参考 `store_test.go` 现有测试的建库方式（如 `OpenStore(t.TempDir() + "/test.db")`）。

- [ ] **Step 2: 运行确认失败**

Run（在 `C:\Android\clipboard-tool\SyncServer`）: `go test -run TestMigrateAddsPinAndHashColumns -v`
Expected: 编译失败（`addColumn` 未定义 / pins 表不存在）。

- [ ] **Step 3: 实现**

`store.go` 的 `migrate()` 中，在现有 CREATE TABLE 块之后追加：

```go
	if err := addColumn(s.db, "messages", "hash", "TEXT"); err != nil {
		return err
	}
	if err := addColumn(s.db, "media", "hash", "TEXT"); err != nil {
		return err
	}
	if _, err := s.db.Exec(`
CREATE TABLE IF NOT EXISTS pins (
	user_id INTEGER NOT NULL,
	hash TEXT NOT NULL,
	pinned INTEGER NOT NULL DEFAULT 0,
	updated_at INTEGER NOT NULL,
	PRIMARY KEY (user_id, hash)
);`); err != nil {
		return err
	}
```

并在文件末尾追加：

```go
// addColumn 检查列是否存在，不存在才 ALTER TABLE（迁移幂等）。
func addColumn(db *sql.DB, table, col, decl string) error {
	rows, err := db.Query(`PRAGMA table_info(` + table + `)`)
	if err != nil {
		return err
	}
	defer rows.Close()
	for rows.Next() {
		var cid int
		var name, ctype string
		var notnull int
		var dflt sql.NullString
		var pk int
		if err := rows.Scan(&cid, &name, &ctype, &notnull, &dflt, &pk); err != nil {
			return err
		}
		if name == col {
			return nil
		}
	}
	_, err = db.Exec(`ALTER TABLE ` + table + ` ADD COLUMN ` + col + ` ` + decl)
	return err
}
```

（`database/sql` 已在 import 中。）

- [ ] **Step 4: 运行确认通过**

Run: `go test -run TestMigrateAddsPinAndHashColumns -v`
Expected: PASS

- [ ] **Step 5: 提交**

```bash
cd C:\Android\clipboard-tool\SyncServer
git add store.go store_test.go
git commit -m "feat(syncserver): schema 迁移加 messages.hash/media.hash/pins 表"
```

### Task 2: 内容哈希（InsertMedia 算 hash、InsertMessage 算内容/标记 hash）

**Covers:** [S4.2]

**Files:**
- Modify: `C:\Android\clipboard-tool\SyncServer\store.go`
- Test: `C:\Android\clipboard-tool\SyncServer\store_test.go`

**Interfaces:**
- Consumes: `addColumn`（Task 1）
- Produces: `func sha256Hex(b []byte) string`；`InsertMedia` 写 hash 列；`InsertMessage` 写 hash 列；`func (s *Store) MediaHash(userID, mediaID int64) (string, bool)`

- [ ] **Step 1: 写失败测试**

```go
func TestInsertMessageHash(t *testing.T) {
	s := newTestStore(t)
	defer s.Close()
	uid, did := newUserDevice(t, s)

	// clip_text：内容哈希
	_, err := s.InsertMessage(uid, did, "clip_text", []byte(`{"text":"hello"}`))
	if err != nil {
		t.Fatal(err)
	}
	// 期望 SHA256("text\0hello") hex 小写
	exp := sha256Hex([]byte("text\x00hello"))
	var got string
	if err := s.db.QueryRow(`SELECT hash FROM messages WHERE type='clip_text'`).Scan(&got); err != nil {
		t.Fatal(err)
	}
	if got != exp {
		t.Fatalf("clip_text hash = %q, want %q", got, exp)
	}

	// clip_image：从 media 表取 hash
	mid, err := s.InsertMedia(uid, []byte("img-bytes"))
	if err != nil {
		t.Fatal(err)
	}
	if _, err := s.InsertMessage(uid, did, "clip_image", []byte(fmt.Sprintf(`{"mediaId":%d,"name":"a.png","size":9}`, mid))); err != nil {
		t.Fatal(err)
	}
	var imgHash string
	if err := s.db.QueryRow(`SELECT hash FROM messages WHERE type='clip_image'`).Scan(&imgHash); err != nil {
		t.Fatal(err)
	}
	if imgHash != sha256Hex([]byte("img-bytes")) {
		t.Fatalf("clip_image hash = %q, want %q", imgHash, sha256Hex([]byte("img-bytes")))
	}

	// delete：payload.hash 存 messages.hash
	if _, err := s.InsertMessage(uid, did, "delete", []byte(`{"hash":"abc123"}`)); err != nil {
		t.Fatal(err)
	}
	var delHash string
	if err := s.db.QueryRow(`SELECT hash FROM messages WHERE type='delete'`).Scan(&delHash); err != nil {
		t.Fatal(err)
	}
	if delHash != "abc123" {
		t.Fatalf("delete hash = %q, want abc123", delHash)
	}
}
```

需要 `newUserDevice` helper（参考 `store_test.go` 现有 `newTestStore`/用户设备创建方式，如 `s.CreateUser` + `s.CreateDevice`）；`fmt` import。

- [ ] **Step 2: 运行确认失败**

Run: `go test -run TestInsertMessageHash -v`
Expected: FAIL（hash 列全为 NULL / sha256Hex 未定义）。

- [ ] **Step 3: 实现**

`store.go`：

```go
func sha256Hex(b []byte) string {
	sum := sha256.Sum256(b)
	return hex.EncodeToString(sum[:])
}
```

（import 增加 `"crypto/sha256"`、`"encoding/hex"`。）

`InsertMedia` 改为：

```go
func (s *Store) InsertMedia(userID int64, data []byte) (int64, error) {
	res, err := s.db.Exec(`INSERT INTO media(user_id, data, hash, created_at) VALUES(?, ?, ?, ?)`,
		userID, data, sha256Hex(data), time.Now().UnixMilli())
	if err != nil {
		return 0, err
	}
	return res.LastInsertId()
}
```

新增 `MediaHash` 与改造 `InsertMessage`：

```go
func (s *Store) MediaHash(userID, mediaID int64) (string, bool) {
	var h sql.NullString
	if err := s.db.QueryRow(`SELECT hash FROM media WHERE id = ? AND user_id = ?`, mediaID, userID).Scan(&h); err != nil || !h.Valid {
		return "", false
	}
	return h.String, true
}

func (s *Store) InsertMessage(userID, originDeviceID int64, msgType string, payload []byte) (int64, error) {
	seq, err := s.nextSeq(userID)
	if err != nil {
		return 0, err
	}
	var hash any // nil → NULL；字符串 → 文本值
	switch msgType {
	case "clip_text":
		var p struct {
			Text string `json:"text"`
		}
		if json.Unmarshal(payload, &p) == nil && p.Text != "" {
			hash = sha256Hex([]byte("text\x00" + p.Text))
		}
	case "clip_image", "clip_file":
		var p struct {
			MediaID int64 `json:"mediaId"`
		}
		if json.Unmarshal(payload, &p) == nil && p.MediaID > 0 {
			if h, ok := s.MediaHash(userID, p.MediaID); ok {
				hash = h
			}
		}
	case "delete", "pin":
		var p struct {
			Hash string `json:"hash"`
		}
		if json.Unmarshal(payload, &p) == nil && p.Hash != "" {
			hash = p.Hash
		}
	}
	res, err := s.db.Exec(`INSERT INTO messages(user_id, origin_device_id, type, seq, ts, payload, hash) VALUES(?, ?, ?, ?, ?, ?, ?)`,
		userID, originDeviceID, msgType, seq, time.Now().UnixMilli(), payload, hash)
	if err != nil {
		return 0, err
	}
	return res.LastInsertId()
}
```

- [ ] **Step 4: 运行确认通过**

Run: `go test -run TestInsertMessageHash -v`
Expected: PASS

- [ ] **Step 5: 提交**

```bash
cd C:\Android\clipboard-tool\SyncServer
git add store.go store_test.go
git commit -m "feat(syncserver): InsertMessage/InsertMedia 落内容哈希列"
```

### Task 3: delete/pin 消息到达时的服务器侧处理

**Covers:** [S4.2]

**Files:**
- Modify: `C:\Android\clipboard-tool\SyncServer\ws.go`
- Modify: `C:\Android\clipboard-tool\SyncServer\store.go`
- Test: `C:\Android\clipboard-tool\SyncServer\store_test.go`

**Interfaces:**
- Consumes: `InsertMessage`（Task 2）
- Produces: `func (s *Store) UpsertPin(userID int64, hash string, pinned bool) error`；`func (s *Store) DeletePin(userID int64, hash string) error`

- [ ] **Step 1: 写失败测试**

```go
func TestPinUpsert(t *testing.T) {
	s := newTestStore(t)
	defer s.Close()
	uid, _ := newUserDevice(t, s)
	if err := s.UpsertPin(uid, "h1", true); err != nil {
		t.Fatal(err)
	}
	var pinned int
	if err := s.db.QueryRow(`SELECT pinned FROM pins WHERE user_id=? AND hash='h1'`, uid).Scan(&pinned); err != nil {
		t.Fatal(err)
	}
	if pinned != 1 {
		t.Fatal("pinned should be 1")
	}
	// 更新为取消置顶
	if err := s.UpsertPin(uid, "h1", false); err != nil {
		t.Fatal(err)
	}
	if err := s.db.QueryRow(`SELECT pinned FROM pins WHERE user_id=? AND hash='h1'`, uid).Scan(&pinned); err != nil {
		t.Fatal(err)
	}
	if pinned != 0 {
		t.Fatal("pinned should be 0 after unpin")
	}
	// DeletePin 移除记录
	if err := s.DeletePin(uid, "h1"); err != nil {
		t.Fatal(err)
	}
	var n int
	if err := s.db.QueryRow(`SELECT COUNT(*) FROM pins WHERE user_id=? AND hash='h1'`, uid).Scan(&n); err != nil {
		t.Fatal(err)
	}
	if n != 0 {
		t.Fatal("pin record should be gone")
	}
	// 用户隔离：另一用户不受影响
	if err := s.UpsertPin(uid, "h2", true); err != nil {
		t.Fatal(err)
	}
	uid2, _ := newUserDevice(t, s)
	if err := s.DeletePin(uid2, "h2"); err != nil {
		t.Fatal(err)
	}
	if err := s.db.QueryRow(`SELECT COUNT(*) FROM pins WHERE user_id=? AND hash='h2'`, uid).Scan(&n); err != nil {
		t.Fatal(err)
	}
	if n != 1 {
		t.Fatal("user A's pin must survive user B's delete")
	}
}
```

- [ ] **Step 2: 运行确认失败**

Run: `go test -run TestPinUpsert -v`
Expected: FAIL（UpsertPin/DeletePin 未定义）。

- [ ] **Step 3: 实现**

`store.go` 追加：

```go
func (s *Store) UpsertPin(userID int64, hash string, pinned bool) error {
	_, err := s.db.Exec(`INSERT INTO pins(user_id, hash, pinned, updated_at) VALUES(?, ?, ?, ?)
		ON CONFLICT(user_id, hash) DO UPDATE SET pinned = excluded.pinned, updated_at = excluded.updated_at`,
		userID, hash, pinned, time.Now().UnixMilli())
	return err
}

// DeletePin 彻底删除置顶条目时移除其置顶标记（内容由 cleanup 清除）。
func (s *Store) DeletePin(userID int64, hash string) error {
	_, err := s.db.Exec(`DELETE FROM pins WHERE user_id = ? AND hash = ?`, userID, hash)
	return err
}
```

`ws.go` 的 `readLoop` 中，在 `json.Unmarshal` 校验之后、`InsertMessage` 之前插入：

```go
		switch in.Type {
		case "pin":
			var p struct {
				Hash   string `json:"hash"`
				Pinned bool   `json:"pinned"`
			}
			if json.Unmarshal(in.Payload, &p) == nil && p.Hash != "" {
				if err := a.store.UpsertPin(c.userID, p.Hash, p.Pinned); err != nil {
					log.Printf("upsert pin: %v", err)
				}
			}
		case "delete":
			// 彻底删除：先清除该 hash 的置顶标记（软删除，内容由 cleanup 清除）
			var p struct {
				Hash string `json:"hash"`
			}
			if json.Unmarshal(in.Payload, &p) == nil && p.Hash != "" {
				if err := a.store.DeletePin(c.userID, p.Hash); err != nil {
					log.Printf("delete pin: %v", err)
				}
			}
		}
```

- [ ] **Step 4: 运行确认通过**

Run: `go test -run TestPinUpsert -v`
Expected: PASS

- [ ] **Step 5: 提交**

```bash
cd C:\Android\clipboard-tool\SyncServer
git add ws.go store.go store_test.go
git commit -m "feat(syncserver): pin upsert + delete 清除置顶标记"
```

### Task 4: Cleanup 软删除（标记匹配 + 置顶除外 + 用户隔离）

**Covers:** [S4.3]

**Files:**
- Modify: `C:\Android\clipboard-tool\SyncServer\store.go`
- Test: `C:\Android\clipboard-tool\SyncServer\cleanup_test.go`

**Interfaces:**
- Consumes: `InsertMessage`（Task 2）、`UpsertPin`（Task 3）、`InsertMedia`（Task 2）
- Produces: 改造 `func (s *Store) Cleanup(olderThan int64) (int, error)`——**签名必须加 userID**：`func (s *Store) Cleanup(userID, olderThan int64) (int, error)`；`cleanup.go` 的 `startCleanup` 相应改为对**每个用户**执行（遍历 `SELECT DISTINCT user_id FROM messages`），或改为无参全表带关联的 SQL（见下）。

- [ ] **Step 1: 写失败测试**

```go
func TestCleanupSoftDelete(t *testing.T) {
	s := newTestStore(t)
	defer s.Close()
	uid, did := newUserDevice(t, s)
	now := time.Now().UnixMilli()

	// 内容消息：旧（超期）
	_, _ = s.InsertMessage(uid, did, "clip_text", []byte(`{"text":"old"}`))
	// 内容消息：被 delete 标记（新消息）
	mid, _ := s.InsertMedia(uid, []byte("img"))
	_, _ = s.InsertMessage(uid, did, "clip_image", []byte(fmt.Sprintf(`{"mediaId":%d,"name":"a.png","size":3}`, mid)))
	_, _ = s.InsertMessage(uid, did, "delete", []byte(`{"hash":"`+sha256Hex([]byte("img"))+`"}`))
	// 置顶内容：新消息，置顶
	_, _ = s.InsertMessage(uid, did, "clip_text", []byte(`{"text":"pinned"}`))
	_ = s.UpsertPin(uid, sha256Hex([]byte("text\x00pinned")), true)
	// clear 标记：清空时间点之前的内容
	_, _ = s.InsertMessage(uid, did, "clip_text", []byte(`{"text":"before-clear"}`))
	_, _ = s.InsertMessage(uid, did, "clear", []byte(`{}`))

	// 手动把 "old" 消息的 ts 改老，模拟超期（注意改 ts 会影响 seq 顺序，测试无碍）
	_, _ = s.db.Exec(`UPDATE messages SET ts = ? WHERE payload = ?`, now-8*24*60*60*1000, `{"text":"old"}`)

	n, err := s.Cleanup(uid, now-7*24*60*60*1000)
	if err != nil {
		t.Fatal(err)
	}
	if n == 0 {
		t.Fatal("expected cleanup to delete something")
	}
	var types []string
	rows, err := s.db.Query(`SELECT type FROM messages`)
	if err != nil {
		t.Fatal(err)
	}
	defer rows.Close()
	for rows.Next() {
		var tp string
		_ = rows.Scan(&tp)
		types = append(types, tp)
	}
	// 断言：
	// - "old"（超期非置顶）已删
	// - delete 标记的 clip_image 已删（其 media 也删）
	// - clear 之前的 clip_text（before-clear）已删，clear 之后的保留
	// - 置顶的 clip_text（pinned）保留
	// - delete / clear / pin 控制消息 7 天内保留（不删）
	got := map[string]bool{}
	for _, tp := range types {
		got[tp] = true
	}
	if got["clip_text"] != true { // 置顶的 clip_text 应保留
		t.Fatalf("pinned clip_text should survive, got %v", types)
	}
	if got["clip_image"] {
		t.Fatalf("delete-marked clip_image should be gone, got %v", types)
	}
	// before-clear 与 old 都是 clip_text，但 old 超期已删、before-clear 被 clear 标记删 → 只剩 pinned 一条 clip_text
	var clipTextCount int
	if err := s.db.QueryRow(`SELECT COUNT(*) FROM messages WHERE type='clip_text'`).Scan(&clipTextCount); err != nil {
		t.Fatal(err)
	}
	if clipTextCount != 1 {
		t.Fatalf("expected exactly 1 clip_text (pinned), got %d (%v)", clipTextCount, types)
	}
	// media 清理：delete 标记的 clip_image 已删 → 其 media 无引用应被删
	var mediaCount int
	if err := s.db.QueryRow(`SELECT COUNT(*) FROM media`).Scan(&mediaCount); err != nil {
		t.Fatal(err)
	}
	if mediaCount != 0 {
		t.Fatalf("expected media cleaned, got %d", mediaCount)
	}
	// 用户隔离：另一用户的数据不受影响
	uid2, did2 := newUserDevice(t, s)
	_, _ = s.InsertMessage(uid2, did2, "clip_text", []byte(`{"text":"other"}`))
	if _, err := s.Cleanup(uid, now-7*24*60*60*1000); err != nil {
		t.Fatal(err)
	}
	var otherCount int
	if err := s.db.QueryRow(`SELECT COUNT(*) FROM messages WHERE user_id=?`, uid2).Scan(&otherCount); err != nil {
		t.Fatal(err)
	}
	if otherCount != 1 {
		t.Fatalf("user B data affected by user A cleanup: %d", otherCount)
	}
}
```

- [ ] **Step 2: 运行确认失败**

Run: `go test -run TestCleanupSoftDelete -v`
Expected: FAIL（签名不匹配 / 断言失败——旧 Cleanup 全表删无标记逻辑）。

- [ ] **Step 3: 实现**

`store.go` 的 `Cleanup` 改为（签名加 userID）：

```go
// Cleanup 软删除：删除该用户被 delete/clear 标记的内容消息（置顶除外）、超期控制消息、无引用超期 media。
// 所有 SQL 均带 user_id，严格用户隔离。
func (s *Store) Cleanup(userID, olderThan int64) (int, error) {
	// 1) 被 delete 记录标记的内容消息（delete 记录仍存于 messages，其 hash 在 messages.hash 列）
	res, err := s.db.Exec(`DELETE FROM messages WHERE user_id = ? AND type IN ('clip_text','clip_image','clip_file')
		AND hash IN (SELECT hash FROM messages WHERE user_id = ? AND type = 'delete')`, userID, userID)
	if err != nil {
		return 0, err
	}
	n, _ := res.RowsAffected()

	// 2) clear 时间点之前的非置顶内容消息
	res, err = s.db.Exec(`DELETE FROM messages WHERE user_id = ? AND type IN ('clip_text','clip_image','clip_file')
		AND ts <= (SELECT COALESCE(MAX(ts), 0) FROM messages WHERE user_id = ? AND type = 'clear')
		AND (hash IS NULL OR hash NOT IN (SELECT hash FROM pins WHERE user_id = ? AND pinned = 1))`, userID, userID, userID)
	if err != nil {
		return 0, err
	}
	m, _ := res.RowsAffected()
	n += int(m)

	// 3) 超期消息（内容消息已在 1/2 处理；这里清 delete/pin/clear 控制消息与超期内容兜底）
	res, err = s.db.Exec(`DELETE FROM messages WHERE user_id = ? AND ts < ?
		AND (hash IS NULL OR hash NOT IN (SELECT hash FROM pins WHERE user_id = ? AND pinned = 1))`, userID, olderThan, userID)
	if err != nil {
		return 0, err
	}
	k, _ := res.RowsAffected()
	n += int(k)

	// 4) 无引用且超期的 media（被内容消息引用的保留；内容消息已删 → 引用消失）
	res, err = s.db.Exec(`DELETE FROM media WHERE user_id = ? AND created_at < ? AND id NOT IN (
		SELECT DISTINCT CAST(json_extract(payload, '$.mediaId') AS INTEGER)
		FROM messages WHERE user_id = ? AND type IN ('clip_image', 'clip_file'))`, userID, olderThan, userID)
	if err != nil {
		return 0, err
	}
	l, _ := res.RowsAffected()
	return int(n + l), nil
}
```

`cleanup.go` 的 `startCleanup` 改为遍历用户执行（新增 `func (s *Store) AllUserIDs() ([]int64, error)`）：

```go
func (s *Store) AllUserIDs() ([]int64, error) {
	rows, err := s.db.Query(`SELECT DISTINCT user_id FROM messages UNION SELECT DISTINCT user_id FROM media`)
	if err != nil {
		return nil, err
	}
	defer rows.Close()
	var ids []int64
	for rows.Next() {
		var id int64
		if err := rows.Scan(&id); err != nil {
			return nil, err
		}
		ids = append(ids, id)
	}
	return ids, rows.Err()
}
```

`cleanup.go` 的定时器回调改为：

```go
func startCleanup(s *Store, stop <-chan struct{}) {
	runCleanup := func() {
		ids, err := s.AllUserIDs()
		if err != nil {
			log.Printf("cleanup users: %v", err)
			return
		}
		olderThan := time.Now().UnixMilli() - retentionMs
		for _, id := range ids {
			if _, err := s.Cleanup(id, olderThan); err != nil {
				log.Printf("cleanup user %d: %v", id, err)
			}
		}
	}
	runCleanup()
	ticker := time.NewTicker(24 * time.Hour)
	defer ticker.Stop()
	for {
		select {
		case <-ticker.C:
			runCleanup()
		case <-stop:
			return
		}
	}
}
```

（`cleanup.go` 需要 import `"log"`。）

- [ ] **Step 4: 运行确认通过**

Run: `go test -run TestCleanupSoftDelete -v`
Expected: PASS

- [ ] **Step 5: 提交**

```bash
cd C:\Android\clipboard-tool\SyncServer
git add store.go cleanup.go cleanup_test.go
git commit -m "feat(syncserver): Cleanup 软删除——delete/clear 标记匹配 + 置顶除外 + 用户隔离"
```

### Task 5: 服务器全量测试 + 交叉编译 + 部署

**Covers:** [S4.4, S7]

**Files:**
- 无源码改动；部署产物 `syncserver`（linux/amd64）

- [ ] **Step 1: 全量测试**

Run（`C:\Android\clipboard-tool\SyncServer`）: `go test ./...`
Expected: 全部 PASS（含既有 ws/smoke/history/media/cleanup 测试）。

- [ ] **Step 2: 交叉编译**

Run: `$env:GOOS="linux"; $env:GOARCH="amd64"; go build -o syncserver .`
Expected: 生成 `syncserver`（ELF，`file syncserver` 可验证）。

- [ ] **Step 3: 备份并部署**

```bash
ssh -i ~/.ssh/id_ed25519 -p 1443 root@107.175.228.83 "cp /opt/syncserver/syncserver /opt/syncserver/syncserver.bak-20260809"
scp -i ~/.ssh/id_ed25519 -P 1443 syncserver root@107.175.228.83:/opt/syncserver/syncserver
```

- [ ] **Step 4: 重启服务并确认**

```bash
ssh -i ~/.ssh/id_ed25519 -p 1443 root@107.175.228.83 "systemctl list-units --type=service | grep -i sync; systemctl restart <sync服务名>; systemctl status <sync服务名> --no-pager | head -5"
```

Expected: 服务 active；旧版备份保留为 `.bak-20260809`（不留 `.bak` 裸名）。

- [ ] **Step 5: 实机冒烟**

```bash
ssh -i ~/.ssh/id_ed25519 -p 1443 root@107.175.228.83 "sqlite3 /opt/syncserver/sync.db 'PRAGMA table_info(messages);' && sqlite3 /opt/syncserver/sync.db 'SELECT name FROM sqlite_master WHERE type=\"table\";'"
```

Expected: messages 表含 `hash` 列；存在 `pins` 表。

- [ ] **Step 6: 提交（部署脚本/文档不涉及，无提交）**

# 第二部分：Windows 端（C# / ClipboardTool）

### Task 6: SyncClient 消息扩展（pin / clear 发送 + Pinned 解析）

**Covers:** [S3.1, S5.2]

**Files:**
- Modify: `C:\Users\Starry\OneDrive\Desktop\agent周事项\剪切板工具开发\ClipboardTool\Services\SyncClient.cs`

**Interfaces:**
- Produces: `SyncMessage` record 加 `bool? Pinned = null` 尾参；`SendPinAsync(string hash, bool pinned)`；`SendClearAsync()`（均返回 `Task<bool>`）

- [ ] **Step 1: record 加字段**

```csharp
public sealed record SyncMessage(
    string Type, long OriginDeviceId, long Seq, long Ts,
    string? Text, string? MediaId, string? Name, long Size, string? Hash = null, bool? Pinned = null);
```

- [ ] **Step 2: ParseMessage 解析 pinned**

`ParseMessage`（SyncClient.cs:145）中，payload 解析块加：

```csharp
                if (payload.TryGetProperty("pinned", out var pn)) pinned = pn.GetBoolean();
```

变量声明处加 `bool? pinned = null;`，返回构造加 `pinned` 参数（位置在 hash 之后）。

- [ ] **Step 3: 发送方法**

在 `SendDeleteAsync` 后追加：

```csharp
    /// <summary>发送置顶/取消置顶（hash 为内容哈希）。失败可重试。</summary>
    public async Task<bool> SendPinAsync(string hash, bool pinned)
    {
        return await SendAsync(JsonSerializer.Serialize(new { type = "pin", payload = new { hash, pinned } }));
    }

    /// <summary>发送彻底清空标记。失败可重试。</summary>
    public async Task<bool> SendClearAsync()
    {
        return await SendAsync(JsonSerializer.Serialize(new { type = "clear", payload = new { } }));
    }
```

- [ ] **Step 4: 验证**

Run（`ClipboardTool/` 目录，先杀进程）:
```powershell
Get-Process -Name ClipboardTool -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet build 2>&1 | Select-String "error|个错误|个警告"
```
Expected: 0 错误 0 警告。

- [ ] **Step 5: 提交**

```bash
cd "C:\Users\Starry\OneDrive\Desktop\agent周事项\剪切板工具开发"
git add ClipboardTool/Services/SyncClient.cs
git commit -m "feat: SyncClient 支持 pin/clear 消息"
```

### Task 7: SyncService 置顶/清空同步 + ApplyRemote 扩展

**Covers:** [S5.2]

**Files:**
- Modify: `C:\Users\Starry\OneDrive\Desktop\agent周事项\剪切板工具开发\ClipboardTool\Services\SyncService.cs`
- Modify: `C:\Users\Starry\OneDrive\Desktop\agent周事项\剪切板工具开发\ClipboardTool\Services\ClipboardStore.cs`
- Modify: `C:\Users\Starry\OneDrive\Desktop\agent周事项\剪切板工具开发\ClipboardTool\MainWindow.xaml.cs`（置顶调用路径）
- Modify: `C:\Users\Starry\OneDrive\Desktop\agent周事项\剪切板工具开发\ClipboardTool\OverlayWindow.xaml.cs`（置顶调用路径）

**Interfaces:**
- Consumes: `SyncClient.SendPinAsync/SendClearAsync`（Task 6）
- Produces: `ClipboardStore.SetPinnedByHash(string hash, bool pinned)`；`SyncService.SetPinned(Entry entry, bool pinned)`；`SyncService.ClearAll(bool fully)`

- [ ] **Step 1: ClipboardStore.SetPinnedByHash**

`ClipboardStore.cs` 在 `SetPinned`（311 行附近）后追加：

```csharp
    /// <summary>按内容哈希设置置顶（同步 pin 消息应用）。</summary>
    public void SetPinnedByHash(string hash, bool pinned)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "UPDATE entries SET pinned = $pinned WHERE hash = $hash COLLATE NOCASE";
        cmd.Parameters.AddWithValue("$pinned", pinned ? 1 : 0);
        cmd.Parameters.AddWithValue("$hash", hash);
        cmd.ExecuteNonQuery();
    }
```

- [ ] **Step 2: SyncService.SetPinned / ClearAll**

`SyncService.cs` 在 `DeleteEntry` 后追加：

```csharp
    /// <summary>置顶/取消置顶：本地设置 + 发 pin 消息（跨端同步）。</summary>
    public void SetPinned(Entry entry, bool pinned)
    {
        if (_running && _client is not null)
        {
            var hash = ComputeSyncHash(entry);
            if (hash is not null)
                _ = Task.Run(async () =>
                {
                    for (var attempt = 0; attempt < 3; attempt++)
                    {
                        if (await _client.SendPinAsync(hash, pinned))
                            return;
                        await Task.Delay(TimeSpan.FromSeconds(1 << attempt));
                    }
                });
        }
        _store.SetPinned(entry.Id, pinned);
    }

    /// <summary>清空历史。fully=false 仅本机；fully=true 发 clear 标记（其他设备/服务器随后清除）。置顶条目均保留。</summary>
    public void ClearAll(bool fully)
    {
        if (fully && _running && _client is not null)
            _ = Task.Run(async () =>
            {
                for (var attempt = 0; attempt < 3; attempt++)
                {
                    if (await _client.SendClearAsync())
                        return;
                    await Task.Delay(TimeSpan.FromSeconds(1 << attempt));
                }
            });
        _store.Clear();
    }
```

- [ ] **Step 3: ApplyRemote 增加 pin/clear 分支**

`ApplyRemote`（SyncService.cs:361 起）的 switch 中 `case "delete"` 之后追加：

```csharp
            case "pin" when !string.IsNullOrEmpty(m.Hash) && m.Pinned is not null:
                _store.SetPinnedByHash(m.Hash!, m.Pinned.Value);
                break;
            case "clear":
                _store.Clear();
                break;
```

- [ ] **Step 4: 置顶调用路径改造**

`MainWindow.xaml.cs:147`：

```csharp
        // 原来: _store.SetPinned(entry.Id, !entry.Pinned);
        (Application.Current as App)?.SyncService?.SetPinned(entry, !entry.Pinned);
```

`OverlayWindow.xaml.cs:364` 同样改为 `SyncService?.SetPinned(entry, !entry.Pinned);`（`Reload()` 保留）。

- [ ] **Step 5: 验证**

同 Task 6 Step 4。Expected: 0 错误 0 警告。

- [ ] **Step 6: 提交**

```bash
cd "C:\Users\Starry\OneDrive\Desktop\agent周事项\剪切板工具开发"
git add ClipboardTool/Services/SyncService.cs ClipboardTool/Services/ClipboardStore.cs ClipboardTool/MainWindow.xaml.cs ClipboardTool/OverlayWindow.xaml.cs
git commit -m "feat: 置顶/清空同步 + applyRemote pin/clear"
```

### Task 8: Windows 清空二级 UI（主窗口 + 托盘）

**Covers:** [S5.1]

**Files:**
- Modify: `C:\Users\Starry\OneDrive\Desktop\agent周事项\剪切板工具开发\ClipboardTool\MainWindow.xaml.cs`
- Modify: `C:\Users\Starry\OneDrive\Desktop\agent周事项\剪切板工具开发\ClipboardTool\Services\TrayIcon.cs`
- Modify: `C:\Users\Starry\OneDrive\Desktop\agent周事项\剪切板工具开发\ClipboardTool\App.xaml.cs`

**Interfaces:**
- Consumes: `SyncService.ClearAll(bool fully)`（Task 7）
- Produces: `TrayIcon` 事件改 `event Action<bool>? ClearHistory`；`App.OnClearHistory(bool fully)`

- [ ] **Step 1: 主窗口按钮改二级菜单**

`MainWindow.xaml.cs` 的 `OnClear`（162 行）替换为：

```csharp
    private void OnClear(object sender, RoutedEventArgs e)
    {
        // 二级选项：本机清空 / 彻底清空（多端）
        if (sender is not System.Windows.Controls.Button btn)
            return;
        var menu = new System.Windows.Controls.ContextMenu();
        var miLocal = new System.Windows.Controls.MenuItem { Header = "本机清空" };
        miLocal.Click += (_, _) => ClearLocal();
        var miFull = new System.Windows.Controls.MenuItem { Header = "彻底清空（多端）" };
        miFull.Click += (_, _) => ClearFull();
        menu.Items.Add(miLocal);
        menu.Items.Add(miFull);
        menu.PlacementTarget = btn;
        menu.IsOpen = true;
    }

    private void ClearLocal()
    {
        var result = MessageBox.Show(this, "确定要清空全部历史记录吗？置顶条目将保留。", "清空历史",
            MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (result != MessageBoxResult.Yes)
            return;
        (Application.Current as App)?.SyncService?.ClearAll(fully: false);
        Refresh();
    }

    private void ClearFull()
    {
        var result = MessageBox.Show(this, "确定要彻底清空吗？将同时清空其他设备上的历史，服务器数据在 7 天内自动清除，且不可恢复。置顶条目将保留。", "彻底清空",
            MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (result != MessageBoxResult.Yes)
            return;
        (Application.Current as App)?.SyncService?.ClearAll(fully: true);
        Refresh();
    }
```

（`MainWindow.xaml` 顶部若缺 `xmlns` 无需改——`ContextMenu`/`MenuItem` 属于 WPF 默认命名空间。）

- [ ] **Step 2: 托盘菜单改子菜单**

`TrayIcon.cs`：事件签名改为 `public event Action<bool>? ClearHistory;`；`miClear` 构造改为：

```csharp
        var miClear = new ToolStripMenuItem("清空历史");
        miClear.DropDownItems.Add("本机清空", null, (_, _) => ClearHistory?.Invoke(false));
        miClear.DropDownItems.Add("彻底清空（多端）", null, (_, _) => ClearHistory?.Invoke(true));
```

（`miClear.Click` 那行删除。）

- [ ] **Step 3: App.xaml.cs OnClearHistory(fully)**

`App.xaml.cs:357` 改为：

```csharp
    private void OnClearHistory(bool fully)
    {
        var msg = fully
            ? "确定要彻底清空吗？将同时清空其他设备上的历史，服务器数据在 7 天内自动清除，且不可恢复。置顶条目将保留。"
            : "确定要清空全部历史记录吗？置顶条目将保留。";
        var result = MessageBox.Show(msg, fully ? "彻底清空" : "清空历史",
            MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (result == MessageBoxResult.Yes)
            _syncService?.ClearAll(fully);
    }
```

（若 `App` 中没有 `_syncService` 字段，用现有 `SyncService` 引用方式，参照 `OnClearHistory` 上下文——原实现直接用 `_store.Clear()`。）

- [ ] **Step 4: 验证**

同 Task 6 Step 4。Expected: 0 错误 0 警告。

- [ ] **Step 5: 提交**

```bash
cd "C:\Users\Starry\OneDrive\Desktop\agent周事项\剪切板工具开发"
git add ClipboardTool/MainWindow.xaml.cs ClipboardTool/Services/TrayIcon.cs ClipboardTool/App.xaml.cs
git commit -m "feat: 清空历史二级选项（本机/彻底多端）"
```

### Task 9: Windows 删除永不触碰源文件

**Covers:** [S5.3]

**Files:**
- Modify: `C:\Users\Starry\OneDrive\Desktop\agent周事项\剪切板工具开发\ClipboardTool\Services\ClipboardStore.cs`

- [ ] **Step 1: TryDeleteFile 加数据目录保护**

`ClipboardStore.cs` 的 `TryDeleteFile`（391 行）替换为：

```csharp
    private static void TryDeleteFile(string? path)
    {
        try
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
                return;
            var full = Path.GetFullPath(path);
            var dataDir = Path.GetFullPath(App.DataDir).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            // 只删数据目录内的副本（图片/同步文件）；本机复制文件的源路径只删记录、绝不触碰
            if (!full.StartsWith(dataDir, StringComparison.OrdinalIgnoreCase))
                return;
            File.Delete(full);
        }
        catch (IOException)
        {
        }
    }
```

- [ ] **Step 2: 确认所有删除路径复用 TryDeleteFile**

检查 `ClipboardStore.cs` 的 `Delete`（按 id）、`DeleteByHash`、`Clear`、`Trim`、孤儿清理（`CleanupOrphanFiles`）——它们对文件路径的处理都必须经 `TryDeleteFile`（当前 `Clear` 已用；`Delete`/`DeleteByHash` 若有直接 `File.Delete` 改为 `TryDeleteFile`；孤儿清理逻辑确认只删 `images/files` 目录内文件，可保留）。用 grep 确认：

```powershell
Select-String -Path ClipboardTool\Services\ClipboardStore.cs "File.Delete"
```

Expected: 除 `TryDeleteFile` 内部外无其他 `File.Delete` 调用（若有则改为 `TryDeleteFile`）。

- [ ] **Step 3: 验证**

同 Task 6 Step 4。Expected: 0 错误 0 警告。

- [ ] **Step 4: 提交**

```bash
cd "C:\Users\Starry\OneDrive\Desktop\agent周事项\剪切板工具开发"
git add ClipboardTool/Services/ClipboardStore.cs
git commit -m "fix: 删除/清空只删数据目录内副本，不触碰源文件"
```

### Task 10: Windows 端验证

**Covers:** [S7]

**Files:**
- 无源码改动；使用 `.tools/` 测试脚本

- [ ] **Step 1: 编译并启动**

```powershell
Get-Process -Name ClipboardTool -ErrorAction SilentlyContinue | Stop-Process -Force
cd ClipboardTool
dotnet build 2>&1 | Select-String "error|个错误|个警告"
```
Expected: 0 错误 0 警告。随后运行 exe（`bin\Debug\net9.0-windows\ClipboardTool.exe --show-main`）。

- [ ] **Step 2: 模拟验证——源文件保护**

用脚本复制一个文件到剪贴板 → 条目出现 → 主窗口删除该条目（彻底/本地均可）→ 检查原文件仍在、无报错。（测试前清空 `%LocalAppData%\ClipboardTool\data\` 防脏数据。）

- [ ] **Step 3: 模拟验证——清空二级选项**

点"清空"按钮 → 出现"本机清空/彻底清空（多端）"菜单；托盘右键"清空历史"→ 同样两个子项。本机清空后置顶条目保留。

- [ ] **Step 4: 模拟验证——置顶同步 + 彻底清空**

Windows 置顶一条 → 手机端（已装新版 APK）长按菜单出现该条目且显示"取消置顶"；Windows 彻底清空 → 手机端历史被清空（置顶除外）、服务器 sync.db 的 messages/media 数量减少（`sqlite3 /opt/syncserver/sync.db 'SELECT COUNT(*) FROM messages;'`）。

- [ ] **Step 5: 提交（验证无改动，无提交）**

# 第三部分：Android 端（Kotlin / clipboard-tool android-dev）

### Task 11: LocalStore 置顶支持（迁移/排序/setPinned/clear 保留置顶）

**Covers:** [S6.1]

**Files:**
- Modify: `C:\Android\clipboard-tool\Android\app\src\main\java\com\starry\clipboardtool\data\LocalStore.kt`
- Modify: `C:\Android\clipboard-tool\Android\app\src\main\java\com\starry\clipboardtool\data\Entry.kt`

**Interfaces:**
- Produces: `Entry.pinned: Boolean = false`；`LocalStore.setPinned(id: Long, pinned: Boolean)`；`LocalStore.setPinnedByHash(hash: String, pinned: Boolean)`；查询排序 `ORDER BY pinned DESC, created_at DESC`；`clear()` 保留置顶

- [ ] **Step 1: Entry 加 pinned 字段**

`Entry.kt`：

```kotlin
data class Entry(
    val id: Long = 0,
    val type: String = "text", // text | image | file
    val content: String = "",
    val thumb: ByteArray? = null,
    val source: String = "local", // local | pc
    val createdAt: Long = 0,
    val pinned: Boolean = false,
)
```

- [ ] **Step 2: 迁移 + 查询/写入加 pinned 列**

`LocalStore.kt`：

```kotlin
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
                created_at INTEGER NOT NULL,
                pinned INTEGER NOT NULL DEFAULT 0
            );
            """.trimIndent())
        // 存量库迁移：补 pinned 列（幂等）
        val cols = HashSet<String>()
        db.rawQuery("PRAGMA table_info(entries)", null).use { c ->
            while (c.moveToNext()) cols.add(c.getString(1))
        }
        if ("pinned" !in cols) {
            db.execSQL("ALTER TABLE entries ADD COLUMN pinned INTEGER NOT NULL DEFAULT 0")
        }
    }
```

- [ ] **Step 3: query/getById 读 pinned + 排序**

`query`（137 行）SQL 改为：

```kotlin
        val sql = "SELECT id, type, content, thumb, source, created_at, pinned FROM entries" +
            (if (where.isEmpty()) "" else " WHERE ${where.joinToString(" AND ")}") +
            " ORDER BY pinned DESC, created_at DESC"
```

`Entry` 构造（144 行）加 `pinned = c.getInt(6) != 0`。`getById`（152 行）同样加列与构造参数。

- [ ] **Step 4: setPinned / setPinnedByHash / clear 保留置顶**

```kotlin
    fun setPinned(id: Long, pinned: Boolean) {
        db.execSQL("UPDATE entries SET pinned = ? WHERE id = ?",
            arrayOf(if (pinned) 1 else 0, id.toString()))
    }

    fun setPinnedByHash(hash: String, pinned: Boolean) {
        db.execSQL("UPDATE entries SET pinned = ? WHERE hash = ?",
            arrayOf(if (pinned) 1 else 0, hash))
    }

    fun clear() {
        // 保留置顶条目（与 Windows 端语义一致）；其文件由 cleanupOrphanFiles 保留
        db.execSQL("DELETE FROM entries WHERE pinned = 0")
        cleanupOrphanFiles()
    }
```

- [ ] **Step 5: 验证**

```powershell
cd C:\Android\clipboard-tool\Android
C:\gradle\gradle-8.6\bin\gradle.bat -p C:\Android\clipboard-tool\Android :app:assembleDebug --offline --no-daemon > C:\Android\.temp\build.log 2>&1
Select-String -Path C:\Android\.temp\build.log "error|BUILD"
```
Expected: BUILD SUCCESSFUL。

- [ ] **Step 6: 提交**

```bash
cd C:\Android\clipboard-tool
git add Android/app/src/main/java/com/starry/clipboardtool/data/LocalStore.kt Android/app/src/main/java/com/starry/clipboardtool/data/Entry.kt
git commit -m "feat(android): 置顶字段/迁移/排序，clear 保留置顶"
```

### Task 12: Android 同步（SyncClient pin / SyncMessage.pinned / SyncService setPinned + applyRemote）

**Covers:** [S3.1, S6.1]

**Files:**
- Modify: `C:\Android\clipboard-tool\Android\app\src\main\java\com\starry\clipboardtool\net\SyncClient.kt`
- Modify: `C:\Android\clipboard-tool\Android\app\src\main\java\com\starry\clipboardtool\net\SyncModels.kt`
- Modify: `C:\Android\clipboard-tool\Android\app\src\main\java\com\starry\clipboardtool\sync\SyncService.kt`

**Interfaces:**
- Consumes: `LocalStore.setPinned/setPinnedByHash/clear`（Task 11）
- Produces: `SyncClient.sendPin(hash: String, pinned: Boolean): Boolean`；`SyncMessage.pinned: Boolean?`；`SyncService.setPinned(entry: Entry, pinned: Boolean)`

- [ ] **Step 1: SyncMessage 加 pinned**

`SyncModels.kt`：

```kotlin
data class SyncMessage(
    val type: String,
    val originDeviceId: Long,
    val seq: Long,
    val ts: Long,
    val text: String?,
    val mediaId: String?,
    val name: String?,
    val size: Long,
    val hash: String? = null,
    val pinned: Boolean? = null,
)
```

`parseSyncMessage` 中 payload 解析块加：

```kotlin
            var pinned: Boolean? = null
            ...
            if (p.has("pinned")) pinned = p.getBoolean("pinned")
```

构造加 `pinned = pinned`。

- [ ] **Step 2: SyncClient.sendPin**

`SyncClient.kt` 在 `sendDelete` 后追加：

```kotlin
    fun sendPin(hash: String, pinned: Boolean): Boolean {
        val payload = JSONObject().put("hash", hash).put("pinned", pinned)
        return send("""{"type":"pin","payload":$payload}""")
    }
```

- [ ] **Step 3: SyncService.setPinned + applyRemote pin/clear**

`SyncService.kt` 在 `deleteEntry` 后追加：

```kotlin
    /** 置顶/取消置顶：本地设置 + 发 pin 消息（跨端同步）。 */
    fun setPinned(entry: Entry, pinned: Boolean) {
        val store = AppState.store
        store.setPinned(entry.id, pinned)
        val hash = store.hashForSync(entry)
        scope.launch {
            val c = client ?: return@launch
            repeat(3) { attempt ->
                if (c.sendPin(hash, pinned)) return@launch
                delay(3000)
            }
        }
        main.post { onHistoryChanged() }
    }
```

`applyRemote` 的 when 中 `"delete"` 分支后追加：

```kotlin
            "pin" -> {
                val hash = m.hash ?: return
                store.setPinnedByHash(hash, m.pinned == true)
                main.post { onHistoryChanged() }
            }
            "clear" -> {
                store.clear()
                main.post { onHistoryChanged() }
            }
```

- [ ] **Step 4: 验证**

同 Task 11 Step 5。Expected: BUILD SUCCESSFUL。

- [ ] **Step 5: 提交**

```bash
cd C:\Android\clipboard-tool
git add Android/app/src/main/java/com/starry/clipboardtool/net/SyncClient.kt Android/app/src/main/java/com/starry/clipboardtool/net/SyncModels.kt Android/app/src/main/java/com/starry/clipboardtool/sync/SyncService.kt
git commit -m "feat(android): pin/clear 消息支持 + 置顶同步"
```

### Task 13: Android UI（长按菜单置顶 + 多选删除补齐彻底删除）

**Covers:** [S6.1, S6.2]

**Files:**
- Modify: `C:\Android\clipboard-tool\Android\app\src\main\java\com\starry\clipboardtool\ui\HistoryScreen.kt`

**Interfaces:**
- Consumes: `SyncService.setPinned`（Task 12）、`Entry.pinned`（Task 11）

- [ ] **Step 1: 长按菜单加置顶项**

`EntryMenuContent`（HistoryScreen.kt:391）在预览区分隔线后、`if (entry.type != "text")` 之前插入：

```kotlin
    DropdownMenuItem(
        text = { Text(if (entry.pinned) "取消置顶" else "置顶") },
        onClick = {
            AppState.syncService?.setPinned(entry, !entry.pinned)
            onDismiss()
        })
```

（长按菜单的 `DropdownMenu` 关闭前会重组——`EntryMenuContent` 内不用 `menuTarget!!`，此处直接使用传入的 `entry` 参数，安全。）

- [ ] **Step 2: 多选删除对话框去掉 hasFile 限制**

`batchDelete` 对话框（HistoryScreen.kt:262-292）替换 confirmButton 为始终显示彻底删除：

```kotlin
            confirmButton = {
                Row {
                    TextButton(onClick = {
                        selected.forEach { AppState.syncService?.deleteEntry(it, fully = true) }
                        exitSelection()
                    }) { Text("彻底删除") }
                    TextButton(onClick = {
                        selected.forEach { AppState.syncService?.deleteEntry(it, fully = false) }
                        exitSelection()
                    }) { Text("本地删除") }
                    TextButton(onClick = { batchDelete = false }) { Text("取消") }
                }
            }
```

`text` 文案改为：

```kotlin
                Text("本地删除：仅移除本机记录。\n彻底删除：图片/文件按内容同步删除服务器与其他设备上的相同内容。")
```

（删除 `val hasFile = selected.any { it.type == "file" }` 与 `if (!hasFile)` 包装；`fully = entry.type != "file"` 逻辑一并移除。）

- [ ] **Step 3: 验证**

同 Task 11 Step 5。Expected: BUILD SUCCESSFUL。

- [ ] **Step 4: 提交**

```bash
cd C:\Android\clipboard-tool
git add Android/app/src/main/java/com/starry/clipboardtool/ui/HistoryScreen.kt
git commit -m "feat(android): 长按菜单置顶 + 多选彻底删除补齐（文件可彻底删）"
```

### Task 14: Android 端构建 + adb 验证

**Covers:** [S7]

**Files:**
- 无源码改动

- [ ] **Step 1: 构建**

同 Task 11 Step 5。Expected: BUILD SUCCESSFUL。

- [ ] **Step 2: 安装 + 启动**

```powershell
adb install -r C:\Android\clipboard-tool\Android\app\build\outputs\apk\debug\app-debug.apk
adb shell am start -n com.starry.clipboardtool/.MainActivity
```

- [ ] **Step 3: 验证置顶同步**

PC 端置顶一条 → 手机列表顶部显示该条目（uiautomator 提取 text 确认）；长按 → 菜单含"取消置顶"。手机端置顶一条 → PC 端该条目出现在列表顶部区域且再次右键显示"取消置顶"。

- [ ] **Step 4: 验证多选彻底删除（含文件）**

手机端长按进入多选 → 勾选含文件条目 → 删除 → 对话框出现"彻底删除"按钮（此前文件条目时隐藏）→ 彻底删除 → PC 端对应条目消失、服务器 messages 减。

- [ ] **Step 5: 验证 clear 接收**

PC 端"彻底清空" → 手机端历史被清（置顶除外）。（与 Task 10 Step 4 联动。）

- [ ] **Step 6: 提交（验证无改动，无提交）**

# 自审记录

- 覆盖：S3.1/S4.1/S4.2/S4.3/S4.4 → Task 1-5；S5.1 → Task 8；S5.2 → Task 6/7；S5.3 → Task 9；S6.1 → Task 11/12/13；S6.2 → Task 13；S7 → Task 5/10/14。无缺漏、无悬空。
- 类型一致性：`Cleanup(userID, olderThan)` 签名在 Task 4 测试与实现一致；`SyncMessage` 尾参 `Pinned`/`pinned` 两端一致；`SetPinned`/`setPinned` 命名区分端，无跨端引用。
- 占位符：无 TBD；每步含完整代码与命令。
- 用户隔离：所有服务器 SQL 显式 `user_id`；pins 主键含 user_id；cleanup 按用户遍历。
- 软删除：delete 只清 pins；clear 纯标记；物理删除仅 cleanup 标记匹配（置顶除外）。
