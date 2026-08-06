using System.Diagnostics;
using System.Windows;

namespace ClipboardTool;

public partial class MainWindow : Window
{
    private readonly ClipboardStore _store;
    private readonly Settings _settings;
    private ImagePreviewWindow? _preview;
    private TextPreviewWindow? _textPreview;

    public MainWindow(ClipboardStore store, Settings settings)
    {
        InitializeComponent();
        _store = store;
        _settings = settings;
        SearchBox.TextChanged += (_, _) => Refresh();
        FilterAll.IsChecked = true;
        // 双击：图片→大图预览；文本→全文预览；文件→用默认程序打开
        HistoryList.MouseDoubleClick += (_, _) =>
        {
            if (HistoryList.SelectedItem is not Entry entry)
                return;
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
                    OpenFile(entry.Content);
                    break;
            }
            Refresh();
        };
        Closing += (_, e) =>
        {
            // 主窗口关闭即隐藏到托盘，真正的退出走托盘菜单
            e.Cancel = true;
            Hide();
        };
        // 从任务栏/托盘重新打开时自动刷新列表（最小化恢复、遮挡后激活都覆盖）
        Activated += (_, _) => Refresh();
        Refresh();
    }

    public void Refresh()
    {
        if (HistoryList is null)
            return;
        var type = FilterText.IsChecked == true ? "text"
            : FilterImage.IsChecked == true ? "image"
            : FilterFile.IsChecked == true ? "file"
            : null;
        HistoryList.ItemsSource = _store.Query(SearchBox.Text, type);
        HistoryList.SelectedIndex = -1;
    }

    private void OnFilterChanged(object sender, System.Windows.RoutedEventArgs e) => Refresh();

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

    private void OnPin(object sender, RoutedEventArgs e)
    {
        if (HistoryList.SelectedItem is not Entry entry)
            return;
        _store.SetPinned(entry.Id, !entry.Pinned);
        Refresh();
    }

    private void OnDelete(object sender, RoutedEventArgs e)
    {
        if (HistoryList.SelectedItem is not Entry entry)
            return;
        _store.Delete(entry.Id);
        Refresh();
    }

    private void OnClear(object sender, RoutedEventArgs e)
    {
        var result = MessageBox.Show(this, "确定要清空全部历史记录吗？置顶条目将保留。", "清空历史",
            MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (result != MessageBoxResult.Yes)
            return;
        _store.Clear();
        Refresh();
    }

    private void OnSettings(object sender, RoutedEventArgs e)
    {
        var win = new SettingsWindow(_settings);
        win.Owner = this;
        win.ShowDialog();
        if (win.Applied && Application.Current is App app)
            app.ApplySettings();
    }

    private void OnRefresh(object sender, RoutedEventArgs e) => Refresh();
}
