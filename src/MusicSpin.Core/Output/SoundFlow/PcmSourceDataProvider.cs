using MusicSpin.Core.Audio;
using SoundFlow.Enums;
using SoundFlow.Interfaces;
using SoundFlow.Metadata.Models;

namespace MusicSpin.Core.Output.SoundFlow;

/// <summary>
/// Adapts an <see cref="IPcmSource"/> to SoundFlow's pull interface. Reports itself as a live stream
/// (Length = -1, not seekable) because MusicSpin's <c>Player</c> owns seeking and end-of-stream.
/// </summary>
internal sealed class PcmSourceDataProvider(IPcmSource source) : ISoundDataProvider
{
    private int _position;

    public int Position => _position;

    public int Length => -1;

    public bool CanSeek => false;

    public SampleFormat SampleFormat => SampleFormat.F32;

    public int SampleRate => source.Format.SampleRate;

    public bool IsDisposed { get; private set; }

    public SoundFormatInfo? FormatInfo => null;

    public event EventHandler<EventArgs>? EndOfStreamReached;

    public event EventHandler<PositionChangedEventArgs>? PositionChanged;

    public int ReadBytes(Span<float> buffer)
    {
        if (IsDisposed)
        {
            return 0;
        }

        var read = source.Read(buffer);
        if (read == 0)
        {
            // Live-stream semantics: SoundFlow fills the rest with silence; the Player stops the device.
            EndOfStreamReached?.Invoke(this, EventArgs.Empty);
            return 0;
        }

        _position = unchecked(_position + read);
        PositionChanged?.Invoke(this, new PositionChangedEventArgs(_position));
        return read;
    }

    public void Seek(int sampleOffset)
    {
        // Seeking is handled by the decoder behind IPcmSource.
    }

    public void Dispose() => IsDisposed = true;
}
