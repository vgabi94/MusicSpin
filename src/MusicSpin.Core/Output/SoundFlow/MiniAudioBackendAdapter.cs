using System.Diagnostics.CodeAnalysis;
using SoundFlow.Abstracts.Devices;
using SoundFlow.Backends.MiniAudio;
using SoundFlow.Backends.MiniAudio.Devices;
using SoundFlow.Backends.MiniAudio.Enums;
using SoundFlow.Components;
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

    /// <summary>Opens a device (null = default) at <paramref name="format"/> pulling from <paramref name="data"/>.</summary>
    IPlaybackStream Open(DeviceInfo? device, AudioFormat format, MiniAudioDeviceConfig config, ISoundDataProvider data);
}

/// <summary>An open playback device plus the player feeding it. Dispose stops and releases both.</summary>
internal interface IPlaybackStream : IDisposable
{
    void Start();

    void Pause();
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
        var stream = new Stream();
        try
        {
            stream.Device = _engine.InitializePlaybackDevice(device, format, config);
            stream.Player = new SoundPlayer(_engine, format, data);
            stream.Device.MasterMixer.AddComponent(stream.Player);
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
