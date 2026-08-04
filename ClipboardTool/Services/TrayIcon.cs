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
    public event Action? CheckUpdate;
    public event Action? Exit;

    public TrayIcon()
    {
        var menu = new ContextMenuStrip();
        var miOpen = new ToolStripMenuItem("打开历史记录");
        var miHelp = new ToolStripMenuItem("使用说明");
        var miUpdate = new ToolStripMenuItem("检查更新");
        var miPause = new ToolStripMenuItem("暂停监听") { CheckOnClick = true };
        var miClear = new ToolStripMenuItem("清空历史");
        var miExit = new ToolStripMenuItem("退出");

        miOpen.Click += (_, _) => OpenMain?.Invoke();
        miHelp.Click += (_, _) => ShowHelp?.Invoke();
        miUpdate.Click += (_, _) => CheckUpdate?.Invoke();
        miPause.CheckedChanged += (_, _) => TogglePause?.Invoke();
        miClear.Click += (_, _) => ClearHistory?.Invoke();
        miExit.Click += (_, _) => Exit?.Invoke();

        menu.Items.Add(miOpen);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(miHelp);
        menu.Items.Add(miUpdate);
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
        if (_icon.ContextMenuStrip?.Items[5] is ToolStripMenuItem miPause)
            miPause.Checked = paused;
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }

    private static Icon CreateIcon()
    {
        using var bmp = CreateIconBitmap(32);
        var hIcon = bmp.GetHicon();
        return Icon.FromHandle(hIcon);
    }

    /// <summary>生成指定尺寸的应用图标 Bitmap（用于托盘和窗口图标）。</summary>
    internal static Bitmap CreateIconBitmap(int size)
    {
        var bmp = new Bitmap(size, size);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.Clear(Color.Transparent);

        var s = size / 32f;
        var accent = Color.FromArgb(0, 120, 215);

        // 顶部蓝色夹子
        using var clipBrush = new SolidBrush(accent);
        g.FillRounded(clipBrush, new Rectangle((int)(10*s), (int)(4*s), (int)(12*s), (int)(6*s)), Math.Max(1, (int)(2*s)));
        using var clipHole = new SolidBrush(Color.White);
        g.FillRounded(clipHole, new Rectangle((int)(13*s), (int)(5*s), (int)(6*s), (int)(4*s)), Math.Max(1, (int)(1*s)));

        // 白色纸张
        using var paperBrush = new SolidBrush(Color.White);
        using var borderPen = new Pen(Color.FromArgb(208, 213, 221), Math.Max(0.5f, 0.8f * s));
        g.FillRounded(paperBrush, new Rectangle((int)(6*s), (int)(8*s), (int)(20*s), (int)(20*s)), Math.Max(1, (int)(3*s)));
        g.DrawRounded(borderPen, new Rectangle((int)(6*s), (int)(8*s), (int)(20*s), (int)(20*s)), Math.Max(1, (int)(3*s)));

        // CV 字母组合（重叠，纸张中央）
        using var cFont = new Font("Segoe UI", Math.Max(8, size * 0.38f), FontStyle.Bold, GraphicsUnit.Pixel);
        using var vFont = new Font("Segoe UI", Math.Max(10, size * 0.44f), FontStyle.Bold, GraphicsUnit.Pixel);
        using var cBrush = new SolidBrush(Color.FromArgb(140, 180, 210)); // 浅灰蓝
        using var vBrush = new SolidBrush(accent); // 标准蓝

        var cFmt = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        var vFmt = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };

        // C 在左（浅蓝灰），V 在右（标准蓝，重叠）
        g.DrawString("C", cFont, cBrush,
            new RectangleF((int)(9*s), (int)(13*s), (int)(11*s), (int)(12*s)), cFmt);
        g.DrawString("V", vFont, vBrush,
            new RectangleF((int)(13*s), (int)(14*s), (int)(11*s), (int)(12*s)), vFmt);

        // 蓝色内容行（装饰线）
        using var lineBrush = new SolidBrush(Color.FromArgb(0, 120, 215, 50));
        g.FillRounded(lineBrush, new Rectangle((int)(10*s), (int)(11*s), (int)(14*s), Math.Max(1, (int)(1.5*s))), Math.Max(1, (int)(0.75*s)));
        using var lineBrush2 = new SolidBrush(Color.FromArgb(0, 120, 215, 35));
        g.FillRounded(lineBrush2, new Rectangle((int)(10*s), (int)(26*s), (int)(10*s), Math.Max(1, (int)(1.5*s))), Math.Max(1, (int)(0.75*s)));

        return bmp;
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
