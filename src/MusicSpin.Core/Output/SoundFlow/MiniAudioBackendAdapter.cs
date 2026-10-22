using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using MusicSpin.Core.Audio;
using MusicSpin.Core.Output.Wasapi;
using SoundFlow.Abstracts.Devices;
using SoundFlow.Backends.MiniAudio;
using SoundFlow.Backends.MiniAudio.Devices;
using SoundFlow.Backends.MiniAudio.Enums;
using SoundFlow.Components;
using SoundFlow.Enums;
using SoundFlow.Interfaces;
using SoundFlow.Structs;

namespace MusicSpin.Core.Output.SoundFlow;

/// <summary>The few miniaudio operations MusicSpin uses; a seam so device selection and output state can be tested.</summary>
internal interface IAudioBackend : IDisposable
{
    /// <summary>Name of the active OS backend (e.g. "PulseAudio", "Wasapi").</summary>
    string ActiveBackend { get; }

    /// <summary>Re-enumerates and returns the playback devices.</summary>
    IReadOnlyList<DeviceInfo> RefreshPlaybackDevices();

    /// <summary>
    /// Opens a device (null = default) at <paramref name="format"/> pulling from <paramref name="data"/>. The stream's
    /// <see cref="IPlaybackStream.Opened"/> says what the device really opened at, which can differ from what was asked.
    /// </summary>
    IPlaybackStream Open(DeviceInfo? device, AudioFormat format, MiniAudioDeviceConfig config, ISoundDataProvider data);
}

/// <summary>An open playback device plus the player feeding it. Dispose stops and releases both.</summary>
internal interface IPlaybackStream : IDisposable
{
    void Start();

    void Pause();

    /// <summary>What the device opened at: share mode, rate and sample format (read back where the backend can).</summary>
    OpenedOutput Opened { get; }
}

/// <summary>
/// Thin pass-through to SoundFlow's miniaudio engine. Excluded from coverage: every member needs a real audio
/// device (the bundled miniaudio has no Null backend), and all decisions live in the tested callers.
/// </summary>
[ExcludeFromCodeCoverage(Justification = "Pass-through to native miniaudio; needs a real audio device.")]
internal sealed class MiniAudioBackendAdapter(IReadOnlyList<MiniAudioBackend>? backends) : IAudioBackend
{
    private readonly MiniAudioEngine _engine = new(backends);

    public string ActiveBackend => _engine.ActiveBackend.ToString();

    public IReadOnlyList<DeviceInfo> RefreshPlaybackDevices()
    {
        _engine.UpdateAudioDevicesInfo();
        return _engine.PlaybackDevices;
    }

    public IPlaybackStream Open(DeviceInfo? device, AudioFormat format, MiniAudioDeviceConfig config, ISoundDataProvider data)
    {
        var name = device?.Name ?? DefaultDeviceName();
        var exclusive = config.Playback?.ShareMode == ShareMode.Exclusive;
        if (exclusive && OperatingSystem.IsWindows() && _engine.ActiveBackend == MiniAudioBackend.Wasapi)
        {
            // miniaudio opens exclusive WASAPI streams at the device's Default Format and resamples (#148): go direct.
            try
            {
                var endpointId = device is { } d && d.Id != nint.Zero ? Marshal.PtrToStringUni(d.Id) : null;
                return WasapiExclusiveStream.Open(endpointId, name, new PcmFormat(format.SampleRate, format.Channels, Bits(format.Format)), data);
            }
            catch (ExclusiveUnavailableException ex)
            {
                var shared = OpenMiniAudio(device, format, SoundFlowAudioOutput.BuildConfig(new AudioOutputOptions()), data, name, exclusive: false);
                shared.Opened = shared.Opened with
                {
                    Notice = $"Exclusive mode unavailable: {ex.Message}. Playing in shared mode through the Windows mixer.",
                };
                return shared;
            }
        }

        return OpenMiniAudio(device, format, config, data, name, exclusive);
    }

    private static int Bits(SampleFormat format) => format switch
    {
        SampleFormat.S16 => 16,
        SampleFormat.S24 => 24,
        _ => 32,
    };

    private string? DefaultDeviceName()
    {
        foreach (var d in _engine.PlaybackDevices)
        {
            if (d.IsDefault)
            {
                return d.Name;
            }
        }

        return null;
    }

    private Stream OpenMiniAudio(DeviceInfo? device, AudioFormat format, MiniAudioDeviceConfig config, ISoundDataProvider data, string? name, bool exclusive)
    {
        var stream = new Stream();
        try
        {
            stream.Device = _engine.InitializePlaybackDevice(device, format, config);
            stream.Player = new SoundPlayer(_engine, format, data);
            stream.Device.MasterMixer.AddComponent(stream.Player);
            // miniaudio does not expose the device's internal (opened) format: report what was asked, unverified.
            stream.Opened = new OpenedOutput(name, exclusive, format.SampleRate, format.Channels,
                format.Format.ToString().ToLowerInvariant(), Bits(format.Format), Verified: false);
            return stream;
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    public void Dispose() => _engine.Dispose();

    [ExcludeFromCodeCoverage(Justification = "Pass-through to native miniaudio; needs a real audio device.")]
    private sealed class Stream : IPlaybackStream
    {
        public AudioPlaybackDevice? Device { get; set; }

        public SoundPlayer? Player { get; set; }

        public OpenedOutput Opened { get; set; } = null!;

        public void Start()
        {
            if (!Device!.IsRunning)
            {
                Device.Start();
            }

            Player!.Play();
        }

        public void Pause() => Player?.Pause();

        public void Dispose()
        {
            if (Player is not null)
            {
                Player.Stop();
                Device?.MasterMixer.RemoveComponent(Player);
                Player.Dispose();
                Player = null;
            }

            if (Device is not null)
            {
                if (Device.IsRunning)
                {
                    Device.Stop();
                }

                Device.Dispose();
                Device = null;
            }
        }
    }
}
