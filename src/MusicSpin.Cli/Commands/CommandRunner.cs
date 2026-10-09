using System.CommandLine;
using MusicSpin.Cli.Output;
using MusicSpin.Core;

namespace MusicSpin.Cli.Commands;

/// <summary>Shared error handling: maps exceptions to exit codes and JSON/text errors.</summary>
internal sealed class CommandRunner(CliEnvironment env, Option<bool> json)
{
    public CliEnvironment Env { get; } = env;

    public void SetAction(Command command, Func<ParseResult, bool, CancellationToken, Task<int>> body)
    {
        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var asJson = parseResult.GetValue(json);
            try
            {
                return await body(parseResult, asJson, cancellationToken).ConfigureAwait(false);
            }
            catch (MusicSpinException ex)
            {
                return Fail(asJson, command.Name, ExitCodes.From(ex.Code), ex.Message);
            }
            catch (OperationCanceledException)
            {
                return Fail(asJson, command.Name, ExitCodes.Cancelled, "Cancelled.");
            }
            catch (Exception ex)
            {
                return Fail(asJson, command.Name, ExitCodes.Error, $"{ex.GetType().Name}: {ex.Message}");
            }
        });
    }

    public int Fail(bool asJson, string command, int exitCode, string message)
    {
        if (asJson)
        {
            JsonOutput.WriteError(Env.Out, command, exitCode, message);
        }
        else
        {
            Env.Error.WriteLine($"musicspin {command}: {message}");
        }

        return exitCode;
    }
}
