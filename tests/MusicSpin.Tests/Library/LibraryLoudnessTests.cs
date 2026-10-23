using MusicSpin.Core.Library;
using MusicSpin.Core.Normalization;
using MusicSpin.Tests.Shared;

namespace MusicSpin.Tests.Library;

/// <summary>The loudness fields of the library: analysis results kept with the track and valid only for the file they came from.</summary>
public sealed class LibraryLoudnessTests : LibraryTestBase
{
    private string Track(string name = "one.flac", string title = "One")
        => LibraryTestData.Write(Path.Combine(Music, name), LibraryTestData.Tiny16, title, "Artist", "Album", "Artist");

    private static LoudnessUpdate Update(string path, double lufs = -20, double peak = 0.5)
    {
        var info = new FileInfo(path);
        return new LoudnessUpdate(path, lufs, peak, info.Length, info.LastWriteTimeUtc.Ticks);
    }

    [Fact]
    public void A_stored_analysis_is_returned_for_the_unchanged_file()
    {
        var path = Track();
        AddAndScan();

        using (var library = Open())
        {
            Assert.Equal(1, library.StoreLoudness([Update(path, -20.5, 0.75)]));
        }

        using var read = OpenRead();
        var loudness = read.LoudnessFor([path])[NormalizationService.KeyOf(path)];
        Assert.Equal((true, -20.5, 0.75), (loudness.Analyzed, loudness.Lufs, loudness.TruePeak));
        Assert.True(loudness.IsKnown);
    }

    [Fact]
    public void An_analysis_is_ignored_once_the_file_has_changed()
    {
        var path = Track();
        AddAndScan();
        using (var library = Open())
        {
            library.StoreLoudness([Update(path)]);
        }

        Touch(path);

        using var read = OpenRead();
        var loudness = read.LoudnessFor([path])[NormalizationService.KeyOf(path)];
        Assert.False(loudness.Analyzed);
        Assert.False(loudness.IsKnown);
    }

    [Fact]
    public void An_analysis_measured_on_an_older_version_of_the_file_is_not_valid()
    {
        var path = Track();
        AddAndScan();
        var stale = Update(path) with { ModifiedUtcTicks = File.GetLastWriteTimeUtc(path).Ticks - TimeSpan.TicksPerSecond };
        using (var library = Open())
        {
            library.StoreLoudness([stale]);
        }

        using var read = OpenRead();
        Assert.False(read.LoudnessFor([path])[NormalizationService.KeyOf(path)].Analyzed);
    }

    [Fact]
    public void A_rescan_of_a_changed_file_drops_its_analysis_and_reads_its_tags_again()
    {
        var path = Track();
        AddAndScan();
        using (var library = Open())
        {
            library.StoreLoudness([Update(path)]);
        }

        using (var file = TagLib.File.Create(path))
        {
            file.Tag.ReplayGainTrackGain = -9;
            file.Tag.ReplayGainTrackPeak = 0.7;
            file.Save();
        }

        Touch(path);
        Rescan();

        var record = Assert.Single(AllTracks());
        Assert.False(record.LoudnessAnalyzed);
        Assert.Null(record.LoudnessLufs);
        Assert.Equal(-9, record.ReplayGainTrackGain);
        using var read = OpenRead();
        var loudness = read.LoudnessFor([path])[NormalizationService.KeyOf(path)];
        Assert.Equal((-9.0, 0.7, false), (loudness.Tags.TrackGainDb, loudness.Tags.TrackPeak, loudness.Analyzed));
    }

    [Fact]
    public void An_unchanged_file_keeps_its_analysis_across_a_rescan()
    {
        var path = Track();
        AddAndScan();
        using (var library = Open())
        {
            library.StoreLoudness([Update(path, -15)]);
        }

        Rescan();

        using var read = OpenRead();
        Assert.Equal(-15, read.LoudnessFor([path])[NormalizationService.KeyOf(path)].Lufs);
    }

    [Fact]
    public void Tags_come_back_with_the_lookup_even_without_an_analysis()
    {
        var path = Track();
        using (var file = TagLib.File.Create(path))
        {
            file.Tag.ReplayGainAlbumGain = -2;
            file.Save();
        }

        AddAndScan();

        using var read = OpenRead();
        var loudness = read.LoudnessFor([path])[NormalizationService.KeyOf(path)];
        Assert.Equal(-2, loudness.Tags.AlbumGainDb);
        Assert.False(loudness.Analyzed);
    }

    [Fact]
    [Trait("Category", "Unhappy")]
    public void Files_that_are_not_library_tracks_are_left_out_and_not_stored()
    {
        var inLibrary = Track();
        AddAndScan();
        var outside = LibraryTestData.Write(Path.Combine(Root, "elsewhere.flac"), LibraryTestData.Tiny16, "Out", "X", "Y");

        using var library = Open();
        Assert.Equal(0, library.StoreLoudness([Update(outside)]));
        var found = library.LoudnessFor([outside, inLibrary, "", "  "]);
        Assert.Equal([NormalizationService.KeyOf(inLibrary)], found.Keys);
    }

    [Fact]
    [Trait("Category", "Unhappy")]
    public void Missing_arguments_are_refused()
    {
        using var library = Open();
        Assert.Throws<ArgumentNullException>(() => library.LoudnessFor(null!));
        Assert.Throws<ArgumentNullException>(() => library.StoreLoudness(null!));
    }

    [Fact]
    public void The_store_reads_and_saves_through_the_database_file()
    {
        var path = Track();
        AddAndScan();
        var store = new LibraryLoudnessStore(Data);

        Assert.True(store.Save([Update(path, -22)]));
        var loaded = store.Load([path])!;

        Assert.Equal(-22, loaded[NormalizationService.KeyOf(path)].Lufs);
    }

    [Fact]
    [Trait("Category", "Unhappy")]
    public void The_store_knows_nothing_and_keeps_nothing_when_there_is_no_library()
    {
        var path = Track();
        var store = new LibraryLoudnessStore(Data);

        Assert.Empty(store.Load([path])!);
        Assert.True(store.Save([Update(path)]));
        Assert.Empty(store.Load([])!);
        Assert.True(store.Save([]));
    }

    [Fact]
    [Trait("Category", "Unhappy")]
    public void A_library_held_by_another_process_means_busy_not_unknown()
    {
        var path = Track();
        AddAndScan();
        var store = new LibraryLoudnessStore(Data, TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(50));

        using (Open())
        {
            Assert.Null(store.Load([path]));
            Assert.False(store.Save([Update(path)]));
        }

        Assert.NotNull(store.Load([path])); // free again
        Assert.True(store.Save([Update(path)]));
    }
}
