# 剪贴板同步服务（SyncServer）实施计划 — M1

> [!NOTE]
> This document may not reflect the current implementation.
> See the final report for up-to-date state:
> [Final Report](../reports/clipboard-sync.md)

> **For agentic workers:** REQUIRED SUB-SKILL: Use compose:subagent (recommended) or compose:execute to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 在 `SyncServer/` 实现 Go 单二进制剪贴板同步服务：账号/设备注册登录、WebSocket 实时转发、媒体上传下载、历史拉取、7 天短期存储清理，并通过本地双客户端联调验证。

**Architecture:** 标准库 `http.ServeMux` 路由（Go 1.22+ 方法+路径模式），SQLite（modernc.org/sqlite 纯 Go 驱动）持久化，gorilla/websocket 长连接 + 内存 Hub 按账号转发。认证统一为设备 token（登录/注册即完成设备登记）。监听 `127.0.0.1:8082`，由 nginx 反代提供 TLS（本计划不含部署，M4 执行）。

**Tech Stack:** Go 1.26、标准库 net/http、github.com/gorilla/websocket、modernc.org/sqlite、golang.org/x/crypto/bcrypt

## Global Constraints

- 全部代码位于 `SyncServer/`，模块名 `syncserver`，不发布到任何远程。
- 依赖：`github.com/gorilla/websocket`、`modernc.org/sqlite`、`golang.org/x/crypto/bcrypt`；禁其他第三方库。
- 政务网模块下载：先 `$env:GOPROXY="https://goproxy.cn,direct"` 再 `go mod tidy`；超时则按 AGENTS.md 启 xray 代理。
- 保留期常量 `retention = 7 * 24 * time.Hour`；所有时间戳 unix 毫秒（int64）。
- 单文件 ≤ 50MB（服务端拒绝 Content-Length 超限）。
- 设备 token：32 随机字节 hex 字符串；服务端仅存 sha256 哈希。
- 密码：bcrypt.DefaultCost；用户名非空且 ≥4 字符。
- 账号隔离：任何跨账号访问返回 404（不泄露存在性）。
- 每次任务结束 git commit（提交信息用仓库既有风格 `feat:`/`test:`/`docs:`/`chore:`）。
- 本计划不修改 `ClipboardTool/`、`Launcher/`、`AGENTS.md` 任何内容。
- 代码不写注释（除非 WHY 非显而易见）；错误处理只覆盖可达路径。

---

### Task 1: 项目脚手架与健康检查

**Covers:** S7

**Files:**
- Create: `SyncServer/go.mod`
- Create: `SyncServer/main.go`
- Create: `SyncServer/main_test.go`

**Interfaces:**
- Consumes: 无
- Produces: `main()` 读取 `-addr`（默认 `127.0.0.1:8082`）与 `-db`（默认 `sync.db`）；`GET /api/health` 返回 `{"ok":true}`

- [ ] **Step 1: 初始化模块并拉取依赖**

```bash
mkdir SyncServer
cd SyncServer
go mod init syncserver
$env:GOPROXY="https://goproxy.cn,direct"
go get github.com/gorilla/websocket modernc.org/sqlite golang.org/x/crypto/bcrypt
```

Expected: 三个依赖进入 go.mod（如拉取失败，启 xray 后重试，见 AGENTS.md 网络节）。

- [ ] **Step 2: 写最小 main.go（占位路由，后续任务填充）**

```go
package main

import (
	"encoding/json"
	"flag"
	"log"
	"net/http"
)

type app struct {
	mux *http.ServeMux
}

func newApp() *app {
	a := &app{mux: http.NewServeMux()}
	a.mux.HandleFunc("GET /api/health", func(w http.ResponseWriter, r *http.Request) {
		json.NewEncoder(w).Encode(map[string]bool{"ok": true})
	})
	return a
}

func main() {
	addr := flag.String("addr", "127.0.0.1:8082", "listen address")
	dbPath := flag.String("db", "sync.db", "sqlite database path")
	flag.Parse()
	a := newApp()
	log.Printf("sync server listening on %s (db=%s)", *addr, *dbPath)
	if err := http.ListenAndServe(*addr, a.mux); err != nil {
		log.Fatal(err)
	}
}
```

- [ ] **Step 3: 写健康检查测试**

```go
package main

import (
	"encoding/json"
	"net/http"
	"net/http/httptest"
	"testing"
)

func TestHealth(t *testing.T) {
	req := httptest.NewRequest("GET", "/api/health", nil)
	rec := httptest.NewRecorder()
	newApp().mux.ServeHTTP(rec, req)
	if rec.Code != http.StatusOK {
		t.Fatalf("status = %d, want 200", rec.Code)
	}
	var body map[string]bool
	if err := json.Unmarshal(rec.Body.Bytes(), &body); err != nil || !body["ok"] {
		t.Fatalf("body = %s, want ok:true", rec.Body.String())
	}
}
```

- [ ] **Step 4: 运行测试确认通过**

Run: `go test ./...`
Expected: `ok syncserver`，`TestHealth` PASS。

- [ ] **Step 5: 提交**

```bash
git add SyncServer/
git commit -m "feat: SyncServer 脚手架与健康检查端点"
```

---

### Task 2: SQLite 存储层

**Covers:** S3, S7

**Files:**
- Create: `SyncServer/store.go`
- Create: `SyncServer/store_test.go`

**Interfaces:**
- Consumes: 无（独立于 Task 1）
- Produces:
  - `type Store struct`、`func OpenStore(path string) (*Store, error)`、`func (s *Store) Close() error`
  - `func (s *Store) CreateUser(username, passwordHash string) (int64, error)` — 用户名重复返回 `ErrUsernameTaken`
  - `func (s *Store) GetUserByUsername(username string) (User, error)`、`type User struct { ID int64; Username string; PasswordHash string }`
  - `func (s *Store) CreateDevice(userID int64, name, tokenHash string) (int64, error)`
  - `func (s *Store) GetDeviceByToken(tokenHash string) (Device, error)`、`type Device struct { ID int64; UserID int64; Name string; TokenHash string; LastSeen int64 }`
  - `func (s *Store) ListDevices(userID int64) ([]Device, error)`、`func (s *Store) DeleteDevice(userID, deviceID int64) error`
  - `func (s *Store) TouchDevice(deviceID int64) error`
  - `func (s *Store) InsertMessage(userID, originDeviceID int64, msgType string, payload []byte) (int64, error)`
  - `func (s *Store) MessagesSince(userID int64, since int64) ([]Message, error)`、`type Message struct { Type string; OriginDeviceID int64; Seq int64; Ts int64; Payload []byte }`
  - `func (s *Store) InsertMedia(userID int64, data []byte) (int64, error)`、`func (s *Store) GetMedia(userID, mediaID int64) ([]byte, error)`
  - `func (s *Store) Cleanup(olderThan int64) (int, error)` — 返回删除的 message+media 总条数

- [ ] **Step 1: 写失败测试（建表与用户/设备 CRUD）**

