using Avalonia;
using Avalonia.Controls;

namespace Jukebox.Controls;

/// <summary>
/// A <see cref="TextBlock"/> subclass that exposes an <see cref="IsPlaying"/>
/// styled property. The playlist DataGrid cell template binds the now-playing
/// speaker icon's <c>IsVisible</c> to this property.
///
/// Historically this control also implemented a marquee scrolling animation
/// for long track names. That animation was disabled (it distracted from the
/// playback experience) and the implementation removed — <c>TextTrimming</c>
/// on the cell template handles overflow with an ellipsis instead. The class
/// is retained because the XAML references it and the <c>IsPlaying</c>
/// property provides a clean binding target.
/// </summary>
public class MarqueeTextBlock : TextBlock
{
    public static readonly StyledProperty<bool> IsPlayingProperty =
        AvaloniaProperty.Register<MarqueeTextBlock, bool>(nameof(IsPlaying));

    public bool IsPlaying
    {
        get => GetValue(IsPlayingProperty);
        set => SetValue(IsPlayingProperty, value);
    }
}
