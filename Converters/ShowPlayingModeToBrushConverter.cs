using Avalonia.Data.Converters;
using Avalonia.Media;
using Jukebox.Models;
using System;
using System.Globalization;

namespace Jukebox.Converters;

public sealed class ShowPlayingModeToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is ShowPlayingMode mode)
        {
            return mode switch
            {
                ShowPlayingMode.Off => Brushes.Gray,
                ShowPlayingMode.Briefly => Brushes.White,
                ShowPlayingMode.Always => Brushes.LightGreen,
                _ => Brushes.Gray
            };
        }
        return Brushes.Gray;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
