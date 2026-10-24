using MusicSpin.Core.Metadata;

namespace MusicSpin.Core.Audio;

/// <summary>
/// A decoder read through an <see cref="IPcmConverter"/>: it reports the converter's output rate and channels (and the
/// track's own bit depth), so a track of another rate can be played by an output that stays open at one format.
/// Seeking seeks the inner decoder and resets the converter. Owns both.
/// </summary>
public sealed class ConvertingDecoder : IAudioDecoder
{
    private const int ChunkFrames = 4096;

    private readonly IAudioDecoder _inner;
    private readonly IPcmConverter _converter;
    private readonly float[] _staged;
    private readonly int _inChannels;
    private readonly int _outChannels;
    private int _stagedStart;
    private int _stagedEnd;
    private bool _innerEnded;
    private bool _disposed;

    /// <exception cref="ArgumentException">The converter does not take the decoder's rate and channels.</exception>
    public ConvertingDecoder(IAudioDecoder inner, IPcmConverter converter)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(converter);
        var from = inner.Format;
        if (converter.From.SampleRate != from.SampleRate || converter.From.Channels != from.Channels
            || from.Channels <= 0 || converter.To.Channels <= 0 || converter.To.SampleRate <= 0)
        {
            throw new ArgumentException($"Converter takes {converter.From.SampleRate} Hz/{converter.From.Channels} ch, the track is {from.SampleRate} Hz/{from.Channels} ch.", nameof(converter));
        }

        _inner = inner;
        _converter = converter;
        _inChannels = from.Channels;
        _outChannels = converter.To.Channels;
        _staged = new float[ChunkFrames * _inChannels];
        Format = new PcmFormat(converter.To.SampleRate, converter.To.Channels, from.SourceBitsPerSample);
    }

    public PcmFormat Format { get; }

    public TrackInfo Track => _inner.Track;

    /// <summary>The inner decoder's position, less what it decoded that has not been converted yet.</summary>
    public TimeSpan Position
    {
        get
        {
            var position = _inner.Position - _inner.Format.SamplesToTime(_stagedEnd - _stagedStart);
            return position < TimeSpan.Zero ? TimeSpan.Zero : position;
        }
    }

    public bool CanSeek => _inner.CanSeek;

    public void Seek(TimeSpan position)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _inner.Seek(position);
        _stagedStart = _stagedEnd = 0;
        _innerEnded = false;
        _converter.Reset();
    }

    public int Read(Span<float> buffer)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var room = buffer.Length - (buffer.Length % _outChannels);
        var written = 0;
        while (written < room)
        {
            if (_stagedStart == _stagedEnd)
            {
                if (_innerEnded)
                {
                    break;
                }

                var read = _inner.Read(_staged);
                read -= read % _inChannels;
                _stagedStart = 0;
                _stagedEnd = Math.Max(read, 0);
                if (read <= 0)
                {
                    _innerEnded = true; // the resampler's last few filtered frames are dropped: a fraction of a millisecond
                    break;
                }

                Sanitize(_staged.AsSpan(0, read));
            }

            var produced = _converter.Convert(_staged.AsSpan(_stagedStart, _stagedEnd - _stagedStart), buffer[written..room], out var consumed);
            if (produced < 0 || consumed < 0 || consumed > _stagedEnd - _stagedStart || produced > room - written)
            {
                throw new InvalidOperationException($"Converter returned {produced} samples for {consumed} consumed.");
            }

            if (produced == 0 && consumed == 0)
            {
                throw new InvalidOperationException("Converter made no progress.");
            }

            _stagedStart += consumed;
            written += produced;
        }

        return written;
    }

    /// <summary>
    /// A broken decoder's NaN or infinity becomes silence: the resampler's filters would otherwise carry it into every
    /// later sample of the track, where a passthrough track loses only that one sample.
    /// </summary>
    private static void Sanitize(Span<float> samples)
    {
        foreach (ref var x in samples)
        {
            if (!float.IsFinite(x))
            {
                x = 0;
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _converter.Dispose();
        _inner.Dispose();
    }
}
