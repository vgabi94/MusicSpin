using System.Collections.Concurrent;
using System.Diagnostics;

namespace MusicSpin.Core.Library;

public sealed record ScanOptions
{
    /// <summary>Re-read every file, even when size + mtime are unchanged.</summary>
    public bool Full { get; init; }

    /// <summary>Parallel tag readers (default: processor count, clamped 2..16).</summary>
    public int? MaxParallelism { get; init; }

    public IProgress<ScanProgress>? Progress { get; init; }

    /// <summary>Minimum interval between progress reports.</summary>
    public TimeSpan ProgressInterval { get; init; } = TimeSpan.FromMilliseconds(200);

    /// <summary>Upserts per database batch.</summary>
    public int BatchSize { get; init; } = 256;

    /// <summary>
    /// Fingerprint files that are read (and, once, unchanged files that have no fingerprint yet) and treat a vanished track
    /// whose fingerprint matches a new file as moved (default true).
    /// </summary>
    public bool DetectMoves { get; init; } = true;
}

/// <summary>A track recognised at a new path (same fingerprint): <paramref name="FromId"/> was replaced by <paramref name="ToId"/>.</summary>
public sealed record TrackMove(string From, string To, string FromId, string ToId);

/// <param name="Phase">walk | read | index | done.</param>
public sealed record ScanProgress(string Phase, int FilesFound, int FilesToRead, int FilesRead, int Failed, string? CurrentPath);

public sealed record ScanIssue(string Path, string Kind, string Message);

public sealed record ScanResult
{
    public int Folders { get; init; }
    public int FoldersMissing { get; init; }
    public int FilesFound { get; init; }
    public int Added { get; init; }
    public int Updated { get; init; }
    public int Unchanged { get; init; }
    public int Removed { get; init; }
    public int Failed { get; init; }

    /// <summary>Tracks found at a new path (rename/move): kept with their date added, playlists follow them. Not counted in Added/Removed.</summary>
    public int Moved { get; init; }

    /// <summary>Vanished and new files that shared a fingerprint but could not be paired one-to-one (duplicates): handled as removed + added.</summary>
    public int AmbiguousMoves { get; init; }

    /// <summary>Unchanged tracks that got their first fingerprint in this scan (once per file, e.g. after an upgrade).</summary>
    public int Fingerprinted { get; init; }

    /// <summary>Playlist entries that now point at a moved track.</summary>
    public int PlaylistEntriesUpdated { get; init; }

    public IReadOnlyList<TrackMove> Moves { get; init; } = [];

    /// <summary>Files that failed earlier and were skipped because they did not change (use --full to retry).</summary>
    public int SkippedUnreadable { get; init; }

    public int Albums { get; init; }
    public int Artists { get; init; }
    public int Tracks { get; init; }
    public bool Full { get; init; }
    public bool Cancelled { get; init; }
    public TimeSpan Elapsed { get; init; }
    public IReadOnlyList<ScanIssue> Issues { get; init; } = [];
}

/// <summary>
/// Incremental, parallel library scan. Rules:
/// - unchanged size + mtime: skipped (unless <see cref="ScanOptions.Full"/>);
/// - files gone from an available root: removed; a root folder that is missing (unmounted drive) keeps its tracks;
/// - subfolders that could not be listed keep their tracks (no false removals on permission errors);
/// - unreadable files are recorded as issues and skipped on later scans while unchanged;
/// - cancellation keeps everything written so far and still rebuilds albums/artists.
/// </summary>
internal sealed class LibraryScanner(LibraryDatabase db, ITrackReader reader, TimeProvider time)
{
    private sealed record ReadOutcome(FoundFile File, TrackRecord? Track, TrackReadException? Error, string? Fingerprint);

