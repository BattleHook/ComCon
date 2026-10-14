using System;
using System.Globalization;
using System.Windows.Data;
using ComCon.Core.Models;

namespace ComCon.UI;

[ValueConversion(typeof(FramingMode), typeof(string))]
public sealed class FramingModeDisplayNameConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is FramingMode mode ? mode.ToDisplayName() : (value?.ToString() ?? string.Empty);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
