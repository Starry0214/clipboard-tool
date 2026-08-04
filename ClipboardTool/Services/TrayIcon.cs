using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace ClipboardTool;

/// <summary>系统托盘常驻图标与右键菜单。</summary>
public sealed class TrayIcon : IDisposable
{
    private readonly NotifyIcon _icon;

    public event Action? OpenMain;
    public event Action? TogglePause;
    public event Action? ClearHistory;
    public event Action? Exit;

    public TrayIcon()
    {
        var menu = new ContextMenuStrip();
        var miOpen = new ToolStripMenuItem("打开历史记录");
        var miPause = new ToolStripMenuItem("暂停监听") { CheckOnClick = true };
        var miClear = new ToolStripMenuItem("清空历史");
        var miExit = new ToolStripMenuItem("退出");

        miOpen.Click += (_, _) => OpenMain?.Invoke();
        miPause.CheckedChanged += (_, _) => TogglePause?.Invoke();
        miClear.Click += (_, _) => ClearHistory?.Invoke();
        miExit.Click += (_, _) => Exit?.Invoke();

        menu.Items.Add(miOpen);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(miPause);
        menu.Items.Add(miClear);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(miExit);

        _icon = new NotifyIcon
        {
            Icon = CreateIcon(),
            Text = "剪贴板工具",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _icon.DoubleClick += (_, _) => OpenMain?.Invoke();
    }

    public void SetPaused(bool paused)
    {
        if (_icon.ContextMenuStrip?.Items[2] is ToolStripMenuItem miPause)
            miPause.Checked = paused;
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }

    private static Icon CreateIcon()
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);

            var accent = Color.FromArgb(0, 120, 215);
            using var bg = new SolidBrush(accent);
            using var clip = new SolidBrush(Color.White);
            using var pen = new Pen(Color.White, 1.5f);

            // 顶部夹子
            g.FillRectangle(clip, 12, 5, 8, 5);
            // 板身
            g.FillRounded(bg, new Rectangle(7, 8, 18, 20), 3);
            g.DrawRectangle(pen, 11, 13, 10, 11);
        }
        var hIcon = bmp.GetHicon();
        return Icon.FromHandle(hIcon);
    }
}

internal static class GraphicsExtensions
{
    internal static void FillRounded(this Graphics g, Brush brush, Rectangle rect, int radius)
    {
        using var path = new GraphicsPath();
        int d = radius * 2;
        path.AddArc(rect.X, rect.Y, d, d, 180, 90);
        path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
        path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
        path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        g.FillPath(brush, path);
    }
}
