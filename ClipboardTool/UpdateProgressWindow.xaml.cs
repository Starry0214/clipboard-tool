using System.Windows;

namespace ClipboardTool;

/// <summary>更新下载进度窗口：进度条 + 百分比 + 速度 + 预计剩余时间。</summary>
public partial class UpdateProgressWindow : Window
{
    public UpdateProgressWindow()
    {
        InitializeComponent();
    }

    /// <summary>更新进度显示（IProgress 回调，已自动切回 UI 线程）。</summary>
    public void Report(Updater.DownloadProgress p)
    {
        var pct = p.TotalBytes > 0 ? p.BytesReceived * 100.0 / p.TotalBytes : 0.0;
        Bar.Value = pct;
        StatusText.Text = p.TotalBytes > 0
            ? $"{FormatSize(p.BytesReceived)} / {FormatSize(p.TotalBytes)}（{pct:F0}%）"
            : $"{FormatSize(p.BytesReceived)}";
        SpeedText.Text = p.BytesPerSecond > 0 && p.BytesReceived < p.TotalBytes
            ? $"{FormatSize(p.BytesPerSecond)}/s · 预计剩余 {FormatEta((p.TotalBytes - p.BytesReceived) / p.BytesPerSecond)}"
            : "";
    }

    private static string FormatSize(double bytes)
    {
        string[] units = ["B", "KB", "MB", "GB"];
        var i = 0;
        var v = bytes;
        while (v >= 1024 && i < units.Length - 1)
        {
            v /= 1024;
            i++;
        }
        return i == 0 ? $"{v:F0} {units[i]}" : $"{v:F1} {units[i]}";
    }

    private static string FormatEta(double seconds) =>
        seconds < 60 ? $"{Math.Max(1, (int)seconds)} 秒"
        : seconds < 3600 ? $"{Math.Max(1, (int)(seconds / 60))} 分钟"
        : $"{seconds / 3600:F1} 小时";
}
