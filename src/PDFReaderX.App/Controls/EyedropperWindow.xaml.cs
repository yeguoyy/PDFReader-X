using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace PDFReaderX.App.Controls;

/// <summary>全屏取色器：鼠标移动实时预览颜色，单击取色，Esc 取消。</summary>
public partial class EyedropperWindow : Window
{
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(30) };

    public EyedropperWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        _timer.Tick += (_, _) => SamplePixel();
    }

    /// <summary>取到的颜色（取消时为 null）。</summary>
    public Color? PickedColor { get; private set; }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Left = SystemParameters.VirtualScreenLeft;
        Top = SystemParameters.VirtualScreenTop;
        Width = SystemParameters.VirtualScreenWidth;
        Height = SystemParameters.VirtualScreenHeight;
        UpdateCursorVisual();
        _timer.Start();
    }

    private void SamplePixel()
    {
        var p = System.Windows.Forms.Cursor.Position;
        using var bitmap = new System.Drawing.Bitmap(1, 1);
        using var graphics = System.Drawing.Graphics.FromImage(bitmap);
        graphics.CopyFromScreen(p.X, p.Y, 0, 0, new System.Drawing.Size(1, 1));
        var pixel = bitmap.GetPixel(0, 0);
        PickedColor = Color.FromRgb(pixel.R, pixel.G, pixel.B);
        Swatch.Fill = new SolidColorBrush(PickedColor.Value);
        HexText.Text = $"#{pixel.R:X2}{pixel.G:X2}{pixel.B:X2}";
        UpdateCursorVisual();
    }

    private void UpdateCursorVisual()
    {
        var p = System.Windows.Forms.Cursor.Position;
        // Cursor.Position 是物理像素，窗口坐标是 DIP，按当前 DPI 换算
        var scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        var x = (p.X - Left * scale) / scale;
        var y = (p.Y - Top * scale) / scale;
        Canvas.SetLeft(CrossV, x);
        Canvas.SetTop(CrossV, y - 9);
        Canvas.SetLeft(CrossH, x - 9);
        Canvas.SetTop(CrossH, y);
        // 预览卡片放在光标右下，靠近屏幕边缘时翻转到左侧
        var cardLeft = x + 18;
        var cardTop = y + 18;
        if (cardLeft + PreviewCard.ActualWidth + 24 > Width)
        {
            cardLeft = x - PreviewCard.ActualWidth - 18;
        }
        if (cardTop + PreviewCard.ActualHeight + 24 > Height)
        {
            cardTop = y - PreviewCard.ActualHeight - 18;
        }
        Canvas.SetLeft(PreviewCard, Math.Max(4, cardLeft));
        Canvas.SetTop(PreviewCard, Math.Max(4, cardTop));
    }

    private void OnPick(object sender, MouseButtonEventArgs e)
    {
        _timer.Stop();
        DialogResult = true;
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            _timer.Stop();
            PickedColor = null;
            DialogResult = false;
        }
    }
}