```go
package main

import (
	"errors"
	"testing"
)

func openTestStore(t *testing.T) *Store {
	t.Helper()
	s, err := OpenStore("file::memory:?cache=shared")
	if err != nil {
		t.Fatalf("OpenStore: %v", err)
	}
	t.Cleanup(func() { s.Close() })
	return s
}

func TestUserCreateAndGet(t *testing.T) {
	s := openTestStore(t)
	id, err := s.CreateUser("alice", "hash1")
	if err != nil {
		t.Fatalf("CreateUser: %v", err)
	}
	u, err := s.GetUserByUsername("alice")
	if err != nil || u.ID != id || u.PasswordHash != "hash1" {
		t.Fatalf("GetUserByUsername = %+v, %v", u, err)
	}
	if _, err := s.CreateUser("alice", "hash2"); !errors.Is(err, ErrUsernameTaken) {
		t.Fatalf("duplicate username err = %v, want ErrUsernameTaken", err)
	}
	if _, err := s.GetUserByUsername("bob"); err == nil {
		t.Fatal("GetUserByUsername(bob) should fail")
	}
}

func TestDeviceCRUD(t *testing.T) {
	s := openTestStore(t)
	uid, _ := s.CreateUser("alice", "hash1")
	did, err := s.CreateDevice(uid, "小米14 Pro", "tokhash1")
	if err != nil {
		t.Fatalf("CreateDevice: %v", err)
	}
	d, err := s.GetDeviceByToken("tokhash1")
	if err != nil || d.ID != did || d.UserID != uid || d.Name != "小米14 Pro" {
		t.Fatalf("GetDeviceByToken = %+v, %v", d, err)
	}
	if _, err := s.GetDeviceByToken("nope"); err == nil {
		t.Fatal("GetDeviceByToken(nope) should fail")
	}
	if err := s.TouchDevice(did); err != nil {
		t.Fatalf("TouchDevice: %v", err)
	}
	if err := s.DeleteDevice(uid, did); err != nil {
		t.Fatalf("DeleteDevice: %v", err)
	}
	if _, err := s.GetDeviceByToken("tokhash1"); err == nil {
		t.Fatal("device should be gone after delete")
	}
	if err := s.DeleteDevice(uid, 99999); err != nil {
		t.Fatalf("DeleteDevice(missing) = %v, want nil", err)
	}
}
```

- [ ] **Step 2: 运行确认失败**

Run: `go test -run TestUserCreateAndGet -v`
Expected: FAIL — `OpenStore` 未定义（编译错误），即失败。

- [ ] **Step 3: 实现 store.go（建表 + 用户/设备 CRUD + 消息 + 媒体 + 清理）**

```go
package main

import (
	"database/sql"
	"errors"
	"strings"
	"time"

	_ "modernc.org/sqlite"
)

var ErrUsernameTaken = errors.New("username taken")

type Store struct{ db *sql.DB }

type User struct {
	ID           int64
	Username     string
	PasswordHash string
}

type Device struct {
	ID        int64
	UserID    int64
	Name      string
	TokenHash string
	LastSeen  int64
}

type Message struct {
	Type           string
	OriginDeviceID int64
	Seq            int64
	Ts             int64
	Payload        []byte
}

func OpenStore(path string) (*Store, error) {
	db, err := sql.Open("sqlite", path)
	if err != nil {
		return nil, err
	}
	s := &Store{db: db}
	if err := s.migrate(); err != nil {
		db.Close()
		return nil, err
	}
	return s, nil
}

func (s *Store) Close() error { return s.db.Close() }

func (s *Store) migrate() error {
	_, err := s.db.Exec(`
CREATE TABLE IF NOT EXISTS users (
	id INTEGER PRIMARY KEY AUTOINCREMENT,
	username TEXT NOT NULL UNIQUE,
	password_hash TEXT NOT NULL,
	created_at INTEGER NOT NULL
);
CREATE TABLE IF NOT EXISTS devices (
	id INTEGER PRIMARY KEY AUTOINCREMENT,
	user_id INTEGER NOT NULL REFERENCES users(id),
	name TEXT NOT NULL,
	token_hash TEXT NOT NULL UNIQUE,
	last_seen INTEGER NOT NULL
);
CREATE TABLE IF NOT EXISTS messages (
	id INTEGER PRIMARY KEY AUTOINCREMENT,
	user_id INTEGER NOT NULL REFERENCES users(id),
	origin_device_id INTEGER NOT NULL,
	type TEXT NOT NULL,
	seq INTEGER NOT NULL,
	ts INTEGER NOT NULL,
	payload BLOB NOT NULL
);
CREATE TABLE IF NOT EXISTS media (
	id INTEGER PRIMARY KEY AUTOINCREMENT,
	user_id INTEGER NOT NULL REFERENCES users(id),
	data BLOB NOT NULL,
	created_at INTEGER NOT NULL
);
CREATE INDEX IF NOT EXISTS idx_messages_user_ts ON messages(user_id, ts);
CREATE INDEX IF NOT EXISTS idx_media_user ON media(user_id, created_at);`)
	return err
}

func (s *Store) CreateUser(username, passwordHash string) (int64, error) {
	res, err := s.db.Exec(`INSERT INTO users(username, password_hash, created_at) VALUES(?, ?, ?)`,
		username, passwordHash, time.Now().UnixMilli())
	if err != nil {
		if strings.Contains(err.Error(), "UNIQUE") {
			return 0, ErrUsernameTaken
		}
		return 0, err
	}
	return res.LastInsertId()
}

func (s *Store) GetUserByUsername(username string) (User, error) {
	var u User
	err := s.db.QueryRow(`SELECT id, username, password_hash FROM users WHERE username = ?`, username).
		Scan(&u.ID, &u.Username, &u.PasswordHash)
	return u, err
}

func (s *Store) CreateDevice(userID int64, name, tokenHash string) (int64, error) {
	res, err := s.db.Exec(`INSERT INTO devices(user_id, name, token_hash, last_seen) VALUES(?, ?, ?, ?)`,
		userID, name, tokenHash, time.Now().UnixMilli())
	if err != nil {
		return 0, err
	}
	return res.LastInsertId()
}

func (s *Store) GetDeviceByToken(tokenHash string) (Device, error) {
	var d Device
	err := s.db.QueryRow(`SELECT id, user_id, name, token_hash, last_seen FROM devices WHERE token_hash = ?`, tokenHash).
		Scan(&d.ID, &d.UserID, &d.Name, &d.TokenHash, &d.LastSeen)
	return d, err
}

func (s *Store) ListDevices(userID int64) ([]Device, error) {
	rows, err := s.db.Query(`SELECT id, user_id, name, token_hash, last_seen FROM devices WHERE user_id = ?`, userID)
	if err != nil {
		return nil, err
	}
	defer rows.Close()
	var out []Device
	for rows.Next() {
		var d Device
		if err := rows.Scan(&d.ID, &d.UserID, &d.Name, &d.TokenHash, &d.LastSeen); err != nil {
			return nil, err
		}
		out = append(out, d)
	}
	return out, rows.Err()
}

func (s *Store) DeleteDevice(userID, deviceID int64) error {
	_, err := s.db.Exec(`DELETE FROM devices WHERE id = ? AND user_id = ?`, deviceID, userID)
	return err
}

func (s *Store) TouchDevice(deviceID int64) error {
	_, err := s.db.Exec(`UPDATE devices SET last_seen = ? WHERE id = ?`, time.Now().UnixMilli(), deviceID)
	return err
}

func (s *Store) InsertMessage(userID, originDeviceID int64, msgType string, payload []byte) (int64, error) {
	seq, err := s.nextSeq(userID)
	if err != nil {
		return 0, err
	}
	res, err := s.db.Exec(`INSERT INTO messages(user_id, origin_device_id, type, seq, ts, payload) VALUES(?, ?, ?, ?, ?, ?)`,
		userID, originDeviceID, msgType, seq, time.Now().UnixMilli(), payload)
	if err != nil {
		return 0, err
	}
	return res.LastInsertId()
}

func (s *Store) nextSeq(userID int64) (int64, error) {
	var seq int64
	err := s.db.QueryRow(`SELECT COALESCE(MAX(seq), 0) + 1 FROM messages WHERE user_id = ?`, userID).Scan(&seq)
	return seq, err
}

func (s *Store) MessagesSince(userID int64, since int64) ([]Message, error) {
	rows, err := s.db.Query(`SELECT type, origin_device_id, seq, ts, payload FROM messages WHERE user_id = ? AND ts > ? ORDER BY ts`, userID, since)
	if err != nil {
		return nil, err
	}
	defer rows.Close()
	var out []Message
	for rows.Next() {
		var m Message
		if err := rows.Scan(&m.Type, &m.OriginDeviceID, &m.Seq, &m.Ts, &m.Payload); err != nil {
			return nil, err
		}
		out = append(out, m)
	}
	return out, rows.Err()
}

func (s *Store) InsertMedia(userID int64, data []byte) (int64, error) {
	res, err := s.db.Exec(`INSERT INTO media(user_id, data, created_at) VALUES(?, ?, ?)`,
		userID, data, time.Now().UnixMilli())
	if err != nil {
		return 0, err
	}
	return res.LastInsertId()
}

func (s *Store) GetMedia(userID, mediaID int64) ([]byte, error) {
	var data []byte
	err := s.db.QueryRow(`SELECT data FROM media WHERE id = ? AND user_id = ?`, mediaID, userID).Scan(&data)
	return data, err
}

func (s *Store) Cleanup(olderThan int64) (int, error) {
	res, err := s.db.Exec(`DELETE FROM messages WHERE ts < ?`, olderThan)
	if err != nil {
		return 0, err
	}
	n, _ := res.RowsAffected()
	res, err = s.db.Exec(`DELETE FROM media WHERE created_at < ?`, olderThan)
	if err != nil {
		return 0, err
	}
	m, _ := res.RowsAffected()
	return int(n + m), nil
}
```

