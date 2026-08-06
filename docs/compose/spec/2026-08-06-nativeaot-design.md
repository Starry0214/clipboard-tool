# NativeAOT 发布攻坚 — 设计文档

日期：2026-08-06
状态：已确认（用户批准设计）
目标：将 Release 发布切换为 NativeAOT，Private 内存从 ~176MB 降至 ~60-80MB

## [S1] 问题

当前 Release 为自包含 JIT 版（PublishSingleFile + SelfContained）：单文件 ~75MB，运行 Private 内存 ~176MB / 工作集 ~286MB。用户认为内存偏高，期望更低。

## [S2] 方案概述

改用 **NativeAOT 发布**（`PublishAot=true`，仅 Release）：.NET 代码预编译为原生机器码，无 JIT/解释器，运行时元数据裁剪，产物为自包含原生 exe（不需目标机装 .NET），内存显著降低（WPF 场景实测预期 Private ~60-80MB）。**同时保留框架依赖版作为对比基线**（实测 Private ~103MB）。

## [S3] 关键技术风险与对策

| 风险 | 现状代码 | AOT 下问题 | 对策 |
|---|---|---|---|
| System.Text.Json 反射 | `Settings.Load/Save` 用 `JsonSerializer.Deserialize<Settings>`（Settings.cs:25,44） | AOT 裁剪反射元数据 → 运行时抛异常 | 新增 `SettingsJsonContext : JsonSerializerContext`（source generator，`[JsonSerializable(typeof(Settings))]`），Load/Save 改用带 context 的重载 |
| dynamic COM | `Settings.ApplyStartMenuShortcut` 用 `dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell"))`（Settings.cs:96-104） | AOT 不支持 dynamic（Microsoft.CSharp 依赖反射） | 改为 **ComImport 接口**：声明 `IWshRuntimeLibrary` 风格接口 + `Type.GetTypeFromProgID` + `Activator.CreateInstance` 转型，或直接用 IShellLink COM 接口（ComImport + CoCreateInstance） |
| SQLite 原生库 | `Microsoft.Data.Sqlite` 9.0.0（SQLitePCLRaw bundle） | bundle 的 e_sqlite3 原生库在 AOT 单文件中的加载方式 | SQLitePCLRaw 2.1+ 支持 NativeAOT（bundle 静态链接）；实测验证，若失败改用 `SQLitePCLRaw.bundle_e_sqlite3` 静态方案 |
| System.Drawing | TrayIcon.CreateIcon 用 GDI+（System.Drawing） | .NET 9 Windows 上 System.Drawing.Common 支持 AOT（有 `[SupportedOSPlatform]` 标注） | 实测验证；若 GDI+ 不可用，托盘图标改用 WPF 渲染或提前生成 .ico 资源 |
| WPF 本身 | 全部 UI | .NET 8+ 官方支持 WPF NativeAOT（实验性，9.0 更成熟） | net9.0-windows 实测 |

## [S4] 实施步骤

1. **csproj**：Release 属性组加 `<PublishAot>true</PublishAot>`，保留 PublishSingleFile（AOT 天然单文件，可去掉 SelfContained/Compression 相关项或保留验证）；`dotnet publish -c Release -r win-x64` 验证编译通过
2. **Settings JSON sourcegen**：`SettingsJsonContext`（partial class 继承 JsonSerializerContext + JsonSerializable），`Load` 用 `JsonSerializer.Deserialize(source, SettingsJsonContext.Default.Settings)`，`Save` 对应
3. **快捷方式 COM 重建**：ComImport 声明 IShellLinkW + IPersistFile（CLSID_ShellLink `{00021401-0000-0000-C000-000000000046}`），`Marshal.BindToMoniker` 或 `Activator.CreateInstance(Type.GetTypeFromCLSID)` 创建，SetPath/SetWorkingDirectory/SetIconLocation → Save（lnk 路径）。删除逻辑不变（File.Delete）
4. **AOT 兼容审计**：全文 grep `dynamic`、`Activator.CreateInstance`、反射 API（GetType().GetMethod 等）、`Assembly.GetExecutingAssembly()`（Updater.CurrentVersion 用 Assembly 反射——AOT 下可用但需 `[RequiresAssemblyFiles]` 检查；改为 `Environment.Version` 或 csproj 生成常量更稳）
5. **全功能回归**（发布产物运行实测）：
   - 启动/单实例/托盘图标/托盘菜单（含开始菜单快捷方式创建/删除）
   - 剪贴板捕获：文本/图片/文件、去重
   - 粘贴：Win32 剪贴板（CF_UNICODETEXT/CF_HDROP/CF_DIB/CF_PNG）、纯文本模式
   - 热键：Win+V 钩子、RegisterHotKey
   - 悬浮列表：弹出/动画/搜索/筛选/右键菜单/预览/单击粘贴/高度自适应
   - 主窗口：列表/搜索/筛选/双击预览/自动刷新
   - 设置：全部项读写、JSON 持久化
   - 更新：检查（版本比较用新方式）、下载进度、应用替换
   - 日志与一键上报
6. **内存实测**：新产物 idle Private/WS，对比基线（JIT 自包含 176/286、框架依赖 103/147），目标 ≤80MB Private
7. **产物形态确认**：单文件大小（预期 30-50MB）、双击运行、免依赖

## [S5] 版本与回退

- 实施期间不改版本号（仍 1.3.2），发布配置调整不影响功能版本
- 若 NativeAOT 遇到不可解问题（如 WPF 兼容崩溃）：回退到现有 JIT 自包含配置（csproj 恢复），报告结论
- 发布配置与功能代码分离，AOT 失败不影响功能代码提交

## [S6] 验证标准

- `dotnet publish -c Release -r win-x64` 0 错误
- 发布 exe 全功能回归通过（S4.5 列表）
- 内存：idle Private ≤80MB（目标），工作集 ≤150MB
- 单文件启动正常，无"缺少依赖"类错误

## [S7] 范围外（YAGNI）

- 不做跨平台（Windows only）
- 不做安装器（分发仍是 exe 直发）
- 不优化启动速度（AOT 天然更快，不额外做）
- 不改 UI 技术栈
