using Avalonia;
using Avalonia.Data.Converters;
using System;
using System.Collections.Generic;
using System.Globalization;

namespace Jukebox.Converters;

// Multi-value converter to compute the top-left margin for the OSD.
//
// Inputs:
//   values[0] = IsPlaylistVisible (bool)
//   values[1] = IsPickerVisible (bool)
//
// Output:
//   Thickness with Left = 420 when a side panel is open, 20 when closed.
public class OsdMarginConverter : IMultiValueConverter
{
    public object? Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
    {
        bool isPanelOpen = false;
        if (values.Count >= 2)
        {
            isPanelOpen = (values[0] is bool playlist && playlist) || 
                          (values[1] is bool picker && picker);
        }

        double left = isPanelOpen ? (Constants.SidePanelWidth + 20) : 20;
        return new Thickness(left, 20, 0, 0);
    }
}

// Multi-value converter to compute the maximum width for the OSD TextBlock.
//
// Inputs:
//   values[0] = IsPlaylistVisible (bool)
//   values[1] = IsPickerVisible (bool)
//   values[2] = ParentWidth (double)
//
// Output:
//   Available width subtracting the OSD left offset and right padding.
public class OsdMaxWidthConverter : IMultiValueConverter
{
    public object? Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
    {
        bool isPanelOpen = false;
        double parentWidth = 0;

        if (values.Count >= 3)
        {
            isPanelOpen = (values[0] is bool playlist && playlist) || 
                          (values[1] is bool picker && picker);
            
            if (values[2] is double w)
            {
                parentWidth = w;
            }
        }

        double offset = isPanelOpen ? (Constants.SidePanelWidth + 20 + 20) : 40; // Left margin + Right margin (20)
        return Math.Max(100, parentWidth - offset);
    }
}
