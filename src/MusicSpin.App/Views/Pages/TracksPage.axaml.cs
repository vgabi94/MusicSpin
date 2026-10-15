using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using MusicSpin.App.ViewModels;

namespace MusicSpin.App.Views.Pages;

public partial class TracksPage : UserControl
{
    public TracksPage()
    {
        InitializeComponent();
        TrackList.AddHandler(DoubleTappedEvent, OnDoubleTapped);
    }

    /// <summary>Double-clicking a row plays it, except on its buttons (they act on the first click).</summary>
    private void OnDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (e.Source is Visual source && source.FindAncestorOfType<Button>(includeSelf: true) is null
            && source is StyledElement { DataContext: TrackRow row } && DataContext is TrackListPageViewModel viewModel)
        {
            viewModel.PlayTrackCommand.Execute(row.Item);
            e.Handled = true;
        }
    }
}
