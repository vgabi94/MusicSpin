namespace MusicSpin.Core.Playback;

/// <summary>What happens when a track or the queue ends. Serialized as "off" | "one" | "all".</summary>
public enum RepeatMode
{
    /// <summary>Play the queue once; <c>next</c> on the last entry / <c>prev</c> on the first are errors.</summary>
    Off,

    /// <summary>A track that ends naturally starts again. <c>next</c>/<c>prev</c> still move (with the same limits as <see cref="Off"/>).</summary>
    One,

    /// <summary>The queue loops: after the last entry comes the first, and <c>next</c>/<c>prev</c> wrap around.</summary>
    All,
}

/// <summary>Parsing of user input for repeat and shuffle modes (CLI, protocol, MCP, settings).</summary>
public static class PlaybackModes
{
    public const string RepeatValues = "off|one|all";

    public static bool TryParseRepeat(string? text, out RepeatMode mode)
    {
        switch (text?.Trim().ToLowerInvariant())
        {
            case "off" or "none" or "no":
                mode = RepeatMode.Off;
                return true;
            case "one" or "track" or "single":
                mode = RepeatMode.One;
                return true;
            case "all" or "queue":
                mode = RepeatMode.All;
                return true;
            default:
                mode = RepeatMode.Off;
                return false;
        }
    }

    public static string Name(RepeatMode mode) => mode switch
    {
        RepeatMode.One => "one",
        RepeatMode.All => "all",
        _ => "off",
    };

    /// <summary>Parses on|off|toggle (and true/false/1/0/yes/no). Toggle resolves against <paramref name="current"/>.</summary>
    public static bool TryParseShuffle(string? text, bool current, out bool enabled)
    {
        switch (text?.Trim().ToLowerInvariant())
        {
            case "on" or "true" or "1" or "yes":
                enabled = true;
                return true;
            case "off" or "false" or "0" or "no":
                enabled = false;
                return true;
            case "toggle":
                enabled = !current;
                return true;
            default:
                enabled = current;
                return false;
        }
    }
}
