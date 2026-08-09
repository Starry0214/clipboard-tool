using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace ClipboardTool;

/// <summary>
/// 多端同步编排：本地捕获→上传；远端消息→入库（source=phone）。
/// 仅入历史、不写系统剪贴板（无回环）；文本直发，图片/文件先上传媒体再发引用。
/// </summary>
public sealed class SyncService : IDisposable
{
    /// <summary>同步服务器双镜像（域名 HTTPS 优先 + IP 直连兜底，与更新服务同域名）。</summary>
    public static readonly string[] DefaultMirrors = ["https://code.starry0214.one/sync", "https://107.175.228.83:8081"];

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
        {
            Log.Info($"同步未启动: running={_running}, loggedIn={LoggedIn}");
            return;
        }
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
                {
                    if (m.Seq <= _settings.SyncLastSeq)
                        continue; // 已处理过（跨重启去重）
                    await ApplyRemote(m, isReplay: true); // delete 只来自彻底删除，任何同步都应用
                    if (m.Seq > _settings.SyncLastSeq)
                    {
                        _settings.SyncLastSeq = m.Seq;
                        _settings.Save();
                    }
                }
            _ = _client.ConnectAsync(_cts.Token);
            SetStatus("已连接");
            Log.Info($"同步已连接: base={BaseUrl}, history={history?.Count ?? -1}, lastSeq={_settings.SyncLastSeq}");
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

    private async void OnRemoteMessage(SyncMessage m)
    {
        if (!_running)
            return;
        try
        {
            if (m.Seq > 0 && m.Seq <= _settings.SyncLastSeq)
                return; // 已处理过
            Log.Info($"同步收到消息: {m.Type} origin={m.OriginDeviceId} seq={m.Seq}");
            await ApplyRemote(m, isReplay: false); // delete 只来自彻底删除，任何同步都应用
            if (m.Seq > _settings.SyncLastSeq)
            {
                _settings.SyncLastSeq = m.Seq;
                _settings.Save();
            }
        }
        catch (Exception ex)
        {
            Log.Error("同步消息入库失败", ex);
        }
    }

    /// <summary>手动同步服务器到本地：全量拉取（含 delete 记录）并应用——删除仅在此路径传播；本地已删条目可从服务器找回。</summary>
    public async Task<string?> SyncNowAsync()
    {
        if (_client is null || !_running)
            return "未连接，无法同步";
        var history = await _client.FetchHistoryAsync(0);
        if (history is null)
            return "同步失败：无法连接服务器";
        var n = 0;
        long maxSeq = _settings.SyncLastSeq;
        foreach (var m in history)
        {
            await ApplyRemote(m, isReplay: true);
            if (m.Seq > maxSeq)
                maxSeq = m.Seq;
            n++;
        }
        _settings.SyncLastSeq = maxSeq;
        _settings.Save();
        return $"同步完成（处理 {n} 条）";
    }

    /// <summary>
    /// 删除条目。fully=false 本地删除：仅删本机、服务器保留（其他设备不知情，手动同步可找回）；
    /// fully=true 彻底删除：本地删 + 发服务器（删原消息落 delete 记录，任何端任何同步都应用删除）。
    /// </summary>
    public void DeleteEntry(Entry entry, bool fully)
    {
        _store.Delete(entry.Id);
        if (!fully || !_running || _client is null)
            return;
        var hash = ComputeSyncHash(entry);
        if (hash is null)
            return;
        _ = Task.Run(async () =>
        {
            for (var attempt = 0; attempt < 3; attempt++)
            {
                if (await _client.SendDeleteAsync(hash))
                    return;
                await Task.Delay(TimeSpan.FromSeconds(1 << attempt));
            }
        });
    }

    private string? ComputeSyncHash(Entry entry)
    {
        if (entry.Type == "text")
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("text\0" + entry.Content))).ToLowerInvariant();
        if (entry.Type == "image")
        {
            var full = _store.GetById(entry.Id); // 列表条目不含原图，需取完整条目
            if (full?.Image is null)
                return null;
            return Convert.ToHexString(SHA256.HashData(full.Image)).ToLowerInvariant();
        }
        return null; // file：本地路径哈希无法跨端匹配
    }

    private async Task ApplyRemote(SyncMessage m, bool isReplay)
    {
        switch (m.Type)
        {
            case "delete" when !string.IsNullOrEmpty(m.Hash):
                _store.DeleteByHash(m.Hash!);
                break;
            case "clip_text" when !string.IsNullOrEmpty(m.Text):
            {
                var entry = new Entry
                {
                    Type = "text",
                    Content = m.Text,
                    Source = "phone",
                    CreatedAt = m.Ts > 0 ? m.Ts / 1000 : DateTimeOffset.UtcNow.ToUnixTimeSeconds(), // 服务器 ts 为毫秒，本地库用秒
                };
                _store.Add(entry);
                break;
            }
            case "clip_image" when m.MediaId is not null:
                await ApplyRemoteMedia(m, "image");
                break;
            case "clip_file" when m.MediaId is not null:
                await ApplyRemoteMedia(m, "file");
                break;
        }
    }

    private async Task ApplyRemoteMedia(SyncMessage m, string type)
    {
        var bytes = _client is null ? null : await _client.DownloadMediaAsync(long.Parse(m.MediaId!));
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
            Image = type == "image" ? bytes : null, // 仅用于内容哈希去重（跨回放同一图片不重复入库）
            Thumb = type == "image" ? MakeThumbBytes(bytes) : null,
            CreatedAt = m.Ts > 0 ? m.Ts / 1000 : DateTimeOffset.UtcNow.ToUnixTimeSeconds(), // 服务器 ts 为毫秒，本地库用秒
        };
        if (!_store.Add(entry))
        {
            try { File.Delete(localPath); } catch (IOException) { } // 去重未新增时清理残留文件
        }
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
