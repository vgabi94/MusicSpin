using MusicSpin.Core.Audio;

namespace MusicSpin.Core.Playback;

/// <summary>
/// The <see cref="IPcmSource"/> handed to outputs: serialises decoder access between the audio thread
/// and control calls (seek), applies volume, and reports end of stream exactly once.
/// </summary>
internal sealed class PlaybackSource(IAudioDecoder decoder, Func<float> volume) : IPcmSource, IDisposable
{
    private readonly Lock _lock = new();
    private bool _endRaised;
    private bool _disposed;

    public event EventHandler? Ended;

    public PcmFormat Format => decoder.Format;

    public TimeSpan Position
    {
        get { lock (_lock) { return _disposed ? TimeSpan.Zero : decoder.Position; } }
    }

    public int Read(Span<float> buffer)
    {
        int read;
        lock (_lock)
        {
            if (_disposed)
            {
                return 0;
            }

            read = decoder.Read(buffer);
            if (read > 0)
            {
                _endRaised = false;
            }
        }

        if (read == 0)
        {
            if (!_endRaised)
            {
                _endRaised = true;
                Ended?.Invoke(this, EventArgs.Empty);
            }

            return 0;
        }

        var gain = volume();
        if (gain < 1f)
        {
            var samples = buffer[..read];
            for (var i = 0; i < samples.Length; i++)
            {
                samples[i] *= gain;
            }
        }

        return read;
    }

    public void Seek(TimeSpan position)
    {
        lock (_lock)
        {
            if (_disposed || !decoder.CanSeek)
            {
                return;
            }

            decoder.Seek(position);
            _endRaised = false;
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _disposed = true;
        }
    }
}
