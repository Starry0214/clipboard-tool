# NativeAOT 发布攻坚 实现计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use compose:subagent (recommended) or compose:execute to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 将 Release 发布切换为 NativeAOT，Private 内存从 ~176MB 降至 ~60-80MB，并保持全部功能可用。

**Architecture:** csproj Release 加 `PublishAot`；修复 3 个 AOT 不兼容点（Settings JSON 反射 → source generator；快捷方式 dynamic COM → IShellLink ComImport；Updater 版本反射 → 编译期常量）；全功能回归后实测内存。

**Tech Stack:** .NET 9 / WPF / NativeAOT（win-x64）、System.Text.Json source generator、COM Interop（IShellLinkW/IPersistFile）、Microsoft.Data.Sqlite 9.0.0

## Global Constraints

- 所有 dotnet 命令在 `ClipboardTool/` 目录执行；build/publish 前杀 ClipboardTool 进程；输出用 `Select-String "error|个错误"` 检查完整输出。
- publish 需 xray-nuget 代理（政务网）；用完立即关 xray 并确认 10809 释放。
- 不改变功能行为、不改 UI、不改数据格式；版本号保持 1.3.2。
- 用户数据目录 `%LocalAppData%\ClipboardTool` 全程不动；测试用临时 db（复制备份、测完恢复）。
- AOT 编译警告（IL2xxx 系列）允许存在，但 `error` 必须为 0。
- NativeAOT 首次发布需下载 AOT 相关 NuGet（Microsoft.DotNet.ILCompiler）——必须走代理。

---

### Task 1: csproj 启用 PublishAot 并首次编译验证

**Covers:** [S3] 风险 ① 前置、[S4] 步骤 1

**Files:**
- Modify: `ClipboardTool/ClipboardTool.csproj`

**Interfaces:**
- Consumes: 无
- Produces: Release 配置 `PublishAot=true`；AOT 编译可行性结论（本任务失败则整体回退，不进入后续任务）

- [ ] **Step 1: 修改 csproj Release 属性组**

将 `ClipboardTool.csproj` 的 Release PropertyGroup 改为（保留 SingleFile 语义，AOT 天然单文件）：

```xml
  <PropertyGroup Condition="'$(Configuration)' == 'Release'">
    <PublishAot>true</PublishAot>
    <RuntimeIdentifier>win-x64</RuntimeIdentifier>
    <StripSymbols>true</StripSymbols>
    <InvariantGlobalization>true</InvariantGlobalization>
  </PropertyGroup>
```

（删除原 `PublishSingleFile/SelfContained/IncludeNativeLibrariesForSelfExtract/EnableCompressionInSingleFile`——AOT 产物本身就是单文件原生 exe，这些配置不再适用；`InvariantGlobalization` 减小 ICU 数据体积与内存。）

- [ ] **Step 2: 首次 AOT 编译（预期会出现 IL 警告与可能的错误）**

```powershell
Get-Process -Name ClipboardTool -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Process xray（xray-nuget.json）；确认 10809 监听
dotnet publish -c Release -r win-x64 2>&1 | Select-String "error|个错误|IL2"
```

期望：编译开始后输出 IL2xxx 系列警告（AOT 分析器提示）。**记录全部 error 行**——这是后续任务的输入。

- [ ] **Step 3: 记录编译结果并提交**

- 若 `0 个错误`：提交 csproj，进入 Task 2
- 若存在 error：不提交，先解决可快速修复项（如 `PublishAot` 与 WPF 的已知冲突），把剩余错误整理给主流程评估是否回退

```powershell
git add ClipboardTool/ClipboardTool.csproj
git commit -m "build: Release 切换 NativeAOT（PublishAot）"
```

---

### Task 2: Settings JSON 反射改 source generator

**Covers:** [S3] 风险 ①

**Files:**
- Modify: `ClipboardTool/Services/Settings.cs`
- Create: `ClipboardTool/Services/SettingsJsonContext.cs`

**Interfaces:**
- Consumes: `Settings` 类型（现有属性不变）
- Produces: `SettingsJsonContext.Default.Settings`（`JsonTypeInfo<Settings>`）；`Settings.Load/Save` 改用带 context 的序列化重载

- [ ] **Step 1: 新建 SettingsJsonContext.cs**

