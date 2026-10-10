using System.CommandLine;
using MusicSpin.Cli.Commands;
using MusicSpin.Cli.Output;

namespace MusicSpin.Cli;

/// <summary>Builds and runs the `music-spin` command tree. Fully in-process testable via <see cref="CliEnvironment"/>.</summary>
public static class CliApp
{
    public const string Description =
        "MusicSpin - hi-res music player CLI. Designed for humans and AI agents: every command accepts --json " +
        "(one JSON object per line on stdout, also for errors) and returns stable exit codes.";

    public static RootCommand BuildRootCommand(CliEnvironment env)
    {
        ArgumentNullException.ThrowIfNull(env);
        var json = new Option<bool>("--json")
        {
            Description = "Write machine-readable JSON to stdout (errors included) instead of text.",
            Recursive = true,
        };

        var dataDir = new Option<string?>("--data-dir")
        {
            Description = "Optional: use this folder itself as the data folder (settings, library, player files) instead of the default '.usrdata' next to the executable. Takes precedence over MUSICSPIN_DATA_DIR.",
            Recursive = true,
        };

        var runner = new CommandRunner(env, json, dataDir);
        var root = new RootCommand($"{Description}\n\n{ExitCodes.HelpText}") { json, dataDir };
        root.Add(InfoCommand.Create(runner));
        root.Add(FormatsCommand.Create(runner));
        root.Add(DevicesCommand.Create(runner));
        root.Add(PlayCommand.Create(runner));
        root.Add(RadioCommand.Create(runner));
        root.Add(ServeCommand.Create(runner));
        root.Add(McpCommand.Create(runner));
        root.Add(DoctorCommand.Create(runner));
        root.Add(LibraryCommand.Create(runner));
        root.Add(PlaylistCommands.Create(runner));
        foreach (var command in BrowseCommands.Create(runner))
        {
            root.Add(command);
        }

        foreach (var command in ControlCommands.Create(runner))
        {
            root.Add(command);
        }
        return root;
    }

    private static string CommandPath(System.CommandLine.Parsing.CommandResult result, RootCommand root)
    {
        var names = new List<string>();
        for (System.CommandLine.Parsing.SymbolResult? current = result; current is System.CommandLine.Parsing.CommandResult c && c.Command != root; current = current.Parent)
        {
            names.Insert(0, c.Command.Name);
        }

        return names.Count == 0 ? "music-spin" : string.Join(' ', names);
    }

    public static async Task<int> RunAsync(string[] args, CliEnvironment env, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(env);

        var root = BuildRootCommand(env);
        ParseResult parseResult;
        try
        {
            parseResult = root.Parse(args);
        }
        catch (InvalidOperationException ex)
        {
            // Safety net: a validator choked on an unconvertible value. Still a usage error, never a crash.
            if (args.Contains("--json", StringComparer.Ordinal))
            {
                JsonOutput.WriteError(env.Out, "music-spin", ExitCodes.Usage, ex.Message);
            }
            else
            {
                await env.Error.WriteLineAsync($"music-spin: {ex.Message}").ConfigureAwait(false);
            }

            return ExitCodes.Usage;
        }

        if (parseResult.Errors.Count > 0)
        {
            // Own the usage-error path so agents get exit code 2 and (with --json) a JSON error.
            var message = string.Join(" ", parseResult.Errors.Select(e => e.Message));
            var command = CommandPath(parseResult.CommandResult, root);
            if (args.Contains("--json", StringComparer.Ordinal))
            {
                JsonOutput.WriteError(env.Out, command, ExitCodes.Usage, message);
            }
            else
            {
                await env.Error.WriteLineAsync($"music-spin: {message}").ConfigureAwait(false);
                await env.Error.WriteLineAsync("Run 'music-spin --help' for usage.").ConfigureAwait(false);
            }

            return ExitCodes.Usage;
        }

        var config = new InvocationConfiguration { Output = env.Out, Error = env.Error };
        return await parseResult.InvokeAsync(config, cancellationToken).ConfigureAwait(false);
    }
}
