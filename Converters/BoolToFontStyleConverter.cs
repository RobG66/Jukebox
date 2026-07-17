using Avalonia.Data.Converters;
using Avalonia.Media;
using System;
using System.Globalization;

namespace Jukebox.Converters;

// Converts a bool to a FontStyle.
// true  -> FontStyle.Italic  (used to visually distinguish the transient browser preview slot)
// false -> FontStyle.Normal
public class BoolToFontStyleConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value is true ? FontStyle.Italic : FontStyle.Normal;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
