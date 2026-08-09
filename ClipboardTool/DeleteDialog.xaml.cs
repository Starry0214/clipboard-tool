using System.Windows;

namespace ClipboardTool;

/// <summary>
/// 删除方式选择对话框。
/// 本地删除：仅删本机、服务器保留（其他设备不知情，手动同步可找回）；
/// 彻底删除：本机删 + 服务器删原消息并广播，所有设备任何同步都会删除。
/// </summary>
public partial class DeleteDialog : Window
{
    /// <summary>true=彻底删除，false=本地删除。</summary>
    public bool Fully { get; private set; }

    public DeleteDialog(Entry entry)
    {
        InitializeComponent();
        var preview = entry.Type == "image" ? "[图片]" :
            entry.Type == "file" ? System.IO.Path.GetFileName(entry.Content) :
            (entry.Content.Length > 40 ? entry.Content[..40] + "…" : entry.Content);
        HintText.Text = $"“{preview}”\n\n本地删除：只在本机删除，不影响其他设备（手动同步可找回）\n彻底删除：所有设备同步删除（不可找回）";
    }

    private void OnLocalDelete(object sender, RoutedEventArgs e)
    {
        Fully = false;
        DialogResult = true;
    }

    private void OnFullDelete(object sender, RoutedEventArgs e)
    {
        Fully = true;
        DialogResult = true;
    }
}
