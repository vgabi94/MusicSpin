using System.Globalization;
using MusicSpin.Core.Audio;
using SoundFlow.Backends.MiniAudio;
using SoundFlow.Backends.MiniAudio.Enums;
using SoundFlow.Structs;

namespace MusicSpin.Core.Output.SoundFlow;

/// <summary>
/// Audio output through SoundFlow (MIT) on top of miniaudio: WASAPI on Windows, CoreAudio on macOS,
/// PulseAudio/ALSA/JACK on Linux. One miniaudio context is shared by all outputs of this provider.
/// </summary>
public sealed class SoundFlowOutputProvider : IAudioOutputProvider, IDisposable
{
    private readonly IReadOnlyList<MiniAudioBackend>? _backends;
    private readonly Lock _lock = new();
    private MiniAudioEngine? _engine;

    /// <param name="backends">Optional backend priority (e.g. [Alsa] for direct hardware access on Linux).</param>
    public SoundFlowOutputProvider(IReadOnlyList<MiniAudioBackend>? backends = null)
    {
        _backends = backends;
    }

    public string Name
    {
        get
        {
            try
            {
                return $"SoundFlow/miniaudio ({Engine.ActiveBackend})";
            }
            catch (MusicSpinException)
            {
                return "SoundFlow/miniaudio (unavailable)";
            }
        }
    }

    internal MiniAudioEngine Engine
    {
        get
        {
            lock (_lock)
            {
                if (_engine is not null)
                {
                    return _engine;
                }

                try
                {
                    _engine = new MiniAudioEngine(_backends);
                    return _engine;
                }
                catch (Exception ex) when (ex is not MusicSpinException)
                {
                    throw new MusicSpinException(
                        MusicSpinErrorCode.AudioDeviceError, $"Could not initialise audio backend: {ex.Message}", ex);
                }
            }
        }
    }

    public IReadOnlyList<AudioDeviceInfo> GetDevices()
    {
        var engine = Engine;
        try
        {
            engine.UpdateAudioDevicesInfo();
        }
        catch (Exception ex)
        {
            throw new MusicSpinException(MusicSpinErrorCode.AudioDeviceError, $"Could not list devices: {ex.Message}", ex);
        }

        var backend = engine.ActiveBackend.ToString();
        return engine.PlaybackDevices
            .Select((d, i) =>
            {
                var formats = d.SupportedDataFormats ?? [];
                return new AudioDeviceInfo(
                    i.ToString(CultureInfo.InvariantCulture),
                    d.Name,
                    d.IsDefault,
                    backend,
                    formats.Select(f => f.Format.ToString().ToLowerInvariant()).Distinct().ToArray(),
                    formats.Select(f => (int)f.SampleRate).Where(r => r > 0).Distinct().Order().ToArray(),
                    formats.Select(f => (int)f.Channels).Where(c => c > 0).Distinct().Order().ToArray());
            })
            .ToArray();
    }

    public IAudioOutput Create(AudioOutputOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return new SoundFlowAudioOutput(this, options);
    }

    /// <summary>Resolves a device by index id ("0", "1", ...) or case-insensitive name substring.</summary>
    internal DeviceInfo? ResolveDevice(string? deviceId)
    {
        if (string.IsNullOrWhiteSpace(deviceId))
        {
            return null;
        }

        var engine = Engine;
        engine.UpdateAudioDevicesInfo();
        var devices = engine.PlaybackDevices;
        if (int.TryParse(deviceId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var index)
            && index >= 0 && index < devices.Length)
        {
            return devices[index];
        }

        foreach (var device in devices)
        {
            if (device.Name.Contains(deviceId, StringComparison.OrdinalIgnoreCase))
            {
                return device;
            }
        }

        throw new MusicSpinException(MusicSpinErrorCode.AudioDeviceError, $"Audio device '{deviceId}' not found.");
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _engine?.Dispose();
            _engine = null;
        }
    }
}
