using System.CommandLine;
using System.Reflection;
using System.Runtime.InteropServices;
using MusicSpin.Cli.Output;
using MusicSpin.Core;

namespace MusicSpin.Cli.Commands;

internal static class DoctorCommand
{
    public static Command Create(CommandRunner runner)
    {
        var backend = Options.Backend();
        var command = new Command("doctor",
            "Check the runtime environment (FFmpeg libraries, audio backend). Exit 6 if FFmpeg is missing, 7 if no audio backend.")
        {
            backend,
        };

        runner.SetAction(command, (parseResult, json, _) =>
        {
            var env = runner.Env;
            var decoder = DecoderDto.From(env.FFmpegStatus());
            var provider = env.OutputProviders(parseResult.GetValue(backend)!);
            string? audioBackend = null;
            string? audioError = null;
            var deviceCount = 0;
            try
            {
                deviceCount = provider.GetDevices().Count;
                audioBackend = provider.Name;
            }
            catch (MusicSpinException ex)
            {
                audioError = ex.Message;
            }
            finally
            {
                (provider as IDisposable)?.Dispose();
            }

            var version = typeof(CliApp).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";
            var report = new DoctorDto(version, RuntimeInformation.FrameworkDescription,
                $"{RuntimeInformation.OSDescription} ({RuntimeInformation.RuntimeIdentifier})",
                decoder, audioBackend, audioError, deviceCount);

            var exitCode = !decoder.Available ? ExitCodes.DecoderUnavailable
                : audioError is not null ? ExitCodes.AudioDeviceError
                : ExitCodes.Success;

            if (json)
            {
                if (exitCode == ExitCodes.Success)
                {
                    JsonOutput.WriteSuccess(env.Out, "doctor", report);
                }
                else
                {
                    // The report is still useful when unhealthy, so it rides along as "data".
                    JsonOutput.WriteError(env.Out, "doctor", exitCode, decoder.Error ?? audioError ?? "unhealthy", report);
                }

                return Task.FromResult(exitCode);
            }

            env.Out.WriteLine($"musicspin {report.Version} on {report.Runtime}, {report.Os}");
            env.Out.WriteLine(decoder.Available
                ? $"  [ok]   FFmpeg {decoder.Version}, license: {decoder.License}{(decoder.Lgpl == true ? "" : " (not LGPL: fine locally, don't redistribute)")}"
                : $"  [fail] FFmpeg: {decoder.Error}");
            env.Out.WriteLine(audioError is null
                ? $"  [ok]   Audio: {audioBackend}, {deviceCount} playback device(s)"
                : $"  [fail] Audio: {audioError}");
            return Task.FromResult(exitCode);
        });

        return command;
    }
}
