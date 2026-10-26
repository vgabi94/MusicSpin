namespace MusicSpin.Core.Equalization;

/// <summary>
/// The player's equalizer: the settings every playback stream filters with, and the presets that can be applied.
/// Changed from any thread (the protocol, the app); the audio thread only reads <see cref="Settings"/> (one volatile
/// read per block) and fades to a change, so changing a band while playing never clicks.
/// </summary>
public sealed class GraphicEqualizer
{
    private readonly Lock _gate = new();
    private EqualizerSettings _settings;
    private List<EqualizerPreset> _user;

    public GraphicEqualizer(EqualizerSettings? settings = null, IEnumerable<EqualizerPreset>? userPresets = null)
    {
        _settings = (settings ?? EqualizerSettings.Default).Clamped();
        _user = [.. (userPresets ?? []).Where(p => !p.BuiltIn)];
    }

    /// <summary>The settings now in effect (an immutable snapshot).</summary>
    public EqualizerSettings Settings => Volatile.Read(ref _settings);

    /// <summary>Raised after every change (on the changing thread).</summary>
    public event EventHandler? Changed;

    /// <summary>The built-in presets, then the user's.</summary>
    public IReadOnlyList<EqualizerPreset> Presets
    {
        get
        {
            lock (_gate)
            {
                return [.. EqualizerSettings.BuiltInPresets, .. _user];
            }
        }
    }

    public IReadOnlyList<EqualizerPreset> UserPresets
    {
        get
        {
            lock (_gate)
            {
                return [.. _user];
            }
        }
    }

    public EqualizerPreset? Find(string? name)
    {
        var n = EqualizerSettings.NormalizeName(name);
        lock (_gate)
        {
            return EqualizerSettings.BuiltIn(n) ?? _user.FirstOrDefault(p => p.Name == n);
        }
    }

    /// <summary>Replaces the settings (brought into range).</summary>
    public void Configure(EqualizerSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        Volatile.Write(ref _settings, settings.Clamped());
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Turns the equalizer on with <paramref name="name"/>'s gains; throws <see cref="ArgumentException"/> for an unknown preset.</summary>
    public void ApplyPreset(string name)
    {
        var preset = Find(name) ?? throw new ArgumentException(
            $"Unknown equalizer preset '{name}'. Presets: {string.Join(", ", Presets.Select(p => p.Name))}.", nameof(name));
        Configure(Settings with { Enabled = true, Gains = [.. preset.Gains], Preset = preset.Name });
    }

    /// <summary>Sets one band (0-based); the gains are then custom. Throws for a band or gain out of range.</summary>
    public void SetBand(int band, double gainDb)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(band);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(band, EqualizerSettings.BandCount);
        if (!double.IsFinite(gainDb) || gainDb is < EqualizerSettings.MinGainDb or > EqualizerSettings.MaxGainDb)
        {
            throw new ArgumentOutOfRangeException(nameof(gainDb), gainDb, $"A band gain must be between {EqualizerSettings.MinGainDb} and {EqualizerSettings.MaxGainDb} dB.");
        }

        var current = Settings;
        var gains = current.BandGains.ToArray();
        gains[band] = gainDb;
        Configure(current with { Gains = gains, Preset = MatchingPreset(gains) });
    }

    /// <summary>Saves the current gains as user preset <paramref name="name"/> (replacing one of that name).</summary>
    public EqualizerPreset SavePreset(string name)
    {
        if (EqualizerSettings.CheckPresetName(name) is { } problem)
        {
            throw new ArgumentException(problem, nameof(name));
        }

        var preset = new EqualizerPreset(EqualizerSettings.NormalizeName(name), [.. Settings.BandGains], false);
        lock (_gate)
        {
            var at = _user.FindIndex(p => p.Name == preset.Name);
            if (at >= 0)
            {
                _user[at] = preset;
            }
            else if (_user.Count >= EqualizerSettings.MaxUserPresets)
            {
                throw new ArgumentException($"At most {EqualizerSettings.MaxUserPresets} presets can be saved; delete one first.", nameof(name));
            }
            else
            {
                _user.Add(preset);
            }
        }

        Configure(Settings with { Preset = preset.Name });
        return preset;
    }

    /// <summary>Deletes user preset <paramref name="name"/>; throws for a built-in or unknown one.</summary>
    public void DeletePreset(string name)
    {
        var n = EqualizerSettings.NormalizeName(name);
        if (EqualizerSettings.BuiltIn(n) is not null)
        {
            throw new ArgumentException($"'{n}' is a built-in preset and cannot be deleted.", nameof(name));
        }

        lock (_gate)
        {
            if (_user.RemoveAll(p => p.Name == n) == 0)
            {
                throw new ArgumentException($"There is no saved equalizer preset '{n}'.", nameof(name));
            }
        }

        var current = Settings;
        Configure(current.Preset == n ? current with { Preset = null } : current);
    }

    /// <summary>The headroom (dB) the current settings take at <paramref name="sampleRate"/>; for status.</summary>
    public double HeadroomDb(int sampleRate = 48_000)
    {
        Span<Biquad> filters = stackalloc Biquad[EqualizerSettings.BandCount];
        EqualizerDesign.Design(Settings, sampleRate, filters, out _, out var headroom);
        return headroom;
    }

    private string? MatchingPreset(double[] gains)
    {
        lock (_gate)
        {
            return EqualizerSettings.BuiltInPresets.Concat(_user).FirstOrDefault(p => p.Gains.SequenceEqual(gains))?.Name;
        }
    }
}
