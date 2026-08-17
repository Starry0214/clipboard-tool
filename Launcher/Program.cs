using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

// 剪贴板助手单文件引导器（NativeAOT，WinExe 无控制台窗口）：
// 1. 自更新：检查服务器 version.txt，若比自身新则下载新引导器覆盖自己并重启
// 2. 解压内嵌主程序到 %LocalAppData%\ClipboardToolApp\（内嵌版本比已解压版新才覆盖）
// 3. 记录自身路径到 launcher_path.txt（供主程序更新引导器用）
// 4. 检测 .NET 9 桌面运行时，缺失则直接下载并安装（进度窗口提示"进行.NET环境安装中"）
// 5. 启动主程序

internal static class Program
{
    private const string EmbeddedName = "ClipboardToolApp.exe";
    private const string AppDirName = "ClipboardToolApp";
    private const string MainExeName = "ClipboardTool.exe";
    private const string LauncherPathFile = "launcher_path.txt";
    private const string VersionFile = "version.txt";
    private const string UpdateBaseUrl = "https://code.starry0214.one/updates";
    private const string FallbackBaseUrl = "http://107.175.228.83:8080";
    private const string InstallerUrl = "https://code.starry0214.one/updates/windowsdesktop-runtime-9.0.17-win-x64.exe";
    private const string InstallerName = "windowsdesktop-runtime-9.0.17-win-x64.exe";

