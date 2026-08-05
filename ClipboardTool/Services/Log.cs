using System.Net.Http;
using System.Text;

namespace ClipboardTool;

/// <summary>
/// 轻量文件日志：%LocalAppData%\ClipboardTool\logs\clipboard.log，
/// 单文件 1MB 大小轮转（clipboard.log → .1.log … .4.log，共保留 5 个）。
/// 写失败静默忽略；上报时合并全部日志文件 POST 到更新服务器。
/// </summary>
public static class Log
{
    private static readonly object Lock = new();
    private static string _dir = "";
    private static readonly string UploadUrl = $"{Updater.UpdateBaseUrl.Replace("/updates", "")}/api/logs/upload";

    public static DateTime StartupTime { get; } = DateTime.Now;

    public static void Init(string dataDir)
    {
        lock (Lock)
        {
            _dir = Path.Combine(dataDir, "logs");
            try
            {
                Directory.CreateDirectory(_dir);
            }
            catch (Exception)
            {
                _dir = ""; // 无法建目录则禁用日志
            }
        }
    }

    public static void Info(string msg) => Write("INFO", msg, null);

    public static void Error(string msg, Exception? ex = null) => Write("ERROR", msg, ex);

    private static void Write(string level, string msg, Exception? ex)
    {
        lock (Lock)
        {
            if (string.IsNullOrEmpty(_dir))
                return;
            try
            {
                var path = Path.Combine(_dir, "clipboard.log");
                RollIfNeeded(path);
                var sb = new StringBuilder();
                sb.AppendLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {msg}");
                if (ex is not null)
                {
                    sb.AppendLine($"    {ex.GetType().Name}: {ex.Message}");
                    foreach (var line in (ex.StackTrace ?? "").Split('\n'))
                        if (!string.IsNullOrWhiteSpace(line))
                            sb.AppendLine($"    {line.Trim()}");
                }
                File.AppendAllText(path, sb.ToString(), Encoding.UTF8);
            }
            catch (Exception)
            {
                // 日志失败不影响主流程
            }
        }
    }

    private static void RollIfNeeded(string path)
    {
        var fi = new FileInfo(path);
        if (!fi.Exists || fi.Length < 1024 * 1024)
            return;
        // 最旧（.4.log）先删，然后逐级滚动
        File.Delete(Path.Combine(_dir, "clipboard.4.log"));
        for (var i = 3; i >= 1; i--)
        {
            var src = Path.Combine(_dir, $"clipboard.{i}.log");
            if (File.Exists(src))
                File.Move(src, Path.Combine(_dir, $"clipboard.{i + 1}.log"), true);
        }
        File.Move(path, Path.Combine(_dir, "clipboard.1.log"), true);
    }

    /// <summary>合并全部日志文件（头部附元信息），POST 上报。成功返回 true。</summary>
    public static async Task<bool> UploadAsync()
    {
        try
        {
            var sb = new StringBuilder();
            sb.AppendLine($"=== 剪贴板助手日志上报 {DateTime.Now:yyyy-MM-dd HH:mm:ss} ===");
            sb.AppendLine($"程序版本: {Updater.CurrentVersion}");
            sb.AppendLine($"系统: {Environment.OSVersion} / .NET {Environment.Version}");
            sb.AppendLine($"启动时间: {StartupTime:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine($"数据目录: {Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClipboardTool")}");
            sb.AppendLine();

            string[] files;
            lock (Lock)
            {
                if (string.IsNullOrEmpty(_dir) || !Directory.Exists(_dir))
                    return false;
                files = Directory.GetFiles(_dir, "clipboard*.log")
                    .OrderBy(f => f, StringComparer.Ordinal)
                    .ToArray();
            }
            foreach (var f in files)
            {
                try
                {
                    sb.AppendLine($"----- {Path.GetFileName(f)} -----");
                    sb.Append(File.ReadAllText(f, Encoding.UTF8));
                }
                catch (Exception)
                {
                }
            }

            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            var content = new StringContent(sb.ToString(), Encoding.UTF8, "text/plain");
            using var resp = await http.PostAsync(UploadUrl, content);
            return resp.IsSuccessStatusCode;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
