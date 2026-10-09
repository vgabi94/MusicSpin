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
    public static void EnsureAvailable()
    {
        var status = Status;
        if (!status.Available)
        {
            throw new MusicSpinException(MusicSpinErrorCode.DecoderUnavailable, status.Error ?? "FFmpeg is not available.");
        }
    }

    /// <summary>File names of the libraries MusicSpin needs, for messages and docs.</summary>
    public static string RequiredLibraries
    {
        get
        {
            var map = ffmpeg.LibraryVersionMap;
            return string.Join(", ", RequiredLibraryNames.Select(n => LibraryFileName(n, map[n])));
        }
    }

    /// <summary>Directories searched, in order (for diagnostics).</summary>
    public static IReadOnlyList<string> CandidateDirectories()
    {
        var candidates = new List<string>();
        var env = Environment.GetEnvironmentVariable(PathEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(env))
        {
            candidates.Add(env);
        }

        candidates.Add(Path.Combine(AppContext.BaseDirectory, "ffmpeg"));
        candidates.Add(UserInstallDirectory);

        if (OperatingSystem.IsMacOS())
        {
            candidates.AddRange(["/opt/homebrew/opt/ffmpeg/lib", "/usr/local/opt/ffmpeg/lib", "/opt/homebrew/lib", "/usr/local/lib"]);
        }

        return candidates;
    }

    /// <summary>Per-user FFmpeg location: ~/.local/share/MusicSpin/ffmpeg, %LOCALAPPDATA%\MusicSpin\ffmpeg, ~/Library/Application Support/MusicSpin/ffmpeg.</summary>
    public static string UserInstallDirectory
        => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MusicSpin", "ffmpeg");

    private static FFmpegStatus TryLoad()
    {
        var expected = new Dictionary<string, int>(ffmpeg.LibraryVersionMap);
        var path = ResolveLibraryPath();
        try
        {
            ffmpeg.RootPath = path;
            DynamicallyLoadedBindings.Initialize();

            var version = ffmpeg.av_version_info();
            var license = ffmpeg.avcodec_license();

            // libavcodec's major version must match the bindings' ABI.
            var codecMajor = (int)(ffmpeg.avcodec_version() >> 16);
            if (codecMajor != ffmpeg.LIBAVCODEC_VERSION_MAJOR)
            {
                return new FFmpegStatus(false, version, license, path, expected,
                    $"FFmpeg {version} has libavcodec {codecMajor}, but MusicSpin needs libavcodec {ffmpeg.LIBAVCODEC_VERSION_MAJOR} (FFmpeg {ExpectedFFmpegVersion}).");
            }

            // Touch swresample too, so a partial install is reported up front.
            _ = ffmpeg.swresample_version();
            ffmpeg.av_log_set_level(ffmpeg.AV_LOG_QUIET);
            return new FFmpegStatus(true, version, license, path, expected, null);
        }
        catch (Exception ex) when (ex is DllNotFoundException or NotSupportedException or EntryPointNotFoundException or BadImageFormatException)
        {
            var where = string.IsNullOrEmpty(path) ? "the system library path" : $"'{path}'";
            return new FFmpegStatus(false, null, null, path, expected,
                $"FFmpeg {ExpectedFFmpegVersion} shared libraries ({RequiredLibraries}) were not found in {where}. " +
                $"Run eng/get-ffmpeg.sh (or .ps1), install FFmpeg {ExpectedFFmpegVersion}, or set {PathEnvironmentVariable}.");
        }
    }

    private static string ResolveLibraryPath()
    {
        var avcodec = LibraryFileName("avcodec", ffmpeg.LibraryVersionMap["avcodec"]);
        foreach (var candidate in CandidateDirectories())
        {
            foreach (var dir in new[] { candidate, Path.Combine(candidate, "lib"), Path.Combine(candidate, "bin") })
            {
                if (File.Exists(Path.Combine(dir, avcodec)))
                {
                    return dir;
                }
            }
        }

        // Empty = let the OS loader search (LD_LIBRARY_PATH, ldconfig cache, PATH on Windows, ...).
        return string.Empty;
    }

    private static string LibraryFileName(string name, int major)
        => OperatingSystem.IsWindows() ? $"{name}-{major}.dll"
            : OperatingSystem.IsMacOS() ? $"lib{name}.{major}.dylib"
            : $"lib{name}.so.{major}";
}
