using System.Windows;

namespace ClipboardTool;

/// <summary>
/// 清空历史选择对话框：本机清空（仅本机、保留置顶）或彻底清空（多端同步、保留置顶）。
/// 单层交互——点"清空"直接弹出选择，不再套二级菜单/二次确认框。
/// </summary>
public partial class ClearDialog : Window
{
    /// <summary>true=彻底清空（多端），false=本机清空。</summary>
    public bool Fully { get; private set; }

    /// <param name="loggedIn">已登录同步时显示彻底清空选项，否则只可本机清空。</param>
    public ClearDialog(bool loggedIn)
    {
        InitializeComponent();
        FullClearBtn.Visibility = loggedIn ? Visibility.Visible : Visibility.Collapsed;
        var fullHint = loggedIn
            ? "彻底清空（多端）：同步清空所有设备，服务器数据 7 天内自动清除（期间可从服务器恢复误删）"
            : "";
        HintText.Text = $"将清空全部历史记录，置顶条目将保留。\n\n" +
                        $"本机清空：仅清除本机历史，不影响其他设备\n{fullHint}";
    }

    private void OnLocalClear(object sender, RoutedEventArgs e)
    {
        Fully = false;
        DialogResult = true;
    }

    private void OnFullClear(object sender, RoutedEventArgs e)
    {
        Fully = true;
        DialogResult = true;
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}
