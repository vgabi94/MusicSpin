using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MusicSpin.App.Services;

namespace MusicSpin.App.ViewModels;

/// <summary>
/// An artist's or a genre's Play and Radio (mockup: groupActs), on its group header or its crate's label. ▶ Play
/// replaces the queue with everything the group shows, in the order shown (#41). Radio starts the group's station, and
/// while that station is on air reads "On air" and opens the Radio page (#23).
/// </summary>
public sealed partial class GroupActionsViewModel : ObservableObject
{
    /// <summary>Radio's tooltip on the group of tracks without a genre: no <c>--genre</c> selects them.</summary>
    public const string NoStationTip = "No station for tracks without a genre";

    private readonly Func<Task<IReadOnlyList<TrackSummary>>> _tracks;
    private readonly PlayerViewModel _player;
    private readonly RadioViewModel _radio;
    private readonly Notices _notices;

    /// <param name="kind">Artist or genre.</param>
    /// <param name="name">The group's name: the artist, or the genre (<see cref="LibraryOrder.UnknownGenre"/> has no station).</param>
    /// <param name="tracks">What the group shows, as tracks in the order shown (read when Play is clicked).</param>
    public GroupActionsViewModel(RadioTagKind kind, string name, Func<Task<IReadOnlyList<TrackSummary>>> tracks, PlayerViewModel player, RadioViewModel radio,
        Notices notices)
    {
        ArgumentNullException.ThrowIfNull(radio);
        Kind = kind;
        Name = name;
        HasStation = !(kind == RadioTagKind.Genre && name == LibraryOrder.UnknownGenre);
        Station = [new RadioTag(kind, name)];
        _tracks = tracks;
        _player = player;
        _radio = radio;
        _notices = notices;
        Refresh();
    }

    public RadioTagKind Kind { get; }

    public string Name { get; }

    /// <summary>The group's station: the artist, or the genre.</summary>
    public IReadOnlyList<RadioTag> Station { get; }

    /// <summary>False for the tracks without a genre: Radio is disabled and says why.</summary>
    public bool HasStation { get; }

    /// <summary>"Play everything by Delta Ruth", "Play all Jazz".</summary>
    public string PlayToolTip => Kind == RadioTagKind.Artist ? $"Play everything by {Name}" : $"Play all {Name}";

    /// <summary>"Jazz radio" (mockup: .radio-btn title).</summary>
    public string RadioToolTip => HasStation ? $"{Name} radio" : NoStationTip;

    /// <summary>The group's station is on air.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RadioLabel))]
    public partial bool IsOnAir { get; private set; }

    /// <summary>"Radio", or "On air" while the group's station plays.</summary>
    public string RadioLabel => IsOnAir ? "On air" : "Radio";

    /// <summary>The station on air changed: the button follows.</summary>
    public void Refresh() => IsOnAir = HasStation && _radio.Plays(Station);

    /// <summary>
    /// ▶ Play: one <c>queue.set</c> with the group's tracks, played from the first, or, with shuffle on, in the
    /// player's shuffled order. It ends radio, as <c>play</c> does.
    /// </summary>
    [RelayCommand]
    private Task Play() => _notices.GuardAsync(async () =>
    {
        var tracks = await _tracks().ConfigureAwait(true);
        await _player.PlayTracksAsync(tracks, start: null).ConfigureAwait(true);
    });

    /// <summary>Radio: starts the group's station; while it is on air, opens the Radio page.</summary>
    [RelayCommand(CanExecute = nameof(HasStation))]
    private Task Radio()
    {
        if (IsOnAir)
        {
            _radio.OpenPageCommand.Execute(null);
            return Task.CompletedTask;
        }

        return _radio.StartAsync(Station);
    }
}
