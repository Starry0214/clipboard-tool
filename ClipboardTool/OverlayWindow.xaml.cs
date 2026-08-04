using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using Point = System.Windows.Point;

namespace ClipboardTool;

public partial class OverlayWindow : Window
{
    private readonly ClipboardStore _store;
    private readonly ClipboardMonitor _monitor;
    private readonly Settings _settings;

    public OverlayWindow(ClipboardStore store, ClipboardMonitor monitor, Settings settings)
    {
        InitializeComponent();
        _store = store;
        _monitor = monitor;
        _settings = settings;
        SearchBox.TextChanged += (_, _) => Reload();
        HistoryList.MouseDoubleClick += (_, _) => PasteSelected(plainText: false);
        // 窗口复用：关闭路径一律 Hide，避免 WPF 窗口 Close 后不可再 Show
        Deactivated += (_, _) => Hide();
    }

    public void ShowAt(Point cursor, List<Entry> items)
    {
        Paster.TargetWindow = NativeMethods.GetForegroundWindow();

        Show();
        UpdateLayout();

        // 位置校正须在 Show 之后（SizeToContent 下 ActualHeight 才有效）
        var screen = System.Windows.Forms.Screen.FromPoint(new System.Drawing.Point((int)cursor.X, (int)cursor.Y));
        var wa = screen.WorkingArea;
        double x = cursor.X + 8, y = cursor.Y + 8;
        if (x + ActualWidth > wa.Right)
            x = Math.Max(wa.Left, cursor.X - ActualWidth - 8);
        if (y + ActualHeight > wa.Bottom)
            y = Math.Max(wa.Top, cursor.Y - ActualHeight - 8);
        Left = x;
        Top = y;

        Activate();
        HistoryList.ItemsSource = items;
        HistoryList.SelectedIndex = items.Count > 0 ? 0 : -1;
        SearchBox.Text = "";
        Keyboard.Focus(SearchBox);
    }

    private void Reload()
    {
        var items = _store.Query(SearchBox.Text);
        HistoryList.ItemsSource = items;
        HistoryList.SelectedIndex = items.Count > 0 ? 0 : -1;
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                e.Handled = true;
                Hide();
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
        Hide();
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

/// <summary>字符串长度 → Visibility：非空显示，空隐藏。</summary>
public sealed class StringToVisibilityConverter : System.Windows.Data.IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
        => value is string s && s.Length > 0 ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        => throw new NotSupportedException();
}
