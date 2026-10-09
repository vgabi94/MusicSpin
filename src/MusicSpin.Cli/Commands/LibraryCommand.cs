using System.CommandLine;
using MusicSpin.Cli.Output;
using MusicSpin.Core;
using MusicSpin.Core.Hosting;
using MusicSpin.Core.Library;

namespace MusicSpin.Cli.Commands;

/// <summary><c>music-spin library ...</c>: folders, scanning, stats, health check, watch.</summary>
internal static class LibraryCommand
{
    public static Command Create(CommandRunner runner)
    {
        var command = new Command("library", "Manage the music library (folders, scan, relocate, stats, check, watch). Data lives in the data folder (see 'doctor').")
        {
            Add(runner), Remove(runner), Folders(runner), Scan(runner), Relocate(runner), Stats(runner), Check(runner), Watch(runner),
        };
        return command;
    }

    private static Command Add(CommandRunner runner)
    {
        var folder = new Argument<string>("folder") { Description = "Folder with music (scanned recursively)." };
        var command = new Command("add", "Add a folder to the library (then run 'library scan'). Status: added | exists | covered (inside another library folder).") { folder };
        runner.SetAction(command, (parseResult, json, _) =>
        {
            using var library = runner.OpenLibraryForWrite(parseResult);
            var result = library.AddFolder(parseResult.GetValue(folder)!);
            if (json)
            {
                JsonOutput.WriteSuccess(runner.Env.Out, "library add", result);
            }
            else
            {
                runner.Env.Out.WriteLine(result.Status switch
                {
                    "exists" => $"Already in the library: {result.Path}",
                    "covered" => $"Already covered by library folder {result.CoveredBy}: {result.Path}",
                    _ => $"Added {result.Path}" + (result.Absorbed.Count > 0 ? $" (replaces {string.Join(", ", result.Absorbed)})" : string.Empty)
                         + ". Run 'music-spin library scan'.",
                });
            }

            return Task.FromResult(ExitCodes.Success);
        }, "library add");
        return command;
    }

    private static Command Remove(CommandRunner runner)
    {
        var folder = new Argument<string>("folder") { Description = "Library folder to remove (music files are not touched)." };
        var command = new Command("remove", "Remove a folder and its tracks from the library. Exit 12 if it is not a library folder.") { folder };
        runner.SetAction(command, (parseResult, json, _) =>
        {
            using var library = runner.OpenLibraryForWrite(parseResult);
            var result = library.RemoveFolder(parseResult.GetValue(folder)!);
            if (json)
            {
                JsonOutput.WriteSuccess(runner.Env.Out, "library remove", result);
            }
            else
            {
                runner.Env.Out.WriteLine($"Removed {result.Path} ({result.TracksRemoved} track(s) dropped from the library).");
            }

            return Task.FromResult(ExitCodes.Success);
        }, "library remove");
        return command;
    }

    private static Command Relocate(CommandRunner runner)
    {
        var from = new Argument<string>("old") { Description = "Old folder (library folder or any folder inside one)." };
        var to = new Argument<string>("new") { Description = "Folder the files are now in (must exist)." };
        var dryRun = new Option<bool>("--dry-run") { Description = "Validate and report what would change; write nothing." };
        var force = new Option<bool>("--force") { Description = "Relocate even when none of the files exist at the new location." };
        var command = new Command("relocate",
            "Point library tracks under <old> at <new> after you moved or renamed the folder (or mounted a drive elsewhere), without rescanning. " +
            "Keeps date added and playlist entries; library folders under <old> move along. " +
            "Exit 3 if <new> does not exist, 12 if nothing in the library is under <old>, 8 if <new> is outside the library, " +
            "a new path is already a track, or no file exists at <new> (without --force).")
        {
            from, to, dryRun, force,
        };
        command.Aliases.Add("move");
        runner.SetAction(command, (parseResult, json, _) =>
        {
            using var library = runner.OpenLibraryForWrite(parseResult);
            var result = library.Relocate(parseResult.GetValue(from)!, parseResult.GetValue(to)!, parseResult.GetValue(dryRun), parseResult.GetValue(force));
            var output = runner.Env.Out;
            if (json)
            {
                JsonOutput.WriteSuccess(output, "library relocate", result);
                return Task.FromResult(ExitCodes.Success);
            }

            output.WriteLine($"{(result.DryRun ? "Would relocate" : "Relocated")} {result.Tracks} track(s) from {result.From} to {result.To}" +
                             $"{(result.FoldersRelocated.Count > 0 ? $"; library folder(s) moved: {string.Join(", ", result.FoldersRelocated)}" : string.Empty)}" +
                             $"; {result.PlaylistEntries} playlist entr{(result.PlaylistEntries == 1 ? "y" : "ies")} updated.");
            foreach (var move in result.Sample)
            {
                output.WriteLine($"  {move.From} -> {move.To}");
            }

            if (result.Tracks > result.Sample.Count)
            {
                output.WriteLine($"  ... {result.Tracks - result.Sample.Count} more.");
            }

            foreach (var warning in result.Warnings)
            {
                output.WriteLine($"warning: {warning}");
            }

            return Task.FromResult(ExitCodes.Success);
        }, "library relocate");
        return command;
    }

