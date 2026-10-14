using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MusicSpin.App.Services;

namespace MusicSpin.App.ViewModels;

/// <summary>Something that opens over the window and Esc closes (see <see cref="LayerRegistry"/>).</summary>
public abstract partial class LayerViewModel : ObservableObject, ILayer
{
    public abstract int Order { get; }

    [ObservableProperty]
    public partial bool IsOpen { get; private set; }

    [RelayCommand]
    public void Open() => IsOpen = true;

    [RelayCommand]
    public void Close() => IsOpen = false;

    [RelayCommand]
    public void Toggle() => IsOpen = !IsOpen;
}

/// <summary>The Now Playing overlay (opened from the player bar's cover; the overlay itself comes with Now Playing).</summary>
public sealed class NowPlayingViewModel : LayerViewModel
{
    public override int Order => LayerOrder.NowPlaying;
}

/// <summary>The queue drawer (opened from the player bar's queue button; the drawer itself comes with the queue).</summary>
public sealed class QueueDrawerViewModel : LayerViewModel
{
    public override int Order => LayerOrder.QueueDrawer;
}
