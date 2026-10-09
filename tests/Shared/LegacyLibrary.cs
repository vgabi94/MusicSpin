using LiteDB;
using MusicSpin.Core.Hosting;
using MusicSpin.Core.Library;

namespace MusicSpin.Tests.Shared;

/// <summary>
/// Turns a current library database into what MusicSpin wrote with library schema 2 (before GUID ids): track ids are the
/// 32-hex path hash and there is no PathKey; album/artist ids are 32-hex name hashes; playlist ids use the "N" format; no
/// schema marker; user version 2. Returns old id by path.
/// </summary>
internal static class LegacyLibrary
{
    public static Dictionary<string, string> DowngradeToSchema2(DataDirectory data)
    {
        using var db = new LiteDatabase(new ConnectionString { Filename = data.LibraryDatabaseFile, Connection = ConnectionType.Direct });
        var oldIds = new Dictionary<string, string>(StringComparer.Ordinal); // new id -> old id
        var byPath = new Dictionary<string, string>(StringComparer.Ordinal);
        var tracks = db.GetCollection("tracks");
        tracks.DropIndex("PathKey");
        var albumIds = db.GetCollection("albums").FindAll().ToDictionary(
            a => a["_id"].AsString,
            a => LibraryKeys.Hash("album\u001f" + a["AlbumArtistKey"].AsString + "\u001f" + a["TitleKey"].AsString),
            StringComparer.Ordinal);
        var docs = tracks.FindAll().ToList();
        tracks.DeleteAll();
        foreach (var doc in docs)
        {
            var oldId = LibraryKeys.PathKey(doc["Path"].AsString);
            oldIds[doc["_id"].AsString] = oldId;
            byPath[doc["Path"].AsString] = oldId;
            doc["_id"] = oldId;
            doc.Remove("PathKey");
            doc["AlbumId"] = albumIds.GetValueOrDefault(doc["AlbumId"].AsString, doc["AlbumId"].AsString);
            tracks.Insert(doc);
        }

        Rekey(db.GetCollection("albums"), d => albumIds[d["_id"].AsString]);
        Rekey(db.GetCollection("artists"), d => LibraryKeys.Hash("artist\u001f" + d["Key"].AsString));
        var playlists = db.GetCollection("playlists");
        foreach (var doc in playlists.FindAll().ToList())
        {
            playlists.Delete(doc["_id"]);
            doc["_id"] = Guid.Parse(doc["_id"].AsString).ToString("N");
            foreach (var entry in doc["Entries"].AsArray.Select(e => e.AsDocument))
            {
                if (entry.TryGetValue("TrackId", out var id) && id.IsString)
                {
                    entry["TrackId"] = oldIds.GetValueOrDefault(id.AsString, LibraryKeys.Hash(id.AsString));
                }
            }

            playlists.Insert(doc);
        }

        db.GetCollection("meta").DeleteAll();
        db.UserVersion = 2;
        db.Checkpoint();
        return byPath;
    }

    private static void Rekey(ILiteCollection<BsonDocument> collection, Func<BsonDocument, string> id)
    {
        var docs = collection.FindAll().ToList();
        collection.DeleteAll();
        foreach (var doc in docs)
        {
            doc["_id"] = id(doc);
            collection.Insert(doc);
        }
    }

    /// <summary>Copies the schema-2 fixture written by the previous MusicSpin version (Fixtures/schema2) into a data folder.</summary>
    public static void CopyFixture(DataDirectory data, bool withQueue = true)
    {
        Directory.CreateDirectory(data.Path);
        var source = Path.Combine(LibraryTestData.FixturesDirectory, "schema2");
        File.Copy(Path.Combine(source, "library.db"), data.LibraryDatabaseFile, overwrite: true);
        if (withQueue)
        {
            File.Copy(Path.Combine(source, "queue.json"), Path.Combine(data.Path, "queue.json"), overwrite: true);
        }
    }

    public static string FixtureFile(string name) => Path.Combine(LibraryTestData.FixturesDirectory, "schema2", name);
}
