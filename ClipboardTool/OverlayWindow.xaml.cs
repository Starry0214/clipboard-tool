using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using Microsoft.Win32;
using Point = System.Windows.Point;

namespace ClipboardTool;

public partial class OverlayWindow : Window
{
    private readonly ClipboardStore _store;
    private readonly ClipboardMonitor _monitor;
    private readonly Settings _settings;
    private ImagePreviewWindow? _preview;
    private TextPreviewWindow? _textPreview;
    private readonly MouseClickCatcher _mouseCatcher = new();
    private readonly System.Windows.Threading.DispatcherTimer _closeTimer;
    private readonly RectangleGeometry _clip = new();
    private Storyboard? _anim;
    private bool _hiding;
    private bool _menuOpen; // 任一右键菜单打开期间抑制"点击外部关闭"，避免点菜单项把窗口收掉
    /// <summary>来源筛选状态：""=全部、"local"=本机、"phone"=手机。</summary>
    private string _sourceFilter = "";

    public OverlayWindow(ClipboardStore store, ClipboardMonitor monitor, Settings settings)
    {
        InitializeComponent();
        _store = store;
        _monitor = monitor;
        _settings = settings;
        FilterAll.IsChecked = true; // 默认"全部"（在 InitializeComponent 之后设置，避免 XAML 初始化时控件未就绪）
        // 未开启多端同步时来源筛选无意义，隐藏（与主窗口一致）
        SourceFilterPanel.Visibility = _settings.SyncEnabled ? Visibility.Visible : Visibility.Collapsed;
        UpdateSourceIcon();
        Clip = _clip;
        // 失焦延迟确认关闭：给快速 Win+V 重按留出窗口，避免 Win 键失焦抢先吞掉动画
        _closeTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        _closeTimer.Tick += (_, _) =>
        {
            _closeTimer.Stop();
            if (IsVisible && !IsActive)
                AnimateOut();
        };
        SearchBox.TextChanged += (_, _) => Reload();
        // 单击条目即粘贴（Win+V 行为）；预览/打开请用右键菜单
        HistoryList.PreviewMouseLeftButtonUp += (_, e) =>
        {
            if (!IsInListItem(e.OriginalSource))
                return;
            if (HistoryList.SelectedItem is Entry entry && HistoryList.SelectedIndex >= 0)
                PasteSelected(plainText: false);
        };
        // 失焦时延迟确认再关闭（避免与热键重按竞争）
        Deactivated += (_, _) =>
        {
            // 右键非粘贴操作（置顶/删除/预览/另存为）后列表保持打开：
            // 期间失焦（如打开预览窗口）不触发自动关闭，列表重新激活时清除标志
            if (_keepOpenAfterMenu)
                return;
            _closeTimer.Stop();
            _closeTimer.Start();
        };
        // 列表重新激活 → 非粘贴操作的"保持打开"状态结束，恢复失焦自动关闭
        Activated += (_, _) => _keepOpenAfterMenu = false;
        // 点击列表外部区域 → 等效 Esc 关闭（菜单打开期间点击菜单项不算外部点击）
        _mouseCatcher.OutsideClick += () =>
        {
            if (_menuOpen)
                return;
            RequestHide();
        };
    }

    private static bool IsInListItem(object source)
    {
        for (DependencyObject? d = source as DependencyObject; d != null; d = VisualTreeHelper.GetParent(d))
            if (d is System.Windows.Controls.ListBoxItem)
                return true;
        return false;
    }

