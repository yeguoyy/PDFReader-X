using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace PDFReaderX.App.Converters;

/// <summary>枚举与 ConverterParameter 相等 → Visible，否则 Collapsed（OneNote 工具栏上下文面板显示用）。</summary>
public sealed class EnumToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var equals = value is Enum enumValue && parameter is string name && enumValue.ToString() == name;
        return equals ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}

/// <summary>两个绑定值相等 → Visible（色板/粗细的选中指示环用）。</summary>
public sealed class EqualityToVisibilityConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        var equals = values.Length >= 2 && Equals(values[0], values[1]);
        return equals ? Visibility.Visible : Visibility.Collapsed;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>粗细值 → 圆形预览直径（像素）。ConverterParameter 传 pen/highlighter/eraser 区分缩放。</summary>
public sealed class WidthToDotSizeConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not double width)
        {
            return 8.0;
        }
        return parameter switch
        {
            "highlighter" => Math.Min(18, 3 + width * 0.42),
            "eraser" => Math.Min(18, 3 + width * 0.42),
            _ => Math.Min(18, 2 + width * 2.6),
        };
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
