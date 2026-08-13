using System.Windows;
using System.Windows.Media;

namespace ClipboardTool;

/// <summary>
/// Fluent 风格统一确认对话框（替代系统 MessageBox 的确认场景）。
/// 支持危险操作（确认按钮红色）与普通确认（强调蓝），图标为警示/信息两种。
/// </summary>
public partial class ConfirmDialog : Window
{
    /// <summary>用户是否点击了确认按钮。</summary>
    public bool Confirmed { get; private set; }

    /// <param name="title">窗口标题与标题文字</param>
    /// <param name="message">正文提示</param>
    /// <param name="confirmText">确认按钮文案</param>
    /// <param name="danger">true=确认按钮用警示红（破坏性操作），false=强调蓝</param>
    /// <param name="subtitle">标题下的小字说明（可选）</param>
    /// <param name="icon">图标类型：warning（默认）/ info</param>
    public ConfirmDialog(string title, string message, string confirmText,
        bool danger = false, string? subtitle = null, string icon = "warning")
    {
        InitializeComponent();
        Title = title;
        TitleText.Text = title;
        MessageText.Text = message;
        ConfirmBtn.Content = confirmText;
        ConfirmBtn.Style = (Style)FindResource(danger ? "DangerButton" : "FluentButton");
        if (!string.IsNullOrEmpty(subtitle))
            SubtitleText.Text = subtitle;
        else
            SubtitleText.Visibility = Visibility.Collapsed;
        // Segoe MDL2 Assets：\uE7BA 警示圆 / \uE946 信息圆
        if (icon == "info")
        {
            IconText.Text = "\uE946";
            IconCircle.Background = new SolidColorBrush(Color.FromRgb(0x00, 0x78, 0xD4));
        }
        else
        {
            IconText.Text = "\uE7BA";
            IconCircle.Background = new SolidColorBrush(Color.FromRgb(0xF7, 0x63, 0x00));
        }
    }

    private void OnConfirm(object sender, RoutedEventArgs e)
    {
        Confirmed = true;
        DialogResult = true;
    }
}
