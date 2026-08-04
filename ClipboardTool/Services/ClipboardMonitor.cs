using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ClipboardTool;

/// <summary>
/// 事件驱动的剪贴板监听：向隐藏窗口注册 AddClipboardFormatListener，
/// 收到 WM_CLIPBOARDUPDATE 后按 文本 → 图片 → 文件 优先级捕获。
/// </summary>
public sealed class ClipboardMonitor
{
    private readonly ClipboardStore _store;
    private HwndSource? _source;
    private bool _paused;

    /// <summary>粘贴器写入剪贴板时置位，抑制随后的监听通知，避免自记自写。</summary>
    public bool SuppressNext { get; set; }

    public ClipboardMonitor(ClipboardStore store) => _store = store;

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
        catch (Exception)
        {
            // 剪贴板被其他进程锁定或格式无法读取时跳过本次
        }
    }

    private void Capture()
    {
        var data = System.Windows.Clipboard.GetDataObject();

        if (data.GetDataPresent(DataFormats.UnicodeText))
        {
            var text = data.GetData(DataFormats.UnicodeText) as string;
            if (!string.IsNullOrEmpty(text))
                _store.Add(new Entry { Type = "text", Content = text, CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds() });
            return;
        }

        if (data.GetDataPresent(DataFormats.Bitmap))
        {
            if (data.GetData(DataFormats.Bitmap) is BitmapSource bmp && bmp.PixelWidth > 0)
            {
                var full = EncodePng(bmp);
                var thumb = EncodePng(MakeThumb(bmp, 200));
                _store.Add(new Entry
                {
                    Type = "image",
                    Content = $"{bmp.PixelWidth}x{bmp.PixelHeight}",
                    Image = full,
                    Thumb = thumb,
                    CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                });
            }
            return;
        }

        if (data.GetDataPresent(DataFormats.FileDrop))
        {
            if (data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0)
                _store.Add(new Entry { Type = "file", Content = files[0], CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds() });
        }
    }

    internal static byte[] EncodePng(BitmapSource src)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(src));
        using var ms = new MemoryStream();
        encoder.Save(ms);
        return ms.ToArray();
    }

    /// <summary>等比缩放到最长边不超过 max，超过才缩放。</summary>
    internal static BitmapSource MakeThumb(BitmapSource src, double max)
    {
        var longest = Math.Max(src.PixelWidth, src.PixelHeight);
        if (longest <= max)
            return src;
        var scale = max / longest;
        return new TransformedBitmap(src, new ScaleTransform(scale, scale));
    }

    /// <summary>byte[] PNG → BitmapSource（供回贴与 UI 展示）。</summary>
    internal static BitmapSource DecodePng(byte[] png)
    {
        using var ms = new MemoryStream(png);
        var decoder = new PngBitmapDecoder(ms, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        return decoder.Frames[0];
    }
}
