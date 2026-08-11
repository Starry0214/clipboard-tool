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
    private SyncService? _sync;

    /// <summary>数据目录（%LocalAppData%\ClipboardTool），与程序分离，更新程序不丢数据。</summary>
    public string DataDir { get; private set; } = "";

    /// <summary>多端同步服务（设置页登录区使用；SyncEnabled 时才启动）。</summary>
    public SyncService? SyncService => _sync;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        if (!_mutex.WaitOne(TimeSpan.Zero, true))
        {
            MessageBox.Show("剪贴板助手已在运行。", "剪贴板助手",
                MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        // 数据目录：%LocalAppData%\ClipboardTool（标准程序数据位置）；
        // 旧版同目录 data/ 首次运行时自动迁移
        // 测试钩子：--data-dir <path> 指定数据目录（联调用，隔离真实数据）
        var dataDir = e.Args.Length >= 2 && e.Args[0] == "--data-dir"
            ? Path.Combine(e.Args[1])
            : Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClipboardTool");
        var legacyDir = Path.Combine(AppContext.BaseDirectory, "data");
        if (Directory.Exists(legacyDir) && !Directory.Exists(dataDir))
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(dataDir)!);
                Directory.Move(legacyDir, dataDir);
                Log.Info($"旧数据迁移成功: {legacyDir} → {dataDir}");
            }
            catch (Exception ex)
            {
                dataDir = legacyDir; // 迁移失败回退旧位置，不丢数据
                Log.Error("旧数据迁移失败，回退旧目录", ex);
            }
        }
        DataDir = dataDir;

        Log.Init(dataDir);
        Log.Info($"程序启动 v{Updater.CurrentVersion}，数据目录 {dataDir}");
        HookUnhandledExceptions();

        _settings = Settings.Load(dataDir);
        _store = new ClipboardStore(dataDir) { MaxEntries = _settings.MaxEntries };
        _store.CleanupOrphanFiles(); // 清理历史遗留的孤儿图片/同步文件（旧版删除条目不删文件）
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

        _sync = new SyncService(_store, _monitor, _settings, DataDir);
        if (_settings.SyncEnabled)
            _ = _sync.StartAsync();

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
        _tray.CheckUpdate += () => _ = CheckForUpdateAsync(manual: true);
        _tray.Feedback += ErrorWindow.ShowFeedback;
        _tray.TogglePause += OnTogglePause;
        _tray.ClearHistory += OnClearHistory;
        _tray.Exit += OnExitRequested;

        // 测试钩子：--show-overlay 直接弹出悬浮列表（等效热键路径）
        if (e.Args.Contains("--show-overlay"))
            Dispatcher.BeginInvoke(OnHotkeyPressed);
        // 测试钩子：--throw 模拟 UI 线程未处理异常（验证错误窗与日志）
        if (e.Args.Contains("--throw"))
            Dispatcher.BeginInvoke(new Action(() => throw new InvalidOperationException("测试异常：--throw 触发")));
        // 测试钩子：--show-main 直接打开主窗口
        if (e.Args.Contains("--show-main"))
            Dispatcher.BeginInvoke(OpenMainWindow);
        // 启动 8 秒后自动检查更新（静默，无更新不打扰）
        var updater = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(8) };
        updater.Tick += async (_, _) =>
        {
            updater.Stop();
            await CheckForUpdateAsync(manual: false);
        };
        updater.Start();
        // 常驻轮询：每 30 分钟静默检查一次（服务端发布新版后客户端最多 30 分钟内提示）
        var poller = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMinutes(30) };
        poller.Tick += async (_, _) => await CheckForUpdateAsync(manual: false);
        poller.Start();
    }

    /// <summary>防止轮询/手动检查并发：弹窗挂起时再次触发会堆叠弹窗，应用关闭时残留续体崩溃（"应用程序对象正在关闭"）。</summary>
    private static bool _checkingUpdate;

    /// <summary>检查更新：自动模式静默，手动模式带反馈。有更新时引导下载并重启安装。</summary>
    private async Task CheckForUpdateAsync(bool manual)
    {
        if (_checkingUpdate)
        {
            if (manual)
                MessageBox.Show("正在检查更新，请稍候。", "检查更新",
                    MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        _checkingUpdate = true;
        try
        {
            await CheckForUpdateCoreAsync(manual);
        }
        finally
        {
            _checkingUpdate = false;
        }
    }

    private async Task CheckForUpdateCoreAsync(bool manual)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var latest = await Updater.CheckAsync();
        if (latest is null)
        {
            Log.Error("检查更新失败：无法连接更新服务器");
            if (manual)
                MessageBox.Show($"检查更新失败：无法连接更新服务器（当前版本 v{Updater.CurrentVersion}）。", "检查更新",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (!Updater.IsNewer(latest))
        {
            Log.Info($"检查更新：当前已是最新 v{Updater.CurrentVersion}");
            if (manual)
                MessageBox.Show($"当前已是最新版本（v{Updater.CurrentVersion}）。", "检查更新",
                    MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        Log.Info($"发现新版本 v{latest}（当前 v{Updater.CurrentVersion}）");
        // 更新日志：优先全量 changelog（跨版本时展示所有中间版本），失败回退 notes.txt（仅最新版）
        var notes = await Updater.GetChangelogAsync() ?? await Updater.GetNotesAsync();
        var msg = $"发现新版本 v{latest}（当前 v{Updater.CurrentVersion}）。\n" +
            (string.IsNullOrEmpty(notes) ? "" : $"\n【更新内容】\n{notes}\n") +
            "\n是否下载并安装？";
        var answer = MessageBox.Show(msg, "发现更新",
            MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes)
            return;

        Log.Info("开始下载更新");
        var progressWin = new UpdateProgressWindow();
        progressWin.Show();
        var newExe = await Updater.DownloadAsync(DataDir,
            new Progress<Updater.DownloadProgress>(progressWin.Report));
        if (progressWin.IsVisible)
            progressWin.Close();
        if (newExe is null)
        {
            Log.Error($"下载更新失败，耗时 {sw.Elapsed.TotalSeconds:F1}s");
            MessageBox.Show("下载更新失败，请检查网络后重试。", "更新失败",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        Log.Info($"下载完成 v{latest}，耗时 {sw.Elapsed.TotalSeconds:F1}s");

        // 引导器解压模式：下载到的是新引导器，替换 launcher（不替换自身）
        if (Updater.IsLauncherMode)
        {
            var launcher = Updater.LauncherPath;
            if (string.IsNullOrEmpty(launcher) || !File.Exists(launcher))
            {
                MessageBox.Show("未找到引导器文件，请从下载目录重新运行 剪贴板助手.exe。", "更新失败",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            var confirm2 = MessageBox.Show("更新已下载完成，重启后生效（新版本将在下次启动时应用）。是否立即重启？", "更新就绪",
                MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (confirm2 != MessageBoxResult.Yes)
                return;
            Log.Info("用户确认重启（引导器模式）");
            Updater.Apply(newExe, launcher); // 用 bat 覆盖引导器并重启引导器
            Shutdown();
            return;
        }

        var confirm = MessageBox.Show("更新已下载完成，是否立即重启以完成更新？", "更新就绪",
            MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes)
            return;

        Log.Info("用户确认重启安装");
        Updater.Apply(newExe, Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "ClipboardTool.exe"));
        Shutdown();
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
            Log.Error($"全局热键注册失败: {ex.Message}");
            MessageBox.Show($"全局热键注册失败：{ex.Message}\n请在设置中更换热键。", "剪贴板助手",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>全局未处理异常捕获：UI 线程弹窗+记日志，其余线程记日志。</summary>
    private void HookUnhandledExceptions()
    {
        DispatcherUnhandledException += (_, e) =>
        {
            Log.Error("UI 线程未处理异常", e.Exception);
            e.Handled = true;
            ErrorWindow.ShowError("出错了", $"程序遇到未处理的错误：\n\n{e.Exception.Message}\n\n可以点击“上报日志”帮助排查。");
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Log.Error("AppDomain 未处理异常", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Log.Error("Task 未观察异常", e.Exception);
            e.SetObserved();
        };
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
        var result = MessageBox.Show("确定要清空全部历史记录吗？置顶条目将保留。", "清空历史",
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
        Log.Info($"设置变更: MaxEntries={_settings.MaxEntries}, UseWinV={_settings.UseWinV}, Hotkey={_settings.HotkeyText}");
        _store.MaxEntries = _settings.MaxEntries;
        _store.Trim();
        _settings.ApplyAutoStart();
        _settings.ApplyStartMenuShortcut();
        RegisterHotkey();
        if (_settings.SyncEnabled)
            _ = _sync?.StartAsync();
        else
            _ = _sync?.StopAsync();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Log.Info("程序退出");
        _tray?.Dispose();
        _monitor?.Stop();
        _hotkeys?.Unregister();
        _keyboardHook?.Dispose();
        _sync?.Dispose();
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
