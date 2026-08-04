using System.Windows;

namespace ClipboardTool;

public partial class MainWindow : Window
{
    private readonly ClipboardStore _store;
    private readonly Settings _settings;

    public MainWindow(ClipboardStore store, Settings settings)
    {
        InitializeComponent();
        _store = store;
        _settings = settings;
        SearchBox.TextChanged += (_, _) => Refresh();
        HistoryList.MouseDoubleClick += (_, _) => Refresh();
        Closing += (_, e) =>
        {
            // 主窗口关闭即隐藏到托盘，真正的退出走托盘菜单
            e.Cancel = true;
            Hide();
        };
        Refresh();
    }

    public void Refresh()
    {
        HistoryList.ItemsSource = _store.Query(SearchBox.Text);
        HistoryList.SelectedIndex = -1;
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
        var result = MessageBox.Show(this, "确定要清空全部历史记录吗？", "清空历史",
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
