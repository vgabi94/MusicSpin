using Avalonia;
using Avalonia.Controls;
using Avalonia.Reactive;
using MusicSpin.App.Controls;
using MusicSpin.App.ViewModels;

namespace MusicSpin.App.Views;

/// <summary>
/// Now Playing. The track list is at most a share of the window high (mockup: max-height: 44vh). The deck tells the
/// view model how many pixels across its record shows, for the cover to be decoded no larger.
/// </summary>
public partial class NowPlayingView : UserControl
{
    private IDisposable? _windowSize;
    private IDisposable? _coverPixels;

    public NowPlayingView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => ReportCoverPixels(Deck.CoverPixels);
    }

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

        _coverPixels = Deck.GetObservable(DeckHost.CoverPixelsProperty).Subscribe(new AnonymousObserver<int>(ReportCoverPixels));
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _windowSize?.Dispose();
        _windowSize = null;
        _coverPixels?.Dispose();
        _coverPixels = null;
    }

    private void ReportCoverPixels(int pixels)
    {
        if (pixels > 0 && DataContext is MainWindowViewModel viewModel)
        {
            viewModel.NowPlaying.CoverPixels = pixels;
        }
    }
}
