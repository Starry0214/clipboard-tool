using System.Diagnostics;
using System.Net.Http;
using System.Reflection;
using System.Text.RegularExpressions;

namespace ClipboardTool;

/// <summary>
/// 自动更新：从更新服务器（code.starry0214.one nginx 静态服务）检测最新版本、下载 exe，
/// 并通过批处理脚本替换重启。
/// </summary>
public static class Updater
{
    /// <summary>更新服务器地址（code.starry0214.one nginx 静态服务，走 HTTPS）。</summary>
    public const string UpdateBaseUrl = "https://code.starry0214.one/updates";

    /// <summary>镜像源：域名 HTTPS 优先，IP HTTP 兜底（域名走境外中继，部分政务网连不上）。</summary>
    public static readonly string[] BaseUrls = [UpdateBaseUrl, "http://107.175.228.83:8080"];

    /// <summary>当前程序版本（来自程序集版本，如 1.2.0）。</summary>
    public static string CurrentVersion { get; } =
        Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0";

    /// <summary>
    /// 是否以"引导器解压模式"运行（exe 位于 %LocalAppData%\ClipboardToolApp\）。
    /// 是 → 更新应替换引导器（launcher）；否 → 老架构自包含直跑，更新替换自身。
    /// </summary>
    public static bool IsLauncherMode
    {
        get
        {
            var exe = Environment.ProcessPath ?? "";
            var appDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClipboardToolApp");
            return exe.StartsWith(appDir, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>引导器路径（由引导器启动时写入 launcher_path.txt）。</summary>
    public static string? LauncherPath
    {
        get
        {
            try
            {
                var f = Path.Combine(
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClipboardToolApp"),
                    "launcher_path.txt");
                return File.Exists(f) ? File.ReadAllText(f).Trim() : null;
            }
            catch (Exception)
            {
                return null;
            }
        }
    }

    /// <summary>查询服务器上的最新版本号。所有镜像均不可达或解析失败返回 null。</summary>
    public static async Task<string?> CheckAsync()
    {
        // 慢网络（如政务网）TLS 握手可能超 10s，放宽总超时；连接阶段 15s 快速失败
        foreach (var baseUrl in BaseUrls)
        {
            using var http = new HttpClient(new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(15) })
            {
                Timeout = TimeSpan.FromSeconds(25),
            };
            try
            {
                var text = await http.GetStringAsync($"{baseUrl}/version.txt");
                var v = text.Trim().TrimStart('v');
                return string.IsNullOrEmpty(v) ? null : v;
            }
            catch (Exception)
            {
                // 该镜像不可达，换下一个
            }
        }
        return null;
    }

    public static bool IsNewer(string latest) =>
        Version.TryParse(latest, out var v) && v > Version.Parse(CurrentVersion);

    /// <summary>拉取服务器上的更新简介（notes.txt，≤8KB）；全部失败或为空返回 null。</summary>
    public static async Task<string?> GetNotesAsync()
    {
        foreach (var baseUrl in BaseUrls)
        {
            try
            {
                using var http = new HttpClient(new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(15) })
                {
                    Timeout = TimeSpan.FromSeconds(25),
                };
                var text = await http.GetStringAsync($"{baseUrl}/notes.txt");
                return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
            }
            catch (Exception)
            {
                // 该镜像不可达，换下一个
            }
        }
        return null;
    }

    /// <summary>拉取全量更新日志（changelog.txt，各版本分块）并筛选出高于当前版本的所有条目（最新在前）。失败返回 null。</summary>
    public static async Task<string?> GetChangelogAsync()
    {
        var full = await GetRawChangelogAsync();
        if (full is null)
            return null;
        var blocks = Regex.Split(full, @"(?m)^(?=v\d+\.\d+\.\d+)")
            .Select(b => b.Trim()).Where(b => b.Length > 0).ToList();
        var wanted = new List<string>();
        foreach (var block in blocks)
        {
            var m = Regex.Match(block, @"^v(\d+\.\d+\.\d+)");
            if (m.Success && IsNewer(m.Groups[1].Value))
                wanted.Add(block);
        }
        return wanted.Count == 0 ? null : string.Join("\n\n", wanted);
    }

    private static async Task<string?> GetRawChangelogAsync()
    {
        foreach (var baseUrl in BaseUrls)
        {
            try
            {
                using var http = new HttpClient(new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(15) })
                {
                    Timeout = TimeSpan.FromSeconds(25),
                };
                var text = await http.GetStringAsync($"{baseUrl}/changelog.txt");
                return string.IsNullOrWhiteSpace(text) ? null : text;
            }
            catch (Exception)
            {
                // 该镜像不可达，换下一个
            }
        }
        return null;
    }

    /// <summary>下载进度：已下载字节 / 总字节（未知为 0）/ 瞬时速度（字节每秒）。</summary>
    public readonly record struct DownloadProgress(long BytesReceived, long TotalBytes, double BytesPerSecond);

    /// <summary>下载最新版 exe 到数据目录 updates/ 下，返回本地路径；所有镜像均失败返回 null。</summary>
    public static async Task<string?> DownloadAsync(string dataDir, IProgress<DownloadProgress>? progress = null)
    {
        var dir = Path.Combine(dataDir, "updates");
        Directory.CreateDirectory(dir);
        var dest = Path.Combine(dir, "ClipboardTool.new.exe");
        foreach (var baseUrl in BaseUrls)
        {
            try
            {
                return await DownloadFrom(baseUrl, dest, progress);
            }
            catch (Exception)
            {
                // 该镜像不可达，换下一个
            }
        }
        return null;
    }

    private static async Task<string> DownloadFrom(string baseUrl, string dest,
        IProgress<DownloadProgress>? progress)
    {
        using var http = new HttpClient(new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(15) })
        {
            Timeout = TimeSpan.FromMinutes(10),
        };
        using var response = await http.GetAsync($"{baseUrl}/ClipboardTool.exe",
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

    /// <summary>通过批处理脚本替换当前 exe 并重启（当前进程先退出释放文件锁）。</summary>
    public static void Apply(string newExePath, string currentExePath)
    {
        var bat = Path.Combine(Path.GetTempPath(), $"clipboard_updater_{Guid.NewGuid():N}.bat");
        // bat 按 UTF-8 写入；开头 chcp 65001 切换代码页，否则中文路径在 GBK 默认代码页下乱码导致 copy/start 失败
        File.WriteAllText(bat,
            $"@echo off\r\n" +
            $"chcp 65001 >nul\r\n" +
            $"timeout /t 3 /nobreak >nul\r\n" +
            $"copy /y \"{newExePath}\" \"{currentExePath}\" >nul\r\n" +
            $"del \"{newExePath}\" >nul 2>&1\r\n" +
            $"start \"\" \"{currentExePath}\"\r\n" +
            $"del \"%~f0\"\r\n", new System.Text.UTF8Encoding(false));
        Process.Start(new ProcessStartInfo
        {
            FileName = bat,
            WindowStyle = ProcessWindowStyle.Hidden,
            UseShellExecute = true,
        });
    }
}
