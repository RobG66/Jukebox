using System.Threading;
using System.Threading.Tasks;

namespace Jukebox.Services;

/// <summary>
/// Abstracts the "show playing" OSD (on-screen display) animation.
/// Extracted from JukeboxViewModel to address MVVM leakage — see
/// Smell Test Report §4.1 (Warning: Direct dispatcher coupling in OSD animation)
/// and §7.2 item #8.
///
/// The OSD animation (3-second hold + 60-step fade) is purely visual and
/// belongs in a service layer, not the main ViewModel. The VM now calls
/// <see cref="ShowAsync"/> with the text and lets the service handle timing.
/// </summary>
public interface IShowPlayingService
{
    /// <summary>True while the OSD is currently visible (fading or held).</summary>
    bool IsVisible { get; }

    /// <summary>Current OSD opacity (0.0 - 1.0).</summary>
    double Opacity { get; }

    /// <summary>Current OSD text.</summary>
    string Text { get; }

    /// <summary>
    /// Raised whenever <see cref="IsVisible"/>, <see cref="Opacity"/>, or
    /// <see cref="Text"/> changes. Subscribers (typically the ViewModel)
    /// forward these to its own observable properties for binding.
    /// </summary>
    event System.EventHandler<ShowPlayingEventArgs>? Changed;

    /// <summary>
    /// Display the OSD with the given text, hold for the configured duration,
    /// then fade out. Cancels any previously-running OSD animation.
    /// </summary>
    /// <param name="text">Text to display.</param>
    /// <param name="cancellationToken">Optional token to cancel the animation.</param>
    Task ShowAsync(string text, CancellationToken cancellationToken = default);

    /// <summary>Immediately hide the OSD and cancel any pending animation.</summary>
    void Hide();
}

/// <summary>Event args for <see cref="IShowPlayingService.Changed"/>.</summary>
public sealed class ShowPlayingEventArgs : System.EventArgs
{
    public string Text { get; init; } = "";
    public double Opacity { get; init; }
    public bool IsVisible { get; init; }
}