```csharp
using System.Text.Json.Serialization;

namespace ClipboardTool;

/// <summary>Settings 的 JSON source generator 上下文（NativeAOT 下反射序列化会被裁剪，必须用生成器）。</summary>
[JsonSerializable(typeof(Settings))]
public sealed partial class SettingsJsonContext : JsonSerializerContext
{
}
```

- [ ] **Step 2: Settings.Load 改用 context**

原代码（Settings.cs:25）：

```csharp
s = JsonSerializer.Deserialize<Settings>(File.ReadAllText(path)) ?? s;
```

改为：

```csharp
s = JsonSerializer.Deserialize(File.ReadAllText(path), SettingsJsonContext.Default.Settings) ?? s;
```

- [ ] **Step 3: Settings.Save 改用 context**

原代码（Settings.cs:44）：

```csharp
File.WriteAllText(_path, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
```

改为：

```csharp
File.WriteAllText(_path, JsonSerializer.Serialize(this, SettingsJsonContext.Default.Settings));
```

注意：`SettingsJsonContext.Default.Settings` 的默认选项不带 `WriteIndented`。如需保持缩进格式，在 context 上声明 `JsonSourceGenerationOptions`：

```csharp
[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(Settings))]
```

- [ ] **Step 4: 验证**

```powershell
dotnet build 2>&1 | Select-String "error|个错误"
# 功能验证：启动程序 → 打开设置 → 改一项（如条数上限）→ 确定 → 检查 settings.json 内容正确
Get-Content "$env:LOCALAPPDATA\ClipboardTool\settings.json"
```

期望：0 错误；settings.json 正常读写（JSON 结构与之前一致或仅格式差异）。

- [ ] **Step 5: 提交**

```powershell
git add ClipboardTool/Services/Settings.cs ClipboardTool/Services/SettingsJsonContext.cs
git commit -m "fix: Settings JSON 改用 source generator（NativeAOT 兼容）"
```

---

### Task 3: 快捷方式 dynamic COM 重建为 IShellLink

**Covers:** [S3] 风险 ②

**Files:**
- Modify: `ClipboardTool/Services/Settings.cs`

**Interfaces:**
- Consumes: `Settings.StartMenuShortcut`（属性不变）
- Produces: `ApplyStartMenuShortcut()` 内部改用 ComImport 的 IShellLinkW；行为与之前完全一致（创建/删除 `%AppData%\...\Start Menu\Programs\剪贴板助手.lnk`，目标=当前 exe，图标=exe,0，工作目录=exe 目录）

- [ ] **Step 1: 替换 ApplyStartMenuShortcut 的实现**

删除原 dynamic 实现（Settings.cs:96-120 附近），替换为：

```csharp
    /// <summary>创建/删除开始菜单快捷方式（%AppData%\...\Start Menu\Programs\剪贴板助手.lnk）。</summary>
    public void ApplyStartMenuShortcut()
    {
        try
        {
            var startMenuDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), "Programs");
            var lnk = Path.Combine(startMenuDir, "剪贴板助手.lnk");
            if (!StartMenuShortcut)
            {
                if (File.Exists(lnk))
                    File.Delete(lnk);
                return;
            }
            Directory.CreateDirectory(startMenuDir);
            var exe = Environment.ProcessPath ?? "";
            if (string.IsNullOrEmpty(exe))
                return;
            // NativeAOT 兼容：不用 dynamic/WScript.Shell，直接走 IShellLink COM 接口
            var shellLink = (IShellLinkW)Activator.CreateInstance(Type.GetTypeFromCLSID(CLSID_ShellLink)!)!;
            try
            {
                shellLink.SetPath(exe);
                shellLink.SetWorkingDirectory(Path.GetDirectoryName(exe) ?? "");
                shellLink.SetIconLocation(exe, 0);
                ((IPersistFile)shellLink).Save(lnk, false);
            }
            finally
            {
                Marshal.ReleaseComObject(shellLink);
            }
        }
        catch (Exception)
        {
            // 与旧实现一致：失败静默（不阻断设置保存）
        }
    }

    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    private class ShellLink { }

    [ComImport, Guid("000214F9-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszFile, int cch, IntPtr pfd, uint fFlags);
        void GetIDList(out IntPtr ppidl);
        void SetIDList(IntPtr pidl);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszName, int cch);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszDir, int cch);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszArgs, int cch);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
        void GetHotkey(out short pwHotkey);
        void SetHotkey(short wHotkey);
        void GetShowCmd(out int piShowCmd);
        void SetShowCmd(int iShowCmd);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszIconPath, int cch, out int piIcon);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, uint dwReserved);
        void Resolve(IntPtr hwnd, uint fFlags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
    }

    [ComImport, Guid("0000010B-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPersistFile
    {
        void GetClassID(out Guid pClassID);
        void IsDirty();
        void Load([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, uint dwMode);
        void Save([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, bool fRemember);
        void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string pszFileName);
        void GetCurFile([Out, MarshalAs(UnmanagedType.LPWStr)] out string ppszFileName);
    }

    private static readonly Guid CLSID_ShellLink = new("00021401-0000-0000-C000-000000000046");
```

