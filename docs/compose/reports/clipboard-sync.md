---
feature: clipboard-sync
status: delivered (M1 服务器端 + M2 Windows 端；M3 Android 端待建)
specs:
  - docs/compose/spec/2026-08-09-clipboard-sync-design.md
plans:
  - docs/compose/plans/2026-08-09-sync-server.md
  - docs/compose/plans/2026-08-09-windows-sync.md
branch: main
commits: 2420ebb..f461f62
---

# 剪贴板跨设备同步 — 最终报告（M1+M2 阶段）

## What Was Built

剪贴板跨设备同步已交付**服务器端（SyncServer，M1）与 Windows 端改造（M2）**，实现手机与电脑剪贴板双向同步：账号注册/登录（设备 token 认证）、WebSocket 实时转发（同账号多设备互推、账号隔离）、媒体上传下载（≤50MB）、7 天短期存储与历史回放、Windows 端历史来源标签（"手机"橙色角标）与主窗口来源筛选（全部/本机/手机）、设置页末尾"实验性功能：多端同步"开关与账号登录区。

同步语义：电脑复制的内容自动上传，手机端收到后写入剪贴板；手机复制的内容进入电脑历史（标注来源），用户在 Win+V 悬浮列表中选择粘贴。Windows 端只入历史不写系统剪贴板，天然无回环。**同步默认关闭**，用户在设置页勾选"多端同步（实验性）"并登录后才启用。

M3（Android App）计划在后续里程碑生成，届时更新本报告。

## Architecture

### SyncServer（`SyncServer/`，Go 1.26 单二进制）

| 文件 | 职责 |
|------|------|
| `main.go` | `NewApp(s *Store)` 路由注册；`main()` 启动参数与清理任务 |
| `store.go` | SQLite（modernc.org/sqlite）：users/devices/messages/media 四表，账号隔离 |
| `auth.go` | 注册/登录、设备 token（32 随机字节 hex，存 sha256）、requireAuth、设备列表/解绑 |
| `media.go` | 上传下载（50MB 限制、跨账号 404） |
| `history.go` | 历史拉取 `?since=<ms>`（空历史返回 `[]` 而非 null） |
| `ws.go` | Hub + gorilla/websocket：30s ping / 60s pong 超时、落库后广播（排除来源设备） |
| `cleanup.go` | 启动即清 + 每 24h 清 >7 天数据 |
| `cmd/phone-sim/` | 模拟手机端联调工具（M2 新增，M4 复用） |

### Windows 端（`ClipboardTool/`，C# WPF .NET 9）

| 文件 | 职责 |
|------|------|
| `Services/SyncClient.cs` | 网络客户端：注册/登录、媒体上传下载、历史拉取、WS 长连接（退避重连 1s→60s、`ConfigureAwait(false)`） |
| `Services/SyncService.cs` | 编排：`EntryCaptured` 事件上传、远端消息入库（source=phone）、`SyncLastSeq` 持久化去重、图片内容哈希去重 |
| `Services/ClipboardMonitor.cs` | 追加 `EntryCaptured` 事件（本地捕获入库成功后触发） |
| `Services/Entry.cs` / `ClipboardStore.cs` | `Source` 字段（local/phone）、旧库自动补列迁移、`Query(search, type, source)` |
| `Services/Settings.cs` | `SyncEnabled`/账号字段/`SyncServerOverride`/`SyncLastSeq` |
| `SettingsWindow` | 设置页末尾"实验性功能：多端同步"开关 + 账号区（登录/注册/退出） |
| `OverlayWindow`/`MainWindow` | 条目右上"手机"橙色标签；主窗口来源筛选（全部/本机/手机） |
| `App.xaml.cs` | SyncService 生命周期（SyncEnabled 启停）、`--data-dir` 测试参数 |

### 关键数据流

- 手机→电脑：手机复制 → 上传 → 服务器广播 → Windows `SyncService` 入库（source=phone）→ 用户从历史选择粘贴
- 电脑→手机：本地复制 → `EntryCaptured` → 上传 → 广播 → 手机写剪贴板 + 入库
- 重连/重启：`GET /history?since=0` 回放，`SyncLastSeq` 持久化保证跨重启不重复；图片按像素字节哈希去重、文本按内容哈希去重、文件靠 seq 去重
- 双镜像：`https://sync.starry0214.one` 优先 + `https://107.175.228.83:8081` IP 直连兜底（跳过证书主机名校验）；`SyncServerOverride` 供本地联调

