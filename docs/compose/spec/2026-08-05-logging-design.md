# 日志 + 一键上报 设计文档

日期：2026-08-05
状态：已确认（用户批准设计）
目标版本：v1.3.0

## [S1] 问题

程序无任何日志，排查问题只能靠 Windows 事件查看器。报错（更新失败、热键失效、崩溃）时用户无法提供上下文信息，开发者难以定位。

## [S2] 方案概述

- 客户端：零依赖轻量 `Log` 静态类（文本文件 + 大小轮转），全局异常钩子捕获未处理异常，自定义错误窗口 + 托盘菜单提供"一键上报日志"。
- 服务器端：现有 Elysia (bun) 服务（code.starry0214.one）新增 `POST /api/logs/upload` 路由，日志落盘到 `/root/code-gen-service/logs/`。

## [S3] 客户端日志组件

新增 `ClipboardTool/Services/Log.cs`（静态类）：

- API：`Log.Info(string msg)`、`Log.Error(string msg, Exception? ex = null)`、`Log.UploadAsync()` 等
- 文件：`%LocalAppData%\ClipboardTool\logs\clipboard.log`
- 大小轮转：单文件 1MB，超出滚动为 `clipboard.1.log`…`clipboard.4.log`（共保留 5 个文件，最旧删除）
- 行格式：`2026-08-05 15:30:00.123 [INFO] 消息` / `[ERROR] 消息` + 异常堆栈（多行）
- 线程安全：`lock` 保护写入
- 写失败（如磁盘满）静默忽略，不抛异常影响主流程

## [S4] 记录内容（四类）

| 类别 | 触发点 | 级别 |
|---|---|---|
| 关键事件 | 启动/退出（版本、数据目录）、热键注册失败、更新检查/下载结果（成功/失败/耗时）、设置变更、数据迁移 | INFO |
| 运行中错误 | 剪贴板监听捕获失败、粘贴失败（Paster 异常）、图片保存/删除失败 | ERROR |
| 未处理异常 | 三个全局钩子：DispatcherUnhandledException / AppDomain.UnhandledException / TaskScheduler.UnobservedTaskException → 写日志 + 弹错误窗 | ERROR |
| 捕获明细 | 每次剪贴板捕获：类型、字节数（文本字符数/图片像素）、耗时 | INFO |

**隐私决策（用户已确认）**：捕获明细**不记录剪贴板文本内容本身**，只记元信息（类型/大小/耗时），避免密码、身份证等敏感文本落盘和上报。

## [S5] 未处理异常捕获

在 `App.xaml.cs` 启动时挂三个钩子：

- `DispatcherUnhandledException`（UI 线程）→ 记日志，`e.Handled = true`，弹 `ErrorWindow`
- `AppDomain.CurrentDomain.UnhandledException`（非 UI 线程致命异常）→ 记日志后由系统处理（不拦截）
- `TaskScheduler.UnobservedTaskException`（async void 遗漏）→ 记日志，`e.SetObserved()`

错误窗口只在异常发生时弹出（非崩溃场景不弹窗打扰）。

为便于测试，程序支持隐藏测试参数 `--throw`：启动后在 UI 线程抛一个未处理异常（模拟崩溃路径），仅调试用。

## [S6] 一键上报

- **上报内容**：合并 `logs/` 下所有日志文件（当前 + 轮转），头部附加元信息块：程序版本、.NET 版本、系统版本、上报时间、启动时长
- **目标**：`POST https://code.starry0214.one/api/logs/upload`，body 为原始文本（Content-Type: text/plain），超时 30 秒
- **入口①**：`ErrorWindow`（自定义窗口：标题"出错了"、错误信息、两个按钮"上报日志"（主）/"关闭"）
- **入口②**：托盘菜单新增"反馈问题" → 打开 `ErrorWindow` 的无错误变体（说明文字 + "上报日志"/"关闭"）
- 上报成功 → MessageBox "日志已上报，感谢反馈"；失败 → "上报失败，请检查网络"
- 上报期间窗口显示"正在上报…"并禁用按钮，完成后恢复

## [S7] 服务器端

在 `code-gen-service`（`/root/code-gen-service/`，Elysia + bun）新增 `src/routes/logs.ts`：

- `POST /api/logs/upload`：读 raw body（text/plain），限制 5MB（超出返回 413），追加时间戳落盘为 `/root/code-gen-service/logs/<yyyyMMdd-HHmmss>-<8位随机>.log`
- 创建 `logs/` 目录（不存在则建）
- 在 `src/index.ts` 注册：`.use(logsRouter)`
- nginx：`code.starry0214.one` 站点 `client_max_body_size` 默认 1MB → 调大到 10MB（server 块内）
- 重启 bun 服务生效

## [S8] 测试验证

客户端（本地）：

1. 启动程序 → 检查 `logs/clipboard.log` 生成，含启动事件、热键注册、更新检查记录
2. 复制文本/图片/文件 → 检查捕获明细行（类型/大小，无内容）
3. 粘贴操作 → 无 ERROR 行
4. 用测试参数（如 `--throw`）触发未处理异常 → ErrorWindow 弹出，日志含堆栈
5. 点击"上报日志" → 服务器收到文件，本地提示成功
6. 托盘"反馈问题" → 同上

服务器端：

1. `curl -X POST --data-binary @test.log https://code.starry0214.one/api/logs/upload` → 200
2. 确认落盘文件内容完整
3. 超过 5MB 的 body → 413

## [S9] 范围外（YAGNI）

- 不上报剪贴板内容（见 S4 隐私决策）
- 不做日志查看器 UI（用户可打开 logs 目录）
- 不做敏感信息脱敏过滤（当前日志不含内容，仅路径等常规信息）
- 不做崩溃自动重启
