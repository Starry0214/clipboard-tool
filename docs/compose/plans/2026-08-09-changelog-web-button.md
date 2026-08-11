# 更新历史网页 + 两端设置界面按钮 实施计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use compose:subagent (recommended) or compose:execute to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 在 Windows 与 Android 设置界面各放一个「查看历史更新记录」按钮，点击跳转到服务器上的统一更新历史网页（单页双 Tab：Windows/Android），网页动态加载现有 changelog.txt / changelog-android.txt 渲染版本时间线。

**Architecture:** 服务器 nginx 静态目录 `/var/www/updates/` 新增单文件 `changelog.html`（内联 CSS+JS）。JS 用 `fetch()` 同源加载两个 changelog txt，按 `vX.Y.Z` 正则分块渲染为卡片时间线；URL hash `#windows`/`#android` 决定默认 Tab，两端按钮跳转时带各自 hash。发版只改 txt，网页自动同步，零额外维护。两端各加一个按钮（Windows 新增「关于」区块显示版本号，Android 复用现有「关于」卡片）。

**Tech Stack:** 静态 HTML/CSS/vanilla JS；C# WPF（Windows）；Kotlin Compose + Android Intent（Android）；scp 部署。

## Global Constraints

- Windows 项目：`ClipboardTool/` 目录执行 dotnet 命令；build 前杀进程 `Get-Process -Name ClipboardTool | Stop-Process -Force`；build 用 `--no-restore` 免代理；必须检查完整 build 输出（`2>&1 | Select-String "error|个错误"`），禁止截断
- Android 构建：`gradle -p <项目绝对路径> :app:assembleDebug --offline --no-daemon`（workdir 参数会失效必须 `-p`；`--no-daemon` 防管道挂起）
- 打开浏览器必须用系统默认方式：Windows `Process.Start(new ProcessStartInfo(url) { UseShellExecute = true })`；Android `Intent(Intent.ACTION_VIEW)`，**不用** `System.Windows.Clipboard` 之类无关 API
- Windows 文件内显式 using：csproj 移除了 `System.Diagnostics` 等全局隐式 using（`SettingsWindow.xaml.cs` 已有 `using System.Diagnostics;`）
- 网页与程序内 URL 常量：域名 `https://code.starry0214.one/updates/`（下载/跳转均用此）；IP 兜底镜像 `http://107.175.228.83:8080` 仅用于程序内下载回退，浏览器跳转只用域名
- 服务器：`root@107.175.228.83` 端口 1443，key `~/.ssh/id_ed25519`，网页部署到 `/var/www/updates/changelog.html`
- 无测试项目；验证方式 = 编译通过 + 模拟操作（见各任务）
- changelog txt 格式（服务器已确认）：`vX.Y.Z` 独占一行开新块；条目行以 `- ` 开头；分组标题行如 `【实验性功能：多端同步（可在设置中开启）】`；块间有空行

---

### Task 1: changelog.html 网页（本地编写 + 渲染验证）

**Covers:** （无 spec 文档；本任务即网页主体）

**Files:**
- Create: `web/changelog.html`（仓库根下 `web/` 目录，部署时 scp 到服务器）

**Interfaces:**
- Produces: `web/changelog.html` —— 单文件，`#windows`/`#android` hash 切换 Tab，fetch 同目录 `changelog.txt`/`changelog-android.txt`
- 后续 Task 4 部署该文件到 `/var/www/updates/changelog.html`（同目录已有两个 txt，同源 fetch 直接可用）

- [ ] **Step 1: 编写 `web/changelog.html`**

要求：
- 内联 CSS + JS，UTF-8，`<html lang="zh-CN">`
- 品牌蓝主色 `#1565C0`（与 app 一致）；卡片白底、圆角 12px、浅阴影；`prefers-color-scheme: dark` 深色适配
- 头部：标题「更新历史」+ 两个 Tab 按钮（`Windows 电脑` / `Android 手机`），点击切换 + 更新 `location.hash`；页面加载读取 hash 选 Tab（默认 `#windows`）；`hashchange` 事件同步 Tab
- JS `parseChangelog(text)`：按行解析——`/^v(\d+\.\d+\.\d+)$/` 开启新块 `{version, groups:[{title, items}]}`；`- ` 开头追加条目（无标题分组自动创建 `title:null`）；`【` 开头开新分组；其他非空行视为条目；空行跳过。`renderBlocks(blocks)` 渲染为版本卡片（版本号 + 分组标题行 + 条目列表）
- 加载：`fetch('changelog.txt')` / `fetch('changelog-android.txt')` 并行，任一失败在该 Tab 显示「无法加载更新记录，请检查网络后重试」；成功则渲染对应平台
- 移动端自适应（`max-width: 640px` 时卡片单列全宽、字体略小）

- [ ] **Step 2: 本地渲染验证**

拉取服务器 txt 到 `web/` 临时验证（验证后删除 txt 副本，只保留 html 源文件）：

