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
    /// <summary>WS 断开期间 HTTP 增量轮询兜底（境外中继对长连接不稳，轮询保证消息不丢）。</summary>
    private System.Threading.Timer? _pollTimer;
    private const int PollIntervalMs = 30_000;

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

    /// <summary>以 FileShare.ReadWrite 读取文件全部字节：WPS 等以 ReadWrite 访问 + FileShare.Read
    /// 独占写打开文件时，File.ReadAllBytes 的 FileShare.Read 共享声明会冲突导致读失败（2026-08-20 实测）。</summary>
    private static byte[] ReadAllBytes(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var ms = new MemoryStream();
        fs.CopyTo(ms);
        return ms.ToArray();
    }

    private static async Task<byte[]> ReadAllBytesAsync(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var ms = new MemoryStream();
        await fs.CopyToAsync(ms);
        return ms.ToArray();
    }

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
        if (_client is not null)
        {
            _client.MessageReceived -= OnRemoteMessage;
            _client.Reconnected -= OnReconnected;
            _client.Dispose();
        }
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
        _client.Reconnected += OnReconnected;
        _monitor.EntryCaptured += OnLocalCaptured;
        // WS 断开期间 HTTP 增量轮询兜底：境外 TCP 中继对长连接不稳定，WS 可能长时间连不上，
        // 轮询保证消息不因 WS 断线而丢失（WS 恢复后实时推送接管，轮询自动跳过）
        _pollTimer?.Dispose();
        _pollTimer = new System.Threading.Timer(_ => PollWhenDisconnected(),
            null, PollIntervalMs, PollIntervalMs);
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
        _pollTimer?.Dispose();
        _pollTimer = null;
        _cts?.Cancel();
        if (_client is not null)
        {
            _client.MessageReceived -= OnRemoteMessage;
            _client.Reconnected -= OnReconnected;
            _client.Dispose();
        }
        _client = null;
        SetStatus("已停用");
        await Task.CompletedTask;
    }

    /// <summary>WS 未连接时（断线/连不上）定期 HTTP 增量拉取兜底，保证消息不因 WS 不稳定而丢失。</summary>
    private void PollWhenDisconnected()
    {
        if (!_running || _client is null || _client.Connected)
            return;
        OnReconnected(); // 复用增量补拉逻辑（幂等，靠 seq 去重）
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
                        var sent = await _client.SendClipAsync(entry.Content);
                        if (!sent)
                            Log.Error($"同步文本发送失败（WS 未连接）: {entry.Content[..Math.Min(entry.Content.Length, 50)]}");
                        else
                            Log.Info($"同步文本已发送: {entry.Content.Length} 字符");
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
            data = full?.Image ?? await ReadAllBytesAsync(entry.Content);
        }
        else
        {
            data = await ReadAllBytesAsync(entry.Content);
        }
        var name = Path.GetFileName(entry.Content);
        var type = entry.Type == "image" ? "clip_image" : "clip_file";
        // 指数退避重试：1s/2s/4s/8s/16s，覆盖境外中继时通时断的窗口；每次失败都记录日志便于排查
        long? mediaId = null;
        for (var attempt = 0; attempt < 5 && mediaId is null; attempt++)
        {
            if (attempt > 0)
                await Task.Delay(TimeSpan.FromSeconds(1 << attempt));
            mediaId = await _client!.UploadMediaAsync(data);
            if (mediaId is null)
                Log.Error($"同步上传媒体失败(第 {attempt + 1} 次): {type} {name} {data.Length} 字节");
        }
        if (mediaId is null)
        {
            Log.Error($"同步上传媒体最终失败，已放弃: {type} {name} {data.Length} 字节");
            return;
        }
        for (var attempt = 0; attempt < 5; attempt++)
        {
            // 图片/文件都用文件名（可读，避免另一端显示 UUID/哈希名）
            if (await _client!.SendClipAsync(type, mediaId.Value, name, data.Length))
            {
                Log.Info($"同步上传成功: {type} {name} {data.Length} 字节, mediaId={mediaId}");
                return;
            }
            Log.Error($"同步 clip_file 消息发送失败(第 {attempt + 1} 次): {type} {name}（WS 未连接，媒体已上传）");
            await Task.Delay(TimeSpan.FromSeconds(1 << attempt));
        }
        Log.Error($"同步 clip_file 消息最终失败，已放弃: {type} {name}（媒体 mediaId={mediaId} 已上传，手动同步可补发）");
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

    /// <summary>WS 连接/重连成功后增量补拉：拉取服务器历史并应用 seq > lastSeq 的消息，
    /// 补上断线窗口期错过的内容（重连只等新消息，不补历史会漏）。服务器 history 按 ts 过滤，
    /// 客户端传 seq 时返回全量，靠 seq 去重与应用幂等（Add 哈希去重 / delete 幂等）兜底。</summary>
    private void OnReconnected()
    {
        if (!_running || _client is null)
            return;
        _ = Task.Run(async () =>
        {
            try
            {
                var history = await _client!.FetchHistoryAsync(_settings.SyncLastSeq);
                if (history is null)
                    return;
                long maxSeq = _settings.SyncLastSeq;
                foreach (var m in history)
                {
                    if (m.Seq > 0 && m.Seq <= _settings.SyncLastSeq)
                        continue; // 已处理过
                    await ApplyRemote(m, isReplay: true); // delete 只来自彻底删除，任何同步都应用
                    if (m.Seq > maxSeq)
                        maxSeq = m.Seq;
                }
                if (maxSeq > _settings.SyncLastSeq)
                {
                    _settings.SyncLastSeq = maxSeq;
                    _settings.Save();
                }
                Log.Info($"同步补拉完成: 处理 {history.Count} 条, lastSeq={maxSeq}");
            }
            catch (Exception ex)
            {
                Log.Error("同步补拉失败", ex);
            }
        });
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
        if (fully && _running && _client is not null)
        {
            // hash 必须在本地删除前计算：_store.Delete 会同步清理图片/文件，之后读不到
            var hash = ComputeSyncHash(entry);
            if (hash is not null)
            {
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
        }
        _store.Delete(entry.Id);
    }

    /// <summary>置顶/取消置顶：本地设置 + 发 pin 消息（跨端同步）。</summary>
    public void SetPinned(Entry entry, bool pinned)
    {
        if (_running && _client is not null)
        {
            var hash = ComputeSyncHash(entry);
            if (hash is not null)
                _ = Task.Run(async () =>
                {
                    for (var attempt = 0; attempt < 3; attempt++)
                    {
                        if (await _client.SendPinAsync(hash, pinned))
                            return;
                        await Task.Delay(TimeSpan.FromSeconds(1 << attempt));
                    }
                });
        }
        _store.SetPinned(entry.Id, pinned);
    }

    /// <summary>清空历史。fully=false 仅本机；fully=true 发 clear 标记（其他设备/服务器随后清除）。置顶条目均保留。</summary>
    public void ClearAll(bool fully)
    {
        if (fully && _running && _client is not null)
            _ = Task.Run(async () =>
            {
                for (var attempt = 0; attempt < 3; attempt++)
                {
                    if (await _client.SendClearAsync())
                        return;
                    await Task.Delay(TimeSpan.FromSeconds(1 << attempt));
                }
            });
        _store.Clear();
    }

    /// <summary>跨端同步身份 = 内容哈希。优先用入库时记录的内容哈希（entry.Hash）——
    /// 条目文件被外部程序（如 WPS 预览重存）改写后仍与服务器/手机端一致；
    /// 旧数据若存的是路径降级哈希（sha256(type\0content)，历史版本文件不可读时产生）则识别丢弃，回退按当前内容计算。</summary>
    private string? ComputeSyncHash(Entry entry)
    {
        if (!string.IsNullOrEmpty(entry.Hash))
        {
            if (entry.Type == "text")
                return entry.Hash.ToLowerInvariant();
            if (entry.Type is "file" or "image")
            {
                var pathFallback = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(entry.Type + "\0" + entry.Content))).ToLowerInvariant();
                if (!entry.Hash.Equals(pathFallback, StringComparison.OrdinalIgnoreCase))
                    return entry.Hash.ToLowerInvariant();
            }
        }
        // 回退：按当前内容计算（text 用内容；file/image 读文件字节）
        if (entry.Type == "text")
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("text\0" + entry.Content))).ToLowerInvariant();
        if (entry.Type == "image")
        {
            var full = _store.GetById(entry.Id); // 列表条目不含原图，需取完整条目（旧数据 BLOB/文件兜底）
            if (full?.Image is not null)
                return Convert.ToHexString(SHA256.HashData(full.Image)).ToLowerInvariant();
            if (full is not null && !string.IsNullOrEmpty(full.Content) && File.Exists(full.Content))
                return Convert.ToHexString(SHA256.HashData(ReadAllBytes(full.Content))).ToLowerInvariant();
            return null;
        }
        if (entry.Type == "file")
        {
            // 文件按内容字节哈希（与手机端一致）：服务器 clip_file 消息不带 hash，需本地读文件计算
            if (!File.Exists(entry.Content))
                return null;
            return Convert.ToHexString(SHA256.HashData(ReadAllBytes(entry.Content))).ToLowerInvariant();
        }
        return null;
    }

    private async Task ApplyRemote(SyncMessage m, bool isReplay)
    {
        switch (m.Type)
        {
            case "delete" when !string.IsNullOrEmpty(m.Hash):
                _store.DeleteByHash(m.Hash!);
                break;
            case "pin" when !string.IsNullOrEmpty(m.Hash) && m.Pinned is not null:
                _store.SetPinnedByHash(m.Hash!, m.Pinned.Value);
                break;
            case "clear":
                _store.Clear();
                break;
            case "clip_text" when !string.IsNullOrEmpty(m.Text):
            {
                // 自己发的消息（HTTP 轮询/重连补拉会把本设备历史拉回来）标 source=local，
                // 否则本机历史会以 phone 身份重复入库（重大 bug：电脑端列表全是"手机"来源）
                var entry = new Entry
                {
                    Type = "text",
                    Content = m.Text,
                    Source = m.OriginDeviceId == _settings.SyncDeviceId ? "local" : "phone",
                    CreatedAt = m.Ts > 0 ? m.Ts / 1000 : DateTimeOffset.UtcNow.ToUnixTimeSeconds(), // 服务器 ts 为毫秒，本地库用秒
                };
                _store.Add(entry);
                break;
            }
            case "clip_image" when m.MediaId is not null:
                await ApplyRemoteMedia(m, "image");
                break;
            case "clip_file" when m.MediaId is not null:
                // 手机端常把图片按文件发送（分享/保存到相册后复制的是文件而非剪贴板位图），
                // 收到的是 clip_file 但扩展名是图片：按图片入库并生成缩略图，否则显示在"文件"分类、无预览。
                // 2026-08-21 实测：手机发 Image_xxx.jpg 作为 clip_file，Windows 误存 file 条目。
                await ApplyRemoteMedia(m, IsImageName(m.Name) ? "image" : "file");
                break;
        }
    }

    private async Task ApplyRemoteMedia(SyncMessage m, string type)
    {
        var bytes = _client is null ? null : await _client.DownloadMediaAsync(long.Parse(m.MediaId!));
        if (bytes is null || bytes.Length == 0)
            return;
        var safeName = SanitizeName(m.Name ?? (type == "image" ? "image.png" : "file.bin"));
        // 图片/文件都用服务器传来的原始文件名（分享图片有 DISPLAY_NAME 原名）；仅本地剪贴板位图无名字时走时间戳命名
        var localPath = type == "image" && string.IsNullOrEmpty(m.Name)
            ? _store.SaveImageFile(bytes)
            : type == "image"
                ? _store.SaveImageFileAs(safeName, bytes)
                : Path.Combine(_filesDir, UniqueFileName(_filesDir, safeName));
        if (type == "file")
            File.WriteAllBytes(localPath, bytes);
        var entry = new Entry
        {
            Type = type,
            Content = localPath,
            Source = m.OriginDeviceId == _settings.SyncDeviceId ? "local" : "phone",
            Image = type == "image" ? bytes : null, // 仅用于内容哈希去重（跨回放同一图片不重复入库）
            Thumb = type == "image" ? MakeThumbBytes(bytes) : null,
            CreatedAt = m.Ts > 0 ? m.Ts / 1000 : DateTimeOffset.UtcNow.ToUnixTimeSeconds(), // 服务器 ts 为毫秒，本地库用秒
        };
        if (!_store.Add(entry))
        {
            try { File.Delete(localPath); } catch (IOException) { } // 去重未新增时清理残留文件
        }
    }

    /// <summary>按扩展名判断是否为图片（手机端分享/复制图片常以 clip_file 携带 .jpg/.png 等）。</summary>
    private static bool IsImageName(string? name)
    {
        if (string.IsNullOrEmpty(name))
            return false;
        var ext = Path.GetExtension(name).ToLowerInvariant();
        return ext is ".jpg" or ".jpeg" or ".png" or ".gif" or ".bmp" or ".webp" or ".tiff" or ".tif";
    }

    private static string SanitizeName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
    }

    /// 优先使用原始文件名；仅当目录中已存在同名文件时才追加 (1)/(2) 后缀，避免默认带哈希前缀。
    private static string UniqueFileName(string dir, string name)
    {
        var path = Path.Combine(dir, name);
        if (!File.Exists(path)) return path;
        var stem = Path.GetFileNameWithoutExtension(name);
        var ext = Path.GetExtension(name);
        for (var i = 1; ; i++)
        {
            var candidate = Path.Combine(dir, $"{stem} ({i}){ext}");
            if (!File.Exists(candidate)) return candidate;
        }
    }

    private static byte[]? MakeThumbBytes(byte[] png)
    {
        try
        {
            var src = ClipboardMonitor.DecodePng(png);
            // alpha 全 0（剪贴板 DIB 不可信 alpha / 历史坏数据）→ 转不透明，缩略图才不会全透明
            return ClipboardMonitor.EncodePng(ClipboardMonitor.MakeThumb(ClipboardMonitor.FixUntrustedAlpha(src), 200));
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// 启动时修复图片条目：旧版本按 PNG 硬解码，手机端分享的 JPEG（存 .png 扩展名）解码失败
    /// 导致 thumb 为空、列表无预览；DecodePng 改为自动识别格式后，启动时对历史数据补生成缩略图。
    /// 另修复剪贴板 DIB alpha 不可信的历史数据：缩略图全透明（alpha 全 0）时重生成并重写原图为不透明。
    public void RepairMissingThumbs()
    {
        foreach (var (id, content, thumb) in _store.GetAllImages())
        {
            try
            {
                if (string.IsNullOrEmpty(content) || !File.Exists(content)) continue;
                // 已有正常缩略图（存在非全透明 alpha）则跳过；全透明/缺失才重生成
                if (thumb is not null && ClipboardMonitor.HasAlphaChannel(ClipboardMonitor.DecodePng(thumb)))
                    continue;
                var bytes = ReadAllBytes(content);
                var src = ClipboardMonitor.DecodePng(bytes);
                // 原图 alpha 全 0 → 重写为不透明 PNG，粘贴到其他应用也恢复正常
                var fixedSrc = ClipboardMonitor.FixUntrustedAlpha(src);
                if (!ReferenceEquals(fixedSrc, src))
                    File.WriteAllBytes(content, ClipboardMonitor.EncodePng(fixedSrc));
                var newThumb = ClipboardMonitor.EncodePng(ClipboardMonitor.MakeThumb(fixedSrc, 200));
                _store.UpdateThumb(id, newThumb);
            }
            catch (Exception)
            {
                // 单个条目修复失败不影响其他
            }
        }
    }

    public void Dispose()
    {
        _cts?.Cancel();
        if (_client is not null)
        {
            _client.MessageReceived -= OnRemoteMessage;
            _client.Reconnected -= OnReconnected;
            _client.Dispose();
        }
        _monitor.EntryCaptured -= OnLocalCaptured;
    }
}