using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ClipboardTool;

/// <summary>
/// 事件驱动的剪贴板监听：向隐藏窗口注册 AddClipboardFormatListener，
/// 收到 WM_CLIPBOARDUPDATE 后按 图片 → 文件 → 文本 优先级捕获。
/// 文件优先于文本：WPS 等复制文件时会把文件名/路径作为 UnicodeText 一起放入剪贴板，
/// 若文本优先会把文件误存成文本条目（2026-08-20 实测 WPS 复制 docx 只存了文件名文本）。
/// </summary>
public sealed class ClipboardMonitor
{
    private readonly ClipboardStore _store;
    private HwndSource? _source;
    private bool _paused;

    /// <summary>粘贴器写入剪贴板时置位，抑制随后的监听通知，避免自记自写。</summary>
    public bool SuppressNext { get; set; }

    /// <summary>本地捕获入库成功后触发（同步模块据此上传）。</summary>
    public event Action<Entry>? EntryCaptured;

    private readonly System.Windows.Threading.DispatcherTimer _retryTimer;
    private string? _retryPath;
    private int _retryCount;
    private const int MaxFileRetries = 5; // 文件被占用/OneDrive 云占位时最多重试次数

    public ClipboardMonitor(ClipboardStore store)
    {
        _store = store;
        _retryTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1500) };
        _retryTimer.Tick += (_, _) => RetryFileCapture();
    }

    public bool Paused
    {
        get => _paused;
        set
        {
            _paused = value;
            if (value) SuppressNext = false;
        }
    }

    /// <summary>挂到 WPF 窗口的消息钩子上并注册监听。</summary>
    public void Start(Window owner)
    {
        var source = HwndSource.FromHwnd(new WindowInteropHelper(owner).EnsureHandle())!;
        source.AddHook(Hook);
        _source = source;
        NativeMethods.AddClipboardFormatListener(source.Handle);
    }

    public void Stop()
    {
        if (_source is null)
            return;
        NativeMethods.RemoveClipboardFormatListener(_source.Handle);
        _source.RemoveHook(Hook);
        _source = null;
    }

    private IntPtr Hook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == NativeMethods.WM_CLIPBOARDUPDATE)
            OnClipboardUpdate();
        return IntPtr.Zero;
    }

    private void OnClipboardUpdate()
    {
        if (_paused || SuppressNext)
        {
            SuppressNext = false;
            return;
        }

        try
        {
            Capture();
        }
        catch (Exception ex)
        {
            // 剪贴板被其他进程锁定或格式无法读取时跳过本次
            Log.Error("剪贴板捕获失败", ex);
        }
    }

    private void Capture()
    {
        var data = System.Windows.Clipboard.GetDataObject();

        // 图片优先：资源管理器复制图片时剪贴板同时含 Bitmap 与 FileDrop，按图片保存并保留原名
        if (data.GetDataPresent(DataFormats.Bitmap))
        {
            if (data.GetData(DataFormats.Bitmap) is BitmapSource bmp && bmp.PixelWidth > 0)
            {
                // 剪贴板 32bpp DIB 位图的 alpha 通道常不可信（来源复制时 alpha 全 0 但 RGB 有效），
                // 原样保存会导致 PNG 与缩略图全透明、WPF 渲染空白（查看器忽略 alpha 显示正常）
                bmp = FixUntrustedAlpha(bmp);
                // 原图保存为 PNG 文件（Content 记录文件路径，粘贴纯文本时可给出路径）
                var png = EncodePng(bmp);
                var thumb = EncodePng(MakeThumb(bmp, 200));
                Log.Info($"捕获图片 {bmp.PixelWidth}x{bmp.PixelHeight}，PNG {png.Length / 1024}KB");
                // 资源管理器复制图片时剪贴板同时含 FileDrop（真实文件名）与 Bitmap，优先用原名
                var path = data.GetDataPresent(DataFormats.FileDrop)
                    && data.GetData(DataFormats.FileDrop) is string[] dropFiles && dropFiles.Length > 0
                    ? SaveImageWithName(dropFiles[0], png)
                    : _store.SaveImageFile(png);
                var entry = new Entry
                {
                    Type = "image",
                    Content = path,
                    Image = png, // 仅用于内容哈希去重，不入库
                    Thumb = thumb,
                    CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                };
                var added = _store.Add(entry);
                if (added)
                    EntryCaptured?.Invoke(entry);
                else
                {
                    try { File.Delete(path); } catch (IOException) { }
                }
            }
            return;
        }

        // 文件优先于文本：WPS 等复制文件时剪贴板同时含文件名文本与 FileDrop，
        // 文本优先会把文件误存成文本条目（2026-08-20 实测 WPS 复制 docx 只存了文件名）
        if (data.GetDataPresent(DataFormats.FileDrop))
        {
            if (data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0)
            {
                var entry = new Entry { Type = "file", Content = files[0], CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds() };
                if (_store.Add(entry))
                {
                    EntryCaptured?.Invoke(entry);
                    Log.Info($"捕获文件 {files[0]}");
                }
                else if (File.Exists(files[0]) && !IsFileReadable(files[0]))
                {
                    // 文件存在但暂不可读（被占用/OneDrive 云占位）：静默跳过会让用户以为没捕获到，安排重试
                    Log.Info($"捕获文件暂不可读，安排重试: {files[0]}");
                    ScheduleFileRetry(files[0]);
                }
                else
                {
                    Log.Info($"捕获文件已存在（去重移顶）: {files[0]}");
                }
            }
            return;
        }

        if (data.GetDataPresent(DataFormats.UnicodeText))
        {
            var text = data.GetData(DataFormats.UnicodeText) as string;
            if (!string.IsNullOrEmpty(text))
            {
                var entry = new Entry { Type = "text", Content = text, CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds() };
                if (_store.Add(entry))
                    EntryCaptured?.Invoke(entry);
                Log.Info($"捕获文本 {text.Length} 字符");
            }
        }
    }

    private static bool IsFileReadable(string path)
    {
        try
        {
            // FileShare.ReadWrite：WPS 等以 ReadWrite 访问 + FileShare.Read 独占写打开文件时，
            // File.OpenRead 的 FileShare.Read 共享声明会与之冲突（实测 WPS 打开的 docx 读不了）
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>文件被占用/云占位时延迟重试捕获（最多 MaxFileRetries 次，每次间隔 1.5s）。</summary>
    private void ScheduleFileRetry(string path)
    {
        _retryPath = path;
        _retryCount = 0;
        _retryTimer.Stop();
        _retryTimer.Start();
    }

    private void RetryFileCapture()
    {
        _retryTimer.Stop();
        if (_retryPath is null)
            return;
        var path = _retryPath;
        var entry = new Entry { Type = "file", Content = path, CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds() };
        if (_store.Add(entry))
        {
            _retryPath = null;
            EntryCaptured?.Invoke(entry);
            Log.Info($"捕获文件（重试成功） {path}");
            return;
        }
        if (++_retryCount >= MaxFileRetries)
        {
            Log.Info($"捕获文件重试放弃（文件仍不可读）: {path}");
            _retryPath = null;
            return;
        }
        _retryTimer.Start();
    }

    internal static byte[] EncodePng(BitmapSource src)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(src));
        using var ms = new MemoryStream();
        encoder.Save(ms);
        return ms.ToArray();
    }

    /// <summary>按原始文件名保存图片（资源管理器复制图片时剪贴板同时含 FileDrop，用它拿真实文件名）。</summary>
    private string SaveImageWithName(string originalPath, byte[] png)
        => _store.SaveImageFileAs(Path.GetFileName(originalPath), png);

    /// <summary>等比缩放到最长边不超过 max，超过才缩放。</summary>
    internal static BitmapSource MakeThumb(BitmapSource src, double max)
    {
        var longest = Math.Max(src.PixelWidth, src.PixelHeight);
        if (longest <= max)
            return src;
        var scale = max / longest;
        return new TransformedBitmap(src, new ScaleTransform(scale, scale));
    }

    /// <summary>byte[] 图片（PNG/JPEG 等，按文件头自动识别格式）→ BitmapSource（供回贴与 UI 展示）。</summary>
    internal static BitmapSource DecodePng(byte[] bytes)
    {
        using var ms = new MemoryStream(bytes);
        // BitmapDecoder.Create 按文件头自动选择解码器：手机端分享的图片常是 JPEG 但存成 .png 扩展名，
        // 若固定用 PngBitmapDecoder 会解码失败导致缩略图为空
        var decoder = BitmapDecoder.Create(ms, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        return decoder.Frames[0];
    }

    /// <summary>剪贴板 32bpp DIB 位图的 alpha 通道常不可信：部分来源复制时 alpha 全 0 但 RGB 内容有效，
    /// 查看器/画图忽略 alpha 显示正常，WPF 渲染则全透明。检测 alpha 全 0 时丢弃 alpha 转不透明 Bgr24；
    /// 存在非 0 alpha（真透明图）则原样保留。</summary>
    internal static BitmapSource FixUntrustedAlpha(BitmapSource src)
    {
        if (HasAlphaChannel(src))
            return src;
        var opaque = new FormatConvertedBitmap(src, PixelFormats.Bgr24, null, 0);
        opaque.Freeze();
        return opaque;
    }

    /// <summary>检测位图 alpha 通道是否存在非 0 像素（全 0 视为 alpha 不可信/全透明，无 alpha 格式视为不透明）。</summary>
    internal static bool HasAlphaChannel(BitmapSource src)
    {
        if (src.Format != PixelFormats.Bgra32 && src.Format != PixelFormats.Pbgra32)
            return true; // 无 alpha 通道（Bgr24 等）视为不透明，无需处理
        var w = src.PixelWidth;
        var h = src.PixelHeight;
        var stride = (w * 4 + 3) / 4 * 4;
        var buf = new byte[stride * h];
        src.CopyPixels(new Int32Rect(0, 0, w, h), buf, stride, 0);
        for (var i = 3; i < buf.Length; i += 4)
        {
            if (buf[i] != 0)
                return true;
        }
        return false;
    }
}