```powershell
ssh -i ~/.ssh/id_ed25519 -p 1443 root@107.175.228.83 "cat /var/www/updates/changelog.txt" > web/changelog.txt
ssh -i ~/.ssh/id_ed25519 -p 1443 root@107.175.228.83 "cat /var/www/updates/changelog-android.txt" > web/changelog-android.txt
```

本地起 HTTP 服务（file:// 下 fetch 会被 CORS 拦截，必须走 http）：

```powershell
# 在 web/ 目录：python -m http.server 8123  （后台运行）
```

用 playwright headless（或本机浏览器）打开 `http://127.0.0.1:8123/changelog.html`，确认：默认 Windows Tab 渲染出全部版本卡片、`#android` 时切到 Android 列表、【实验性功能】分组标题样式存在、深色模式不破版。

- [ ] **Step 3: 清理验证副本并提交**

删除 `web/changelog.txt`、`web/changelog-android.txt`（本地不需要，服务器是唯一来源）。git add `web/changelog.html` + `docs/compose/plans/2026-08-09-changelog-web-button.md`，commit：`feat(web): 更新历史网页（单页双平台 Tab，动态加载 changelog）`

---

### Task 2: Windows 设置界面「关于」区块 + 按钮

**Covers:** 无 spec；本任务实现 Windows 端按钮

**Files:**
- Modify: `ClipboardTool/SettingsWindow.xaml`（在「数据目录」区块后、「实验性功能」区块前插入「关于」区块）
- Modify: `ClipboardTool/SettingsWindow.xaml.cs`（构造函数设版本号文本 + 新增 `OnOpenChangelog` 处理器）

**Interfaces:**
- Consumes: `Updater.CurrentVersion`（静态属性，`ClipboardTool/Services/Updater.cs:21`，返回 `Assembly.GetExecutingAssembly().GetName().Version?.ToString(3)`）
- Produces: `OnOpenChangelog(object sender, RoutedEventArgs e)` —— 无返回值，打开浏览器

- [ ] **Step 1: XAML 新增「关于」区块**

在 `SettingsWindow.xaml` 第 61 行（`</Border>`，数据目录 CardBorder 结束）与第 63 行（`实验性功能` TextBlock）之间插入：

```xml
                <TextBlock Text="关于" FontWeight="SemiBold" Foreground="#1A1A1A" Margin="0,0,0,6"/>
                <Border Style="{StaticResource CardBorder}" Margin="0,0,0,4">
                    <StackPanel Margin="4">
                        <TextBlock x:Name="AboutVersionText" FontSize="12" Foreground="#666666" Margin="0,0,0,6"/>
                        <Button Content="查看历史更新记录" Style="{StaticResource FluentButtonSecondary}"
                                Padding="10,4" FontSize="12" Click="OnOpenChangelog"/>
                    </StackPanel>
                </Border>
```

- [ ] **Step 2: 代码后端设版本号 + 点击处理器**

`SettingsWindow.xaml.cs` 构造函数中 `LoadDataInfo();` 之后加：

```csharp
        AboutVersionText.Text = $"版本 {Updater.CurrentVersion}";
```

文件末尾（`OnOk` 之前）新增：

```csharp
    /// <summary>打开更新历史网页（服务器单页，Windows 平台 Tab）。</summary>
    private void OnOpenChangelog(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo("https://code.starry0214.one/updates/changelog.html#windows")
            {
                UseShellExecute = true,
            });
        }
        catch (Exception)
        {
        }
    }
```

（`using System.Diagnostics;` 第 1 行已存在，无需新增）

- [ ] **Step 3: 编译验证**

```powershell
Get-Process -Name ClipboardTool -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet build --no-restore 2>&1 | Select-String "error|个错误|个警告"
```

预期：0 个错误。若出现 XAML 错误，检查插入位置缩进与闭合标签。

- [ ] **Step 4: 运行验证（按钮行为）**

启动 exe（`--show-main` 打开主窗口），UIAutomation 定位主窗口「设置」入口打开设置窗口，找到「查看历史更新记录」按钮点击，确认系统默认浏览器打开 `https://code.starry0214.one/updates/changelog.html#windows`。

- [ ] **Step 5: Commit**

```bash
git add ClipboardTool/SettingsWindow.xaml ClipboardTool/SettingsWindow.xaml.cs
git commit -m "feat(win): 设置界面新增关于区块与查看历史更新记录入口"
```

---

### Task 3: Android 设置界面「查看历史更新记录」按钮

**Covers:** 无 spec；本任务实现 Android 端按钮

**Files:**
- Modify: `C:\Android\clipboard-tool\Android\app\src\main\java\com\starry\clipboardtool\ui\SettingsScreen.kt`

**Interfaces:**
- Consumes: 现有 `SectionCard("关于")` 区块（第 97-113 行）；`LocalContext.current` 已有
- Produces: 无对外接口；按钮 onClick 直接 `context.startActivity(intent)`

