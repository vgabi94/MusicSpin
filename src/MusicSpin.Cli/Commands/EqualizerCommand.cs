using System.CommandLine;
using System.Globalization;
using System.Text.Json;
using MusicSpin.Core;
using MusicSpin.Core.Equalization;
using MusicSpin.Core.Remote;

namespace MusicSpin.Cli.Commands;

/// <summary>
/// <c>music-spin eq</c>: the background player's 10-band graphic equalizer (#141). Changes are saved as the defaults in
/// settings.json, like <c>normalize</c>.
/// </summary>
internal static class EqualizerCommand
{
    public static Command Create(CommandRunner runner)
    {
        var command = new Command("eq",
            "Show or change the 10-band graphic equalizer (31 Hz .. 16 kHz, -12..+12 dB per band). The pre-amp is lowered automatically so boosts " +
            "never clip or play louder than the original; off or flat leaves the samples untouched (bit-perfect). Changes fade in without clicks " +
            "and are saved as the default in settings.json. Without a subcommand: show it. No running player: exit 13.");
        Action(runner, command, "eq", _ => null);

        var show = new Command("show", "Show the equalizer: on/off, preset, pre-amp, headroom and the gain of every band.");
        Action(runner, show, "eq show", _ => null);
        command.Subcommands.Add(show);

        var on = new Command("on", "Turn the equalizer on with its current gains.");
        Action(runner, on, "eq on", _ => new Request(new { enabled = true }));
        command.Subcommands.Add(on);

        var off = new Command("off", "Turn the equalizer off (bit-perfect pass-through); the gains are kept.");
        Action(runner, off, "eq off", _ => new Request(new { enabled = false }));
        command.Subcommands.Add(off);

        var band = new Argument<string>("band") { Description = $"1..{EqualizerSettings.BandCount}, or a centre frequency: {BandList}." };
        var gain = new Argument<double>("db") { Description = $"Gain in dB, {EqualizerSettings.MinGainDb} to {EqualizerSettings.MaxGainDb}." };
        var set = new Command("set", "Set one band's gain (turns the gains custom unless they match a preset). Example: eq set 1k -3, eq set 2 +4.") { band, gain };
        set.Validators.Add(r =>
        {
            if (Options.Get(r, band) is { } text && !EqualizerSettings.TryParseBand(text, out _))
            {
                r.AddError($"Unknown band '{text}'. Use 1..{EqualizerSettings.BandCount} or one of: {BandList}.");
            }

            if (r.GetValue(gain) is var g && !(double.IsFinite(g) && g is >= EqualizerSettings.MinGainDb and <= EqualizerSettings.MaxGainDb))
            {
                r.AddError($"The gain must be between {EqualizerSettings.MinGainDb} and {EqualizerSettings.MaxGainDb} dB.");
            }
        });
        Action(runner, set, "eq set", p => new Request(new { band = p.GetValue(band), gain = p.GetValue(gain) }));
        command.Subcommands.Add(set);

        var preampDb = new Argument<double>("db") { Description = $"Pre-amp in dB, {EqualizerSettings.MinPreampDb} to {EqualizerSettings.MaxPreampDb}." };
        var preamp = new Command("preamp", "Set the pre-amp (automatic headroom still keeps boosts from clipping).") { preampDb };
        preamp.Validators.Add(r =>
        {
            if (r.GetValue(preampDb) is var p && !(double.IsFinite(p) && p is >= EqualizerSettings.MinPreampDb and <= EqualizerSettings.MaxPreampDb))
            {
                r.AddError($"The pre-amp must be between {EqualizerSettings.MinPreampDb} and {EqualizerSettings.MaxPreampDb} dB.");
            }
        });
        Action(runner, preamp, "eq preamp", p => new Request(new { preamp = p.GetValue(preampDb) }));
        command.Subcommands.Add(preamp);

        var presets = new Command("preset", "Equalizer presets: list, apply, save, delete.");
        var list = new Command("list", "List the presets: the built-in ones (flat, rock, pop, jazz, classical, bass-boost, treble-boost, vocal, loudness, ...) and yours.");
        Action(runner, list, "eq preset list", _ => null, presetsOnly: true);
        presets.Subcommands.Add(list);

        var applyName = new Argument<string>("name") { Description = "The preset's name." };
        var apply = new Command("apply", "Apply a preset (turns the equalizer on).") { applyName };
        Action(runner, apply, "eq preset apply", p => new Request(new { preset = p.GetValue(applyName) }));
        presets.Subcommands.Add(apply);

        var saveName = new Argument<string>("name") { Description = $"A name (at most {EqualizerSettings.MaxPresetNameLength} characters, not a built-in one)." };
        var save = new Command("save", "Save the current gains as a preset of yours (replaces one of that name).") { saveName };
        save.Validators.Add(r =>
        {
            if (Options.Get(r, saveName) is { } text && EqualizerSettings.CheckPresetName(text) is { } problem)
            {
                r.AddError(problem);
            }
        });
        Action(runner, save, "eq preset save", p => new Request(new { save = p.GetValue(saveName) }, Presets: true));
        presets.Subcommands.Add(save);

        var deleteName = new Argument<string>("name") { Description = "One of your presets." };
        var delete = new Command("delete", "Delete a preset of yours.") { deleteName };
        Action(runner, delete, "eq preset delete", p => new Request(new { delete = p.GetValue(deleteName) }, Presets: true));
        presets.Subcommands.Add(delete);
        command.Subcommands.Add(presets);
        return command;
    }

