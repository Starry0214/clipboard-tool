using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace ClipboardTool;

public partial class SettingsWindow : Window
{
    private readonly Settings _settings;
    public bool Applied { get; private set; }

    public SettingsWindow(Settings settings)
    {
        InitializeComponent();
        _settings = settings;
        HotkeyBox.Text = settings.HotkeyText;
        MaxBox.Text = settings.MaxEntries.ToString();
        OverlayHeightBox.Text = settings.OverlayMaxHeight.ToString();
        AutoStartCheck.IsChecked = settings.AutoStart;
        StartMenuCheck.IsChecked = settings.StartMenuShortcut;
        PlainCheck.IsChecked = settings.PastePlainText;

        WinVCheck.IsChecked = settings.UseWinV;
        WinVCheck.Checked += (_, _) => UpdateWinVState();
        WinVCheck.Unchecked += (_, _) => UpdateWinVState();
        UpdateWinVState();

        HotkeyBox.PreviewKeyDown += OnHotkeyKeyDown;
        HotkeyBox.GotKeyboardFocus += (_, _) => HotkeyBox.SelectAll();
        LoadDataInfo();

        SyncCheck.IsChecked = settings.SyncEnabled; // 初始选中态必须在 InitializeComponent 之后设置（XAML 时序陷阱）
        SyncCheck.Checked += (_, _) => UpdateSyncUi();
        SyncCheck.Unchecked += (_, _) => UpdateSyncUi();
        SyncUserBox.Text = settings.SyncUsername;
        SyncDeviceBox.Text = string.IsNullOrEmpty(settings.SyncDeviceName) ? Environment.MachineName : settings.SyncDeviceName;
        UpdateSyncUi();
    }

    private SyncService? Sync => (Application.Current as App)?.SyncService;

    private void UpdateSyncUi()
    {
        var enabled = SyncCheck.IsChecked == true;
        SyncPanel.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
        var loggedIn = Sync?.LoggedIn == true;
        SyncUserBox.IsEnabled = !loggedIn;
        SyncPassBox.IsEnabled = !loggedIn;
        SyncDeviceBox.IsEnabled = !loggedIn;
        SyncLoginBtn.Visibility = loggedIn ? Visibility.Collapsed : Visibility.Visible;
        SyncRegisterBtn.Visibility = loggedIn ? Visibility.Collapsed : Visibility.Visible;
        SyncLogoutBtn.Visibility = loggedIn ? Visibility.Visible : Visibility.Collapsed;
        SyncNowBtn.Visibility = loggedIn ? Visibility.Visible : Visibility.Collapsed;
        if (loggedIn)
        {
            var s = Sync!;
            SyncStatusText.Text = $"已登录：{s.AccountName}（{s.DeviceName}）";
            SyncStatusText.Foreground = System.Windows.Media.Brushes.Green;
        }
        else
        {
            SyncStatusText.Text = "未登录";
            SyncStatusText.Foreground = System.Windows.Media.Brushes.Gray;
        }
    }

    private async void OnSyncLogin(object sender, RoutedEventArgs e)
    {
        var sync = Sync;
        if (sync is null)
            return;
        SyncLoginBtn.IsEnabled = false;
        SyncStatusText.Text = "登录中…";
        var ok = await sync.LoginAsync(SyncUserBox.Text.Trim(), SyncPassBox.Password, SyncDeviceBox.Text.Trim());
        SyncLoginBtn.IsEnabled = true;
        SyncStatusText.Text = ok ? "已登录" : sync.StatusText;
        UpdateSyncUi();
    }

    private async void OnSyncRegister(object sender, RoutedEventArgs e)
    {
        var sync = Sync;
        if (sync is null)
            return;
        SyncRegisterBtn.IsEnabled = false;
        SyncStatusText.Text = "注册中…";
        var ok = await sync.RegisterAsync(SyncUserBox.Text.Trim(), SyncPassBox.Password, SyncDeviceBox.Text.Trim());
        SyncRegisterBtn.IsEnabled = true;
        SyncStatusText.Text = ok ? "已注册并登录" : sync.StatusText;
        UpdateSyncUi();
    }

    private void OnSyncLogout(object sender, RoutedEventArgs e)
    {
        Sync?.Logout();
        SyncStatusText.Text = "未登录";
        UpdateSyncUi();
    }

