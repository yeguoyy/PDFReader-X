using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace PDFReaderX.App.Controls;

/// <summary>新建快捷笔对话框：选择类型/颜色/粗细，自动生成名称。</summary>
public partial class NewPenDialog : Window
{
    private readonly IReadOnlyList<double> _penWidths;
    private readonly IReadOnlyList<double> _highlightWidths;

    public NewPenDialog(IReadOnlyList<Color> colors, IReadOnlyList<double> penWidths, IReadOnlyList<double> highlightWidths)
    {
        InitializeComponent();
        _penWidths = penWidths;
        _highlightWidths = highlightWidths;
        ColorGrid.ItemsSource = colors;
        SelectedColor = Colors.Black;
        // 在 XAML 里挂 Checked 会在控件初始化中途触发（字段还没创建），所以改为构造完成后订阅
        PenTypeRadio.Checked += OnTypeChanged;
        HlTypeRadio.Checked += OnTypeChanged;
        UpdateTypeUi();
    }

    /// <summary>确认后创建的快捷笔（取消为 null）。</summary>
    public QuickPenStyle? Result { get; private set; }

    public Color SelectedColor { get; private set; }

    public double SelectedWidth { get; private set; }

    private bool IsHighlighter => HlTypeRadio.IsChecked == true;

    private void OnTypeChanged(object sender, RoutedEventArgs e)
    {
        UpdateTypeUi();
    }

    private void UpdateTypeUi()
    {
        if (HlTypeRadio is null || WidthBox is null)
        {
            return; // 控件树尚未构建完成
        }
        var presets = IsHighlighter ? _highlightWidths : _penWidths;
        WidthBox.ItemsSource = presets;
        var preferred = IsHighlighter ? 24.0 : 2.5;
        SelectedWidth = presets.FirstOrDefault(w => Math.Abs(w - preferred) < 0.01);
        if (SelectedWidth <= 0)
        {
            SelectedWidth = presets[Math.Min(2, presets.Count - 1)];
        }
        WidthBox.SelectedItem = SelectedWidth;
        PenPreview.Visibility = IsHighlighter ? Visibility.Collapsed : Visibility.Visible;
        MarkerPreview.Visibility = IsHighlighter ? Visibility.Visible : Visibility.Collapsed;
        UpdatePreview();
    }

    private void OnColorSwatchClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: Color color })
        {
            SelectedColor = color;
            UpdatePreview();
        }
    }

    private void OnMoreColorsClick(object sender, RoutedEventArgs e)
    {
        var dialog = new ColorPickerDialog(SelectedColor) { Owner = this };
        if (dialog.ShowDialog() == true)
        {
            SelectedColor = dialog.SelectedColor;
            UpdatePreview();
        }
    }

    private void UpdatePreview()
    {
        PenPreviewTip.Fill = new SolidColorBrush(SelectedColor);
        MarkerPreviewTip.Fill = new SolidColorBrush(SelectedColor);
        NameText.Text = $"名称：{BuildName()}";
    }

    private string BuildName()
    {
        var colorName = ColorName(SelectedColor);
        return IsHighlighter ? $"{colorName}荧光笔" : $"{colorName}钢笔";
    }

    private void OnAddClick(object sender, RoutedEventArgs e)
    {
        if (WidthBox.SelectedItem is double width && width > 0)
        {
            SelectedWidth = width;
        }
        var isHl = IsHighlighter;
        Result = new QuickPenStyle
        {
            Key = Guid.NewGuid().ToString("N"),
            Tool = isHl ? InkTool.Highlighter : InkTool.Pen,
            Kind = isHl ? "highlighter" : "pen",
            Color = SelectedColor,
            Width = SelectedWidth,
            Name = BuildName(),
        };
        DialogResult = true;
    }

    private static string ColorName(Color color)
    {
        foreach (var (value, name) in KnownColors)
        {
            if (value == color)
            {
                return name;
            }
        }
        return $"#{color.R:X2}{color.G:X2}{color.B:X2}";
    }

    private static readonly (Color, string)[] KnownColors =
    {
        (Colors.Black, "黑色"),
        (Colors.White, "白色"),
        (Colors.Red, "红色"),
        (Colors.Orange, "橙色"),
        (Colors.Yellow, "黄色"),
        (Colors.Green, "绿色"),
        (Colors.Lime, "亮绿色"),
        (Colors.Cyan, "青色"),
        (Colors.Blue, "蓝色"),
        (Colors.Purple, "紫色"),
        (Colors.Magenta, "品红色"),
        (Colors.Pink, "粉色"),
        (Colors.Brown, "棕色"),
        (Colors.Gray, "灰色"),
        (Colors.DarkSlateGray, "深灰色"),
    };
}