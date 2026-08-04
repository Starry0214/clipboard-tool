using System.Windows;
using System.Windows.Input;

namespace ClipboardTool;

/// <summary>文本全文预览窗口：双击文本条目时打开，可滚动查看与选中复制。</summary>
public partial class TextPreviewWindow : Window
{
    public TextPreviewWindow()
    {
        InitializeComponent();
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
                Hide();
        };
        Closing += (_, e) => { e.Cancel = true; Hide(); };
    }

    public void ShowText(Entry entry)
    {
        ContentBox.Text = entry.Content;
        TitleText.Text = $"文本预览  ·  {entry.Content.Length} 字符";
        Show();
        Activate();
    }

    private void OnCopyAll(object sender, RoutedEventArgs e)
    {
        System.Windows.Clipboard.SetText(ContentBox.Text, TextDataFormat.UnicodeText);
    }
}
