using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace ClipboardTool;

/// <summary>同步服务器消息（WS 下行信封 / history 条目）。</summary>
public sealed record SyncMessage(
    string Type, long OriginDeviceId, long Seq, long Ts,
    string? Text, string? MediaId, string? Name, long Size, string? Hash = null);

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
        "://" + baseUrl[(baseUrl.IndexOf("://") + 3)..] + $"/ws?token={Uri.EscapeDataString(token)}";

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
            using var resp = await Http.SendAsync(req).ConfigureAwait(false);
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
            using var resp = await Http.SendAsync(req).ConfigureAwait(false);
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
            using var resp = await Http.SendAsync(req).ConfigureAwait(false);
            if (resp.StatusCode != System.Net.HttpStatusCode.OK)
                return null;
            return await resp.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
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
            using var resp = await Http.SendAsync(req).ConfigureAwait(false);
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
        string? mediaId = null, name = null, text = null, hash = null;
        long size = 0;
        if (m.TryGetProperty("payload", out var payload))
        {
            if (payload.ValueKind == JsonValueKind.Object)
            {
                if (payload.TryGetProperty("text", out var t)) text = t.GetString();
                if (payload.TryGetProperty("mediaId", out var id)) mediaId = id.GetRawText().Trim('"');
                if (payload.TryGetProperty("name", out var n)) name = n.GetString();
                if (payload.TryGetProperty("size", out var sz)) size = sz.GetInt64();
                if (payload.TryGetProperty("hash", out var h)) hash = h.GetString();
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
            text, mediaId, name, size, hash);
    }

    /// <summary>建立 WS 长连接并进入读循环；断线自动退避重连直到取消。</summary>
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
                await ws.ConnectAsync(new Uri(WsUrl(_baseUrl, _token)), _cts.Token).ConfigureAwait(false);
                _connected = true;
                delay = TimeSpan.FromSeconds(1);
                Log.Info($"同步 WS 已连接: {_baseUrl}");
                await ReadLoopAsync(ws, _cts.Token).ConfigureAwait(false);
                Log.Info("同步 WS 读循环退出");
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Log.Info($"同步 WS 连接失败: {ex.GetType().Name} {ex.Message}，{delay.TotalSeconds:F0}s 后重试");
            }
            finally
            {
                _connected = false;
            }
            if (_cts.IsCancellationRequested)
                break;
            try
            {
                await Task.Delay(delay, _cts.Token).ConfigureAwait(false);
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
                result = await ws.ReceiveAsync(buffer, ct).ConfigureAwait(false);
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
                ws.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None).GetAwaiter().GetResult();
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
