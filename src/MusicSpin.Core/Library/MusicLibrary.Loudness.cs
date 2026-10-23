using MusicSpin.Core.Normalization;

namespace MusicSpin.Core.Library;

public sealed partial class MusicLibrary
{
    /// <summary>
    /// What the library knows about the loudness of files, by <see cref="NormalizationService.KeyOf"/>: their ReplayGain / R128
    /// tags and, when it ran, the analysis. An analysis counts only while the file still has the size and modification time it
    /// was measured on and was measured by the current <see cref="LoudnessMeter.AlgorithmVersion"/>. Files that are not library tracks are left out.
    /// </summary>
    public IReadOnlyDictionary<string, TrackLoudness> LoudnessFor(IEnumerable<string> paths) => Guard(() =>
    {
        ArgumentNullException.ThrowIfNull(paths);
        var result = new Dictionary<string, TrackLoudness>(StringComparer.Ordinal);
        foreach (var path in paths.Where(p => !string.IsNullOrWhiteSpace(p)))
        {
            var key = NormalizationService.KeyOf(path);
            if (result.ContainsKey(key) || _db.Tracks.FindOne(t => t.PathKey == key) is not { } track)
            {
                continue;
            }

            var tags = new ReplayGainTags(track.ReplayGainTrackGain, track.ReplayGainTrackPeak, track.ReplayGainAlbumGain, track.ReplayGainAlbumPeak);
            var valid = track.LoudnessAnalyzed && track.LoudnessVersion == LoudnessMeter.AlgorithmVersion && FileStamp.Read(track.Path) == new FileStamp(track.LoudnessSizeBytes, track.LoudnessModifiedUtcTicks);
            result[key] = valid
                ? new TrackLoudness(tags, track.LoudnessLufs, track.LoudnessTruePeak, Analyzed: true)
                : new TrackLoudness(tags);
        }

        return (IReadOnlyDictionary<string, TrackLoudness>)result;
    });

    /// <summary>Keeps analysis results with the tracks they belong to. Returns how many were kept (files that are not library tracks are skipped).</summary>
    /// <exception cref="MusicSpinException">DataDirectoryNotWritable.</exception>
    public int StoreLoudness(IReadOnlyList<LoudnessUpdate> updates) => Guard(() =>
    {
        ArgumentNullException.ThrowIfNull(updates);
        var kept = 0;
        foreach (var update in updates)
        {
            var key = NormalizationService.KeyOf(update.Path);
            if (_db.Tracks.FindOne(t => t.PathKey == key) is not { } track)
            {
                continue;
            }

            track.LoudnessLufs = update.Lufs;
            track.LoudnessTruePeak = update.TruePeak;
            track.LoudnessAnalyzed = true;
            track.LoudnessVersion = update.Version;
            track.LoudnessSizeBytes = update.SizeBytes;
            track.LoudnessModifiedUtcTicks = update.ModifiedUtcTicks;
            _db.Tracks.Update(track);
            kept++;
        }

        return kept;
    });
}
