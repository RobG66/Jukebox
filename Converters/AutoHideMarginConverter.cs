using Avalonia.Data.Converters;
using System;
using System.Collections.Generic;
using System.Globalization;

namespace Jukebox.Converters;

/// <summary>
/// Multi-value converter that computes the bottom margin for the content area
/// based on whether the transport bar is in auto-hide mode.
///
/// Inputs:
///   values[0] = IsAutoHideEnabled (bool)
///   values[1] = ControlBarHeight (double)
///
/// Output:
///   Thickness with Bottom = 0 when auto-hide is ON (content fills full height,
///   transport bar overlays with opacity). When auto-hide is OFF, Bottom =
///   ControlBarHeight so content stops above the bar (bar has its own space).
///
/// Used in JukeboxControl.axaml to bind ContentView.Margin.
/// </summary>
public class AutoHideMarginConverter : IMultiValueConverter
{
    public object? Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values.Count >= 2
            && values[0] is bool isAutoHide
            && values[1] is double height)
        {
            double bottom = isAutoHide ? 0 : height;
            return new Avalonia.Thickness(0, 0, 0, bottom);
        }
        // Fallback: no margin
        return new Avalonia.Thickness(0);
    }
}
