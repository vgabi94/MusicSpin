using Avalonia;
using Avalonia.Controls;
using Avalonia.Reactive;

namespace MusicSpin.App.Views;

/// <summary>Now Playing. The track list is at most a share of the window high (mockup: max-height: 44vh).</summary>
public partial class NowPlayingView : UserControl
{
    private IDisposable? _windowSize;

    public NowPlayingView() => InitializeComponent();

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (TopLevel.GetTopLevel(this) is { } top)
        {
            _windowSize = top.GetObservable(TopLevel.ClientSizeProperty).Subscribe(new AnonymousObserver<Size>(size =>
            {
                if (this.TryFindResource("NowPlayingTrackListShare", out var share) && share is double s)
                {
                    TrackScroller.MaxHeight = size.Height * s;
                }
            }));
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _windowSize?.Dispose();
        _windowSize = null;
    }
}
