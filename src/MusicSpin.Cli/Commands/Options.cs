using System.CommandLine;

namespace MusicSpin.Cli.Commands;

/// <summary>Option factories shared by several commands (each command needs its own instance).</summary>
internal static class Options
{
    public static Option<string> Backend() => new Option<string>("--backend")
    {
        Description = "Audio output backend: 'auto' (SoundFlow/miniaudio: WASAPI, CoreAudio, PulseAudio/ALSA) or 'null' (no sound card, decode only).",
        DefaultValueFactory = _ => "auto",
    }.AcceptOnlyFromAmong("auto", "null");

    public static Option<string?> Device() => new("--device")
    {
        Description = "Output device id (from 'musicspin devices') or part of its name. Default: system default device.",
    };

    public static Option<bool> Exclusive() => new("--exclusive")
    {
        Description = "Open the device exclusively (WASAPI exclusive / ALSA hw / CoreAudio) for bit-perfect output.",
    };
}
