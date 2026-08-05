using System.Runtime.InteropServices;

namespace ClipboardTool;

internal static class NativeMethods
{
    // ---- 剪贴板监听 ----
    [DllImport("user32.dll")]
    internal static extern bool AddClipboardFormatListener(IntPtr hwnd);

    [DllImport("user32.dll")]
    internal static extern bool RemoveClipboardFormatListener(IntPtr hwnd);

    // ---- 全局热键 ----
    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    // ---- 前台窗口 / 按键模拟 ----
    [DllImport("user32.dll")]
    internal static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    internal static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    internal static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

    // ---- 屏幕坐标 ----
    [DllImport("user32.dll")]
    internal static extern bool GetCursorPos(out POINT pt);

    // ---- 窗口定位（物理像素） ----
    [DllImport("user32.dll")]
    internal static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    // ---- OLE 初始化（后台线程使用剪贴板前必须调用） ----
    [DllImport("ole32.dll")]
    internal static extern int OleInitialize(IntPtr pvReserved);

    [DllImport("ole32.dll")]
    internal static extern void OleUninitialize();

    // ---- Win32 原生剪贴板（同步复制、失败即释放，无需 OLE/消息泵） ----
    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool OpenClipboard(IntPtr hWndNewOwner);

    [DllImport("user32.dll")]
    internal static extern bool EmptyClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern IntPtr SetClipboardData(uint uFormat, IntPtr hMem);

    [DllImport("user32.dll")]
    internal static extern bool CloseClipboard();

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern uint RegisterClipboardFormat(string lpszFormat);

    internal const uint CF_UNICODETEXT = 13;
    internal const uint CF_HDROP = 15;
    internal const uint CF_DIB = 8;

    // ---- 常量 ----
    internal const int WM_CLIPBOARDUPDATE = 0x031D;
    internal const int WM_HOTKEY = 0x0312;

    internal const uint KEYEVENTF_KEYUP = 0x0002;
    internal const uint MOD_ALT = 0x0001;
    internal const uint MOD_CONTROL = 0x0002;
    internal const uint MOD_SHIFT = 0x0004;
    internal const uint MOD_WIN = 0x0008;
    internal const uint MOD_NOREPEAT = 0x4000;
    internal const byte VK_V = 0x56;
    internal const byte VK_CONTROL = 0x11;

    internal const uint SWP_NOSIZE = 0x0001;
    internal const uint SWP_NOZORDER = 0x0004;
    internal const uint SWP_NOACTIVATE = 0x0010;

    [StructLayout(LayoutKind.Sequential)]
    internal struct POINT
    {
        public int X, Y;
    }
}