    private async void OnSyncNow(object sender, RoutedEventArgs e)
    {
        var sync = Sync;
        if (sync is null)
            return;
        SyncNowBtn.IsEnabled = false;
        SyncStatusText.Text = "同步中…";
        var result = await sync.SyncNowAsync();
        SyncNowBtn.IsEnabled = true;
        if (result is null)
            UpdateSyncUi();
        else
            SyncStatusText.Text = result;
    }

    /// <summary>展示数据目录路径与占用大小。</summary>
    private void LoadDataInfo()
    {
        var dataDir = (Application.Current as App)?.DataDir ?? "";
        DataDirText.Text = dataDir;
        SizeText.Text = $"占用 {FormatSize(CalcDirSize(dataDir))}";
    }

    private void OnOpenDataDir(object sender, RoutedEventArgs e)
    {
        var dataDir = (Application.Current as App)?.DataDir ?? "";
        try
        {
            if (Directory.Exists(dataDir))
                Process.Start("explorer.exe", dataDir);
        }
        catch (Exception)
        {
        }
    }

    private static long CalcDirSize(string dir)
    {
        long total = 0;
        try
        {
            foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                total += new FileInfo(f).Length;
        }
        catch (Exception)
        {
        }
        return total;
    }

    private static string FormatSize(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):F1} GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):F0} MB",
        >= 1L << 10 => $"{bytes / (double)(1L << 10):F0} KB",
        _ => $"{bytes} B",
    };

    /// <summary>Win+V 覆盖模式：禁用自定义热键输入框并显示提示。</summary>
    private void UpdateWinVState()
    {
        var on = WinVCheck.IsChecked == true;
        HotkeyBox.IsEnabled = !on;
        HotkeyBox.Opacity = on ? 0.5 : 1;
        WinVHintBox.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnHotkeyKeyDown(object sender, KeyEventArgs e)
    {
        e.Handled = true;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift
            or Key.LWin or Key.RWin)
            return;

        var parts = new List<string>();
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Windows))
            parts.Add("Win");
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
            parts.Add("Ctrl");
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Alt))
            parts.Add("Alt");
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
            parts.Add("Shift");

        var main = key switch
        {
            Key.D0 => "0",
            Key.D1 => "1",
            Key.D2 => "2",
            Key.D3 => "3",
            Key.D4 => "4",
            Key.D5 => "5",
            Key.D6 => "6",
            Key.D7 => "7",
            Key.D8 => "8",
            Key.D9 => "9",
            >= Key.A and <= Key.Z => key.ToString(),
            >= Key.F1 and <= Key.F24 => $"F{key - Key.F1 + 1}",
            _ => null,
        };
        if (main is null || parts.Count == 0)
            return;
        HotkeyBox.Text = string.Join("+", parts) + "+" + main;
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        var useWinV = WinVCheck.IsChecked == true;

        // Win+V 覆盖模式下无需自定义热键；否则校验热键可解析
        if (!useWinV)
        {
            try
            {
                Settings.ParseHotkey(HotkeyBox.Text);
            }
            catch (FormatException)
            {
                MessageBox.Show(this, "热键格式无效，请点击输入框后按下新的组合键。", "设置",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
        }

        if (!int.TryParse(MaxBox.Text, out var max) || max < 10 || max > 10000)
        {
            MessageBox.Show(this, "条数上限须为 10 ~ 10000 的整数。", "设置",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (!int.TryParse(OverlayHeightBox.Text, out var overlayH) || overlayH < 0 || overlayH > 2000)
        {
            MessageBox.Show(this, "悬浮列表高度须为 0 ~ 2000 的整数（0 表示自动）。", "设置",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _settings.UseWinV = useWinV;
        _settings.HotkeyText = HotkeyBox.Text;
        _settings.MaxEntries = max;
        _settings.OverlayMaxHeight = overlayH;
        _settings.AutoStart = AutoStartCheck.IsChecked == true;
        _settings.StartMenuShortcut = StartMenuCheck.IsChecked == true;
        _settings.PastePlainText = PlainCheck.IsChecked == true;
        _settings.SyncEnabled = SyncCheck.IsChecked == true;
        _settings.Save();
        Applied = true;
        Close();
    }
}
