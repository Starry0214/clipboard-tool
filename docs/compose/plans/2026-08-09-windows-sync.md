# Windows 端多端同步改造实施计划 — M2

> [!NOTE]
> This document may not reflect the current implementation.
> See the final report for up-to-date state:
> [Final Report](../reports/clipboard-sync.md)

> **For agentic workers:** REQUIRED SUB-SKILL: Use compose:subagent (recommended) or compose:execute to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 改造现有 Windows 剪贴板工具（ClipboardTool/，C# WPF .NET 9）：新增同步模块（SyncClient + SyncService），历史库加来源字段与标签/筛选，设置页末尾加"实验性功能：多端同步"开关与账号区，与 M1 已交付的 SyncServer（`SyncServer/`）联调。

**Architecture:** 复用 M1 服务器 API（注册/登录返回设备 token；WS 上行 `{"type","payload"}`、下行带 `originDeviceId/seq/ts` 信封；`POST/GET /api/media` 传二进制）。Windows 端为纯接收+转发：本地复制经 `ClipboardMonitor.EntryCaptured` 事件上传；远端消息只入历史库（**不写系统剪贴板**，无回环）；图片存 `data/images/`、文件存 `data/files/`（Content=本地路径，复用现有 Paster/预览）。同步模块默认关闭，由设置页"实验性功能"开关启用。

**Tech Stack:** C# / .NET 9 / WPF；`System.Net.Http` + `System.Net.WebSockets.ClientWebSocket`；现有 Microsoft.Data.Sqlite。

## Global Constraints

- 所有 dotnet 命令必须在 `ClipboardTool/` 目录执行（workdir）；build 前杀进程 `Get-Process -Name ClipboardTool | Stop-Process -Force`；build 检查完整输出 `dotnet build 2>&1 | Select-String "error|个错误|个警告"`，禁止截断。
- 本计划不修改 `Services/Paster.cs`、`KeyboardHook.cs`、`HotkeyManager.cs`、`TrayIcon.cs` 逻辑；不升版本号（M5 发布时统一提升双 csproj）。
- 显式 using：用到 `System.Net.Http`/`System.Diagnostics`/`System.Drawing`/`System.Windows.Forms` 的文件必须显式 using（csproj 已 Remove 全局隐式）。
- 同步默认关闭：`Settings.SyncEnabled = false`，对现有用户零影响。
- 来源值：DB 存 `"local"` / `"phone"`；UI 显示"本机" / "手机"。`Entry.Source` 默认 `"local"`。
- 服务器双镜像常量：`["https://sync.starry0214.one", "https://107.175.228.83:8081"]`（第二镜像 IP 直连需跳过证书主机名校验）；`Settings.SyncServerOverride` 非空时只用它（本地联调用 `http://127.0.0.1:8082`，UI 不暴露此字段）。
- 同步内容上限 50MB（服务器限制）；上传失败重试 3 次指数退避；WS 断线退避重连（1s 起，上限 60s）。
- 不新增测试项目（项目惯例：无测试项目，验证 = build + 启动 exe 模拟操作 + `.tools/check_db.py` 查库）。
- 服务器行为以 `SyncServer/` 实现为准（M1 已交付）：注册/登录 201/200 返回 `{"deviceId","token"}`；WS 路径 `/ws?token=`；media 上传 201 返回 `{"mediaId"}`。

---

### Task 1: Entry.Source 与 DB source 列迁移

**Covers:** S6

**Files:**
- Modify: `ClipboardTool/Services/Entry.cs`
- Modify: `ClipboardTool/Services/ClipboardStore.cs`

**Interfaces:**
- Produces: `Entry.Source`（string，默认 `"local"`）；`ClipboardStore` 构造时自动为旧库补 `source` 列（PRAGMA 探测 + ALTER，与现有 hash 列迁移同模式）；`Query(string? search, string? type, string? source)` 第三参数可选；`Add` 持久化 source。

- [ ] **Step 1: Entry 加 Source 字段**

`Entry.cs` 追加（record 属性区）：

```csharp
    public string Source { get; set; } = "local"; // local | phone（同步来源）
```

- [ ] **Step 2: ClipboardStore 迁移补列**

在 `ClipboardStore` 构造函数中 `hasHash` 探测块之后追加：

```csharp
        // 旧库无 source 列时补列（多端同步）
        var hasSource = false;
        using (var probe2 = _conn.CreateCommand())
        {
            probe2.CommandText = "PRAGMA table_info(entries)";
            using var r2 = probe2.ExecuteReader();
            while (r2.Read())
                if (r2.GetString(1) == "source")
                    hasSource = true;
        }
        if (!hasSource)
        {
            using var alter2 = _conn.CreateCommand();
            alter2.CommandText = "ALTER TABLE entries ADD COLUMN source TEXT NOT NULL DEFAULT 'local'";
            alter2.ExecuteNonQuery();
        }
```

- [ ] **Step 3: Add 持久化 source、Query 支持 source 筛选**

`Add` 的 INSERT 改为：

```csharp
        cmd.CommandText = """
            INSERT INTO entries (type, content, hash, thumb, pinned, created_at, source)
            VALUES ($type, $content, $hash, $thumb, $pinned, $created, $source)
            """;
        cmd.Parameters.AddWithValue("$source", string.IsNullOrEmpty(e.Source) ? "local" : e.Source);
```

`Query` 签名改为 `public List<Entry> Query(string? search = null, string? type = null, string? source = null)`，在 type 筛选之后追加：

```csharp
        if (!string.IsNullOrEmpty(source))
        {
            where.Add("source = $source");
            cmd.Parameters.AddWithValue("$source", source);
        }
```

两个 SELECT 语句（有 where / 无 where）都要在列清单中加 `source`，`Query` 的 reader 填充追加：

```csharp
                Source = reader.IsDBNull(6) ? "local" : reader.GetString(6),
```

`GetById` 的 SELECT 与填充也加 `source`（列序：id=0, type=1, content=2, thumb=3, image=4, pinned=5, created_at=6, source=7；reader.GetString(7)）。

- [ ] **Step 4: 构建验证**

Run: `Get-Process -Name ClipboardTool -ErrorAction SilentlyContinue | Stop-Process -Force; dotnet build 2>&1 | Select-String "error|个错误|个警告"`
Expected: 无 error、0 个错误、0 个警告。

- [ ] **Step 5: 启动验证迁移（旧库自动补列）**

```powershell
Get-Process -Name ClipboardTool -ErrorAction SilentlyContinue | Stop-Process -Force
$app = Start-Process (Join-Path (Get-Location) "bin\Debug\net9.0-windows\ClipboardTool.exe") -ArgumentList "--show-main" -PassThru
Start-Sleep -Seconds 4
& "..\.tools\check_db.py"  # 或 python .tools/check_db.py：确认 entries 表含 source 列、现有行 source='local'
Stop-Process -Name ClipboardTool -Force
```

