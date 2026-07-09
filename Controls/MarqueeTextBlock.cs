using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.VisualTree;
using System;
using System.Threading;

namespace Jukebox.Controls;

public class MarqueeTextBlock : TextBlock
{
    public static readonly StyledProperty<bool> IsPlayingProperty =
        AvaloniaProperty.Register<MarqueeTextBlock, bool>(nameof(IsPlaying));

    public bool IsPlaying
    {
        get => GetValue(IsPlayingProperty);
        set => SetValue(IsPlayingProperty, value);
    }

    public static readonly StyledProperty<double> MarqueeOffsetProperty =
        AvaloniaProperty.Register<MarqueeTextBlock, double>(nameof(MarqueeOffset));

    public double MarqueeOffset
    {
        get => GetValue(MarqueeOffsetProperty);
        set => SetValue(MarqueeOffsetProperty, value);
    }

    private Animation? _activeAnimation;
    private CancellationTokenSource? _animationCts;
    private readonly TranslateTransform _translateTransform;
    private double _currentOffset;

    static MarqueeTextBlock()
    {
        IsPlayingProperty.Changed.AddClassHandler<MarqueeTextBlock>((x, e) => x.OnIsPlayingChanged(e));
        TextProperty.Changed.AddClassHandler<MarqueeTextBlock>((x, e) => x.OnTextChanged(e));
    }

    public MarqueeTextBlock()
    {
        _translateTransform = new TranslateTransform();
        RenderTransform = _translateTransform;

        // Bind TranslateTransform.X to MarqueeOffset
        _translateTransform.Bind(TranslateTransform.XProperty, this.GetObservable(MarqueeOffsetProperty));

        LayoutUpdated += (s, e) => UpdateAnimation();
    }

    private void OnIsPlayingChanged(AvaloniaPropertyChangedEventArgs e)
    {
        UpdateAnimation();
    }

    private void OnTextChanged(AvaloniaPropertyChangedEventArgs e)
    {
        StopAnimation();
        UpdateAnimation();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        StopAnimation();
    }

    private Control? FindClippingContainer()
    {
        var parent = this.GetVisualParent() as Control;
        while (parent != null)
        {
            if (parent is Border)
                return parent;
            parent = parent.GetVisualParent() as Control;
        }
        return null;
    }

    private void UpdateAnimation()
    {
        // Marquee scrolling intentionally disabled. The control is kept for its
        // IsPlaying property (which the playlist uses to show the now-playing
        // speaker icon next to the track name), but the scrolling animation
        // itself was removed because:
        //   - It distracted from the playback experience.
        //   - The wider station-name column (added in this patch) shows most
        //     station names fully without needing to scroll.
        //   - The TextTrimming="CharacterEllipsis" binding (set in
        //     PlaylistView.axaml) already provides a clean "..." fallback for
        //     names that don't fit.
        //
        // To re-enable, restore the body below:
        //   if (!IsPlaying) { StopAnimation(); return; }
        //   var container = FindClippingContainer();
        //   if (container == null) { StopAnimation(); return; }
        //   ... (original scroll logic) ...
        StopAnimation();
    }

    private void StartMarqueeAnimation(double offset)
    {
        double scrollSpeed = 25; // 25 pixels per second (uniform speed)
        double scrollTime = Math.Abs(offset) / scrollSpeed;
        double startDelay = 2.0;
        double endDelay = 2.0;
        double snapTime = 0.1;
        double totalTime = startDelay + scrollTime + endDelay + snapTime;

        _activeAnimation = new Animation
        {
            Duration = TimeSpan.FromSeconds(totalTime),
            IterationCount = IterationCount.Infinite,
            Children =
            {
                new KeyFrame
                {
                    KeyTime = TimeSpan.FromSeconds(0),
                    Setters = { new Setter(MarqueeOffsetProperty, 0.0) }
                },
                new KeyFrame
                {
                    KeyTime = TimeSpan.FromSeconds(startDelay),
                    Setters = { new Setter(MarqueeOffsetProperty, 0.0) }
                },
                new KeyFrame
                {
                    KeyTime = TimeSpan.FromSeconds(startDelay + scrollTime),
                    Setters = { new Setter(MarqueeOffsetProperty, offset) }
                },
                new KeyFrame
                {
                    KeyTime = TimeSpan.FromSeconds(startDelay + scrollTime + endDelay),
                    Setters = { new Setter(MarqueeOffsetProperty, offset) }
                },
                new KeyFrame
                {
                    KeyTime = TimeSpan.FromSeconds(startDelay + scrollTime + endDelay + snapTime),
                    Setters = { new Setter(MarqueeOffsetProperty, 0.0) }
                }
            }
        };

        _animationCts = new CancellationTokenSource();
        _activeAnimation.RunAsync(this, _animationCts.Token);
    }

    private void StopAnimation()
    {
        if (_animationCts != null)
        {
            _animationCts.Cancel();
            _animationCts.Dispose();
            _animationCts = null;
        }
        _activeAnimation = null;
        _currentOffset = 0;
        MarqueeOffset = 0;
    }
}
