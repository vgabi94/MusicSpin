using System.CommandLine;
using MusicSpin.Cli.Commands;
using MusicSpin.Cli.Output;

namespace MusicSpin.Cli;

/// <summary>Builds and runs the `musicspin` command tree. Fully in-process testable via <see cref="CliEnvironment"/>.</summary>
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

        var runner = new CommandRunner(env, json);
        var root = new RootCommand($"{Description}\n\n{ExitCodes.HelpText}") { json };
        root.Add(InfoCommand.Create(runner));
        root.Add(FormatsCommand.Create(runner));
        root.Add(DevicesCommand.Create(runner));
        root.Add(PlayCommand.Create(runner));
        root.Add(ServeCommand.Create(runner));
        root.Add(DoctorCommand.Create(runner));
        return root;
    }

    public static async Task<int> RunAsync(string[] args, CliEnvironment env, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(env);

        var root = BuildRootCommand(env);
        var parseResult = root.Parse(args);

        if (parseResult.Errors.Count > 0)
        {
            // Own the usage-error path so agents get exit code 2 and (with --json) a JSON error.
            var message = string.Join(" ", parseResult.Errors.Select(e => e.Message));
            var command = parseResult.CommandResult.Command == root ? "musicspin" : parseResult.CommandResult.Command.Name;
            if (args.Contains("--json", StringComparer.Ordinal))
            {
                JsonOutput.WriteError(env.Out, command, ExitCodes.Usage, message);
            }
            else
            {
                await env.Error.WriteLineAsync($"musicspin: {message}").ConfigureAwait(false);
                await env.Error.WriteLineAsync("Run 'musicspin --help' for usage.").ConfigureAwait(false);
            }

            return ExitCodes.Usage;
        }

        var config = new InvocationConfiguration { Output = env.Out, Error = env.Error };
        return await parseResult.InvokeAsync(config, cancellationToken).ConfigureAwait(false);
    }
}