- [ ] **Step 4: 补消息/媒体/清理测试并全部跑通**

```go
func TestMessagesAndMedia(t *testing.T) {
	s := openTestStore(t)
	uid, _ := s.CreateUser("alice", "hash1")
	did, _ := s.CreateDevice(uid, "phone", "th1")

	payload := []byte(`{"text":"hello"}`)
	mid, err := s.InsertMessage(uid, did, "clip_text", payload)
	if err != nil || mid == 0 {
		t.Fatalf("InsertMessage = %d, %v", mid, err)
	}
	msgs, err := s.MessagesSince(uid, 0)
	if err != nil || len(msgs) != 1 || msgs[0].Type != "clip_text" || string(msgs[0].Payload) != string(payload) || msgs[0].Seq != 1 {
		t.Fatalf("MessagesSince = %+v, %v", msgs, err)
	}
	if got, err := s.MessagesSince(uid, msgs[0].Ts); err != nil || len(got) != 0 {
		t.Fatalf("MessagesSince(since=ts) = %+v, %v", got, err)
	}

	mediaID, err := s.InsertMedia(uid, []byte("PNGDATA"))
	if err != nil || mediaID == 0 {
		t.Fatalf("InsertMedia = %d, %v", mediaID, err)
	}
	data, err := s.GetMedia(uid, mediaID)
	if err != nil || string(data) != "PNGDATA" {
		t.Fatalf("GetMedia = %q, %v", data, err)
	}
	if _, err := s.GetMedia(uid, 99999); err == nil {
		t.Fatal("GetMedia(missing) should fail")
	}
	if _, err := s.GetMedia(uid+1, mediaID); err == nil {
		t.Fatal("GetMedia(other user) should fail")
	}
}

func TestCleanup(t *testing.T) {
	s := openTestStore(t)
	uid, _ := s.CreateUser("alice", "hash1")
	did, _ := s.CreateDevice(uid, "phone", "th1")
	_, _ = s.InsertMessage(uid, did, "clip_text", []byte(`{"text":"old"}`))
	_, _ = s.InsertMedia(uid, []byte("OLD"))

	now := time.Now().UnixMilli()
	if _, err := s.db.Exec(`UPDATE messages SET ts = ? WHERE user_id = ?`, now-8*24*3600*1000, uid); err != nil {
		t.Fatal(err)
	}
	if _, err := s.db.Exec(`UPDATE media SET created_at = ? WHERE user_id = ?`, now-8*24*3600*1000, uid); err != nil {
		t.Fatal(err)
	}
	n, err := s.Cleanup(now - 7*24*3600*1000)
	if err != nil || n != 2 {
		t.Fatalf("Cleanup = %d, %v; want 2", n, err)
	}
	if msgs, _ := s.MessagesSince(uid, 0); len(msgs) != 0 {
		t.Fatalf("messages remain: %+v", msgs)
	}
}
```

Run: `go test ./...`
Expected: 全部 PASS。

- [ ] **Step 5: 提交**

```bash
git add SyncServer/store.go SyncServer/store_test.go
git commit -m "feat: SQLite 存储层（用户/设备/消息/媒体/清理）"
```

---

### Task 3: 账号认证（注册/登录/设备 token 中间件）

**Covers:** S3, S4

**Files:**
- Create: `SyncServer/auth.go`
- Create: `SyncServer/auth_test.go`

**Interfaces:**
- Consumes: Task 2 的 `Store`、`User`、`Device`、`ErrUsernameTaken`；Task 1 的 `app`
- Produces:
  - `func (a *app) handleRegister(w, r)`、`func (a *app) handleLogin(w, r)`
  - 请求体 `{"username": "...", "password": "...", "deviceName": "..."}`；响应 `{"deviceId": 1, "token": "<hex>"}`；错误 `{"error": "..."}` + 400/401/409
  - `func (a *app) requireAuth(next http.HandlerFunc) http.HandlerFunc` — 校验 `Authorization: Bearer <token>`，注入 `contextKeyDevice`（`*Device`）
  - `var contextKeyDevice = struct{}{}`；helper `func deviceFrom(r *http.Request) *Device`

- [ ] **Step 1: 写失败测试**

```go
package main

import (
	"encoding/json"
	"net/http"
	"net/http/httptest"
	"strings"
	"testing"
)

func newTestApp(t *testing.T) (*app, *Store) {
	t.Helper()
	s, err := OpenStore("file::memory:?cache=shared")
	if err != nil {
		t.Fatalf("OpenStore: %v", err)
	}
	t.Cleanup(func() { s.Close() })
	a := newApp()
	a.store = s
	return a, s
}

func doJSON(t *testing.T, a *app, method, path, body, token string) *httptest.ResponseRecorder {
	t.Helper()
	var req *http.Request
	if body == "" {
		req = httptest.NewRequest(method, path, nil)
	} else {
		req = httptest.NewRequest(method, path, strings.NewReader(body))
	}
	if token != "" {
		req.Header.Set("Authorization", "Bearer "+token)
	}
	rec := httptest.NewRecorder()
	a.mux.ServeHTTP(rec, req)
	return rec
}

func TestRegisterAndLogin(t *testing.T) {
	a, s := newTestApp(t)
	rec := doJSON(t, a, "POST", "/api/auth/register", `{"username":"alice","password":"secret123","deviceName":"小米14 Pro"}`, "")
	if rec.Code != http.StatusCreated {
		t.Fatalf("register status = %d, body=%s", rec.Code, rec.Body.String())
	}
	var reg struct {
		DeviceID int64  `json:"deviceId"`
		Token    string `json:"token"`
	}
	if err := json.Unmarshal(rec.Body.Bytes(), &reg); err != nil || reg.Token == "" || reg.DeviceID == 0 {
		t.Fatalf("register body = %s", rec.Body.String())
	}
	if _, err := s.GetDeviceByToken(sha256Hex(reg.Token)); err != nil {
		t.Fatalf("token not stored: %v", err)
	}

	// 重复用户名
	rec = doJSON(t, a, "POST", "/api/auth/register", `{"username":"alice","password":"x","deviceName":"pc"}`, "")
	if rec.Code != http.StatusConflict {
		t.Fatalf("duplicate register status = %d, want 409", rec.Code)
	}
	// 弱密码/短用户名
	if rec := doJSON(t, a, "POST", "/api/auth/register", `{"username":"ab","password":"x","deviceName":"pc"}`); rec.Code != http.StatusBadRequest {
		t.Fatalf("short username status = %d, want 400", rec.Code)
	}
	// 登录成功（同账号第二设备）
	rec = doJSON(t, a, "POST", "/api/auth/login", `{"username":"alice","password":"secret123","deviceName":"PC 工作机"}`, "")
	if rec.Code != http.StatusOK {
		t.Fatalf("login status = %d, body=%s", rec.Code, rec.Body.String())
	}
	if err := json.Unmarshal(rec.Body.Bytes(), &reg); err != nil || reg.Token == "" {
		t.Fatalf("login body = %s", rec.Body.String())
	}
	// 密码错误
	rec = doJSON(t, a, "POST", "/api/auth/login", `{"username":"alice","password":"wrong","deviceName":"pc"}`, "")
	if rec.Code != http.StatusUnauthorized {
		t.Fatalf("bad password status = %d, want 401", rec.Code)
	}
	// 未注册用户
	rec = doJSON(t, a, "POST", "/api/auth/login", `{"username":"nobody","password":"x","deviceName":"pc"}`, "")
	if rec.Code != http.StatusUnauthorized {
		t.Fatalf("unknown user status = %d, want 401", rec.Code)
	}
}

func TestRequireAuth(t *testing.T) {
	a, _ := newTestApp(t)
	rec := doJSON(t, a, "POST", "/api/auth/register", `{"username":"alice","password":"secret123","deviceName":"phone"}`, "")
	var reg struct {
		Token string `json:"token"`
	}
	json.Unmarshal(rec.Body.Bytes(), &reg)

	// 无 token
	rec = doJSON(t, a, "GET", "/api/devices", "", "")
	if rec.Code != http.StatusUnauthorized {
		t.Fatalf("no token status = %d, want 401", rec.Code)
	}
	// 伪造 token
	rec = doJSON(t, a, "GET", "/api/devices", "", "deadbeef")
	if rec.Code != http.StatusUnauthorized {
		t.Fatalf("bad token status = %d, want 401", rec.Code)
	}
	// 有效 token
	rec = doJSON(t, a, "GET", "/api/devices", "", reg.Token)
	if rec.Code != http.StatusOK {
		t.Fatalf("valid token status = %d, body=%s", rec.Code, rec.Body.String())
	}
}
```

