using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;

namespace ClipboardTool;

/// <summary>图片原图预览窗口：双击图片条目时打开，支持适应窗口/实际大小。</summary>
public partial class ImagePreviewWindow : Window
{
    public ImagePreviewWindow()
    {
        InitializeComponent();
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
                Hide();
        };
        PreviewImage.MouseWheel += OnMouseWheel;
        Closing += (_, e) => { e.Cancel = true; Hide(); };
    }

    /// <summary>显示指定图片条目的原图。</summary>
    public void ShowImage(ClipboardStore store, Entry entry)
    {
        var full = store.GetById(entry.Id);
        var png = full?.Image;
        if (png is null || png.Length == 0)
        {
            PreviewImage.Source = null;
            EmptyHint.Visibility = Visibility.Visible;
            TitleText.Text = "图片预览（无原图）";
        }
        else
        {
            var bmp = ClipboardMonitor.DecodePng(png);
            PreviewImage.Source = bmp;
            EmptyHint.Visibility = Visibility.Collapsed;
            TitleText.Text = $"图片预览  {bmp.PixelWidth} × {bmp.PixelHeight}";
            FitToWindow();
        }
        Show();
        Activate();
    }

    private void FitToWindow()
    {
        PreviewImage.Stretch = System.Windows.Media.Stretch.Uniform;
        Scroller.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        Scroller.VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
    }

    private void OnFit(object sender, RoutedEventArgs e) => FitToWindow();

    private void OnActual(object sender, RoutedEventArgs e)
    {
        PreviewImage.Stretch = System.Windows.Media.Stretch.None;
        Scroller.HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;
        Scroller.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
    }

    private void OnMouseWheel(object sender, MouseWheelEventArgs e)
    {
        // 按住 Ctrl 滚轮缩放
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            e.Handled = true;
            PreviewImage.Stretch = System.Windows.Media.Stretch.None;
            Scroller.HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;
            Scroller.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            var scale = e.Delta > 0 ? 1.15 : 1 / 1.15;
            var tf = PreviewImage.RenderTransform as System.Windows.Media.ScaleTransform ?? new System.Windows.Media.ScaleTransform(1, 1);
            tf.ScaleX = System.Math.Clamp(tf.ScaleX * scale, 0.1, 8);
            tf.ScaleY = System.Math.Clamp(tf.ScaleY * scale, 0.1, 8);
            PreviewImage.RenderTransformOrigin = new Point(0.5, 0.5);
            PreviewImage.RenderTransform = tf;
        }
    }
}
