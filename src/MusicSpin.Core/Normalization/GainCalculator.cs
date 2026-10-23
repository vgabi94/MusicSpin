namespace MusicSpin.Core.Normalization;

/// <summary>What is known about a track's loudness: its tags and, for untagged files, the analysis.</summary>
/// <param name="Tags">ReplayGain / R128 tags read at scan time.</param>
/// <param name="Lufs">Analysed integrated loudness; null when not analysed or silent.</param>
/// <param name="TruePeak">Analysed true peak (linear); null when not analysed.</param>
/// <param name="Analyzed">The analysis ran (and <paramref name="Lufs"/> is null when the track is silent).</param>
public sealed record TrackLoudness(ReplayGainTags Tags, double? Lufs = null, double? TruePeak = null, bool Analyzed = false)
{
    /// <summary>
    /// Nothing more to measure: the analysis ran, or the tags give a track gain <em>and</em> its peak. A tagged track without
    /// a peak (Opus R128 tags, a gain-only MP3) is measured too: without a peak its gain could never lift it above 0 dB.
    /// </summary>
    public bool IsKnown => Analyzed || (ReplayGainTags.ValidGainOrNull(Tags.TrackGainDb) is not null && ReplayGainTags.ValidPeakOrNull(Tags.TrackPeak) is not null);
}

/// <summary>Where a track's gain came from.</summary>
public enum GainSource
{
    /// <summary>Levelling is off.</summary>
    Off,

    /// <summary>The gain is not known (yet): the track plays unchanged.</summary>
    Unknown,

    /// <summary>The track's ReplayGain / R128 tag.</summary>
    TrackTag,

    /// <summary>The album's ReplayGain / R128 tag.</summary>
    AlbumTag,

    /// <summary>EBU R128 analysis of the file.</summary>
    Analysis,
}

/// <summary>A track's gain.</summary>
/// <param name="Linear">The factor the samples are multiplied by (exactly 1 when the gain is below 0.005 dB).</param>
/// <param name="Db">The gain in dB (after clipping prevention).</param>
/// <param name="Limited">The gain was lowered so that the track's peak stays at or below full scale.</param>
public readonly record struct NormalizationGain(double Linear, double Db, GainSource Source, bool Limited)
{
    public static NormalizationGain Unity(GainSource source) => new(1.0, 0.0, source, false);

    public string SourceName => Source switch
    {
        GainSource.Off => "off",
        GainSource.TrackTag => "track-tag",
        GainSource.AlbumTag => "album-tag",
        GainSource.Analysis => "analysis",
        _ => "unknown",
    };
}

/// <summary>Turns tags, analysis and settings into a gain.</summary>
public static class GainCalculator
{
    /// <summary>The ReplayGain 2.0 reference the tag gains are relative to.</summary>
    public const double ReplayGainReferenceLufs = -18;

    /// <summary>
    /// No track is boosted more than this, preamp included. A very quiet recording stays quiet rather than being lifted
    /// by +20 dB (that is hard on the ears, and the noise comes with it); a cut can be much larger than a boost.
    /// </summary>
    public const double MaxBoostDb = 6;

    /// <summary>No track is cut more than this.</summary>
    public const double MaxCutDb = -40;

    private const double UnityBelowDb = 0.005;

    /// <summary>
    /// The gain for a track. <paramref name="albumContext"/> says the track plays as part of its album in order
    /// (used by <see cref="NormalizationMode.Auto"/>; <see cref="NormalizationMode.Album"/> always counts as such).
    /// Preference: album tag (album context), track tag, analysis. The gain avoids lifting the track's peak above full scale
    /// (measured on the peak the tags or the analysis give; a track whose peak is unknown is never boosted) and is limited
    /// to <see cref="MaxCutDb"/> .. <see cref="MaxBoostDb"/>. Values that are not finite numbers (a damaged library) are
    /// ignored: a bad loudness leaves the track <see cref="GainSource.Unknown"/>, a bad peak counts as no peak.
    /// </summary>
    public static NormalizationGain Compute(NormalizationSettings settings, TrackLoudness? loudness, bool albumContext)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var s = settings.Clamped();
        if (s.Mode == NormalizationMode.Off)
        {
            return NormalizationGain.Unity(GainSource.Off);
        }

        if (loudness is null)
        {
            return NormalizationGain.Unity(GainSource.Unknown);
        }

        var wantAlbum = s.Mode == NormalizationMode.Album || (s.Mode == NormalizationMode.Auto && albumContext);
        var tags = loudness.Tags;
        var truePeak = ValidPeak(loudness.TruePeak);
        double db;
        double? peak;
        GainSource source;
        if (wantAlbum && ValidGain(tags.AlbumGainDb) is { } albumGain)
        {
            db = albumGain + (s.TargetLufs - ReplayGainReferenceLufs);
            peak = ValidPeak(tags.AlbumPeak) ?? ValidPeak(tags.TrackPeak) ?? truePeak;
            source = GainSource.AlbumTag;
        }
        else if (ValidGain(tags.TrackGainDb) is { } trackGain)
        {
            db = trackGain + (s.TargetLufs - ReplayGainReferenceLufs);
            peak = ValidPeak(tags.TrackPeak) ?? truePeak;
            source = GainSource.TrackTag;
        }
        else if (loudness.Lufs is { } lufs && double.IsFinite(lufs))
        {
            db = s.TargetLufs - lufs;
            peak = truePeak;
            source = GainSource.Analysis;
        }
        else
        {
            return NormalizationGain.Unity(GainSource.Unknown);
        }

        db += s.PreampDb;
        var limited = false;
        var ceiling = peak is { } p ? -20.0 * Math.Log10(p) : 0.0;
        if (db > ceiling)
        {
            db = ceiling;
            limited = true;
        }

        if (!double.IsFinite(db))
        {
            return NormalizationGain.Unity(GainSource.Unknown);
        }

        db = Math.Clamp(db, MaxCutDb, MaxBoostDb);
        if (Math.Abs(db) < UnityBelowDb)
        {
            return new NormalizationGain(1.0, 0.0, source, limited);
        }

        return new NormalizationGain(Math.Pow(10.0, db / 20.0), db, source, limited);
    }

    private static double? ValidGain(double? value) => ReplayGainTags.ValidGainOrNull(value);

    private static double? ValidPeak(double? value) => ReplayGainTags.ValidPeakOrNull(value);

    /// <summary>
    /// Whether an entry plays as part of its album in order: not shuffled, not radio, and the entry before or after it is on
    /// the same album (by the same album artist). Entries without an album never do.
    /// </summary>
    public static bool IsAlbumContext((string? Album, string? AlbumArtist) item, (string? Album, string? AlbumArtist)? previous, (string? Album, string? AlbumArtist)? next, bool shuffled, bool radio)
    {
        if (shuffled || radio || string.IsNullOrWhiteSpace(item.Album))
        {
            return false;
        }

        bool Same((string? Album, string? AlbumArtist)? other)
            => other is { } o
               && string.Equals(o.Album, item.Album, StringComparison.OrdinalIgnoreCase)
               && string.Equals(o.AlbumArtist ?? string.Empty, item.AlbumArtist ?? string.Empty, StringComparison.OrdinalIgnoreCase);

        return Same(previous) || Same(next);
    }
}