    public void ShowAt(Point cursor, List<Entry> items)
    {
        Paster.TargetWindow = NativeMethods.GetForegroundWindow();
        _hiding = false;
        _closeTimer.Stop();

        Show();
        _mouseCatcher.Start(new WindowInteropHelper(this).Handle);

        // 先填充内容再强制布局：SizeToContent 的窗口此时才得到最终高度，
        // 位置校正与展开动画必须基于最终尺寸（否则首次唤起会按空内容高度展开而"卡住"）
        HistoryList.ItemsSource = items;
        HistoryList.SelectedIndex = items.Count > 0 ? 0 : -1;
        SearchBox.Text = "";
        FilterAll.IsChecked = true;
        UpdateLayout();
        // 传入的 items 未按来源筛选过滤；且收起再呼出时 SearchBox/FilterAll 可能已是目标值、
        // 事件不触发 Reload——这里强制按当前类型+来源筛选重新查询，否则图标显示筛选但列表是全部
        Reload();

        var screen = System.Windows.Forms.Screen.FromPoint(new System.Drawing.Point((int)cursor.X, (int)cursor.Y));
        var wa = screen.WorkingArea;
        // 全物理像素坐标系：cursor/WorkingArea 是物理像素，窗口实际尺寸按当前 DPI 换算成物理像素，
        // 用 SetWindowPos 直接定位，绕开 WPF Left/Top 的逻辑单位转换（不同 DPI/分辨率下精确）
        var dpi = VisualTreeHelper.GetDpi(this);
        // 悬浮列表最大高度：手动设置值优先，否则按工作区高度 70% 自动适配；下限 240 逻辑像素。
        // 在 UpdateLayout 前设置，SizeToContent 一次布局即得最终高度（不触发二次测量）。
        var maxH = _settings.OverlayMaxHeight > 0
            ? _settings.OverlayMaxHeight
            : wa.Height * 0.7 / dpi.DpiScaleY;
        MaxHeight = Math.Clamp(maxH, 240, wa.Height / dpi.DpiScaleY);
        double winW = ActualWidth * dpi.DpiScaleX;
        double winH = ActualHeight * dpi.DpiScaleY;
        // 窗口左上角对齐鼠标位置；超出屏幕边缘时向回校正
        double x = cursor.X, y = cursor.Y;
        if (x + winW > wa.Right)
            x = Math.Max(wa.Left, cursor.X - winW);
        if (y + winH > wa.Bottom)
            y = Math.Max(wa.Top, cursor.Y - winH);
        NativeMethods.SetWindowPos(new WindowInteropHelper(this).Handle, IntPtr.Zero,
            (int)x, (int)y, 0, 0, NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);
        Left = x / dpi.DpiScaleX;
        Top = y / dpi.DpiScaleY;

        Activate();
        Keyboard.Focus(SearchBox);
        AnimateIn();
    }

    /// <summary>供外部（App 热键）请求关闭：展开动画倒放后隐藏。</summary>
    public void RequestHide() => AnimateOut();

    // ---- 进出场动画：线从左上角向右滑出 → 向下扩张成完整界面；关闭时倒放 ----

