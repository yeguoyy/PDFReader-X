using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace PDFReaderX.App.Converters;

/// <summary>Color → SolidColorBrush，用于工具栏颜色选择。</summary>
public sealed class ColorToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is Color color ? new SolidColorBrush(color) : Binding.DoNothing;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}
