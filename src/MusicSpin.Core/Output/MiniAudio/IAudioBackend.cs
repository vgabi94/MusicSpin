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

    /// <summary>
    /// Stops reading the source and returns once what was already handed to the device has played out, so a later
    /// <see cref="Start"/> goes on from exactly where the listener stopped hearing. The device itself stays open (an
    /// exclusive one stays held: the owner disposes it to let other apps in).
    /// <para>
    /// Returns true when the stream is known to read its source no more. False means that could not be confirmed (a
    /// device thread stuck in its driver): a caller that is about to give the stream another source must then dispose
    /// the stream instead of switching it.
    /// </para>
    /// </summary>
    bool Pause();

    /// <summary>What the device opened at, read back from the backend.</summary>
    OpenedOutput Opened { get; }

    /// <summary>
    /// The device stopped on its own (unplugged, invalidated); the argument says why. Raised on the audio thread or a
    /// notification thread, which the stream's <see cref="IDisposable.Dispose"/> waits for: handlers must never block on
    /// anything that is held while the stream is disposed.
    /// </summary>
    event EventHandler<string>? Lost;

    /// <summary>
    /// The backend moved this stream to the new system default device by itself (miniaudio reroutes default devices);
    /// <see cref="Opened"/> now describes the new device. Raised on a backend thread.
    /// </summary>
    event EventHandler? Rerouted
    {
        add { }
        remove { }
    }

    /// <summary>
    /// The system default device changed while this stream, opened on the default, stays on the old one (the WASAPI
    /// exclusive path cannot follow by itself): the owner reopens on the new default. Raised on a backend thread.
    /// </summary>
    event EventHandler? DefaultDeviceChanged
    {
        add { }
        remove { }
    }
}