Expected: 旧库自动补 `source` 列，既有条目 source 全部为 `local`。

- [ ] **Step 6: 提交**

```bash
git add ClipboardTool/Services/Entry.cs ClipboardTool/Services/ClipboardStore.cs
git commit -m "feat: 历史条目来源字段（local/phone）与旧库自动迁移"
```

---

### Task 2: Settings 新增同步字段

**Covers:** S6（实验性开关默认关闭）

**Files:**
- Modify: `ClipboardTool/Services/Settings.cs`

**Interfaces:**
- Produces: `Settings.SyncEnabled`（默认 false）、`SyncUsername`、`SyncToken`、`SyncDeviceId`、`SyncDeviceName`、`SyncServerOverride`（默认空，联调用）；`App`/`SyncService` 读取。

- [ ] **Step 1: 加字段**

```csharp
    /// <summary>实验性功能：多端同步（默认关闭，设置页末尾开关启用）。</summary>
    public bool SyncEnabled { get; set; }

    /// <summary>同步账号信息（登录成功后持久化，退出登录时清空 token）。</summary>
    public string SyncUsername { get; set; } = "";
    public string SyncToken { get; set; } = "";
    public long SyncDeviceId { get; set; }
    public string SyncDeviceName { get; set; } = "";

    /// <summary>同步服务器地址覆盖（空=内置双镜像；联调时可填 http://127.0.0.1:8082，UI 不暴露）。</summary>
    public string SyncServerOverride { get; set; } = "";
```

- [ ] **Step 2: 构建验证**

Run: `dotnet build 2>&1 | Select-String "error|个错误|个警告"`
Expected: 0 错误 0 警告。

- [ ] **Step 3: 提交**

```bash
git add ClipboardTool/Services/Settings.cs
git commit -m "feat: 设置新增同步账号与实验性开关字段"
```

---

### Task 3: SyncClient 网络客户端

**Covers:** S4, S6

**Files:**
- Create: `ClipboardTool/Services/SyncClient.cs`

**Interfaces:**
- Consumes: 无（纯网络层）；`Settings` 字段由外部传入
- Produces:
  - `sealed record SyncMessage(string Type, long OriginDeviceId, long Seq, long Ts, string? Text, string? MediaId, string? Name, long Size)`
  - `sealed class SyncClient : IDisposable`，构造 `SyncClient(string baseUrl, string token, string deviceName)`
  - `static Task<(long DeviceId, string Token)?> RegisterAsync(string baseUrl, string username, string password, string deviceName)`
  - `static Task<(long DeviceId, string Token)?> LoginAsync(string baseUrl, string username, string password, string deviceName)`
  - `Task<long?> UploadMediaAsync(byte[] data)`（≤50MB，返回 mediaId 或 null）
  - `Task<byte[]?> DownloadMediaAsync(long mediaId)`
  - `Task<List<SyncMessage>?> FetchHistoryAsync(long since)`（`GET /api/history`）
  - `Task ConnectAsync(CancellationToken ct)`（WS 长连接；异常自动退避重连，1s→60s）
  - `Task SendClipAsync(string text)` / `Task SendClipAsync(string type, long mediaId, string name, long size)`
  - `event Action<SyncMessage>? MessageReceived`
  - `bool Connected`（当前 WS 是否连接中）

- [ ] **Step 1: 实现 SyncClient.cs**