- [ ] **Step 2: 运行确认失败**

Run: `go test -run 'TestRegisterAndLogin|TestRequireAuth' -v`
Expected: FAIL — `a.store` 字段、`sha256Hex`、handler 未定义。

- [ ] **Step 3: 实现 auth.go**

```go
package main

import (
	"crypto/rand"
	"crypto/sha256"
	"encoding/hex"
	"encoding/json"
	"errors"
	"net/http"
	"strings"

	"golang.org/x/crypto/bcrypt"
)

var contextKeyDevice = struct{}{}

func sha256Hex(s string) string {
	h := sha256.Sum256([]byte(s))
	return hex.EncodeToString(h[:])
}

func newToken() (string, error) {
	b := make([]byte, 32)
	if _, err := rand.Read(b); err != nil {
		return "", err
	}
	return hex.EncodeToString(b), nil
}

type authRequest struct {
	Username   string `json:"username"`
	Password   string `json:"password"`
	DeviceName string `json:"deviceName"`
}

type authResponse struct {
	DeviceID int64  `json:"deviceId"`
	Token    string `json:"token"`
}

func (a *app) handleRegister(w http.ResponseWriter, r *http.Request) {
	var req authRequest
	if err := json.NewDecoder(r.Body).Decode(&req); err != nil {
		writeError(w, http.StatusBadRequest, "invalid json")
		return
	}
	if len(req.Username) < 4 || len(req.Password) < 6 {
		writeError(w, http.StatusBadRequest, "username >= 4 chars, password >= 6 chars")
		return
	}
	hash, err := bcrypt.GenerateFromPassword([]byte(req.Password), bcrypt.DefaultCost)
	if err != nil {
		writeError(w, http.StatusInternalServerError, "internal")
		return
	}
	uid, err := a.store.CreateUser(req.Username, string(hash))
	if errors.Is(err, ErrUsernameTaken) {
		writeError(w, http.StatusConflict, "username taken")
		return
	}
	if err != nil {
		writeError(w, http.StatusInternalServerError, "internal")
		return
	}
	writeAuthResponse(w, http.StatusCreated, a, uid, req.DeviceName)
}

func (a *app) handleLogin(w http.ResponseWriter, r *http.Request) {
	var req authRequest
	if err := json.NewDecoder(r.Body).Decode(&req); err != nil {
		writeError(w, http.StatusBadRequest, "invalid json")
		return
	}
	u, err := a.store.GetUserByUsername(req.Username)
	if err != nil {
		writeError(w, http.StatusUnauthorized, "bad credentials")
		return
	}
	if bcrypt.CompareHashAndPassword([]byte(u.PasswordHash), []byte(req.Password)) != nil {
		writeError(w, http.StatusUnauthorized, "bad credentials")
		return
	}
	writeAuthResponse(w, http.StatusOK, a, u.ID, req.DeviceName)
}

func writeAuthResponse(w http.ResponseWriter, status int, a *app, userID int64, deviceName string) {
	token, err := newToken()
	if err != nil {
		writeError(w, http.StatusInternalServerError, "internal")
		return
	}
	if deviceName == "" {
		deviceName = "未命名设备"
	}
	did, err := a.store.CreateDevice(userID, deviceName, sha256Hex(token))
	if err != nil {
		writeError(w, http.StatusInternalServerError, "internal")
		return
	}
	json.NewEncoder(w).Encode(authResponse{DeviceID: did, Token: token})
}

func (a *app) requireAuth(next http.HandlerFunc) http.HandlerFunc {
	return func(w http.ResponseWriter, r *http.Request) {
		raw, ok := strings.CutPrefix(r.Header.Get("Authorization"), "Bearer ")
		if !ok || raw == "" {
			writeError(w, http.StatusUnauthorized, "missing token")
			return
		}
		dev, err := a.store.GetDeviceByToken(sha256Hex(raw))
		if err != nil {
			writeError(w, http.StatusUnauthorized, "invalid token")
			return
		}
		a.store.TouchDevice(dev.ID)
		next(w, r.WithContext(withDevice(r.Context(), &dev)))
	}
}

func writeError(w http.ResponseWriter, status int, msg string) {
	w.WriteHeader(status)
	json.NewEncoder(w).Encode(map[string]string{"error": msg})
}
```

- [ ] **Step 4: 把 store/handler 接线进 newApp，跑通测试**

在 `main.go` 的 `newApp` 里增加 store 与路由：

```go
type app struct {
	mux   *http.ServeMux
	store *Store
}

func newApp() *app {
	a := &app{mux: http.NewServeMux()}
	a.mux.HandleFunc("GET /api/health", func(w http.ResponseWriter, r *http.Request) {
		json.NewEncoder(w).Encode(map[string]bool{"ok": true})
	})
	a.mux.HandleFunc("POST /api/auth/register", a.handleRegister)
	a.mux.HandleFunc("POST /api/auth/login", a.handleLogin)
	a.mux.HandleFunc("GET /api/devices", a.requireAuth(a.handleListDevices))
	a.mux.HandleFunc("DELETE /api/devices/{id}", a.requireAuth(a.handleDeleteDevice))
	return a
}
```

在 `auth.go` 追加设备管理 handler 与 context helper：

```go
func withDevice(ctx context.Context, d *Device) context.Context {
	return context.WithValue(ctx, contextKeyDevice, d)
}

func deviceFrom(r *http.Request) *Device {
	d, _ := r.Context().Value(contextKeyDevice).(*Device)
	return d
}

func (a *app) handleListDevices(w http.ResponseWriter, r *http.Request) {
	dev := deviceFrom(r)
	list, err := a.store.ListDevices(dev.UserID)
	if err != nil {
		writeError(w, http.StatusInternalServerError, "internal")
		return
	}
	type devView struct {
		ID       int64  `json:"id"`
		Name     string `json:"name"`
		LastSeen int64  `json:"lastSeen"`
	}
	out := make([]devView, 0, len(list))
	for _, d := range list {
		out = append(out, devView{d.ID, d.Name, d.LastSeen})
	}
	json.NewEncoder(w).Encode(map[string]any{"devices": out})
}

func (a *app) handleDeleteDevice(w http.ResponseWriter, r *http.Request) {
	dev := deviceFrom(r)
	var id int64
	if _, err := fmt.Sscanf(r.PathValue("id"), "%d", &id); err != nil {
		writeError(w, http.StatusBadRequest, "bad id")
		return
	}
	if err := a.store.DeleteDevice(dev.UserID, id); err != nil {
		writeError(w, http.StatusInternalServerError, "internal")
		return
	}
	w.WriteHeader(http.StatusNoContent)
}
```

