using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ClipboardTool;

/// <summary>
/// 低级键盘钩子（WH_KEYBOARD_LL）：用于拦截系统硬绑定的 Win 组合键（如 Win+V）。
/// 按下匹配的组合键时触发事件并吞掉按键，阻止系统响应。
/// </summary>
public sealed class KeyboardHook : IDisposable
{
    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_SYSKEYDOWN = 0x0104;

    private const int VK_LWIN = 0x5B;
    private const int VK_RWIN = 0x5C;
    private const int VK_CONTROL = 0x11;
    private const int VK_MENU = 0x12;
    private const int VK_SHIFT = 0x10;

    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    private readonly LowLevelKeyboardProc _proc;
    private IntPtr _hookId;
    private uint _mods;
    private uint _vk;

    public event Action? HotkeyPressed;

    public KeyboardHook() => _proc = HookCallback;

    public void Start()
    {
        using var curProcess = Process.GetCurrentProcess();
        using var curModule = curProcess.MainModule!;
        _hookId = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, GetModuleHandle(curModule.ModuleName), 0);
    }

    /// <summary>设置要拦截的组合键（mods 含 MOD_WIN 时启用拦截）。</summary>
    public void Configure(uint mods, uint vk)
    {
        _mods = mods;
        _vk = vk;
    }

    public void Stop()
    {
        if (_hookId != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_hookId);
            _hookId = IntPtr.Zero;
        }
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && _vk != 0 && (wParam == (IntPtr)WM_KEYDOWN || wParam == (IntPtr)WM_SYSKEYDOWN))
        {
            var vkCode = Marshal.ReadInt32(lParam);
            if (vkCode == _vk && ModsMatch())
            {
                HotkeyPressed?.Invoke();
                // 注入一次 Shift 按下/松开：让系统认为这是组合键（Win+Shift 无默认动作），
                // 避免 Win 松开时被判定为单独按下而弹出开始菜单
                keybd_event(VK_SHIFT, 0, 0, UIntPtr.Zero);
                keybd_event(VK_SHIFT, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
                return (IntPtr)1; // 吞掉按键，系统（explorer）收不到
            }
        }
        return CallNextHookEx(_hookId, nCode, wParam, lParam);
    }

    private bool ModsMatch()
    {
        bool win = IsDown(VK_LWIN) || IsDown(VK_RWIN);
        bool ctrl = IsDown(VK_CONTROL);
        bool alt = IsDown(VK_MENU);
        bool shift = IsDown(VK_SHIFT);
        return ((_mods & NativeMethods.MOD_WIN) != 0) == win
            && ((_mods & NativeMethods.MOD_CONTROL) != 0) == ctrl
            && ((_mods & NativeMethods.MOD_ALT) != 0) == alt
            && ((_mods & NativeMethods.MOD_SHIFT) != 0) == shift;
    }

    private static bool IsDown(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;

    public void Dispose() => Stop();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string lpModuleName);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

    private const uint KEYEVENTF_KEYUP = 0x0002;
}
