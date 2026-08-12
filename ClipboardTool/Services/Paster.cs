using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ClipboardTool;

/// <summary>
/// 把条目写入剪贴板并向目标窗口模拟 Ctrl+V 完成粘贴。
/// 全部在后台线程执行（UI 零阻塞），统一走 Win32 原生剪贴板（OpenClipboard/SetClipboardData，
/// 同步复制、失败即释放，无 OLE 滞留问题）：文本=CF_UNICODETEXT、文件=CF_HDROP、图片=CF_DIB。
/// </summary>
public static class Paster
{
    /// <summary>粘贴前由调用方保存的目标窗口（Overlay 弹出前的前台窗口）。</summary>
    public static IntPtr TargetWindow { get; set; }

    public static void Paste(ClipboardMonitor monitor, Entry entry, bool plainTextOnly)
    {
        monitor.SuppressNext = true;
        var target = TargetWindow;

        var worker = new Thread(() =>
        {
            try
            {
                NativeMethods.SetForegroundWindow(target);
                Thread.Sleep(30);
                if (plainTextOnly)
                    Retry(() => SetClipboardText(entry.Content)); // 强制纯文本：文本=内容、文件=路径、图片=尺寸
                else
                    WriteClipboardWin32(entry);
                Thread.Sleep(60);
                SendCtrlV();
            }
            catch (Exception ex)
            {
                // 剪贴板持续被占用：复位抑制标志避免吞掉下一次捕获
                monitor.SuppressNext = false;
                Log.Error($"粘贴失败（类型 {entry.Type}）", ex);
            }
        });
        worker.SetApartmentState(ApartmentState.STA);
        worker.IsBackground = true;
        worker.Start();
    }

    // ---- Win32 剪贴板写入（带重试） ----

    private static void WriteClipboardWin32(Entry entry) => Retry(() =>
    {
        if (entry.Type == "image" && entry.Image is not null)
            SetClipboardImage(entry.Image);
        else if (entry.Type == "file")
            SetClipboardFiles(entry.Content);
        else
            SetClipboardText(entry.Content);
    });

    private static void Retry(Action write)
    {
        Exception? last = null;
        for (var i = 0; i < 5; i++)
        {
            try
            {
                write();
                return;
            }
            catch (Exception ex)
            {
                last = ex;
                Thread.Sleep(100);
            }
        }
        throw last ?? new InvalidOperationException("剪贴板写入失败");
    }

    private static void SetClipboardText(string text)
    {
        if (!NativeMethods.OpenClipboard(IntPtr.Zero))
            throw new InvalidOperationException("OpenClipboard 失败");
        try
        {
            NativeMethods.EmptyClipboard();
            var h = Marshal.StringToHGlobalUni(text);
            if (NativeMethods.SetClipboardData(NativeMethods.CF_UNICODETEXT, h) == IntPtr.Zero)
            {
                Marshal.FreeHGlobal(h);
                throw new InvalidOperationException("SetClipboardData 失败");
            }
        }
        finally
        {
            NativeMethods.CloseClipboard();
        }
    }

