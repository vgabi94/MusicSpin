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
        Description = "Output device id (from 'music-spin devices') or part of its name. Default: system default device.",
    };

    public static Option<bool> Exclusive() => new("--exclusive")
    {
        Description = "Open the device exclusively (WASAPI exclusive / ALSA hw / CoreAudio) for bit-perfect output.",
    };

    public static Option<int> Limit() => Validated(new Option<int>("--limit")
    {
        Description = $"Page size (1-{MusicSpin.Core.Library.Paging.MaxLimit}, default {MusicSpin.Core.Library.Paging.DefaultLimit}).",
        DefaultValueFactory = _ => MusicSpin.Core.Library.Paging.DefaultLimit,
    }, v => v is < 1 or > MusicSpin.Core.Library.Paging.MaxLimit ? $"--limit must be between 1 and {MusicSpin.Core.Library.Paging.MaxLimit}." : null);

    public static Option<int> Offset() => Validated(new Option<int>("--offset")
    {
        Description = "Number of items to skip (default 0).",
    }, v => v < 0 ? "--offset must be 0 or greater." : null);

    public static Option<bool> Descending() => new("--desc") { Description = "Reverse the sort order." };

    public static Option<string> Sort(string defaultValue, params string[] values) => new Option<string>("--sort")
    {
        Description = $"Sort by: {string.Join(", ", values)} (default {defaultValue}).",
        DefaultValueFactory = _ => defaultValue,
    }.AcceptOnlyFromAmong(values);

    public static Option<T> Validated<T>(Option<T> option, Func<T, string?> validate)
    {
        ArgumentNullException.ThrowIfNull(option);
        option.Validators.Add(r =>
        {
            if (TryGetValue(r, out T? value) && validate(value!) is { } error)
            {
                r.AddError(error);
            }
        });
        return option;
    }

    /// <summary>
    /// Validators also run when the token could not be converted (e.g. "--limit abc"); the conversion error is
    /// already reported, so the validator must not throw.
    /// </summary>
    public static bool TryGetValue<T>(System.CommandLine.Parsing.OptionResult result, out T? value)
    {
        ArgumentNullException.ThrowIfNull(result);
        try
        {
            value = result.GetValueOrDefault<T>();
            return true;
        }
        catch (InvalidOperationException)
        {
            value = default;
            return false;
        }
    }

    /// <summary>Safe option read for command-level validators (unconvertible tokens yield the default).</summary>
    public static T? Get<T>(System.CommandLine.Parsing.CommandResult result, Option<T> option)
    {
        ArgumentNullException.ThrowIfNull(result);
        try
        {
            return result.GetValue(option);
        }
        catch (InvalidOperationException)
        {
            return default;
        }
    }

    /// <inheritdoc cref="Get{T}(System.CommandLine.Parsing.CommandResult, Option{T})"/>
    public static T? Get<T>(System.CommandLine.Parsing.CommandResult result, Argument<T> argument)
    {
        ArgumentNullException.ThrowIfNull(result);
        try
        {
            return result.GetValue(argument);
        }
        catch (InvalidOperationException)
        {
            return default;
        }
    }
}
