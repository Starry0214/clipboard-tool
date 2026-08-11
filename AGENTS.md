# AGENTS.md

轻量剪贴板工具（C# / .NET 9），Win+V 替代品：历史管理、图片/文本/文件预览、类型筛选、自动更新。**两个独立项目**：`ClipboardTool/`（WPF 主程序，net9.0-windows）+ `Launcher/`（NativeAOT 单文件引导器，负责运行时检测/安装与自更新；**禁 WPF/WinForms**——进度窗口是纯 Win32 的 `ProgressWindow.cs`）。根目录另有 `docs/compose/spec/clipboard-tool.md` 功能规格和 `.tools/` 本地测试脚本（后者被 .gitignore 忽略）。

## 构建与验证（最容易踩的坑）

- **所有 dotnet 命令必须在 `ClipboardTool/` 目录执行**（shell 的 workdir 指向它），否则 MSB1003 报"未找到项目"。
- **`dotnet build` 必须检查完整输出**：用 `dotnet build 2>&1 | Select-String "error|个错误|个警告"`。**禁止用 `Select-Object -Last N` 截断**——本会话曾多次因截断漏看编译错误，导致一直在运行旧 exe 调试假 bug。
- **build 前必须杀运行中的进程**：`Get-Process -Name ClipboardTool | Stop-Process -Force`，否则 MSB3021（exe 被锁定无法覆盖）。
- build 慢（~2 分钟）是 NuGet 漏洞检查在政务网超时（NU1900 警告），无害；可加 `--no-restore` 加速。
- 无测试项目。验证方式：启动 exe 后模拟操作（见"测试"节）。

## 架构要点（文件名看不出来的）

- **剪贴板粘贴必须用 Win32 原生 API**（`Services/Paster.cs`：OpenClipboard/EmptyClipboard/SetClipboardData + CF_UNICODETEXT/CF_HDROP/CF_DIB，后台 STA 线程执行，UI 零阻塞）。**不要改回 `System.Windows.Clipboard`**——WPF 实现依赖 OLE 消息泵，后台线程用会静默失败、Flush 失败还会滞留锁死剪贴板（本会话血泪）。
- **数据目录在 `%LocalAppData%\ClipboardTool`**（`App.DataDir`），与 exe 分离；启动时自动把旧版 exe 同目录 `data/` 迁移过去。更新/覆盖程序不丢数据。
- **图片文件化存储**：原图存 `data/images/*.png`（条目 Content=文件路径），缩略图存 BLOB。**列表查询（`Query`）不含原图**——粘贴/预览前必须 `store.GetById(id)` 取完整条目（含从文件读出的 Image），否则图片会退化成粘贴尺寸文本。
- **清空保留置顶**：`ClipboardStore.Clear()` 与 `Trim()` 一样有置顶保护（`WHERE pinned = 0`），清空只删非置顶条目，置顶条目及其图片文件保留。确认对话框文案明确提示"置顶条目将保留"。
- **热键双轨**：Win 修饰键（如 Win+V）走低级键盘钩子 `KeyboardHook`（系统硬绑定热键 RegisterHotKey 会失败）；其他修饰键走 RegisterHotKey。设置里 `UseWinV` 勾选项优先。
- **悬浮列表窗口复用**：所有关闭路径用 `Hide()`，**绝不能用 `Close()`**（WPF 窗口 Close 后不能再 Show）。
- **Overlay 弹出位置用物理像素 + `SetWindowPos`**（cursor/WorkingArea 是物理像素，WPF Left/Top 是逻辑单位，直接赋值会在非 100% DPI 下偏移）。
- **XAML 初始化顺序陷阱**：`IsChecked="True"` 写在 XAML 里会在 `InitializeComponent()` 期间触发事件，此时其他控件（如 ListBox）尚未创建 → NullReferenceException 闪退。初始选中态必须在构造函数 `InitializeComponent()` 之后设置。
- csproj 移除了 WinForms/Drawing 的全局隐式 using（`Using Remove`），用到 `System.Drawing`/`System.Windows.Forms`/`System.Diagnostics`/`System.Net.Http` 的文件需显式 using。
- 版本号由两个 `csproj` 的 `<Version>` 控制（当前 1.4.3，**两处必须一致**），自动更新与它比较。`Launcher/embedded/ClipboardToolApp.exe`（被 gitignore）是主程序 exe 副本，解压时以实际 FileVersion 为准（`GetExeVersion` 自愈，见发布节）。
- **更新说明分组规则（用户 2026-08-11）**：Windows 更新说明中联网同步相关条目单独归入【实验性功能：多端同步（可在设置中开启）】标题下，其他条目按正常列表排列。