    private static Command Folders(CommandRunner runner)
    {
        var command = new Command("folders", "List library folders.");
        runner.SetAction(command, (parseResult, json, _) =>
        {
            using var library = runner.OpenLibraryForRead(parseResult);
            var folders = library.Folders().Select(FolderDto.From).ToList();
            if (json)
            {
                JsonOutput.WriteSuccess(runner.Env.Out, "library folders", new FoldersDto(library.Directory.Path, folders));
            }
            else if (folders.Count == 0)
            {
                runner.Env.Out.WriteLine("No library folders. Add one with 'music-spin library add <folder>'.");
            }
            else
            {
                foreach (var f in folders)
                {
                    runner.Env.Out.WriteLine($"{f.Path}  {f.TrackCount} track(s){(f.Exists ? string.Empty : "  [missing]")}{(f.LastScan is null ? "  [never scanned]" : string.Empty)}");
                }
            }

            return Task.FromResult(ExitCodes.Success);
        }, "library folders");
        return command;
    }

    private static Command Scan(CommandRunner runner)
    {
        var full = new Option<bool>("--full") { Description = "Re-read every file (default: only new/changed files by size + modification time)." };
        var parallel = Options.Validated(new Option<int?>("--parallel") { Description = "Parallel file readers (default: CPU count, 2-16)." },
            v => v is < 1 or > 64 ? "--parallel must be between 1 and 64." : null);
        var quiet = new Option<bool>("--quiet") { Description = "No progress on stderr." };
        var noMoves = new Option<bool>("--no-move-detection")
        {
            Description = "Do not recognise renamed/moved files (they are then removed + added: new id and date added; playlist entries become missing).",
        };
        var command = new Command("scan",
            "Scan library folders: add new, update changed, drop deleted files. Progress goes to stderr (JSON lines with --json). " +
            "Ctrl+C cancels (exit 130); work done so far is kept.")
        {
            full, parallel, quiet, noMoves,
        };

        runner.SetAction(command, (parseResult, json, cancellationToken) =>
        {
            var env = runner.Env;
            using var library = runner.OpenLibraryForWrite(parseResult);
            var options = new ScanOptions
            {
                Full = parseResult.GetValue(full),
                MaxParallelism = parseResult.GetValue(parallel),
                DetectMoves = !parseResult.GetValue(noMoves),
                Progress = parseResult.GetValue(quiet) ? null : new SyncProgress<ScanProgress>(p => WriteProgress(env.Error, json, p)),
            };
            var result = library.Scan(options, cancellationToken);
            var dto = ScanResultDto.From(result);
            if (result.Cancelled)
            {
                return Task.FromResult(runner.Fail(json, "library scan", ExitCodes.Cancelled, "Scan cancelled; files processed so far were saved.", dto));
            }

            if (json)
            {
                JsonOutput.WriteSuccess(env.Out, "library scan", dto);
            }
            else
            {
                WriteScanSummary(env.Out, dto);
            }

            return Task.FromResult(ExitCodes.Success);
        }, "library scan");
        return command;
    }

