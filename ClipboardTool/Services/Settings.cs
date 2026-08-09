using System.Text.Json;
using Microsoft.Win32;

namespace ClipboardTool;

/// <summary>用户可配置项，存于 data/settings.json。</summary>
public sealed class Settings
{
    public string HotkeyText { get; set; } = "Ctrl+Alt+V";
    public bool UseWinV { get; set; }
    public int MaxEntries { get; set; } = 500;
    public bool AutoStart { get; set; }
    public bool PastePlainText { get; set; }
    public bool StartMenuShortcut { get; set; }

    /// <summary>悬浮列表最大高度（逻辑像素，0=按屏幕工作区高度自动适配 70%）。</summary>
    public int OverlayMaxHeight { get; set; }

    /// <summary>实验性功能：多端同步（默认关闭，设置页末尾开关启用）。</summary>
    public bool SyncEnabled { get; set; }

    /// <summary>同步账号信息（登录成功后持久化，退出登录时清空 token）。</summary>
    public string SyncUsername { get; set; } = "";
    public string SyncToken { get; set; } = "";
    public long SyncDeviceId { get; set; }
    public string SyncDeviceName { get; set; } = "";

    /// <summary>同步服务器地址覆盖（空=内置双镜像；联调时可填 http://127.0.0.1:8082，UI 不暴露）。</summary>
    public string SyncServerOverride { get; set; } = "";

    /// <summary>已处理的同步消息最大 seq（持久化去重：重启后回放跳过已处理消息）。</summary>
    public long SyncLastSeq { get; set; }

    private string _path = "";

    public static Settings Load(string dataDir)
    {
        var path = Path.Combine(dataDir, "settings.json");
        var s = new Settings { _path = path };
        try
        {
            if (File.Exists(path))
            {
                s = JsonSerializer.Deserialize<Settings>(File.ReadAllText(path)) ?? s;
                // 兼容旧配置：热键已是 Win+V 时视为启用 Win+V 覆盖
                if (!s.UseWinV && string.Equals(s.HotkeyText, "Win+V", StringComparison.OrdinalIgnoreCase))
                    s.UseWinV = true;
            }
        }
        catch (JsonException)
        {
            // 配置损坏时回退默认值
        }
        s._path = path;
        return s;
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (IOException)
        {
        }
    }

    /// <summary>解析 "Ctrl+Alt+V" 形式的组合键为 Win32 修饰键与虚拟键码。</summary>
    public static (uint mods, uint vk) ParseHotkey(string text)
    {
        uint mods = 0, vk = 0;
        foreach (var raw in text.Split('+'))
        {
            var part = raw.Trim().ToUpperInvariant();
            switch (part)
            {
                case "CTRL" or "CONTROL": mods |= NativeMethods.MOD_CONTROL; break;
                case "ALT": mods |= NativeMethods.MOD_ALT; break;
                case "SHIFT": mods |= NativeMethods.MOD_SHIFT; break;
                case "WIN" or "WINDOWS": mods |= NativeMethods.MOD_WIN; break;
                default:
                    if (part.Length == 1 && char.IsLetterOrDigit(part[0]))
                        vk = part[0];
                    else if (part.Length > 1 && part[0] == 'F' && int.TryParse(part[1..], out var f) && f is >= 1 and <= 24)
                        vk = (uint)(0x70 + f - 1);
                    else
                        throw new FormatException($"无法解析热键: {text}");
                    break;
            }
        }
        if (vk == 0)
            throw new FormatException($"热键缺少主键: {text}");
        return (mods | NativeMethods.MOD_NOREPEAT, vk);
    }

    public void ApplyAutoStart()
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
            if (AutoStart)
                key.SetValue("ClipboardTool", $"\"{Environment.ProcessPath}\"");
            else
                key.DeleteValue("ClipboardTool", false);
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>创建/删除开始菜单快捷方式（%AppData%\...\Start Menu\Programs\剪贴板助手.lnk）。</summary>
    public void ApplyStartMenuShortcut()
    {
        try
        {
            var startMenuDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), "Programs");
            var lnk = Path.Combine(startMenuDir, "剪贴板助手.lnk");
            if (!StartMenuShortcut)
            {
                if (File.Exists(lnk))
                    File.Delete(lnk);
                return;
            }
            Directory.CreateDirectory(startMenuDir);
            var exe = Environment.ProcessPath ?? "";
            dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!)!;
            dynamic sc = shell.CreateShortcut(lnk);
            sc.TargetPath = exe;
            sc.WorkingDirectory = Path.GetDirectoryName(exe) ?? "";
            sc.IconLocation = $"{exe},0";
            sc.Save();
        }
        catch (Exception)
        {
        }
    }
}
