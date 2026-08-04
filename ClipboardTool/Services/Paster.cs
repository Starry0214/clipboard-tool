using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Media.Imaging;

namespace ClipboardTool;

/// <summary>
/// 把条目写入剪贴板并向目标窗口模拟 Ctrl+V 完成粘贴。
/// 文本/文件走 Win32 原生剪贴板（后台线程，UI 零阻塞、失败即释放）；
/// 图片走 UI 线程 WPF 剪贴板（OLE 就绪，带重试）。
/// </summary>
public static class Paster
{
    /// <summary>粘贴前由调用方保存的目标窗口（Overlay 弹出前的前台窗口）。</summary>
    public static IntPtr TargetWindow { get; set; }

    public static void Paste(ClipboardMonitor monitor, Entry entry, bool plainTextOnly)
    {
        monitor.SuppressNext = true;
        var target = TargetWindow;

        if (entry.Type == "image" && entry.Image is not null)
        {
            // 图片：UI 线程 WPF 写入（OLE/消息泵就绪最可靠）
            NativeMethods.SetForegroundWindow(target);
            Thread.Sleep(30);
            try
            {
                WriteClipboard(() => System.Windows.Clipboard.SetImage(ClipboardMonitor.DecodePng(entry.Image)));
            }
            catch (Exception)
            {
                monitor.SuppressNext = false;
                return;
            }
            SendCtrlVAsync();
            return;
        }

        // 文本/文件：后台线程 Win32 写入（无需 OLE/消息泵，UI 零阻塞）
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

    // ---- Win32 剪贴板（文本/文件） ----

    private static void WriteClipboardWin32(Entry entry)
    {
        Exception? last = null;
        for (var i = 0; i < 5; i++)
        {
            try
            {
                if (entry.Type == "file")
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

    // ---- WPF 剪贴板（图片）带重试 ----

    private static void WriteClipboard(Action write)
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

    // ---- 模拟 Ctrl+V ----

    private static void SendCtrlVAsync()
    {
        var worker = new Thread(() =>
        {
            try
            {
                Thread.Sleep(60);
                SendCtrlV();
            }
            catch (Exception)
            {
            }
        });
        worker.IsBackground = true;
        worker.Start();
    }

    private static void SendCtrlV()
    {
        NativeMethods.keybd_event(NativeMethods.VK_CONTROL, 0, 0, UIntPtr.Zero);
        NativeMethods.keybd_event(NativeMethods.VK_V, 0, 0, UIntPtr.Zero);
        NativeMethods.keybd_event(NativeMethods.VK_V, 0, NativeMethods.KEYEVENTF_KEYUP, UIntPtr.Zero);
        NativeMethods.keybd_event(NativeMethods.VK_CONTROL, 0, NativeMethods.KEYEVENTF_KEYUP, UIntPtr.Zero);
    }
}
