using System.Windows;
using System.Windows.Interop;

namespace ClipboardTool;

/// <summary>Win32 RegisterHotKey 全局热键封装，消息经 WPF 钩子分发。</summary>
public sealed class HotkeyManager
{
    private const int HotkeyId = 0xB100;
    private HwndSource? _source;
    private bool _registered;

    public event Action? Pressed;

    /// <summary>重新注册热键（改绑时调用）。注册失败抛出异常由调用方处理。</summary>
    public void Register(Window owner, uint mods, uint vk)
    {
        Unregister();
        var source = HwndSource.FromHwnd(new WindowInteropHelper(owner).EnsureHandle())!;
        if (_source is null)
        {
            source.AddHook(Hook);
            _source = source;
        }
        if (!NativeMethods.RegisterHotKey(source.Handle, HotkeyId, mods, vk))
            throw new InvalidOperationException("热键注册失败，可能被其他程序占用");
        _registered = true;
    }

    public void Unregister()
    {
        if (_source is null || !_registered)
            return;
        NativeMethods.UnregisterHotKey(_source.Handle, HotkeyId);
        _registered = false;
    }

    private IntPtr Hook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == NativeMethods.WM_HOTKEY && (int)wParam == HotkeyId)
        {
            handled = true;
            Pressed?.Invoke();
        }
        return IntPtr.Zero;
    }
}
