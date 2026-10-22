using MusicSpin.Core.Audio;

namespace MusicSpin.Core.Output.MiniAudio;

/// <summary>A playback device as the backend lists it.</summary>
/// <param name="Id">The backend's opaque device id (miniaudio's ma_device_id bytes).</param>
/// <param name="Formats">The formats the device reports natively (sample format name, channels, rate); may be empty.</param>
/// <param name="EndpointId">The WASAPI endpoint id on Windows (for the exclusive path); null elsewhere.</param>
internal sealed record BackendDevice(string Name, bool IsDefault, byte[] Id, IReadOnlyList<(string Format, int Channels, int SampleRate)> Formats, string? EndpointId = null);

/// <summary>What to open: the format our callback writes, at the track's own rate and channel count.</summary>
/// <param name="Format">The sample format we hand the device (f32 in shared mode, the source's depth in exclusive).</param>
/// <param name="SourceBits">The track's bit depth (the WASAPI exclusive path picks its container from it).</param>
internal sealed record OpenRequest(DeviceSample Format, int SampleRate, int Channels, bool Exclusive, int SourceBits);

/// <summary>The few device operations MusicSpin needs; a seam so device choice and reporting are tested without hardware.</summary>
internal interface IAudioBackend : IDisposable
{
    /// <summary>Name of the active OS backend (e.g. "PulseAudio", "WASAPI", "Null").</summary>
    string ActiveBackend { get; }

    IReadOnlyList<BackendDevice> RefreshPlaybackDevices();

    /// <summary>
    /// Opens <paramref name="device"/> (null = default) for <paramref name="request"/>, pulling from <paramref name="source"/>.
    /// The stream's <see cref="IPlaybackStream.Opened"/> is what the device really opened at, which can differ.
    /// </summary>
    IPlaybackStream Open(BackendDevice? device, OpenRequest request, IPcmSource source);
}

/// <summary>An open playback device. Dispose stops and releases it.</summary>
internal interface IPlaybackStream : IDisposable
{
    void Start();

    /// <summary>Keeps the device open, rendering silence without reading the source.</summary>
    void Pause();

    /// <summary>What the device opened at, read back from the backend.</summary>
    OpenedOutput Opened { get; }

    /// <summary>The device stopped on its own (unplugged, invalidated); the argument says why. Raised on the audio thread.</summary>
    event EventHandler<string>? Lost;
}
