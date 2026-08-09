---
feature: clipboard-sync
status: delivered (M1 服务器端；M2 Windows/M3 Android 待建)
specs:
  - docs/compose/spec/2026-08-09-clipboard-sync-design.md
plans:
  - docs/compose/plans/2026-08-09-sync-server.md
branch: main
commits: 2420ebb..b1efa95
---

# 剪贴板跨设备同步 — 最终报告（M1 阶段）

## What Was Built

剪贴板跨设备同步的**服务器端（SyncServer）**已交付：Go 单二进制的剪贴板同步服务，提供账号注册/登录（用户名+密码，bcrypt，设备 token 认证）、WebSocket 实时消息转发（同账号多设备互推，跨账号隔离）、媒体上传下载（≤50MB，7 天保留）、历史拉取（since 过滤）、定时清理。部署形态为监听 `127.0.0.1:8082`，由 nginx 反代提供 TLS（部署属 M4）。

整个"剪贴板跨设备同步"特性由三部分组成，本报告覆盖已交付的 M1；M2（Windows 端改造）与 M3（Android 端）计划在各自里程碑开始时生成并执行，届时更新本报告。

## Architecture

代码位于 `SyncServer/`（Go 1.26，模块 `syncserver`），单包多文件：

| 文件 | 职责 |
|------|------|
| `main.go` | `NewApp(s *Store)` 导出构造：路由注册（Go 1.22 方法+路径模式）；`main()` 启动参数与清理任务 |
| `store.go` | SQLite 存储层（modernc.org/sqlite 纯 Go 驱动）：users / devices / messages / media 四表，账号隔离查询 |
| `auth.go` | 注册/登录 handler、设备 token 生成（32 随机字节 hex，存 sha256）、`requireAuth` 中间件、设备列表/解绑 |
| `media.go` | 媒体上传下载（`Content-Length` + `LimitReader` 双重 50MB 限制，跨账号 404） |
| `history.go` | 历史拉取 `?since=<unix_ms>` |
| `ws.go` | Hub（`map[userID]map[*conn]bool`）+ gorilla/websocket 长连接：30s ping / 60s pong 超时、读循环落库后广播（排除来源设备） |
| `cleanup.go` | `startCleanup`：启动即清 + 每 24h 清 `>7 天` 的 messages/media |

数据流：客户端经 WS 上行 `{"type":"clip_text|clip_image|clip_file","payload":{...}}` → 服务端补 `originDeviceId/seq/ts` 落库 → 广播给同账号其他设备的连接；离线恢复走 `GET /api/history`。图片/文件先 `POST /api/media` 拿 mediaId，消息只带引用，避免大包阻塞 WS。

### Design Decisions

- **凭证统一为设备 token**（注册/登录即完成设备登记），而非用户 token + 设备 token 两层——API/WS 单一凭证，解绑即吊销，实现最简。
- **广播排除来源设备**而非排除来源连接——同设备多连接不会收到自己发的消息，客户端侧防回环负担最小。
- **WS 只传轻量 JSON，二进制走 HTTP**——50MB 媒体不经过 64 条缓冲的 send channel，避免背压阻塞。
- **`NewApp` 导出**（替代包内 `newApp`）——为 M2/M3 客户端与测试提供统一构造入口。

## Usage

```bash
cd SyncServer
go run . -addr 127.0.0.1:8082 -db sync.db
```

| 端点 | 说明 |
|---|---|
| `POST /api/auth/register` | `{"username","password","deviceName"}` → 201 `{"deviceId","token"}`（用户名 ≥4，密码 ≥6） |
| `POST /api/auth/login` | 同字段 → 200 同响应（同账号新设备） |
| `GET /api/devices` / `DELETE /api/devices/{id}` | 设备列表 / 解绑 |
| `POST /api/media` | raw body ≤50MB → 201 `{"mediaId"}` |
| `GET /api/media/{id}` | 下载（仅本账号，过期/跨账号 404） |
| `GET /api/history?since=<ms>` | 短期历史 |
| `GET /ws?token=` | 长连接；上行 `{"type","payload"}`，下行带 `originDeviceId/seq/ts` 信封 |
| `GET /api/health` | 健康检查 |

除 auth 外均需 `Authorization: Bearer <token>`。

## Verification

`go test ./...` 全绿，共 15 个测试：健康检查、用户/设备 CRUD、消息/媒体存取、清理（含旧数据删除）、注册/登录全路径（409/400/401 分支）、requireAuth（无 token/伪造/有效）、媒体上传下载（跨账号 404、超限 413）、历史 since 过滤、WS 同账号转发（含中文、来源设备不回声）、跨账号隔离、坏 token 拒绝、启动清理。`TestSmokeEndToEnd` 端到端冒烟四步 PASS：文本互通、图片上传→转发→下载字节比对、历史拉取、跨账号隔离。`go vet ./...` 无告警。

`-race` 检测因本机无 64 位 CGO 工具链（MinGW `cc1.exe` 不支持 64-bit）跳过——环境限制，非代码问题。

## Journey Log

> Brief notes on what informed the final design. Not required reading.

- [lesson] 计划中三处测试代码缺陷在执行时暴露并修正：重复用户名用例用了弱密码（被 400 校验提前拦截，测不到 409）；同账号第二设备误用 register（409 冲突）应改用 login；测试用户名 "bob" 仅 3 字符不满足 ≥4 校验。
- [lesson] Go 的 `const` 不能用方法调用表达式（`time.Hour.Milliseconds()`），保留期常量改为纯毫秒字面量。
- [lesson] `json.NewEncoder(w).Encode` 前必须显式 `WriteHeader`，否则注册 201/上传 201 会变 200（两处测试当场捕获）。
- [lesson] `-race` 依赖 CGO 工具链，Windows 政务网环境不可用——普通测试已覆盖并发路径，联调验证依赖 M4 真机场景。

## Source Materials

| File | Role | Notes |
|------|------|-------|
| `docs/compose/spec/2026-08-09-clipboard-sync-design.md` | 设计 spec | S3/S4/S7 由本计划实现；S5/S6 待 M2/M3 |
| `docs/compose/plans/2026-08-09-sync-server.md` | 实施计划 | 8 任务全部完成 |
