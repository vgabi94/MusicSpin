using System.Text.Json;
using MusicSpin.Core.Hosting;
using MusicSpin.Core.Remote;
using MusicSpin.Core.Settings;

namespace MusicSpin.Core.Library;

/// <summary>A station started lately (<see cref="StationStore.Recent"/>).</summary>
public sealed record RecentStation(RadioFilter Filter, string Description, DateTimeOffset StartedAt);

/// <summary>A station saved under a name (<see cref="StationStore.Saved"/>).</summary>
public sealed record SavedStation(string Name, RadioFilter Filter, string Description, DateTimeOffset SavedAt);

/// <summary>
/// Recent and saved radio stations (#38), in <c>stations.json</c> in the data folder. Stations are filters (names, genres,
/// years, album ids), never file paths, so the file travels with a portable data folder. The background player records a
/// station in the recent list each time one starts (from the CLI, MCP or the app); the CLI saves, lists and deletes them.
/// <para>Every change is read-modify-write under <c>stations.lock</c>, so the player recording a station and a CLI saving
/// one at the same moment can't lose each other's change; the file itself is replaced atomically (temp file + rename).
/// A file that can't be read is set aside as <c>stations.json.bad</c> on the next change, and reads as empty meanwhile
/// (<see cref="LoadWarning"/> says so).</para>
/// </summary>
public sealed class StationStore
{
    /// <summary>How many recent stations are kept (the Radio page's Recent stations shows six).</summary>
    public const int RecentLimit = 6;

    public const int MaxNameLength = 100;

    public const int MaxSaved = 500;

    private const int Version = 1;
    private static readonly TimeSpan DefaultLockTimeout = TimeSpan.FromSeconds(5);

    private readonly DataDirectory _dir;
    private readonly TimeProvider _time;
    private readonly TimeSpan _lockTimeout;
    private readonly Action<int>? _onLockRetry;

    /// <param name="onLockRetry">Test hook: called with the attempt number instead of waiting before a lock retry.</param>
    public StationStore(DataDirectory dir, TimeProvider? time = null, TimeSpan? lockTimeout = null, Action<int>? onLockRetry = null)
    {
        _dir = dir ?? throw new ArgumentNullException(nameof(dir));
        _time = time ?? TimeProvider.System;
        _lockTimeout = lockTimeout ?? DefaultLockTimeout;
        _onLockRetry = onLockRetry;
    }

    public string FilePath => _dir.StationsFile;

    private string LockPath => FilePath + ".lock";

    /// <summary>Set when the last read found an unreadable file (it then reads as empty).</summary>
    public string? LoadWarning { get; private set; }

    /// <summary>The recent stations, newest first, without duplicates (at most <see cref="RecentLimit"/>).</summary>
    public IReadOnlyList<RecentStation> Recent() => Read().Recent.Select(r => new RecentStation(r.Filter!, r.Filter!.Describe(), r.StartedAt)).ToList();

