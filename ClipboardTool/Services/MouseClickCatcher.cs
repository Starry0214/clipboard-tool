using System.Runtime.InteropServices;

namespace ClipboardTool;

/// <summary>
/// 低级鼠标钩子（WH_MOUSE_LL）：Overlay 可见期间捕获左键点击，
/// 点击位置在目标窗口矩形之外时触发 OutsideClick（用于"点击外部关闭"）。
/// </summary>
public sealed class MouseClickCatcher : IDisposable
{
    private const int WH_MOUSE_LL = 14;
    private const int WM_LBUTTONDOWN = 0x0201;

    private delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);

    private readonly LowLevelMouseProc _proc;
    private IntPtr _hookId;
    private IntPtr _targetHwnd;

    public event Action? OutsideClick;

    public MouseClickCatcher() => _proc = HookCallback;

    public void Start(IntPtr targetHwnd)
    {
        _targetHwnd = targetHwnd;
        if (_hookId != IntPtr.Zero)
            return;
        // 低级钩子回调运行在本进程 UI 线程，hMod 传 exe 模块句柄即可
        _hookId = SetWindowsHookEx(WH_MOUSE_LL, _proc, GetModuleHandle(null), 0);
    }

    public bool IsActive => _hookId != IntPtr.Zero;

    public void Stop()
    {
        _targetHwnd = IntPtr.Zero;
        if (_hookId != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_hookId);
            _hookId = IntPtr.Zero;
        }
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && wParam == (IntPtr)WM_LBUTTONDOWN && _targetHwnd != IntPtr.Zero)
        {
            GetCursorPos(out var pt);
            GetWindowRect(_targetHwnd, out var rc);
            if (pt.X < rc.Left || pt.X > rc.Right || pt.Y < rc.Top || pt.Y > rc.Bottom)
                OutsideClick?.Invoke();
        }
        return CallNextHookEx(_hookId, nCode, wParam, lParam);
    }

    public void Dispose() => Stop();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT pt);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT rc);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X, Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
    }
}
