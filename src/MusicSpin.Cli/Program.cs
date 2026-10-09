using MusicSpin.Cli;
using MusicSpin.Core.Remote;

if (args is [PlayerProcessLauncher.WindowsSpawnVerb, .. var spawn])
{
    // Internal (Windows): the short-lived helper that starts the detached background player. See PlayerProcessLauncher.
    return PlayerProcessLauncher.RunWindowsSpawnHelper(spawn, Console.Out);
}

return await CliApp.RunAsync(args, CliEnvironment.CreateDefault()).ConfigureAwait(false);
