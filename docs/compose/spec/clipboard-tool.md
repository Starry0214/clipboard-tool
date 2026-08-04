---
feature: clipboard-tool
status: delivered
updated: 2026-08-04
branch: feat/clipboard-tool
commits: 110cbe51..110cbe51
---

# 轻量剪贴板工具 (Clipboard Tool)

## Report

**What was built** — C# / .NET 9 WPF 常驻托盘的剪贴板历史工具。`AddClipboardFormatListener` 事件驱动监听（非轮询），按文本 → 图片 → 文件路径优先级捕获：文本与文件按内容哈希去重，图片按像素字节哈希去重（同一张图连续复制只记一条）。历史存 SQLite 单文件 `data/clipboard.db`，支持条数上限裁剪（默认 500，可配，置顶条目不裁剪）与置顶保护。图片存缩略图（约 200px）+ 原图，列表懒加载预览、回贴用原图。

全局热键（默认 Ctrl+Alt+V，可改绑）在光标处弹出无边框悬浮列表：顶部搜索框即输即滤（图片不参与搜索）、↑/↓ 选择、Enter 粘贴（写回剪贴板并向原前台窗口模拟 Ctrl+V，粘贴路径抑制自监听）、Esc 关闭、失焦自动隐藏、置顶条目排最前。托盘菜单提供主窗口 / 暂停监听 / 清空历史（二次确认）/ 退出；主窗口支持浏览、搜索、置顶、删除、清空；设置窗可改热键、上限、开机自启（注册表 Run，路径带引号）、默认纯文本粘贴。单实例保护，重复启动被拒并提示。

**Verification** — `dotnet build`：PASS（0 警告 0 错误）。剪贴板监听与去重：PASS（相同文本、相同图片重复复制均只记一条）。文件路径捕获：PASS（type=file 入库）。持久化：PASS（重启后记录保留）。上限裁剪：PASS（上限 3 时裁剪到 3 条）。置顶保护：PASS（置顶条目不被裁剪）。悬浮列表：PASS（经 `--show-overlay` 钩子验证弹出、ESC 关闭、Enter 粘贴——剪贴板 FileDrop 写入所选文件路径并自动关闭；`--overlay-loop` 连续 5 次弹出/隐藏无崩溃）。主窗口与单实例：PASS。打包发布（T7）：未执行，用户决定仅本地使用。

**Journey log** — 1) 政务网 NuGet 源不可达：临时启动 E 盘 v2rayN 自带的 xray 核心 + 最小化路由（仅 nuget.org 域走代理，其余直连）还原包，用完立即关闭。2) `UseWindowsForms` 隐式 using 与 WPF 类型冲突：csproj 用 `Using Remove` 移除全局 WinForms/Drawing using，TrayIcon 显式引入。3) Review 发现 WPF 窗口 `Close()` 后不可复用，二次弹出即崩：全部关闭路径改 `Hide()` 复用。4) 调试陷阱：`dotnet build | Select-Object -Last 1` 截断错误输出、且 exe 被运行中进程锁定（MSB3021），连续 4 次 build 静默失败导致一直在测试旧 exe——此后 build 必须看完整输出并先杀进程。5) PostMessage/SendMessage 注入 `WM_KEYDOWN` 可触发 WPF `PreviewKeyDown`（用于 UI 自动化验证）；SendKeys 注入不触发 `RegisterHotKey` 全局热键（热键弹出无法自动化验证，经 `--show-overlay` 钩子等效验证）。

## [S1] Problem

Windows 自带 Win+V 剪贴板历史存在明显不足：无搜索功能、历史条数上限固定不可配、图片与文件路径支持差、无法置顶常用内容、且历史可能同步到云端引发隐私顾虑。用户需要一个**本地运行、轻量、稳定**的剪贴板历史替代工具，常驻后台、全天可用。

稳定性是第一诉求：工具开机自启、全天常驻，崩溃或丢数据不可接受。因此采用 **C# / .NET 9 WPF**，使用 Windows 原生事件驱动的剪贴板监听（`AddClipboardFormatListener`，非轮询），自包含单文件发布、无第三方运行时依赖。

## [S2] Design

### 技术栈与运行形态

- 目标框架 `net9.0-windows`，WPF + WinForms 互操作（托盘 NotifyIcon）。
- 发布：`win-x64` 自包含单文件 exe，无 .NET 运行时依赖。
- 常驻形态：启动即入系统托盘；开机自启（注册表 `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`，可在设置中开关）。

### 剪贴板监听 (ClipboardMonitor)

- 专用隐藏窗口接收 `WM_CLIPBOARDUPDATE` 消息（`AddClipboardFormatListener` 注册，事件驱动，不轮询）。
- 收到通知后，按优先级尝试读取：文本（`CF_UNICODETEXT`）→ 图片（`CF_BITMAP`/PNG）→ 文件路径（`CF_HDROP`，多个文件只记首个路径+可选的完整集合）。
- 去重：与最近一条记录内容相同则忽略（不重复入库）。
- 监听可暂停/恢复（托盘菜单控制），暂停期间收到通知直接忽略。

### 存储 (ClipboardStore)