    /// <summary>镜像源：域名 HTTPS 优先，IP HTTP 兜底（域名走境外中继，部分政务网连不上）。</summary>
    private static readonly string[] BaseUrls = [UpdateBaseUrl, FallbackBaseUrl];

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);

    private const uint MB_OK = 0x00000000;
    private const uint MB_YESNO = 0x00000004;
    private const uint MB_ICONQUESTION = 0x00000020;
    private const uint MB_ICONWARNING = 0x00000030;
    private const uint MB_ICONERROR = 0x00000010;
    private const uint MB_DEFBUTTON1 = 0x00000100;
    private const uint MB_DEFBUTTON2 = 0x00000200;
    private const int IDYES = 6;

    /// <summary>引导器自身版本（与 csproj &lt;Version&gt; 及内嵌主程序版本同步）。</summary>
    private static readonly Version SelfVersion =
        Assembly.GetExecutingAssembly().GetName().Version ?? new Version(1, 0, 0);

    private static int Main(string[] args)
    {
        // 测试钩子：模拟下载+安装进度，验证进度窗口渲染与关闭（等价于主程序的 --show-overlay）
        if (args.Contains("--test-progress", StringComparer.Ordinal))
        {
            var ok = ProgressWindow.Run("剪贴板助手", "进行.NET环境安装中…", reporter =>
            {
                for (var i = 0; i <= 100; i += 2)
                {
                    reporter.Report(i);
                    Thread.Sleep(50);
                }
                reporter.Stage("正在安装运行时（如弹出 UAC 请允许）…");
                for (var i = 90; i < 100; i++)
                {
                    reporter.Report(i);
                    Thread.Sleep(100);
                }
                reporter.Report(100);
                return true;
            });
            return ok ? 0 : 1;
        }

        // 测试钩子：首个镜像设为不可达地址，验证自动回退到 IP 直连镜像
        if (args.Contains("--test-fallback", StringComparer.Ordinal))
        {
            try
            {
                var text = GetText(VersionFile, ["http://127.0.0.1:1", FallbackBaseUrl]);
                return text.Contains("1.3.6", StringComparison.Ordinal) ? 0 : 2;
            }
            catch (Exception)
            {
                return 3;
            }
        }

        var appDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppDirName);
        try
        {
            Directory.CreateDirectory(appDir);
            var mainExe = Path.Combine(appDir, MainExeName);

            // ① 自更新：服务器版本比自身新 → 下载新引导器覆盖自己并重启
            if (TrySelfUpdate(appDir))
                return 0; // 自更新已触发重启，本进程退出

            // ② 解压内嵌主程序：以实际 exe 文件版本为准（曾因内嵌版本错配导致 version.txt=1.3.5 而 exe 是 1.3.4，永不重解压）
            if (GetExeVersion(mainExe) is not { } exeVer || exeVer < SelfVersion)
            {
                ExtractEmbedded(mainExe);
                WriteVersionFile(Path.Combine(appDir, VersionFile), SelfVersion.ToString(3));
            }

            // ③ 记录引导器自身路径，供主程序"检查更新"覆盖引导器用
            try
            {
                File.WriteAllText(Path.Combine(appDir, LauncherPathFile), Environment.ProcessPath ?? "");
            }
            catch (Exception)
            {
            }

            // ④ 运行时检测：缺失则直接自动安装（不再询问，进度窗口提示）
            if (!RuntimeInstalled() && !InstallRuntime(appDir))
                return 1;

            // ⑤ 启动主程序
            Process.Start(new ProcessStartInfo
            {
                FileName = mainExe,
                UseShellExecute = true,
                WorkingDirectory = appDir,
            });
            return 0;
        }
        catch (Exception ex)
        {
            MessageBoxW(IntPtr.Zero, $"启动失败：{ex.Message}", "剪贴板助手", MB_OK | MB_ICONERROR);
            return 1;
        }
    }

    /// <summary>检查服务器是否有更新的引导器；先弹窗询问用户，确认后才下载并覆盖自己（bat 延迟替换），返回 true。</summary>
    private static bool TrySelfUpdate(string appDir)
    {
        try
        {
            var text = GetText(VersionFile);
            if (!Version.TryParse(text.Trim().TrimStart('v'), out var latest) || latest <= SelfVersion)
                return false;

            // 更新前必须提示用户确认，不做静默更新（用户拒绝则本次不更新，正常运行旧版）。
            // 默认按钮用"否"（暂不更新），回车/快速点击不会误触更新。
            var ask = MessageBoxW(IntPtr.Zero,
                $"发现新版本 v{latest}，是否立即更新？\n更新过程约需几秒，完成后自动重启。",
                "剪贴板助手", MB_YESNO | MB_ICONQUESTION | MB_DEFBUTTON2);
            if (ask != IDYES)
                return false;

            // 下载新版引导器（带进度窗口：约 8MB 需下载几秒，无反馈会被误以为卡死/应用消失）
            var newLauncher = Path.Combine(appDir, "launcher.new.exe");
            var dlOk = ProgressWindow.Run("剪贴板助手", "正在下载更新…", reporter =>
            {
                DownloadFile("ClipboardTool.exe", newLauncher, (read, total) =>
                {
                    if (total > 0)
                        reporter.Report((int)Math.Min(100, read * 100L / total));
                });
                reporter.Stage("正在准备重启…");
                return true;
            });
            if (!dlOk || !File.Exists(newLauncher))
                return false; // 下载失败/进度窗口被取消：不更新，正常启动旧版

            // bat：等本进程退出 → 覆盖自身 → 重启（中文路径用 chcp 65001 + UTF-8）
            var self = Environment.ProcessPath ?? throw new InvalidOperationException("无法确定自身路径");
            var bat = Path.Combine(Path.GetTempPath(), $"clipboard_launcher_updater_{Guid.NewGuid():N}.bat");
            File.WriteAllText(bat,
                $"@echo off\r\n" +
                $"chcp 65001 >nul\r\n" +
                $"timeout /t 3 /nobreak >nul\r\n" +
                $"copy /y \"{newLauncher}\" \"{self}\" >nul\r\n" +
                $"del \"{newLauncher}\" >nul 2>&1\r\n" +
                $"start \"\" \"{self}\"\r\n" +
                $"del \"%~f0\"\r\n", new System.Text.UTF8Encoding(false));
            Process.Start(new ProcessStartInfo
            {
                FileName = bat,
                WindowStyle = ProcessWindowStyle.Hidden,
                UseShellExecute = true,
            });
            return true;
        }
        catch (Exception)
        {
            return false; // 网络/解析失败静默，不影响正常启动
        }
    }

    /// <summary>带 10s 连接超时的 HttpClient：域名不通时快速失败，好切换到下一镜像。</summary>
    private static HttpClient CreateClient(TimeSpan timeout) =>
        new(new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(10) }) { Timeout = timeout };

    /// <summary>按镜像顺序取文本，全部失败抛出最后一次异常。</summary>
    private static string GetText(string path, string[]? mirrors = null)
    {
        // 慢网络 TLS 握手可能超 10s，放宽到 25s（连接阶段由 CreateClient 的 10s ConnectTimeout 快速失败）
        Exception? last = null;
        foreach (var baseUrl in mirrors ?? BaseUrls)
        {
            try
            {
                using var http = CreateClient(TimeSpan.FromSeconds(25));
                return http.GetStringAsync($"{baseUrl}/{path}").GetAwaiter().GetResult();
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                last = ex;
            }
        }
        throw last ?? new InvalidOperationException("网络请求失败");
    }

    /// <summary>按镜像顺序下载到 dest；progress 报告 (已读字节, 总字节)。</summary>
    private static void DownloadFile(string path, string dest, Action<long, long>? progress)
    {
        Exception? last = null;
        foreach (var baseUrl in BaseUrls)
        {
            try
            {
                using var http = CreateClient(TimeSpan.FromMinutes(10));
                using var resp = http.GetAsync($"{baseUrl}/{path}", HttpCompletionOption.ResponseHeadersRead)
                    .GetAwaiter().GetResult();
                resp.EnsureSuccessStatusCode();
                var total = resp.Content.Headers.ContentLength ?? 0;
                using var fs = new FileStream(dest, FileMode.Create, FileAccess.Write);
                using var stream = resp.Content.ReadAsStreamAsync().GetAwaiter().GetResult();
                var buffer = new byte[81920];
                long read = 0;
                while (true)
                {
                    var n = stream.Read(buffer, 0, buffer.Length);
                    if (n <= 0)
                        break;
                    fs.Write(buffer, 0, n);
                    read += n;
                    if (total > 0)
                        progress?.Invoke(read, total);
                }
                return;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                last = ex;
            }
        }
        throw last ?? new InvalidOperationException("下载失败");
    }

    /// <summary>解压内嵌主程序到指定路径。</summary>
    private static void ExtractEmbedded(string target)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(EmbeddedName)
            ?? throw new InvalidOperationException("内嵌主程序缺失");
        using (var fs = new FileStream(target, FileMode.Create, FileAccess.Write))
            stream.CopyTo(fs);
    }

    private static Version? GetExeVersion(string path)
    {
        try
        {
            if (!File.Exists(path))
                return null;
            return Version.TryParse(FileVersionInfo.GetVersionInfo(path).FileVersion, out var v) ? v : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static void WriteVersionFile(string path, string version)
    {
        try
        {
            File.WriteAllText(path, version);
        }
        catch (Exception)
        {
        }
    }

    private static bool RuntimeInstalled()
    {
        try
        {
            // 机器级 + 用户级两处都查：per-user 安装（%LocalAppData%\Microsoft\dotnet）不写 PATH，
            // 只查 Program Files 会检测不到，导致每次启动都重新触发安装（UAC 反复弹）
            var roots = new[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "dotnet"),
            };
            foreach (var root in roots)
            {
                var sharedDir = Path.Combine(root, "shared", "Microsoft.WindowsDesktop.App");
                if (Directory.Exists(sharedDir) &&
                    Directory.GetDirectories(sharedDir).Any(d => Path.GetFileName(d).StartsWith("9.", StringComparison.Ordinal)))
                    return true;
            }

            // 兜底：dotnet CLI（自定义安装位置，如开发机 PATH）
            var dotnet = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet", "dotnet.exe");
            if (!File.Exists(dotnet))
                dotnet = "dotnet"; // 自定义安装位置（如开发机）回退 PATH
            var psi = new ProcessStartInfo(dotnet, "--list-runtimes")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi);
            if (p is null)
                return false;
            var out_ = p.StandardOutput.ReadToEnd();
            p.WaitForExit(10000);
            return out_.Contains("WindowsDesktop.App 9.", StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>下载并静默安装 .NET 9 桌面运行时，全程用进度窗口提示（下载显示真实百分比，安装阶段缓动）。</summary>
    private static bool InstallRuntime(string appDir)
    {
        var installer = Path.Combine(appDir, InstallerName);
        try
        {
            var failReason = "";
            var ok = ProgressWindow.Run("剪贴板助手", "进行.NET环境安装中…", reporter =>
            {
                // 下载安装包（缓存存在则跳过）
                if (!File.Exists(installer))
                {
                    reporter.Stage("正在下载 .NET 9 运行时（约 60MB）…");
                    DownloadFile(InstallerName, installer,
                        (read, total) => reporter.Report((int)(read * 100 / total)));
                }

                // 静默安装（可能弹 UAC）：Process.Start 在 UAC 授权期间会阻塞调用线程，
                // 放到独立线程执行，本线程持续动画并提示用户注意 UAC 弹窗
                reporter.Stage("正在等待管理员授权（如弹出 UAC 请允许）…");
                var logPath = Path.Combine(appDir, "dotnet-install.log");
                var installDone = new ManualResetEventSlim();
                var started = false;
                int? exitCode = null;
                Exception? startError = null;
                var installThread = new Thread(() =>
                {
                    try
                    {
                        using var p = Process.Start(new ProcessStartInfo(
                            installer, $"/install /quiet /norestart /log \"{logPath}\"")
                        {
                            UseShellExecute = true,
                        });
                        started = true; // Process.Start 返回 = 已通过 UAC，真正安装开始
                        p?.WaitForExit();
                        if (p is not null)
                            exitCode = p.ExitCode;
                    }
                    catch (Exception ex)
                    {
                        startError = ex;
                    }
                    finally
                    {
                        installDone.Set();
                    }
                });
                installThread.Start();

                var sw = Stopwatch.StartNew();
                while (!installDone.Wait(500) && sw.Elapsed < TimeSpan.FromMinutes(6))
                {
                    reporter.Stage(started ? "正在安装运行时…" : "正在等待管理员授权（如弹出 UAC 请允许）…");
                    reporter.Report(Math.Min(99, 90 + (int)(sw.Elapsed.TotalSeconds / 2)));
                }
                if (startError is Win32Exception wex && wex.NativeErrorCode is 1223 or 5)
                {
                    failReason = "安装 .NET 运行时需要管理员权限。\n请在 UAC 弹窗中选择“是”后重试，或手动下载安装：\n" + InstallerUrl + $"\n或 {FallbackBaseUrl}/{InstallerName}";
                    return false;
                }
                if (startError is not null)
                    throw startError;

                // 提权后实际安装进程可能与句柄不一致，轮询等待安装生效（最多 2 分钟）
                while (sw.Elapsed < TimeSpan.FromMinutes(2) && !RuntimeInstalled())
                {
                    Thread.Sleep(500);
                    reporter.Report(Math.Min(99, 90 + (int)(sw.Elapsed.TotalSeconds / 2)));
                }
                if (!RuntimeInstalled())
                {
                    var codeNote = exitCode is { } c && c != 0 ? $"（退出码 {c}）" : "";
                    failReason = $"运行时安装未成功{codeNote}。\n\n请手动下载安装：\n{InstallerUrl}\n或 {FallbackBaseUrl}/{InstallerName}\n\n安装日志：{logPath}";
                    return false;
                }
                reporter.Report(100);
                return true;
            });

            if (!ok)
                MessageBoxW(IntPtr.Zero, failReason, "剪贴板助手", MB_OK | MB_ICONERROR);
            return ok;
        }
        catch (Exception ex)
        {
            MessageBoxW(IntPtr.Zero,
                $"下载/安装失败：{ex.Message}\n\n请手动下载安装：\n{InstallerUrl}",
                "剪贴板助手", MB_OK | MB_ICONERROR);
            return false;
        }
    }
}
