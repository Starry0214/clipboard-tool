using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
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

    public OverlayWindow(ClipboardStore store, ClipboardMonitor monitor, Settings settings)
    {
        InitializeComponent();
        _store = store;
        _monitor = monitor;
        _settings = settings;
        FilterAll.IsChecked = true; // 默认"全部"（在 InitializeComponent 之后设置，避免 XAML 初始化时控件未就绪）
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
        // 双击：图片→大图预览；文本→全文预览；文件→用默认程序打开
        HistoryList.MouseDoubleClick += (_, _) =>
        {
            if (HistoryList.SelectedItem is not Entry entry)
                return;
            switch (entry.Type)
            {
                case "image":
                    (_preview ??= new ImagePreviewWindow()).ShowImage(_store, entry);
                    break;
                case "text":
                    (_textPreview ??= new TextPreviewWindow()).ShowText(entry);
                    break;
                default:
                    OpenFile(entry.Content);
                    break;
            }
        };
        // 失焦时延迟确认再关闭（避免与热键重按竞争）
        Deactivated += (_, _) =>
        {
            _closeTimer.Stop();
            _closeTimer.Start();
        };
        // 点击列表外部区域 → 等效 Esc 关闭
        _mouseCatcher.OutsideClick += RequestHide;
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

        var screen = System.Windows.Forms.Screen.FromPoint(new System.Drawing.Point((int)cursor.X, (int)cursor.Y));
        var wa = screen.WorkingArea;
        // 窗口左上角对齐鼠标位置；超出屏幕边缘时向回校正
        double x = cursor.X, y = cursor.Y;
        if (x + ActualWidth > wa.Right)
            x = Math.Max(wa.Left, cursor.X - ActualWidth);
        if (y + ActualHeight > wa.Bottom)
            y = Math.Max(wa.Top, cursor.Y - ActualHeight);
        Left = x;
        Top = y;

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

    /// <summary>用系统默认程序打开文件（文件历史条目的双击行为）。</summary>
    private static void OpenFile(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
        }
        catch (Exception)
        {
            // 文件不存在或无法打开时静默忽略
        }
    }

    private void Reload()
    {
        if (HistoryList is null)
            return;
        var type = FilterText.IsChecked == true ? "text"
            : FilterImage.IsChecked == true ? "image"
            : FilterFile.IsChecked == true ? "file"
            : null;
        var items = _store.Query(SearchBox.Text, type);
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
        // 图片条目始终粘贴原图；文本默认纯文本写入
        var plain = plainText || _settings.PastePlainText;
        Paster.Paste(_monitor, entry, plain);
        RequestHide();
    }

    // ---- 右键快捷菜单：置顶 / 删除 / 预览 / 粘贴 ----

    private static Entry? ContextEntry(object sender)
        => sender is MenuItem { Parent: ContextMenu cm } && cm.PlacementTarget is FrameworkElement fe
            ? fe.DataContext as Entry
            : null;

    private void OnContextMenuOpened(object sender, RoutedEventArgs e)
    {
        if (sender is ContextMenu cm && cm.PlacementTarget is FrameworkElement fe
            && fe.DataContext is Entry entry && cm.Items.Count > 0 && cm.Items[0] is MenuItem pin)
            pin.Header = entry.Pinned ? "取消置顶" : "置顶";
    }

    private void OnPin(object sender, RoutedEventArgs e)
    {
        if (ContextEntry(sender) is not Entry entry)
            return;
        _store.SetPinned(entry.Id, !entry.Pinned);
        Reload();
    }

    private void OnDelete(object sender, RoutedEventArgs e)
    {
        if (ContextEntry(sender) is not Entry entry)
            return;
        _store.Delete(entry.Id);
        Reload();
    }

    private void OnPreview(object sender, RoutedEventArgs e)
    {
        if (ContextEntry(sender) is not Entry entry)
            return;
        switch (entry.Type)
        {
            case "image":
                (_preview ??= new ImagePreviewWindow()).ShowImage(_store, entry);
                break;
            case "text":
                (_textPreview ??= new TextPreviewWindow()).ShowText(entry);
                break;
            default:
                OpenFile(entry.Content);
                break;
        }
    }

    private void OnPaste(object sender, RoutedEventArgs e)
    {
        if (ContextEntry(sender) is not Entry entry)
            return;
        Paster.Paste(_monitor, entry, _settings.PastePlainText);
        RequestHide();
    }
}

/// <summary>byte[] PNG → BitmapSource，带简单缓存。</summary>
public sealed class ThumbnailConverter : System.Windows.Data.IValueConverter
{
    private static readonly Dictionary<string, BitmapSource> Cache = new();

    public object Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
    {
        if (value is not byte[] png)
            return DependencyProperty.UnsetValue;
        var key = System.Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(png))[..16];
        if (Cache.TryGetValue(key, out var cached))
            return cached;
        using var ms = new MemoryStream(png);
        var decoder = new PngBitmapDecoder(ms, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        var bmp = decoder.Frames[0];
        if (Cache.Count > 200)
            Cache.Clear();
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