### Design Decisions

- **Windows 端只入历史、不写系统剪贴板**——双向同步语义下手机端写剪贴板即可，电脑端保持"用户从历史选择"的既有交互，消除回环。
- **SyncClient 全部 await 加 `ConfigureAwait(false)`**——网络层不捕获 UI SynchronizationContext，避免线程池/UI 互相等待。
- **seq 持久化去重**（`SyncLastSeq` 存 settings.json）——跨重启回放精确去重，比仅靠内容哈希可靠（文件条目路径每次不同）。
- **`--data-dir` 测试参数**——联调用隔离数据目录，不污染真实历史。

## Usage

Windows 端：设置 → 勾选"多端同步（实验性）" → 输入账号/密码/设备名 → 注册或登录。再次打开设置可退出登录。

服务器（M4 部署前本地联调用）：

```bash
cd SyncServer
go run . -addr 127.0.0.1:8082 -db sync.db
```

模拟手机端（联调）：

```bash
cd SyncServer
go run ./cmd/phone-sim -base http://127.0.0.1:8082 -user alice -pass secret123 -device phone-sim -kind text -text "hello"
go run ./cmd/phone-sim -base http://127.0.0.1:8082 -user alice -pass secret123 -device phone-sim -kind image -media test.png
```

## Verification

- **SyncServer**：`go test ./...` 15 个测试全绿 + `TestSmokeEndToEnd` 端到端冒烟（文本/图片/历史/隔离）。
- **Windows 端**：`dotnet build` 0 错误 0 警告；启动 exe 模拟操作验证（`--data-dir` 隔离目录）。
- **M2 本地联调**（真实 SyncServer + exe + phone-sim 全链路）：
  - 手机→电脑：文本/图片/文件实时推送入库，source=phone，缩略图/文件落盘正确
  - 电脑→手机：Set-Clipboard 复制 → phone-sim 实时收到（originDeviceId 正确）
  - 跨重启回放：history 拉取不重复（SyncLastSeq 去重）
  - 图片内容去重：同一图片 5 次上传仅 1 条入库，重复文件不残留
  - 防回环：本地复制条目不重复同步
  - 空历史：服务器返回 `[]`（修复 null 后）

## Journey Log

> Brief notes on what informed the final design. Not required reading.

- [lesson] **WS URL 拼接 bug**：`"ws" + host[..]` 少了 `://`，UriFormatException 静默重试——幸好加了连接日志才暴露；网络层必须有状态日志。
- [lesson] **UI 线程死锁**：网络层 await 捕获 WPF SynchronizationContext + `GetResult()` 同步等待 → 图片下载必死锁（文本路径无恙，隐蔽性强）。修复：全部 `ConfigureAwait(false)` + async 链。联调矩阵必须覆盖"带下载的媒体路径"。
- [lesson] **单位不一致**：服务器 ts 毫秒 vs 本地库秒——跨端协议的时间单位必须在 spec 里写明。
- [lesson] **回放重复**：文件条目路径每次新 GUID → 哈希去重失效，须用服务器 seq 持久化去重。
- [lesson] **Go nil slice 序列化为 null**：空历史 `{"messages":null}` 让 C# `EnumerateArray()` 抛异常返回 null——服务端空集合一律 `make([]T, 0)`。
- [lesson] **go run 进程管理**：`go run` 实际运行的是编译出的 `syncserver` 临时进程，杀 `go` 进程不杀它，旧代码占用端口导致"修复未生效"假象。

## Source Materials

| File | Role | Notes |
|------|------|-------|
| `docs/compose/spec/2026-08-09-clipboard-sync-design.md` | 设计 spec | S3/S4/S7 已实现；S5 待 M3 |
| `docs/compose/plans/2026-08-09-sync-server.md` | M1 实施计划 | 8 任务完成 |
| `docs/compose/plans/2026-08-09-windows-sync.md` | M2 实施计划 | 8 任务完成 |
