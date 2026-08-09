# 剪贴板跨设备同步设计（Android + Windows + VPS 同步服务）

> [!NOTE]
> This document may not reflect the current implementation.
> See the final report for up-to-date state:
> [Final Report](../reports/clipboard-sync.md)

日期：2026-08-09
状态：已获用户批准（2026-08-09）

## [S1] 问题

用户为小米 14 Pro（Android）开发剪贴板工具，核心诉求是**与现有 Windows 剪贴板工具（C#/WPF，v1.3.8）跨设备双向同步**。现有 Windows 工具是成熟的本地历史管理工具（文本/图片/文件、Win+V 悬浮列表、自更新），本设计为其增加"手机端 + 账号 + 同步服务"，构成三端闭环。

约束：
- 同步服务自建在现有 VPS（107.175.228.83），nignx 已有域名 `code.starry0214.one` 与证书体系。
- Windows 端处于政务网，域名（境外中继）时通时断，必须沿用"域名优先 + IP 直连兜底"双镜像策略。
- Android 10+ 后台读剪贴板受限（官方文档：仅默认 IME 与获焦 App 可访问剪贴板；小米 HyperOS 后台回调亦不派发——2026-08-09 实证，无障碍服务方案废弃）。

## [S2] 解决方案总览

三端拓扑：

```
┌──────────────┐   WSS/HTTPS   ┌───────────────────┐   WSS/HTTPS   ┌──────────────┐
│ Android App  │◄────────────►│  VPS 同步服务      │◄────────────►│ Windows 工具  │
│ 小米14 Pro   │              │  Go 单二进制       │              │ C# WPF 改造  │
│ Kotlin+Compose│             │  nginx 反代+证书   │              │ .NET 9       │
└──────────────┘              │  SQLite 短期存储   │              └──────────────┘
                              └───────────────────┘
```

- **同步服务**：Go 单二进制部署于 VPS，监听 `127.0.0.1:8082`（内网明文）；nginx 提供两个 TLS 入口并反代到该端口——`sync.starry0214.one:443`（域名，证书复用/新签）+ `107.175.228.83:8081`（IP 直连兜底，同一证书，客户端跳过证书主机名校验）。SQLite 短期存储最近 7 天消息与媒体，定时清理。
- **传输**：WebSocket 长连接推实时消息（延迟 <1s，心跳 30s）；图片/文件等二进制走 HTTP API（上传得 mediaId，消息只带引用）。
- **账号**：用户名 + 密码注册/登录，bcrypt 哈希；登录/注册即完成设备登记并签发设备 token（所有 API/WS 凭证）。同一账号下所有已登录设备组成同步组。
- **Windows 端改造**：新增同步模块 + 历史库 `source` 字段 + 来源标签/筛选 + 设置页登录。
- **Android 端新建**：Kotlin + Jetpack Compose，App 打开（获焦）即同步当前剪贴板（小米后台无法监听，2026-08-09 实证后废弃无障碍服务方案），App 内历史列表。

## [S3] 账号与设备管理

- `POST /api/auth/register`：用户名 + 密码注册（bcrypt 存储，用户名唯一、非空、长度 ≥4），注册即完成设备登记并返回 deviceId + 设备 token。
- `POST /api/auth/login`：校验密码，完成设备登记（已有设备复用，新设备登记）并返回 deviceId + 设备 token。**凭证统一为设备 token**（随机 256bit，服务端存哈希）：所有 API/WS 请求都带它，绑定 user_id + device_id；token 长期有效，解绑设备或改密码（吊销该账号全部设备 token）即失效。
- `GET /api/devices` / `DELETE /api/devices/{id}`：查看设备列表（名称、最后在线时间）、解绑设备（吊销该设备 token）。
- 数据隔离：users / devices / messages / media 全部带 user_id，任何查询以 token 对应账号为界。
- 双端 UI：Windows 设置窗口加"账号登录"区（注册/登录/设备管理）；Android 首启显示登录页，主界面设置页含账号与设备管理、无障碍引导。

## [S4] 同步协议与数据流

### API 端点（前缀 `/api`，除 auth 外均需 `Authorization: Bearer <token>`）

