using System.Windows;

namespace ClipboardTool;

public partial class HelpWindow : Window
{
    public HelpWindow()
    {
        InitializeComponent();
        Closing += (_, e) => { e.Cancel = true; Hide(); };
    }

    public void Open()
    {
        Show();
        Activate();
    }
}
