using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;

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
    private const string InstallerUrl = "https://code.starry0214.one/updates/windowsdesktop-runtime-9.0.17-win-x64.exe";
    private const string InstallerName = "windowsdesktop-runtime-9.0.17-win-x64.exe";

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);

    private const uint MB_OK = 0x00000000;
    private const uint MB_ICONWARNING = 0x00000030;
    private const uint MB_ICONERROR = 0x00000010;

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

        var appDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppDirName);
        try
        {
            Directory.CreateDirectory(appDir);
            var mainExe = Path.Combine(appDir, MainExeName);

            // ① 自更新：服务器版本比自身新 → 下载新引导器覆盖自己并重启
            if (TrySelfUpdate(appDir))
                return 0; // 自更新已触发重启，本进程退出

            // ② 解压内嵌主程序（内嵌版本比已解压版新才覆盖，保留被自动更新替换过的新版）
            var extractedVer = ReadVersionFile(Path.Combine(appDir, VersionFile));
            if (!File.Exists(mainExe) || extractedVer is null || extractedVer < SelfVersion)
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

    /// <summary>检查服务器是否有更新的引导器；有则下载并覆盖自己（bat 延迟替换），返回 true。</summary>
    private static bool TrySelfUpdate(string appDir)
    {
        try
        {
            var text = GetText(VersionFile);
            if (!Version.TryParse(text.Trim().TrimStart('v'), out var latest) || latest <= SelfVersion)
                return false;

            // 下载新版引导器
            var newLauncher = Path.Combine(appDir, "launcher.new.exe");
            DownloadFile("ClipboardTool.exe", newLauncher, null);

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

    /// <summary>从更新服务器取文本；失败抛异常。</summary>
    private static string GetText(string path)
    {
        using var http = CreateClient(TimeSpan.FromSeconds(10));
        return http.GetStringAsync($"{UpdateBaseUrl}/{path}").GetAwaiter().GetResult();
    }

    /// <summary>从更新服务器下载到 dest；progress 报告 (已读字节, 总字节)。</summary>
    private static void DownloadFile(string path, string dest, Action<long, long>? progress)
    {
        using var http = CreateClient(TimeSpan.FromMinutes(10));
        using var resp = http.GetAsync($"{UpdateBaseUrl}/{path}", HttpCompletionOption.ResponseHeadersRead)
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
    }

    /// <summary>解压内嵌主程序到指定路径。</summary>
    private static void ExtractEmbedded(string target)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(EmbeddedName)
            ?? throw new InvalidOperationException("内嵌主程序缺失");
        using (var fs = new FileStream(target, FileMode.Create, FileAccess.Write))
            stream.CopyTo(fs);
    }

    private static Version? ReadVersionFile(string path)
    {
        try
        {
            if (!File.Exists(path))
                return null;
            return Version.TryParse(File.ReadAllText(path).Trim(), out var v) ? v : null;
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
            // 必须用完整路径：无运行时机器上 PATH 是引导器启动时的旧值，装完运行时后仍解析不到 dotnet
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
                    failReason = "安装 .NET 运行时需要管理员权限。\n请在 UAC 弹窗中选择“是”后重试，或手动下载安装：\n" + InstallerUrl;
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
                    failReason = $"运行时安装未成功{codeNote}。\n\n请手动下载安装：\n{InstallerUrl}\n\n安装日志：{logPath}";
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