    private void AnimateIn()
    {
        _anim?.Stop(this);
        Opacity = 1;
        var w = ActualWidth;
        var h = ActualHeight;
        _clip.Rect = new Rect(0, 0, 2, 2);

        var sb = new Storyboard();
        // 阶段1（220ms）：一条线向右滑出成顶线；阶段2（480ms）：向下扩张到全界面
        var rectAnim = new RectAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(480) };
        rectAnim.KeyFrames.Add(new LinearRectKeyFrame(new Rect(0, 0, 2, 2), KeyTime.FromTimeSpan(TimeSpan.Zero)));
        rectAnim.KeyFrames.Add(new LinearRectKeyFrame(new Rect(0, 0, Math.Max(w, 2), 2), KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(220))));
        rectAnim.KeyFrames.Add(new EasingRectKeyFrame(new Rect(0, 0, Math.Max(w, 2), Math.Max(h, 2)),
            KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(480)))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        });
        Storyboard.SetTargetProperty(rectAnim, new PropertyPath("(UIElement.Clip).(RectangleGeometry.Rect)"));
        sb.Children.Add(rectAnim);

        // 动画完成后强制 Clip 恢复全尺寸（防御动画期间窗口尺寸变化导致的裁剪）
        sb.Completed += (_, _) => _clip.Rect = new Rect(0, 0, ActualWidth, ActualHeight);

        _anim = sb;
        sb.Begin(this);
    }

    private void AnimateOut()
    {
        _closeTimer.Stop();
        if (_hiding || !IsVisible)
            return;
        _hiding = true;
        _anim?.Stop(this);
        var w = ActualWidth;
        var h = ActualHeight;

        var sb = new Storyboard();
        // 倒放：全界面向上收缩成顶线（220ms），再向左缩回小线（400ms）
        var rectAnim = new RectAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(400) };
        rectAnim.KeyFrames.Add(new LinearRectKeyFrame(new Rect(0, 0, Math.Max(w, 2), Math.Max(h, 2)), KeyTime.FromTimeSpan(TimeSpan.Zero)));
        rectAnim.KeyFrames.Add(new LinearRectKeyFrame(new Rect(0, 0, Math.Max(w, 2), 2), KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(220))));
        rectAnim.KeyFrames.Add(new EasingRectKeyFrame(new Rect(0, 0, 2, 2),
            KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(400)))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn },
        });
        Storyboard.SetTargetProperty(rectAnim, new PropertyPath("(UIElement.Clip).(RectangleGeometry.Rect)"));
        sb.Children.Add(rectAnim);

        sb.Completed += (_, _) =>
        {
            Hide();
            _mouseCatcher.Stop();
            _hiding = false;
        };
        _anim = sb;
        sb.Begin(this);
    }

    private void OnFilterChanged(object sender, System.Windows.RoutedEventArgs e) => Reload();

    /// <summary>单击循环切换来源：全部 → 手机 → 本机 → 全部。</summary>
    private void OnSourceFilterCycle(object sender, System.Windows.RoutedEventArgs e)
    {
        _sourceFilter = _sourceFilter switch
        {
            "" => "phone",
            "phone" => "local",
            _ => "",
        };
        UpdateSourceIcon();
        Reload();
    }

    private void OnSourceMenuOpened(object sender, RoutedEventArgs e)
    {
        // 菜单打开期间暂停失焦关闭（与条目右键菜单一致），否则 150ms 后窗口会自动收起
        _closeTimer.Stop();
        _menuOpen = true;
        // 菜单打开（控件已加载）后预选当前状态对应的菜单项（视觉反馈）
        if (SourceMenuAll is not null)
            SourceMenuAll.IsChecked = _sourceFilter == "";
        if (SourceMenuLocal is not null)
            SourceMenuLocal.IsChecked = _sourceFilter == "local";
        if (SourceMenuPhone is not null)
            SourceMenuPhone.IsChecked = _sourceFilter == "phone";
    }

    private void OnSourceMenuSelect(object sender, System.Windows.RoutedEventArgs e)
    {
        _keepOpenAfterMenu = true; // 非粘贴操作：选择来源筛选后保持列表打开，便于查看筛选结果
        _sourceFilter = sender switch
        {
            System.Windows.Controls.MenuItem { Tag: "local" } => "local",
            System.Windows.Controls.MenuItem { Tag: "phone" } => "phone",
            _ => "",
        };
        UpdateSourceIcon();
        Reload();
    }

    private void OnSourceMenuClosed(object sender, RoutedEventArgs e)
    {
        _menuOpen = false;
        // 与条目右键菜单一致：非粘贴操作（选了来源）后保持打开；否则恢复失焦自动关闭
        if (_keepOpenAfterMenu)
            return;
        if (IsVisible && !IsActive)
        {
            _closeTimer.Stop();
            _closeTimer.Start();
        }
    }

    /// <summary>切换图标三态显示并更新悬浮提示（当前状态 + 操作说明）。</summary>
    private void UpdateSourceIcon()
    {
        SourceIconAll.Visibility = _sourceFilter == "" ? Visibility.Visible : Visibility.Collapsed;
        SourceIconPhone.Visibility = _sourceFilter == "phone" ? Visibility.Visible : Visibility.Collapsed;
        SourceIconLocal.Visibility = _sourceFilter == "local" ? Visibility.Visible : Visibility.Collapsed;
        var state = _sourceFilter switch
        {
            "" => "全部来源",
            "local" => "只看本机复制",
            _ => "只看手机同步",
        };
        SourceFilterBtn.ToolTip = $"{state} — 单击切换（全部→手机→本机），右键直接选择";
    }

    /// <summary>用系统默认程序打开文件（数据目录内副本先复制到临时目录，防 WPS 等默认程序重存改写）。</summary>
    private static void OpenFile(string path, long entryId = 0) => FileOpener.Open(path, entryId);

    private void Reload()
    {
        if (HistoryList is null)
            return;
        var type = FilterText.IsChecked == true ? "text"
            : FilterImage.IsChecked == true ? "image"
            : FilterFile.IsChecked == true ? "file"
            : null;
        var source = _sourceFilter.Length > 0 ? _sourceFilter : null;
        var items = _store.Query(SearchBox.Text, type, source);
        HistoryList.ItemsSource = items;
        HistoryList.SelectedIndex = items.Count > 0 ? 0 : -1;
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                e.Handled = true;
                RequestHide();
                break;
            case Key.Down:
                e.Handled = true;
                MoveSelection(1);
                break;
            case Key.Up:
                e.Handled = true;
                MoveSelection(-1);
                break;
            case Key.Enter:
                e.Handled = true;
                PasteSelected(plainText: false);
                break;
            case Key.V when Keyboard.Modifiers.HasFlag(ModifierKeys.Control):
                e.Handled = true;
                PasteSelected(plainText: true);
                break;
        }
        base.OnPreviewKeyDown(e);
    }

    private void MoveSelection(int delta)
    {
        if (HistoryList.Items.Count == 0)
            return;
        var idx = HistoryList.SelectedIndex;
        idx = idx < 0 ? 0 : Math.Clamp(idx + delta, 0, HistoryList.Items.Count - 1);
        HistoryList.SelectedIndex = idx;
        HistoryList.ScrollIntoView(HistoryList.Items[idx]);
    }

    private void PasteSelected(bool plainText)
    {
        if (HistoryList.SelectedItem is not Entry entry)
            return;
        // 列表条目不含原图 BLOB，粘贴前按 id 取完整条目（图片回贴必需）
        var full = _store.GetById(entry.Id) ?? entry;
        var plain = plainText || _settings.PastePlainText;
        Paster.Paste(_monitor, full, plain);
        RequestHide();
    }

    // ---- 右键快捷菜单：置顶 / 删除 / 预览 / 粘贴 ----

    private static Entry? ContextEntry(object sender)
        => sender is MenuItem { Parent: ContextMenu cm } && cm.PlacementTarget is FrameworkElement fe
            ? fe.DataContext as Entry
            : null;

    private void OnContextMenuOpened(object sender, RoutedEventArgs e)
    {
        // 菜单打开期间暂停失焦关闭，否则菜单会随列表一起消失导致点击无效
        _closeTimer.Stop();
        _menuOpen = true;
        if (sender is ContextMenu cm && cm.PlacementTarget is FrameworkElement fe
            && fe.DataContext is Entry entry && cm.Items.Count > 0 && cm.Items[0] is MenuItem pin)
            pin.Header = entry.Pinned ? "取消置顶" : "置顶";
    }

    private bool _keepOpenAfterMenu; // 右键非粘贴操作后保持列表打开（粘贴已主动关闭）

    private void OnContextMenuClosed(object sender, RoutedEventArgs e)
    {
        _menuOpen = false;
        if (_keepOpenAfterMenu)
        {
            // 置顶/删除/预览/另存为 不关闭列表：菜单关闭后不再启动失焦定时器，
            // 保持打开状态由 Deactivated 处理器尊重；列表重新激活（Activated）时清除
            return;
        }
        // 菜单关闭后恢复失焦关闭逻辑
        if (IsVisible && !IsActive)
        {
            _closeTimer.Stop();
            _closeTimer.Start();
        }
    }

    private void OnPin(object sender, RoutedEventArgs e)
    {
        if (ContextEntry(sender) is not Entry entry)
            return;
        _keepOpenAfterMenu = true; // 非粘贴操作不关闭列表
        (Application.Current as App)?.SyncService?.SetPinned(entry, !entry.Pinned);
        Reload();
    }

    private void OnDelete(object sender, RoutedEventArgs e)
    {
        if (ContextEntry(sender) is not Entry entry)
            return;
        _keepOpenAfterMenu = true; // 非粘贴操作不关闭列表
        var dlg = new DeleteDialog(entry) { Owner = this };
        if (dlg.ShowDialog() != true)
            return;
        (Application.Current as App)?.SyncService?.DeleteEntry(entry, dlg.Fully);
        Reload();
    }

    private void OnPreview(object sender, RoutedEventArgs e)
    {
        if (ContextEntry(sender) is not Entry entry)
            return;
        _keepOpenAfterMenu = true; // 非粘贴操作不关闭列表
        // 预览后把该条目移到列表顶部（非置顶），便于知道刚才预览的是哪一条
        _store.TouchById(entry.Id);
        switch (entry.Type)
        {
            case "image":
                (_preview ??= new ImagePreviewWindow()).ShowImage(_store, entry);
                break;
            case "text":
                (_textPreview ??= new TextPreviewWindow()).ShowText(entry);
                break;
            default:
                OpenFile(entry.Content, entry.Id);
                break;
        }
        Reload();
    }

    private void OnPaste(object sender, RoutedEventArgs e)
    {
        if (ContextEntry(sender) is not Entry entry)
            return;
        // 列表条目不含原图 BLOB，粘贴前取完整条目
        var full = _store.GetById(entry.Id) ?? entry;
        Paster.Paste(_monitor, full, _settings.PastePlainText);
        RequestHide();
    }

    private void OnPastePlain(object sender, RoutedEventArgs e)
    {
        if (ContextEntry(sender) is not Entry entry)
            return;
        var full = _store.GetById(entry.Id) ?? entry;
        // 强制纯文本：文本=内容、文件=路径、图片=尺寸信息
        Paster.Paste(_monitor, full, plainTextOnly: true);
        RequestHide();
    }

    /// <summary>另存为：文本写入 .txt、图片/文件复制原文件到指定位置（图片默认扩展名按真实格式）。</summary>
    private void OnSaveAs(object sender, RoutedEventArgs e)
    {
        if (ContextEntry(sender) is not Entry entry)
            return;
        _keepOpenAfterMenu = true; // 非粘贴操作不关闭列表
        var full = _store.GetById(entry.Id) ?? entry;
        var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        string fileName, filter;
        if (full.Type == "text")
        {
            fileName = $"剪贴板文本_{stamp}.txt";
            filter = "文本文件 (*.txt)|*.txt|所有文件 (*.*)|*.*";
        }
        else
        {
            // 图片/文件：默认扩展名按真实格式（手机端 JPEG 内容可能存成 .png 扩展名）
            var ext = full.Type == "image" ? RealImageExt(full.Content) : Path.GetExtension(full.Content);
            fileName = full.Type == "image"
                ? $"剪贴板图片_{stamp}{ext}"
                : Path.GetFileName(full.Content);
            filter = "所有文件 (*.*)|*.*";
        }
        var dlg = new SaveFileDialog
        {
            Title = "另存为",
            FileName = fileName,
            Filter = filter,
            AddExtension = true,
        };
        if (dlg.ShowDialog(this) != true)
            return;
        // “所有文件”过滤下 AddExtension 不生效：用户重命名时删掉后缀会存成无后缀文件，这里自动补回
        var savePath = dlg.FileName;
        var defaultExt = full.Type == "text" ? ".txt"
            : full.Type == "image" ? RealImageExt(full.Content)
            : Path.GetExtension(full.Content);
        if (!string.IsNullOrEmpty(defaultExt) && string.IsNullOrEmpty(Path.GetExtension(savePath)))
            savePath += defaultExt;
        try
        {
            if (full.Type == "text")
                File.WriteAllText(savePath, full.Content, new UTF8Encoding(false));
            else
            {
                if (string.IsNullOrEmpty(full.Content) || !File.Exists(full.Content))
                {
                    MessageBox.Show(this, "原文件已不存在，无法另存为。", "剪贴板助手",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                File.Copy(full.Content, savePath, overwrite: true);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"另存为失败：{ex.Message}", "剪贴板助手",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>按文件头识别真实图片格式扩展名（.png/.jpg/.gif/.bmp/.webp），识别失败回退 .png。</summary>
    private static string RealImageExt(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            var head = new byte[12];
            if (fs.Read(head, 0, head.Length) < head.Length)
                return ".png";
            if (head[0] == 0x89 && head[1] == 0x50 && head[2] == 0x4E && head[3] == 0x47) return ".png";
            if (head[0] == 0xFF && head[1] == 0xD8) return ".jpg";
            if (head[0] == 'G' && head[1] == 'I' && head[2] == 'F') return ".gif";
            if (head[0] == 'B' && head[1] == 'M') return ".bmp";
            if (head[0] == 'R' && head[1] == 'I' && head[2] == 'F' && head[3] == 'F') return ".webp";
            return ".png";
        }
        catch (Exception)
        {
            return ".png";
        }
    }
}

/// <summary>byte[] PNG → BitmapSource，有界缓存（LRU，上限 60，冻结对象可完整回收）。</summary>
public sealed class ThumbnailConverter : System.Windows.Data.IValueConverter
{
    // 列表可见条目约 15-20 条，60 上限足够滚动余量；超出按插入顺序淘汰最旧，
    // 避免缓存随截图总数无限增长（截图多时内存不会持续扩大）
    private const int MaxCache = 60;
    private static readonly Dictionary<string, BitmapSource> Cache = new();

    public object Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
    {
        if (value is not byte[] png)
            return DependencyProperty.UnsetValue;
        // 缩略图 BLOB 相同则内容哈希相同（PNG 头/IHDR 前缀对同尺寸图相同，不能用前缀做 key）
        var key = System.Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(png));
        if (Cache.TryGetValue(key, out var cached))
            return cached;
        using var ms = new MemoryStream(png);
        var decoder = new PngBitmapDecoder(ms, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        var bmp = decoder.Frames[0];
        if (bmp.CanFreeze)
            bmp.Freeze(); // 冻结后 WPF 可释放中间像素，缓存对象可被 GC 完整回收
        if (Cache.Count >= MaxCache)
        {
            // Dictionary 保持插入顺序，淘汰最先插入的条目
            var oldest = Cache.Keys.First();
            Cache.Remove(oldest);
        }
        Cache[key] = bmp;
        return bmp;
    }

    public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>文本非空 → 隐藏（用于占位提示文字）。支持 string 与 int(Length) 两种绑定值。</summary>
public sealed class StringToVisibilityConverter : System.Windows.Data.IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
    {
        var len = value switch
        {
            string s => s.Length,
            int i => i,
            _ => 0,
        };
        return len > 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        => throw new NotSupportedException();
}