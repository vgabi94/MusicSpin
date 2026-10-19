using Microsoft.Extensions.Time.Testing;
using MusicSpin.Core;
using MusicSpin.Core.Hosting;
using MusicSpin.Core.Library;
using MusicSpin.Tests.Shared;

namespace MusicSpin.Tests.Radio;

/// <summary><see cref="StationStore"/>: recent and saved radio stations in stations.json (#38).</summary>
public sealed class StationStoreTests
{
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 19, 8, 0, 0, TimeSpan.Zero));
    private readonly DataDirectory _dir = new(Path.Combine(TempDir.Create("stations"), ".usrdata"), DataDirectorySource.Override);

    private StationStore Store(Action<int>? onLockRetry = null) => new(_dir, _time, TimeSpan.FromSeconds(5), onLockRetry);

    private static RadioFilter Genre(params string[] genres) => new() { Genres = genres };

    private static string J<T>(T value) => MusicSpin.Core.Remote.ProtocolJson.Serialize(value);

    private static MusicSpinException Fails(MusicSpinErrorCode code, Action action)
    {
        var ex = Assert.Throws<MusicSpinException>(action);
        Assert.Equal(code, ex.Code);
        return ex;
    }

    [Fact]
    public void Recent_stations_are_newest_first_without_duplicates_and_keep_six()
    {
        var store = Store();
        foreach (var genre in new[] { "A", "B", "C", "D", "E", "F", "G" })
        {
            store.AddRecent(Genre(genre));
            _time.Advance(TimeSpan.FromMinutes(1));
        }

        store.AddRecent(Genre("c")); // C again (another case): moves to the top instead of appearing twice

        var recent = store.Recent();
        Assert.Equal(StationStore.RecentLimit, recent.Count);
        Assert.Equal(["genre c", "genre G", "genre F", "genre E", "genre D", "genre B"], recent.Select(r => r.Description));
        Assert.Equal(_time.GetUtcNow(), recent[0].StartedAt);
        Assert.Equal(J(Genre("G")), J(store.GetRecent(2).Filter));
    }

    [Fact]
    public void The_same_station_in_another_order_is_one_station()
    {
        var store = Store();
        store.AddRecent(new RadioFilter { Genres = ["Jazz", "Ambient"], Years = [new YearRange(1960, 1969), new YearRange(1950, 1959)] });
        store.AddRecent(new RadioFilter { Genres = ["ambient", "jazz"], Years = [new YearRange(1950, 1959), new YearRange(1960, 1969)] });
        store.AddRecent(new RadioFilter { Genres = ["Jazz"], ExcludeGenres = ["Ambient"] }); // different: an exclude

        Assert.Equal(2, store.Recent().Count);
    }

    [Fact]
    public void Saved_stations_save_replace_get_and_delete_by_name_case_insensitively()
    {
        var store = Store();
        var saved = store.Save("  Late Jazz ", Genre("Jazz"));
        store.Save("ambient", Genre("Ambient"));

        Assert.Equal(("Late Jazz", "genre Jazz", _time.GetUtcNow()), (saved.Name, saved.Description, saved.SavedAt));
        Assert.Equal(["ambient", "Late Jazz"], store.Saved().Select(s => s.Name)); // by name
        Assert.Equal(J(Genre("Jazz")), J(store.Get("late jazz").Filter));

        _time.Advance(TimeSpan.FromHours(1));
        var replaced = store.Save("LATE JAZZ", Genre("Bebop"), replace: true);
        Assert.Equal(2, store.Saved().Count);
        Assert.Equal((J(Genre("Bebop")), _time.GetUtcNow()), (J(store.Get("Late Jazz").Filter), replaced.SavedAt));

        Assert.Equal("ambient", store.Delete("AMBIENT").Name);
        Assert.Equal(["LATE JAZZ"], store.Saved().Select(s => s.Name));
    }

    [Fact]
    [Trait("Category", "Unhappy")]
    public void Bad_names_taken_names_and_unknown_stations_fail_without_changing_anything()
    {
        var store = Store();
        store.Save("Jazz", Genre("Jazz"));
        var before = File.ReadAllText(store.FilePath);

        Assert.Contains("already saved", Fails(MusicSpinErrorCode.InvalidState, () => store.Save("jazz", Genre("Rock"))).Message, StringComparison.Ordinal);
        Fails(MusicSpinErrorCode.InvalidArgument, () => store.Save(" ", Genre("Rock")));
        Fails(MusicSpinErrorCode.InvalidArgument, () => store.Save(new string('x', StationStore.MaxNameLength + 1), Genre("Rock")));
        Fails(MusicSpinErrorCode.InvalidArgument, () => store.Save("Bad", new RadioFilter { Years = [new YearRange(1999, 1990)] }));
        Fails(MusicSpinErrorCode.InvalidArgument, () => store.AddRecent(new RadioFilter { Genres = [""] }));
        Fails(MusicSpinErrorCode.NotFound, () => store.Delete("Rock"));
        Fails(MusicSpinErrorCode.NotFound, () => store.Get("Rock"));
        Assert.Contains("No recent radio stations yet", Fails(MusicSpinErrorCode.NotFound, () => store.GetRecent(1)).Message, StringComparison.Ordinal);
        Fails(MusicSpinErrorCode.InvalidArgument, () => store.GetRecent(0));
        store.AddRecent(Genre("Jazz"));
        Assert.Contains("only 1 recent", Fails(MusicSpinErrorCode.NotFound, () => store.GetRecent(2)).Message, StringComparison.Ordinal);

        Assert.Equal(["Jazz"], store.Saved().Select(s => s.Name));
        Assert.Equal(J(Genre("Jazz")), J(store.Get("Jazz").Filter));
        Assert.NotEqual(before, File.ReadAllText(store.FilePath)); // only AddRecent wrote
    }

    [Fact]
    public void The_file_holds_filters_not_paths_so_a_copied_data_folder_reads_the_same()
    {
        var store = Store();
        store.Save("Kind of Blue", new RadioFilter { Albums = ["album-id-1"], Artists = ["Miles Davis"] });
        store.AddRecent(Genre("Jazz"));

        var text = File.ReadAllText(store.FilePath);
        Assert.DoesNotContain(_dir.Path, text, StringComparison.Ordinal);
        Assert.DoesNotContain(Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar), text, StringComparison.Ordinal);

        var moved = new DataDirectory(Path.Combine(TempDir.Create("stations-moved"), ".usrdata"), DataDirectorySource.Override);
        Directory.CreateDirectory(moved.Path);
        File.Copy(store.FilePath, moved.StationsFile);
        var copy = new StationStore(moved, _time);
        Assert.Equal(J(store.Saved()), J(copy.Saved()));
        Assert.Equal(J(store.Recent()), J(copy.Recent()));
    }

    [Theory]
    [Trait("Category", "Unhappy")]
    [InlineData("{ not json")]
    [InlineData("{\"version\":99,\"recent\":[],\"saved\":[]}")]
    [InlineData("null")]
    public void An_unreadable_file_reads_as_empty_with_a_warning_and_is_set_aside_on_the_next_change(string content)
    {
        Directory.CreateDirectory(_dir.Path);
        File.WriteAllText(_dir.StationsFile, content);
        var store = Store();

        Assert.Empty(store.Recent());
        Assert.Contains("Ignoring unreadable radio stations file", store.LoadWarning, StringComparison.Ordinal);
        Assert.Empty(store.Saved());

        store.Save("Jazz", Genre("Jazz"));

        Assert.Equal(content, File.ReadAllText(_dir.StationsFile + ".bad"));
        Assert.Equal(["Jazz"], store.Saved().Select(s => s.Name));
        Assert.Null(store.LoadWarning);
    }

    [Fact]
    [Trait("Category", "Unhappy")]
    public void Damaged_entries_are_dropped_and_the_rest_kept()
    {
        Directory.CreateDirectory(_dir.Path);
        File.WriteAllText(_dir.StationsFile, """
            {"version":1,
             "recent":[{"filter":{"genres":["Jazz"]},"startedAt":"2026-10-01T00:00:00Z"},{"startedAt":"2026-10-01T00:00:00Z"},
                       {"filter":{"years":[{"from":1999,"to":1990}]},"startedAt":"2026-10-01T00:00:00Z"}],
             "saved":[{"name":"","filter":{}},{"name":"Ok","filter":{"artists":["Miles Davis"]},"savedAt":"2026-10-01T00:00:00Z"},null]}
            """);
        var store = Store();

        Assert.Equal(["genre Jazz"], store.Recent().Select(r => r.Description));
        Assert.Equal(["Ok"], store.Saved().Select(s => s.Name));
        Assert.Null(store.LoadWarning);
    }

    [Fact]
    [Trait("Category", "Unhappy")]
    public void A_change_waits_for_another_process_holding_the_lock_then_goes_ahead()
    {
        Directory.CreateDirectory(_dir.Path);
        var held = new FileStream(_dir.StationsFile + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var retries = new List<int>();
        var store = Store(attempt =>
        {
            retries.Add(attempt);
            if (attempt == 3)
            {
                held.Dispose(); // the other process finishes its change
            }
        });

        store.Save("Jazz", Genre("Jazz"));

        Assert.Equal([1, 2, 3], retries);
        Assert.Equal(["Jazz"], store.Saved().Select(s => s.Name));
    }

    [Fact]
    [Trait("Category", "Unhappy")]
    public void A_lock_that_is_never_released_fails_with_invalid_state_and_writes_nothing()
    {
        Directory.CreateDirectory(_dir.Path);
        using var held = new FileStream(_dir.StationsFile + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var store = Store(_ => _time.Advance(TimeSpan.FromSeconds(2)));

        Assert.Contains("in use by another MusicSpin process", Fails(MusicSpinErrorCode.InvalidState, () => store.AddRecent(Genre("Jazz"))).Message, StringComparison.Ordinal);
        Assert.False(File.Exists(store.FilePath));
    }

    [Fact]
    public async Task Concurrent_writers_lose_no_change()
    {
        // Two "processes" (separate stores) saving at once: every save survives the read-modify-write.
        var a = new StationStore(_dir, _time);
        var b = new StationStore(_dir, _time);
        using var start = new ManualResetEventSlim();
        var tasks = new[] { a, b }.Select((store, k) => Task.Run(() =>
        {
            start.Wait();
            for (var i = 0; i < 25; i++)
            {
                store.Save($"s{k}-{i}", Genre($"G{k}{i}"));
            }
        })).ToArray();
        start.Set();
        await Task.WhenAll(tasks);

        Assert.Equal(50, Store().Saved().Count);
    }

    [Fact]
    [Trait("Category", "Unhappy")]
    public void A_data_folder_that_is_a_file_is_not_writable()
    {
        var parent = TempDir.Create("stations-file");
        var blocked = new DataDirectory(Path.Combine(parent, "data"), DataDirectorySource.Override);
        File.WriteAllText(blocked.Path, "not a folder");
        var store = new StationStore(blocked, _time);

        Assert.Empty(store.Recent());
        Fails(MusicSpinErrorCode.DataDirectoryNotWritable, () => store.Save("Jazz", Genre("Jazz")));
    }
}
