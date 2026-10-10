using FFmpeg.AutoGen;

namespace MusicSpin.Core.Decoding.FFmpeg;

/// <summary>Result of trying to load the FFmpeg shared libraries.</summary>
/// <param name="Available">True when libavformat/libavcodec/libavutil/libswresample loaded and match the bindings.</param>
/// <param name="Version">FFmpeg version string (av_version_info), e.g. "9.0.2".</param>
/// <param name="License">FFmpeg build license (avcodec_license), e.g. "LGPL version 2.1 or later".</param>
/// <param name="LibraryPath">Directory searched first (empty = OS default search path).</param>
/// <param name="Error">Why loading failed, when <paramref name="Available"/> is false.</param>
public sealed record FFmpegStatus(
    bool Available,
    string? Version,
    string? License,
    string LibraryPath,
    IReadOnlyDictionary<string, int> ExpectedLibraryVersions,
    string? Error)
{
    /// <summary>True when the loaded build is LGPL (what MusicSpin should redistribute). GPL is fine for local use.</summary>
    public bool IsLgpl => License?.Contains("LGPL", StringComparison.OrdinalIgnoreCase) == true;
}

/// <summary>
/// Loads FFmpeg as separate shared libraries at runtime (never statically linked into MusicSpin),
/// keeping MusicSpin MIT while FFmpeg stays under its own (preferably LGPL) license.
/// <para>Search order (first directory containing the expected libavcodec wins):</para>
/// <list type="number">
/// <item><c>MUSICSPIN_FFMPEG_PATH</c> environment variable</item>
/// <item><c>ffmpeg/</c> next to the executable (how release builds ship it)</item>
/// <item>per-user install: <c>LocalApplicationData/MusicSpin/ffmpeg</c> (what <c>eng/get-ffmpeg</c> installs)</item>
/// <item>Homebrew prefixes on macOS</item>
/// <item>the OS default library search path (ldconfig, PATH, ...)</item>
/// </list>
/// Each candidate directory is also checked for <c>lib/</c> and <c>bin/</c> subfolders.
/// </summary>
public static class FFmpegRuntime
{
    public const string PathEnvironmentVariable = "MUSICSPIN_FFMPEG_PATH";

    private static readonly Lock InitLock = new();
    private static readonly string[] RequiredLibraryNames = ["avcodec", "avformat", "avutil", "swresample"];
    private static FFmpegStatus? _status;

    /// <summary>FFmpeg release the bindings were generated for, e.g. "9.0".</summary>
    public static string ExpectedFFmpegVersion => "9.0";

    /// <summary>Loads FFmpeg once per process and returns the cached status.</summary>
    public static FFmpegStatus Status
    {
        get
        {
            lock (InitLock)
            {
                return _status ??= TryLoad();
            }
        }
    }

    public static bool IsAvailable => Status.Available;

    /// <summary>Throws <see cref="MusicSpinException"/> (DecoderUnavailable) if FFmpeg cannot be used.</summary>
    public static void EnsureAvailable() => EnsureAvailable(Status);

    internal static void EnsureAvailable(FFmpegStatus status)
    {
        if (!status.Available)
        {
            throw new MusicSpinException(MusicSpinErrorCode.DecoderUnavailable, status.Error ?? "FFmpeg is not available.");
        }
    }

    /// <summary>File names of the libraries MusicSpin needs, for messages and docs.</summary>
    public static string RequiredLibraries => RequiredLibraryFiles(ffmpeg.LibraryVersionMap, CurrentOs);

    /// <summary>Directories searched, in order (for diagnostics).</summary>
    public static IReadOnlyList<string> CandidateDirectories()
        => CandidateDirectories(Environment.GetEnvironmentVariable(PathEnvironmentVariable), AppContext.BaseDirectory,
            UserInstallDirectory, OperatingSystem.IsMacOS());

    /// <summary>Per-user FFmpeg location: ~/.local/share/MusicSpin/ffmpeg, %LOCALAPPDATA%\MusicSpin\ffmpeg, ~/Library/Application Support/MusicSpin/ffmpeg.</summary>
    public static string UserInstallDirectory
        => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MusicSpin", "ffmpeg");

    internal enum Os
    {
        Linux,
        Windows,
        MacOS,
    }

    private static Os CurrentOs => OperatingSystem.IsWindows() ? Os.Windows : OperatingSystem.IsMacOS() ? Os.MacOS : Os.Linux;

    internal static string RequiredLibraryFiles(IReadOnlyDictionary<string, int> versions, Os os)
        => string.Join(", ", RequiredLibraryNames.Select(n => LibraryFileName(n, versions[n], os)));

