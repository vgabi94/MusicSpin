using MusicSpin.Cli.Commands;
using MusicSpin.Core.Playback;
using MusicSpin.Core.Remote;

namespace MusicSpin.Tests.Cli;

/// <summary>The human-readable <c>status</c> lines (#69: a muted player says so, with the level it goes back to).</summary>
public sealed class PlayerStatusTextTests
{
    private static PlayerStatus Playing(float volume, bool muted)
        => new(PlayerState.Playing, 12, 300, volume, null, 0, 3, new QueueItem("/music/a.flac", "Blue in Green", "Miles Davis"), null, false, null, null,
            Muted: muted);

    [Fact]
    public void A_muted_player_shows_its_level_and_that_it_is_muted()
    {
        var lines = PlayerSupport.Describe(Playing(0.8f, muted: true)).ToList();
        Assert.Contains(lines, l => l.EndsWith("volume 80% (muted)", StringComparison.Ordinal));
    }

    [Fact]
    public void An_unmuted_player_shows_just_its_level()
    {
        var lines = PlayerSupport.Describe(Playing(0.8f, muted: false)).ToList();
        Assert.Contains(lines, l => l.EndsWith("volume 80%", StringComparison.Ordinal));
        Assert.DoesNotContain(lines, l => l.Contains("muted", StringComparison.Ordinal));
    }
}