```csharp
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace ClipboardTool;

/// <summary>同步服务器消息（WS 下行信封 / history 条目）。</summary>
public sealed record SyncMessage(
    string Type, long OriginDeviceId, long Seq, long Ts,
    string? Text, string? MediaId, string? Name, long Size);

/// <summary>
/// 同步服务器客户端：注册/登录、媒体上传下载、历史拉取、WS 长连接（自动重连）。
/// 基址格式：https://host 或 http://127.0.0.1:8082（WS 自动推导 wss/ws）。
/// </summary>
public sealed class SyncClient : IDisposable
{
    private static readonly HttpClient Http = new(new SocketsHttpHandler
    {
        ConnectTimeout = TimeSpan.FromSeconds(15),
        SslOptions = new System.Net.Security.SslClientAuthenticationOptions
        {
            RemoteCertificateValidationCallback = (_, _, _, _) => true, // IP 直连镜像证书主机名不匹配，跳过校验
        },
    })
    { Timeout = TimeSpan.FromSeconds(30) };

    private readonly string _baseUrl;
    private readonly string _token;
    private readonly string _deviceName;
    private ClientWebSocket? _ws;
    private CancellationTokenSource? _cts;
    private volatile bool _connected;
    private readonly object _sendLock = new();

    public SyncClient(string baseUrl, string token, string deviceName)
    {
        _baseUrl = baseUrl.TrimEnd('/');
        _token = token;
        _deviceName = deviceName;
    }

    public event Action<SyncMessage>? MessageReceived;
    public bool Connected => _connected;

    private static string WsUrl(string baseUrl, string token) =>
        (baseUrl.StartsWith("https", StringComparison.OrdinalIgnoreCase) ? "wss" : "ws") +
        baseUrl[(baseUrl.IndexOf("://") + 2)..] + $"/ws?token={Uri.EscapeDataString(token)}";

    private static async Task<(long DeviceId, string Token)?> AuthAsync(string endpoint, string baseUrl,
        string username, string password, string deviceName)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, baseUrl.TrimEnd('/') + endpoint)
            {
                Content = new StringContent(JsonSerializer.Serialize(new
                {
                    username, password, deviceName,
                }), Encoding.UTF8, "application/json"),
            };
            using var resp = await Http.SendAsync(req);
            if (resp.StatusCode != System.Net.HttpStatusCode.Created && resp.StatusCode != System.Net.HttpStatusCode.OK)
                return null;
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            var root = doc.RootElement;
            return (root.GetProperty("deviceId").GetInt64(), root.GetProperty("token").GetString()!);
        }
        catch (Exception)
        {
            return null;
        }
    }

    public static Task<(long DeviceId, string Token)?> RegisterAsync(string baseUrl, string username, string password, string deviceName)
        => AuthAsync("/api/auth/register", baseUrl, username, password, deviceName);

    public static Task<(long DeviceId, string Token)?> LoginAsync(string baseUrl, string username, string password, string deviceName)
        => AuthAsync("/api/auth/login", baseUrl, username, password, deviceName);

    public async Task<long?> UploadMediaAsync(byte[] data)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, _baseUrl + "/api/media")
            {
                Content = new ByteArrayContent(data),
            };
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _token);
            using var resp = await Http.SendAsync(req);
            if (resp.StatusCode != System.Net.HttpStatusCode.Created)
                return null;
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            return doc.RootElement.GetProperty("mediaId").GetInt64();
        }
        catch (Exception)
        {
            return null;
        }
    }

    public async Task<byte[]?> DownloadMediaAsync(long mediaId)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, $"{_baseUrl}/api/media/{mediaId}");
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _token);
            using var resp = await Http.SendAsync(req);
            if (resp.StatusCode != System.Net.HttpStatusCode.OK)
                return null;
            return await resp.Content.ReadAsByteArrayAsync();
        }
        catch (Exception)
        {
            return null;
        }
    }

    public async Task<List<SyncMessage>?> FetchHistoryAsync(long since = 0)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, $"{_baseUrl}/api/history?since={since}");
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _token);
            using var resp = await Http.SendAsync(req);
            if (resp.StatusCode != System.Net.HttpStatusCode.OK)
                return null;
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            var list = new List<SyncMessage>();
            foreach (var m in doc.RootElement.GetProperty("messages").EnumerateArray())
            {
                list.Add(ParseMessage(m));
            }
            return list;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static SyncMessage ParseMessage(JsonElement m)
    {
        string? mediaId = null, name = null, text = null;
        long size = 0;
        if (m.TryGetProperty("payload", out var payload))
        {
            if (payload.ValueKind == JsonValueKind.Object)
            {
                if (payload.TryGetProperty("text", out var t)) text = t.GetString();
                if (payload.TryGetProperty("mediaId", out var id)) mediaId = id.GetRawText().Trim('"');
                if (payload.TryGetProperty("name", out var n)) name = n.GetString();
                if (payload.TryGetProperty("size", out var sz)) size = sz.GetInt64();
            }
            else if (payload.ValueKind == JsonValueKind.String)
            {
                text = payload.GetString();
            }
        }
        return new SyncMessage(
            m.GetProperty("type").GetString() ?? "",
            m.TryGetProperty("originDeviceId", out var od) ? od.GetInt64() : 0,
            m.TryGetProperty("seq", out var sq) ? sq.GetInt64() : 0,
            m.TryGetProperty("ts", out var ts) ? ts.GetInt64() : 0,
            text, mediaId, name, size);
    }

    /// <summary>建立 WS 长连接并进入读循环；断线自动退避重连直到 Dispose。</summary>
    public async Task ConnectAsync(CancellationToken ct)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var delay = TimeSpan.FromSeconds(1);
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                using var ws = new ClientWebSocket();
                ws.Options.RemoteCertificateValidationCallback = (_, _, _, _) => true;
                _ws = ws;
                await ws.ConnectAsync(new Uri(WsUrl(_baseUrl, _token)), _cts.Token);
                _connected = true;
                delay = TimeSpan.FromSeconds(1);
                await ReadLoopAsync(ws, _cts.Token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception)
            {
                // 断线/握手失败：退避重连
            }
            finally
            {
                _connected = false;
            }
            if (_cts.IsCancellationRequested)
                break;
            try
            {
                await Task.Delay(delay, _cts.Token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            delay = TimeSpan.FromMilliseconds(Math.Min(delay.TotalMilliseconds * 2, 60_000));
        }
    }

    private async Task ReadLoopAsync(ClientWebSocket ws, CancellationToken ct)
    {
        var buffer = new byte[65536];
        using var ms = new MemoryStream();
        while (!ct.IsCancellationRequested)
        {
            ms.SetLength(0);
            WebSocketReceiveResult result;
            do
            {
                result = await ws.ReceiveAsync(buffer, ct);
                if (result.MessageType == WebSocketMessageType.Close)
                    return;
                ms.Write(buffer, 0, result.Count);
            }
            while (!result.EndOfMessage);

            var json = Encoding.UTF8.GetString(ms.ToArray());
            try
            {
                using var doc = JsonDocument.Parse(json);
                MessageReceived?.Invoke(ParseMessage(doc.RootElement));
            }
            catch (JsonException)
            {
            }
        }
    }

    public async Task SendClipAsync(string text)
    {
        await SendAsync(JsonSerializer.Serialize(new { type = "clip_text", payload = new { text } }));
    }

    public async Task SendClipAsync(string type, long mediaId, string name, long size)
    {
        await SendAsync(JsonSerializer.Serialize(new { type, payload = new { mediaId, name, size } }));
    }

    private async Task SendAsync(string json)
    {
        var ws = _ws;
        if (ws is null || !_connected)
            return;
        try
        {
            var bytes = Encoding.UTF8.GetBytes(json);
            lock (_sendLock)
            {
                ws.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None).AsTask().GetAwaiter().GetResult();
            }
        }
        catch (Exception)
        {
        }
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _ws?.Dispose();
        _cts?.Dispose();
    }
}
```

- [ ] **Step 2: 构建验证**

Run: `dotnet build 2>&1 | Select-String "error|个错误|个警告"`
Expected: 0 错误 0 警告。

- [ ] **Step 3: 提交**

```bash
git add ClipboardTool/Services/SyncClient.cs
git commit -m "feat: 同步服务器客户端（注册/登录/媒体/历史/WS 重连）"
```

---

### Task 4: SyncService 业务编排

**Covers:** S4, S6

**Files:**
- Create: `ClipboardTool/Services/SyncService.cs`
- Modify: `ClipboardTool/Services/ClipboardMonitor.cs`（追加 EntryCaptured 事件）

**Interfaces:**
- Consumes: `SyncClient`、`ClipboardStore`、`Settings`、`Log`
- Produces:
  - `sealed class SyncService : IDisposable`，构造 `SyncService(ClipboardStore store, ClipboardMonitor monitor, Settings settings, string dataDir)`
  - `event Action<string>? StatusChanged`（UI 显示同步状态：未登录/已连接/重连中）
  - `bool LoggedIn`（Settings.SyncToken 非空）
  - `Task<bool> LoginAsync(string username, string password, string deviceName)`（登录成功持久化 Settings）
  - `Task<bool> RegisterAsync(string username, string password, string deviceName)`
  - `void Logout()`（清 token 并断开）
  - `Task StartAsync()`（已登录才连接；连接后拉历史 + 订阅 EntryCaptured）
  - `Task StopAsync()`
  - `ClipboardMonitor.EntryCaptured`：`public event Action<Entry>? EntryCaptured;`，在 `Capture()` 各分支 `_store.Add(...)` 成功后触发（`EntryCaptured?.Invoke(entry)`，entry 为实际入库对象）

- [ ] **Step 1: ClipboardMonitor 追加事件**

`ClipboardMonitor` 类内（SuppressNext 附近）加：

```csharp
    /// <summary>本地捕获入库成功后触发（同步模块据此上传）。</summary>
    public event Action<Entry>? EntryCaptured;
```

