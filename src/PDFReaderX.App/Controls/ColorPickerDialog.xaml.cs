using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PDFReaderX.App.Controls;

/// <summary>HSV 颜色选择对话框：色相条 + 饱和度/明度区域 + RGB/HEX 精确输入（仿 Windows 画图）。</summary>
public partial class ColorPickerDialog : Window
{
    private const int SvWidth = 250;
    private const int SvHeight = 158;

    private readonly WriteableBitmap _svBitmap;
    private bool _syncing;

    private double _hue;
    private double _saturation;
    private double _value;

    public ColorPickerDialog(Color initialColor)
    {
        InitializeComponent();
        _svBitmap = new WriteableBitmap(SvWidth, SvHeight, 96, 96, PixelFormats.Bgr32, null);
        SvImage.Source = _svBitmap;
        OriginalColor = initialColor;
        SelectedColor = initialColor;
        var (h, s, v) = RgbToHsv(initialColor.R, initialColor.G, initialColor.B);
        _hue = h;
        _saturation = s;
        _value = v;
        PaintSvBitmap();
        UpdateUi();
        // 窗口布局完成后刷新滑块/光标位置（构造时 ActualWidth 还是 0）
        Loaded += (_, _) => UpdateUi();
    }

    /// <summary>打开时的初始颜色。</summary>
    public Color OriginalColor { get; }

    /// <summary>确定后选中的颜色。</summary>
    public Color SelectedColor { get; private set; }

    private void PaintSvBitmap()
    {
        _svBitmap.Lock();
        var pixels = new byte[SvWidth * SvHeight * 4];
        for (var y = 0; y < SvHeight; y++)
        {
            var value = 1.0 - (double)y / (SvHeight - 1);
            for (var x = 0; x < SvWidth; x++)
            {
                var sat = (double)x / (SvWidth - 1);
                var (r, g, b) = HsvToRgb(_hue, sat, value);
                var i = (y * SvWidth + x) * 4;
                pixels[i] = b;
                pixels[i + 1] = g;
                pixels[i + 2] = r;
                pixels[i + 3] = 255;
            }
        }
        _svBitmap.WritePixels(new Int32Rect(0, 0, SvWidth, SvHeight), pixels, SvWidth * 4, 0);
        _svBitmap.Unlock();
    }

    /// <summary>把当前 HSV 同步到所有 UI（十字光标、色相滑块、RGB/HEX 输入、预览）。</summary>
    private void UpdateUi()
    {
        Canvas.SetLeft(SvCursorOuter, _saturation * (SvWidth - 14));
        Canvas.SetTop(SvCursorOuter, (1 - _value) * (SvHeight - 14));
        Canvas.SetLeft(SvCursorInner, _saturation * (SvWidth - 10) - 2);
        Canvas.SetTop(SvCursorInner, (1 - _value) * (SvHeight - 10) - 2);
        Canvas.SetLeft(HueCursor, _hue / 360.0 * (SvHost.ActualWidth - HueCursor.Width));

        var (r, g, b) = HsvToRgb(_hue, _saturation, _value);
        SelectedColor = Color.FromRgb(r, g, b);
        NewColorRect.Fill = new SolidColorBrush(SelectedColor);
        OldColorRect.Fill = new SolidColorBrush(OriginalColor);

        if (!_syncing)
        {
            _syncing = true;
            RBox.Text = r.ToString();
            GBox.Text = g.ToString();
            BBox.Text = b.ToString();
            HexBox.Text = $"#{r:X2}{g:X2}{b:X2}";
            _syncing = false;
        }
    }

    private void OnSvMouseDown(object sender, MouseButtonEventArgs e)
    {
        SvHost.CaptureMouse();
        SetSvFromMouse(e.GetPosition(SvHost));
    }