| 端点 | 说明 |
|---|---|
| POST /auth/register | 注册，返回 token |
| POST /auth/login | 登录，返回 token |
| POST /devices | 更新设备名（可选；设备名也可随 register/login 提交） |
| GET /devices | 设备列表 |
| DELETE /devices/{id} | 解绑设备 |
| GET /history?since=<unix_ms> | 拉取该账号短期历史（≤7 天），新设备/离线恢复用 |
| POST /media | 上传二进制（multipart 或 raw body），返回 {mediaId}；单文件 ≤50MB |
| GET /media/{id} | 下载媒体；仅限本账号；过期 404 |
| WS /ws?token= | 长连接；心跳 ping/pong 30s；消息推送通道 |

### 消息格式

JSON 信封：

```json
{ "type": "clip_text|clip_image|clip_file|ack|ping", "originDeviceId": "...", "seq": 1, "ts": 1754700000000, "payload": { ... } }
```

- `clip_text`：payload = `{text}`；`clip_image` / `clip_file`：payload = `{mediaId, name, size}`。`seq` 为发送端单调递增序号，接收端按序去重。
- 服务端为同账号在线设备转发（除 originDeviceId 外）；离线设备不缓存消息——离线恢复一律走 `GET /history`（服务端 SQLite 存最近 7 天）。

### 双向数据流

- **手机 → 电脑**：手机复制 → 打开 App（获焦）自动同步当前剪贴板 → 本地入库 → 上传（文本直发 / 图片文件先 POST /media）→ 服务端推送 → 电脑 SyncService 收到 → **写入历史库（source=phone）**，用户在 Win+V 悬浮列表中选择粘贴。电脑端不自动写系统剪贴板。
- **电脑 → 手机**：电脑复制 → 现有剪贴板监听 → 同步上传 → 服务端推送 → 手机 **写入系统剪贴板（后台允许写）+ 本地入库（source=pc）**，手机任意 App 直接粘贴。
- **方向语义（用户 2026-08-09 修正）**：**手机端只上传文字**（图片/文件仅在手机本地留历史，不同步）；电脑→手机方向传输文本+图片+文件。手机端接收图片/文件经 FileProvider 暴露（剪贴板 content:// Uri），可在任意 App 粘贴。

### 防回环与去重

- 手机端写入剪贴板时记录 `lastPushedWriteTs`；无障碍回调若剪贴板内容与最近一次同步收到的内容一致（内容哈希相同）则跳过上传。
- 双端统一去重：同一内容哈希在 60s 内不重复上传。

### 断线与离线

- 心跳 30s，超时判定断线；指数退避重连（1s 起，上限 60s，手机端在 App 被杀前保持）。
- 上线后先 `GET /history?since=<本地最新 ts>` 补齐缺口，再收实时推送。
- 服务端后台任务每日清理 >7 天的 messages 与 media。

### 错误处理

- 上传失败重试 3 次（指数退避）；HTTP 5xx 时退避重试。
- 单文件 >50MB 拒绝并提示（不进入同步）。
- 服务端 401（token 失效）→ 双端引导重新登录。
- WS 连接失败：域名失败自动回退 IP 直连（`wss://107.175.228.83:8081`，跳过证书主机名校验），与现有 Updater 双镜像策略一致。

## [S5] Android 端设计（新建项目）

- **技术栈**：Kotlin + Jetpack Compose + Material 3；minSdk 26，targetSdk 34+。
- **模块**：
  - `MainActivity`：**获焦即同步**（`onWindowFocusChanged(hasFocus=true)` 调 `SyncService.onLocalClip`——Android 10+ 剪贴板访问仅限获焦 App/默认 IME，小米后台回调不派发，此为实现上限；store 哈希去重兜底防重复）；历史列表（时间分组、来源标签、类型筛选）、点击条目写回剪贴板；登录页（注册/登录）；设置页（账号/设备管理、同步机制说明）。
- **测试**：SyncClient 协议解析单测；真机验证为主。

## [S6] Windows 端设计（改造现有项目）

