using MusicSpin.Core.Audio;
using SoundFlow.Abstracts.Devices;
using SoundFlow.Backends.MiniAudio.Devices;
using SoundFlow.Backends.MiniAudio.Enums;
using SoundFlow.Components;
using SoundFlow.Enums;
using SoundFlow.Structs;

namespace MusicSpin.Core.Output.SoundFlow;

/// <summary>
/// Opens a miniaudio playback device at the source's native sample rate/channels and a device sample
/// format matching the source bit depth (S16/S24/S32), so with exclusive mode and volume 1.0 the
/// original samples reach the DAC unchanged.
/// </summary>
internal sealed class SoundFlowAudioOutput(SoundFlowOutputProvider provider, AudioOutputOptions options) : IAudioOutput
{
    private readonly Lock _lock = new();
    private AudioPlaybackDevice? _device;
    private SoundPlayer? _player;

    public bool IsOpen
    {
        get { lock (_lock) { return _device is not null; } }
    }

    public void Open(IPcmSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        Stop();

        lock (_lock)
        {
            try
            {
                var engine = provider.Engine;
                var format = new AudioFormat
                {
                    SampleRate = source.Format.SampleRate,
                    Channels = source.Format.Channels,
                    Format = DeviceSampleFormat(source.Format.SourceBitsPerSample),
                    Layout = AudioFormat.GetLayoutFromChannels(source.Format.Channels),
                };

                _device = engine.InitializePlaybackDevice(provider.ResolveDevice(options.DeviceId), format, BuildConfig(options));
                _player = new SoundPlayer(engine, format, new PcmSourceDataProvider(source));
                _device.MasterMixer.AddComponent(_player);
            }
            catch (Exception ex)
            {
                DisposeDevice();
                throw ex as MusicSpinException
                      ?? new MusicSpinException(MusicSpinErrorCode.AudioDeviceError, $"Could not open audio device: {ex.Message}", ex);
            }
        }
    }

    public void Start()
    {
        lock (_lock)
        {
            if (_device is null || _player is null)
            {
                throw new MusicSpinException(MusicSpinErrorCode.InvalidState, "Output is not open.");
            }

            if (!_device.IsRunning)
            {
                _device.Start();
            }

            _player.Play();
        }
    }

    public void Pause()
    {
        lock (_lock)
        {
            _player?.Pause();
        }
    }

    public void Stop()
    {
        lock (_lock)
        {
            DisposeDevice();
        }
    }

    public void Dispose() => Stop();

    internal static SampleFormat DeviceSampleFormat(int sourceBits) => sourceBits switch
    {
        > 0 and <= 16 when sourceBits != 1 => SampleFormat.S16,
        > 16 and <= 24 => SampleFormat.S24,
        // 32-bit int sources, float sources and DSD (decoded to float PCM) go out as float.
        _ => SampleFormat.F32,
    };

    private static MiniAudioDeviceConfig BuildConfig(AudioOutputOptions options)
    {
        var exclusive = options.Exclusive;
        return new MiniAudioDeviceConfig
        {
            Playback = new DeviceSubConfig { ShareMode = exclusive ? ShareMode.Exclusive : ShareMode.Shared },
            // Bit-perfect intent: never let the OS/miniaudio silently resample or clip.
            NoClip = true,
            Wasapi = new WasapiSettings { NoAutoConvertSRC = exclusive, NoDefaultQualitySRC = exclusive },
            CoreAudio = new CoreAudioSettings { AllowNominalSampleRateChange = exclusive },
            Alsa = exclusive ? new AlsaSettings { NoAutoFormat = true, NoAutoResample = true, NoAutoChannels = true } : null,
        };
    }

    private void DisposeDevice()
    {
        if (_player is not null)
        {
            _player.Stop();
            _device?.MasterMixer.RemoveComponent(_player);
            _player.Dispose();
            _player = null;
        }

        if (_device is not null)
        {
            if (_device.IsRunning)
            {
                _device.Stop();
            }

            _device.Dispose();
            _device = null;
        }
    }
}
