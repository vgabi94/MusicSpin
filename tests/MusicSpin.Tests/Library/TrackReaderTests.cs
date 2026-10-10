using MusicSpin.Core;
using MusicSpin.Core.Audio;
using MusicSpin.Core.Library;
using MusicSpin.Core.Metadata;
using MusicSpin.Tests.Shared;

namespace MusicSpin.Tests.Library;

/// <summary>
/// TrackReader's decisions between TagLib# and FFmpeg, odd tag values, and compilation/cover flags written into copies
/// of the fixtures with TagLib# (crafted fixtures).
/// </summary>
public sealed class TrackReaderTests : IDisposable
{
    private readonly string _dir = TempDir.Create("reader");

    public void Dispose() => TempDir.TryDelete(_dir);

    private string Garbage(string name = "song.flac")
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, [.. Enumerable.Range(0, 512).Select(i => (byte)(i * 7))]);
        return path;
    }

    private string Copy(string fixture)
    {
        var path = Path.Combine(_dir, fixture);
        File.Copy(Path.Combine(LibraryTestData.FixturesDirectory, fixture), path);
        return path;
    }

    private static TrackInfo Probed(string path, Dictionary<string, string> tags) => new()
    {
        Path = path,
        Container = "flac",
        Codec = "flac",
        SampleRate = 96_000,
        Channels = 2,
        BitsPerSample = 24,
        BitRate = 2_304_000,
        Duration = TimeSpan.FromSeconds(3),
        IsLossless = true,
        Tags = new Dictionary<string, string>(tags, StringComparer.OrdinalIgnoreCase),
    };

    [Fact]
    public void When_taglib_cannot_read_a_file_ffmpegs_tags_are_parsed_instead()
    {
        var path = Garbage();
        var ffmpeg = new StubFactory(_ => Probed(path, new()
        {
            ["title"] = "  Song\0 ",
            ["artist"] = "Artist",
            ["albumartist"] = "Album Artist",
            ["album"] = "Album",
            ["genre"] = "Jazz",
            ["track"] = " 3 / 12 ",
            ["disc"] = "2",
            ["date"] = "2019-05-01",
            ["compilation"] = "1",
            ["replaygain_track_gain"] = "-6.50 dB",
            ["replaygain_track_peak"] = "0.988",
            ["replaygain_album_gain"] = "+1.25 DB",
            ["replaygain_album_peak"] = "1",
        }));

        var r = new TrackReader(ffmpeg).Read(path);

        Assert.Equal("ffmpeg", r.TagSource);
        Assert.Equal(("Song", "Artist", "Album Artist", "Album", "Jazz"), (r.Title, r.Artist, r.AlbumArtist, r.Album, r.Genre));
        Assert.Equal((3, 12, 2, (int?)null, 2019), (r.TrackNumber, r.TrackCount, r.DiscNumber, r.DiscCount, r.Year));
        Assert.True(r.IsCompilation);
        Assert.Equal((-6.5, 0.988, 1.25, 1.0), (r.ReplayGainTrackGain, r.ReplayGainTrackPeak, r.ReplayGainAlbumGain, r.ReplayGainAlbumPeak));
        Assert.Equal(("flac", 96_000, 24, 2304, 2), (r.Format, r.SampleRate, r.BitsPerSample, r.BitrateKbps, r.Channels));
        Assert.Equal(3, r.DurationSeconds);
        Assert.True(r.IsLossless);
        Assert.True(r.IsHiRes);
    }

    [Theory]
    [InlineData("x/0", "19", "loud", "NaN dB", "yes")]
    [InlineData("0", "abcd", "", "Infinity", "0")]
    [InlineData("/", "-001", "dB", "1e999", "true")]
    public void Odd_ffmpeg_tag_values_become_empty_fields_instead_of_garbage(string track, string date, string gain, string peak, string compilation)
    {
        var path = Garbage();
        var ffmpeg = new StubFactory(_ => Probed(path, new()
        {
            ["title"] = "   ",
            ["album_artist"] = "Preferred",
            ["albumartist"] = "Ignored",
            ["track"] = track,
            ["year"] = date,
            ["replaygain_track_gain"] = gain,
            ["replaygain_track_peak"] = peak,
            ["compilation"] = compilation,
        }) with { BitRate = 0, BitsPerSample = null, Duration = TimeSpan.Zero });

        var r = new TrackReader(ffmpeg).Read(path);

        Assert.Null(r.Title);
        Assert.Equal("Preferred", r.AlbumArtist);
        Assert.Null(r.TrackNumber);
        Assert.Null(r.TrackCount);
        Assert.Null(r.DiscNumber);
        Assert.Null(r.Year);
        Assert.Null(r.ReplayGainTrackGain);
        Assert.Null(r.ReplayGainTrackPeak);
        Assert.Equal(compilation == "true", r.IsCompilation);
        Assert.Null(r.BitrateKbps);
        Assert.Null(r.BitsPerSample);
        Assert.Equal(0, r.DurationSeconds); // unknown duration is not invented
    }

    public static TheoryData<Exception> BrokenTagLibInstalls() => new()
    {
        new FileNotFoundException("Could not load file or assembly", "TagLibSharp, Version=2.3.0.0"),
        new FileLoadException("Could not load file or assembly", "TagLibSharp, Version=2.3.0.0"),
        new BadImageFormatException("Bad image", "TagLibSharp.dll"),
        new TypeLoadException("Could not load type 'TagLib.File' from assembly 'TagLibSharp'."),
        new MissingMethodException("Method not found: 'TagLib.File TagLib.File.Create(System.String)'."),
    };

    [Theory]
    [MemberData(nameof(BrokenTagLibInstalls))]
    public void A_missing_or_incompatible_taglib_fails_the_scan_loudly(Exception error)
    {
        var path = Copy("tiny-44k.mp3");
        var reader = new TrackReader(new StubFactory(_ => throw new InvalidOperationException("not reached")), _ => throw error);

        var ex = Assert.Throws<MusicSpinException>(() => reader.Read(path));

        Assert.Equal(MusicSpinErrorCode.Unknown, ex.Code);
        Assert.StartsWith("TagLibSharp.dll is missing or incompatible", ex.Message, StringComparison.Ordinal);
        Assert.Same(error, ex.InnerException);
    }

    public static TheoryData<Exception> OrdinaryFailures() => new()
    {
        new FileNotFoundException("other", "Other.dll"),
        new FileLoadException("other", "Other.dll"),
        new BadImageFormatException("other"),
        new TypeLoadException("Could not load type 'Foo'."),
        new InvalidDataException("garbage"),
    };

    [Theory]
    [MemberData(nameof(OrdinaryFailures))]
    public void Other_taglib_failures_on_an_existing_file_fall_back_to_ffmpeg(Exception error)
    {
        var path = Copy("tiny-44k.mp3");
        var reader = new TrackReader(new StubFactory(p => Probed(p, new() { ["title"] = "From FFmpeg" })), _ => throw error);

        var r = reader.Read(path);

        Assert.Equal("ffmpeg", r.TagSource);
        Assert.Equal("From FFmpeg", r.Title);
    }

    [Fact]
    public void A_file_that_vanishes_before_ffmpeg_probes_it_is_missing()
    {
        var path = Path.Combine(_dir, "gone.flac");
        var reader = new TrackReader(
            new StubFactory(p => throw new MusicSpinException(MusicSpinErrorCode.FileNotFound, $"File not found: {p}")),
            _ => throw new TagLib.CorruptFileException("bad header"));

        var ex = Assert.Throws<TrackReadException>(() => reader.Read(path));

        Assert.Equal("missing", ex.Kind);
        Assert.Equal($"File disappeared during the scan: {path}", ex.Message);
    }

    [Fact]
    public void When_both_readers_fail_the_error_names_both_reasons()
    {
        var path = Garbage();
        var reader = new TrackReader(new StubFactory(_ => throw new MusicSpinException(MusicSpinErrorCode.UnsupportedFormat, "Invalid data found")));

        var ex = Assert.Throws<TrackReadException>(() => reader.Read(path));

        Assert.Equal("unreadable", ex.Kind);
        Assert.StartsWith("Not a readable audio file: TagLib# (", ex.Message, StringComparison.Ordinal);
        Assert.EndsWith("FFmpeg (Invalid data found).", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_tag_only_result_is_kept_when_ffmpeg_cannot_complete_the_audio_properties()
    {
        // A WAV header without samples: TagLib# reads it but reports no duration, so FFmpeg is asked and fails.
        var path = Path.Combine(_dir, "empty.wav");
        File.WriteAllBytes(path, EmptyWav());
        var reader = new TrackReader(new StubFactory(_ => throw new InvalidOperationException("probe failed")));

        var r = reader.Read(path);

        Assert.Equal("taglib", r.TagSource);
        Assert.Equal(44_100, r.SampleRate);
        Assert.Equal(2, r.Channels);
        Assert.Equal(0, r.DurationSeconds);
    }

    [Fact]
    public void Ffmpeg_fills_in_the_audio_properties_taglib_could_not_and_keeps_taglibs_tags()
    {
        var path = Path.Combine(_dir, "empty.wav");
        File.WriteAllBytes(path, EmptyWav());
        using (var file = TagLib.File.Create(path))
        {
            file.Tag.Title = "Tagged";
            file.Save();
        }

        var r = new TrackReader(new StubFactory(p => Probed(p, new() { ["title"] = "ignored" }) with { Codec = "pcm_s16le", BitsPerSample = 16, SampleRate = 44_100 }))
            .Read(path);

        Assert.Equal("taglib+ffmpeg", r.TagSource);
        Assert.Equal("Tagged", r.Title);
        Assert.Equal(3, r.DurationSeconds);
        Assert.Equal(16, r.BitsPerSample);
    }

    [Theory]
    [InlineData("tiny-44k.mp3")]
    [InlineData("tiny-16bit-44k.flac")]
    [InlineData("tone-16bit-44k.m4a")]
    public void Untagged_files_are_not_compilations_and_have_no_cover(string fixture)
    {
        var r = new TrackReader(null).Read(Copy(fixture));

        Assert.False(r.IsCompilation);
        Assert.False(r.HasEmbeddedCover);
    }

    [Fact]
    public void An_id3v2_tcmp_frame_marks_an_mp3_as_a_compilation()
    {
        var path = Copy("tiny-44k.mp3");
        using (var file = TagLib.File.Create(path))
        {
            var id3 = (TagLib.Id3v2.Tag)file.GetTag(TagLib.TagTypes.Id3v2, create: true);
            id3.SetTextFrame("TCMP", "1");
            file.Save();
        }

        Assert.True(new TrackReader(null).Read(path).IsCompilation);
    }

    [Fact]
    public void An_itunes_cpil_atom_marks_an_m4a_as_a_compilation()
    {
        var path = Copy("tone-16bit-44k.m4a");
        using (var file = TagLib.File.Create(path))
        {
            ((TagLib.Mpeg4.AppleTag)file.GetTag(TagLib.TagTypes.Apple, create: true)).IsCompilation = true;
            file.Save();
        }

        Assert.True(new TrackReader(null).Read(path).IsCompilation);
    }

    [Fact]
    public void A_vorbis_compilation_comment_and_an_embedded_picture_are_detected_in_flac()
    {
        var path = Copy("tiny-16bit-44k.flac");
        using (var file = TagLib.File.Create(path))
        {
            ((TagLib.Ogg.XiphComment)file.GetTag(TagLib.TagTypes.Xiph, create: true)).SetField("COMPILATION", "1");
            file.Tag.Pictures = [new TagLib.Picture(new TagLib.ByteVector([0x89, 0x50, 0x4E, 0x47])) { MimeType = "image/png", Type = TagLib.PictureType.FrontCover }];
            file.Save();
        }

        var r = new TrackReader(null).Read(path);

        Assert.True(r.IsCompilation);
        Assert.True(r.HasEmbeddedCover);
    }

    private static byte[] EmptyWav()
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write("RIFF"u8);
        w.Write(36);
        w.Write("WAVEfmt "u8);
        w.Write(16);
        w.Write((short)1);
        w.Write((short)2);
        w.Write(44_100);
        w.Write(44_100 * 4);
        w.Write((short)4);
        w.Write((short)16);
        w.Write("data"u8);
        w.Write(0);
        w.Flush();
        return ms.ToArray();
    }

    private sealed class StubFactory(Func<string, TrackInfo> probe) : IAudioDecoderFactory
    {
        public string Name => "stub";

        public bool IsAvailable => true;

        public TrackInfo Probe(string path) => probe(path);

        public IAudioDecoder Open(string path) => throw new NotSupportedException();

        public IReadOnlyList<AudioFormatSupport> GetSupportedFormats() => [];
    }
}
