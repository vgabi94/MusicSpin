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
            using var stream = File.OpenRead(FilePath);
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
        var temp = FilePath + ".tmp";
        try
        {
            Directory.CreateDirectory(directory.Path);
            File.WriteAllText(temp, JsonSerializer.Serialize(settings, JsonOptions));
            File.Move(temp, FilePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            try
            {
                File.Delete(temp);
            }
            catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException)
            {
                // best effort
            }

            throw AppPaths.NotWritable(directory, ex.Message);
        }
    }
}
