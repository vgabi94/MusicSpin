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
    /// <summary>Backend name, e.g. "SoundFlow/miniaudio (PulseAudio)".</summary>
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
}
