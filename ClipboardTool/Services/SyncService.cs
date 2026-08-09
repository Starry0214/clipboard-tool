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

    /// <summary>最近一次操作的状态文本（登录/注册失败原因等）。</summary>
    public string StatusText { get; private set; } = "";
    public string AccountName => _settings.SyncUsername;
    public string DeviceName => _settings.SyncDeviceName;

    private string BaseUrl
    {
        get
        {
            if (!string.IsNullOrEmpty(_settings.SyncServerOverride))
                return _settings.SyncServerOverride;
            return DefaultMirrors[0];
        }
    }

    private void SetStatus(string s)
    {
        StatusText = s;
        StatusChanged?.Invoke(s);
    }

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
            var src = ClipboardMonitor.DecodePng(png);
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
