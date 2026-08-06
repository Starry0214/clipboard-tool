using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;

// 剪贴板助手单文件引导器（NativeAOT，WinExe 无控制台窗口）：
// 1. 自更新：检查服务器 version.txt，若比自身新则下载新引导器覆盖自己并重启
// 2. 解压内嵌主程序到 %LocalAppData%\ClipboardToolApp\（内嵌版本比已解压版新才覆盖）
// 3. 记录自身路径到 launcher_path.txt（供主程序更新引导器用）
// 4. 检测 .NET 9 桌面运行时，缺失则从更新服务器下载并静默安装
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
    private const uint MB_YESNO = 0x00000004;
    private const uint MB_ICONQUESTION = 0x00000020;
    private const uint IDYES = 6;

    /// <summary>引导器自身版本（与 csproj &lt;Version&gt; 及内嵌主程序版本同步）。</summary>
    private static readonly Version SelfVersion =
        Assembly.GetExecutingAssembly().GetName().Version ?? new Version(1, 0, 0);

    private static int Main()
    {
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

            // ④ 运行时检测
            if (!RuntimeInstalled())
            {
                var answer = MessageBoxW(IntPtr.Zero,
                    "剪贴板助手需要 .NET 9 桌面运行时才能运行（约 60MB）。\n\n是否现在从更新服务器下载并安装？",
                    "剪贴板助手", MB_YESNO | MB_ICONQUESTION);
                if (answer != IDYES)
                    return 1;
                if (!InstallRuntime(appDir))
                    return 1;
            }

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
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            var text = http.GetStringAsync($"{UpdateBaseUrl}/{VersionFile}").GetAwaiter().GetResult();
            if (!Version.TryParse(text.Trim().TrimStart('v'), out var latest) || latest <= SelfVersion)
                return false;

            // 下载新版引导器
            var newLauncher = Path.Combine(appDir, "launcher.new.exe");
            using (var resp = http.GetAsync($"{UpdateBaseUrl}/ClipboardTool.exe").GetAwaiter().GetResult())
            {
                resp.EnsureSuccessStatusCode();
                using var fs = new FileStream(newLauncher, FileMode.Create, FileAccess.Write);
                resp.Content.CopyToAsync(fs).GetAwaiter().GetResult();
            }

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
            var psi = new ProcessStartInfo("dotnet", "--list-runtimes")
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

    private static bool InstallRuntime(string appDir)
    {
        var installer = Path.Combine(appDir, InstallerName);
        try
        {
            if (!File.Exists(installer))
            {
                MessageBoxW(IntPtr.Zero, "正在从更新服务器下载 .NET 9 运行时安装包（约 60MB）…",
                    "剪贴板助手", MB_OK);
                using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
                using var resp = http.GetAsync(InstallerUrl).GetAwaiter().GetResult();
                resp.EnsureSuccessStatusCode();
                using var fs = new FileStream(installer, FileMode.Create, FileAccess.Write);
                resp.Content.CopyToAsync(fs).GetAwaiter().GetResult();
            }
            MessageBoxW(IntPtr.Zero, "正在安装运行时（如弹出 UAC 请允许）…", "剪贴板助手", MB_OK);
            using (var p = Process.Start(new ProcessStartInfo(installer, "/install /quiet /norestart")
            {
                UseShellExecute = true,
            }))
            {
                p?.WaitForExit();
            }
            return RuntimeInstalled();
        }
        catch (Exception ex)
        {
            MessageBoxW(IntPtr.Zero, $"下载/安装失败：{ex.Message}\n\n请手动下载安装：\n{InstallerUrl}",
                "剪贴板助手", MB_OK | MB_ICONERROR);
            return false;
        }
    }
}