补 imports（auth.go）：`context`、`fmt`。`main.go` 的 `main()` 打开 store：

```go
store, err := OpenStore(*dbPath)
if err != nil {
	log.Fatal(err)
}
defer store.Close()
a := newApp()
a.store = store
```

Run: `go test ./...`
Expected: 全部 PASS（含 Task 2 既有测试）。

- [ ] **Step 5: 提交**

```bash
git add SyncServer/
git commit -m "feat: 账号注册/登录与设备 token 认证"
```

---

### Task 4: 媒体上传下载

**Covers:** S4, S7

**Files:**
- Create: `SyncServer/media.go`
- Create: `SyncServer/media_test.go`

**Interfaces:**
- Consumes: Task 3 的 `requireAuth`、`deviceFrom`、`writeError`
- Produces: `POST /api/media`（raw body，`Content-Length` ≤ 50MB）→ 201 `{"mediaId": 1}`；`GET /api/media/{id}` → 200 二进制（仅本账号，跨账号/不存在 404）

- [ ] **Step 1: 写失败测试**

```go
package main

import (
	"encoding/json"
	"fmt"
	"net/http"
	"strings"
	"testing"
)

func registerToken(t *testing.T, a *app, username, deviceName string) (int64, string) {
	t.Helper()
	rec := doJSON(t, a, "POST", "/api/auth/register",
		`{"username":"`+username+`","password":"secret123","deviceName":"`+deviceName+`"}`, "")
	if rec.Code != http.StatusCreated {
		t.Fatalf("register = %d %s", rec.Code, rec.Body.String())
	}
	var reg struct {
		DeviceID int64  `json:"deviceId"`
		Token    string `json:"token"`
	}
	json.Unmarshal(rec.Body.Bytes(), &reg)
	return reg.DeviceID, reg.Token
}

func TestMediaUploadDownload(t *testing.T) {
	a, _ := newTestApp(t)
	_, tokA := registerToken(t, a, "alice", "phone")
	_, tokB := registerToken(t, a, "bob", "phone")

	req := httptest.NewRequest("POST", "/api/media", strings.NewReader("PNGDATA123"))
	req.Header.Set("Authorization", "Bearer "+tokA)
	rec := httptest.NewRecorder()
	a.mux.ServeHTTP(rec, req)
	if rec.Code != http.StatusCreated {
		t.Fatalf("upload = %d %s", rec.Code, rec.Body.String())
	}
	var up struct {
		MediaID int64 `json:"mediaId"`
	}
	json.Unmarshal(rec.Body.Bytes(), &up)
	if up.MediaID == 0 {
		t.Fatalf("upload body = %s", rec.Body.String())
	}

	req = httptest.NewRequest("GET", "/api/media/"+fmt.Sprint(up.MediaID), nil)
	req.Header.Set("Authorization", "Bearer "+tokA)
	rec = httptest.NewRecorder()
	a.mux.ServeHTTP(rec, req)
	if rec.Code != http.StatusOK || rec.Body.String() != "PNGDATA123" {
		t.Fatalf("download = %d %q", rec.Code, rec.Body.String())
	}

	// 跨账号 404
	req = httptest.NewRequest("GET", "/api/media/"+fmt.Sprint(up.MediaID), nil)
	req.Header.Set("Authorization", "Bearer "+tokB)
	rec = httptest.NewRecorder()
	a.mux.ServeHTTP(rec, req)
	if rec.Code != http.StatusNotFound {
		t.Fatalf("cross-account = %d, want 404", rec.Code)
	}
}

func TestMediaTooLarge(t *testing.T) {
	a, _ := newTestApp(t)
	_, tok := registerToken(t, a, "alice", "phone")
	big := strings.Repeat("x", maxMediaBytes+1)
	req := httptest.NewRequest("POST", "/api/media", strings.NewReader(big))
	req.ContentLength = int64(len(big))
	req.Header.Set("Authorization", "Bearer "+tok)
	rec := httptest.NewRecorder()
	a.mux.ServeHTTP(rec, req)
	if rec.Code != http.StatusRequestEntityTooLarge {
		t.Fatalf("oversize = %d, want 413", rec.Code)
	}
}
```

- [ ] **Step 2: 运行确认失败**

Run: `go test -run 'TestMedia' -v`
Expected: FAIL — `maxMediaBytes` 未定义或 handler 不存在。

- [ ] **Step 3: 实现 media.go**

```go
package main

import (
	"encoding/json"
	"fmt"
	"io"
	"net/http"
	"strconv"
)

const maxMediaBytes = 50 * 1024 * 1024

func (a *app) handleMediaUpload(w http.ResponseWriter, r *http.Request) {
	dev := deviceFrom(r)
	if r.ContentLength > maxMediaBytes {
		writeError(w, http.StatusRequestEntityTooLarge, "media too large")
		return
	}
	data, err := io.ReadAll(io.LimitReader(r.Body, maxMediaBytes+1))
	if err != nil || len(data) > maxMediaBytes {
		writeError(w, http.StatusRequestEntityTooLarge, "media too large")
		return
	}
	id, err := a.store.InsertMedia(dev.UserID, data)
	if err != nil {
		writeError(w, http.StatusInternalServerError, "internal")
		return
	}
	json.NewEncoder(w).Encode(map[string]int64{"mediaId": id})
}

func (a *app) handleMediaDownload(w http.ResponseWriter, r *http.Request) {
	dev := deviceFrom(r)
	id, err := strconv.ParseInt(r.PathValue("id"), 10, 64)
	if err != nil {
		writeError(w, http.StatusBadRequest, "bad id")
		return
	}
	data, err := a.store.GetMedia(dev.UserID, id)
	if err != nil {
		writeError(w, http.StatusNotFound, "not found")
		return
	}
	w.Header().Set("Content-Type", "application/octet-stream")
	fmt.Fprint(w, string(data))
}
```

- [ ] **Step 4: 注册路由并跑通**

在 `main.go` 的 `newApp` 增加：

```go
	a.mux.HandleFunc("POST /api/media", a.requireAuth(a.handleMediaUpload))
	a.mux.HandleFunc("GET /api/media/{id}", a.requireAuth(a.handleMediaDownload))
```

Run: `go test ./...`
Expected: 全部 PASS。

- [ ] **Step 5: 提交**

```bash
git add SyncServer/
git commit -m "feat: 媒体上传下载（50MB 限制、账号隔离）"
```

---

### Task 5: 历史拉取 API

**Covers:** S4

**Files:**
- Modify: `SyncServer/store.go`（Message struct 加 json tag）
- Create: `SyncServer/history.go`
- Create: `SyncServer/history_test.go`

**Interfaces:**
- Consumes: Task 3 的 `requireAuth`、`deviceFrom`；Task 2 的 `MessagesSince`
- Produces: `GET /api/history?since=<unix_ms>` → 200 `{"messages": [{"type","originDeviceId","seq","ts","payload"}]}`（payload raw JSON 透传）

- [ ] **Step 1: 给 store.go 的 Message struct 加 json tag**

```go
type Message struct {
	Type           string          `json:"type"`
	OriginDeviceID int64           `json:"originDeviceId"`
	Seq            int64           `json:"seq"`
	Ts             int64           `json:"ts"`
	Payload        json.RawMessage `json:"payload"`
}
```

（store.go 需补 `encoding/json` import。）

