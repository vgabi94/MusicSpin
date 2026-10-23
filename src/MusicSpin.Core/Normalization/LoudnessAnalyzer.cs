using MusicSpin.Core.Audio;

namespace MusicSpin.Core.Normalization;

/// <summary>Measures a whole track: decodes it and runs a <see cref="LoudnessMeter"/>.</summary>
public static class LoudnessAnalyzer
{
    /// <summary>Samples read per call (about 0.1 s of stereo at 44.1 kHz): short enough to notice a cancellation quickly.</summary>
    private const int ChunkSamples = 8192;

    /// <exception cref="OperationCanceledException">Cancelled.</exception>
    /// <exception cref="MusicSpinException">The file cannot be opened or decoded.</exception>
    public static LoudnessResult Analyze(IPcmSource source, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        var format = source.Format;
        var meter = new LoudnessMeter(format.SampleRate, format.Channels);
        var chunk = new float[ChunkSamples - ChunkSamples % Math.Max(format.Channels, 1)];
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = source.Read(chunk);
            if (read <= 0)
            {
                break;
            }

            meter.Add(chunk.AsSpan(0, read - read % format.Channels));
            Thread.Yield(); // a low-priority worker: let playback's threads go first
        }

        return meter.Finish();
    }

    public static LoudnessResult Analyze(IAudioDecoderFactory decoders, string path, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(decoders);
        using var decoder = decoders.Open(path);
        return Analyze(decoder, cancellationToken);
    }
}
