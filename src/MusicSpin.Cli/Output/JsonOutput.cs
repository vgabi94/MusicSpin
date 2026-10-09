using System.Text.Json;
using MusicSpin.Core.Remote;

namespace MusicSpin.Cli.Output;

/// <summary>
/// JSON conventions: one compact JSON document per line, camelCase, enums as camelCase strings,
/// null properties omitted. Envelope: {"ok":true,"command":"...","data":{...}} or
/// {"ok":false,"command":"...","error":{"code":"...","exitCode":N,"message":"..."}}.
/// </summary>
public static class JsonOutput
{
    public const int SchemaVersion = 1;

    public static JsonSerializerOptions Options => ProtocolJson.Options;

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

    public static void WriteSuccess<T>(TextWriter writer, string command, T data)
        => writer.WriteLine(Serialize(new SuccessEnvelope<T>(true, command, data)));

    public static void WriteError(TextWriter writer, string command, int exitCode, string message, object? data = null)
        => writer.WriteLine(Serialize(new ErrorEnvelope(false, command, new ErrorInfo(ExitCodes.Name(exitCode), exitCode, message), data)));

    public sealed record SuccessEnvelope<T>(bool Ok, string Command, T Data);

    public sealed record ErrorEnvelope(bool Ok, string Command, ErrorInfo Error, object? Data = null);

    public sealed record ErrorInfo(string Code, int ExitCode, string Message);
}