    /// <summary>The saved stations, by name.</summary>
    public IReadOnlyList<SavedStation> Saved()
        => Read().Saved.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase).Select(ToSaved).ToList();

    /// <summary>The recent station <paramref name="number"/> (1 = newest). NotFound when there is no such station.</summary>
    public RecentStation GetRecent(int number)
    {
        var recent = Recent();
        if (number < 1)
        {
            throw new MusicSpinException(MusicSpinErrorCode.InvalidArgument, $"Recent station numbers start at 1 (got {number}).");
        }

        return number <= recent.Count ? recent[number - 1]
            : throw new MusicSpinException(MusicSpinErrorCode.NotFound, recent.Count == 0
                ? "No recent radio stations yet: start one with 'music-spin radio'."
                : $"There are only {recent.Count} recent radio stations (see 'music-spin radio list').");
    }

    /// <summary>The saved station named <paramref name="name"/> (case-insensitive). NotFound when there is none.</summary>
    public SavedStation Get(string name)
    {
        var key = CheckName(name);
        return Read().Saved.FirstOrDefault(s => Same(s.Name, key)) is { } found ? ToSaved(found)
            : throw new MusicSpinException(MusicSpinErrorCode.NotFound, $"No saved radio station named '{key}' (see 'music-spin radio list').");
    }

    /// <summary>
    /// Puts <paramref name="filter"/> first in the recent list: a station already there (the same filters, in any order and
    /// case) moves up instead of appearing twice, and the oldest beyond <see cref="RecentLimit"/> drop off.
    /// </summary>
    /// <exception cref="MusicSpinException">InvalidArgument (bad filter); DataDirectoryNotWritable; InvalidState (locked).</exception>
    public void AddRecent(RadioFilter filter)
    {
        ArgumentNullException.ThrowIfNull(filter);
        filter.EnsureValid();
        var key = Key(filter);
        Change(file =>
        {
            file.Recent.RemoveAll(r => Key(r.Filter!) == key);
            file.Recent.Insert(0, new RecentEntry { Filter = filter, StartedAt = _time.GetUtcNow() });
            if (file.Recent.Count > RecentLimit)
            {
                file.Recent.RemoveRange(RecentLimit, file.Recent.Count - RecentLimit);
            }
        });
    }

    /// <summary>
    /// Saves <paramref name="filter"/> as <paramref name="name"/>. InvalidState when the name is taken (case-insensitive)
    /// and <paramref name="replace"/> is false; InvalidArgument for an empty or too long name, or a bad filter.
    /// </summary>
    public SavedStation Save(string name, RadioFilter filter, bool replace = false)
    {
        var key = CheckName(name);
        ArgumentNullException.ThrowIfNull(filter);
        filter.EnsureValid();
        SavedEntry? saved = null;
        Change(file =>
        {
            var existing = file.Saved.FindIndex(s => Same(s.Name, key));
            if (existing >= 0 && !replace)
            {
                throw new MusicSpinException(MusicSpinErrorCode.InvalidState,
                    $"A radio station named '{file.Saved[existing].Name}' is already saved (names are case-insensitive). Pick another name or replace it.");
            }

            if (existing < 0 && file.Saved.Count >= MaxSaved)
            {
                throw new MusicSpinException(MusicSpinErrorCode.InvalidState, $"At most {MaxSaved} radio stations can be saved; delete one first.");
            }

            saved = new SavedEntry { Name = key, Filter = filter, SavedAt = _time.GetUtcNow() };
            if (existing >= 0)
            {
                file.Saved[existing] = saved;
            }
            else
            {
                file.Saved.Add(saved);
            }
        });
        return ToSaved(saved!);
    }

    /// <summary>Deletes the saved station <paramref name="name"/>. NotFound when there is none.</summary>
    public SavedStation Delete(string name)
    {
        var key = CheckName(name);
        SavedEntry? removed = null;
        Change(file =>
        {
            var index = file.Saved.FindIndex(s => Same(s.Name, key));
            if (index < 0)
            {
                throw new MusicSpinException(MusicSpinErrorCode.NotFound, $"No saved radio station named '{key}' (see 'music-spin radio list').");
            }

            removed = file.Saved[index];
            file.Saved.RemoveAt(index);
        });
        return ToSaved(removed!);
    }

    /// <summary>
    /// The same station whatever the order or case of its values: lists are sorted case-insensitively before comparing.
    /// </summary>
    internal static string Key(RadioFilter f)
    {
        static IReadOnlyList<string>? S(IReadOnlyList<string>? list)
            => list is { Count: > 0 } ? [.. list.Select(v => v.Trim().ToUpperInvariant()).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)] : null;
        static IReadOnlyList<YearRange>? Y(IReadOnlyList<YearRange>? list)
            => list is { Count: > 0 } ? [.. list.Distinct().OrderBy(y => y.From).ThenBy(y => y.To)] : null;

        return ProtocolJson.Serialize(f with
        {
            Artists = S(f.Artists), Albums = S(f.Albums), Genres = S(f.Genres), Years = Y(f.Years), Formats = S(f.Formats), Searches = S(f.Searches),
            ExcludeArtists = S(f.ExcludeArtists), ExcludeAlbums = S(f.ExcludeAlbums), ExcludeGenres = S(f.ExcludeGenres), ExcludeYears = Y(f.ExcludeYears),
            ExcludeFormats = S(f.ExcludeFormats), ExcludeSearches = S(f.ExcludeSearches),
        });
    }

    private static SavedStation ToSaved(SavedEntry s) => new(s.Name, s.Filter!, s.Filter!.Describe(), s.SavedAt);

    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static string CheckName(string name)
    {
        var trimmed = name?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            throw new MusicSpinException(MusicSpinErrorCode.InvalidArgument, "A radio station name must not be empty.");
        }

        return trimmed.Length <= MaxNameLength ? trimmed
            : throw new MusicSpinException(MusicSpinErrorCode.InvalidArgument, $"A radio station name can be at most {MaxNameLength} characters.");
    }

    private StationsFile Read()
    {
        LoadWarning = null;
        if (!File.Exists(FilePath))
        {
            return new StationsFile();
        }

        try
        {
            return Parse(SharedFile.ReadAllText(FilePath));
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            LoadWarning = $"Ignoring unreadable radio stations file '{FilePath}': {ex.Message}";
            return new StationsFile();
        }
    }

    private static StationsFile Parse(string json)
    {
        var file = JsonSerializer.Deserialize<StationsFile>(json, ProtocolJson.Options) ?? throw new InvalidDataException("empty file");
        if (file.Version != Version)
        {
            throw new InvalidDataException($"version {file.Version}, expected {Version}");
        }

        // A hand-edited or damaged entry is dropped rather than failing every command.
        file.Recent = [.. (file.Recent ?? []).Where(r => r?.Filter is not null && r.Filter.Validate() is null)];
        file.Saved = [.. (file.Saved ?? []).Where(s => s?.Filter is not null && !string.IsNullOrWhiteSpace(s.Name) && s.Filter.Validate() is null)];
        return file;
    }

    private void Change(Action<StationsFile> change)
    {
        try
        {
            Directory.CreateDirectory(_dir.Path);
            using var lockFile = AcquireLock();
            StationsFile file;
            try
            {
                file = File.Exists(FilePath) ? Parse(SharedFile.ReadAllText(FilePath)) : new StationsFile();
            }
            catch (Exception ex) when (ex is JsonException or InvalidDataException)
            {
                // Unreadable: kept aside for the user rather than overwritten, and the change starts from empty.
                File.Move(FilePath, FilePath + ".bad", overwrite: true);
                file = new StationsFile();
            }

            change(file);
            var temp = $"{FilePath}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
            try
            {
                File.WriteAllText(temp, JsonSerializer.Serialize(file, JsonOptions));
                SharedFile.Replace(temp, FilePath);
            }
            catch
            {
                TryDelete(temp);
                throw;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw AppPaths.NotWritable(_dir, ex.Message);
        }
    }

    private FileStream AcquireLock()
    {
        var deadline = _time.GetUtcNow() + _lockTimeout;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return new FileStream(LockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.None);
            }
            catch (IOException) when (_time.GetUtcNow() < deadline)
            {
                if (_onLockRetry is null)
                {
                    Thread.Sleep(20); // another process holds it for one small read-modify-write
                }
                else
                {
                    _onLockRetry(attempt);
                }
            }
            catch (IOException ex) when (File.Exists(LockPath))
            {
                throw new MusicSpinException(MusicSpinErrorCode.InvalidState,
                    $"The radio stations file is in use by another MusicSpin process (lock '{LockPath}'). Try again.", ex);
            }
        }
    }

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

    private static readonly JsonSerializerOptions JsonOptions = new(ProtocolJson.Options) { WriteIndented = true };

    private sealed class StationsFile
    {
        public int Version { get; set; } = StationStore.Version;
        public List<RecentEntry> Recent { get; set; } = [];
        public List<SavedEntry> Saved { get; set; } = [];
    }

    private sealed class RecentEntry
    {
        public RadioFilter? Filter { get; set; }
        public DateTimeOffset StartedAt { get; set; }
    }

    private sealed class SavedEntry
    {
        public string Name { get; set; } = string.Empty;
        public RadioFilter? Filter { get; set; }
        public DateTimeOffset SavedAt { get; set; }
    }
}