`Capture()` 三处 `_store.Add(...)` 处分别改为捕获返回值并在 true 时触发。以文本分支为例（图片/文件分支同理，触发对象为各自 new 的 Entry）：

```csharp
            if (!string.IsNullOrEmpty(text))
            {
                var entry = new Entry { Type = "text", Content = text, CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds() };
                if (_store.Add(entry))
                    EntryCaptured?.Invoke(entry);
                Log.Info($"捕获文本 {text.Length} 字符");
            }
```

（图片分支：`var added = _store.Add(...)` 已有，在 `if (added)` 前触发 `EntryCaptured?.Invoke(entry)`；文件分支同文本模式。）

- [ ] **Step 2: 实现 SyncService.cs**

```csharp
using System.Diagnostics;

namespace ClipboardTool;

/// <summary>
/// 多端同步编排：本地捕获→上传；远端消息→入库（source=phone）。
/// 仅入历史、不写系统剪贴板（无回环）；文本直发，图片/文件先上传媒体再发引用。
/// </summary>
public sealed class SyncService : IDisposable
{
    /// <summary>同步服务器双镜像（域名 HTTPS 优先 + IP 直连兜底）。</summary>
    public static readonly string[] DefaultMirrors = ["https://sync.starry0214.one", "https://107.175.228.83:8081"];

    private readonly ClipboardStore _store;
    private readonly ClipboardMonitor _monitor;
    private readonly Settings _settings;
    private readonly string _filesDir;
    private SyncClient? _client;
    private CancellationTokenSource? _cts;
    private volatile bool _running;

    public SyncService(ClipboardStore store, ClipboardMonitor monitor, Settings settings, string dataDir)
    {
        _store = store;
        _monitor = monitor;
        _settings = settings;
        _filesDir = Path.Combine(dataDir, "files");
        Directory.CreateDirectory(_filesDir);
    }

    public event Action<string>? StatusChanged;
    public bool LoggedIn => !string.IsNullOrEmpty(_settings.SyncToken);

    private string BaseUrl
    {
        get
        {
            if (!string.IsNullOrEmpty(_settings.SyncServerOverride))
                return _settings.SyncServerOverride;
            return DefaultMirrors[0];
        }
    }

    private void SetStatus(string s) => StatusChanged?.Invoke(s);

    public async Task<bool> LoginAsync(string username, string password, string deviceName)
    {
        var cred = await SyncClient.LoginAsync(BaseUrl, username, password, deviceName);
        if (cred is null)
        {
            SetStatus("登录失败：账号不存在或密码错误");
            return false;
        }
        _settings.SyncUsername = username;
        _settings.SyncToken = cred.Value.Token;
        _settings.SyncDeviceId = cred.Value.DeviceId;
        _settings.SyncDeviceName = deviceName;
        _settings.Save();
        SetStatus("已登录");
        return true;
    }

    public async Task<bool> RegisterAsync(string username, string password, string deviceName)
    {
        var cred = await SyncClient.RegisterAsync(BaseUrl, username, password, deviceName);
        if (cred is null)
        {
            SetStatus("注册失败：无法连接服务器");
            return false;
        }
        _settings.SyncUsername = username;
        _settings.SyncToken = cred.Value.Token;
        _settings.SyncDeviceId = cred.Value.DeviceId;
        _settings.SyncDeviceName = deviceName;
        _settings.Save();
        SetStatus("已注册并登录");
        return true;
    }

    public void Logout()
    {
        _cts?.Cancel();
        _client?.Dispose();
        _client = null;
        _settings.SyncToken = "";
        _settings.Save();
        SetStatus("未登录");
    }

    public async Task StartAsync()
    {
        if (_running || !LoggedIn)
            return;
        _running = true;
        _cts = new CancellationTokenSource();
        _client = new SyncClient(BaseUrl, _settings.SyncToken, _settings.SyncDeviceName);
        _client.MessageReceived += OnRemoteMessage;
        _monitor.EntryCaptured += OnLocalCaptured;
        SetStatus("连接中…");
        try
        {
            var history = await _client.FetchHistoryAsync();
            if (history is not null)
                foreach (var m in history)
                    ApplyRemote(m, isReplay: true);
            _ = _client.ConnectAsync(_cts.Token);
            SetStatus("已连接");
        }
        catch (Exception ex)
        {
            Log.Error("同步启动失败", ex);
            SetStatus("连接失败");
            _running = false;
        }
    }

    public async Task StopAsync()
    {
        _running = false;
        _monitor.EntryCaptured -= OnLocalCaptured;
        _cts?.Cancel();
        _client?.Dispose();
        _client = null;
        SetStatus("已停用");
        await Task.CompletedTask;
    }

    private void OnLocalCaptured(Entry entry)
    {
        if (!_running || _client is null)
            return;
        _ = Task.Run(async () =>
        {
            try
            {
                switch (entry.Type)
                {
                    case "text":
                        await _client.SendClipAsync(entry.Content);
                        break;
                    case "image":
                    case "file":
                        await UploadAndSendAsync(entry);
                        break;
                }
            }
            catch (Exception ex)
            {
                Log.Error("同步上传失败", ex);
            }
        });
    }

    private async Task UploadAndSendAsync(Entry entry)
    {
        byte[] data;
        if (entry.Type == "image")
        {
            var full = _store.GetById(entry.Id);
            data = full?.Image ?? await File.ReadAllBytesAsync(entry.Content);
        }
        else
        {
            data = await File.ReadAllBytesAsync(entry.Content);
        }
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var mediaId = await _client!.UploadMediaAsync(data);
            if (mediaId is not null)
            {
                var name = entry.Type == "image" ? $"image_{entry.Id}.png" : Path.GetFileName(entry.Content);
                await _client.SendClipAsync(entry.Type == "image" ? "clip_image" : "clip_file", mediaId.Value, name, data.Length);
                return;
            }
            await Task.Delay(TimeSpan.FromSeconds(1 << attempt));
        }
    }

    private void OnRemoteMessage(SyncMessage m)
    {
        if (!_running)
            return;
        try
        {
            ApplyRemote(m, isReplay: false);
        }
        catch (Exception ex)
        {
            Log.Error("同步消息入库失败", ex);
        }
    }

    private void ApplyRemote(SyncMessage m, bool isReplay)
    {
        switch (m.Type)
        {
            case "clip_text" when !string.IsNullOrEmpty(m.Text):
            {
                var entry = new Entry
                {
                    Type = "text",
                    Content = m.Text,
                    Source = "phone",
                    CreatedAt = m.Ts > 0 ? m.Ts : DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                };
                _store.Add(entry);
                break;
            }
            case "clip_image" when m.MediaId is not null:
                ApplyRemoteMedia(m, "image");
                break;
            case "clip_file" when m.MediaId is not null:
                ApplyRemoteMedia(m, "file");
                break;
        }
    }

    private void ApplyRemoteMedia(SyncMessage m, string type)
    {
        var bytes = _client?.DownloadMediaAsync(long.Parse(m.MediaId!)).GetAwaiter().GetResult();
        if (bytes is null || bytes.Length == 0)
            return;
        var safeName = SanitizeName(m.Name ?? (type == "image" ? "image.png" : "file.bin"));
        var localPath = type == "image"
            ? _store.SaveImageFile(bytes)
            : Path.Combine(_filesDir, $"{Guid.NewGuid():N}_{safeName}");
        if (type == "file")
            File.WriteAllBytes(localPath, bytes);
        var entry = new Entry
        {
            Type = type,
            Content = localPath,
            Source = "phone",
            Thumb = type == "image" ? MakeThumbBytes(bytes) : null,
            CreatedAt = m.Ts > 0 ? m.Ts : DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        };
        _store.Add(entry);
    }

    private static string SanitizeName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
    }

    private static byte[]? MakeThumbBytes(byte[] png)
    {
        try
        {
            using var src = ClipboardMonitor.DecodePng(png);
            return ClipboardMonitor.EncodePng(ClipboardMonitor.MakeThumb(src, 200));
        }
        catch (Exception)
        {
            return null;
        }
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _client?.Dispose();
        _monitor.EntryCaptured -= OnLocalCaptured;
    }
}
```