- [ ] **Step 2: 写失败测试**

```go
package main

import (
	"encoding/json"
	"fmt"
	"net/http"
	"net/http/httptest"
	"testing"
)

func TestHistory(t *testing.T) {
	a, _ := newTestApp(t)
	did, tok := registerToken(t, a, "alice", "phone")
	uid, _ := a.store.GetDeviceByToken(sha256Hex(tok))
	_, _ = a.store.InsertMessage(uid.UserID, did, "clip_text", []byte(`{"text":"a"}`))
	_, _ = a.store.InsertMessage(uid.UserID, did, "clip_text", []byte(`{"text":"b"}`))

	req := httptest.NewRequest("GET", "/api/history?since=0", nil)
	req.Header.Set("Authorization", "Bearer "+tok)
	rec := httptest.NewRecorder()
	a.mux.ServeHTTP(rec, req)
	if rec.Code != http.StatusOK {
		t.Fatalf("history = %d %s", rec.Code, rec.Body.String())
	}
	var out struct {
		Messages []Message `json:"messages"`
	}
	if err := json.Unmarshal(rec.Body.Bytes(), &out); err != nil {
		t.Fatalf("unmarshal: %v", err)
	}
	if len(out.Messages) != 2 || out.Messages[0].Type != "clip_text" {
		t.Fatalf("messages = %+v", out.Messages)
	}

	req = httptest.NewRequest("GET", "/api/history?since="+fmt.Sprint(out.Messages[1].Ts), nil)
	req.Header.Set("Authorization", "Bearer "+tok)
	rec = httptest.NewRecorder()
	a.mux.ServeHTTP(rec, req)
	if err := json.Unmarshal(rec.Body.Bytes(), &out); err != nil || len(out.Messages) != 0 {
		t.Fatalf("since filter = %+v, %v", out.Messages, err)
	}
}
```

- [ ] **Step 3: 运行确认失败**

Run: `go test -run TestHistory -v`
Expected: FAIL — handler 未注册（404）或编译错误。

- [ ] **Step 4: 实现 history.go 并注册路由**

```go
package main

import (
	"encoding/json"
	"net/http"
	"strconv"
)

func (a *app) handleHistory(w http.ResponseWriter, r *http.Request) {
	dev := deviceFrom(r)
	since, _ := strconv.ParseInt(r.URL.Query().Get("since"), 10, 64)
	msgs, err := a.store.MessagesSince(dev.UserID, since)
	if err != nil {
		writeError(w, http.StatusInternalServerError, "internal")
		return
	}
	json.NewEncoder(w).Encode(map[string][]Message{"messages": msgs})
}
```

在 `main.go` 的 `NewApp` 增加：

```go
	a.mux.HandleFunc("GET /api/history", a.requireAuth(a.handleHistory))
```

Run: `go test ./...`
Expected: 全部 PASS。

- [ ] **Step 5: 提交**

```bash
git add SyncServer/
git commit -m "feat: 历史拉取 API（since 过滤）"
```

---

### Task 6: WebSocket 实时转发

**Covers:** S4

**Files:**
- Create: `SyncServer/ws.go`
- Create: `SyncServer/ws_test.go`

**Interfaces:**
- Consumes: Task 3 的 token 校验（WS 用 `?token=` 查询参数直接查 `GetDeviceByToken`）、Task 2 的 `InsertMessage`/`MessagesSince`
- Produces:
  - `type Hub struct`（`map[int64]map[*conn]bool`，key=userID）、`func newHub() *Hub`、`register`/`unregister`/`broadcast(userID, originDeviceID int64, msg []byte)`
  - `type conn struct { userID, deviceID int64; ws *websocket.Conn; send chan []byte }`
  - `func (a *app) handleWS(w, r)`：`?token=` 校验 → 升级 → 注册 → `go c.writeLoop()` → `c.readLoop(a)`
  - 上行消息 `{"type":"...","payload":{...}}`（服务端补齐 originDeviceId/seq/ts）；下行即落库后带完整信封的消息，广播给同账号**其他设备**的连接

- [ ] **Step 1: 写失败测试**

```go
package main

import (
	"encoding/json"
	"net/http"
	"net/http/httptest"
	"strings"
	"testing"
	"time"

	"github.com/gorilla/websocket"
)

func wsURL(t *testing.T, ts *httptest.Server, token string) string {
	t.Helper()
	return "ws" + strings.TrimPrefix(ts.URL, "http") + "/ws?token=" + token
}

func dialWS(t *testing.T, url string) *websocket.Conn {
	t.Helper()
	c, _, err := websocket.DefaultDialer.Dial(url, nil)
	if err != nil {
		t.Fatalf("dial: %v", err)
	}
	t.Cleanup(func() { c.Close() })
	return c
}

func readMessage(t *testing.T, c *websocket.Conn) map[string]any {
	t.Helper()
	_, data, err := c.ReadMessage()
	if err != nil {
		t.Fatalf("read: %v", err)
	}
	var m map[string]any
	if err := json.Unmarshal(data, &m); err != nil {
		t.Fatalf("unmarshal %s: %v", data, err)
	}
	return m
}

func TestWSForwardWithinAccount(t *testing.T) {
	a, _ := newTestApp(t)
	ts := httptest.NewServer(a.mux)
	t.Cleanup(ts.Close)

	didA, tokA := registerToken(t, a, "alice", "phone")
	_, tokB := registerToken(t, a, "alice", "pc")

	connA := dialWS(t, wsURL(t, ts, tokA))
	connB := dialWS(t, wsURL(t, ts, tokB))

	if err := connA.WriteMessage(websocket.TextMessage, []byte(`{"type":"clip_text","payload":{"text":"hello 世界"}}`)); err != nil {
		t.Fatalf("write A: %v", err)
	}
	m := readMessage(t, connB)
	if m["type"] != "clip_text" {
		t.Fatalf("B received type = %v", m)
	}
	if m["originDeviceId"] != float64(didA) {
		t.Fatalf("originDeviceId = %v, want %d", m["originDeviceId"], didA)
	}
	if m["payload"].(map[string]any)["text"] != "hello 世界" {
		t.Fatalf("payload = %v", m["payload"])
	}
	connA.SetReadDeadline(time.Now().Add(500 * time.Millisecond))
	if _, _, err := connA.ReadMessage(); err == nil {
		t.Fatal("source device should not receive its own message")
	}
}

func TestWSIsolationAcrossAccounts(t *testing.T) {
	a, _ := newTestApp(t)
	ts := httptest.NewServer(a.mux)
	t.Cleanup(ts.Close)

	_, tokA := registerToken(t, a, "alice", "phone")
	_, tokB := registerToken(t, a, "bob", "phone")

	connA := dialWS(t, wsURL(t, ts, tokA))
	connB := dialWS(t, wsURL(t, ts, tokB))

	_ = connA.WriteMessage(websocket.TextMessage, []byte(`{"type":"clip_text","payload":{"text":"secret"}}`))
	connB.SetReadDeadline(time.Now().Add(500 * time.Millisecond))
	if _, _, err := connB.ReadMessage(); err == nil {
		t.Fatal("cross-account message leaked")
	}
}

func TestWSRequiresToken(t *testing.T) {
	a, _ := newTestApp(t)
	ts := httptest.NewServer(a.mux)
	t.Cleanup(ts.Close)
	if _, _, err := websocket.DefaultDialer.Dial(wsURL(t, ts, "bad-token"), nil); err == nil {
		t.Fatal("dial with bad token should fail")
	}
}
```

- [ ] **Step 2: 运行确认失败**

Run: `go test -run 'TestWS' -v`
Expected: FAIL — `handleWS` 未注册/未定义。

- [ ] **Step 3: 实现 ws.go**

