using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;

// 剪贴板助手单文件引导器（NativeAOT）：
// 1. 解压内嵌主程序到 %LocalAppData%\ClipboardToolApp\
// 2. 检测 .NET 9 桌面运行时，缺失则从更新服务器下载并静默安装
// 3. 启动主程序（优先启动被自动更新替换过的新版）

internal static class Program
{
    private const string EmbeddedName = "ClipboardToolApp.exe";
    private const string AppDirName = "ClipboardToolApp";
    private const string MainExeName = "ClipboardTool.exe";
    private const string InstallerUrl = "https://code.starry0214.one/updates/windowsdesktop-runtime-9.0.17-win-x64.exe";
    private const string InstallerName = "windowsdesktop-runtime-9.0.17-win-x64.exe";

    private static int Main()
    {
        var appDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppDirName);
        try
        {
            Directory.CreateDirectory(appDir);
            var mainExe = Path.Combine(appDir, MainExeName);

            // 解压目录已有 exe（可能被自动更新替换为新版）→ 直接用，不覆盖；
            // 仅首次或缺失时解压内嵌版
            if (!File.Exists(mainExe))
                ExtractEmbedded(mainExe);

            if (!RuntimeInstalled())
            {
                Console.WriteLine("未检测到 .NET 9 桌面运行时，需要先安装（约 60MB）。");
                Console.WriteLine("将从更新服务器下载并安装，请稍候…");
                if (!InstallRuntime(appDir))
                    return 1;
                Console.WriteLine("运行时安装完成。");
            }

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
            Console.WriteLine($"启动失败：{ex.Message}");
            Console.WriteLine("按任意键退出…");
            Console.ReadKey();
            return 1;
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
                Console.WriteLine("正在下载运行时安装包…");
                using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
                using var resp = http.GetAsync(InstallerUrl).GetAwaiter().GetResult();
                resp.EnsureSuccessStatusCode();
                using var fs = new FileStream(installer, FileMode.Create, FileAccess.Write);
                resp.Content.CopyToAsync(fs).GetAwaiter().GetResult();
            }
            Console.WriteLine("正在安装运行时（如弹出 UAC 请允许）…");
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
            Console.WriteLine($"下载/安装失败：{ex.Message}");
            Console.WriteLine($"请手动下载安装：{InstallerUrl}");
            return false;
        }
    }
}
