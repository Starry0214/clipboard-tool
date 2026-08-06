# AGENTS.md

轻量剪贴板工具（C# / .NET 9 WPF，net9.0-windows），Win+V 替代品：历史管理、图片/文本/文件预览、类型筛选、自动更新。所有开发工作都在 `ClipboardTool/` 目录内（根目录另有 `docs/compose/spec/clipboard-tool.md` 功能规格和 `.tools/` 本地测试脚本，后者被 .gitignore 忽略）。

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
- 版本号由 `csproj` 的 `<Version>` 控制（当前 1.2.0），自动更新与它比较。

## 网络与代理（政务网）

- nuget.org / github.com 直连不可达。需要时临时启动 xray 核心：`E:\VPS\vpn\v2rayN-With-Core\bin\xray\xray.exe run -config <配置>`，配置在 `.tools/`：`xray-nuget.json`（仅 nuget 域名走代理，用于 publish）、`xray-github.json`（github.com 走代理，用于 git push）。
- **用完立即关闭 xray**（用户强规则：进程 Stop-Process + 确认 10809 端口释放）。
- 更新服务器：域名 `https://code.starry0214.one/`（nginx 静态服务，HTTPS）。**用户规则：程序内下载/更新一律走域名，禁止 IP 直连**（`107.175.228.83` 仅用于 SSH/scp 部署管理）。

## 更新与发布流程

- 更新源：域名 `https://code.starry0214.one/updates/`（`version.txt` + `ClipboardTool.exe` + 运行时安装包，SSH：`ssh -i ~/.ssh/id_ed25519 -p 1443 root@107.175.228.83`，nginx 配置 `/www/server/panel/vhost/nginx/updates.conf`）。
- 发新版本（单文件引导器架构）：改主项目 csproj `<Version>` + Launcher csproj `<Version>` → 杀进程 → 主项目 `dotnet publish -c Release`（需代理）→ 复制主程序 exe 到 `Launcher/embedded/ClipboardToolApp.exe` → Launcher `dotnet publish -c Release`（AOT，产物 `Launcher/bin/Release/net9.0/win-x64/publish/剪贴板助手.exe`）→ 重命名上传为 `ClipboardTool.exe` 到 `/var/www/updates/` → 更新 `version.txt`。

## 测试

- 测试脚本在 `.tools/`：`overlay_ctrl.ps1`（find/esc/enter 投递按键）、`count_visible.ps1`（窗口计数）、`enum_windows2.ps1`（窗口枚举）、`check_db.py`/`check_img.py`（SQLite 查询）。
- 程序支持测试参数：`--show-overlay`（等效热键弹出）、`--show-main`（打开主窗口）。引导器支持：`--test-progress`（运行时安装进度窗口模拟）。
- **SendKeys/SendInput 注入不触发 RegisterHotKey**；`keybd_event` 注入的按键会经过低级键盘钩子（可模拟 Win+V）。
- 崩溃排查：`Get-WinEvent -FilterHashtable @{LogName='Application'; ProviderName='.NET Runtime'} -MaxEvents 5`。
- 模拟按键/剪贴板操作前先清空 `data/`（删 db + images），避免脏数据干扰判断。

## Git

- 远程：`github.com/Starry0214/clipboard-tool`（私有，remote URL 内含 token）。push 需 xray-github 代理。
- 本地标签：v1.0.0、v1.1.0；未推送提交会积压，用户要求时才推送。
