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
}