- [ ] **Step 3: 构建验证**

Run: `Get-Process -Name ClipboardTool -ErrorAction SilentlyContinue | Stop-Process -Force; dotnet build 2>&1 | Select-String "error|个错误|个警告"`
Expected: 0 错误 0 警告。

- [ ] **Step 4: 提交**

```bash
git add ClipboardTool/Services/SyncService.cs ClipboardTool/Services/ClipboardMonitor.cs
git commit -m "feat: 同步服务编排（本地上传/远端入库/历史回放/防重）"
```

---

### Task 5: 设置页 UI（实验性开关 + 账号区）

**Covers:** S6（实验性开关，用户 2026-08-09 补充需求）

**Files:**
- Modify: `ClipboardTool/SettingsWindow.xaml`
- Modify: `ClipboardTool/SettingsWindow.xaml.cs`
- Modify: `ClipboardTool/App.xaml.cs`（加 `public SyncService? SyncService => _sync;` 属性，供设置页取用；完整接线在 Task 7）

**Interfaces:**
- Consumes: `SyncService`（从 `(Application.Current as App)?.SyncService` 获取，可空）
- Produces: 设置页末尾"实验性功能"卡片：`SyncCheck`（多端同步开关）+ `SyncPanel`（账号区，勾选后显示）：`SyncUserBox`/`SyncPassBox`/`SyncDeviceBox`/`SyncLoginBtn`/`SyncRegisterBtn`/`SyncStatusText`/`SyncLogoutBtn`

- [ ] **Step 1: XAML 加实验性功能卡片**

`SettingsWindow.xaml` 的 ScrollViewer 内 StackPanel 末尾（数据目录 Border 之后）追加：

```xml
                <TextBlock Text="实验性功能" FontWeight="SemiBold" Foreground="#1A1A1A" Margin="0,0,0,6"/>
                <Border Style="{StaticResource CardBorder}" Margin="0,0,0,4">
                    <StackPanel Margin="4">
                        <CheckBox x:Name="SyncCheck" Content="多端同步（实验性）"
                                  Style="{StaticResource FluentCheckBox}"/>
                        <TextBlock Text="开启后，本机复制的内容会经自建服务器同步到同一账号下的其他设备（手机），手机复制的内容会出现在本机历史中并标注“手机”来源。"
                                   FontSize="12" Foreground="#888888" TextWrapping="Wrap" LineHeight="18" Margin="0,4,0,0"/>
                        <StackPanel x:Name="SyncPanel" Margin="0,10,0,0" Visibility="Collapsed">
                            <TextBlock Text="账号" FontSize="12" Foreground="#888888" Margin="0,0,0,4"/>
                            <TextBox x:Name="SyncUserBox" Style="{StaticResource FluentTextBox}" Margin="0,0,0,8"/>
                            <TextBlock Text="密码" FontSize="12" Foreground="#888888" Margin="0,0,0,4"/>
                            <PasswordBox x:Name="SyncPassBox" Style="{StaticResource FluentTextBox}" Margin="0,0,0,8"/>
                            <TextBlock Text="设备名称（如：工作电脑）" FontSize="12" Foreground="#888888" Margin="0,0,0,4"/>
                            <TextBox x:Name="SyncDeviceBox" Style="{StaticResource FluentTextBox}" Margin="0,0,0,10"/>
                            <StackPanel Orientation="Horizontal">
                                <Button x:Name="SyncLoginBtn" Content="登录" Style="{StaticResource FluentButton}"
                                        Padding="14,5" FontSize="12" Click="OnSyncLogin"/>
                                <Button x:Name="SyncRegisterBtn" Content="注册" Style="{StaticResource FluentButtonSecondary}"
                                        Padding="14,5" FontSize="12" Margin="8,0,0,0" Click="OnSyncRegister"/>
                                <Button x:Name="SyncLogoutBtn" Content="退出登录" Style="{StaticResource FluentButtonSecondary}"
                                        Padding="14,5" FontSize="12" Margin="8,0,0,0" Click="OnSyncLogout" Visibility="Collapsed"/>
                            </StackPanel>
                            <TextBlock x:Name="SyncStatusText" FontSize="12" Foreground="#C55A11"
                                       TextWrapping="Wrap" LineHeight="18" Margin="0,8,0,0"/>
                        </StackPanel>
                    </StackPanel>
                </Border>
```

（若 `FluentTextBox` 对 PasswordBox 不兼容，改用无 Style 的默认 PasswordBox。）

- [ ] **Step 2: code-behind 实现开关与账号交互**

`SettingsWindow.xaml.cs`：

构造末尾追加：

```csharp
        SyncCheck.IsChecked = settings.SyncEnabled; // 初始选中态必须在 InitializeComponent 之后设置（XAML 时序陷阱）
        SyncCheck.Checked += (_, _) => UpdateSyncUi();
        SyncCheck.Unchecked += (_, _) => UpdateSyncUi();
        SyncUserBox.Text = settings.SyncUsername;
        SyncDeviceBox.Text = string.IsNullOrEmpty(settings.SyncDeviceName) ? Environment.MachineName : settings.SyncDeviceName;
        UpdateSyncUi();
```

新增方法：

