using System.Diagnostics;
using System.Net.Http;
using System.Reflection;

namespace ClipboardTool;

/// <summary>
/// 自动更新：从更新服务器（code.starry0214.one nginx 静态服务）检测最新版本、下载 exe，
/// 并通过批处理脚本替换重启。
/// </summary>
public static class Updater
{
    /// <summary>更新服务器地址（code.starry0214.one nginx 静态服务，走 HTTPS）。</summary>
    public const string UpdateBaseUrl = "https://code.starry0214.one/updates";

    /// <summary>当前程序版本（来自程序集版本，如 1.2.0）。</summary>
    public static string CurrentVersion { get; } =
        Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0";

    /// <summary>查询服务器上的最新版本号。网络不可达或解析失败返回 null。</summary>
    public static async Task<string?> CheckAsync()
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        try
        {
            var text = await http.GetStringAsync($"{UpdateBaseUrl}/version.txt");
            var v = text.Trim().TrimStart('v');
            return string.IsNullOrEmpty(v) ? null : v;
        }
        catch (Exception)
        {
            return null;
        }
    }

    public static bool IsNewer(string latest) =>
        Version.TryParse(latest, out var v) && v > Version.Parse(CurrentVersion);

    /// <summary>下载进度：已下载字节 / 总字节（未知为 0）/ 瞬时速度（字节每秒）。</summary>
    public readonly record struct DownloadProgress(long BytesReceived, long TotalBytes, double BytesPerSecond);

    /// <summary>下载最新版 exe 到数据目录 updates/ 下，返回本地路径；失败返回 null。</summary>
    public static async Task<string?> DownloadAsync(string dataDir, IProgress<DownloadProgress>? progress = null)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        try
        {
            var dir = Path.Combine(dataDir, "updates");
            Directory.CreateDirectory(dir);
            var dest = Path.Combine(dir, "ClipboardTool.new.exe");
            using var response = await http.GetAsync($"{UpdateBaseUrl}/ClipboardTool.exe",
                HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();
            var total = response.Content.Headers.ContentLength ?? 0;
            await using var src = await response.Content.ReadAsStreamAsync();
            await using var dst = File.Create(dest);
            var buffer = new byte[81920];
            long received = 0;
            var sw = Stopwatch.StartNew();
            long lastBytes = 0;
            var lastTick = sw.Elapsed;
            while (true)
            {
                var n = await src.ReadAsync(buffer);
                if (n == 0)
                    break;
                await dst.WriteAsync(buffer.AsMemory(0, n));
                received += n;
                var now = sw.Elapsed;
                if (progress is not null && now - lastTick >= TimeSpan.FromMilliseconds(250))
                {
                    var speed = (received - lastBytes) / (now - lastTick).TotalSeconds;
                    lastBytes = received;
                    lastTick = now;
                    progress.Report(new DownloadProgress(received, total, speed));
                }
            }
            progress?.Report(new DownloadProgress(received, total, 0));
            return dest;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>通过批处理脚本替换当前 exe 并重启（当前进程先退出释放文件锁）。</summary>
    public static void Apply(string newExePath, string currentExePath)
    {
        var bat = Path.Combine(Path.GetTempPath(), $"clipboard_updater_{Guid.NewGuid():N}.bat");
        File.WriteAllText(bat,
            $"@echo off\r\n" +
            $"timeout /t 3 /nobreak >nul\r\n" +
            $"copy /y \"{newExePath}\" \"{currentExePath}\" >nul\r\n" +
            $"del \"{newExePath}\" >nul 2>&1\r\n" +
            $"start \"\" \"{currentExePath}\"\r\n" +
            $"del \"%~f0\"\r\n");
        Process.Start(new ProcessStartInfo
        {
            FileName = bat,
            WindowStyle = ProcessWindowStyle.Hidden,
            UseShellExecute = true,
        });
    }
}
