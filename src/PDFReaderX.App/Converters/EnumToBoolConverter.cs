using System.Globalization;
using System.Windows.Data;

namespace PDFReaderX.App.Converters;

/// <summary>枚举值相等 → true（RadioButton 的工具切换用）。ConverterParameter 传枚举名字符串。</summary>
public sealed class EnumToBoolConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is Enum enumValue && parameter is string name)
        {
            return enumValue.ToString() == name;
        }
        return value?.Equals(parameter) == true;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is true && parameter is string name && targetType.IsEnum)
        {
            return Enum.Parse(targetType, name);
        }
        return Binding.DoNothing;
    }
}