    public ScanResult Scan(ScanOptions options, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var nowTicks = time.GetUtcNow().UtcTicks;
        var issues = new ConcurrentQueue<ScanIssue>();
        var folders = db.Folders.FindAll().ToList();
        var progress = new ProgressThrottle(options.Progress, options.ProgressInterval);
        var cancelled = false;

        var available = new List<LibraryFolderRecord>();
        foreach (var folder in folders)
        {
            if (Directory.Exists(folder.Path))
            {
                available.Add(folder);
            }
            else
            {
                issues.Enqueue(new ScanIssue(folder.Path, "folder_missing",
                    "Library folder not found (unmounted drive or moved?). Its tracks are kept until the folder is removed from the library."));
            }
        }

        // 1. Walk.
        var found = new Dictionary<string, (FoundFile File, string FolderId)>(StringComparer.Ordinal);
        var problemDirs = new List<string>();
        try
        {
            foreach (var folder in available)
            {
                FileWalker.Walk(folder.Path,
                    file =>
                    {
                        found.TryAdd(LibraryKeys.PathId(LibraryKeys.NormalizePath(file.Path)), (file, folder.Id));
                        progress.Report(new ScanProgress("walk", found.Count, 0, 0, 0, null));
                    },
                    issue =>
                    {
                        issues.Enqueue(new ScanIssue(issue.Path, issue.Kind, issue.Message));
                        problemDirs.Add(issue.Path);
                    },
                    cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
        }

        // 2. Decide what to read.
        var existing = db.Tracks.FindAll().ToDictionary(t => t.Id, StringComparer.Ordinal);
        var previousIssues = db.Issues.Find(i => i.IsFile).ToDictionary(i => i.Id, StringComparer.Ordinal);
        var toRead = new List<(string Id, FoundFile File, string FolderId)>();
        int unchanged = 0, skippedUnreadable = 0;
        var keptIssues = new List<ScanIssueRecord>();
        var folderMoves = new List<TrackRecord>();
        foreach (var (id, (file, folderId)) in found)
        {
            if (!options.Full && existing.TryGetValue(id, out var track)
                && track.SizeBytes == file.SizeBytes && track.ModifiedUtcTicks == file.ModifiedUtcTicks)
            {
                unchanged++;
                if (track.FolderId != folderId)
                {
                    track.FolderId = folderId;
                    folderMoves.Add(track);
                }

                continue;
            }

            if (!options.Full && previousIssues.TryGetValue(id, out var issue)
                && issue.SizeBytes == file.SizeBytes && issue.ModifiedUtcTicks == file.ModifiedUtcTicks)
            {
                skippedUnreadable++;
                keptIssues.Add(issue);
                continue;
            }

            toRead.Add((id, file, folderId));
        }

        if (folderMoves.Count > 0)
        {
            db.Tracks.Update(folderMoves);
        }

        // 2b. Fingerprint unchanged tracks that have none yet (libraries scanned before fingerprints existed), once.
        var fingerprinted = 0;
        if (options.DetectMoves && !cancelled)
        {
            var reading = toRead.Select(r => r.Id).ToHashSet(StringComparer.Ordinal);
            var missingPrints = found.Keys.Where(id => !reading.Contains(id) && existing.TryGetValue(id, out var t) && t.Fingerprint.Length == 0)
                .Select(id => existing[id]).ToList();
            if (missingPrints.Count > 0)
            {
                try
                {
                    Parallel.ForEach(missingPrints,
                        new ParallelOptions { MaxDegreeOfParallelism = options.MaxParallelism ?? Math.Clamp(Environment.ProcessorCount, 2, 16), CancellationToken = cancellationToken },
                        t => t.Fingerprint = FileFingerprint.Compute(t.Path) ?? string.Empty);
                }
                catch (OperationCanceledException)
                {
                    cancelled = true;
                }

                var done = missingPrints.Where(t => t.Fingerprint.Length > 0).ToList();
                db.Tracks.Update(done);
                fingerprinted = done.Count;
            }
        }

        // 3. Read in parallel, write batches on this thread.
        int added = 0, updated = 0, failed = 0, read = 0;
        var addedTracks = new List<TrackRecord>();
        var removedIds = new HashSet<string>(StringComparer.Ordinal);
        var newIssueRecords = new List<ScanIssueRecord>();
        if (!cancelled && toRead.Count > 0)
        {
            using var queue = new BlockingCollection<ReadOutcome>(boundedCapacity: 4096);
            var parallelism = options.MaxParallelism ?? Math.Clamp(Environment.ProcessorCount, 2, 16);
            var producer = Task.Run(() =>
            {
                try
                {
                    Parallel.ForEach(toRead,
                        new ParallelOptions { MaxDegreeOfParallelism = parallelism, CancellationToken = cancellationToken },
                        item =>
                        {
                            try
                            {
                                var track = reader.Read(item.File.Path);
                                var print = options.DetectMoves ? FileFingerprint.Compute(item.File.Path) : null;
                                queue.Add(new ReadOutcome(item.File, track, null, print), cancellationToken);
                            }
                            catch (TrackReadException ex)
                            {
                                queue.Add(new ReadOutcome(item.File, null, ex, null), cancellationToken);
                            }
                        });
                }
                finally
                {
                    queue.CompleteAdding();
                }
            }, CancellationToken.None);

            var batch = new List<TrackRecord>(options.BatchSize);
            var folderOf = toRead.ToDictionary(t => t.Id, t => t.FolderId, StringComparer.Ordinal);
            foreach (var outcome in queue.GetConsumingEnumerable(CancellationToken.None))
            {
                read++;
                var normalized = LibraryKeys.NormalizePath(outcome.File.Path);
                var id = LibraryKeys.PathId(normalized);
                if (outcome.Track is { } track)
                {
                    track.Id = id;
                    track.Path = normalized;
                    track.FolderId = folderOf[id];
                    track.Directory = System.IO.Path.GetDirectoryName(normalized) ?? string.Empty;
                    track.FileName = System.IO.Path.GetFileName(normalized);
                    track.SizeBytes = outcome.File.SizeBytes;
                    track.ModifiedUtcTicks = outcome.File.ModifiedUtcTicks;
                    track.Fingerprint = outcome.Fingerprint ?? string.Empty;
                    if (existing.TryGetValue(id, out var old))
                    {
                        track.AddedUtcTicks = old.AddedUtcTicks;
                        // Keep derived fields; the indexer recomputes them and skips untouched rows.
                        updated++;
                    }
                    else
                    {
                        track.AddedUtcTicks = nowTicks;
                        added++;
                        addedTracks.Add(track);
                    }

                    batch.Add(track);
                    if (batch.Count >= options.BatchSize)
                    {
                        db.Tracks.Upsert(batch);
                        batch.Clear();
                    }
                }
                else if (outcome.Error is { } error)
                {
                    if (error.Kind == "missing")
                    {
                        // Deleted between walk and read: treat as removed.
                        if (existing.ContainsKey(id))
                        {
                            removedIds.Add(id);
                        }
                    }
                    else
                    {
                        failed++;
                        issues.Enqueue(new ScanIssue(normalized, error.Kind, error.Message));
                        newIssueRecords.Add(new ScanIssueRecord
                        {
                            Id = id, Path = normalized, Kind = error.Kind, Message = error.Message, IsFile = true,
                            SizeBytes = outcome.File.SizeBytes, ModifiedUtcTicks = outcome.File.ModifiedUtcTicks, AtUtcTicks = nowTicks,
                        });
                        // A previously good file that became unreadable is no longer playable.
                        if (existing.ContainsKey(id))
                        {
                            removedIds.Add(id);
                        }
                    }
                }

                progress.Report(new ScanProgress("read", found.Count, toRead.Count, read, failed, outcome.File.Path));
            }

            if (batch.Count > 0)
            {
                db.Tracks.Upsert(batch);
            }

            try
            {
                producer.GetAwaiter().GetResult();
            }
            catch (OperationCanceledException)
            {
                cancelled = true;
            }
            catch (AggregateException ex) when (ex.Flatten().InnerExceptions.All(e => e is OperationCanceledException))
            {
                cancelled = true;
            }
            catch (AggregateException ex) when (ex.Flatten().InnerExceptions.OfType<MusicSpinException>().FirstOrDefault() is { } fatal)
            {
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(fatal).Throw();
            }
        }

        // 4. Removals: only for complete walks, only under available roots, never under folders we could not list.
        var availableIds = available.Select(f => f.Id).ToHashSet(StringComparer.Ordinal);
        var vanished = new List<TrackRecord>();
        if (!cancelled)
        {
            foreach (var track in existing.Values)
            {
                if (!found.ContainsKey(track.Id) && availableIds.Contains(track.FolderId)
                    && !problemDirs.Any(d => LibraryKeys.IsUnder(track.Path, d)))
                {
                    removedIds.Add(track.Id);
                    vanished.Add(track);
                }
            }
        }

        // 4b. Moves: a vanished track and a new file with the same fingerprint are the same track.
        var (moves, ambiguous) = options.DetectMoves && !cancelled ? MatchMoves(vanished, addedTracks) : ([], 0);
        var playlistEntries = 0;
        if (moves.Count > 0)
        {
            foreach (var (old, now) in moves)
            {
                now.AddedUtcTicks = old.AddedUtcTicks;
            }

            db.Tracks.Update(moves.Select(m => m.New));
            playlistEntries = PlaylistMaintenance.Retarget(db, moves.ToDictionary(m => m.Old.Id, m => m.New, StringComparer.Ordinal), null);
        }

        foreach (var id in removedIds)
        {
            db.Tracks.Delete(id);
        }

        // 5. Issues: a complete scan replaces the list; a cancelled one only adds.
        var fileIssuePaths = newIssueRecords.Select(r => r.Path).ToHashSet(StringComparer.Ordinal);
        var folderIssues = issues.Where(i => !fileIssuePaths.Contains(i.Path))
            .Select(i => new ScanIssueRecord
            {
                Id = LibraryKeys.Hash("issue\u001f" + i.Kind + "\u001f" + i.Path), Path = i.Path, Kind = i.Kind, Message = i.Message,
                IsFile = false, AtUtcTicks = nowTicks,
            })
            .DistinctBy(r => r.Id)
            .ToList();
        if (!cancelled)
        {
            db.Issues.DeleteAll();
            db.Issues.InsertBulk(keptIssues);
        }

        db.Issues.Upsert(newIssueRecords);
        db.Issues.Upsert(folderIssues);

        // 6. Rebuild albums/artists and folder stats (also after cancellation, so the library stays consistent).
        progress.Report(new ScanProgress("index", found.Count, toRead.Count, read, failed, null), force: true);
        // Nothing changed: the albums/artists index is still valid (saves a full pass on large libraries).
        var changed = added + updated + removedIds.Count > 0 || cancelled || (existing.Count > 0 && db.Albums.Count() == 0);
        var (albums, artists) = changed ? LibraryIndexer.Rebuild(db) : (db.Albums.Count(), db.Artists.Count());
        var counts = folders.ToDictionary(f => f.Id, f => db.Tracks.Count(t => t.FolderId == f.Id), StringComparer.Ordinal);
        foreach (var folder in folders)
        {
            folder.TrackCount = counts.GetValueOrDefault(folder.Id);
            if (!cancelled && availableIds.Contains(folder.Id))
            {
                folder.LastScanUtcTicks = nowTicks;
            }
        }

        db.Folders.Update(folders);
        db.Db.Checkpoint();
        progress.Report(new ScanProgress("done", found.Count, toRead.Count, read, failed, null), force: true);

        return new ScanResult
        {
            Folders = folders.Count,
            FoldersMissing = folders.Count - available.Count,
            FilesFound = found.Count,
            Added = added - moves.Count,
            Updated = updated,
            Unchanged = unchanged,
            Removed = removedIds.Count - moves.Count,
            Failed = failed,
            Moved = moves.Count,
            AmbiguousMoves = ambiguous,
            Fingerprinted = fingerprinted,
            PlaylistEntriesUpdated = playlistEntries,
            Moves = [.. moves.OrderBy(m => m.New.Path, StringComparer.Ordinal).Take(MaxMovesListed).Select(m => new TrackMove(m.Old.Path, m.New.Path, m.Old.Id, m.New.Id))],
            SkippedUnreadable = skippedUnreadable,
            Albums = albums,
            Artists = artists,
            Tracks = counts.Values.Sum(),
            Full = options.Full,
            Cancelled = cancelled,
            Elapsed = stopwatch.Elapsed,
            Issues = issues.ToList(),
        };
    }

    public const int MaxMovesListed = 200;

    /// <summary>
    /// Pairs vanished tracks with new files of the same fingerprint. One-to-one groups are moves. In a group with several
    /// candidates (duplicates), a vanished and a new file are paired only when they are the only two with that file name;
    /// everything else in the group stays removed + added (never a guess).
    /// </summary>
    internal static (List<(TrackRecord Old, TrackRecord New)> Moves, int Ambiguous) MatchMoves(List<TrackRecord> vanished, List<TrackRecord> added)
    {
        var moves = new List<(TrackRecord Old, TrackRecord New)>();
        var ambiguous = 0;
        var newByPrint = added.Where(t => t.Fingerprint.Length > 0).GroupBy(t => t.Fingerprint, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
        foreach (var group in vanished.Where(t => t.Fingerprint.Length > 0).GroupBy(t => t.Fingerprint, StringComparer.Ordinal))
        {
            if (!newByPrint.TryGetValue(group.Key, out var news))
            {
                continue;
            }

            var olds = group.ToList();
            if (olds.Count == 1 && news.Count == 1)
            {
                moves.Add((olds[0], news[0]));
                continue;
            }

            var paired = 0;
            foreach (var old in olds)
            {
                var sameNameNew = news.Where(n => n.FileName.Equals(old.FileName, StringComparison.OrdinalIgnoreCase)).ToList();
                var sameNameOld = olds.Count(o => o.FileName.Equals(old.FileName, StringComparison.OrdinalIgnoreCase));
                if (sameNameNew.Count == 1 && sameNameOld == 1)
                {
                    moves.Add((old, sameNameNew[0]));
                    paired++;
                }
            }

            ambiguous += olds.Count + news.Count - 2 * paired;
        }

        return (moves, ambiguous);
    }

    private sealed class ProgressThrottle(IProgress<ScanProgress>? target, TimeSpan interval)
    {
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private TimeSpan _last = TimeSpan.MinValue;

        public void Report(ScanProgress value, bool force = false)
        {
            if (target is null)
            {
                return;
            }

            var now = _clock.Elapsed;
            if (force || _last == TimeSpan.MinValue || now - _last >= interval)
            {
                _last = now;
                target.Report(value);
            }
        }
    }
}