## 网络与代理（政务网）

- nuget.org / github.com 直连不可达。需要时临时启动 xray 核心：`E:\VPS\vpn\v2rayN-With-Core\bin\xray\xray.exe run -config <配置>`，配置在 `.tools/`：`xray-nuget.json`（仅 nuget 域名走代理，用于 publish）、`xray-github.json`（github.com 走代理，用于 git push）。
- **用完立即关闭 xray**（用户强规则：进程 Stop-Process + 确认 10809 端口释放）。
- 更新服务器：域名 `https://code.starry0214.one/`（nginx 静态服务，HTTPS）。**程序内下载/更新：域名 HTTPS 优先，连不上自动回退 IP 直连 `http://107.175.228.83:8080`**（引导器与主程序 Updater 均已实现双镜像回退；`107.175.228.83` 同时是 SSH/scp 部署地址）。
- **域名走境外 TCP 透传中继**（DNSPod CNAME → luvipcdn.cn，11+ 境外节点，非缓存型 CDN，响应头与源站一致）：政务网到境外节点时通时断，导致"检查更新失败/超时/下载慢"——这是架构固有特性，不是配置错误；慢节点已知 172.237.28.151（~2.7s）、172.105.209.180（~3.5s，Linode）。排查命令：`Resolve-DnsName code.starry0214.one`、逐节点 `Invoke-WebRequest https://<节点IP>/updates/version.txt -SkipCertificateCheck -Headers @{Host='code.starry0214.one'}`、服务器日志 `/www/wwwlogs/code.starry0214.one.log`。

## 更新与发布流程