```go
package main

import (
	"encoding/json"
	"log"
	"net/http"
	"sync"
	"time"

	"github.com/gorilla/websocket"
)

const (
	wsWriteWait  = 10 * time.Second
	wsPongWait   = 60 * time.Second
	wsPingPeriod = 30 * time.Second
)

var upgrader = websocket.Upgrader{
	ReadBufferSize:  4096,
	WriteBufferSize: 4096,
}

type Hub struct {
	mu    sync.Mutex
	conns map[int64]map[*conn]bool
}

type conn struct {
	userID   int64
	deviceID int64
	ws       *websocket.Conn
	send     chan []byte
}

func newHub() *Hub { return &Hub{conns: make(map[int64]map[*conn]bool)} }

func (h *Hub) register(c *conn) {
	h.mu.Lock()
	defer h.mu.Unlock()
	if h.conns[c.userID] == nil {
		h.conns[c.userID] = make(map[*conn]bool)
	}
	h.conns[c.userID][c] = true
}

func (h *Hub) unregister(c *conn) {
	h.mu.Lock()
	defer h.mu.Unlock()
	if set := h.conns[c.userID]; set != nil {
		delete(set, c)
		if len(set) == 0 {
			delete(h.conns, c.userID)
		}
	}
}

func (h *Hub) broadcast(userID int64, originDeviceID int64, msg []byte) {
	h.mu.Lock()
	defer h.mu.Unlock()
	for c := range h.conns[userID] {
		if c.deviceID == originDeviceID {
			continue
		}
		select {
		case c.send <- msg:
		default:
			close(c.send)
			delete(h.conns[userID], c)
		}
	}
}

type wsIncoming struct {
	Type    string          `json:"type"`
	Payload json.RawMessage `json:"payload"`
}

func (a *app) handleWS(w http.ResponseWriter, r *http.Request) {
	raw := r.URL.Query().Get("token")
	if raw == "" {
		writeError(w, http.StatusUnauthorized, "missing token")
		return
	}
	dev, err := a.store.GetDeviceByToken(sha256Hex(raw))
	if err != nil {
		writeError(w, http.StatusUnauthorized, "invalid token")
		return
	}
	ws, err := upgrader.Upgrade(w, r, nil)
	if err != nil {
		return
	}
	c := &conn{userID: dev.UserID, deviceID: dev.ID, ws: ws, send: make(chan []byte, 64)}
	a.hub.register(c)
	a.store.TouchDevice(dev.ID)
	go c.writeLoop()
	c.readLoop(a)
}

func (c *conn) readLoop(a *app) {
	defer func() {
		a.hub.unregister(c)
		c.ws.Close()
	}()
	c.ws.SetReadLimit(1 << 20)
	c.ws.SetReadDeadline(time.Now().Add(wsPongWait))
	c.ws.SetPongHandler(func(string) error {
		return c.ws.SetReadDeadline(time.Now().Add(wsPongWait))
	})
	for {
		_, data, err := c.ws.ReadMessage()
		if err != nil {
			return
		}
		var in wsIncoming
		if err := json.Unmarshal(data, &in); err != nil || in.Type == "" {
			continue
		}
		if _, err := a.store.InsertMessage(c.userID, c.deviceID, in.Type, in.Payload); err != nil {
			log.Printf("insert message: %v", err)
			continue
		}
		msgs, err := a.store.MessagesSince(c.userID, 0)
		if err != nil || len(msgs) == 0 {
			continue
		}
		last := msgs[len(msgs)-1]
		out, _ := json.Marshal(map[string]any{
			"type":           last.Type,
			"originDeviceId": last.OriginDeviceID,
			"seq":            last.Seq,
			"ts":             last.Ts,
			"payload":        json.RawMessage(last.Payload),
		})
		a.hub.broadcast(c.userID, c.deviceID, out)
	}
}

func (c *conn) writeLoop() {
	ticker := time.NewTicker(wsPingPeriod)
	defer func() {
		ticker.Stop()
		c.ws.Close()
	}()
	for {
		select {
		case msg, ok := <-c.send:
			c.ws.SetWriteDeadline(time.Now().Add(wsWriteWait))
			if !ok {
				c.ws.WriteMessage(websocket.CloseMessage, []byte{})
				return
			}
			if err := c.ws.WriteMessage(websocket.TextMessage, msg); err != nil {
				return
			}
		case <-ticker.C:
			c.ws.SetWriteDeadline(time.Now().Add(wsWriteWait))
			if err := c.ws.WriteMessage(websocket.PingMessage, nil); err != nil {
				return
			}
		}
	}
}
```

- [ ] **Step 4: 接线并跑通**

在 `main.go` 中（app struct 加 hub 字段、NewApp 注册）：

```go
type app struct {
	mux   *http.ServeMux
	store *Store
	hub   *Hub
}

// NewApp() 内：
	a.hub = newHub()
	a.mux.HandleFunc("GET /ws", a.handleWS)
```

Run: `go test ./...`
Expected: 全部 PASS（TestWS 三例通过）。

- [ ] **Step 5: 提交**

```bash
git add SyncServer/
git commit -m "feat: WebSocket 实时转发（账号隔离、心跳、来源设备排除）"
```

---

### Task 7: 定时清理任务

**Covers:** S4, S7

**Files:**
- Create: `SyncServer/cleanup.go`
- Create: `SyncServer/cleanup_test.go`

**Interfaces:**
- Consumes: Task 2 的 `Store.Cleanup`
- Produces: `const retentionMs = 7 * 24 * time.Hour.Milliseconds()`；`func startCleanup(s *Store, stop <-chan struct{})`——启动立即清理一次，之后每 24h 清理；`main()` 调用

- [ ] **Step 1: 写失败测试**

```go
package main

import (
	"testing"
	"time"
)

func TestStartCleanupRemovesOld(t *testing.T) {
	s := openTestStore(t)
	uid, _ := s.CreateUser("alice", "hash1")
	did, _ := s.CreateDevice(uid, "phone", "th1")
	_, _ = s.InsertMessage(uid, did, "clip_text", []byte(`{"text":"old"}`))

	now := time.Now().UnixMilli()
	if _, err := s.db.Exec(`UPDATE messages SET ts = ? WHERE user_id = ?`, now-retentionMs-1000, uid); err != nil {
		t.Fatal(err)
	}
	stop := make(chan struct{})
	startCleanup(s, stop)
	defer close(stop)

	deadline := time.Now().Add(2 * time.Second)
	for time.Now().Before(deadline) {
		if msgs, _ := s.MessagesSince(uid, 0); len(msgs) == 0 {
			return
		}
		time.Sleep(50 * time.Millisecond)
	}
	t.Fatal("old message not cleaned within 2s")
}
```

- [ ] **Step 2: 运行确认失败**

Run: `go test -run TestStartCleanupRemovesOld -v`
Expected: FAIL — `retentionMs`/`startCleanup` 未定义。

- [ ] **Step 3: 实现 cleanup.go**

```go
package main

import (
	"time"
)

const retentionMs = 7 * 24 * time.Hour.Milliseconds()

func startCleanup(s *Store, stop <-chan struct{}) {
	go func() {
		s.Cleanup(time.Now().UnixMilli() - retentionMs)
		ticker := time.NewTicker(24 * time.Hour)
		defer ticker.Stop()
		for {
			select {
			case <-ticker.C:
				s.Cleanup(time.Now().UnixMilli() - retentionMs)
			case <-stop:
				return
			}
		}
	}()
}
```

- [ ] **Step 4: main() 接线并跑通**

在 `main.go` 的 `main()` 中：

```go
	stop := make(chan struct{})
	defer close(stop)
	startCleanup(store, stop)
```

Run: `go test ./...`
Expected: 全部 PASS。

- [ ] **Step 5: 提交**

```bash
git add SyncServer/
git commit -m "feat: 7 天保留期定时清理"
```

---

### Task 8: 端到端冒烟测试（本地双客户端联调）

**Covers:** S4

**Files:**
- Create: `SyncServer/smoke_test.go`

