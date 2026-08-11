using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace PDFReaderX.App.Controls;

/// <summary>Color pick event args.</summary>
public sealed class ColorPickedEventArgs : EventArgs
{
    public ColorPickedEventArgs(Color color) => Color = color;

    public Color Color { get; }
}

/// <summary>Paint-style tool option item for width dots.</summary>
public sealed class WidthPresetItem
{
    public double Value { get; init; }

    public double DotSize { get; init; }
}

/// <summary>
/// OneNote/Paint style tool option panel: stroke preview, width presets (+/-),
/// recent colors, color pens grid, "more colors" and eyedropper buttons.
/// </summary>
public partial class InkToolOptionsPanel : UserControl
{
    /// <summary>Raised when a color is picked from recent colors, color grid or eyedropper/more-colors flow.</summary>
    public event EventHandler<ColorPickedEventArgs>? ColorPicked;

    /// <summary>Raised when a width preset is picked (dot click or +/- step).</summary>
    public event EventHandler? WidthPicked;

    /// <summary>Raised when "more colors" button clicked (open custom color dialog).</summary>
    public event EventHandler? MoreColorsClicked;

    /// <summary>Raised when eyedropper button clicked.</summary>
    public event EventHandler? EyedropperClicked;

    public static readonly DependencyProperty CurrentColorProperty = DependencyProperty.Register(
        nameof(CurrentColor), typeof(Color), typeof(InkToolOptionsPanel),
        new PropertyMetadata(Colors.Black, OnAppearanceChanged));

    public static readonly DependencyProperty CurrentWidthProperty = DependencyProperty.Register(
        nameof(CurrentWidth), typeof(double), typeof(InkToolOptionsPanel),
        new PropertyMetadata(3.0, OnAppearanceChanged));

    public static readonly DependencyProperty WidthPresetsProperty = DependencyProperty.Register(
        nameof(WidthPresets), typeof(IEnumerable<double>), typeof(InkToolOptionsPanel),
        new PropertyMetadata(null, OnWidthPresetsChanged));

    public static readonly DependencyProperty ColorOptionsProperty = DependencyProperty.Register(
        nameof(ColorOptions), typeof(IEnumerable<Color>), typeof(InkToolOptionsPanel),
        new PropertyMetadata(null, OnColorOptionsChanged));

    public static readonly DependencyProperty RecentColorsProperty = DependencyProperty.Register(
        nameof(RecentColors), typeof(IEnumerable<Color>), typeof(InkToolOptionsPanel),
        new PropertyMetadata(null, OnRecentColorsChanged));

    /// <summary>pen / highlighter / eraser - affects preview line style and width scaling.</summary>
    public static readonly DependencyProperty PreviewKindProperty = DependencyProperty.Register(
        nameof(PreviewKind), typeof(string), typeof(InkToolOptionsPanel),
        new PropertyMetadata("pen", OnAppearanceChanged));

    public InkToolOptionsPanel()
    {
        InitializeComponent();
        RebuildWidthDots();
        UpdatePreview();
        UpdateColorSections();
    }

    public Color CurrentColor
    {
        get => (Color)GetValue(CurrentColorProperty);
        set => SetValue(CurrentColorProperty, value);
    }

    public double CurrentWidth
    {
        get => (double)GetValue(CurrentWidthProperty);
        set => SetValue(CurrentWidthProperty, value);
    }

    public IEnumerable<double> WidthPresets
    {
        get => (IEnumerable<double>)GetValue(WidthPresetsProperty);
        set => SetValue(WidthPresetsProperty, value);
    }

    public IEnumerable<Color> ColorOptions
    {
        get => (IEnumerable<Color>)GetValue(ColorOptionsProperty);
        set => SetValue(ColorOptionsProperty, value);
    }

    public IEnumerable<Color> RecentColors
    {
        get => (IEnumerable<Color>)GetValue(RecentColorsProperty);
        set => SetValue(RecentColorsProperty, value);
    }

    public string PreviewKind
    {
        get => (string)GetValue(PreviewKindProperty);
        set => SetValue(PreviewKindProperty, value);
    }

    private readonly ObservableCollection<WidthPresetItem> _widthItems = new();
    private readonly ObservableCollection<Color> _recentItems = new();

