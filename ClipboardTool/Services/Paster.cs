using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;
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
                WriteClipboardWin32(entry);
                Thread.Sleep(60);
                SendCtrlV();
            }
            catch (Exception)
            {
                // 剪贴板持续被占用：复位抑制标志避免吞掉下一次捕获
                monitor.SuppressNext = false;
            }
        });
        worker.SetApartmentState(ApartmentState.STA);
        worker.IsBackground = true;
        worker.Start();
    }

    // ---- Win32 剪贴板写入（带重试） ----

    private static void WriteClipboardWin32(Entry entry)
    {
        Exception? last = null;
        for (var i = 0; i < 5; i++)
        {
            try
            {
                if (entry.Type == "image" && entry.Image is not null)
                    SetClipboardImage(entry.Image);
                else if (entry.Type == "file")
                    SetClipboardFiles(entry.Content);
                else
                    SetClipboardText(entry.Content);
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
        var h = ConvertToDib(bmp);
        try
        {
            if (!NativeMethods.OpenClipboard(IntPtr.Zero))
                throw new InvalidOperationException("OpenClipboard 失败");
            try
            {
                NativeMethods.EmptyClipboard();
                if (NativeMethods.SetClipboardData(NativeMethods.CF_DIB, h) == IntPtr.Zero)
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

    /// <summary>PNG BLOB → System.Drawing.Bitmap。</summary>
    private static Bitmap BitmapFromPng(byte[] png)
    {
        using var ms = new MemoryStream(png);
        var decoder = new PngBitmapDecoder(ms, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        var src = decoder.Frames[0];

        using var outMs = new MemoryStream();
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(src));
        encoder.Save(outMs);
        outMs.Position = 0;
        return new Bitmap(outMs);
    }

    /// <summary>Bitmap → 32bpp DIB（BITMAPINFOHEADER + 自底向上像素数据）。返回的句柄由调用方管理。</summary>
    private static IntPtr ConvertToDib(Bitmap bmp)
    {
        const int headerSize = 40; // BITMAPINFOHEADER
        var rect = new Rectangle(0, 0, bmp.Width, bmp.Height);
        var bmpData = bmp.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
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
