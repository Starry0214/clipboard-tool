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
    public event Action? ShowHelp;
    public event Action? Exit;

    public TrayIcon()
    {
        var menu = new ContextMenuStrip();
        var miOpen = new ToolStripMenuItem("打开历史记录");
        var miHelp = new ToolStripMenuItem("使用说明");
        var miPause = new ToolStripMenuItem("暂停监听") { CheckOnClick = true };
        var miClear = new ToolStripMenuItem("清空历史");
        var miExit = new ToolStripMenuItem("退出");

        miOpen.Click += (_, _) => OpenMain?.Invoke();
        miHelp.Click += (_, _) => ShowHelp?.Invoke();
        miPause.CheckedChanged += (_, _) => TogglePause?.Invoke();
        miClear.Click += (_, _) => ClearHistory?.Invoke();
        miExit.Click += (_, _) => Exit?.Invoke();

        menu.Items.Add(miOpen);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(miHelp);
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
        if (_icon.ContextMenuStrip?.Items[4] is ToolStripMenuItem miPause)
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
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.Clear(Color.Transparent);

            var accent = Color.FromArgb(0, 120, 215);
            var dark = Color.FromArgb(0, 100, 180);

            // 顶部蓝色夹子（圆角矩形）
            using var clipBrush = new SolidBrush(accent);
            g.FillRounded(clipBrush, new Rectangle(10, 4, 12, 6), 2);
            // 夹子内白色镂空
            using var clipHole = new SolidBrush(Color.White);
            g.FillRounded(clipHole, new Rectangle(13, 5, 6, 4), 1);

            // 白色纸张（带浅灰边框）
            using var paperBrush = new SolidBrush(Color.White);
            using var borderPen = new Pen(Color.FromArgb(208, 213, 221), 0.8f);
            g.FillRounded(paperBrush, new Rectangle(6, 8, 20, 20), 3);
            g.DrawRounded(borderPen, new Rectangle(6, 8, 20, 20), 3);

            // 蓝色内容行（三条横线，逐渐变短）
            using var lineBrush = new SolidBrush(Color.FromArgb(0, 120, 215, 180));
            using var lineBrush2 = new SolidBrush(Color.FromArgb(0, 120, 215, 120));
            using var lineBrush3 = new SolidBrush(Color.FromArgb(0, 120, 215, 80));
            g.FillRounded(lineBrush, new Rectangle(10, 14, 14, 2), 1);
            g.FillRounded(lineBrush2, new Rectangle(10, 18, 10, 2), 1);
            g.FillRounded(lineBrush3, new Rectangle(10, 22, 12, 2), 1);
        }
        var hIcon = bmp.GetHicon();
        return Icon.FromHandle(hIcon);
    }
}

internal static class GraphicsExtensions
{
    internal static void FillRounded(this Graphics g, Brush brush, Rectangle rect, int radius)
    {
        using var path = CreateRoundedRectPath(rect, radius);
        g.FillPath(brush, path);
    }

    internal static void DrawRounded(this Graphics g, Pen pen, Rectangle rect, int radius)
    {
        using var path = CreateRoundedRectPath(rect, radius);
        g.DrawPath(pen, path);
    }

    private static GraphicsPath CreateRoundedRectPath(Rectangle rect, int radius)
    {
        var path = new GraphicsPath();
        int d = radius * 2;
        path.AddArc(rect.X, rect.Y, d, d, 180, 90);
        path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
        path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
        path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}
