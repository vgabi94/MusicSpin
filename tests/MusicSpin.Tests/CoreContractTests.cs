using MusicSpin.Core;
using MusicSpin.Core.Hosting;
using MusicSpin.Core.Library;
using MusicSpin.Core.Settings;
using MusicSpin.Tests.Shared;

namespace MusicSpin.Tests;

/// <summary>Small public contracts every front end relies on: exit codes, the exception type, format ids, settings saves.</summary>
public sealed class CoreContractTests : IDisposable
{
    private readonly string _root = TempDir.Create("contract");

    public void Dispose() => TempDir.TryDelete(_root);

    public static TheoryData<MusicSpinErrorCode> Codes => [.. Enum.GetValues<MusicSpinErrorCode>()];

    [Theory]
    [MemberData(nameof(Codes))]
    public void Every_error_code_maps_to_an_exit_code_with_a_name_and_back(MusicSpinErrorCode code)
    {
        var exit = ExitCodes.From(code);
        var name = ExitCodes.Name(exit);
        Assert.Matches("^[a-z_]+$", name);
        Assert.Contains($"{exit} {name}", ExitCodes.HelpText, StringComparison.Ordinal);
        if (code != MusicSpinErrorCode.Unknown)
        {
            Assert.Equal(code, ExitCodes.ToErrorCode(exit));
        }
        else
        {
            Assert.Equal(ExitCodes.Error, exit);
        }
    }

    [Theory]
    [InlineData(ExitCodes.Success, "ok")]
    [InlineData(ExitCodes.Cancelled, "cancelled")]
    [InlineData(99, "error")]
    [InlineData(-1, "error")]
    public void Exit_codes_without_an_error_code_have_stable_names(int exit, string name)
    {
        Assert.Equal(name, ExitCodes.Name(exit));
        Assert.Equal(MusicSpinErrorCode.Unknown, ExitCodes.ToErrorCode(exit));
    }

    [Fact]
    public void Exception_constructors_default_to_unknown_and_keep_inner_exceptions()
    {
        Assert.Equal(MusicSpinErrorCode.Unknown, new MusicSpinException().Code);
        Assert.False(string.IsNullOrEmpty(new MusicSpinException().Message));
        Assert.Equal(("m", MusicSpinErrorCode.Unknown), (new MusicSpinException("m").Message, new MusicSpinException("m").Code));
        var inner = new IOException("io");
        var wrapped = new MusicSpinException("outer", inner);
        Assert.Same(inner, wrapped.InnerException);
        Assert.Equal(MusicSpinErrorCode.Unknown, wrapped.Code);
        Assert.Same(inner, new MusicSpinException(MusicSpinErrorCode.NotFound, "x", inner).InnerException);
    }

    [Theory]
    [InlineData(null, ".w64", "pcm")]
    [InlineData(null, ".RF64", "pcm")]
    [InlineData(null, ".aifc", "pcm")]
    [InlineData(null, ".dff", "dsd")]
    [InlineData(null, ".wv", "wavpack")]
    [InlineData(null, ".ape", "ape")]
    [InlineData(null, ".tta", "tta")]
    [InlineData(null, ".mp2", "mp3")]
    [InlineData(null, ".aac", "aac")]
    [InlineData(null, ".opus", "opus")]
    [InlineData(null, ".oga", "vorbis")]
    [InlineData(null, ".wma", "wma")]
    [InlineData(null, ".mpc", "musepack")]
    [InlineData(null, ".m4b", "aac")]
    [InlineData(null, ".xyz", "xyz")]
    [InlineData("DSD (Direct Stream Digital), least significant bit first", ".dsf", "dsd")]
    [InlineData("WavPack", ".wv", "wavpack")]
    [InlineData("Monkey's Audio", ".ape", "ape")]
    [InlineData("ape", ".x", "ape")]
    [InlineData("MP3 (MPEG audio layer 3)", ".bin", "mp3")]
    [InlineData("AAC (Advanced Audio Coding)", ".m4a", "aac")]
    [InlineData("opus", ".ogg", "opus")]
    [InlineData("vorbis", ".ogg", "vorbis")]
    [InlineData("wmalossless", ".wma", "wmalossless")]
    [InlineData("wmav2", ".wma", "wma")]
    [InlineData("True Audio", ".x", "tta")]
    [InlineData("ALAC (Apple Lossless Audio Codec)", ".m4a", "alac")]
    public void Format_ids_cover_every_codec_and_extension(string? codec, string extension, string expected)
        => Assert.Equal(expected, AudioFileTypes.FormatId(codec, extension));

    [Fact]
    [Trait("Category", "Unhappy")]
    public void Saving_settings_over_a_folder_is_not_writable_and_leaves_no_temp_file()
    {
        var data = new DataDirectory(Path.Combine(_root, "data"), DataDirectorySource.Override);
        Directory.CreateDirectory(Path.Combine(data.SettingsFile, "x")); // settings.json is a non-empty folder
        var store = new SettingsStore(data);
        var ex = Assert.Throws<MusicSpinException>(() => store.Save(new AppSettings { Repeat = "all" }));
        Assert.Equal(MusicSpinErrorCode.DataDirectoryNotWritable, ex.Code);
        Assert.Empty(Directory.GetFiles(data.Path, "*.tmp"));
        Assert.False(store.TryCreate(new AppSettings())); // "exists" (as a folder): nothing created
        Assert.Empty(Directory.GetFiles(data.Path, "*.tmp"));
    }

    [Fact]
    public void Settings_save_overwrites_and_try_create_never_does()
    {
        var data = new DataDirectory(Path.Combine(_root, "data2"), DataDirectorySource.Override);
        var store = new SettingsStore(data);
        Assert.True(store.TryCreate(new AppSettings { Repeat = "one" }));
        Assert.False(store.TryCreate(new AppSettings { Repeat = "all" }));
        Assert.Equal("one", store.Load().Repeat);
        store.Save(new AppSettings { Repeat = "all", Shuffle = true });
        var loaded = store.Load();
        Assert.Equal(("all", true), (loaded.Repeat, loaded.Shuffle));
        Assert.Null(store.LoadWarning);
        Assert.Empty(Directory.GetFiles(data.Path, "*.tmp"));
    }

    [Fact]
    [Trait("Category", "Unhappy")]
    public void A_json_null_settings_file_loads_defaults()
    {
        var data = new DataDirectory(Path.Combine(_root, "data3"), DataDirectorySource.Override);
        Directory.CreateDirectory(data.Path);
        File.WriteAllText(data.SettingsFile, "null");
        var store = new SettingsStore(data);
        Assert.Equal("off", store.Load().Repeat);
        Assert.Null(store.LoadWarning);
    }

    [Fact]
    public void Track_tags_that_are_blank_read_as_missing()
    {
        var track = TestAudio.Track() with
        {
            Tags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["TITLE"] = "  ", ["genre"] = "Jazz", ["date"] = "1959", ["track"] = "3/9" },
        };
        Assert.Null(track.Title);
        Assert.Null(track.Artist);
        Assert.Equal(("Jazz", "1959", "3/9"), (track.Genre, track.Date, track.TrackNumber));
    }
}