```csharp
    private SyncService? Sync => (Application.Current as App)?.SyncService;

    private void UpdateSyncUi()
    {
        var enabled = SyncCheck.IsChecked == true;
        SyncPanel.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
        var loggedIn = Sync?.LoggedIn == true;
        SyncUserBox.IsEnabled = !loggedIn;
        SyncPassBox.IsEnabled = !loggedIn;
        SyncDeviceBox.IsEnabled = !loggedIn;
        SyncLoginBtn.Visibility = loggedIn ? Visibility.Collapsed : Visibility.Visible;
        SyncRegisterBtn.Visibility = loggedIn ? Visibility.Collapsed : Visibility.Visible;
        SyncLogoutBtn.Visibility = loggedIn ? Visibility.Visible : Visibility.Collapsed;
        if (loggedIn)
        {
            var s = Sync!;
            SyncStatusText.Text = $"已登录：{s.AccountName}（{s.DeviceName}）";
            SyncStatusText.Foreground = System.Windows.Media.Brushes.Green;
        }
        else
        {
            SyncStatusText.Text = "未登录";
            SyncStatusText.Foreground = System.Windows.Media.Brushes.Gray;
        }
    }

    private async void OnSyncLogin(object sender, RoutedEventArgs e)
    {
        var sync = Sync;
        if (sync is null)
            return;
        SyncLoginBtn.IsEnabled = false;
        SyncStatusText.Text = "登录中…";
        var ok = await sync.LoginAsync(SyncUserBox.Text.Trim(), SyncPassBox.Password, SyncDeviceBox.Text.Trim());
        SyncLoginBtn.IsEnabled = true;
        SyncStatusText.Text = ok ? "已登录" : sync.StatusText;
        UpdateSyncUi();
    }

    private async void OnSyncRegister(object sender, RoutedEventArgs e)
    {
        var sync = Sync;
        if (sync is null)
            return;
        SyncRegisterBtn.IsEnabled = false;
        SyncStatusText.Text = "注册中…";
        var ok = await sync.RegisterAsync(SyncUserBox.Text.Trim(), SyncPassBox.Password, SyncDeviceBox.Text.Trim());
        SyncRegisterBtn.IsEnabled = true;
        SyncStatusText.Text = ok ? "已注册并登录" : sync.StatusText;
        UpdateSyncUi();
    }

    private void OnSyncLogout(object sender, RoutedEventArgs e)
    {
        Sync?.Logout();
        SyncStatusText.Text = "未登录";
        UpdateSyncUi();
    }
```

`OnOk` 中保存开关（在 `_settings.Save()` 前）：

```csharp
        _settings.SyncEnabled = SyncCheck.IsChecked == true;
```

同步在 `SyncService` 中补充 UI 读取的属性（Task 4 文件追加）：

```csharp
    /// <summary>最近一次操作的状态文本（登录/注册失败原因等）。</summary>
    public string StatusText { get; private set; } = "";
    public string AccountName => _settings.SyncUsername;
    public string DeviceName => _settings.SyncDeviceName;
```

并在 `SetStatus` 中记录：

```csharp
    private void SetStatus(string s)
    {
        StatusText = s;
        StatusChanged?.Invoke(s);
    }
```

`App.xaml.cs` 先加属性（接线在 Task 7）：

```csharp
    private SyncService? _sync;
    /// <summary>多端同步服务（设置页登录区使用；SyncEnabled 时才启动）。</summary>
    public SyncService? SyncService => _sync;
```

- [ ] **Step 3: 构建验证**

Run: `Get-Process -Name ClipboardTool -ErrorAction SilentlyContinue | Stop-Process -Force; dotnet build 2>&1 | Select-String "error|个错误|个警告"`
Expected: 0 错误 0 警告。

- [ ] **Step 4: 冒烟验证设置页**

```powershell
& "bin\Debug\net9.0-windows\ClipboardTool.exe" --show-main
```

手动：主窗口 → 设置 → 底部出现"实验性功能 / 多端同步"卡片；勾选后出现账号区；取消勾选账号区收起；点确定后 settings.json 出现 `"SyncEnabled": true`。

- [ ] **Step 5: 提交**

```bash
git add ClipboardTool/SettingsWindow.xaml ClipboardTool/SettingsWindow.xaml.cs ClipboardTool/App.xaml.cs ClipboardTool/Services/SyncService.cs
git commit -m "feat: 设置页实验性功能开关与账号登录区"
```

---

### Task 6: 来源标签与主窗口来源筛选

**Covers:** S6

**Files:**
- Modify: `ClipboardTool/OverlayWindow.xaml`
- Modify: `ClipboardTool/MainWindow.xaml`
- Modify: `ClipboardTool/MainWindow.xaml.cs`

**Interfaces:**
- Consumes: `Entry.Source`（Task 1）
- Produces: 悬浮列表与主窗口条目右上角"手机"橙色来源标签（仅 source=phone 显示）；主窗口筛选区新增来源筛选（全部/本机/手机）→ `_store.Query(search, type, source)`

- [ ] **Step 1: Overlay 条目加来源标签**

`OverlayWindow.xaml` 的 DataTemplate 中，把现有 PinBadge Border 替换为"来源标签 + 置顶角标"横向 StackPanel：

```xml
                            <!-- 来源标签 + 置顶角标 -->
                            <StackPanel Orientation="Horizontal" HorizontalAlignment="Right" VerticalAlignment="Top" Margin="0,0,4,0">
                                <Border x:Name="SourceBadge" Visibility="Collapsed"
                                        Background="#FFF4E5" CornerRadius="4" Padding="4,2"
                                        Margin="0,0,4,0">
                                    <TextBlock Text="手机" FontSize="10" Foreground="#C55A11" FontWeight="SemiBold"/>
                                </Border>
                                <Border x:Name="PinBadge" Visibility="Collapsed"
                                        Background="#E8F0FE" CornerRadius="4" Padding="4,2">
                                    <TextBlock Text="置顶" FontSize="10" Foreground="#0078D4" FontWeight="SemiBold"/>
                                </Border>
                            </StackPanel>
```

DataTemplate.Triggers 中追加：

```xml
                            <DataTrigger Binding="{Binding Source}" Value="phone">
                                <Setter TargetName="SourceBadge" Property="Visibility" Value="Visible"/>
                            </DataTrigger>
```

- [ ] **Step 2: MainWindow 条目加来源标签（结构与 Overlay 完全一致）**

`MainWindow.xaml` 的 DataTemplate 中 `PinBadge` Border（`:121-125`）同样替换为"来源标签 + 置顶角标"横向 StackPanel，并在 `DataTemplate.Triggers`（`:138-140` Pinned 触发器之后）加相同 SourceBadge DataTrigger：

