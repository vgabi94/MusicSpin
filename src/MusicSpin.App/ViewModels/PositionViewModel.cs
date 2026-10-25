using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MusicSpin.App.Services;

namespace MusicSpin.App.ViewModels;

/// <summary>
/// The position bar and the elapsed time (mockup: "Position"). Dragging the bar moves <see cref="Scrub"/> (the elapsed
/// time follows it) and seeks on release; the elapsed time is a box that takes the CLI's <c>seek</c> syntax.
/// The position moves 30 times a second while playing, but the times show whole seconds: <see cref="Shown"/>,
/// <see cref="ValueText"/> and <see cref="Elapsed"/> change only when the second shown does (#121).
/// </summary>
public sealed partial class PositionViewModel : ObservableObject
{
    /// <summary>←/→ on the bar and Ctrl+←/→ anywhere.</summary>
    public const double KeyStep = 5;

    /// <summary>PgUp/PgDn on the bar.</summary>
    public const double LargeKeyStep = 30;

    private readonly PlayerViewModel _player;

    /// <summary>The whole second the times show.</summary>
    private double _second;

    public PositionViewModel(PlayerViewModel player)
    {
        ArgumentNullException.ThrowIfNull(player);
        _player = player;
        _second = Second(Shown);
        Elapsed = new TypedField(() => Formats.Time(Shown), ApplyTyped);
        player.PropertyChanged += OnPlayerChanged;
    }

    /// <summary>The elapsed time box.</summary>
    public TypedField Elapsed { get; }

    public double Duration => _player.Duration;

    /// <summary>Where the player is.</summary>
    public double Position => _player.Position;

    /// <summary>While the bar is dragged: where it would seek (the bar sets it).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Shown), nameof(ValueText))]
    public partial double? Scrub { get; set; }

    /// <summary>The position the bar and the elapsed time show.</summary>
    public double Shown => Scrub ?? _player.Position;

    public string DurationText => Formats.Time(Duration);

    /// <summary>For screen readers: "1:23 of 6:46".</summary>
    public string ValueText => $"{Formats.Time(Shown)} of {Formats.Time(Duration)}";

    public bool CanSeek => _player.HasTrack && Duration > 0;

    /// <summary>Seeks to <paramref name="seconds"/>, kept within the track.</summary>
    [RelayCommand(AllowConcurrentExecutions = true)]
    private Task SeekTo(double seconds) => CanSeek ? _player.SeekCommand.ExecuteAsync(Math.Clamp(seconds, 0, Duration)) : Task.CompletedTask;

    /// <summary>Ctrl+←/→: moves by <paramref name="seconds"/> from where the player is.</summary>
    [RelayCommand(AllowConcurrentExecutions = true)]
    private Task SeekBy(double seconds) => SeekTo(_player.Position + seconds);

    private bool ApplyTyped(string text)
    {
        if (!CanSeek || !TypedValues.TryPosition(text, _player.Position, Duration, out var seconds))
        {
            return false;
        }

        _ = SeekTo(seconds);
        return true;
    }

    partial void OnScrubChanged(double? value)
    {
        _second = Second(Shown);
        Elapsed.Refresh();
    }

    /// <summary>The whole second <paramref name="seconds"/> shows as (<see cref="Formats.Time"/> rounds down).</summary>
    private static double Second(double seconds) => Math.Floor(Math.Max(0, seconds));

    private void OnPlayerChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(PlayerViewModel.Position):
                OnPropertyChanged(nameof(Position)); // the bar (it moves only by whole pixels: ScrubBar.FillWidth)
                if (Scrub is null && Second(_player.Position) != _second)
                {
                    _second = Second(_player.Position);
                    OnPropertyChanged(nameof(Shown));
                    OnPropertyChanged(nameof(ValueText));
                    Elapsed.Refresh();
                }

                break;
            case nameof(PlayerViewModel.Duration):
                OnPropertyChanged(nameof(Duration));
                OnPropertyChanged(nameof(DurationText));
                OnPropertyChanged(nameof(ValueText));
                OnPropertyChanged(nameof(CanSeek));
                break;
            case nameof(PlayerViewModel.HasTrack):
                OnPropertyChanged(nameof(CanSeek));
                break;
        }
    }
}