    internal static void WriteScanSummary(TextWriter output, ScanResultDto r)
    {
        output.WriteLine($"Scanned {r.Folders} folder(s){(r.FoldersMissing > 0 ? $" ({r.FoldersMissing} missing)" : string.Empty)}: " +
                         $"{r.FilesFound} audio file(s), {r.Added} added, {r.Updated} updated, {r.Unchanged} unchanged, {r.Removed} removed, " +
                         $"{(r.Moved > 0 ? $"{r.Moved} moved, " : string.Empty)}" +
                         $"{r.Failed} failed{(r.SkippedUnreadable > 0 ? $", {r.SkippedUnreadable} unreadable skipped" : string.Empty)} in {r.ElapsedSeconds:0.00}s.");
        output.WriteLine($"Library: {r.Tracks} track(s), {r.Albums} album(s), {r.Artists} artist(s).");
        foreach (var move in r.Moves.Take(20))
        {
            output.WriteLine($"  moved: {move.From} -> {move.To}");
        }

        if (r.Moved > 20)
        {
            output.WriteLine($"  ... {r.Moved - 20} more move(s); see --json.");
        }

        if (r.PlaylistEntriesUpdated > 0)
        {
            output.WriteLine($"  {r.PlaylistEntriesUpdated} playlist entr{(r.PlaylistEntriesUpdated == 1 ? "y" : "ies")} now point at moved tracks.");
        }

        if (r.AmbiguousMoves > 0)
        {
            output.WriteLine($"  {r.AmbiguousMoves} file(s) looked moved but had identical copies; treated as removed + added.");
        }

        foreach (var issue in r.Issues.Take(20))
        {
            output.WriteLine($"  [{issue.Kind}] {issue.Path}: {issue.Message}");
        }

        if (r.IssueCount > 20)
        {
            output.WriteLine($"  ... {r.IssueCount - 20} more issue(s); see 'music-spin library check'.");
        }
    }

    private static void WriteProgress(TextWriter error, bool json, ScanProgress p)
    {
        if (json)
        {
            error.WriteLine(JsonOutput.Serialize(new ScanProgressDto("progress", p.Phase, p.FilesFound, p.FilesToRead, p.FilesRead, p.Failed, p.CurrentPath)));
        }
        else
        {
            error.WriteLine(p.Phase switch
            {
                "walk" => $"scan: found {p.FilesFound} file(s)...",
                "read" => $"scan: read {p.FilesRead}/{p.FilesToRead} ({p.Failed} failed)",
                "index" => "scan: indexing albums and artists...",
                _ => "scan: done",
            });
        }
    }

    private static Command Stats(CommandRunner runner)
    {
        var command = new Command("stats", "Library totals: tracks, albums, artists, size, duration, by format, hi-res breakdown.");
        runner.SetAction(command, (parseResult, json, _) =>
        {
            using var library = runner.OpenLibraryForRead(parseResult);
            var stats = library.Stats();
            if (json)
            {
                JsonOutput.WriteSuccess(runner.Env.Out, "library stats", stats);
                return Task.FromResult(ExitCodes.Success);
            }

            var o = runner.Env.Out;
            o.WriteLine($"{stats.Tracks} track(s), {stats.Albums} album(s), {stats.Artists} artist(s) in {stats.Folders} folder(s)");
            o.WriteLine($"{stats.TotalSizeBytes / 1024.0 / 1024 / 1024:0.00} GiB, {TimeSpan.FromSeconds(stats.TotalDurationSeconds):d\\.hh\\:mm\\:ss}");
            o.WriteLine($"Lossless: {stats.LosslessTracks}, hi-res: {stats.HiResTracks} track(s) / {stats.HiResAlbums} album(s)");
            foreach (var f in stats.ByFormat)
            {
                o.WriteLine($"  {f.Format,-10} {f.Tracks,7} track(s) {f.SizeBytes / 1024.0 / 1024:0.0} MiB");
            }

            foreach (var r in stats.ByResolution)
            {
                o.WriteLine($"  {r.Resolution,-10} {r.Tracks,7} track(s)");
            }

            return Task.FromResult(ExitCodes.Success);
        }, "library stats");
        return command;
    }