```xml
                            <!-- 来源标签 + 置顶角标 -->
                            <StackPanel Orientation="Horizontal" HorizontalAlignment="Right" VerticalAlignment="Top" Margin="0,0,4,0">
                                <Border x:Name="SourceBadge" Visibility="Collapsed"
                                        Background="#FFF4E5" CornerRadius="4" Padding="4,2"
                                        Margin="0,0,4,0">
                                    <TextBlock Text="手机" FontSize="10" Foreground="#C55A11" FontWeight="SemiBold"/>
                                </Border>
                                <Border x:Name="PinBadge" Visibility="Collapsed"
                                        Background="#E8F0FE" CornerRadius="4" Padding="4,2">
                                    <TextBlock Text="置顶" FontSize="10" Foreground="#0078D4" FontWeight="SemiBold"/>
                                </Border>
                            </StackPanel>
```

```xml
                            <DataTrigger Binding="{Binding Source}" Value="phone">
                                <Setter TargetName="SourceBadge" Property="Visibility" Value="Visible"/>
                            </DataTrigger>
```

- [ ] **Step 3: 主窗口来源筛选**

`MainWindow.xaml` 顶部筛选 StackPanel 中 `FilterFile` RadioButton（`:64-75`）之后追加来源筛选：

```xml
                    <Rectangle Width="1" Height="16" Fill="#DDDDDD" Margin="8,0"/>
                    <RadioButton x:Name="SourceAll" GroupName="SourceFilter"
                                 Content="全部" FontSize="11" Foreground="#666666"
                                 IsChecked="True" Checked="OnSourceFilterChanged" Margin="0,0,6,0"/>
                    <RadioButton x:Name="SourceLocal" GroupName="SourceFilter"
                                 Content="本机" FontSize="11" Foreground="#666666"
                                 Checked="OnSourceFilterChanged" Margin="0,0,6,0"/>
                    <RadioButton x:Name="SourcePhone" GroupName="SourceFilter"
                                 Content="手机" FontSize="11" Foreground="#666666"
                                 Checked="OnSourceFilterChanged"/>
```

（若横向空间不足，把 `SearchBox` 的 `Padding="0,0,96,0"` 调整为 `"0,0,176,0"`。）

`MainWindow.xaml.cs` 的 `Refresh()` 改为：

```csharp
    public void Refresh()
    {
        if (HistoryList is null)
            return;
        var type = FilterText.IsChecked == true ? "text"
            : FilterImage.IsChecked == true ? "image"
            : FilterFile.IsChecked == true ? "file"
            : null;
        var source = SourceLocal.IsChecked == true ? "local"
            : SourcePhone.IsChecked == true ? "phone"
            : null;
        HistoryList.ItemsSource = _store.Query(SearchBox.Text, type, source);
        HistoryList.SelectedIndex = -1;
    }

    private void OnSourceFilterChanged(object sender, System.Windows.RoutedEventArgs e) => Refresh();
```

- [ ] **Step 4: 构建 + 冒烟验证**

Run: `Get-Process -Name ClipboardTool -ErrorAction SilentlyContinue | Stop-Process -Force; dotnet build 2>&1 | Select-String "error|个错误|个警告"`

启动 `--show-main` + `--show-overlay`：确认条目右上角标签不显示（本地条目）；来源筛选三档可切换（当前全为本地条目时"手机"档为空列表）。

- [ ] **Step 5: 提交**

```bash
git add ClipboardTool/OverlayWindow.xaml ClipboardTool/MainWindow.xaml ClipboardTool/MainWindow.xaml.cs
git commit -m "feat: 历史条目手机来源标签与主窗口来源筛选"
```

---

### Task 7: App 接线（同步生命周期）

**Covers:** S4, S6

**Files:**
- Modify: `ClipboardTool/App.xaml.cs`

**Interfaces:**
- Consumes: `SyncService`（Task 4）、`Settings.SyncEnabled`（Task 2）
- Produces: `App` 持有 `_sync`；OnStartup 创建并在 SyncEnabled 时启动；`ApplySettings()` 按开关启停；OnExit 释放；新增 `--data-dir <path>` 测试参数（联调隔离数据目录）

- [ ] **Step 1: OnStartup 创建同步服务**

`App.xaml.cs` 中 `_monitor.Start(_messageWindow);` 之后追加：

```csharp
        _sync = new SyncService(_store, _monitor, _settings, DataDir);
        if (_settings.SyncEnabled)
            _ = _sync.StartAsync();
```

- [ ] **Step 2: ApplySettings 启停同步**

`ApplySettings()` 末尾（RegisterHotkey 之后）追加：

```csharp
        if (_settings.SyncEnabled)
            _ = _sync?.StartAsync();
        else
            _ = _sync?.StopAsync();
```

- [ ] **Step 3: OnExit 释放 + --data-dir 测试参数**

`OnExit` 中 `_store?.Dispose();` 前追加：

```csharp
        _sync?.Dispose();
```

OnStartup 数据目录计算处，将 `var dataDir = Path.Combine(...)` 替换为支持测试参数：

```csharp
        // 测试钩子：--data-dir <path> 指定数据目录（联调用，隔离真实数据）
        var dataDir = e.Args.Length >= 2 && e.Args[0] == "--data-dir"
            ? Path.Combine(e.Args[1])
            : Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClipboardTool");
```

（`--data-dir` 必须是第一个参数；联调脚本使用。）

- [ ] **Step 4: 构建验证**

Run: `Get-Process -Name ClipboardTool -ErrorAction SilentlyContinue | Stop-Process -Force; dotnet build 2>&1 | Select-String "error|个错误|个警告"`
Expected: 0 错误 0 警告。

- [ ] **Step 5: 提交**

```bash
git add ClipboardTool/App.xaml.cs
git commit -m "feat: 同步服务生命周期接线与 --data-dir 测试参数"
```

---

### Task 8: 本地联调验证（SyncServer + exe + 模拟手机端）

**Covers:** S4, S6

**Files:**
- Create: `SyncServer/cmd/phone-sim/main.go`（模拟手机端联调工具，M4 复用）

**Interfaces:**
- Consumes: M1 SyncServer 全部端点；`phone-sim` 走 HTTP/WS 公开接口
- Produces: `go run ./cmd/phone-sim -base http://127.0.0.1:8082 -user alice -pass secret123 -device phone-sim -kind text -text "hello"`；`-kind image|file -media <path>`；登录（自动注册）后连 WS 发送消息并等待 2s 退出

- [ ] **Step 1: 实现 phone-sim 工具**

