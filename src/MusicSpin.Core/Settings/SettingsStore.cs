using System.Text.Json;
using MusicSpin.Core.Hosting;

namespace MusicSpin.Core.Settings;

/// <summary>Loads/saves <see cref="AppSettings"/> atomically in the data folder.</summary>
public sealed class SettingsStore(DataDirectory directory)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public string FilePath => directory.SettingsFile;

    /// <summary>Set when the last <see cref="Load"/> found an unreadable file and fell back to defaults.</summary>
    public string? LoadWarning { get; private set; }

    /// <summary>Returns the stored settings, or defaults when the file is missing or invalid (never throws for bad content).</summary>
    public AppSettings Load()
    {
        LoadWarning = null;
        if (Directory.Exists(FilePath))
        {
            LoadWarning = $"Ignoring settings: '{FilePath}' is a folder, not a file.";
            return new AppSettings();
        }

        if (!File.Exists(FilePath))
        {
            return new AppSettings();
        }

        try
        {
            using var stream = SharedFile.OpenRead(FilePath); // another process may be saving it right now
            return JsonSerializer.Deserialize<AppSettings>(stream, JsonOptions) ?? new AppSettings();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            LoadWarning = $"Ignoring unreadable settings file '{FilePath}': {ex.Message}";
            return new AppSettings();
        }
    }

    /// <summary>Writes via a temp file + rename so a crash never leaves a half-written settings file.</summary>
    /// <exception cref="MusicSpinException">DataDirectoryNotWritable.</exception>
    public void Save(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var temp = TempPath();
        try
        {
            Directory.CreateDirectory(directory.Path);
            File.WriteAllText(temp, JsonSerializer.Serialize(settings, JsonOptions));
            SharedFile.Replace(temp, FilePath); // a reader may have the old one open for a moment
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            TryDelete(temp);
            throw AppPaths.NotWritable(directory, ex.Message);
        }
    }

    /// <summary>
    /// Writes <paramref name="settings"/> only when no settings file exists yet; false when another process created one
    /// first (several processes may start on a fresh data folder at once, e.g. racing <c>play</c> commands and players).
    /// </summary>
    /// <exception cref="MusicSpinException">DataDirectoryNotWritable.</exception>
    public bool TryCreate(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var temp = TempPath();
        try
        {
            Directory.CreateDirectory(directory.Path);
            File.WriteAllText(temp, JsonSerializer.Serialize(settings, JsonOptions));
            if (AtomicFile.TryPublish(temp, FilePath))
            {
                return true;
            }

            TryDelete(temp);
            return false; // someone else was first; theirs are the same defaults
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            TryDelete(temp);
            throw AppPaths.NotWritable(directory, ex.Message);
        }
    }

    // Per process and call, so concurrent writers never share (or fight over) one temp file.
    private string TempPath() => $"{FilePath}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // best effort
        }
    }
}
