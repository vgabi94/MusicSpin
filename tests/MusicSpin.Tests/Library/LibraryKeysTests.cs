using MusicSpin.Core.Library;

namespace MusicSpin.Tests.Library;

public sealed class LibraryKeysTests
{
    [Theory]
    [InlineData("The Beatles", "the beatles")]
    [InlineData("  The   Beatles ", "the beatles")]
    [InlineData("THE BEATLES", "the beatles")]
    [InlineData("VA", "various artists")]
    [InlineData("V.A.", "various artists")]
    [InlineData("Various", "various artists")]
    [InlineData(null, "")]
    [InlineData("   ", "")]
    public void Name_keys_are_case_and_whitespace_insensitive(string? name, string expected)
        => Assert.Equal(expected, LibraryKeys.NameKey(name));

    [Fact]
    public void Name_keys_normalize_unicode_composition()
        => Assert.Equal(LibraryKeys.NameKey("Beyonce\u0301"), LibraryKeys.NameKey("Beyonc\u00e9"));

    [Fact]
    public void IsUnder_respects_folder_boundaries()
    {
        var root = Path.Combine(Path.GetTempPath(), "m");
        Assert.True(LibraryKeys.IsUnder(root, root));
        Assert.True(LibraryKeys.IsUnder(Path.Combine(root, "a", "b.flac"), root));
        Assert.False(LibraryKeys.IsUnder(root + "2", root));
        Assert.False(LibraryKeys.IsUnder(Path.GetTempPath(), root));
    }

    [Fact]
    public void Ids_are_short_and_stable_even_for_huge_paths()
    {
        var huge = "/" + new string('x', 5000);
        var id = LibraryKeys.PathId(huge);
        Assert.Equal(32, id.Length);
        Assert.Equal(id, LibraryKeys.PathId(huge));
        Assert.NotEqual(id, LibraryKeys.PathId(huge + "y"));
    }

    [Fact]
    public void Display_name_prefers_the_most_common_spelling()
        => Assert.Equal("Miles Davis", LibraryKeys.DisplayName(["miles davis", "Miles Davis", "Miles Davis", null], "x"));

    [Theory]
    [InlineData("flac", true, 16, 44_100, false)]
    [InlineData("flac", true, 24, 44_100, true)]
    [InlineData("flac", true, 16, 96_000, true)]
    [InlineData("alac", true, 16, 48_000, false)]
    [InlineData("pcm", true, 24, 192_000, true)]
    [InlineData("dsd", true, 1, 2_822_400, true)]
    [InlineData("mp3", false, null, 96_000, false)]
    [InlineData("opus", false, null, 48_000, false)]
    public void HiRes_rule(string format, bool lossless, int? bits, int rate, bool expected)
        => Assert.Equal(expected, AudioFileTypes.IsHiRes(format, lossless, bits, rate));

    [Theory]
    [InlineData("a.FLAC", true)]
    [InlineData("a.dsf", true)]
    [InlineData("a.m4a", true)]
    [InlineData("a.jpg", false)]
    [InlineData("a.cue", false)]
    [InlineData("flac", false)]
    public void Audio_extensions(string name, bool expected) => Assert.Equal(expected, AudioFileTypes.IsAudioFile(name));

    [Theory]
    [InlineData("FLAC (Free Lossless Audio Codec)", ".flac", "flac")]
    [InlineData("Unknown Audio (65534)", ".wav", "pcm")]
    [InlineData(null, ".dsf", "dsd")]
    [InlineData("alac", ".m4a", "alac")]
    [InlineData(null, ".mp3", "mp3")]
    public void Format_ids_come_from_codec_or_extension(string? codec, string extension, string expected)
        => Assert.Equal(expected, AudioFileTypes.FormatId(codec, extension));
}
