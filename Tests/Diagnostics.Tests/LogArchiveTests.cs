using System.IO.Compression;
using System.Text;
using LockscreenGif.Services;

namespace Diagnostics.Tests;

internal static class LogArchiveTests
{
    public static async Task RunAsync(string directory)
    {
        var logs = Path.Combine(directory, "log-export-source");
        Directory.CreateDirectory(logs);
        var livePath = Path.Combine(logs, "app_live.log");
        const string liveText = "A log still open for writing.\n";
        await File.WriteAllTextAsync(livePath, liveText);
        var largeText = new string('x', 150_000) + "\nFinal line.\n";
        await File.WriteAllTextAsync(Path.Combine(logs, "app_older.log"), largeText);
        await File.WriteAllTextAsync(Path.Combine(logs, "analytics.json"), "synthetic settings");
        await File.WriteAllTextAsync(Path.Combine(logs, "CrashDump.dmp"), "synthetic dump");
        Directory.CreateDirectory(Path.Combine(logs, "nested"));
        await File.WriteAllTextAsync(Path.Combine(logs, "nested", "app_other.log"), "not an application log in the root folder");

        // Match the logger's sharing behavior: another read is allowed while writing remains open.
        await using (var writer = new FileStream(livePath, FileMode.Append, FileAccess.Write, FileShare.Read))
        {
            var archivePath = Path.Combine(directory, "logs.zip");
            var count = await LogArchiveWriter.WriteAsync(logs, archivePath);
            Program.Check(count == 2, "Log export includes only top-level app_*.log files");
            using var archive = ZipFile.OpenRead(archivePath);
            Program.Check(
                archive.Entries.Count == 2 && archive.Entries.All(entry => !entry.FullName.Contains('/') && !entry.FullName.Contains('\\')),
                "Log ZIP contains filenames without local directory paths"
            );
            using var liveReader = new StreamReader(archive.GetEntry("app_live.log")!.Open());
            using var olderReader = new StreamReader(archive.GetEntry("app_older.log")!.Open());
            Program.Check(await liveReader.ReadToEndAsync() == liveText, "Active log can be exported while its writer is open");
            Program.Check(await olderReader.ReadToEndAsync() == largeText, "Multi-buffer log contents survive ZIP export exactly");
            await writer.WriteAsync(Encoding.UTF8.GetBytes("Logging continues.\n"));
        }
        Program.Check(
            await File.ReadAllTextAsync(livePath) == liveText + "Logging continues.\n",
            "Export leaves source logs intact and writable"
        );

        var existing = Path.Combine(directory, "existing-archive.zip");
        await File.WriteAllTextAsync(existing, "existing data");
        await ExpectFailureAsync<IOException>(() => LogArchiveWriter.WriteAsync(logs, existing));
        Program.Check(
            await File.ReadAllTextAsync(existing) == "existing data",
            "Archive generation never overwrites an existing destination"
        );

        var empty = Path.Combine(directory, "empty-log-export");
        Directory.CreateDirectory(empty);
        var emptyArchive = Path.Combine(directory, "empty.zip");
        await ExpectFailureAsync<InvalidOperationException>(() => LogArchiveWriter.WriteAsync(empty, emptyArchive));
        Program.Check(!File.Exists(emptyArchive), "No logs produces a useful failure without an empty ZIP");

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var cancelledArchive = Path.Combine(directory, "cancelled.zip");
        await ExpectFailureAsync<OperationCanceledException>(() => LogArchiveWriter.WriteAsync(logs, cancelledArchive, cancelled.Token));
        Program.Check(!File.Exists(cancelledArchive), "Cancelled log export does not create an archive");

        using (var locked = new FileStream(livePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            await ExpectFailureAsync<IOException>(() => LogArchiveWriter.WriteAsync(logs, Path.Combine(directory, "unreadable.zip")));
        }
        Program.Check(true, "An unreadable log fails export instead of silently reporting an incomplete ZIP as saved");
    }

    private static async Task ExpectFailureAsync<T>(Func<Task<int>> operation)
        where T : Exception
    {
        try
        {
            await operation();
        }
        catch (T)
        {
            return;
        }
        throw new InvalidOperationException("Expected log export failure: " + typeof(T).Name);
    }
}
