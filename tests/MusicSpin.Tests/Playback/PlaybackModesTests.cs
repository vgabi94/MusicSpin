using MusicSpin.Core.Playback;

namespace MusicSpin.Tests.Playback;

public class PlaybackModesTests
{
    [Theory]
    [InlineData("off", RepeatMode.Off)]
    [InlineData(" NONE ", RepeatMode.Off)]
    [InlineData("no", RepeatMode.Off)]
    [InlineData("one", RepeatMode.One)]
    [InlineData("Track", RepeatMode.One)]
    [InlineData("single", RepeatMode.One)]
    [InlineData("ALL", RepeatMode.All)]
    [InlineData("queue", RepeatMode.All)]
    public void Repeat_accepts_the_documented_names_and_synonyms_in_any_case(string text, RepeatMode expected)
    {
        Assert.True(PlaybackModes.TryParseRepeat(text, out var mode));
        Assert.Equal(expected, mode);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("forever")]
    [InlineData("1")]
    public void Unknown_repeat_values_are_rejected_and_default_to_off(string? text)
    {
        Assert.False(PlaybackModes.TryParseRepeat(text, out var mode));
        Assert.Equal(RepeatMode.Off, mode);
    }

    [Theory]
    [InlineData(RepeatMode.Off, "off")]
    [InlineData(RepeatMode.One, "one")]
    [InlineData(RepeatMode.All, "all")]
    [InlineData((RepeatMode)42, "off")]
    public void Repeat_names_round_trip(RepeatMode mode, string name)
    {
        Assert.Equal(name, PlaybackModes.Name(mode));
        if (Enum.IsDefined(mode))
        {
            Assert.True(PlaybackModes.TryParseRepeat(name, out var parsed));
            Assert.Equal(mode, parsed);
        }
    }

    [Theory]
    [InlineData("on", false, true)]
    [InlineData("TRUE", false, true)]
    [InlineData("1", false, true)]
    [InlineData(" yes ", false, true)]
    [InlineData("off", true, false)]
    [InlineData("false", true, false)]
    [InlineData("0", true, false)]
    [InlineData("No", true, false)]
    [InlineData("toggle", false, true)]
    [InlineData("toggle", true, false)]
    public void Shuffle_accepts_switches_and_toggle_relative_to_the_current_state(string text, bool current, bool expected)
    {
        Assert.True(PlaybackModes.TryParseShuffle(text, current, out var enabled));
        Assert.Equal(expected, enabled);
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("random", true)]
    [InlineData("", false)]
    public void Unknown_shuffle_values_are_rejected_and_keep_the_current_state(string? text, bool current)
    {
        Assert.False(PlaybackModes.TryParseShuffle(text, current, out var enabled));
        Assert.Equal(current, enabled);
    }
}
