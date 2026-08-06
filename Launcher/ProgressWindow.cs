using System;
using System.Runtime.InteropServices;
using System.Threading;

// 运行时安装进度窗口（纯 Win32 自绘：静态文本 + 原生进度条，NativeAOT 无托管 UI 框架可用）：
// 主线程创建窗口并跑消息循环，worker 线程执行下载/安装，跨线程 SendMessage 更新进度与文本。

internal static class ProgressWindow
{
    private const string ClassName = "ClipboardLauncherProgressWnd";

    private const uint WS_OVERLAPPED = 0x00000000;
    private const uint WS_CAPTION = 0x00C00000;
    private const uint WS_SYSMENU = 0x00080000;
    private const uint WS_CHILD = 0x40000000;
    private const uint WS_VISIBLE = 0x10000000;

    private const uint WM_CLOSE = 0x0010;
    private const uint WM_DESTROY = 0x0002;
    private const uint WM_SETFONT = 0x0030;
    private const uint PBM_SETRANGE32 = 0x0406;
    private const uint PBM_SETPOS = 0x0402;

    private const uint COLOR_BTNFACE = 15;
    private const int DEFAULT_GUI_FONT = 17;
    private const uint SW_SHOW = 5;
    private const uint SPI_GETWORKAREA = 0x0030;
    private const uint ICC_PROGRESS_CLASS = 0x00000020;
    private const int IDC_ARROW = 32512;

    private static IntPtr _hwnd;
    private static IntPtr _hwndText;
    private static IntPtr _hwndBar;
    private static bool _result;
    private static Exception? _exception;
    private static WndProc? _wndProc; // 持引用防止委托被 GC

    /// <summary>显示带进度条的窗口并执行 work（后台线程）；窗口关闭后返回 work 结果，work 抛出的异常原样抛出。</summary>
    public static bool Run(string title, string message, Func<Reporter, bool> work)
    {
        SetProcessDPIAware();
        var icc = new INITCOMMONCONTROLSEX
        {
            dwSize = (uint)Marshal.SizeOf<INITCOMMONCONTROLSEX>(),
            dwICC = ICC_PROGRESS_CLASS,
        };
        InitCommonControlsEx(ref icc);

        _result = false;
        _exception = null;
        RegisterClass();
        CreateWindow(title, message);
        if (_hwnd == IntPtr.Zero)
            throw new InvalidOperationException("创建进度窗口失败");

        var thread = new Thread(() =>
        {
            try
            {
                _result = work(new Reporter());
            }
            catch (Exception ex)
            {
                _exception = ex;
            }
            finally
            {
                // wParam=1 表示程序主动关闭（用户点 X 的 WM_CLOSE wParam=0 会被 WndProc 忽略）
                PostMessageW(_hwnd, WM_CLOSE, (IntPtr)1, IntPtr.Zero);
            }
        });
        thread.Start();

        while (GetMessageW(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            TranslateMessage(ref msg);
            DispatchMessageW(ref msg);
        }

        thread.Join();
        if (_exception is not null)
            throw _exception;
        return _result;
    }

    private static void RegisterClass()
    {
        if (GetClassInfoW(GetModuleHandleW(null), ClassName, out _))
            return;
        var wc = new WNDCLASSEX
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc = WndProcImpl),
            hInstance = GetModuleHandleW(null),
            hCursor = LoadCursorW(IntPtr.Zero, (IntPtr)IDC_ARROW),
            hbrBackground = (IntPtr)(COLOR_BTNFACE + 1),
            lpszClassName = ClassName,
        };
        if (RegisterClassExW(ref wc) == 0)
            throw new InvalidOperationException("注册窗口类失败");
    }

    private static void CreateWindow(string title, string message)
    {
        var hInstance = GetModuleHandleW(null);
        const int w = 460;
        const int h = 130;
        var area = GetWorkArea();
        var x = area.left + (area.right - area.left - w) / 2;
        var y = area.top + (area.bottom - area.top - h) / 2;

        _hwnd = CreateWindowExW(0, ClassName, title,
            WS_OVERLAPPED | WS_CAPTION | WS_SYSMENU,
            x, y, w, h, IntPtr.Zero, IntPtr.Zero, hInstance, IntPtr.Zero);

        _hwndText = CreateWindowExW(0, "Static", message,
            WS_CHILD | WS_VISIBLE, 16, 14, w - 32, 22, _hwnd, IntPtr.Zero, hInstance, IntPtr.Zero);

        _hwndBar = CreateWindowExW(0, "msctls_progress32", "",
            WS_CHILD | WS_VISIBLE, 16, 50, w - 32, 18, _hwnd, IntPtr.Zero, hInstance, IntPtr.Zero);

        SendMessageW(_hwndText, WM_SETFONT, GetStockObject(DEFAULT_GUI_FONT), (IntPtr)1);
        SendMessageW(_hwndBar, PBM_SETRANGE32, IntPtr.Zero, (IntPtr)100);
        ShowWindow(_hwnd, SW_SHOW);
        UpdateWindow(_hwnd);
    }

    private static RECT GetWorkArea()
    {
        var rect = new RECT();
        SystemParametersInfoW(SPI_GETWORKAREA, 0, ref rect, 0);
        return rect;
    }

    private static IntPtr WndProcImpl(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == WM_CLOSE)
        {
            if (wParam != (IntPtr)1)
                return IntPtr.Zero; // 安装进行中，禁止用户关闭
            DestroyWindow(hWnd);
            return IntPtr.Zero;
        }
        if (msg == WM_DESTROY)
        {
            PostQuitMessage(0);
            return IntPtr.Zero;
        }
        return DefWindowProcW(hWnd, msg, wParam, lParam);
    }

    /// <summary>worker 线程用来更新进度条与提示文本。</summary>
    internal sealed class Reporter
    {
        public void Report(int percent) =>
            SendMessageW(_hwndBar, PBM_SETPOS, (IntPtr)Math.Clamp(percent, 0, 100), IntPtr.Zero);

        public void Stage(string text) =>
            SetWindowTextW(_hwndText, text);
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEX
    {
        public uint cbSize;
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public string lpszMenuName;
        public string lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int left;
        public int top;
        public int right;
        public int bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public int ptX;
        public int ptY;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct INITCOMMONCONTROLSEX
    {
        public uint dwSize;
        public uint dwICC;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowExW(uint dwExStyle, string lpClassName, string lpWindowName,
        uint dwStyle, int x, int y, int nWidth, int nHeight,
        IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern ushort RegisterClassExW(ref WNDCLASSEX wc);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetClassInfoW(IntPtr hInstance, string lpClassName, out WNDCLASSEX wc);

    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProcW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool PostMessageW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessageW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool SetWindowTextW(IntPtr hWnd, string text);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, uint nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool UpdateWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool SystemParametersInfoW(uint uiAction, uint uiParam, ref RECT pvParam, uint fWinIni);

    [DllImport("user32.dll")]
    private static extern int GetMessageW(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref MSG lpMsg);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessageW(ref MSG lpMsg);

    [DllImport("user32.dll")]
    private static extern void PostQuitMessage(int nExitCode);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandleW(string? lpModuleName);

    [DllImport("user32.dll")]
    private static extern IntPtr LoadCursorW(IntPtr hInstance, IntPtr lpCursorName);

    [DllImport("user32.dll")]
    private static extern bool SetProcessDPIAware();

    [DllImport("gdi32.dll")]
    private static extern IntPtr GetStockObject(int fnObject);

    [DllImport("comctl32.dll")]
    private static extern bool InitCommonControlsEx(ref INITCOMMONCONTROLSEX lpInitCtrls);
}
