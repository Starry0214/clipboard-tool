using System.Windows;
using System.Windows.Input;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace ClipboardTool;

public partial class SettingsWindow : Window
{
    private readonly Settings _settings;
    public bool Applied { get; private set; }

    public SettingsWindow(Settings settings)
    {
        InitializeComponent();
        _settings = settings;
        HotkeyBox.Text = settings.HotkeyText;
        MaxBox.Text = settings.MaxEntries.ToString();
        AutoStartCheck.IsChecked = settings.AutoStart;
        PlainCheck.IsChecked = settings.PastePlainText;

        HotkeyBox.PreviewKeyDown += OnHotkeyKeyDown;
        HotkeyBox.GotKeyboardFocus += (_, _) => HotkeyBox.SelectAll();
    }

    private void OnHotkeyKeyDown(object sender, KeyEventArgs e)
    {
        e.Handled = true;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift)
            return;

        var parts = new List<string>();
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
            parts.Add("Ctrl");
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Alt))
            parts.Add("Alt");
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
            parts.Add("Shift");

        var main = key switch
        {
            Key.D0 => "0",
            Key.D1 => "1",
            Key.D2 => "2",
            Key.D3 => "3",
            Key.D4 => "4",
            Key.D5 => "5",
            Key.D6 => "6",
            Key.D7 => "7",
            Key.D8 => "8",
            Key.D9 => "9",
            >= Key.A and <= Key.Z => key.ToString(),
            >= Key.F1 and <= Key.F24 => $"F{key - Key.F1 + 1}",
            _ => null,
        };
        if (main is null || parts.Count == 0)
            return;
        HotkeyBox.Text = string.Join("+", parts) + "+" + main;
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        // 校验热键可解析
        try
        {
            Settings.ParseHotkey(HotkeyBox.Text);
        }
        catch (FormatException)
        {
            MessageBox.Show(this, "热键格式无效，请点击输入框后按下新的组合键。", "设置",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (!int.TryParse(MaxBox.Text, out var max) || max < 10 || max > 10000)
        {
            MessageBox.Show(this, "条数上限须为 10 ~ 10000 的整数。", "设置",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _settings.HotkeyText = HotkeyBox.Text;
        _settings.MaxEntries = max;
        _settings.AutoStart = AutoStartCheck.IsChecked == true;
        _settings.PastePlainText = PlainCheck.IsChecked == true;
        _settings.Save();
        Applied = true;
        Close();
    }
}