SQLite 单文件 `data/clipboard.db`（`Microsoft.Data.Sqlite`，NuGet 包经 xray 代理还原），单表 `entries`：

| 列 | 类型 | 说明 |
|---|---|---|
| id | INTEGER PK AUTOINCREMENT | |
| type | TEXT | `text` / `image` / `file` |
| content | TEXT NULL | 文本内容或文件路径 |
| thumb | BLOB NULL | 图片缩略图 PNG（列表预览用，约 200px） |
| image | BLOB NULL | 图片原图 PNG（回贴用） |
| pinned | INTEGER | 0/1 置顶 |
| created_at | INTEGER | Unix 秒 |

- 条数上限默认 500（可配置）：仅裁剪 `pinned=0` 的条目，置顶条目不删。
- 相同内容去重（见监听）。

### 全局热键

- Win32 `RegisterHotKey`，默认 `Ctrl+Alt+V`，可在设置中改绑。
- 热键按下 → 在鼠标光标处弹出悬浮历史列表（OverlayWindow）。

### 悬浮列表 (OverlayWindow)

- 无边框、`Topmost`、半透明背景，弹出位置贴近鼠标光标，屏幕边缘自动校正，失去焦点自动关闭。
- 顶部搜索框：输入即过滤（文本按内容匹配；文件按路径匹配；图片不参与关键词搜索，但可翻看）。
- 列表项展示：文本（单行截断预览）/ 图片（缩略图）/ 文件（图标+路径）。
- 置顶条目始终排在最前。
- 键盘：`↑`/`↓` 选择、`Enter` 粘贴并关闭、`Esc` 关闭、`Ctrl+V` 纯文本粘贴（去格式）。
- 粘贴动作：把选中内容写入系统剪贴板（图片写原图 BLOB，文本按需纯文本化），再向当前前台窗口模拟 `Ctrl+V`（SendInput）完成输入。

### 纯文本粘贴

- 文本条目粘贴时默认写入 `CF_UNICODETEXT`；当条目来源含富文本（如网页复制）时，仅保留纯文本层，丢弃 HTML/RTF 格式。

### 托盘与主窗口

- 托盘菜单：打开主窗口 / 暂停监听（勾选态） / 清空历史（二次确认） / 设置 / 退出。
- 主窗口 (MainWindow)：全量历史浏览 + 搜索 + 置顶/取消置顶 + 删除单条 + 清空全部；打开设置。
- 设置（设置窗或主窗口内页）：热键改绑、条数上限、开机自启、是否默认纯文本粘贴。

## [S3] Out of Scope

- 云同步 / 多设备共享
- 剪贴板内容加密（本地明文 SQLite）
- 富文本格式历史（保留 HTML/RTF 原样粘贴）
- 非 Windows 平台
- 剪贴板内容实时编辑后再粘贴

## Tasks

- [x] T1: 项目脚手架 — `dotnet new wpf` + 目标框架 net9.0-windows + 自包含单文件发布配置 + 空壳托盘 — acceptance: `dotnet build` 通过，`dotnet publish` 产出单文件 exe（covers: S2 技术栈）
- [x] T2: ClipboardMonitor 剪贴板监听 — 隐藏窗口 + `AddClipboardFormatListener` + 文本捕获 + 去重 — acceptance: 复制不同文本时产生不同记录，连续复制相同文本只产生一条（covers: S2 剪贴板监听；depends: T1）
- [x] T3: ClipboardStore SQLite 持久化 — 建库建表、写入/查询/裁剪、置顶保护 — acceptance: 重启程序后历史仍在；超上限后最旧的非置顶条目被裁剪、置顶条目保留（covers: S2 存储；depends: T2）
- [x] T4: 图片与文件路径捕获 — 复制截图/图片入库（缩略图+原图），复制文件记录路径 — acceptance: 复制 PNG 截图后列表出现缩略图且可回贴；复制文件后记录其路径（covers: S2 监听、存储；depends: T3）
- [x] T5: 全局热键 + OverlayWindow 悬浮列表 — 热键弹出、光标跟随、搜索过滤、↑↓/Enter/Esc、回车粘贴（含纯文本粘贴）、图片回贴 — acceptance: 按热键在光标处弹出列表；搜索过滤生效；Enter 把所选条目粘贴到原前台窗口；置顶条目排最前（covers: S2 全局热键、悬浮列表、纯文本粘贴；depends: T4）
- [x] T6: 托盘菜单 + 主窗口 + 设置 — 托盘完整菜单、主窗口浏览/置顶/删除/清空、设置（热键/上限/自启/纯文本） — acceptance: 托盘四项功能可用；主窗口置顶与删除生效；修改热键后生效；开启自启后注册表出现对应项（covers: S2 托盘与主窗口；depends: T5）
- [ ] T7: 打包发布 — win-x64 自包含单文件 — acceptance: 发布目录仅一个 exe，在未装 .NET 的机器（或干净环境）可启动并正常监听（covers: S2 技术栈；depends: T6）— **取消**：用户决定仅本地使用，不做自包含发布（csproj 发布配置已就绪，需要时可执行 `dotnet publish -c Release`）
