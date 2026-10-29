using System.Globalization;
using MusicSpin.Core.Hosting;

namespace MusicSpin.App.Services;

/// <summary>
/// The desktop app's own log, <c>app.log</c> in the data folder (#185): failures nobody planned for, with their stack
/// traces, so an error notice or a crash leaves a trail. <c>player.log</c> belongs to the background player. Writing
/// never throws: a log that can't be written is skipped.
/// </summary>
public sealed class AppLog
{
    public const string FileName = "app.log";

    /// <summary>Past this size the log moves to <c>app.log.1</c> (replacing the older one) before the next line.</summary>
    public const long MaxBytes = 1024 * 1024;

    private readonly TimeProvider _time;
    private readonly Lock _gate = new();

    public AppLog(string? file, TimeProvider? time = null)
    {
        File = file;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>Logs nothing (tests, previews).</summary>
    public static AppLog None { get; } = new(null);

    /// <summary>The log file, or null for <see cref="None"/>.</summary>
    public string? File { get; }

    public static AppLog For(DataDirectory directory, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(directory);
        return new AppLog(Path.Combine(directory.Path, FileName), time);
    }

    public void Write(string message, Exception? exception = null)
    {
        if (File is not { } file)
        {
            return;
        }

        var stamp = _time.GetUtcNow().ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
        var text = $"{stamp} [{Environment.ProcessId}] {message}{Environment.NewLine}"
            + (exception is null ? string.Empty : exception + Environment.NewLine);
        try
        {
            lock (_gate)
            {
                var info = new FileInfo(file);
                if (info.Exists && info.Length > MaxBytes)
                {
                    System.IO.File.Move(file, file + ".1", overwrite: true);
                }

                System.IO.File.AppendAllText(file, text);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // the log must never take the app down
        }
    }
}