注意：`Activator.CreateInstance(Type.GetTypeFromCLSID(...))` 在 AOT 下可用（COM 激活不依赖反射元数据，但 `GetTypeFromCLSID` 返回类型在 AOT 下可能被裁剪）——**更稳妥的做法**是用 `Marshal.GetActiveObject` 不可用（非注册对象），改用 `CoCreateInstance` 风格：

实际上最稳的 AOT 方案是 P/Invoke `CoCreateInstance`：

```csharp
    [DllImport("ole32.dll")]
    private static extern int CoCreateInstance(ref Guid rclsid, IntPtr pUnkOuter, uint dwClsContext, ref Guid riid, out IntPtr ppv);
```

替换创建逻辑：

```csharp
            var iidIShellLink = new Guid("000214F9-0000-0000-C000-000000000046");
            CoCreateInstance(ref CLSID_ShellLink, IntPtr.Zero, 1 /*CLSCTX_INPROC_SERVER*/, ref iidIShellLink, out var pShellLink);
            try
            {
                var shellLink = (IShellLinkW)Marshal.GetObjectForIUnknown(pShellLink);
                shellLink.SetPath(exe);
                shellLink.SetWorkingDirectory(Path.GetDirectoryName(exe) ?? "");
                shellLink.SetIconLocation(exe, 0);
                Marshal.GetObjectForIUnknown(pShellLink) is IPersistFile pf
                    ? ((IPersistFile)Marshal.GetObjectForIUnknown(pShellLink)).Save(lnk, false)
                    : throw new InvalidOperationException("IPersistFile 不可用");
            }
            finally
            {
                if (pShellLink != IntPtr.Zero)
                    Marshal.Release(pShellLink);
            }
```

实现时以 **CoCreateInstance + GetObjectForIUnknown** 为准（AOT 完全兼容）；若 `Marshal.GetObjectForIUnknown` 也触发 IL 警告，则用 ComWrappers 方案（`StrategyBasedComWrappers`，net8+ 内置）——见 Task 3 Step 2 的验证结果决定。

- [ ] **Step 2: 构建验证**

```powershell
dotnet build 2>&1 | Select-String "error|个错误"
```

期望：0 错误（可能有 IL2xxx 警告，忽略）。

- [ ] **Step 3: 功能验证**

```powershell
# 通过设置 UI：勾选"添加到开始菜单"→ 确定
Start-Process bin\Debug\net9.0-windows\ClipboardTool.exe --show-main
# （UIA 或手动）设置 → 勾选 → 确定
Test-Path "$env:APPDATA\Microsoft\Windows\Start Menu\Programs\剪贴板助手.lnk"
# 取消勾选 → 确定
# Test-Path 应为 False
```

期望：勾选创建 .lnk、取消删除 .lnk，与旧实现行为一致。

- [ ] **Step 4: 提交**

```powershell
git add ClipboardTool/Services/Settings.cs
git commit -m "fix: 快捷方式改用 IShellLink COM（NativeAOT 兼容，去 dynamic）"
```

---

### Task 4: Updater 版本反射改编译期常量 + AOT 全量编译

**Covers:** [S3] 风险 ③ 相关、[S4] 步骤 4

**Files:**
- Modify: `ClipboardTool/Services/Updater.cs`
- Modify: `ClipboardTool/ClipboardTool.csproj`

