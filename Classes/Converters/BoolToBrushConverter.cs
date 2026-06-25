using Avalonia.Data.Converters;
using Avalonia.Media;
using System;
using System.Globalization;

namespace Jukebox.Classes.Converters;

/// <summary>
/// Converts a boolean to one of two brushes.
///
/// Used in JukeboxControl.axaml to switch the transport bar's background
/// between opaque (auto-hide OFF — bar has its own row) and semi-transparent
/// (auto-hide ON — bar overlays the playing content with opacity so playback
/// is partially visible underneath).
///
/// Usage in XAML:
///   &lt;conv:BoolToBrushConverter x:Key="AutoHideBgConverter"
///       TrueBrush="#B0000000"
///       FalseBrush="{DynamicResource PanelBackground}"/&gt;
///   ...
///   &lt;Border Background="{Binding IsAutoHideEnabled,
///       Converter={StaticResource AutoHideBgConverter}}"/&gt;
/// </summary>
public class BoolToBrushConverter : IValueConverter
{
    /// <summary>Brush used when the bound value is true.</summary>
    public IBrush? TrueBrush { get; set; }

    /// <summary>Brush used when the bound value is false.</summary>
    public IBrush? FalseBrush { get; set; }

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is bool b)
            return b ? TrueBrush : FalseBrush;
        return FalseBrush;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
