using System.Globalization;
using MusicSpin.Core.Audio;

namespace MusicSpin.Core.Output.MiniAudio;

/// <summary>
/// Opens a playback device at the source's own sample rate and channels. Shared mode hands the OS mixer float32;
/// exclusive mode hands the device the source's own depth (s16/s24, f32 for 32-bit and float sources), so with
/// volume 1.0 the original samples reach the DAC unchanged. When the device goes away mid-play, <see cref="Failed"/>
/// fires (on a pool thread) and the output closes.
/// </summary>
internal sealed class MiniAudioOutput(MiniAudioOutputProvider provider, AudioOutputOptions options) : IAudioOutput
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
                var stream = provider.Engine.Open(provider.ResolveDevice(options.DeviceId), BuildRequest(source.Format, options), source);
                _stream = stream;
                stream.Lost += (_, reason) => OnLost(stream, reason);
                _opened = Review(stream.Opened, source.Format, options);
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

    /// <inheritdoc />
    public event EventHandler<AudioOutputFailedEventArgs>? Failed;

    /// <summary>The open device stream, for tests.</summary>
    internal IPlaybackStream? Stream
    {
        get { lock (_lock) { return _stream; } }
    }

    /// <summary>The sample format we write: float32 in shared mode (the OS mixer is float), the source's depth in exclusive.</summary>
    internal static DeviceSample DeviceSampleFormat(int sourceBits, bool exclusive) => !exclusive ? DeviceSample.F32 : sourceBits switch
    {
        > 0 and <= 16 when sourceBits != 1 => DeviceSample.S16,
        > 16 and <= 24 => DeviceSample.S24,
        // 32-bit int sources, float sources and DSD (decoded to float PCM) go out as float.
        _ => DeviceSample.F32,
    };

    internal static OpenRequest BuildRequest(PcmFormat format, AudioOutputOptions options)
        => new(DeviceSampleFormat(format.SourceBitsPerSample, options.Exclusive), format.SampleRate, format.Channels, options.Exclusive, format.SourceBitsPerSample);

    private void OnLost(IPlaybackStream stream, string reason)
    {
        // Raised on the audio thread, which cannot close its own device (it would wait for itself): hand off.
        ThreadPool.QueueUserWorkItem(_ =>
        {
            lock (_lock)
            {
                if (!ReferenceEquals(_stream, stream))
                {
                    return; // already closed or re-opened
                }

                DisposeDevice();
            }

            Failed?.Invoke(this, new AudioOutputFailedEventArgs(reason));
        });
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
