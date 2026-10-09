namespace MusicSpin.Core.Settings;

/// <summary>User settings persisted as JSON in the data folder (<c>settings.json</c>).</summary>
public sealed record AppSettings
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    /// <summary>Preferred output device id or name; null = system default.</summary>
    public string? OutputDevice { get; init; }

    /// <summary>Open the output device exclusively (bit-perfect).</summary>
    public bool ExclusiveMode { get; init; }

    /// <summary>Linear volume 0..1.</summary>
    public double Volume { get; init; } = 1.0;

    /// <summary>Keep the library in sync with the file system while the app runs.</summary>
    public bool WatchLibrary { get; init; }

    /// <summary>Default for <see cref="PlayerIdleTimeoutSeconds"/>: 15 minutes.</summary>
    public const double DefaultPlayerIdleTimeoutSeconds = 900;

    /// <summary>
    /// The background player exits after being stopped (or having an empty queue) this long with no app attached.
    /// 0 = never. Paused playback keeps it alive.
    /// </summary>
    public double PlayerIdleTimeoutSeconds { get; init; } = DefaultPlayerIdleTimeoutSeconds;

    /// <summary>Repeat mode a new background player starts with: "off" | "one" | "all". Updated by 'music-spin repeat'.</summary>
    public string Repeat { get; init; } = "off";

    /// <summary>Whether a new background player starts with shuffle on. Updated by 'music-spin shuffle'.</summary>
    public bool Shuffle { get; init; }
}
