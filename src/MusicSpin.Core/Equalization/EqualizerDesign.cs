namespace MusicSpin.Core.Equalization;

/// <summary>One biquad's coefficients, normalised so a0 = 1.</summary>
internal readonly record struct Biquad(double B0, double B1, double B2, double A1, double A2)
{
    /// <summary>
    /// RBJ "Audio EQ Cookbook" peaking filter: <paramref name="gainDb"/> at <paramref name="centreHz"/>, unity far from it.
    /// <paramref name="q"/> = √2 is one octave wide, so neighbouring bands of the 10-band EQ meet about half way.
    /// </summary>
    public static Biquad Peaking(double centreHz, double gainDb, double q, int sampleRate)
    {
        var a = Math.Pow(10, gainDb / 40);
        var w0 = 2 * Math.PI * centreHz / sampleRate;
        var cos = Math.Cos(w0);
        var alpha = Math.Sin(w0) / (2 * q);
        var a0 = 1 + (alpha / a);
        return new Biquad((1 + (alpha * a)) / a0, -2 * cos / a0, (1 - (alpha * a)) / a0, -2 * cos / a0, (1 - (alpha / a)) / a0);
    }

    /// <summary>|H(e^jω)| at <paramref name="hz"/>.</summary>
    public double Magnitude(double hz, int sampleRate)
    {
        var w = 2 * Math.PI * hz / sampleRate;
        var (c1, s1, c2, s2) = (Math.Cos(w), Math.Sin(w), Math.Cos(2 * w), Math.Sin(2 * w));
        var nr = B0 + (B1 * c1) + (B2 * c2);
        var ni = -(B1 * s1) - (B2 * s2);
        var dr = 1 + (A1 * c1) + (A2 * c2);
        var di = -(A1 * s1) - (A2 * s2);
        return Math.Sqrt(((nr * nr) + (ni * ni)) / ((dr * dr) + (di * di)));
    }
}

/// <summary>
/// The filters for one <see cref="EqualizerSettings"/> at one sample rate: a peaking biquad per band that is not at
/// 0 dB (and below 0.45 × the rate: a band at or past Nyquist cannot be built and is left out), and one linear gain for
/// the pre-amp plus headroom.
/// <para>
/// Headroom (ear and clip safety): the pre-amp is lowered by however much the whole curve, pre-amp included, would rise
/// above 0 dB at its highest point, so a full-scale tone at the most boosted frequency still leaves at full scale and a
/// boost never makes anything louder than it was. That also keeps volume levelling's (#139) peak guarantee: a levelled
/// track that peaked below full scale still does. Cuts with a positive pre-amp can bring the level back up to, never past, 0 dB.
/// </para>
/// Pure and allocation-free when filling a caller's span, so the audio thread can design without allocating.
/// </summary>
internal static class EqualizerDesign
{
    public const double Q = 1.4142135623730951;

    /// <summary>Highest centre a band may have, as a fraction of the sample rate.</summary>
    public const double MaxCentreFraction = 0.45;

    /// <summary>
    /// Fills <paramref name="filters"/> (at least <see cref="EqualizerSettings.BandCount"/> long) and returns how many it
    /// used; <paramref name="gain"/> is the linear pre-amp after headroom and <paramref name="headroomDb"/> how much it was lowered.
    /// </summary>
    public static int Design(EqualizerSettings settings, int sampleRate, Span<Biquad> filters, out double gain, out double headroomDb)
    {
        var count = 0;
        var gains = settings.BandGains;
        for (var i = 0; i < EqualizerSettings.BandCount; i++)
        {
            var g = gains[i];
            var centre = EqualizerSettings.Bands[i];
            if (g != 0 && double.IsFinite(g) && sampleRate > 0 && centre < MaxCentreFraction * sampleRate)
            {
                filters[count++] = Biquad.Peaking(centre, Math.Clamp(g, EqualizerSettings.MinGainDb, EqualizerSettings.MaxGainDb), Q, sampleRate);
            }
        }

        var preamp = double.IsFinite(settings.PreampDb) ? Math.Clamp(settings.PreampDb, EqualizerSettings.MinPreampDb, EqualizerSettings.MaxPreampDb) : 0;
        var peak = PeakDb(filters[..count], sampleRate) + preamp;
        headroomDb = Math.Max(0, peak);
        gain = Math.Pow(10, (preamp - headroomDb) / 20);
        return count;
    }

    /// <summary>The highest point of the cascade's response in dB (0 for no filters), over the audible band up to 0.49 × the rate.</summary>
    public static double PeakDb(ReadOnlySpan<Biquad> filters, int sampleRate)
    {
        if (filters.IsEmpty || sampleRate <= 0)
        {
            return 0;
        }

        var top = Math.Min(22_000, 0.49 * sampleRate);
        var peak = double.NegativeInfinity;
        const int points = 384;
        for (var p = 0; p <= points + EqualizerSettings.BandCount; p++)
        {
            // A log grid from 15 Hz, then every band centre exactly (where a peaking filter's maximum is).
            var hz = p <= points ? 15 * Math.Pow(top / 15, (double)p / points) : EqualizerSettings.Bands[p - points - 1];
            if (hz > top)
            {
                continue;
            }

            var magnitude = 1.0;
            foreach (var f in filters)
            {
                magnitude *= f.Magnitude(hz, sampleRate);
            }

            peak = Math.Max(peak, 20 * Math.Log10(magnitude));
        }

        return double.IsFinite(peak) ? peak : 0;
    }

    /// <summary>The cascade's (and gain's) response at <paramref name="hz"/> in dB.</summary>
    public static double ResponseDb(ReadOnlySpan<Biquad> filters, double gain, double hz, int sampleRate)
    {
        var magnitude = gain;
        foreach (var f in filters)
        {
            magnitude *= f.Magnitude(hz, sampleRate);
        }

        return 20 * Math.Log10(magnitude);
    }
}
