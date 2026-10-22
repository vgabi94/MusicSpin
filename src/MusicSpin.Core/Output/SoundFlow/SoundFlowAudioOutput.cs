using System.Globalization;
using MusicSpin.Core.Audio;
using SoundFlow.Backends.MiniAudio.Devices;
using SoundFlow.Backends.MiniAudio.Enums;
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
    private IPlaybackStream? _stream;
    private OpenedOutput? _opened;

    public bool IsOpen
    {
        get { lock (_lock) { return _stream is not null; } }
    }

    /// <inheritdoc />
    public OpenedOutput? Opened
    {
        get { lock (_lock) { return _opened; } }
    }

    public void Open(IPcmSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        Stop();

        lock (_lock)
        {
            try
            {
                var format = new AudioFormat
                {
                    SampleRate = source.Format.SampleRate,
                    Channels = source.Format.Channels,
                    Format = DeviceSampleFormat(source.Format.SourceBitsPerSample),
                    Layout = AudioFormat.GetLayoutFromChannels(source.Format.Channels),
                };

                _stream = provider.Engine.Open(
                    provider.ResolveDevice(options.DeviceId), format, BuildConfig(options), new PcmSourceDataProvider(source));
                _opened = Review(_stream.Opened, source.Format, options);
            }
            catch (Exception ex)
            {
                DisposeDevice();
                throw ex as MusicSpinException
                      ?? new MusicSpinException(
                          MusicSpinErrorCode.AudioDeviceError,
                          $"Could not open audio device{(options.Exclusive ? " in exclusive mode" : string.Empty)}: {ex.Message}", ex);
            }
        }
    }

    public void Start()
    {
        lock (_lock)
        {
            if (_stream is null)
            {
                throw new MusicSpinException(MusicSpinErrorCode.InvalidState, "Output is not open.");
            }

            _stream.Start();
        }
    }

    public void Pause()
    {
        lock (_lock)
        {
            _stream?.Pause();
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

    internal static MiniAudioDeviceConfig BuildConfig(AudioOutputOptions options)
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

    /// <summary>
    /// Checks what the device opened at against what was asked: exclusive asked but shared given, or a verified rate
    /// that is not the track's, become a notice, so nothing claims exclusive or bit-perfect when it is not.
    /// </summary>
    internal static OpenedOutput Review(OpenedOutput opened, PcmFormat source, AudioOutputOptions options)
    {
        var notices = new List<string>();
        if (opened.Notice is { } notice)
        {
            notices.Add(notice);
        }
        else if (options.Exclusive && !opened.Exclusive)
        {
            notices.Add("Exclusive mode was requested, but the device opened in shared mode.");
        }

        if (opened.Verified && opened.SampleRate != source.SampleRate)
        {
            notices.Add(string.Create(CultureInfo.InvariantCulture,
                $"The device runs at {opened.SampleRate / 1000.0:0.###} kHz, not the track's {source.SampleRate / 1000.0:0.###} kHz: the audio is resampled."));
        }

        return opened with { Notice = notices.Count == 0 ? null : string.Join(" ", notices) };
    }

    private void DisposeDevice()
    {
        _stream?.Dispose();
        _stream = null;
        _opened = null;
    }
}
