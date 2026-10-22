namespace MusicSpin.Core.Audio;

/// <summary>Options for opening an output device.</summary>
/// <param name="DeviceId">Device id from <see cref="IAudioOutputProvider.GetDevices"/>; null = system default.</param>
/// <param name="Exclusive">
/// Request exclusive access (WASAPI exclusive / ALSA hw / CoreAudio hog) so samples reach the DAC without the OS mixer.
/// </param>
public sealed record AudioOutputOptions(string? DeviceId = null, bool Exclusive = false);

/// <summary>A playback device and the native formats it reports (empty lists = unknown/any).</summary>
/// <param name="Id">Stable within one enumeration: index as string ("0", "1", ...).</param>
/// <param name="SampleFormats">Native sample formats, e.g. "s16", "s24", "s32", "f32".</param>
/// <param name="SampleRates">Native sample rates in Hz, ascending.</param>
/// <param name="Channels">Supported channel counts; empty when the device accepts any.</param>
public sealed record AudioDeviceInfo(
    string Id,
    string Name,
    bool IsDefault,
    string Backend,
    IReadOnlyList<string> SampleFormats,
    IReadOnlyList<int> SampleRates,
    IReadOnlyList<int> Channels);

/// <summary>Enumerates devices and creates outputs. One implementation per audio backend.</summary>
public interface IAudioOutputProvider
{
    /// <summary>Backend name, e.g. "miniaudio (PulseAudio)".</summary>
    string Name { get; }

    /// <exception cref="MusicSpinException">AudioDeviceError if the backend cannot be initialised.</exception>
    IReadOnlyList<AudioDeviceInfo> GetDevices();

    /// <summary>Creates an (unopened) output.</summary>
    IAudioOutput Create(AudioOutputOptions options);
}

/// <summary>
/// A device output that pulls PCM from an <see cref="IPcmSource"/> on its own audio thread.
/// </summary>
public interface IAudioOutput : IDisposable
{
    /// <summary>Opens the device for <paramref name="source"/>'s format. Re-opening replaces the previous source.</summary>
    /// <exception cref="MusicSpinException">AudioDeviceError.</exception>
    void Open(IPcmSource source);

    void Start();

    void Pause();

    /// <summary>Stops and releases the device.</summary>
    void Stop();

    bool IsOpen { get; }

    /// <summary>
    /// What the device actually opened at (share mode, rate, sample format), read back after <see cref="Open"/>;
    /// null while closed or when the output cannot tell.
    /// </summary>
    OpenedOutput? Opened => null;

    /// <summary>
    /// The device failed while open (unplugged, invalidated); the output has closed itself. Raised on a pool thread.
    /// </summary>
    event EventHandler<AudioOutputFailedEventArgs>? Failed
    {
        add { }
        remove { }
    }

    /// <summary>
    /// The output moved to another device without stopping (the system default changed, or the device was lost and it
    /// reopened on the default): the source keeps its position. Raised on a pool thread.
    /// </summary>
    event EventHandler<AudioOutputReroutedEventArgs>? Rerouted
    {
        add { }
        remove { }
    }
}

/// <summary>Where an output moved to, and what to tell the user about it (null when the move needs no word).</summary>
public sealed class AudioOutputReroutedEventArgs(OpenedOutput opened, string? message) : EventArgs
{
    public OpenedOutput Opened { get; } = opened;

    /// <summary>E.g. the chosen device was disconnected, or exclusive mode was refused on the new device.</summary>
    public string? Message { get; } = message;
}

/// <summary>Why an open output stopped on its own.</summary>
public sealed class AudioOutputFailedEventArgs(string reason) : EventArgs
{
    public string Reason { get; } = reason;
}

/// <summary>The format a device really opened at, as read back from the backend after opening it.</summary>
/// <param name="Device">The device's name; null when unknown.</param>
/// <param name="Exclusive">True only when the device is held exclusively (no OS mixer in between).</param>
/// <param name="SampleRate">The device's rate in Hz.</param>
/// <param name="Channels">The device's channel count.</param>
/// <param name="SampleFormat">"s16", "s24" (packed), "s24in32" (24 valid bits in a 32-bit container), "s32" or "f32".</param>
/// <param name="BitsPerSample">Valid bits per sample (24 for s24in32).</param>
/// <param name="Verified">
/// True when the values were read back from the device (miniaudio's internal device format, WASAPI exclusive);
/// false when they are what was asked for and the output cannot tell what it opened.
/// </param>
/// <param name="Notice">Why the output is not what was asked for (e.g. exclusive refused, a resampled rate); null when it is.</param>
public sealed record OpenedOutput(
    string? Device,
    bool Exclusive,
    int SampleRate,
    int Channels,
    string SampleFormat,
    int BitsPerSample,
    bool Verified,
    string? Notice = null)
{
    /// <summary>"exclusive, 44.1 kHz 24-bit" / "shared, 48 kHz 32-bit float".</summary>
    public string Describe()
        => string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"{(Exclusive ? "exclusive" : "shared")}, {SampleRate / 1000.0:0.###} kHz {BitsPerSample}-bit{(SampleFormat == "f32" ? " float" : string.Empty)}");
}
