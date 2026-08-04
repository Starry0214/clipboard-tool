using System.Windows;
using System.Windows.Interop;
using Application = System.Windows.Application;
using MessageBox = System.Windows.MessageBox;

namespace ClipboardTool;

public partial class App : Application
{
    private const string MutexName = "ClipboardTool_SingleInstance";

    private readonly Mutex _mutex = new(true, MutexName);
    private ClipboardStore _store = null!;
    private Settings _settings = null!;
    private ClipboardMonitor _monitor = null!;
    private HotkeyManager _hotkeys = null!;
    private KeyboardHook _keyboardHook = null!;
    private TrayIcon _tray = null!;
    private MessageWindow _messageWindow = null!;
    private OverlayWindow _overlay = null!;
    private MainWindow? _main;
    private HelpWindow? _help;

    /// <summary>数据目录（%LocalAppData%\ClipboardTool），与程序分离，更新程序不丢数据。</summary>
    public string DataDir { get; private set; } = "";

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        if (!_mutex.WaitOne(TimeSpan.Zero, true))
        {
            MessageBox.Show("剪贴板工具已在运行。", "剪贴板工具",
                MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        // 数据目录：%LocalAppData%\ClipboardTool（标准程序数据位置）；
        // 旧版同目录 data/ 首次运行时自动迁移
        var dataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClipboardTool");
        var legacyDir = Path.Combine(AppContext.BaseDirectory, "data");
        if (Directory.Exists(legacyDir) && !Directory.Exists(dataDir))
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(dataDir)!);
                Directory.Move(legacyDir, dataDir);
            }
            catch (Exception)
            {
                dataDir = legacyDir; // 迁移失败回退旧位置，不丢数据
            }
        }
        DataDir = dataDir;

        _settings = Settings.Load(dataDir);
        _store = new ClipboardStore(dataDir) { MaxEntries = _settings.MaxEntries };
        _monitor = new ClipboardMonitor(_store);
        _hotkeys = new HotkeyManager();
        _keyboardHook = new KeyboardHook();
        _keyboardHook.Start();
        _keyboardHook.HotkeyPressed += OnHotkeyPressed;
        _tray = new TrayIcon();

        // 常驻隐藏窗口承载剪贴板监听与全局热键消息
        _messageWindow = new MessageWindow();
        _messageWindow.Show();

        _monitor.Start(_messageWindow);
        RegisterHotkey();
        _settings.ApplyAutoStart();

        // 生成窗口图标并放入全局资源（供所有窗口绑定）
        using (var iconBmp = TrayIcon.CreateIconBitmap(256))
        {
            var hIcon = iconBmp.GetHicon();
            var icon = System.Drawing.Icon.FromHandle(hIcon);
            var imageSource = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(
                icon.Handle,
                System.Windows.Int32Rect.Empty,
                System.Windows.Media.Imaging.BitmapSizeOptions.FromEmptyOptions());
            imageSource.Freeze();
            Current.Resources["AppIcon"] = imageSource;
        }

        _overlay = new OverlayWindow(_store, _monitor, _settings);
        _hotkeys.Pressed += OnHotkeyPressed;
        _tray.OpenMain += OpenMainWindow;
        _tray.ShowHelp += OpenHelp;
        _tray.TogglePause += OnTogglePause;
        _tray.ClearHistory += OnClearHistory;
        _tray.Exit += OnExitRequested;

        // 测试钩子：--show-overlay 直接弹出悬浮列表（等效热键路径）
        if (e.Args.Contains("--show-overlay"))
            Dispatcher.BeginInvoke(OnHotkeyPressed);
        // 测试钩子：--show-main 直接打开主窗口
        if (e.Args.Contains("--show-main"))
            Dispatcher.BeginInvoke(OpenMainWindow);
    }

    private void RegisterHotkey()
    {
        if (_settings.UseWinV)
        {
            // Win+V 覆盖模式：低级钩子拦截，系统剪贴板历史被接管
            _hotkeys.Unregister();
            _keyboardHook.Configure(NativeMethods.MOD_WIN | NativeMethods.MOD_NOREPEAT, 0x56);
            return;
        }

        var (mods, vk) = Settings.ParseHotkey(_settings.HotkeyText);

        if ((mods & NativeMethods.MOD_WIN) != 0)
        {
            // Win 组合键（如 Win+V）被系统硬绑定，RegisterHotKey 会失败 → 用低级钩子直接拦截
            _hotkeys.Unregister();
            _keyboardHook.Configure(mods, vk);
            return;
        }

        _keyboardHook.Configure(0, 0);
        try
        {
            _hotkeys.Register(_messageWindow, mods, vk);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"全局热键注册失败：{ex.Message}\n请在设置中更换热键。", "剪贴板工具",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnHotkeyPressed()
    {
        if (_overlay.IsVisible)
        {
            _overlay.RequestHide();
            return;
        }
        NativeMethods.GetCursorPos(out var pt);
        _overlay.ShowAt(new Point(pt.X, pt.Y), _store.Query(null));
    }

    private void OpenMainWindow()
    {
        _main ??= new MainWindow(_store, _settings);
        if (!_main.IsVisible)
            _main.Show();
        _main.Refresh();
        _main.Activate();
    }

    private void OpenHelp()
    {
        _help ??= new HelpWindow();
        _help.Open();
    }

    private void OnTogglePause()
    {
        _monitor.Paused = !_monitor.Paused;
        _tray.SetPaused(_monitor.Paused);
    }

    private void OnClearHistory()
    {
        var result = MessageBox.Show("确定要清空全部历史记录吗？", "清空历史",
            MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (result == MessageBoxResult.Yes)
            _store.Clear();
    }

    private void OnExitRequested()
    {
        _tray.Dispose();
        Shutdown();
    }

    /// <summary>应用设置变更（热键、上限、自启）。</summary>
    public void ApplySettings()
    {
        _store.MaxEntries = _settings.MaxEntries;
        _store.Trim();
        _settings.ApplyAutoStart();
        RegisterHotkey();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _tray?.Dispose();
        _monitor?.Stop();
        _hotkeys?.Unregister();
        _keyboardHook?.Dispose();
        _store?.Dispose();
        base.OnExit(e);
    }

    /// <summary>不可见消息窗口：仅承载 Win32 消息（剪贴板监听、热键）。</summary>
    private sealed class MessageWindow : Window
    {
        public MessageWindow()
        {
            Width = 0;
            Height = 0;
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = System.Windows.Media.Brushes.Transparent;
            ShowInTaskbar = false;
            ShowActivated = false;
            ResizeMode = ResizeMode.NoResize;
            Left = -10000;
            Top = -10000;
        }
    }
}