**Interfaces:**
- Consumes: 全部 HTTP/WS 端点；复用 `registerToken`（Task 4 定义于 media_test.go）、`dialWS`/`readMessage`（Task 6 定义于 ws_test.go）
- Produces: `TestSmokeEndToEnd`——内起 httptest 服务器，注册 3 设备，验证文本互通、图片上传→转发→下载比对、历史拉取、跨账号隔离

- [ ] **Step 1: 写冒烟测试**

```go
package main

import (
	"bytes"
	"encoding/json"
	"fmt"
	"net/http"
	"net/http/httptest"
	"testing"
	"time"

	"github.com/gorilla/websocket"
)

func smokeWrite(t *testing.T, c *websocket.Conn, msg string) {
	t.Helper()
	if err := c.WriteMessage(websocket.TextMessage, []byte(msg)); err != nil {
		t.Fatalf("write: %v", err)
	}
}

func smokeUpload(t *testing.T, base, token string, data []byte) int64 {
	t.Helper()
	req, _ := http.NewRequest("POST", base+"/api/media", bytes.NewReader(data))
	req.Header.Set("Authorization", "Bearer "+token)
	resp, err := http.DefaultClient.Do(req)
	if err != nil {
		t.Fatalf("upload: %v", err)
	}
	defer resp.Body.Close()
	var out struct {
		MediaID int64 `json:"mediaId"`
	}
	json.NewDecoder(resp.Body).Decode(&out)
	if out.MediaID == 0 {
		t.Fatalf("upload status = %d", resp.StatusCode)
	}
	return out.MediaID
}

func smokeDownload(t *testing.T, base, token string, id int64) []byte {
	t.Helper()
	req, _ := http.NewRequest("GET", fmt.Sprintf("%s/api/media/%d", base, id), nil)
	req.Header.Set("Authorization", "Bearer "+token)
	resp, err := http.DefaultClient.Do(req)
	if err != nil {
		t.Fatalf("download: %v", err)
	}
	defer resp.Body.Close()
	var buf bytes.Buffer
	buf.ReadFrom(resp.Body)
	return buf.Bytes()
}

func smokeHistory(t *testing.T, base, token string) []any {
	t.Helper()
	req, _ := http.NewRequest("GET", base+"/api/history?since=0", nil)
	req.Header.Set("Authorization", "Bearer "+token)
	resp, err := http.DefaultClient.Do(req)
	if err != nil {
		t.Fatalf("history: %v", err)
	}
	defer resp.Body.Close()
	var out struct {
		Messages []any `json:"messages"`
	}
	json.NewDecoder(resp.Body).Decode(&out)
	return out.Messages
}

func TestSmokeEndToEnd(t *testing.T) {
	a, _ := newTestApp(t)
	ts := httptest.NewServer(a.mux)
	t.Cleanup(ts.Close)

	_, tokA := registerToken(t, a, "alice", "phone")
	_, tokB := registerToken(t, a, "alice", "pc")
	_, tokC := registerToken(t, a, "mallory", "phone")

	connA := dialWS(t, wsURL(t, ts, tokA))
	connB := dialWS(t, wsURL(t, ts, tokB))
	connC := dialWS(t, wsURL(t, ts, tokC))
	defer connA.Close()
	defer connB.Close()
	defer connC.Close()

	smokeWrite(t, connA, `{"type":"clip_text","payload":{"text":"hello 世界"}}`)
	m := readMessage(t, connB)
	if m["type"] != "clip_text" || m["payload"].(map[string]any)["text"] != "hello 世界" {
		t.Fatalf("text forward = %v", m)
	}
	t.Log("PASS 文本互通")

	img := []byte("FAKEPNG-IMAGE-DATA-12345")
	mediaID := smokeUpload(t, ts.URL, tokA, img)
	smokeWrite(t, connA, fmt.Sprintf(`{"type":"clip_image","payload":{"mediaId":%d,"name":"a.png","size":%d}}`, mediaID, len(img)))
	m = readMessage(t, connB)
	if m["type"] != "clip_image" {
		t.Fatalf("image forward = %v", m)
	}
	got := smokeDownload(t, ts.URL, tokB, int64(m["payload"].(map[string]any)["mediaId"].(float64)))
	if !bytes.Equal(got, img) {
		t.Fatalf("image mismatch: got %d bytes want %d", len(got), len(img))
	}
	t.Log("PASS 图片上传/转发/下载")

	if hist := smokeHistory(t, ts.URL, tokB); len(hist) < 2 {
		t.Fatalf("history len = %d", len(hist))
	}
	t.Log("PASS 历史拉取")

	connC.SetReadDeadline(time.Now().Add(500 * time.Millisecond))
	smokeWrite(t, connA, `{"type":"clip_text","payload":{"text":"secret"}}`)
	if _, _, err := connC.ReadMessage(); err == nil {
		t.Fatal("cross-account leak")
	}
	t.Log("PASS 跨账号隔离")
}
```

- [ ] **Step 2: 运行确认失败**

Run: `go test -run TestSmokeEndToEnd -v`
Expected: FAIL — `smoke_test.go` 尚无（或路由未注册导致 404/连接失败）。

- [ ] **Step 3: 重构 newApp 为导出的 NewApp（供后续 M2/M3 客户端复用同一构造）**

`main.go` 全量替换为：

```go
package main

import (
	"encoding/json"
	"flag"
	"log"
	"net/http"
)

type app struct {
	mux   *http.ServeMux
	store *Store
	hub   *Hub
}

func NewApp(s *Store) *app {
	a := &app{mux: http.NewServeMux(), store: s, hub: newHub()}
	a.mux.HandleFunc("GET /api/health", func(w http.ResponseWriter, r *http.Request) {
		json.NewEncoder(w).Encode(map[string]bool{"ok": true})
	})
	a.mux.HandleFunc("POST /api/auth/register", a.handleRegister)
	a.mux.HandleFunc("POST /api/auth/login", a.handleLogin)
	a.mux.HandleFunc("GET /api/devices", a.requireAuth(a.handleListDevices))
	a.mux.HandleFunc("DELETE /api/devices/{id}", a.requireAuth(a.handleDeleteDevice))
	a.mux.HandleFunc("POST /api/media", a.requireAuth(a.handleMediaUpload))
	a.mux.HandleFunc("GET /api/media/{id}", a.requireAuth(a.handleMediaDownload))
	a.mux.HandleFunc("GET /api/history", a.requireAuth(a.handleHistory))
	a.mux.HandleFunc("GET /ws", a.handleWS)
	return a
}

func main() {
	addr := flag.String("addr", "127.0.0.1:8082", "listen address")
	dbPath := flag.String("db", "sync.db", "sqlite database path")
	flag.Parse()
	store, err := OpenStore(*dbPath)
	if err != nil {
		log.Fatal(err)
	}
	defer store.Close()
	stop := make(chan struct{})
	defer close(stop)
	startCleanup(store, stop)
	log.Printf("sync server listening on %s (db=%s)", *addr, *dbPath)
	if err := http.ListenAndServe(*addr, NewApp(store).mux); err != nil {
		log.Fatal(err)
	}
}
```

同步更新 Task 3 中定义的 `newTestApp`（auth_test.go）：

```go
func newTestApp(t *testing.T) (*app, *Store) {
	t.Helper()
	s, err := OpenStore("file::memory:?cache=shared")
	if err != nil {
		t.Fatalf("OpenStore: %v", err)
	}
	t.Cleanup(func() { s.Close() })
	return NewApp(s), s
}
```

- [ ] **Step 4: 冒烟 + 全量回归**

Run: `go test -run TestSmokeEndToEnd -v`
Expected: 四行 PASS 日志（文本互通/图片上传转发下载/历史拉取/跨账号隔离）。

Run: `go test ./...`
Expected: 全部 PASS。

- [ ] **Step 5: 提交**

```bash
git add SyncServer/
git commit -m "feat: 端到端冒烟测试（文本/图片/历史/隔离）"
```