- 更新源（双镜像，代码自动回退）：域名 `https://code.starry0214.one/updates/` 优先 + IP `http://107.175.228.83:8080/` 兜底（`version.txt` + `ClipboardTool.exe` + 运行时安装包，SSH：`ssh -i ~/.ssh/id_ed25519 -p 1443 root@107.175.228.83`，nginx 配置 `/www/server/panel/vhost/nginx/updates.conf`）。
- **notes.txt 只写当前版本更新内容，不累加历史**（"发现更新"弹窗会全文展示 notes.txt，`App.xaml.cs` 直接拼接）。
- **跨版本全量日志**：`changelog.txt`（Windows）与 `changelog-android.txt`（Android）按版本分块（`vX.Y.Z` 开头）累加全部历史；两端更新弹窗优先展示"当前版本之后的所有版本"日志（`Updater.GetChangelogAsync` / `Updater.changelogForNewer`），拉取失败才回退 notes.txt。**每次发版必须在对应 changelog 顶部追加新版本块**。
- **服务器保留所有历史版本**（用户规则 2026-08-09）：发布新版本时把旧文件归档为带版本号文件名（`ClipboardTool-vX.Y.Z.exe` / `ClipboardToolApp-vX.Y.Z.apk`）保留在服务器，固定名 `ClipboardTool.exe` / `ClipboardToolApp.apk` 始终指向最新版；不在服务器留 `*.bak`。
- **发布前必须做内嵌解压校验**（1.3.5 曾因内嵌版本错配翻车：引导器版本 1.3.5 但内嵌主程序是 1.3.4，用户更新后永远显示 1.3.4）：复制内嵌文件后确认其 FileVersion 与 csproj 一致；AOT 发布后用 `--no-restore`，再清空 `%LocalAppData%\ClipboardToolApp\` 运行新引导器，确认解压出的主程序版本与 version.txt 一致后才上传。
- 发新版本（单文件引导器架构）：改主项目 csproj `<Version>` + Launcher csproj `<Version>`（必须一致）→ 杀进程 → 主项目 `dotnet publish -c Release --no-restore`（**`--no-restore` 免代理**；只有 NuGet 恢复才需 xray-nuget）→ 复制主程序 exe 到 `Launcher/embedded/ClipboardToolApp.exe` → Launcher `dotnet publish -c Release --no-restore`（AOT，产物 `Launcher/bin/Release/net9.0/win-x64/publish/剪贴板助手.exe`，发布前确认其 FileVersion）→ 归档旧版（`cp ClipboardTool.exe ClipboardTool-v旧版本.exe`）→ 重命名上传新 exe 为 `ClipboardTool.exe` 到 `/var/www/updates/` → 更新 `version.txt` + `notes.txt` + `changelog.txt` 顶部追加新版本块。
- Android 发版：改 `Android/app/build.gradle.kts` 的 `versionCode`/`versionName` → `gradle :app:assembleDebug`（或 release）→ 归档旧版（`cp ClipboardToolApp.apk ClipboardToolApp-v旧版本.apk`）→ 上传为 `ClipboardToolApp.apk` → 更新 `version-android.txt` + `changelog-android.txt` 顶部追加新版本块。

## 测试

- **防执行卡死（用户强规则 2026-08-09）**：① `uiautomator dump` 后禁止 cat 整份 XML——`adb pull` 到本地用 python 只提取 text/bounds；② bash 命令一律设合理 timeout（普通 30-60s、构建 300s，禁 600s+ 干等）；③ 禁止 `;` 串联命令掩盖失败（用 `&&` 明确依赖）；④ Android 构建用 `--offline` 免 maven 联网检查（政务网超时会挂起），**另加 `--no-daemon`**（守护进程持有管道句柄会让 `| Select-String` 管道永不关闭、命令假挂起）；⑤ Android 构建的 workdir 参数可能失效（跑成默认目录报"does not contain a Gradle build"），用 `gradle -p <项目绝对路径>` 显式指定；⑥ 长输出命令用 `Select-String` 过滤；⑦ **playwright-cli 仅 `open` 命令禁止接 `|` 管道**（`open` 会 spawn 常驻 daemon 进程 `cliDaemon.js`，其 stdout 配置为 pipe 继承该命令管道句柄，主进程退出后 daemon 常驻持有句柄 → `| Select-String` 等 EOF 永不关闭假挂起——与 gradle daemon 同构，源码 session.js `startDaemon`；实测 open 带管道必挂、snapshot 等后续命令带管道安全）；每次 playwright 操作后 `playwright-cli close-all` 清理会话（`.playwright-cli/` 已 gitignore）；⑧ **任何可能 spawn 常驻 daemon 的命令（gradle/playwright-cli/npm/node 系等）统一用「输出重定向到文件再过滤」规避管道挂起**：`cmd > .tools\out.log 2>&1` 后 `Select-String -Path .tools\out.log "pattern"`——daemon 继承的是文件句柄而非管道句柄，文件写入不阻塞任何下游，命令等主进程退出即返回（实测 playwright open 重定向秒回、带管道必挂）；识别信号=CLI 有 daemon/server/session/watch/attach 概念。
- 测试脚本在 `.tools/`：`overlay_ctrl.ps1`（find/esc/enter 投递按键）、`count_visible.ps1`（窗口计数）、`enum_windows2.ps1`（窗口枚举）、`check_db.py`/`check_img.py`（SQLite 查询）。
- 程序支持测试参数：`--show-overlay`（等效热键弹出）、`--show-main`（打开主窗口）。引导器支持：`--test-progress`（进度窗口模拟）、`--test-fallback`（强制走 IP 直连镜像，验证回退）。
- **SendKeys/SendInput 注入不触发 RegisterHotKey**；`keybd_event` 注入的按键会经过低级键盘钩子（可模拟 Win+V）。
- 崩溃排查：`Get-WinEvent -FilterHashtable @{LogName='Application'; ProviderName='.NET Runtime'} -MaxEvents 5`。
- 模拟按键/剪贴板操作前先清空 `data/`（删 db + images），避免脏数据干扰判断。

## Git

- 远程：`github.com/Starry0214/clipboard-tool`（私有，remote URL 内含 token）。push 需 xray-github 代理。
- 本地标签：v1.0.0、v1.1.0；未推送提交会积压，用户要求时才推送。