    private static Command Check(CommandRunner runner)
    {
        var command = new Command("check",
            "Health report: missing files/folders, likely duplicates (artist + title + duration), missing tags, unreadable files. " +
            "Exit 0 even when issues are found; see 'healthy'.");
        runner.SetAction(command, (parseResult, json, _) =>
        {
            using var library = runner.OpenLibraryForRead(parseResult);
            var check = CheckDto.From(library.Check());
            if (json)
            {
                JsonOutput.WriteSuccess(runner.Env.Out, "library check", check);
                return Task.FromResult(ExitCodes.Success);
            }

            var o = runner.Env.Out;
            o.WriteLine(check.Healthy ? "Library looks healthy." : "Library issues:");
            Section(o, "Missing files", check.MissingFiles);
            Section(o, "Missing folders", check.MissingFolders);
            Section(o, "Possible duplicates", check.Duplicates.Select(d => $"{d.Artist} - {d.Title}: {string.Join(" | ", d.Paths)}").ToList());
            Section(o, "Missing tags", check.MissingTags.Select(m => $"{m.Path} ({string.Join(", ", m.Missing)})").ToList());
            Section(o, "Unreadable", check.Unreadable.Select(u => $"{u.Path} [{u.Kind}] {u.Message}").ToList());
            return Task.FromResult(ExitCodes.Success);
        }, "library check");
        return command;
    }

    private static void Section(TextWriter o, string title, IReadOnlyList<string> lines)
    {
        if (lines.Count == 0)
        {
            return;
        }

        o.WriteLine($"{title} ({lines.Count}):");
        foreach (var line in lines.Take(50))
        {
            o.WriteLine($"  {line}");
        }

        if (lines.Count > 50)
        {
            o.WriteLine($"  ... {lines.Count - 50} more");
        }
    }

    private static Command Watch(CommandRunner runner)
    {
        var debounce = Options.Validated(new Option<int>("--debounce-ms")
        {
            Description = "Quiet period after file changes before rescanning (default 2000).",
            DefaultValueFactory = _ => 2000,
        }, v => v is < 50 or > 600_000 ? "--debounce-ms must be between 50 and 600000." : null);
        var command = new Command("watch",
            "Watch library folders and run an incremental scan after changes (basic sync). Prints one line per event " +
            "(JSON lines with --json: watching, scan, error). Runs until Ctrl+C (exit 0).")
        {
            debounce,
        };

        runner.SetAction(command, async (parseResult, json, cancellationToken) =>
        {
            var env = runner.Env;
            List<string> folders;
            using (var library = runner.OpenLibraryForWrite(parseResult))
            {
                folders = library.Folders().Where(f => f.Exists).Select(f => f.Path).ToList();
            }

            if (folders.Count == 0)
            {
                return runner.Fail(json, "library watch", ExitCodes.NotFound, "No existing library folders to watch. Add one with 'music-spin library add <folder>'.");
            }

            using var gate = new SemaphoreSlim(1, 1);
            using var watcher = new LibraryWatcher(folders, TimeSpan.FromMilliseconds(parseResult.GetValue(debounce)), env.Time);
            watcher.Changed += async (_, paths) =>
            {
                await gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
                try
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        return;
                    }

                    using var library = runner.OpenLibraryForWrite(parseResult);
                    var result = ScanResultDto.From(library.Scan(new ScanOptions(), cancellationToken));
                    Emit(env.Out, json, "scan", new { changedPaths = paths.Count, result }, () => WriteScanSummary(env.Out, result));
                }
                catch (MusicSpinException ex)
                {
                    Emit(env.Out, json, "error", new { code = ExitCodes.Name(ExitCodes.From(ex.Code)), message = ex.Message },
                        () => env.Error.WriteLine($"music-spin library watch: {ex.Message}"));
                }
                finally
                {
                    gate.Release();
                }
            };

            Emit(env.Out, json, "watching", new { folders, debounceMs = parseResult.GetValue(debounce) },
                () => env.Out.WriteLine($"Watching {folders.Count} folder(s). Ctrl+C to stop."));
            try
            {
                await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // normal shutdown
            }

            await gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            Emit(env.Out, json, "stopped", new { }, () => env.Out.WriteLine("Stopped watching."));
            return ExitCodes.Success;
        }, "library watch");
        return command;
    }

    private static readonly Lock OutputLock = new();

    private static void Emit(TextWriter output, bool json, string @event, object data, Action text)
    {
        lock (OutputLock)
        {
            if (json)
            {
                output.WriteLine(JsonOutput.Serialize(new { @event, data }));
            }
            else
            {
                text();
            }

            output.Flush();
        }
    }

    /// <summary>Reports synchronously on the calling thread (Progress&lt;T&gt; would post to the thread pool and reorder lines).</summary>
    private sealed class SyncProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
