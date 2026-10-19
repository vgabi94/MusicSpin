using System.Runtime.Versioning;
using MusicSpin.App.Services.MediaControls;
using MusicSpin.Tests.Shared;

namespace MusicSpin.Tests.Ui;

/// <summary>
/// The macOS media controls (#35). <see cref="MacNowPlaying"/> (what Now Playing shows, which command a handler sends)
/// is tested on every OS; the adapter itself gets a smoke test on macOS, where it writes to the real
/// <c>MPNowPlayingInfoCenter</c>, reads it back and calls its own remote command handlers.
/// </summary>
public sealed class MacMediaControlsTests
{
    private static readonly MediaInfo Playing = new(
        "Song", "Artist", "Album", null, TimeSpan.FromMinutes(3), TimeSpan.FromSeconds(42), MediaPlayback.Playing, 0.5, "t-1");

    [Fact]
    public void Now_playing_carries_the_tags_the_timeline_and_a_running_rate_while_playing()
    {
        var fields = MacNowPlaying.Describe(Playing);

        Assert.Equal(new MacNowPlaying.Fields("Song", "Artist", "Album", 180, 42, 1.0, MacNowPlaying.State.Playing, null), fields);
    }

    [Theory]
    [InlineData(MediaPlayback.Paused, MacNowPlaying.State.Paused)]
    [InlineData(MediaPlayback.Stopped, MacNowPlaying.State.Stopped)]
    public void Paused_or_stopped_freezes_the_clock(MediaPlayback playback, MacNowPlaying.State state)
    {
        var fields = MacNowPlaying.Describe(Playing with { Playback = playback });

        Assert.Equal(0.0, fields.Rate);
        Assert.Equal(state, fields.State);
        Assert.Equal(42, fields.Elapsed);
    }

    [Fact]
    public void Missing_tags_and_an_unknown_length_are_left_out_and_the_position_is_clamped()
    {
        var unknown = MacNowPlaying.Describe(Playing with { Title = " ", Artist = null, Album = "", Duration = TimeSpan.Zero, Position = TimeSpan.FromSeconds(-3) });
        Assert.Null(unknown.Title);
        Assert.Null(unknown.Artist);
        Assert.Null(unknown.Album);
        Assert.Null(unknown.Duration);
        Assert.Equal(0, unknown.Elapsed);

        Assert.Equal(180, MacNowPlaying.Describe(Playing with { Position = TimeSpan.FromMinutes(5) }).Elapsed);
    }

    [Fact]
    public void Only_a_cover_file_that_exists_is_offered_as_artwork()
    {
        var cover = Path.Combine(TempDir.Create("mac-cover"), "cover.jpg");

        Assert.Null(MacNowPlaying.Describe(Playing with { CoverFile = cover }).CoverFile);
        File.WriteAllBytes(cover, [1]);
        Assert.Equal(cover, MacNowPlaying.Describe(Playing with { CoverFile = cover }).CoverFile);
    }

    [Theory]
    [InlineData("musicSpinPlay:", MediaCommandKind.Play)]
    [InlineData("musicSpinPause:", MediaCommandKind.Pause)]
    [InlineData("musicSpinToggle:", MediaCommandKind.PlayPause)]
    [InlineData("musicSpinStop:", MediaCommandKind.Stop)]
    [InlineData("musicSpinNext:", MediaCommandKind.Next)]
    [InlineData("musicSpinPrevious:", MediaCommandKind.Previous)]
    public void Each_remote_command_sends_its_command(string selector, MediaCommandKind kind)
        => Assert.Equal(new MediaCommand(kind), MacNowPlaying.CommandFor(selector, 0, null));

    [Fact]
    public void The_scrubber_sets_the_position_in_the_current_track_only()
    {
        Assert.Equal(new MediaCommand(MediaCommandKind.SetPosition, TimeSpan.FromSeconds(61.5), TrackId: "t-1"), MacNowPlaying.CommandFor("musicSpinPosition:", 61.5, "t-1"));
        Assert.Null(MacNowPlaying.CommandFor("musicSpinPosition:", 61.5, null));
        Assert.Null(MacNowPlaying.CommandFor("musicSpinPosition:", -1, "t-1"));
        Assert.Null(MacNowPlaying.CommandFor("musicSpinPosition:", double.NaN, "t-1"));
        Assert.Null(MacNowPlaying.CommandFor("musicSpinExplode:", 0, "t-1"));
    }

    [Fact]
    public void Every_handled_command_has_a_distinct_handler()
    {
        Assert.Equal(MacNowPlaying.Commands.Count, MacNowPlaying.Commands.Select(c => c.Selector).Distinct().Count());
        Assert.All(MacNowPlaying.Commands, c => Assert.True(c.Selector == "musicSpinPosition:" || MacNowPlaying.CommandFor(c.Selector, 0, null) is not null, c.Selector));
    }

    [Fact]
    public void Smoke_on_macOS_now_playing_and_the_remote_commands_work_for_real()
    {
        if (!OperatingSystem.IsMacOS())
        {
            Assert.Skip("macOS only");
            return;
        }

        Smoke();
    }

    [SupportedOSPlatform("macos")]
    private static void Smoke()
    {
        var cover = Path.Combine(TempDir.Create("mac-smoke"), "cover.png");
        File.WriteAllBytes(cover, Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg=="));
        var received = new List<MediaCommand>();
        using (var adapter = MacMediaControlsAdapter.Create())
        {
            adapter.CommandReceived += (_, command) => received.Add(command);
            Assert.Throws<InvalidOperationException>(MacMediaControlsAdapter.Create);

            adapter.Update(Playing with { CoverFile = cover });
            Assert.Equal(("Song", MacNowPlaying.State.Playing, true), adapter.ReadBack());

            adapter.Update(Playing with { Title = "Other", Playback = MediaPlayback.Paused, CoverFile = null });
            Assert.Equal(("Other", MacNowPlaying.State.Paused, false), adapter.ReadBack());

            Assert.Equal(0, ObjCSend(adapter, "musicSpinNext:"));
            Assert.Equal(0, ObjCSend(adapter, "musicSpinToggle:"));
            Assert.Equal(0, ObjCSend(adapter, "musicSpinPosition:")); // no event: position 0
            Assert.Equal(
                [new MediaCommand(MediaCommandKind.Next), new MediaCommand(MediaCommandKind.PlayPause), new MediaCommand(MediaCommandKind.SetPosition, TrackId: "t-1")],
                received);

            adapter.Clear();
            Assert.Equal((null, MacNowPlaying.State.Stopped, false), adapter.ReadBack());
            Assert.Equal(110, ObjCSend(adapter, "musicSpinPosition:")); // nothing playing: no now playing item
        }

        // Disposed: the slot is free again, and the old target answers without a live adapter.
        using var again = MacMediaControlsAdapter.Create();
        again.Update(Playing);
        Assert.Equal("Song", again.ReadBack().Title);
    }

    [SupportedOSPlatform("macos")]
    private static nint ObjCSend(MacMediaControlsAdapter adapter, string selector) => ObjC.SendForStatus(adapter.Target, ObjC.Sel(selector), IntPtr.Zero);
}
