using System.Globalization;
using MusicSpin.Core.Audio;
using MusicSpin.Core.Metadata;
using TagLib;

namespace MusicSpin.Core.Library;

/// <summary>Why a file could not be read.</summary>
public sealed class TrackReadException(string kind, string message, Exception? inner = null) : Exception(message, inner)
{
    /// <summary>unreadable | access_denied | missing.</summary>
    public string Kind { get; } = kind;
}

/// <summary>Reads tags + audio properties: TagLib# first (fast), FFmpeg probing as fallback/complement.</summary>
public interface ITrackReader
{
    /// <summary>Fills tag/audio fields of a new <see cref="TrackRecord"/> (path/size/mtime are set by the caller).</summary>
    /// <exception cref="TrackReadException">The file cannot be used.</exception>
    TrackRecord Read(string path);
}

public sealed class TrackReader(IAudioDecoderFactory? ffmpeg) : ITrackReader
{
    public TrackRecord Read(string path)
    {
        var extension = System.IO.Path.GetExtension(path);
        var record = new TrackRecord();
        Exception? tagLibError = null;
        var needProbe = false;

        try
        {
            needProbe = !ReadWithTagLib(path, extension, record);
        }
        catch (Exception ex) when (IsBrokenTagLibInstall(ex))
        {
            // Not a problem with this file: TagLibSharp.dll (shipped next to the executable so it stays replaceable
            // under the LGPL) is missing or incompatible. Fail the scan loudly instead of skipping every file.
            throw new MusicSpinException(MusicSpinErrorCode.Unknown,
                $"TagLibSharp.dll is missing or incompatible (it must sit next to the MusicSpin executable): {ex.Message}", ex);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException && !System.IO.File.Exists(path))
        {
            throw new TrackReadException("missing", $"File disappeared during the scan: {path}", ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new TrackReadException("access_denied", $"Permission denied: {path}", ex);
        }
        catch (Exception ex)
        {
            // TagLib throws UnsupportedFormatException/CorruptFileException and, on garbage input, arbitrary exceptions.
            tagLibError = ex;
        }

        if (tagLibError is null && !needProbe)
        {
            return record;
        }

        if (ffmpeg is not { IsAvailable: true })
        {
            if (tagLibError is null)
            {
                return record; // TagLib data without exact audio props is still useful
            }

            throw new TrackReadException("unreadable",
                $"TagLib# could not read the file ({Message(tagLibError)}) and FFmpeg is not available for a second attempt.", tagLibError);
        }

        TrackInfo info;
        try
        {
            info = ffmpeg.Probe(path);
        }
        catch (MusicSpinException ex) when (ex.Code == MusicSpinErrorCode.FileNotFound && !System.IO.File.Exists(path))
        {
            throw new TrackReadException("missing", $"File disappeared during the scan: {path}", ex);
        }
        catch (Exception ex)
        {
            if (tagLibError is null)
            {
                return record; // keep TagLib result
            }

            throw new TrackReadException("unreadable", $"Not a readable audio file: TagLib# ({Message(tagLibError)}); FFmpeg ({ex.Message}).", ex);
        }

        ApplyFFmpeg(record, info, extension, tagsToo: tagLibError is not null);
        record.TagSource = tagLibError is null ? "taglib+ffmpeg" : "ffmpeg";
        return record;
    }

    /// <returns>False when audio properties are incomplete and FFmpeg should fill them in.</returns>
    private static bool ReadWithTagLib(string path, string extension, TrackRecord r)
    {
        using var file = TagLib.File.Create(path, ReadStyle.Average | ReadStyle.PictureLazy);
        var tag = file.Tag;
        r.Title = Clean(tag.Title);
        r.Artist = Clean(tag.JoinedPerformers);
        r.AlbumArtist = Clean(tag.JoinedAlbumArtists);
        r.Album = Clean(tag.Album);
        r.TrackNumber = Positive(tag.Track);
        r.TrackCount = Positive(tag.TrackCount);
        r.DiscNumber = Positive(tag.Disc);
        r.DiscCount = Positive(tag.DiscCount);
        r.Year = Positive(tag.Year);
        r.Genre = Clean(tag.JoinedGenres);
        r.ReplayGainTrackGain = Finite(tag.ReplayGainTrackGain);
        r.ReplayGainTrackPeak = Finite(tag.ReplayGainTrackPeak);
        r.ReplayGainAlbumGain = Finite(tag.ReplayGainAlbumGain);
        r.ReplayGainAlbumPeak = Finite(tag.ReplayGainAlbumPeak);
        r.HasEmbeddedCover = tag.Pictures is { Length: > 0 };
        r.IsCompilation = IsCompilation(file);

        var p = file.Properties;
        var codec = p?.Codecs.FirstOrDefault(c => c is not null && (c.MediaTypes & MediaTypes.Audio) != 0)?.Description;
        r.Codec = codec;
        r.Format = AudioFileTypes.FormatId(codec, extension);
        r.SampleRate = p?.AudioSampleRate ?? 0;
        r.BitsPerSample = p?.BitsPerSample > 0 ? p.BitsPerSample : null;
        r.BitrateKbps = p?.AudioBitrate > 0 ? p.AudioBitrate : null;
        r.Channels = p?.AudioChannels ?? 0;
        r.DurationSeconds = p?.Duration.TotalSeconds ?? 0;
        r.IsLossless = AudioFileTypes.IsLosslessFormat(r.Format);
        if (r.Format == "dsd")
        {
            r.BitsPerSample = 1;
        }

        r.IsHiRes = AudioFileTypes.IsHiRes(r.Format, r.IsLossless, r.BitsPerSample, r.SampleRate);
        r.TagSource = "taglib";

        // ALAC in MP4 reports no bit depth via TagLib#; also catch files TagLib# parsed without audio properties.
        return r.SampleRate > 0 && r.DurationSeconds > 0 && r.Channels > 0 && (!r.IsLossless || r.BitsPerSample is not null);
    }

    private static void ApplyFFmpeg(TrackRecord r, TrackInfo info, string extension, bool tagsToo)
    {
        r.Codec = info.Codec;
        r.Format = AudioFileTypes.FormatId(info.Codec, extension);
        r.SampleRate = info.SampleRate;
        r.BitsPerSample = info.BitsPerSample ?? r.BitsPerSample;
        r.BitrateKbps = info.BitRate is > 0 ? (int)(info.BitRate.Value / 1000) : r.BitrateKbps;
        r.Channels = info.Channels;
        if (info.Duration > TimeSpan.Zero)
        {
            r.DurationSeconds = info.Duration.TotalSeconds;
        }

        r.IsLossless = info.IsLossless || AudioFileTypes.IsLosslessFormat(r.Format);
        r.IsHiRes = AudioFileTypes.IsHiRes(r.Format, r.IsLossless, r.BitsPerSample, r.SampleRate);

        if (!tagsToo)
        {
            return;
        }

        var t = info.Tags;
        r.Title = Clean(Get(t, "title"));
        r.Artist = Clean(Get(t, "artist"));
        r.AlbumArtist = Clean(Get(t, "album_artist") ?? Get(t, "albumartist"));
        r.Album = Clean(Get(t, "album"));
        r.Genre = Clean(Get(t, "genre"));
        (r.TrackNumber, r.TrackCount) = NumberPair(Get(t, "track"));
        (r.DiscNumber, r.DiscCount) = NumberPair(Get(t, "disc"));
        r.Year = Year(Get(t, "date") ?? Get(t, "year"));
        r.IsCompilation = Get(t, "compilation") is "1" or "true";
        r.ReplayGainTrackGain = Db(Get(t, "replaygain_track_gain"));
        r.ReplayGainTrackPeak = Db(Get(t, "replaygain_track_peak"));
        r.ReplayGainAlbumGain = Db(Get(t, "replaygain_album_gain"));
        r.ReplayGainAlbumPeak = Db(Get(t, "replaygain_album_peak"));
    }

    private static bool IsCompilation(TagLib.File file)
    {
        try
        {
            if (file.GetTag(TagTypes.Apple) is TagLib.Mpeg4.AppleTag apple && apple.IsCompilation)
            {
                return true;
            }

            if (file.GetTag(TagTypes.Xiph) is TagLib.Ogg.XiphComment xiph && xiph.GetFirstField("COMPILATION") is "1")
            {
                return true;
            }

            if (file.GetTag(TagTypes.Id3v2) is TagLib.Id3v2.Tag id3
                && TagLib.Id3v2.TextInformationFrame.Get(id3, "TCMP", false) is { } frame
                && frame.Text.FirstOrDefault() is "1")
            {
                return true;
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Compilation flag is best-effort.
        }

        return false;
    }

    private static string? Get(IReadOnlyDictionary<string, string> tags, string key) => tags.TryGetValue(key, out var v) ? v : null;

    private static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim().Replace("\0", string.Empty, StringComparison.Ordinal);

    private static int? Positive(uint value) => value is > 0 and < int.MaxValue ? (int)value : null;

    private static double? Finite(double value) => double.IsFinite(value) ? value : null;

    private static (int?, int?) NumberPair(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return (null, null);
        }

        var parts = value.Split('/');
        int? Parse(string s) => int.TryParse(s.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) && n > 0 ? n : null;
        return (Parse(parts[0]), parts.Length > 1 ? Parse(parts[1]) : null);
    }

    private static int? Year(string? value)
        => value is { Length: >= 4 } && int.TryParse(value.AsSpan(0, 4), NumberStyles.Integer, CultureInfo.InvariantCulture, out var y) && y > 0 ? y : null;

    private static double? Db(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var cleaned = value.Replace("dB", string.Empty, StringComparison.OrdinalIgnoreCase).Trim();
        return double.TryParse(cleaned, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && double.IsFinite(d) ? d : null;
    }

    private static bool IsBrokenTagLibInstall(Exception ex) => ex switch
    {
        FileNotFoundException f => f.FileName?.StartsWith("TagLibSharp", StringComparison.OrdinalIgnoreCase) == true,
        FileLoadException f => f.FileName?.StartsWith("TagLibSharp", StringComparison.OrdinalIgnoreCase) == true,
        BadImageFormatException b => b.FileName?.StartsWith("TagLibSharp", StringComparison.OrdinalIgnoreCase) == true,
        TypeLoadException or MissingMemberException => ex.Message.Contains("TagLib", StringComparison.Ordinal),
        _ => false,
    };

    private static string Message(Exception ex) => $"{ex.GetType().Name}: {ex.Message}";
}
