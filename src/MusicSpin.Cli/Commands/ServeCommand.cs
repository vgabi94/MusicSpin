using System.CommandLine;
using MusicSpin.Cli.Serve;
using MusicSpin.Core.Audio;
using MusicSpin.Core.Playback;

namespace MusicSpin.Cli.Commands;

internal static class ServeCommand
{
    public static Command Create(CommandRunner runner)
    {
        var backend = Options.Backend();
        var device = Options.Device();
        var exclusive = Options.Exclusive();
        var command = new Command("serve",
            "Run a long-lived player controlled by JSON lines on stdin; responses/events are JSON lines on stdout. " +
            "Same protocol as the background player: {\"id\":1,\"cmd\":\"load\",\"path\":\"a.flac\"} with cmd = " +
            "hello|status|load(path)|play(index?)|pause|resume|toggle|stop|next|prev|seek(seconds|delta)|volume(value|delta)|info(path)|" +
            "queue|queue.set(items)|queue.add(items)|queue.clear|repeat(mode)|shuffle(mode)|normalize(mode,target,preamp)|radio(filter)|quit.")
        {
            backend, device, exclusive,
        };

        runner.SetAction(command, async (parseResult, _, cancellationToken) =>
        {
            var env = runner.Env;
            var provider = env.OutputProviders(parseResult.GetValue(backend)!);
            var options = new PlayerOptions { Output = new AudioOutputOptions(parseResult.GetValue(device), parseResult.GetValue(exclusive)) };
            using var player = new Player(env.Decoders, provider, options);
            try
            {
                var server = new JsonLinesServer(player, env.Decoders, env.In, env.Out,
                    new OutputInfo(provider.Name, parseResult.GetValue(device), parseResult.GetValue(exclusive)))
                {
                    RadioPool = new MusicSpin.Core.Library.RadioPoolCache(runner.DataDirectory(parseResult), env.LibraryLockTimeout).Get,
                };
                await server.RunAsync(cancellationToken).ConfigureAwait(false);
                return ExitCodes.Success;
            }
            finally
            {
                player.Dispose();
                (provider as IDisposable)?.Dispose();
            }
        });

        return command;
    }
}