    private void OnSvMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
        {
            SetSvFromMouse(e.GetPosition(SvHost));
        }
    }

    private void OnSvMouseUp(object sender, MouseButtonEventArgs e)
    {
        SvHost.ReleaseMouseCapture();
    }

    private void SetSvFromMouse(Point p)
    {
        _saturation = Math.Clamp(p.X / SvWidth, 0, 1);
        _value = Math.Clamp(1 - p.Y / SvHeight, 0, 1);
        UpdateUi();
    }

    private void OnHueMouseDown(object sender, MouseButtonEventArgs e)
    {
        HueHost.CaptureMouse();
        SetHueFromMouse(e.GetPosition(HueHost));
    }

    private void OnHueMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
        {
            SetHueFromMouse(e.GetPosition(HueHost));
        }
    }

    private void OnHueMouseUp(object sender, MouseButtonEventArgs e)
    {
        HueHost.ReleaseMouseCapture();
    }

    private void SetHueFromMouse(Point p)
    {
        _hue = Math.Clamp(p.X / HueHost.ActualWidth, 0, 1) * 360;
        PaintSvBitmap();
        UpdateUi();
        // 窗口布局完成后刷新滑块/光标位置（构造时 ActualWidth 还是 0）
        Loaded += (_, _) => UpdateUi();
    }

    private void OnRgbLostFocus(object sender, RoutedEventArgs e)
    {
        CommitRgb();
    }

    private void OnRgbKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            CommitRgb();
            Keyboard.ClearFocus();
        }
    }

    private void CommitRgb()
    {
        if (_syncing)
        {
            return;
        }
        if (byte.TryParse(RBox.Text, out var r)
            && byte.TryParse(GBox.Text, out var g)
            && byte.TryParse(BBox.Text, out var b))
        {
            var (h, s, v) = RgbToHsv(r, g, b);
            _hue = h;
            _saturation = s;
            _value = v;
            PaintSvBitmap();
            UpdateUi();
        }
    }

    private void OnHexLostFocus(object sender, RoutedEventArgs e)
    {
        CommitHex();
    }

    private void OnHexKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            CommitHex();
            Keyboard.ClearFocus();
        }
    }

    private void CommitHex()
    {
        if (_syncing)
        {
            return;
        }
        var text = HexBox.Text.Trim().TrimStart('#');
        if (text.Length == 6
            && byte.TryParse(text.Substring(0, 2), System.Globalization.NumberStyles.HexNumber, null, out var r)
            && byte.TryParse(text.Substring(2, 2), System.Globalization.NumberStyles.HexNumber, null, out var g)
            && byte.TryParse(text.Substring(4, 2), System.Globalization.NumberStyles.HexNumber, null, out var b))
        {
            var (h, s, v) = RgbToHsv(r, g, b);
            _hue = h;
            _saturation = s;
            _value = v;
            PaintSvBitmap();
            UpdateUi();
        }
    }

    private void OnOkClick(object sender, RoutedEventArgs e)
    {
        CommitRgb();
        CommitHex();
        DialogResult = true;
    }

    private static (byte r, byte g, byte b) HsvToRgb(double h, double s, double v)
    {
        var c = v * s;
        var hp = h / 60.0;
        var x = c * (1 - Math.Abs(hp % 2 - 1));
        (double r, double g, double b) rgb = hp switch
        {
            < 1 => (c, x, 0.0),
            < 2 => (x, c, 0.0),
            < 3 => (0.0, c, x),
            < 4 => (0.0, x, c),
            < 5 => (x, 0.0, c),
            _ => (c, 0.0, x),
        };
        var m = v - c;
        return (
            (byte)Math.Round((rgb.r + m) * 255),
            (byte)Math.Round((rgb.g + m) * 255),
            (byte)Math.Round((rgb.b + m) * 255));
    }

    private static (double h, double s, double v) RgbToHsv(byte r, byte g, byte b)
    {
        var rn = r / 255.0;
        var gn = g / 255.0;
        var bn = b / 255.0;
        var max = Math.Max(rn, Math.Max(gn, bn));
        var min = Math.Min(rn, Math.Min(gn, bn));
        var delta = max - min;
        double hue = 0;
        if (delta > 0)
        {
            if (max == rn)
            {
                hue = 60 * (((gn - bn) / delta) % 6);
            }
            else if (max == gn)
            {
                hue = 60 * ((bn - rn) / delta + 2);
            }
            else
            {
                hue = 60 * ((rn - gn) / delta + 4);
            }
        }
        if (hue < 0)
        {
            hue += 360;
        }
        var sat = max == 0 ? 0 : delta / max;
        return (hue, sat, max);
    }
}