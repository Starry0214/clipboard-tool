---
feature: 更新历史网页 + 两端设置按钮
status: delivered
specs: []
plans:
  - docs/compose/plans/2026-08-09-changelog-web-button.md
branch: main / android-dev
commits: 6f32b2b..e6c15c9 (win), 92fbd1f (android)
---

# 更新历史网页 + 两端设置界面按钮 — Final Report

## What Was Built

Windows 与 Android 设置界面各新增「查看历史更新记录」按钮，点击用系统默认浏览器打开统一更新历史网页（`https://code.starry0214.one/updates/changelog.html`）。网页为单页双 Tab（Windows 电脑 / Android 手机），JS 动态加载服务器上现有的 `changelog.txt` / `changelog-android.txt`，按 `vX.Y.Z` 分块渲染为品牌蓝风格版本时间线卡片（含【实验性功能】分组标题）。**发版只需更新 txt，网页自动同步，零额外维护。**

## Architecture

- **网页 `web/changelog.html`**（部署到 `/var/www/updates/changelog.html`，nginx 静态服务）：单文件内联 CSS+JS，零依赖。`parseChangelog(text)` 按行解析 `vX.Y.Z` 块 + `- ` 条目 + `【...】` 分组标题；URL hash `#windows`/`#android` 决定默认 Tab（`hashchange` 事件同步切换）；`fetch()` 同源加载两个 txt（`cache: no-store`），失败显示"无法加载更新记录"；`prefers-color-scheme: dark` 深色适配；移动端自适应。
- **Windows `SettingsWindow.xaml(.cs)`**：新增「关于」区块（CardBorder 风格）——`AboutVersionText` 显示 `Updater.CurrentVersion` + 「查看历史更新记录」按钮，`OnOpenChangelog` 用 `Process.Start(new ProcessStartInfo(url) { UseShellExecute = true })` 打开 `...changelog.html#windows`（与 OnDownloadApp 同模式）。
- **Android `SettingsScreen.kt`**：「关于」SectionCard 中「检查更新」旁新增 `OutlinedButton`，`Intent(Intent.ACTION_VIEW)` 打开 `...changelog.html#android`。

### Design Decisions

- **JS 动态加载 txt 而非静态 HTML 手维护**：服务器只有文本 changelog，做 HTML 页面若手维护会在每次发版时漏更；JS fetch 渲染让 txt 成为唯一事实源。
- **单页双 Tab 而非两个独立页面**：用户选择；`#windows`/`#android` hash 让两端按钮直达各自平台 Tab。
- **浏览器跳转只用域名**：IP 兜底镜像（`http://107.175.228.83:8080`）仅用于程序内下载回退；浏览器跳转固定 HTTPS 域名。

## Usage

- 网页：任意浏览器打开 `https://code.starry0214.one/updates/changelog.html`（或 `#android`），顶部 Tab 切换平台，深色模式跟随系统。
- Windows：设置 → 关于 → 查看历史更新记录。
- Android：设置 → 关于 → 查看历史更新记录。

## Verification

- **网页本地渲染**：拉服务器 txt 到本地 + `python http.server` + playwright-cli headless——Windows Tab 渲染 v1.4.3→v1.3.7 全部卡片、【实验性功能】分组标题存在；`#android` 切 Tab 渲染 v1.0.3→v1.0.0。
- **部署**：scp 到 `/var/www/updates/`，服务器 curl 三个文件均 200。
- **线上渲染**：playwright 打开域名页，内容区完整渲染（fetch 因境外节点较慢但成功）；`#android` 自动激活 Android Tab。
- **Windows 端到端**：UIAutomation 点击「设置」→「查看历史更新记录」→ Edge Dev 打开"更新历史 - 剪贴板助手"页（部署前为 404，恰好证明 URL 路径正确）。
- **Android 端到端**：真机 aff0300b 安装 debug 包，设置页显示新按钮，点击后 `topResumedActivity` 切到 Edge 浏览器。
- 两端编译 0 错误 0 警告（dotnet build / gradle assembleDebug --offline --no-daemon）。

## Journey Log

> Brief notes on what informed the final design. Not required reading.

- [lesson] **playwright-cli 命令禁止接 `|` 管道**：浏览器会话 node 进程持有 stdout 管道句柄，`| Select-String` 等 EOF 永不关闭、输出完整后假挂起——与 gradle daemon 完全同构（无管道秒回、带管道必挂）。已固化 AGENTS.md 防卡死规则⑦ + MEMORY.md Rules，每次操作后 `playwright-cli close-all` 清理会话。
- [lesson] 网页验证须走 http 服务而非 file://（fetch 被 CORS 拦截）；渲染验证用 playwright headless 比 webfetch 可靠（webfetch 不执行 JS）。
- [lesson] Windows 设置窗口（ShowInTaskbar=False）无法用 `Get-Process.MainWindowTitle` 定位，须 EnumWindows 按标题取句柄再 FromHandle。

## Source Materials

| File | Role | Notes |
|------|------|-------|
| `web/changelog.html` | 网页主体 | 部署到服务器 /var/www/updates/ |
| `ClipboardTool/SettingsWindow.xaml(.cs)` | Windows 关于区块 | Updater.CurrentVersion + 打开网页 |
| `Android/.../ui/SettingsScreen.kt` | Android 关于卡片按钮 | OutlinedButton + ACTION_VIEW |
| `docs/compose/plans/2026-08-09-changelog-web-button.md` | 实施计划 | 完整执行 |