**Interfaces:**
- Consumes: 无
- Produces: `Updater.CurrentVersion` 不再依赖 `Assembly.GetExecutingAssembly()`（AOT 下 IL2026 警告源），改为 csproj `AssemblyInformationalVersion` 或直接 `const string`；保持返回类型 `string`

- [ ] **Step 1: 改造 CurrentVersion**

原代码（Updater.cs:17-18）：

```csharp
    public static string CurrentVersion { get; } =
        Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0";
```

改为：

```csharp
    /// <summary>当前程序版本（与 csproj &lt;Version&gt; 同步，编译期注入）。</summary>
    public static string CurrentVersion { get; } =
        typeof(Updater).Assembly.GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "1.0.0";
```

若此写法仍触发 IL 警告，直接改成硬编码常量并注释说明需与 csproj 同步：

```csharp
    public static string CurrentVersion { get; } = "1.3.2"; // 与 csproj <Version> 同步
```

删除 `using System.Reflection;`（如不再需要）。

- [ ] **Step 2: 完整 AOT 编译（含全部修复）**

```powershell
Get-Process -Name ClipboardTool | Stop-Process -Force
# xray 已运行时直接 publish
dotnet publish -c Release -r win-x64 2>&1 | Select-String "error|个错误|IL2026|IL3050"
Get-Item bin\Release\net9.0-windows\win-x64\publish\ClipboardTool.exe | Select-Object Length, LastWriteTime
```

期望：`0 个错误`；产物存在（预期 30-60MB 单文件）。若仍有 error：回到对应 Task 修复后重跑，最多 3 轮；3 轮不解决则按 S5 回退。

- [ ] **Step 3: 提交**

```powershell
git add ClipboardTool/Services/Updater.cs ClipboardTool/ClipboardTool.csproj
git commit -m "fix: Updater 版本号去反射（NativeAOT 兼容）"
```

---

### Task 5: 发布产物全功能回归

**Covers:** [S4] 步骤 5、[S6]

**Files:** 无（纯验证）

**Interfaces:**
- Consumes: Task 4 的发布产物 `bin/Release/net9.0-windows/win-x64/publish/ClipboardTool.exe`

- [ ] **Step 1: 准备隔离数据**

```powershell
$bak = "C:\Users\Starry\OneDrive\Desktop\agent周事项\剪切板工具开发\.tools\data_backup_aot_$(Get-Date -Format yyyyMMddHHmmss)"
Copy-Item "$env:LOCALAPPDATA\ClipboardTool" $bak -Recurse -Force
Remove-Item "$env:LOCALAPPDATA\ClipboardTool\clipboard.db" -Force -ErrorAction SilentlyContinue
Remove-Item "$env:LOCALAPPDATA\ClipboardTool\logs" -Recurse -Force -ErrorAction SilentlyContinue
```

- [ ] **Step 2: 启动 + 基础功能**

```powershell
Start-Process bin\Release\net9.0-windows\win-x64\publish\ClipboardTool.exe --show-main
Start-Sleep 5
# 窗口存在、进程存活
powershell -NoProfile -Command "& 'C:\Users\Starry\OneDrive\Desktop\agent周事项\剪切板工具开发\.tools\enum_windows2.ps1' (Get-Process ClipboardTool).Id | Select-String 'VIS=True'"
```

期望：主窗口可见、进程存活、无崩溃弹窗。

- [ ] **Step 3: 剪贴板捕获 + 粘贴**

```powershell
Set-Clipboard -Value "AOT测试文本-$(Get-Date -Format HHmmss)"; Start-Sleep 2
# 悬浮列表显示并含该文本（UIA 或 check_db.py 确认 db 有条目）
python C:\Users\Starry\OneDrive\Desktop\agent周事项\剪切板工具开发\.tools\check_db.py "$env:LOCALAPPDATA\ClipboardTool\clipboard.db"
# 复制图片/文件各一，确认捕获
```

期望：文本/图片/文件捕获正常；`GetById`（预览路径）正常。

- [ ] **Step 4: 热键 + 悬浮列表**

```powershell
# --show-overlay 等效验证悬浮列表渲染（AOT 版 WPF 渲染）
Get-Process -Name ClipboardTool | Stop-Process -Force
Start-Process bin\Release\net9.0-windows\win-x64\publish\ClipboardTool.exe --show-overlay
Start-Sleep 4
# 窗口 440 宽可见（list_win_rect 或 enum_windows）
```

