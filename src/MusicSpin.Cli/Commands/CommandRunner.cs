using System.CommandLine;
using MusicSpin.Cli.Output;
using MusicSpin.Core;
using MusicSpin.Core.Hosting;
using MusicSpin.Core.Library;
using MusicSpin.Core.Settings;

namespace MusicSpin.Cli.Commands;

/// <summary>Shared error handling: maps exceptions to exit codes and JSON/text errors.</summary>
internal sealed class CommandRunner(CliEnvironment env, Option<bool> json, Option<string?> dataDir)
{
    public CliEnvironment Env { get; } = env;

    /// <summary>Resolves the data folder: --data-dir, then MUSICSPIN_DATA_DIR, then &lt;exe dir&gt;/.usrdata.</summary>
    public DataDirectory DataDirectory(ParseResult parseResult)
        => AppPaths.Resolve(parseResult.GetValue(dataDir), Env.GetEnvironmentVariable, Env.BaseDirectory);

    /// <summary>Opens the library for writing; creates the data folder and a default settings file on first use.</summary>
    public MusicLibrary OpenLibraryForWrite(ParseResult parseResult)
    {
        var dir = DataDirectory(parseResult);
        var library = MusicLibrary.OpenWritable(dir, Env.Decoders, Env.LibraryLockTimeout, Env.Time);
        try
        {
            EnsureSettings(dir);
        }
        catch
        {
            library.Dispose();
            throw;
        }

        return library;
    }

    /// <summary>Opens the library for queries (missing database = empty; read-only folder = read-only).</summary>
    public MusicLibrary OpenLibraryForRead(ParseResult parseResult)
        => MusicLibrary.OpenForRead(DataDirectory(parseResult), Env.LibraryLockTimeout);

    /// <summary>Writes default settings when none exist. A corrupt file is left alone (and reported on stderr).</summary>
    public (AppSettings Settings, string? Warning, bool Created) EnsureSettings(DataDirectory dir)
    {
        var store = new SettingsStore(dir);
        var settings = store.Load();
        var created = false;
        if (!File.Exists(store.FilePath))
        {
            created = store.TryCreate(settings);
        }

        if (store.LoadWarning is { } warning)
        {
            Env.Error.WriteLine($"music-spin: warning: {warning}");
        }

        return (settings, store.LoadWarning, created);
    }

    public void SetAction(Command command, Func<ParseResult, bool, CancellationToken, Task<int>> body, string? name = null)
    {
        var commandName = name ?? command.Name;
        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var asJson = parseResult.GetValue(json);
            try
            {
                return await body(parseResult, asJson, cancellationToken).ConfigureAwait(false);
            }
            catch (PlayerRequestException ex)
            {
                return Fail(asJson, commandName, ex.ExitCode, ex.Message);
            }
            catch (MusicSpinException ex)
            {
                return Fail(asJson, commandName, ExitCodes.From(ex.Code), ex.Message);
            }
            catch (OperationCanceledException)
            {
                return Fail(asJson, commandName, ExitCodes.Cancelled, "Cancelled.");
            }
            catch (Exception ex)
            {
                return Fail(asJson, commandName, ExitCodes.Error, $"{ex.GetType().Name}: {ex.Message}");
            }
        });
    }

    public int Fail(bool asJson, string command, int exitCode, string message, object? data = null)
    {
        if (asJson)
        {
            JsonOutput.WriteError(Env.Out, command, exitCode, message, data);
        }
        else
        {
            Env.Error.WriteLine($"music-spin {command}: {message}");
        }

        return exitCode;
    }
}