```go
package main

import (
	"bytes"
	"encoding/json"
	"flag"
	"fmt"
	"io"
	"net/http"
	"os"
	"strings"
	"time"

	"github.com/gorilla/websocket"
)

func main() {
	base := flag.String("base", "http://127.0.0.1:8082", "server base url")
	user := flag.String("user", "alice", "username")
	pass := flag.String("pass", "secret123", "password")
	device := flag.String("device", "phone-sim", "device name")
	kind := flag.String("kind", "text", "text | image | file")
	text := flag.String("text", "", "text content (kind=text)")
	media := flag.String("media", "", "media file path (kind=image|file)")
	flag.Parse()

	token := loginOrRegister(*base, *user, *pass, *device)

	conn, _, err := websocket.DefaultDialer.Dial(
		strings.Replace(*base, "http", "ws", 1)+"/ws?token="+token, nil)
	if err != nil {
		fatal("ws dial", err)
	}
	defer conn.Close()

	switch *kind {
	case "text":
		send(conn, map[string]any{"type": "clip_text", "payload": map[string]string{"text": *text}})
	case "image", "file":
		data, err := os.ReadFile(*media)
		if err != nil {
			fatal("read media", err)
		}
		mediaID := upload(*base, token, data)
		msgType := "clip_image"
		if *kind == "file" {
			msgType = "clip_file"
		}
		send(conn, map[string]any{"type": msgType, "payload": map[string]any{
			"mediaId": mediaID, "name": baseName(*media), "size": len(data),
		}})
	}
	fmt.Printf("sent %s (%s)\n", *kind, *text)
	time.Sleep(2 * time.Second)
}

func loginOrRegister(base, user, pass, device string) string {
	body := fmt.Sprintf(`{"username":%q,"password":%q,"deviceName":%q}`, user, pass, device)
	resp, err := http.Post(base+"/api/auth/login", "application/json", strings.NewReader(body))
	if err != nil {
		fatal("login request", err)
	}
	if resp.StatusCode != http.StatusOK {
		resp.Body.Close()
		resp, err = http.Post(base+"/api/auth/register", "application/json", strings.NewReader(body))
		if err != nil {
			fatal("register request", err)
		}
	}
	defer resp.Body.Close()
	var out struct {
		Token string `json:"token"`
	}
	json.NewDecoder(resp.Body).Decode(&out)
	if out.Token == "" {
		fatal("auth failed", fmt.Errorf("status=%d", resp.StatusCode))
	}
	return out.Token
}

func upload(base, token string, data []byte) int64 {
	req, _ := http.NewRequest("POST", base+"/api/media", bytes.NewReader(data))
	req.Header.Set("Authorization", "Bearer "+token)
	resp, err := http.DefaultClient.Do(req)
	if err != nil {
		fatal("upload", err)
	}
	defer resp.Body.Close()
	var out struct {
		MediaID int64 `json:"mediaId"`
	}
	json.NewDecoder(resp.Body).Decode(&out)
	if out.MediaID == 0 {
		b, _ := io.ReadAll(resp.Body)
		fatal("upload failed", fmt.Errorf("status=%d %s", resp.StatusCode, b))
	}
	return out.MediaID
}

func send(conn *websocket.Conn, msg any) {
	data, _ := json.Marshal(msg)
	if err := conn.WriteMessage(websocket.TextMessage, data); err != nil {
		fatal("ws send", err)
	}
}

func baseName(p string) string {
	if i := strings.LastIndexAny(p, `/\`); i >= 0 {
		return p[i+1:]
	}
	return p
}

func fatal(what string, err error) {
	fmt.Fprintf(os.Stderr, "phone-sim FAIL %s: %v\n", what, err)
	os.Exit(1)
}
```

- [ ] **Step 2: 构建 phone-sim 与 SyncServer**

Run（SyncServer/ 目录）：`go build ./... 2>&1`
Expected: 无输出（构建成功）。

- [ ] **Step 3: 启动本地 SyncServer**

```powershell
# SyncServer/ 目录，后台运行（独立终端或 Start-Process）
go run . -addr 127.0.0.1:8082 -db C:\Users\Starry\AppData\Local\Temp\sync_m2_test.db
```

Expected: 日志 `sync server listening on 127.0.0.1:8082`。

- [ ] **Step 4: 启动 exe（隔离数据目录）并登录**

```powershell
$tmp = "C:\Users\Starry\AppData\Local\Temp\clipboard_m2_test"
Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue
& "ClipboardTool\bin\Debug\net9.0-windows\ClipboardTool.exe" --data-dir $tmp --show-main
```

手动：设置 → 勾选"多端同步" → 账号区填 `alice / secret123 / PC 联调机` → 点"注册" → 状态"已注册并登录" → 确定。

再手动编辑 `$tmp\settings.json` 加 `"SyncServerOverride": "http://127.0.0.1:8082"`（或登录前先加），重启 exe（SyncEnabled=true 时自动连接）。

- [ ] **Step 5: 验证矩阵（双向、三类内容、历史回放、防回环）**

```powershell
# 1) 手机→电脑 文本：exe 历史出现"手机"橙色标签条目
go run ./cmd/phone-sim -base http://127.0.0.1:8082 -user alice -pass secret123 -device phone-sim -kind text -text "来自手机的文本"
python .tools\check_db.py   # 确认该条 source='phone'

# 2) 手机→电脑 图片：data/images/ 新增 png、缩略图可见
go run ./cmd/phone-sim -base http://127.0.0.1:8082 -user alice -pass secret123 -device phone-sim -kind image -media C:\path\to\test.png

# 3) 手机→电脑 文件：data/files/ 新增文件
go run ./cmd/phone-sim -base http://127.0.0.1:8082 -user alice -pass secret123 -device phone-sim -kind file -media C:\path\to\test.txt

# 4) 电脑→手机：复制一段文本，phone-sim 应收到 WS 消息（phone-sim 输出即验证；或加 -listen 模式）
#    phone-sim 当前为发后即退；改为先读 5s 再退：在 sleep 前加 conn.ReadMessage() 循环打印收到的消息
# 5) 防回环：手机发来的文本不会再次上传（服务器 history 中该条 originDeviceId=phone-sim）
python .tools\check_db.py   # 确认无重复条目
# 6) 历史回放：重启 exe → 服务器 7 天内消息合并入库，哈希去重不重复
```

第 4 步需要 phone-sim 支持接收：在 Step 1 代码的 `send(...)` 之后、`time.Sleep` 之前插入接收循环：

```go
	// 接收模式：打印 5 秒内收到的消息（验证电脑→手机方向）
	conn.SetReadDeadline(time.Now().Add(5 * time.Second))
	for {
		_, data, err := conn.ReadMessage()
		if err != nil {
			break
		}
		fmt.Printf("received: %s\n", data)
	}
```

- [ ] **Step 6: 异常路径抽查**

- 停掉 SyncServer → exe 日志出现重连尝试，不崩溃（`Log.Error` 记录）；重启 SyncServer → 自动重连恢复。
- `settings.json` 清空 SyncToken → 重启 exe：同步不启动，设置页显示"未登录"。

- [ ] **Step 7: 提交**

```bash
git add SyncServer/cmd/phone-sim/main.go
git commit -m "feat: 模拟手机端联调工具（phone-sim）"
```

