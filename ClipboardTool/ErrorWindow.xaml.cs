using System.Windows;
using MessageBox = System.Windows.MessageBox;

namespace ClipboardTool;

/// <summary>错误/反馈窗口：显示错误信息，提供一键上报日志。</summary>
public partial class ErrorWindow : Window
{
    private ErrorWindow(string title, string message)
    {
        InitializeComponent();
        Title = title;
        TitleText.Text = title;
        DetailBox.Text = message;
    }

    /// <summary>未处理异常弹窗（UI 线程）。</summary>
    public static void ShowError(string title, string message) =>
        new ErrorWindow(title, message).Show();

    /// <summary>托盘“反馈问题”入口：无错误上下文。</summary>
    public static void ShowFeedback() =>
        new ErrorWindow("反馈问题", "遇到了问题？点击“上报日志”把最近的日志发给开发者，方便定位问题。").Show();

    private async void OnUpload(object sender, RoutedEventArgs e)
    {
        BtnUpload.IsEnabled = false;
        BtnUpload.Content = "正在上报…";
        try
        {
            var ok = await Log.UploadAsync();
            MessageBox.Show(ok ? "日志已上报，感谢反馈！" : "上报失败，请检查网络后重试。",
                ok ? "上报成功" : "上报失败",
                MessageBoxButton.OK, ok ? MessageBoxImage.Information : MessageBoxImage.Warning);
        }
        finally
        {
            BtnUpload.IsEnabled = true;
            BtnUpload.Content = "上报日志";
        }
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
