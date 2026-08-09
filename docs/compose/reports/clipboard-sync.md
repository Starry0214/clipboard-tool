---
feature: clipboard-sync
status: delivered (M1-M4 完成；M5 发布待做)
specs:
  - docs/compose/spec/2026-08-09-clipboard-sync-design.md
plans:
  - docs/compose/plans/2026-08-09-sync-server.md
  - docs/compose/plans/2026-08-09-windows-sync.md
  - docs/compose/plans/2026-08-09-android-app.md
branch: main
commits: 2420ebb..cca566a
---

# 剪贴板跨设备同步 — 最终报告（M1-M4 完成）

## What Was Built

剪贴板跨设备同步三端全部交付：**SyncServer**（Go 单二进制，账号/设备/消息/媒体 API + WebSocket 实时转发 + 7 天短期存储）、**Windows 端改造**（来源标签/筛选、设置页实验性开关与账号登录、双向同步）、**Android App**（Kotlin+Compose，无障碍剪贴板监听、登录、双向同步、历史列表）。

**同步语义**（用户确认）：手机端只上传**文字**；电脑→手机方向传输文本+图片+文件（手机收到自动写入系统剪贴板，可在任意 App 粘贴）。Windows 端只入历史不写系统剪贴板（用户从 Win+V 历史选择粘贴）。**Windows 端同步默认关闭**（设置页"实验性功能：多端同步"开关），Android 端登录即同步。

## Architecture

### 生产部署（VPS 107.175.228.83，2026-08-09）

| 组件 | 配置 |
|------|------|
| syncserver 服务 | `/opt/syncserver/syncserver`（linux-amd64 交叉编译），systemd `syncserver.service`：`-addr 127.0.0.1:8082 -db /opt/syncserver/sync.db`，Restart=always |
| 域名入口 | `https://code.starry0214.one/sync/`——复用现有 443+证书，`location /sync/` 剥离前缀反代 127.0.0.1:8082（WS upgrade、client_max_body_size 60m、proxy_read_timeout 300s）；conf：`extension/code.starry0214.one/sync-location.conf` |
| IP 兜底入口 | `https://107.175.228.83:8081`——nginx `sync-8081.conf`（listen 8081 ssl 复用 code 证书，反代 127.0.0.1:8082）；`ufw allow 8081/tcp` |
| 客户端 mirrors | `["https://code.starry0214.one/sync", "https://107.175.228.83:8081"]`（IP 直连跳过证书主机名校验） |

### SyncServer（`SyncServer/`，Go 1.26）

`NewApp(store)` 路由注册；store.go（SQLite 四表，账号隔离）；auth.go（注册/登录、设备 token、requireAuth、设备管理）；media.go（50MB 限制、跨账号 404）；history.go（since 过滤，空历史返回 `[]`）；ws.go（Hub + gorilla/websocket，30s ping/60s pong，落库后广播排除来源设备）；cleanup.go（7 天清理）；`cmd/phone-sim/` 联调工具。

### Windows 端（`ClipboardTool/`，C# WPF .NET 9）

`SyncClient.cs`（OkHttp 同款 ClientWebSocket，`ConfigureAwait(false)`、双镜像回退、退避重连）；`SyncService.cs`（EntryCaptured 上传、远端入库 source=phone、SyncLastSeq 持久化去重、图片内容哈希去重）；`ClipboardStore/Entry`（source 列自动迁移、Query 三参数筛选）；设置页实验性开关+账号区；主窗口/overlay"手机"橙色来源标签+主窗口来源筛选；`--data-dir` 测试参数。

### Android 端（`Android/`，Kotlin + Compose）

`AppState`（SharedPreferences 持久化，已登录自动启动同步）；`LocalStore`（原生 SQLite，哈希去重与 Windows 对齐）；`SyncClient`（OkHttp WS/HTTP，TrustManager 跳过证书校验、双镜像）；`SyncService`（文字上传、远端三类内容接收+写剪贴板、seq 去重、防回环 suppressHash）；`ClipboardListener`（无障碍服务，OnPrimaryClipChangedListener，无常驻通知）；`ClipboardEvents`（读剪贴板文本/图片/文件、FileProvider 写剪贴板）；登录页（含服务器地址输入）/历史页（时间分组/来源标签/类型筛选/点击回填/长按删除）/设置页（账号/服务器/无障碍引导）；`DebugClipReceiver` 调试广播钩子（adb 注入/读取剪贴板，联调用）。

## Verification

- **生产端到端**（phone-sim 经域名 443 直连生产）：注册 m4test、双设备登录、WS 实时发消息、历史拉取（seq=1/2）全部通过——nginx 反代 + WS upgrade 正常。
- **本机公网验证**：域名 `https://code.starry0214.one/sync/api/health` 200（0.65s，经境外中继）；IP `https://107.175.228.83:8081/api/health` 200（0.65s，直连）——期间修复 **ufw 未放行 8081** 导致的公网超时（VPS 本机 curl 走 loopback 不暴露，必须公网验证）。
- M1-M3 验证同前（服务器单测/冒烟、Windows 本地联调、Android 真机全自动联调）。

## Journey Log

> Brief notes on what informed the final design. Not required reading.

- [pivot] **部署复用 code.starry0214.one 域名**（用户提议，免新域名/新证书/DNS）：`location /sync/` 路径前缀反代 + `listen 8081 ssl` 复用同一证书作 IP 兜底。
- [lesson] **ufw 未放行的端口公网必超时**：VPS 本机 curl 不受防火墙影响，部署后必须从外部（本机公网）验证双入口。
- [lesson] **PowerShell→ssh heredoc 双层转义易错**：nginx conf 改用本地写文件 + scp 上传。
- [lesson] 中文路径 worktree、明文 HTTP、Main 线程网络等教训见下。

## Source Materials

| File | Role | Notes |
|------|------|-------|
| `docs/compose/spec/2026-08-09-clipboard-sync-design.md` | 设计 spec | S1-S9；S4 已按用户修正同步语义 |
| `docs/compose/plans/2026-08-09-sync-server.md` | M1 计划 | 完成 |
| `docs/compose/plans/2026-08-09-windows-sync.md` | M2 计划 | 完成 |
| `docs/compose/plans/2026-08-09-android-app.md` | M3 计划 | 完成 |
