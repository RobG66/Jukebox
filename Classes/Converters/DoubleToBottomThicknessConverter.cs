using Avalonia;
using Avalonia.Data.Converters;
using System;
using System.Globalization;

namespace Jukebox.Classes.Converters;

/// <summary>
/// Value converter that maps a double value (representing height)
/// to a Thickness margin with the bottom property set to that value.
/// </summary>
public class DoubleToBottomThicknessConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is double d)
        {
            return new Thickness(0, 0, 0, d);
        }
        if (value is float f)
        {
            return new Thickness(0, 0, 0, f);
        }
        if (value is int i)
        {
            return new Thickness(0, 0, 0, i);
        }
        return new Thickness(0);
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
