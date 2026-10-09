using System.CommandLine;
using MusicSpin.Cli.Output;

namespace MusicSpin.Cli.Commands;

internal static class DevicesCommand
{
    public static Command Create(CommandRunner runner)
    {
        var backend = Options.Backend();
        var command = new Command("devices", "List audio output devices (id, name, default, native formats).") { backend };

        runner.SetAction(command, (parseResult, json, _) =>
        {
            var provider = runner.Env.OutputProviders(parseResult.GetValue(backend)!);
            try
            {
                var devices = provider.GetDevices();
                if (json)
                {
                    JsonOutput.WriteSuccess(runner.Env.Out, "devices", new DevicesDto(provider.Name, devices));
                    return Task.FromResult(ExitCodes.Success);
                }

                runner.Env.Out.WriteLine($"Backend: {provider.Name}");
                if (devices.Count == 0)
                {
                    runner.Env.Out.WriteLine("  (no playback devices)");
                }

                foreach (var d in devices)
                {
                    runner.Env.Out.WriteLine($"  {d.Id,3}  {(d.IsDefault ? "*" : " ")} {d.Name}");
                }

                return Task.FromResult(ExitCodes.Success);
            }
            finally
            {
                (provider as IDisposable)?.Dispose();
            }
        });

        return command;
    }
}