    private static string BandList => string.Join(", ", Enumerable.Range(0, EqualizerSettings.BandCount).Select(EqualizerSettings.BandName));

    /// <summary>A protocol <c>eq</c> request; <paramref name="Presets"/>: it changes the saved presets.</summary>
    private sealed record Request(object Body, bool Presets = false);

    private static void Action(CommandRunner runner, Command command, string name, Func<ParseResult, Request?> request, bool presetsOnly = false)
    {
        runner.SetAction(command, async (parseResult, json, cancellationToken) =>
        {
            var r = request(parseResult);
            await using var client = await PlayerSupport.ConnectAsync(runner, parseResult, cancellationToken).ConfigureAwait(false);
            var data = await client.SendAsync("eq", r?.Body, cancellationToken).ConfigureAwait(false);
            var warning = r is null ? null : PlayerSupport.SaveEqualizer(runner, parseResult, data, r.Presets);
            if (json)
            {
                PlayerSupport.WriteStatus(runner, json, name, data, new Dictionary<string, object?>
                {
                    ["warnings"] = warning is null ? null : new[] { warning },
                });
                return ExitCodes.Success;
            }

            if (warning is not null)
            {
                runner.Env.Error.WriteLine($"music-spin {name}: warning: {warning}");
            }

            var status = ProtocolJson.Deserialize<PlayerStatus>(data)?.Equalizer
                ?? throw new MusicSpinException(MusicSpinErrorCode.InvalidState, "This player has no equalizer.");
            foreach (var line in presetsOnly ? PresetLines(status) : Lines(status))
            {
                runner.Env.Out.WriteLine(line);
            }

            return ExitCodes.Success;
        });
    }

    /// <summary>The equalizer, for people: a summary line, then one line per band.</summary>
    internal static IEnumerable<string> Lines(EqualizerStatus eq)
    {
        yield return Summary(eq);
        for (var i = 0; i < eq.Gains.Count && i < eq.Bands.Count; i++)
        {
            var g = eq.Gains[i];
            var bar = new string(g >= 0 ? '+' : '-', (int)Math.Round(Math.Abs(g)));
            yield return string.Create(CultureInfo.InvariantCulture, $"  {i + 1,2}  {EqualizerSettings.BandName(i),5} Hz  {g,6:+0.0;-0.0;0.0} dB  {bar}");
        }
    }

    internal static string Summary(EqualizerStatus eq)
        => string.Create(CultureInfo.InvariantCulture,
            $"Equalizer: {(eq.Enabled ? "on" : "off")} · {eq.Preset ?? "custom"}{(eq.PreampDb != 0 ? $" · pre-amp {eq.PreampDb:+0.#;-0.#} dB" : string.Empty)}{(eq.Enabled && eq.HeadroomDb > 0 ? $" · headroom -{eq.HeadroomDb:0.0} dB" : string.Empty)}");

    private static IEnumerable<string> PresetLines(EqualizerStatus eq)
    {
        foreach (var name in eq.Presets)
        {
            var gains = eq.UserPresets.TryGetValue(name, out var mine) ? mine : EqualizerSettings.BuiltIn(name)?.Gains ?? [];
            var tags = (name == eq.Preset ? " (current)" : string.Empty) + (eq.UserPresets.ContainsKey(name) ? " (yours)" : string.Empty);
            yield return string.Create(CultureInfo.InvariantCulture,
                $"{name,-14} {string.Join(" ", gains.Select(g => g.ToString("+0;-0;0", CultureInfo.InvariantCulture).PadLeft(3)))}{tags}");
        }
    }
}
