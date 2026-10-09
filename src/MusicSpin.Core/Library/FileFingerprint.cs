using System.Globalization;
using System.Security.Cryptography;

namespace MusicSpin.Core.Library;

/// <summary>
/// Cheap content fingerprint used to recognise a file after a rename or move: the size plus a SHA-256 (truncated to 128 bits)
/// of the first and last 64 KB (the whole file when it is smaller than 128 KB). Reads at most 128 KB per file. Tag edits
/// usually change the first chunk (and often the size), so an edited file gets a different fingerprint.
/// </summary>
public static class FileFingerprint
{
    public const int ChunkSize = 64 * 1024;

    /// <summary>The fingerprint (<c>size-hex:hash-hex</c>), or null when the file cannot be read.</summary>
    public static string? Compute(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, bufferSize: 1);
            var size = stream.Length;
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[ChunkSize];
            Append(stream, hash, buffer, (int)Math.Min(ChunkSize, size));
            if (size > ChunkSize)
            {
                stream.Position = Math.Max(ChunkSize, size - ChunkSize);
                Append(stream, hash, buffer, (int)(size - stream.Position));
            }

            Span<byte> sizeBytes = stackalloc byte[8];
            BitConverter.TryWriteBytes(sizeBytes, size);
            hash.AppendData(sizeBytes);
            return size.ToString("x", CultureInfo.InvariantCulture) + ":" + Convert.ToHexStringLower(hash.GetHashAndReset())[..32];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static void Append(FileStream stream, IncrementalHash hash, byte[] buffer, int count)
    {
        var total = 0;
        while (total < count)
        {
            var n = stream.Read(buffer, total, count - total);
            if (n == 0)
            {
                break; // truncated while reading: hash what we got
            }

            total += n;
        }

        hash.AppendData(buffer, 0, total);
    }
}
