# 剪贴板助手（ClipboardTool）

轻量、跨平台的剪贴板历史管理工具，支持 Windows 和 Android 双端，可作为 Win+V 的替代品。

## 功能特性

- 📋 剪贴板历史自动记录：文本、图片、文件
- 🖼️ 图片 / 文本 / 文件预览
- 🔍 类型筛选与快速检索
- 📌 重要内容置顶，跨端同步
- 🗑️ 历史清理（本机清理 / 多端彻底清理）
- 🔄 多端同步：Windows ↔ Android
- ⚡ 自动更新：单文件引导器 + 双镜像更新源

## 技术栈

| 端 | 技术 |
|---|---|
| Windows 主程序 | C# / .NET 9 / WPF |
| Windows 引导器 | C# / .NET 9 / NativeAOT 单文件 |
| Android | Kotlin / Jetpack Compose |
| 同步服务器 | Go / SQLite / WebSocket |

## 目录结构

```text
ClipboardTool/          Windows WPF 主程序
Launcher/               Windows 单文件引导器（自动更新）
Android/                Android 客户端
SyncServer/             Go 多端同步服务器
web/                    更新历史网页
docs/                   设计文档与规格
```

## 构建

### Windows

```powershell
cd ClipboardTool
dotnet build -c Release
```

### Android

```bash
cd Android
gradle :app:assembleDebug
```

### 同步服务器

```bash
cd SyncServer
go build ./...
```

## 说明

- Windows 数据目录：`%LocalAppData%\ClipboardTool`
- 同步服务为自建服务器，默认关闭，可在设置中开启

## 仓库地址

https://github.com/Starry0214/clipboard-tool