- **新增** `Services/SyncClient.cs`：`ClientWebSocket` 客户端，域名优先 + IP:8081 直连兜底（跳过证书主机名校验），心跳/重连/上传下载。
- **新增** `Services/SyncService.cs`：接入现有剪贴板监听（复制事件 → 同步上传）；收到远端消息 → 调 `ClipboardStore` 入库（source=phone）；文本直接入库，图片存 `data/images/*.png`、文件存 `data/files/`（复用现有条目模型与预览逻辑）。
- **DB 迁移**：`clipboard_items` 加 `source` 列（'local'/'phone'，默认 'local'）；现有库自动迁移。
- **UI**：主窗口与 overlay 列表条目显示来源标签；筛选（全部/本机/手机）；设置窗口加"账号登录"区（注册/登录/设备管理）。
- **实验性开关（用户 2026-08-09 补充）**：设置页末尾新增"实验性功能：多端同步"开关（默认关闭）；勾选后显示账号登录区与设备管理、启动同步模块（SyncClient/SyncService）；取消勾选即停用同步（本地历史不受影响）。
- **注意**：不改动现有 Win32 粘贴实现（`Services/Paster.cs`）、热键、悬浮窗逻辑；同步仅入历史，不自动写系统剪贴板。
- **测试**：沿用现有模拟验证方式（无测试项目）；对测试服务器注入模拟消息验证入库与标签。

## [S7] 服务器端设计（新建）

- **技术栈**：Go 单二进制；HTTP + WebSocket 同端口；SQLite（纯 Go 驱动如 modernc.org/sqlite，免 CGO 便于交叉编译）。
- **表**：`users(id, username unique, password_hash, created_at)`、`devices(id, user_id, name, token_hash, last_seen)`、`messages(id, user_id, origin_device_id, type, payload, created_at)`、`media(id, user_id, data, created_at)`。
- **行为**：token 认证中间件；WS 连接注册到 `map[user_id][]conn`，转发按账号隔离；每日清理 >7 天数据；设备心跳更新 last_seen。
- **部署**：VPS 上 systemd（或 nohup）常驻监听 `127.0.0.1:8082`（内网明文）；nginx 两个 TLS 入口（`sync.starry0214.one:443` + `107.175.228.83:8081`，同一证书）反代到 127.0.0.1:8082；防火墙放行 8081。
- **测试**：Go 单测（认证、账号隔离、注册/登录/解绑、清理）；本地双 WS 客户端联调脚本（文本/图片互通）。

## [S8] 实施里程碑

| 里程碑 | 内容 | 验收标准 |
|---|---|---|
| M1 | 服务器：账号/设备/消息/media API、WS 推送、SQLite、清理任务、Go 单测 | 本地双客户端脚本互通文本/图片；单测通过 |
| M2 | Windows 端：SyncClient/SyncService、DB source 迁移、来源标签与筛选 UI、设置页登录 | 连测试服务器，注入消息入历史且标签正确 |
| M3 | Android 端：脚手架、获焦即同步、SyncClient、SQLite、历史 UI、登录/设备管理 | 真机跑通登录 + 打开 App 同步当前剪贴板 + 历史 + 回填 |
| M4 | 部署与联调：VPS 部署 + nginx、双端真实联调、防回环/离线恢复验证、MIUI 保活验证 | 手机↔电脑文本/图片/文件全链路通过；政务网域名失败时 IP 兜底可用 |
| M5 | 发布：Android APK；Windows 版本提升（双 csproj 一致）+ 内嵌校验 + 更新服务器上传 | 双端正式版本可更新、可同步 |

## [S9] 风险与已知限制

- 小米 HyperOS 后台无法监听剪贴板（回调不派发 + 读取返回 null，Android 官方限制，无障碍服务不豁免）——手机端同步依赖"打开 App"，后台复制需打开 App 补同步（2026-08-09 实证，已知限制）。
- 政务网到境外节点时通时断是架构固有特性，IP 直连兜底可缓解但不保证稳定。
- 服务器短期存储 7 天：新设备接入只能拉回 7 天内内容，更早内容无法找回。
- 首次实现不含端到端加密（TLS 传输 + token 认证）；若后续需要防服务器窥探，可加应用层加密。
