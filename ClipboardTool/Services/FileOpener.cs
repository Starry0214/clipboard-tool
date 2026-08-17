using System.Diagnostics;

namespace ClipboardTool;

/// <summary>文件打开辅助：数据目录内副本先复制到临时目录再交给默认程序，防止默认程序重存改写原文件。</summary>
public static class FileOpener
{
    /// <summary>用系统默认程序打开文件。若文件位于应用数据目录（同步副本/图片文件），先复制到临时目录再打开，
    /// 避免默认程序（如 WPS 打开 PDF 时重存）改写原文件导致内容 hash 变化、跨端删除/置顶失配。</summary>
    public static void Open(string path)
    {
        try
        {
            var openPath = path;
            var dataDir = (System.Windows.Application.Current as App)?.DataDir;
            if (!string.IsNullOrEmpty(path) && !string.IsNullOrEmpty(dataDir))
            {
                try
                {
                    var full = Path.GetFullPath(path);
                    if (full.StartsWith(dataDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    {
                        // 临时副本放数据目录内（用户要求，与数据同目录便于管理；磁盘清理不会动它）
                        var tmpDir = Path.Combine(dataDir, "preview_tmp");
                        Directory.CreateDirectory(tmpDir);
                        // 顺手清理 7 天前的旧临时副本，避免堆积
                        try
                        {
                            foreach (var f in Directory.EnumerateFiles(tmpDir))
                            {
                                var fi = new FileInfo(f);
                                if (DateTime.UtcNow - fi.LastWriteTimeUtc > TimeSpan.FromDays(7))
                                    fi.Delete();
                            }
                        }
                        catch (Exception) { }
                        var tmp = Path.Combine(tmpDir, $"{DateTime.Now:yyyyMMdd_HHmmss_fff}_{Path.GetFileName(full)}");
                        File.Copy(full, tmp, overwrite: true);
                        openPath = tmp;
                    }
                }
                catch (Exception)
                {
                    // 复制失败（源缺失/被占用）时仍尝试直接打开原路径
                }
            }
            Process.Start(new ProcessStartInfo { FileName = openPath, UseShellExecute = true });
        }
        catch (Exception)
        {
            // 文件不存在或无法打开时静默忽略
        }
    }
}