期望：悬浮列表显示、搜索/筛选/右键菜单可用（UIA 抽查 1-2 项）。

- [ ] **Step 5: 设置读写 + 开始菜单快捷方式**

```powershell
# 设置窗口改一项保存；勾选"添加到开始菜单"确定
Test-Path "$env:APPDATA\Microsoft\Windows\Start Menu\Programs\剪贴板助手.lnk"
Get-Content "$env:LOCALAPPDATA\ClipboardTool\settings.json"
```

期望：settings.json 正常（sourcegen 生效）；.lnk 创建成功（IShellLink 生效）；取消勾选删除成功。

- [ ] **Step 6: 日志 + 更新检查**

```powershell
Get-Content "$env:LOCALAPPDATA\ClipboardTool\logs\clipboard.log" | Select-Object -Last 3
# 含"程序启动 v1.3.2"与检查更新记录
```

期望：日志写入正常（含版本号 1.3.2）；更新检查正常（服务器 version.txt 1.3.2 = 已是最新）。

- [ ] **Step 7: 恢复用户数据**

```powershell
Get-Process -Name ClipboardTool | Stop-Process -Force
Copy-Item "$bak\*" "$env:LOCALAPPDATA\ClipboardTool\" -Recurse -Force
```

---

### Task 6: 内存实测与结论

**Covers:** [S4] 步骤 6-7、[S6]

**Files:** 无

**Interfaces:**
- Consumes: AOT 发布产物

- [ ] **Step 1: 内存基线对比**

```powershell
# AOT 版（隔离数据）
Start-Process bin\Release\net9.0-windows\win-x64\publish\ClipboardTool.exe
Start-Sleep 8
Get-Process -Name ClipboardTool | Select-Object @{N='PrivateMB';E={[math]::Round($_.PrivateMemorySize64/1MB)}}, @{N='WSMB';E={[math]::Round($_.WorkingSet64/1MB)}}
```

记录值并与基线表对比：

| 版本 | Private | 工作集 |
|---|---|---|
| JIT 自包含（旧） | ~176MB | ~286MB |
| 框架依赖（Debug） | ~103MB | ~147MB |
| NativeAOT（本次） | 目标 ≤80MB | 目标 ≤150MB |

- [ ] **Step 2: 结论汇报**

- 达标（Private ≤80MB）：汇报成功，说明产物路径与预期
- 未达标但显著改善（<120MB）：汇报现状与差距，问用户是否接受
- 无改善或更高：回到 S5 回退策略

---

## Self-Review

**Spec 覆盖：**
- [S3] 风险① JSON → Task 2 ✓；风险② COM → Task 3 ✓；风险③ SQLite → Task 4 全量编译验证（SQLitePCLRaw AOT 支持在编译期暴露，若 error 在 Task 4 Step 2 拦截）✓；风险④ System.Drawing、⑤ WPF → Task 5 回归 ✓
- [S4] 步骤 1→Task 1、2→Task 2、3→Task 3、4→Task 4、5→Task 5、6→Task 6、7→Task 6 ✓
- [S5] 回退 → Task 1 Step 3 / Task 4 Step 2 / Task 6 Step 2 均有分支 ✓
- [S6] 验证标准 → Task 5/6 ✓
- [S7] 范围外 → 无实现任务（正确）✓

**类型一致性：** `SettingsJsonContext.Default.Settings`（Task 2）与 `JsonSerializer.Deserialize/Serialize` 重载一致；`IShellLinkW/IPersistFile/CoCreateInstance`（Task 3）接口方法签名与 COM vtable 一致（IShellLinkW 官方 19 方法顺序）；`CurrentVersion` 返回 string 不变（Task 4）✓

**已知风险：** IShellLinkW 接口方法顺序必须严格匹配 COM vtable（19 个方法按文档顺序排列，本计划已按官方定义排列）；若 GetObjectForIUnknown 触发 IL 警告，改用 StrategyBasedComWrappers（net8+ 内置，AOT 支持）。Task 3 的 COM 接口实现以"官方 vtable 顺序"为准，若编译或运行异常按此备选方案调整。
