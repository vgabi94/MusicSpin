using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MusicSpin.App.Services;

namespace MusicSpin.App.ViewModels;

/// <summary>
/// A page of Settings (#188): an entry in the dialog's sidebar (its title and icon) and what the dialog shows on the
/// right while it is chosen. A page that reads something when Settings opens (the library's folders, the audio
/// devices) can fail: it then says why instead of its settings, with Try again, and its entry is marked.
/// </summary>
public abstract partial class SettingsPage : ObservableObject
{
    private readonly Notices _notices;

    /// <param name="icon">A key in Icons/IconsResource.axaml.</param>
    protected SettingsPage(string title, string icon, Notices notices)
    {
        ArgumentNullException.ThrowIfNull(notices);
        Title = title;
        Icon = icon;
        _notices = notices;
    }

    /// <summary>"Appearance": the sidebar entry, its accessible name and the page's heading.</summary>
    public string Title { get; }

    public string Icon { get; }

    /// <summary>Why the page couldn't load (null: it did, or it has nothing to load).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasProblem))]
    public partial string? Problem { get; private set; }

    public bool HasProblem => Problem is not null;

    /// <summary>"Library couldn't load": over the reason, when there is one.</summary>
    public string ProblemTitle => $"{Title} couldn't load";

    /// <summary>
    /// Reads what the page shows afresh (Settings opening, and Try again). A failure stays on the page until the next
    /// load succeeds.
    /// </summary>
    [RelayCommand]
    public async Task LoadAsync() => Problem = await _notices.TryAsync(ReadAsync).ConfigureAwait(true);

    /// <summary>What <see cref="LoadAsync"/> reads; throws when it can't. Most pages have nothing to read.</summary>
    protected virtual Task ReadAsync() => Task.CompletedTask;
}

/// <summary>The theme and Motion.</summary>
public sealed class AppearancePage(SettingsViewModel settings, Notices notices) : SettingsPage("Appearance", "Appearance", notices)
{
    public SettingsViewModel Settings { get; } = settings;
}

/// <summary>The record speed, the vinyl transition and the deck's design.</summary>
public sealed class TurntablePage(SettingsViewModel settings, Notices notices) : SettingsPage("Turntable", "Turntable", notices)
{
    public SettingsViewModel Settings { get; } = settings;
}

/// <summary>The shelf's tilt and depth.</summary>
public sealed class ShelfPage(SettingsViewModel settings, Notices notices) : SettingsPage("Shelf", "Shelf", notices)
{
    public SettingsViewModel Settings { get; } = settings;
}

/// <summary>The music folders, the scan and the folder watch (#31).</summary>
public sealed class LibraryPage(LibrarySettingsViewModel library, Notices notices) : SettingsPage("Library", "Folder", notices)
{
    public LibrarySettingsViewModel Library { get; } = library;

    protected override Task ReadAsync() => Library.ReadAsync();
}

/// <summary>The output device, exclusive mode and volume levelling (#32, #139).</summary>
public sealed class AudioPage(AudioSettingsViewModel audio, Notices notices) : SettingsPage("Audio", "AudioOutput", notices)
{
    public AudioSettingsViewModel Audio { get; } = audio;

    protected override Task ReadAsync() => Audio.ReadAsync();
}

/// <summary>The 10-band equalizer and its presets (#141).</summary>
public sealed class EqualizerPage(EqualizerViewModel equalizer, Notices notices) : SettingsPage("Equalizer", "Equalizer", notices)
{
    public EqualizerViewModel Equalizer { get; } = equalizer;
}
