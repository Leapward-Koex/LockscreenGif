using System.IO.Compression;
using System.Text.Json;
using LockscreenGif.Models.Diagnostics;
using LockscreenGif.Services.Diagnostics;

namespace Diagnostics.Tests;

internal static class DiagnosticLogAttachmentTests
{
    internal static async Task RunAsync(string directory)
    {
        var logs = Path.Combine(directory, "diagnostic-log-attachments");
        Directory.CreateDirectory(logs);
        var session = new DiagnosticSession
        {
            SourceName = "Private holiday.gif",
            SourcePath = @"C:\Users\SyntheticPerson\Private holiday.gif",
        };
        var text = $"2026-09-26 [ERROR] Selected {session.SourcePath}\nSID S-1-5-21-123-456-789-1000\nReading Private holiday.gif\n";
        var source = Path.Combine(logs, "app_2026-09-26.log");
        await File.WriteAllTextAsync(source, text);
        var lockedPath = Path.Combine(logs, "app_2026-09-25.log");
        await File.WriteAllTextAsync(lockedPath, "unreadable\n");
        await File.WriteAllTextAsync(Path.Combine(logs, "app_PersonalFilename.log"), "A second application log.\n");
        await File.WriteAllTextAsync(Path.Combine(logs, "analytics.json"), "not included");
        await File.WriteAllTextAsync(Path.Combine(logs, "CrashDump.dmp"), "not included");
        var report = Path.Combine(directory, "with-app-logs.zip");
        using (var locked = new FileStream(lockedPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        using (var writer = new FileStream(source, FileMode.Append, FileAccess.Write, FileShare.Read))
        {
            await DiagnosticReportWriter.ExportAsync(session, report, getLogDirectory: () => logs);
        }
        using (var zip = ZipFile.OpenRead(report))
        {
            Program.Check(
                zip.GetEntry("summary.md") is not null
                    && zip.GetEntry("session.json") is not null
                    && zip.GetEntry("events.jsonl") is not null,
                "Adding app logs retains all diagnostic evidence files"
            );
            var attached = await ReadAsync(zip, "logs/app_2026-09-26.log");
            Program.Check(
                attached.Contains("[ERROR]") && attached.Contains("<source GIF>") && attached.Contains("<selected GIF>"),
                "Application logs are attached from an active writer and redacted with the report's source aliases"
            );
            foreach (var entry in zip.Entries)
            {
                var content = await ReadAsync(zip, entry.FullName);
                Program.Check(
                    !content.Contains("SyntheticPerson")
                        && !content.Contains("Private holiday")
                        && !content.Contains("S-1-5-21")
                        && !content.Contains("PersonalFilename"),
                    "App-log attachment redaction: " + entry.FullName
                );
            }
            Program.Check(
                zip.Entries.All(entry =>
                    !entry.FullName.Contains("PersonalFilename")
                    && !entry.FullName.EndsWith(".dmp")
                    && !entry.FullName.Contains("analytics.json")
                ),
                "Diagnostic log entry names are safe and unrelated files stay excluded"
            );
            using var manifest = JsonDocument.Parse(await ReadAsync(zip, "logs/manifest.json"));
            Program.Check(
                manifest
                    .RootElement.GetProperty("Files")
                    .EnumerateArray()
                    .Any(file => file.GetProperty("Status").GetString() == "read_failed"),
                "An unreadable log is explicit in the manifest without failing the report"
            );
            Program.Check(
                zip.GetEntry("logs/app_2026-09-25.log") is null,
                "Unreadable log contents are never represented as a complete attachment"
            );
        }
        Program.Check(await File.ReadAllTextAsync(source) == text, "Diagnostic log export never modifies original application logs");

        var unavailableReport = Path.Combine(directory, "unavailable-app-logs.zip");
        await DiagnosticReportWriter.ExportAsync(
            session,
            unavailableReport,
            getLogDirectory: () => throw new UnauthorizedAccessException("synthetic private directory")
        );
        using (var zip = ZipFile.OpenRead(unavailableReport))
        {
            using var manifest = JsonDocument.Parse(await ReadAsync(zip, "logs/manifest.json"));
            Program.Check(
                zip.GetEntry("summary.md") is not null
                    && manifest.RootElement.GetProperty("DirectoryStatus").GetString() == "access_denied",
                "An unavailable log folder does not prevent diagnostic export"
            );
            Program.Check(
                !(await ReadAsync(zip, "logs/manifest.json")).Contains("private"),
                "Collection failures never expose raw exception messages in the manifest"
            );
        }
        await LimitsAsync(directory);
    }

    private static async Task LimitsAsync(string directory)
    {
        var logs = Path.Combine(directory, "bounded-diagnostic-logs");
        Directory.CreateDirectory(logs);
        for (var index = 1; index <= 10; index++)
        {
            var path = Path.Combine(logs, $"app_2026-01-{index:00}.log");
            var text =
                index == 10
                    ? new string('q', DiagnosticLogAttachments.MaximumBytesPerFile + 128)
                        + "\nA complete recent log line.\npartial-private-last-line"
                    : "An older log.\n";
            await File.WriteAllTextAsync(path, text);
            File.SetLastWriteTimeUtc(path, new DateTime(2026, 1, index, 12, 0, 0, DateTimeKind.Utc));
        }
        var report = Path.Combine(directory, "bounded-app-logs.zip");
        await DiagnosticReportWriter.ExportAsync(new DiagnosticSession(), report, getLogDirectory: () => logs);
        using var zip = ZipFile.OpenRead(report);
        using var manifest = JsonDocument.Parse(await ReadAsync(zip, "logs/manifest.json"));
        var files = manifest.RootElement.GetProperty("Files").EnumerateArray().ToArray();
        Program.Check(
            files.Length == DiagnosticLogAttachments.MaximumFiles && manifest.RootElement.GetProperty("OmittedFiles").GetInt32() == 2,
            "The newest eight logs are included and older omissions are counted"
        );
        Program.Check(
            files[0].GetProperty("Truncated").GetBoolean()
                && files[0].GetProperty("BytesRead").GetInt32() == DiagnosticLogAttachments.MaximumBytesPerFile,
            "Large log snapshots use bounded tails and disclose truncation"
        );
        var tail = await ReadAsync(zip, "logs/app_2026-01-10.log");
        Program.Check(
            tail == "A complete recent log line.\n" && files[0].GetProperty("PartialLinesOmitted").GetBoolean(),
            "Partial lines at either snapshot boundary cannot leak cut identifiers"
        );
        Program.Check(zip.GetEntry("logs/app_2026-01-01.log") is null, "The oldest logs are excluded when the file limit is reached");
    }

    private static async Task<string> ReadAsync(ZipArchive zip, string name)
    {
        using var reader = new StreamReader(zip.GetEntry(name)!.Open());
        return await reader.ReadToEndAsync();
    }
}
