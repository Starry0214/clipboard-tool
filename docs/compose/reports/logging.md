---
feature: 日志与一键上报
status: delivered
specs:
  - docs/compose/spec/2026-08-05-logging-design.md
plans:
  - docs/compose/plans/2026-08-05-logging.md
branch: main
commits: 2d86f22..ceb4741
---

# 日志与一键上报 — 最终报告

## What Was Built

剪贴板助手新增了完整的日志与反馈链路：程序在 `%LocalAppData%\ClipboardTool\logs\clipboard.log` 记录关键事件、运行错误、未处理异常和剪贴板捕获明细（仅元信息，不含内容），单文件 1MB 大小轮转共保留 5 个文件。遇到未处理异常时弹出自定义错误窗口，用户可一键把合并后的日志（附程序版本/系统/启动时间元信息）POST 到自建服务器 `https://code.starry0214.one/api/logs/upload`；托盘菜单也新增"反馈问题"入口支持手动上报。

## Architecture

**客户端**（`ClipboardTool/Services/Log.cs`）：

- 静态类，`Log.Init(dataDir)` 在 App 启动早期调用，`Log.Info/Error` 线程安全（`lock`）写入
- 轮转：写前检查文件超 1MB 则滚动 `clipboard.log → .1.log → … → .4.log`，最旧删除
- `Log.UploadAsync()`：合并全部日志文件，头部附元信息块（版本/系统/启动时间/数据目录），POST 到服务器，30 秒超时，成功返回 bool
- 写失败静默忽略，不影响主流程

**异常捕获**（`App.xaml.cs`）：

- 三个钩子：`DispatcherUnhandledException`（UI 线程，记日志 + `e.Handled=true` + 弹窗）、`AppDomain.UnhandledException`（仅记日志）、`TaskScheduler.UnobservedTaskException`（记日志 + `SetObserved`）
- 测试钩子 `--throw`：启动后抛 UI 线程未处理异常，用于验证错误窗与日志

**错误窗口**（`ClipboardTool/ErrorWindow.xaml(.cs)`）：

- `ShowError(title, message)`（未处理异常）与 `ShowFeedback()`（托盘"反馈问题"入口，无错误上下文）
- "上报日志"按钮点击后禁用并显示"正在上报…"，完成后 MessageBox 提示成功/失败

**埋点位置**：

- 关键事件：启动/退出（版本+数据目录）、数据迁移成功/失败、热键注册失败、设置变更（MaxEntries/UseWinV/Hotkey）、更新检查结果与下载耗时
- 运行错误：剪贴板捕获失败（`ClipboardMonitor`）、粘贴失败（`Paster`）
- 捕获明细：文本字符数、图片尺寸+PNG 大小、文件路径——**不含剪贴板内容本身**（隐私决策）

**服务器端**（`/root/code-gen-service/src/routes/logs.ts`）：

- Elysia 路由 `POST /api/logs/upload`：body 限制 5MB（超限 413），落盘 `/root/code-gen-service/logs/<yyyyMMddHHmmss>-<随机>.log`
- nginx `code.starry0214.one` 站点 `client_max_body_size 10m`
- 服务已从裸进程升级为 **systemd 服务**（`code-gen.service`，重启自动恢复）——这是实施中发现原 nohup 进程在 SSH 会话结束后被杀后做的调整

### Design Decisions

- **记录剪贴板元信息而非内容**：避免密码/身份证等敏感文本落盘和上报，用户明确确认的取舍
- **自研零依赖 Log 而非 NLog/Serilog**：轻量工具不引入依赖；日志功能需求简单（写文件+轮转+上报）
- **大小轮转而非按天**：捕获明细日志量大，1MB×5 保证磁盘占用有界
- **上报走 HTTPS 域名而非 IP**：复用已配置的 `code.starry0214.one` 站点（DNS 直指 107 服务器，带证书），顺带解决了更新地址域名化

## Usage

- **日志文件**：`%LocalAppData%\ClipboardTool\logs\clipboard.log`（.1~.4 为轮转文件）
- **上报**：程序崩溃弹"出错了"窗口 → 点"上报日志"；或托盘右键 → "反馈问题"
- **测试**：`ClipboardTool.exe --throw` 模拟未处理异常；服务器日志在 `/root/code-gen-service/logs/`
- **服务管理**：`systemctl status code-gen.service` / `systemctl restart code-gen.service`

## Verification

- 构建 0 错误（每个任务后验证）
- 启动后 `clipboard.log` 含 `[INFO] 程序启动 v1.2.2，数据目录 …`
- `--throw` 启动后：日志含 `[ERROR] UI 线程未处理异常` + 完整堆栈，屏幕弹出"出错了"窗口
- 复制文本后日志含 `[INFO] 捕获文本 N 字符`；隐私检查通过（日志中无文本内容）
- 端到端上报：点击"上报日志"→ 服务器收到 992 字节文件，含元信息头 + 启动日志 + 异常堆栈 + 更新检查失败记录
- 服务器 curl 直测：`POST /api/logs/upload` 本地与公网均返回 `ok`

## Journey Log

- [lesson] 服务器 bun 服务用 `nohup … &` 经 SSH 启动会在会话结束后被杀（SSH 会话关闭时 SIGHUP 传播），改用 systemd 服务托管
- [lesson] 非交互 SSH 会话 PATH 不含 `/root/.bun/bin`，`bun` 命令必须用绝对路径或 systemd ExecStart 指定
- [dead end] PowerShell 5.1 读无 BOM 的 UTF-8 .ps1 脚本时中文字面量乱码，UIA 自动化脚本需用 `[char]0xXXXX` 码点构造中文匹配串
- [lesson] WPF MessageBox 的按钮在 UIA 树中暴露为 Pane 无 InvokePattern；自定义 WPF 窗口的 Button 正常支持 Invoke

## Source Materials

| File | Role | Notes |
|------|------|-------|
| `docs/compose/spec/2026-08-05-logging-design.md` | 设计规格 | 已按最终实现标记 |
| `docs/compose/plans/2026-08-05-logging.md` | 实现计划 | 已按最终实现标记 |
