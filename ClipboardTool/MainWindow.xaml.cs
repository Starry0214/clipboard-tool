using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;

namespace ClipboardTool;

public partial class MainWindow : Window
{
    private readonly ClipboardStore _store;
    private readonly Settings _settings;
    private ImagePreviewWindow? _preview;
    private TextPreviewWindow? _textPreview;

    /// <summary>来源筛选状态：""=全部、"local"=本机、"phone"=手机。</summary>
    private string _sourceFilter = "";

    public MainWindow(ClipboardStore store, Settings settings)
    {
        InitializeComponent();
        _store = store;
        _settings = settings;
        VersionText.Text = $"v{Updater.CurrentVersion}";
        // 未开启多端同步时来源筛选无意义，隐藏
        SourceFilterPanel.Visibility = _settings.SyncEnabled ? Visibility.Visible : Visibility.Collapsed;
        SearchBox.TextChanged += (_, _) => Refresh();
        UpdateSourceIcon();
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
        Activated += (_, _) =>
        {
            // 同步开关变化后重新打开主窗口时同步来源筛选显隐
            SourceFilterPanel.Visibility = _settings.SyncEnabled ? Visibility.Visible : Visibility.Collapsed;
            Refresh();
        };
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
        var source = _sourceFilter.Length > 0 ? _sourceFilter : null;
        HistoryList.ItemsSource = _store.Query(SearchBox.Text, type, source);
        HistoryList.SelectedIndex = -1;
    }

    private void OnFilterChanged(object sender, System.Windows.RoutedEventArgs e) => Refresh();

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
        Refresh();
    }

    private void OnSourceMenuOpened(object sender, RoutedEventArgs e)
    {
        // 菜单打开（控件已加载）后预选当前状态对应的菜单项（视觉反馈）
        if (SourceMenuAll is not null)
            SourceMenuAll.IsChecked = _sourceFilter == "";
        if (SourceMenuLocal is not null)
            SourceMenuLocal.IsChecked = _sourceFilter == "local";
        if (SourceMenuPhone is not null)
            SourceMenuPhone.IsChecked = _sourceFilter == "phone";
    }

    private void OnSourceMenuSelect(object sender, RoutedEventArgs e)
    {
        _sourceFilter = sender switch
        {
            MenuItem { Tag: "local" } => "local",
            MenuItem { Tag: "phone" } => "phone",
            _ => "",
        };
        UpdateSourceIcon();
        Refresh();
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
        (Application.Current as App)?.SyncService?.SetPinned(entry, !entry.Pinned);
        Refresh();
    }

    private void OnDelete(object sender, RoutedEventArgs e)
    {
        if (HistoryList.SelectedItem is not Entry entry)
            return;
        var dlg = new DeleteDialog(entry) { Owner = this };
        if (dlg.ShowDialog() != true)
            return;
        (Application.Current as App)?.SyncService?.DeleteEntry(entry, dlg.Fully);
        Refresh();
    }

    private void OnClear(object sender, RoutedEventArgs e)
    {
        // 单层交互：直接弹出清空选择对话框（本机清空 / 彻底清空（多端）），不再套二级菜单与二次确认
        var loggedIn = (Application.Current as App)?.SyncService?.LoggedIn == true;
        var dlg = new ClearDialog(loggedIn) { Owner = this };
        if (dlg.ShowDialog() != true)
            return;
        (Application.Current as App)?.SyncService?.ClearAll(dlg.Fully);
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
