using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;

namespace Jukebox.Services;

/// <summary>
/// Default <see cref="IShowPlayingService"/> implementation.
///
/// Replaces the OSD animation logic that previously lived in
/// JukeboxViewModel.TriggerShowPlayingOSDAsync (lines 119-160). The logic
/// is identical — 3-second hold, then 60-step fade over 3 seconds — but
/// it now lives behind an interface so the ViewModel can be tested without
/// a real Dispatcher, and so the animation can be swapped or mocked.
///
/// Threading: all observable state updates are posted to the UI thread via
/// <see cref="Dispatcher.UIThread.Post"/>. The animation itself runs on the
/// calling thread (which is the UI thread in normal use).
/// </summary>
public sealed class ShowPlayingService : IShowPlayingService
{
    private readonly object _lock = new();
    private CancellationTokenSource? _cts;

    public bool IsVisible { get; private set; }
    public double Opacity { get; private set; }
    public string Text { get; private set; } = "";

    public event EventHandler<ShowPlayingEventArgs>? Changed;

    public async Task ShowAsync(string text, double? holdSeconds = null, bool alwaysShow = false, CancellationToken cancellationToken = default)
    {
        CancellationTokenSource cts;
        lock (_lock)
        {
            _cts?.Cancel();
            _cts?.Dispose();
            _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts = _cts;
        }
        var token = cts.Token;

        // Use the caller-specified hold duration, or fall back to the
        // default (3 seconds).
        int holdMs = holdSeconds.HasValue
            ? (int)(holdSeconds.Value * 1000)
            : Constants.OsdHoldMs;

        Text = text;
        Opacity = Constants.OsdStartOpacity;
        IsVisible = true;
        RaiseChanged();

        if (alwaysShow)
        {
            return;
        }

        try
        {
            await Task.Delay(holdMs, token);

            int steps = Constants.OsdFadeSteps;
            int delay = holdMs / steps;
            double stepDrop = Constants.OsdStartOpacity / steps;

            for (int i = 0; i < steps; i++)
            {
                await Task.Delay(delay, token);
                lock (_lock)
                {
                    if (cts != _cts) return;
                }
                Opacity -= stepDrop;
                RaiseChanged();
            }

            lock (_lock)
            {
                if (cts != _cts) return;
            }
            Opacity = 0.0;
            IsVisible = false;
            RaiseChanged();
        }
        catch (TaskCanceledException) { }
    }

    public void Hide()
    {
        lock (_lock)
        {
            _cts?.Cancel();
            _cts?.Dispose();
            _cts = null;
        }
        IsVisible = false;
        Opacity = 0.0;
        RaiseChanged();
    }

    private void RaiseChanged()
    {
        // Always dispatch the event raise to the UI thread, since the OSD
        // animation runs from PropertyChanged handlers which may be invoked
        // from any thread (e.g., when CurrentTrack changes from a VLC callback).
        Dispatcher.UIThread.Post(() =>
        {
            Changed?.Invoke(this, new ShowPlayingEventArgs
            {
                Text = Text,
                Opacity = Opacity,
                IsVisible = IsVisible,
            });
        });
    }
}
