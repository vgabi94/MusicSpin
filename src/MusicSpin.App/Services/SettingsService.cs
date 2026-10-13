using MusicSpin.Core.Hosting;
using MusicSpin.Core.Settings;

namespace MusicSpin.App.Services;

/// <summary>The app's view of <c>settings.json</c>, which it shares with the CLI.</summary>
public interface ISettingsService
{
    AppSettings Current { get; }

    /// <summary>
    /// Re-reads the file (the CLI may have changed other values), applies <paramref name="change"/> and saves.
    /// </summary>
    /// <exception cref="Core.MusicSpinException">DataDirectoryNotWritable: <see cref="Current"/> still changes for this session.</exception>
    void Update(Func<AppSettings, AppSettings> change);
}

public sealed class SettingsService(DataDirectory directory) : ISettingsService
{
    private readonly SettingsStore _store = new(directory);

    public AppSettings Current { get; private set; } = new SettingsStore(directory).Load();

    public void Update(Func<AppSettings, AppSettings> change)
    {
        ArgumentNullException.ThrowIfNull(change);
        var fresh = _store.Load();
        Current = change(_store.LoadWarning is null ? fresh : Current);
        _store.Save(Current);
    }
}