    internal static IReadOnlyList<string> CandidateDirectories(string? environmentPath, string baseDirectory, string userInstallDirectory, bool macOS)
    {
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(environmentPath))
        {
            candidates.Add(environmentPath);
        }

        candidates.Add(Path.Combine(baseDirectory, "ffmpeg"));
        candidates.Add(userInstallDirectory);

        if (macOS)
        {
            candidates.AddRange(["/opt/homebrew/opt/ffmpeg/lib", "/usr/local/opt/ffmpeg/lib", "/opt/homebrew/lib", "/usr/local/lib"]);
        }

        return candidates;
    }

    /// <summary>First candidate (or its lib/ or bin/ subfolder) holding <paramref name="avcodecFile"/>; empty = OS default search.</summary>
    internal static string ResolveLibraryPath(IEnumerable<string> candidates, string avcodecFile, Func<string, bool> fileExists)
    {
        foreach (var candidate in candidates)
        {
            foreach (var dir in new[] { candidate, Path.Combine(candidate, "lib"), Path.Combine(candidate, "bin") })
            {
                if (fileExists(Path.Combine(dir, avcodecFile)))
                {
                    return dir;
                }
            }
        }

        // Empty = let the OS loader search (LD_LIBRARY_PATH, ldconfig cache, PATH on Windows, ...).
        return string.Empty;
    }

    /// <summary>What the native load reported: version strings and libavcodec's major version.</summary>
    internal readonly record struct LoadedLibraries(string Version, string License, int CodecMajor);

    /// <summary>
    /// Turns a load attempt into a status: the libraries are missing/broken (<paramref name="load"/> throws a loader
    /// exception), have the wrong ABI (libavcodec major), or are usable.
    /// </summary>
    internal static FFmpegStatus Evaluate(Func<LoadedLibraries> load, string path, IReadOnlyDictionary<string, int> expected, int expectedCodecMajor, string requiredLibraries)
    {
        try
        {
            var loaded = load();
            if (loaded.CodecMajor != expectedCodecMajor)
            {
                return new FFmpegStatus(false, loaded.Version, loaded.License, path, expected,
                    $"FFmpeg {loaded.Version} has libavcodec {loaded.CodecMajor}, but MusicSpin needs libavcodec {expectedCodecMajor} (FFmpeg {ExpectedFFmpegVersion}).");
            }

            return new FFmpegStatus(true, loaded.Version, loaded.License, path, expected, null);
        }
        catch (Exception ex) when (ex is DllNotFoundException or NotSupportedException or EntryPointNotFoundException or BadImageFormatException)
        {
            var where = string.IsNullOrEmpty(path) ? "the system library path" : $"'{path}'";
            return new FFmpegStatus(false, null, null, path, expected,
                $"FFmpeg {ExpectedFFmpegVersion} shared libraries ({requiredLibraries}) were not found in {where}. " +
                $"Run eng/get-ffmpeg.sh (or .ps1), install FFmpeg {ExpectedFFmpegVersion}, or set {PathEnvironmentVariable}.");
        }
    }

    private static FFmpegStatus TryLoad()
    {
        var expected = new Dictionary<string, int>(ffmpeg.LibraryVersionMap);
        var path = ResolveLibraryPath(CandidateDirectories(), LibraryFileName("avcodec", expected["avcodec"], CurrentOs), File.Exists);
        var status = Evaluate(() =>
        {
            ffmpeg.RootPath = path;
            DynamicallyLoadedBindings.Initialize();
            var version = ffmpeg.av_version_info();
            var license = ffmpeg.avcodec_license();
            var codecMajor = (int)(ffmpeg.avcodec_version() >> 16);
            if (codecMajor == ffmpeg.LIBAVCODEC_VERSION_MAJOR)
            {
                // Touch swresample too, so a partial install is reported up front.
                _ = ffmpeg.swresample_version();
                ffmpeg.av_log_set_level(ffmpeg.AV_LOG_QUIET);
            }

            return new LoadedLibraries(version, license, codecMajor);
        }, path, expected, ffmpeg.LIBAVCODEC_VERSION_MAJOR, RequiredLibraryFiles(expected, CurrentOs));
        return status;
    }

    internal static string LibraryFileName(string name, int major, Os os) => os switch
    {
        Os.Windows => $"{name}-{major}.dll",
        Os.MacOS => $"lib{name}.{major}.dylib",
        _ => $"lib{name}.so.{major}",
    };
}
