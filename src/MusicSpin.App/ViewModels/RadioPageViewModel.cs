using System.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MusicSpin.App.Services;

namespace MusicSpin.App.ViewModels;

/// <summary>
/// Radio: endless random playback of the library. This page starts a station over the whole library; choosing
/// filters comes with the radio page itself.
/// </summary>
public sealed partial class RadioPageViewModel : PageViewModel
{
    private readonly PlayerViewModel _player;
    private readonly Notices _notices;

    public RadioPageViewModel(PlayerViewModel player, Notices notices)
    {
        ArgumentNullException.ThrowIfNull(player);
        _player = player;
        _notices = notices;
        _player.PropertyChanged += OnPlayerChanged;
    }

    public override string Title => "Radio";

    /// <summary>What is on air ("whole library"), or null when no station plays.</summary>
    public string? OnAir => _player.IsRadio ? _player.RadioDescription : null;

    public override Task LoadAsync() => Task.CompletedTask;

    [RelayCommand]
    private Task StartRadio() => _notices.GuardAsync(() => _player.StartRadioAsync());

    private void OnPlayerChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(PlayerViewModel.QueueMode) or nameof(PlayerViewModel.RadioDescription))
        {
            OnPropertyChanged(nameof(OnAir));
        }
    }
}
