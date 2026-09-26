using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace LockscreenGif.Services.Diagnostics;

internal static class DiagnosticLogAttachments
{
    internal const int MaximumFiles = 8;
    internal const int MaximumBytesPerFile = 2 * 1024 * 1024;

    internal static Task WriteAsync(ZipArchive zip, Func<string> getLogDirectory, DiagnosticRedactor redactor) =>
        Task.Run(() => WriteCoreAsync(zip, getLogDirectory, redactor));

    private static async Task WriteCoreAsync(ZipArchive zip, Func<string> getLogDirectory, DiagnosticRedactor redactor)
    {
        var manifest = new LogManifest();
        FileInfo[] files = [];
        try
        {
            files = new DirectoryInfo(getLogDirectory())
                .GetFiles("app_*.log", SearchOption.TopDirectoryOnly)
                .OrderByDescending(file => file.LastWriteTimeUtc)
                .ThenBy(file => file.Name, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            manifest.DirectoryStatus = files.Length == 0 ? "no_logs" : "available";
            manifest.OmittedFiles = Math.Max(0, files.Length - MaximumFiles);
        }
        catch (Exception ex)
        {
            manifest.DirectoryStatus = FailureCategory(ex);
        }

        foreach (var file in files.Take(MaximumFiles))
        {
            // Keep the logger's standard date names; alias unexpected names so entry names cannot expose personal text.
            var name = Regex.IsMatch(file.Name, @"^app_[0-9]{4}-[0-9]{2}-[0-9]{2}\.log$", RegexOptions.CultureInvariant)
                ? file.Name
                : $"application-{manifest.Files.Count + 1:00}.log";
            var details = new LogFile { Entry = "logs/" + name };
            manifest.Files.Add(details);
            string text;
            try
            {
                if ((file.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    details.Status = "skipped_link";
                    continue;
                }
                await using var input = new FileStream(
                    file.FullName,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete,
                    65536,
                    useAsync: true
                );
                var length = input.Length;
                details.SourceBytes = length;
                var bytes = new byte[(int)Math.Min(length, MaximumBytesPerFile)];
                input.Position = length - bytes.Length;
                details.Truncated = input.Position > 0;
                await input.ReadExactlyAsync(bytes).ConfigureAwait(false);
                details.BytesRead = bytes.Length;
                var start = 0;
                if (details.Truncated)
                {
                    // Discard the leading partial line instead of exposing a path/identifier cut across the tail boundary.
                    var newline = Array.IndexOf(bytes, (byte)'\n');
                    start = newline < 0 ? bytes.Length : newline + 1;
                    details.PartialLinesOmitted = true;
                }
                var end = Array.LastIndexOf(bytes, (byte)'\n') + 1;
                if (end < bytes.Length)
                {
                    details.PartialLinesOmitted = true;
                }
                text = end > start ? Encoding.UTF8.GetString(bytes, start, end - start).TrimStart('\uFEFF') : string.Empty;
                details.Status = text.Length == 0 && bytes.Length > 0 ? "no_complete_lines" : "included";
            }
            catch (Exception ex)
            {
                details.Status = FailureCategory(ex);
                continue;
            }

            // Redact before writing. ZIP/output failures must still fail the export, rather than masquerading as missing logs.
            await WriteEntryAsync(zip, details.Entry, redactor.Redact(text)).ConfigureAwait(false);
        }
        await WriteEntryAsync(
                zip,
                "logs/manifest.json",
                JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true })
            )
            .ConfigureAwait(false);
    }

    private static async Task WriteEntryAsync(ZipArchive zip, string name, string text)
    {
        await using var entry = zip.CreateEntry(name, CompressionLevel.Fastest).Open();
        await using var writer = new StreamWriter(entry, new UTF8Encoding(false));
        await writer.WriteAsync(text).ConfigureAwait(false);
    }

    private static string FailureCategory(Exception error) =>
        error switch
        {
            FileNotFoundException or DirectoryNotFoundException => "not_found",
            UnauthorizedAccessException or System.Security.SecurityException => "access_denied",
            IOException => "read_failed",
            _ => "unavailable",
        };

    private sealed class LogManifest
    {
        public DateTimeOffset CapturedAt { get; } = DateTimeOffset.UtcNow;
        public int MaxFiles { get; } = MaximumFiles;
        public int MaxBytesPerFile { get; } = MaximumBytesPerFile;
        public string Scope { get; } =
            "Recent application logs at export time; may include activity outside the diagnostic session. Redacted complete lines only.";
        public string DirectoryStatus { get; set; } = "unavailable";
        public int OmittedFiles { get; set; }
        public List<LogFile> Files { get; } = [];
    }

    private sealed class LogFile
    {
        public string Entry { get; init; } = "";
        public string Status { get; set; } = "unavailable";
        public long? SourceBytes { get; set; }
        public int BytesRead { get; set; }
        public bool Truncated { get; set; }
        public bool PartialLinesOmitted { get; set; }
    }
}
