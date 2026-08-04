using System.Windows;
using System.Windows.Media.Imaging;

namespace ClipboardTool;

/// <summary>把条目写入剪贴板并向目标窗口模拟 Ctrl+V 完成粘贴。</summary>
public static class Paster
{
    /// <summary>粘贴前由调用方保存的目标窗口（Overlay 弹出前的前台窗口）。</summary>
    public static IntPtr TargetWindow { get; set; }

    public static void Paste(ClipboardMonitor monitor, Entry entry, bool plainTextOnly)
    {
        NativeMethods.SetForegroundWindow(TargetWindow);
        Thread.Sleep(60);

        monitor.SuppressNext = true;
        try
        {
            switch (entry.Type)
            {
                case "image" when entry.Image is not null:
                    System.Windows.Clipboard.SetImage(ClipboardMonitor.DecodePng(entry.Image));
                    break;
                case "file":
                    var data = new DataObject();
                    data.SetData(DataFormats.FileDrop, new[] { entry.Content });
                    System.Windows.Clipboard.SetDataObject(data, true);
                    break;
                default:
                    System.Windows.Clipboard.SetText(entry.Content, TextDataFormat.UnicodeText);
                    break;
            }
        }
        catch (Exception)
        {
            // 剪贴板被占用时放弃写入，不继续模拟按键；复位抑制标志避免吞掉下一次捕获
            monitor.SuppressNext = false;
            return;
        }

        Thread.Sleep(60);
        NativeMethods.keybd_event(NativeMethods.VK_CONTROL, 0, 0, UIntPtr.Zero);
        NativeMethods.keybd_event(NativeMethods.VK_V, 0, 0, UIntPtr.Zero);
        NativeMethods.keybd_event(NativeMethods.VK_V, 0, NativeMethods.KEYEVENTF_KEYUP, UIntPtr.Zero);
        NativeMethods.keybd_event(NativeMethods.VK_CONTROL, 0, NativeMethods.KEYEVENTF_KEYUP, UIntPtr.Zero);
    }
}