    private static void SetClipboardFiles(string path)
    {
        // DROPFILES 结构（20 字节头）+ UTF-16 路径序列（双 null 结尾）
        var pathBytes = Encoding.Unicode.GetBytes(path + "\0\0");
        var size = 20 + pathBytes.Length;
        var h = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.WriteInt32(h, 0, 20); // pFiles 偏移
            Marshal.WriteInt32(h, 16, 1); // fWide = TRUE
            Marshal.Copy(pathBytes, 0, h + 20, pathBytes.Length);

            if (!NativeMethods.OpenClipboard(IntPtr.Zero))
                throw new InvalidOperationException("OpenClipboard 失败");
            try
            {
                NativeMethods.EmptyClipboard();
                if (NativeMethods.SetClipboardData(NativeMethods.CF_HDROP, h) == IntPtr.Zero)
                    throw new InvalidOperationException("SetClipboardData 失败");
                h = IntPtr.Zero; // 系统接管内存
            }
            finally
            {
                NativeMethods.CloseClipboard();
            }
        }
        finally
        {
            if (h != IntPtr.Zero)
                Marshal.FreeHGlobal(h);
        }
    }

    private static void SetClipboardImage(byte[] png)
    {
        using var bmp = BitmapFromPng(png);
        var hDib = ConvertToDib(bmp);
        var hPng = CopyBytesToHGlobal(png);
        try
        {
            if (!NativeMethods.OpenClipboard(IntPtr.Zero))
                throw new InvalidOperationException("OpenClipboard 失败");
            try
            {
                NativeMethods.EmptyClipboard();
                // 同时提供 CF_DIB 与 CF_PNG，兼容不同应用对图片格式的识别
                if (NativeMethods.SetClipboardData(NativeMethods.CF_DIB, hDib) == IntPtr.Zero)
                    throw new InvalidOperationException("SetClipboardData(DIB) 失败");
                hDib = IntPtr.Zero; // 系统接管内存
                var pngFormat = NativeMethods.RegisterClipboardFormat("PNG");
                if (NativeMethods.SetClipboardData(pngFormat, hPng) == IntPtr.Zero)
                    throw new InvalidOperationException("SetClipboardData(PNG) 失败");
                hPng = IntPtr.Zero;
            }
            finally
            {
                NativeMethods.CloseClipboard();
            }
        }
        finally
        {
            if (hDib != IntPtr.Zero)
                Marshal.FreeHGlobal(hDib);
            if (hPng != IntPtr.Zero)
                Marshal.FreeHGlobal(hPng);
        }
    }

    private static IntPtr CopyBytesToHGlobal(byte[] bytes)
    {
        var h = Marshal.AllocHGlobal(bytes.Length);
        Marshal.Copy(bytes, 0, h, bytes.Length);
        return h;
    }

    /// <summary>图片 BLOB（PNG/JPEG 等，按文件头自动识别格式）→ System.Drawing.Bitmap。
    /// 手机端分享的图片常是 JPEG 但存成 .png 扩展名，固定用 PngBitmapDecoder 会解码失败导致粘贴无反应。</summary>
    private static Bitmap BitmapFromPng(byte[] bytes)
    {
        using var ms = new MemoryStream(bytes);
        var decoder = BitmapDecoder.Create(ms, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        return BitmapFromSource(decoder.Frames[0]);
    }

    /// <summary>BitmapSource → 32bppArgb Bitmap：直接拷贝像素，跳过 PNG 重编码（大图 PNG 编码是粘贴卡顿主因）。</summary>
    private static Bitmap BitmapFromSource(BitmapSource src)
    {
        var src32 = new FormatConvertedBitmap(src, PixelFormats.Bgra32, null, 0);
        src32.Freeze();
        var w = src32.PixelWidth;
        var h = src32.PixelHeight;
        var bmp = new Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        var data = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        try
        {
            src32.CopyPixels(new Int32Rect(0, 0, w, h), data.Scan0, data.Stride * h, data.Stride);
        }
        finally
        {
            bmp.UnlockBits(data);
        }
        return bmp;
    }

    /// <summary>Bitmap → 32bpp DIB（BITMAPINFOHEADER + 自底向上像素数据）。返回的句柄由调用方管理。</summary>
    private static IntPtr ConvertToDib(Bitmap bmp)
    {
        const int headerSize = 40; // BITMAPINFOHEADER
        var rect = new Rectangle(0, 0, bmp.Width, bmp.Height);
        var bmpData = bmp.LockBits(rect, ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        try
        {
            var stride = Math.Abs(bmpData.Stride);
            var pixelBytes = stride * bmp.Height;
            var h = Marshal.AllocHGlobal(headerSize + pixelBytes);

            Marshal.WriteInt32(h, 0, headerSize);      // biSize
            Marshal.WriteInt32(h, 4, bmp.Width);       // biWidth
            Marshal.WriteInt32(h, 8, bmp.Height);      // biHeight（正=自底向上）
            Marshal.WriteInt16(h, 12, 1);              // biPlanes
            Marshal.WriteInt16(h, 14, 32);             // biBitCount
            Marshal.WriteInt32(h, 16, 0);              // biCompression = BI_RGB
            Marshal.WriteInt32(h, 20, pixelBytes);     // biSizeImage

            // 逐行复制并垂直翻转（DIB 自底向上）
            var rowBuf = new byte[stride];
            for (var y = 0; y < bmp.Height; y++)
            {
                Marshal.Copy(bmpData.Scan0 + y * bmpData.Stride, rowBuf, 0, stride);
                Marshal.Copy(rowBuf, 0, h + headerSize + (bmp.Height - 1 - y) * stride, stride);
            }
            return h;
        }
        finally
        {
            bmp.UnlockBits(bmpData);
        }
    }

    // ---- 模拟 Ctrl+V ----

    private static void SendCtrlV()
    {
        NativeMethods.keybd_event(NativeMethods.VK_CONTROL, 0, 0, UIntPtr.Zero);
        NativeMethods.keybd_event(NativeMethods.VK_V, 0, 0, UIntPtr.Zero);
        NativeMethods.keybd_event(NativeMethods.VK_V, 0, NativeMethods.KEYEVENTF_KEYUP, UIntPtr.Zero);
        NativeMethods.keybd_event(NativeMethods.VK_CONTROL, 0, NativeMethods.KEYEVENTF_KEYUP, UIntPtr.Zero);
    }
}
