using System.Security.Cryptography;
using System.Text;
using LockscreenGif.Models.Diagnostics;

namespace LockscreenGif.Services.Diagnostics;

internal static class CacheFileReader
{
    public static async Task<CacheFileEvidence> ReadAsync(string path, CancellationToken token)
    {
        CacheFileEvidence result = new() { Path = path };
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var before = new FileInfo(path);
            var length = before.Length;
            var written = before.LastWriteTimeUtc;
            var created = before.CreationTimeUtc;
            await using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                // Large GIFs otherwise generate thousands of 64 KiB baseline reads.
                FileShare.ReadWrite | FileShare.Delete,
                1024 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan
            );
            var header = new byte[8];
            var count = await stream.ReadAsync(header, token);
            stream.Position = 0;
            var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, token));
            var after = new FileInfo(path);
            var stable =
                after.Exists
                && after.Length == length
                && after.LastWriteTimeUtc == written
                && after.CreationTimeUtc == created
                && stream.Length == length;
            result = new CacheFileEvidence
            {
                Path = path,
                Length = length,
                LastWriteUtc = written,
                CreationUtc = created,
                Sha256 = hash,
                Format = Format(header.AsSpan(0, count)),
                Stable = stable,
                HashSource = attempt == 0 ? "Full read" : "Full read after retry",
                HashReadAt = DateTimeOffset.UtcNow,
                Error = stable ? null : "File changed during inspection; comparison is indeterminate.",
            };
            if (stable)
            {
                break;
            }
        }
        return result;
    }

    internal static string Format(ReadOnlySpan<byte> header)
    {
        if (header.Length >= 6 && (Encoding.ASCII.GetString(header[..6]) is "GIF87a" or "GIF89a"))
        {
            return "GIF";
        }

        if (header.Length >= 3 && header[0] == 255 && header[1] == 216 && header[2] == 255)
        {
            return "JPEG";
        }

        if (header.Length >= 4 && header[0] == 137 && header[1] == 80 && header[2] == 78 && header[3] == 71)
        {
            return "PNG";
        }

        return header.Length == 0 ? "Empty" : "Unknown";
    }
}