- [ ] **Step 1: 「关于」卡片加按钮**

将第 100-108 行「当前版本」Text + 独立「检查更新」Button 改为一行两个按钮（Row 布局），并在其下保留更新结果 Text：

```kotlin
        SectionCard("关于") {
            Text("当前版本：v${Updater.currentVersion(context)}",
                style = MaterialTheme.typography.bodyMedium)
            Spacer(Modifier.height(8.dp))
            Row {
                Button(onClick = {
                    updateResult = "检查中…"
                    updateChangelog = null
                    UpdateChecker.checkManual(context) { latest, changelog ->
                        updateResult = latest
                        updateChangelog = changelog
                    }
                }) { Text("检查更新") }
                Spacer(Modifier.width(8.dp))
                OutlinedButton(onClick = {
                    val intent = Intent(Intent.ACTION_VIEW,
                        Uri.parse("https://code.starry0214.one/updates/changelog.html#android"))
                    context.startActivity(intent)
                }) { Text("查看历史更新记录") }
            }
            val isNewVersion = updateResult?.matches(Regex("^\\d+\\.\\d+\\.\\d+$")) == true
            if (updateResult != null && !isNewVersion && updateResult != "检查中…") {
                Text(updateResult!!, style = MaterialTheme.typography.bodyMedium)
            }
        }
```

新增 imports（文件顶部 import 区）：

```kotlin
import android.content.Intent
import android.net.Uri
import androidx.compose.material3.OutlinedButton
import androidx.compose.foundation.layout.width
```

- [ ] **Step 2: 构建验证**

```powershell
gradle -p C:\Android\clipboard-tool\Android :app:assembleDebug --offline --no-daemon 2>&1 | Select-String "error|BUILD"
```

预期：`BUILD SUCCESSFUL`，0 error。

- [ ] **Step 3: 设备验证（点击行为）**

安装到 aff0300b：

```powershell
C:\Android\platform-tools\adb.exe -s aff0300b install -r C:\Android\clipboard-tool\Android\app\build\outputs\apk\debug\app-debug.apk
```

启动 App → 设置页 → 点击「查看历史更新记录」，确认系统浏览器打开 `https://code.starry0214.one/updates/changelog.html#android`（`adb shell dumpsys activity activities | Select-String "mResumedActivity"` 可见浏览器）。

- [ ] **Step 4: Commit**（Android 仓库 `C:\Android\clipboard-tool`，分支 android-dev）

```bash
git add Android/app/src/main/java/com/starry/clipboardtool/ui/SettingsScreen.kt
git commit -m "feat(android): 设置页新增查看历史更新记录入口（跳转网页）"
```

---

### Task 4: 部署 + 端到端验证

**Covers:** 网页上线 + 全链路验证

**Files:**
- Deploy: `web/changelog.html` → `root@107.175.228.83:/var/www/updates/changelog.html`

**Interfaces:**
- Consumes: Task 1 的 `web/changelog.html`；服务器 /var/www/updates/ 下已有的 changelog.txt、changelog-android.txt

- [ ] **Step 1: 部署**

```powershell
scp -i ~/.ssh/id_ed25519 -P 1443 web/changelog.html root@107.175.228.83:/var/www/updates/changelog.html
```

- [ ] **Step 2: 服务器端验证**

```powershell
ssh -i ~/.ssh/id_ed25519 -p 1443 root@107.175.228.83 "curl -s -o /dev/null -w '%{http_code}\n' http://127.0.0.1:8080/changelog.html && curl -s -o /dev/null -w '%{http_code}\n' http://127.0.0.1:8080/changelog.txt && curl -s -o /dev/null -w '%{http_code}\n' http://127.0.0.1:8080/changelog-android.txt"
```

预期：三个 `200`。

- [ ] **Step 3: 线上渲染验证**

本机浏览器（或 playwright headless）打开 `https://code.starry0214.one/updates/changelog.html`，确认：页面可访问、Windows Tab 渲染出全部版本（v1.4.3 至 v1.3.7）、`#android` 切到 Android 列表、深色模式正常。若域名不通（境外中继时通时断），改用 `http://107.175.228.83:8080/changelog.html` 验证后重试域名。

- [ ] **Step 4: 两端端到端回归（按钮点击 → 网页打开）**

Windows：启动已编译 exe，设置 →「查看历史更新记录」→ 浏览器打开 `#windows` 页。
Android：已安装 debug APK，设置 →「查看历史更新记录」→ 浏览器打开 `#android` 页。

- [ ] **Step 5: 提交收尾**

Windows 仓库：确认无遗漏改动。向用户汇报：网页已上线、两端按钮完成，询问是否连同上轮未推送改动（Windows `4e5269d`、Android `d085b9d`/`193527b`/`ab6c8e2`）一起推送并发版 1.4.4 / 1.0.4。