    private static void OnAppearanceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var panel = (InkToolOptionsPanel)d;
        panel.UpdatePreview();
        panel.UpdateColorSections();
    }

    private static void OnColorOptionsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((InkToolOptionsPanel)d).UpdateColorSections();

    private static void OnWidthPresetsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((InkToolOptionsPanel)d).RebuildWidthDots();

    private static void OnRecentColorsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var panel = (InkToolOptionsPanel)d;
        if (e.OldValue is INotifyCollectionChanged oldNcc)
        {
            oldNcc.CollectionChanged -= panel.OnRecentSourceChanged;
        }
        if (e.NewValue is INotifyCollectionChanged newNcc)
        {
            newNcc.CollectionChanged += panel.OnRecentSourceChanged;
        }
        panel.RebuildRecentItems();
    }

    private void OnRecentSourceChanged(object? sender, NotifyCollectionChangedEventArgs e)
        => RebuildRecentItems();

    private void RebuildWidthDots()
    {
        _widthItems.Clear();
        var presets = WidthPresets?.ToList() ?? new List<double>();
        for (var i = 0; i < presets.Count; i++)
        {
            _widthItems.Add(new WidthPresetItem { Value = presets[i], DotSize = 5.0 + i * 2.4 });
        }
        WidthDots.ItemsSource = _widthItems;
    }

    private void RebuildRecentItems()
    {
        _recentItems.Clear();
        if (RecentColors is not null)
        {
            foreach (var color in RecentColors)
            {
                _recentItems.Add(color);
            }
        }
        RecentList.ItemsSource = _recentItems;
        RecentSection.Visibility = PreviewKind == "eraser" || _recentItems.Count == 0
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    /// <summary>橡皮擦没有颜色概念，隐藏颜色与最近颜色区域。</summary>
    private void UpdateColorSections()
    {
        var isEraser = PreviewKind == "eraser";
        ColorSection.Visibility = isEraser ? Visibility.Collapsed : Visibility.Visible;
        RebuildRecentItems();
    }

    /// <summary>Update the stroke preview bar (color + width scaled per tool kind).</summary>
    private void UpdatePreview()
    {
        var width = CurrentWidth;
        var height = PreviewKind switch
        {
            "highlighter" => Math.Clamp(8 + width * 0.28, 8, 22),
            "eraser" => Math.Clamp(4 + width * 0.3, 6, 22),
            _ => Math.Clamp(2 + width * 1.5, 3, 18),
        };
        PreviewBar.Height = height;
        PreviewBar.Background = new SolidColorBrush(PreviewKind == "eraser" ? Colors.Gray : CurrentColor);
        // 色板图标：钢笔用笔形，荧光笔用笔头形
        ColorGrid.ItemTemplate = PreviewKind == "highlighter"
            ? (DataTemplate)FindResource("MarkerColorTemplate")
            : (DataTemplate)FindResource("PenColorTemplate");
    }

    private void OnWidthDotClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: WidthPresetItem item })
        {
            CurrentWidth = item.Value;
            WidthPicked?.Invoke(this, EventArgs.Empty);
        }
    }

    private void OnWidthMinusClick(object sender, RoutedEventArgs e) => StepWidth(-1);

    private void OnWidthPlusClick(object sender, RoutedEventArgs e) => StepWidth(1);

    private void StepWidth(int direction)
    {
        var presets = WidthPresets?.ToList() ?? new List<double>();
        if (presets.Count == 0)
        {
            return;
        }
        var index = presets
            .Select((value, i) => (value, i))
            .OrderBy(pair => Math.Abs(pair.value - CurrentWidth))
            .First().i;
        var next = index + direction;
        if (next < 0 || next >= presets.Count)
        {
            return;
        }
        CurrentWidth = presets[next];
        WidthPicked?.Invoke(this, EventArgs.Empty);
    }

    private void OnColorItemClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: Color color })
        {
            RaiseColorPicked(color);
        }
    }

    private void OnRecentItemClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: Color color })
        {
            RaiseColorPicked(color);
        }
    }

    private void OnMoreColorsClick(object sender, RoutedEventArgs e)
        => MoreColorsClicked?.Invoke(this, EventArgs.Empty);

    private void OnEyedropperClick(object sender, RoutedEventArgs e)
        => EyedropperClicked?.Invoke(this, EventArgs.Empty);

    private void RaiseColorPicked(Color color)
    {
        CurrentColor = color;
        ColorPicked?.Invoke(this, new ColorPickedEventArgs(color));
    }
}