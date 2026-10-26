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

    /// <summary>
    /// How much audio (milliseconds) is decoded ahead of the output. Bigger survives slower disks/CPUs; smaller reacts to
    /// nothing faster (seek and skip flush it). Values outside 100..10000 are clamped.
    /// </summary>
    public int PlaybackBufferMs { get; init; } = 2000;

    /// <summary>
    /// Show a tray icon (Play/Pause, Next, Previous, Quit) while the background player runs in a desktop session (#83).
    /// Turned off for one player with <c>music-spin play --no-tray</c> or <c>MUSICSPIN_NO_TRAY=1</c>.
    /// </summary>
    public bool TrayIcon { get; init; } = true;

    /// <summary>Play consecutive tracks with the same sample rate, channels and bit depth without a gap.</summary>
    public bool Gapless { get; init; } = true;

    /// <summary>
    /// Volume levelling (ReplayGain / R128, #139): "auto" (the default: album gain for an album played in order, track gain
    /// otherwise) | "track" | "album" | "off". "off" leaves the samples untouched. Updated by 'music-spin normalize'.
    /// </summary>
    public string Normalization { get; init; } = "auto";

    /// <summary>The loudness (LUFS) levelled tracks are brought to: -30..-8, -18 by default (ReplayGain 2.0's reference).</summary>
    public double NormalizationTargetLufs { get; init; } = global::MusicSpin.Core.Normalization.NormalizationSettings.DefaultTargetLufs;

    /// <summary>Added to every levelled track's gain, in dB: -12..+12.</summary>
    public double NormalizationPreampDb { get; init; }

    // ---- Desktop app (Settings in the app; the CLI ignores them) ----

    /// <summary>The app's colour theme: "dark" | "light".</summary>
    public string Theme { get; init; } = "dark";

    /// <summary>
    /// How the app animates: "smooth" (full durations), "fast" (half) or "none" (instant). The record keeps spinning in
    /// every mode.
    /// </summary>
    public string Motion { get; init; } = "smooth";

    /// <summary>How fast the records in the app turn: 33.333, 45 or 16.667 rpm.</summary>
    public double RecordSpeedRpm { get; init; } = 33.333;

    /// <summary>The turntable design in Now Playing: "classic" (the default) or another design's id.</summary>
    public string TurntableDesign { get; init; } = "classic";

    /// <summary>The album shelf's tilt in degrees (20..80).</summary>
    public double ShelfTilt { get; init; } = 58;

    /// <summary>The album shelf's perspective depth (0 = flat .. 100).</summary>
    public double ShelfDepth { get; init; } = 40;

    /// <summary>Whether the 10-band graphic equalizer (#141) filters playback.</summary>
    public bool EqualizerEnabled { get; init; }

    /// <summary>The equalizer's ten band gains in dB (31 Hz .. 16 kHz, each -12..+12); null or another length is flat.</summary>
    public double[]? EqualizerGains { get; init; }

    /// <summary>The equalizer's pre-amp in dB (-12..+12); automatic headroom keeps boosts from clipping on top of it.</summary>
    public double EqualizerPreampDb { get; init; }

    /// <summary>The preset the gains came from, or null once a band was changed by hand.</summary>
    public string? EqualizerPreset { get; init; } = "flat";

    /// <summary>The user's saved equalizer presets: name to ten band gains in dB.</summary>
    public Dictionary<string, double[]>? EqualizerPresets { get; init; }
}
