using MusicSpin.App.Services;
using MusicSpin.App.Theming;

namespace MusicSpin.App.Composition;

/// <summary>
/// Everything the view models need from outside: built once at startup by <see cref="AppComposition"/> (the real
/// player, library and clock), or by tests with a player in the test process, a temporary library and a fake clock.
/// </summary>
public sealed record AppServices(
    ISettingsService Settings,
    ILibraryService Library,
    ICoverService Covers,
    IPlayerConnector Player,
    IAppearance Appearance,
    TimeProvider Time,
    IUiDispatcher Ui,
    string? StartupWarning = null);
