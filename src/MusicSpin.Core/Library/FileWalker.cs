namespace MusicSpin.Core.Library;

/// <summary>An audio file found on disk.</summary>
public sealed record FoundFile(string Path, long SizeBytes, long ModifiedUtcTicks);

/// <summary>A folder-level problem found while walking.</summary>
public sealed record WalkIssue(string Path, string Kind, string Message);

/// <summary>
/// Recursive directory walker that survives the real world: inaccessible folders are reported and skipped,
/// directory symlinks are followed but loops are detected (by resolved target), depth is capped,
/// and files that vanish mid-walk are ignored.
/// </summary>
public static class FileWalker
{
    public const int MaxDepth = 64;

    public static void Walk(string root, Action<FoundFile> onFile, Action<WalkIssue> onIssue, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(onFile);
        ArgumentNullException.ThrowIfNull(onIssue);
        var visited = new HashSet<string>(LibraryKeys.CaseInsensitivePaths ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        var stack = new Stack<(string Path, string Real, int Depth)>();
        try
        {
            stack.Push((root, Canonical(new DirectoryInfo(root), null), 0));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            onIssue(new WalkIssue(root, KindOf(ex), ex.Message));
            return;
        }

        while (stack.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (dir, real, depth) = stack.Pop();

            if (!visited.Add(real))
            {
                onIssue(new WalkIssue(dir, "symlink_loop", $"Skipped: already visited via '{real}' (symlink loop or duplicate link)."));
                continue;
            }

            if (depth > MaxDepth)
            {
                onIssue(new WalkIssue(dir, "too_deep", $"Skipped: deeper than {MaxDepth} levels."));
                continue;
            }

            IEnumerable<FileSystemInfo> entries;
            try
            {
                entries = new DirectoryInfo(dir).EnumerateFileSystemInfos("*", new EnumerationOptions
                {
                    RecurseSubdirectories = false,
                    IgnoreInaccessible = false,
                    AttributesToSkip = 0,
                    ReturnSpecialDirectories = false,
                }).ToList();
            }
            catch (UnauthorizedAccessException ex)
            {
                onIssue(new WalkIssue(dir, "access_denied", ex.Message));
                continue;
            }
            catch (DirectoryNotFoundException ex)
            {
                onIssue(new WalkIssue(dir, "missing", ex.Message));
                continue;
            }
            catch (IOException ex)
            {
                onIssue(new WalkIssue(dir, "access_denied", ex.Message));
                continue;
            }

            foreach (var entry in entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    if (entry is DirectoryInfo sub)
                    {
                        stack.Push((sub.FullName, Canonical(sub, real), depth + 1));
                    }
                    else if (entry is FileInfo file && AudioFileTypes.IsAudioFile(file.Name))
                    {
                        // Symlinked files: stat the target; broken links throw / report !Exists.
                        var target = file.LinkTarget is null ? file : (file.ResolveLinkTarget(true) as FileInfo ?? file);
                        if (!target.Exists)
                        {
                            onIssue(new WalkIssue(file.FullName, "missing", "Broken symlink or file vanished."));
                            continue;
                        }

                        onFile(new FoundFile(file.FullName, target.Length, target.LastWriteTimeUtc.Ticks));
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    onIssue(new WalkIssue(entry.FullName, KindOf(ex), ex.Message));
                }
            }
        }
    }

    private static string KindOf(Exception ex)
        => ex.Message.Contains("symbolic link", StringComparison.OrdinalIgnoreCase) ? "symlink_loop"
            : ex is FileNotFoundException or DirectoryNotFoundException ? "missing"
            : "access_denied";

    /// <summary>
    /// Canonical identity of a directory for loop detection: symlinks resolve to their final target,
    /// regular directories extend their parent's canonical path.
    /// </summary>
    private static string Canonical(DirectoryInfo dir, string? parentReal)
    {
        if (dir.LinkTarget is not null)
        {
            var target = dir.ResolveLinkTarget(returnFinalTarget: true);
            return target is null ? dir.FullName : LibraryKeys.NormalizePath(target.FullName);
        }

        return parentReal is null ? LibraryKeys.NormalizePath(dir.FullName) : System.IO.Path.Combine(parentReal, dir.Name);
    }
}
