---
feature: clipboard-sync
status: delivered (M1 服务器端 + M2 Windows 端 + M3 Android 端；M4 部署/M5 发布待做)
specs:
  - docs/compose/spec/2026-08-09-clipboard-sync-design.md
plans:
  - docs/compose/plans/2026-08-09-sync-server.md
  - docs/compose/plans/2026-08-09-windows-sync.md
  - docs/compose/plans/2026-08-09-android-app.md
branch: main
commits: 2420ebb..e52b5b9
---

# 剪贴板跨设备同步 — 最终报告（M1+M2+M3 完成）

## What Was Built

剪贴板跨设备同步三端全部交付：**SyncServer**（Go 单二进制，账号/设备/消息/媒体 API + WebSocket 实时转发 + 7 天短期存储）、**Windows 端改造**（来源标签/筛选、设置页实验性开关与账号登录、双向同步）、**Android App**（Kotlin+Compose，无障碍剪贴板监听、登录、双向同步、历史列表）。

**同步语义**（用户确认）：手机端只上传**文字**；电脑→手机方向传输文本+图片+文件（手机收到自动写入系统剪贴板，可在任意 App 粘贴）。Windows 端只入历史不写系统剪贴板（用户从 Win+V 历史选择粘贴）。**Windows 端同步默认关闭**（设置页"实验性功能：多端同步"开关），Android 端登录即同步。

## Architecture

### SyncServer（`SyncServer/`，Go 1.26）

`NewApp(store)` 路由注册；store.go（SQLite 四表，账号隔离）；auth.go（注册/登录、设备 token、requireAuth、设备管理）；media.go（50MB 限制、跨账号 404）；history.go（since 过滤，空历史返回 `[]`）；ws.go（Hub + gorilla/websocket，30s ping/60s pong，落库后广播排除来源设备）；cleanup.go（7 天清理）；`cmd/phone-sim/` 联调工具。

### Windows 端（`ClipboardTool/`，C# WPF .NET 9）

`SyncClient.cs`（OkHttp 同款 ClientWebSocket，`ConfigureAwait(false)`、双镜像回退、退避重连）；`SyncService.cs`（EntryCaptured 上传、远端入库 source=phone、SyncLastSeq 持久化去重、图片内容哈希去重）；`ClipboardStore/Entry`（source 列自动迁移、Query 三参数筛选）；设置页实验性开关+账号区；主窗口/overlay"手机"橙色来源标签+主窗口来源筛选；`--data-dir` 测试参数。

### Android 端（`Android/`，Kotlin + Compose）

`AppState`（SharedPreferences 持久化，已登录自动启动同步）；`LocalStore`（原生 SQLite，哈希去重与 Windows 对齐）；`SyncClient`（OkHttp WS/HTTP，TrustManager 跳过证书校验、双镜像）；`SyncService`（文字上传、远端三类内容接收+写剪贴板、seq 去重、防回环 suppressHash）；`ClipboardListener`（无障碍服务，OnPrimaryClipChangedListener，无常驻通知）；`ClipboardEvents`（读剪贴板文本/图片/文件、FileProvider 写剪贴板）；登录页（含服务器地址输入）/历史页（时间分组/来源标签/类型筛选/点击回填/长按删除）/设置页（账号/服务器/无障碍引导）；`DebugClipReceiver` 调试广播钩子（adb 注入/读取剪贴板，联调用）。

## Verification

- **SyncServer**：go test 15 测试全绿 + 端到端冒烟（文本/图片/历史/隔离）。
- **Windows 端**：构建 0 错 0 警；本地联调全链路（M2）：双向三类内容、回放去重、防回环、空历史边界；修复 WS URL 缺协议头、UI 线程死锁、时间单位、seq 去重 4 个 bug。
- **Android 端**：协议解析单测 3/3（worktree ASCII 路径根治中文路径问题）；**真机（小米 14 Pro）全自动联调**（adb 注入剪贴板/读剪贴板/uiautomator 断言）：
  - 手机复制 → 服务器收到（seq 递增、origin 正确）
  - 电脑（phone-sim）发文本/图片 → 手机实时接收 → 写系统剪贴板（DEBUG_GET_CLIP 读出"来自电脑的实时消息-777"）→ 历史列表"电脑"标签+缩略图
  - 回放：重启后拉历史只入库不写剪贴板；seq 去重无重复
  - 防回环：App 自写剪贴板内容不再上传（suppressHash + 哈希去重双保险）
  - 修复：明文 HTTP 拦截（usesCleartextTraffic）、NetworkOnMainThreadException（login/register 切 IO 线程）、登录态不响应式（remember state）、重装后自动登录不启动同步（AppState.onCreate 启动）

## Journey Log

> Brief notes on what informed the final design. Not required reading.

- [lesson] **中文路径是 Android 构建的深坑**：AGP `overridePathCheck` 只绕过静态检查，Gradle 测试 worker 仍失败——worktree 到 ASCII 路径根治（`git worktree add -b android-dev C:\Android\clipboard-tool`）。
- [lesson] **Android 9+ 明文 HTTP 拦截**：联调服务器 http://127.0.0.1 必须 `usesCleartextTraffic="true"`；生产双镜像用 wss 不受影响。
- [lesson] **rememberCoroutineScope 默认 Main dispatcher**：suspend 网络调用必须显式 `withContext(Dispatchers.IO)`，否则 NetworkOnMainThreadException 且被 runCatching 静默吞掉——网络层错误必须打日志。
- [lesson] **重装后无障碍服务绑定有时序**：install -r 后立即触发剪贴板会丢事件，需等服务绑定（日志"accessibility service connected"确认）。
- [lesson] **adb 传中文参数经 shell 可能损坏**：调试钩子先验证 ASCII，避免误判。
- [lesson] **小米 ROM 无 `cmd clipboard` 命令**：剪贴板注入用 manifest 广播接收器（DEBUG_SET_CLIP）实现全自动测试。
- [lesson] **剪贴板图片跨 App 粘贴必须 FileProvider**：file:// Uri 其他 App 读不到。

## Source Materials

| File | Role | Notes |
|------|------|-------|
| `docs/compose/spec/2026-08-09-clipboard-sync-design.md` | 设计 spec | S1-S9；S4 已按用户修正同步语义 |
| `docs/compose/plans/2026-08-09-sync-server.md` | M1 计划 | 完成 |
| `docs/compose/plans/2026-08-09-windows-sync.md` | M2 计划 | 完成 |
| `docs/compose/plans/2026-08-09-android-app.md` | M3 计划 | 完成 |
