using System.IO.Compression;

namespace LockscreenGif.Services;

/// <summary>Copies application logs to a caller-owned temporary ZIP, without blocking ongoing logging.</summary>
public static class LogArchiveWriter
{
    public static Task<int> WriteAsync(string logDirectory, string archivePath, CancellationToken cancellationToken = default) =>
        Task.Run(() => WriteCoreAsync(logDirectory, archivePath, cancellationToken), cancellationToken);

    private static async Task<int> WriteCoreAsync(string logDirectory, string archivePath, CancellationToken cancellationToken)
    {
        var logs = Directory.GetFiles(logDirectory, "app_*.log", SearchOption.TopDirectoryOnly);
        if (logs.Length == 0)
        {
            throw new InvalidOperationException("No application logs are available to export yet.");
        }

        Array.Sort(logs, StringComparer.OrdinalIgnoreCase);
        await using var output = new FileStream(archivePath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, useAsync: true);
        using var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);
        var buffer = new byte[65536];
        foreach (var path in logs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await using var input = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                65536,
                useAsync: true
            );
            // Bound each copy to its initial length so an actively growing log cannot keep export running forever.
            var remaining = input.Length;
            var entry = archive.CreateEntry(Path.GetFileName(path), CompressionLevel.Fastest);
            await using var target = entry.Open();
            while (remaining > 0)
            {
                var read = await input
                    .ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), cancellationToken)
                    .ConfigureAwait(false);
                if (read == 0)
                {
                    throw new IOException("A log file changed while it was being exported. Please try again.");
                }
                await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                remaining -= read;
            }
        }
        return logs.Length;
    }
}
