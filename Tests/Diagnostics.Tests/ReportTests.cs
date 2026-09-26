using System.IO.Compression;
using System.Text.Json;
using LockscreenGif.Models;
using LockscreenGif.Models.Diagnostics;
using LockscreenGif.Services.Diagnostics;

namespace Diagnostics.Tests;

internal static class ReportTests
{
    internal static async Task RunAsync(string directory)
    {
        var session = new DiagnosticSession
        {
            SourceName = "Private vacation.gif",
            SourcePath = @"C:\Users\SecretUser\Private vacation.gif",
            Environment = new() { ["Cache directory"] = @"C:\ProgramData\Microsoft\Windows\SystemData\S-1-5-21-111-222-333-1001\ReadOnly" },
            Error = @"Failed reading C:\Users\SecretUser\private\a.gif",
            Events =
            [
                new(
                    DateTimeOffset.UtcNow,
                    0,
                    "File",
                    @"Observed C:\ProgramData\Microsoft\Windows\SystemData\S-1-5-21-111-222-333-1001\ReadOnly\LockScreen_A\LockScreen___1920_1080.jpg"
                ),
            ],
            Findings = [new("Test", @"Failure S-1-5-21-111-222-333-1001; Private vacation.gif")],
            ApplyResult = new LockscreenApplyResult
            {
                Success = true,
                Files =
                [
                    new()
                    {
                        Path = @"C:\cache\LockScreen___1920_1080.jpg",
                        Copied = true,
                        Verified = true,
                        Sha256 = "ABC",
                    },
                ],
            },
        };
        var report = Path.Combine(directory, "report.zip");
        await DiagnosticReportWriter.ExportAsync(session, report);
        using var zip = ZipFile.OpenRead(report);
        Program.Check(
            zip.Entries.Select(e => e.FullName).Order().SequenceEqual(new[] { "events.jsonl", "session.json", "summary.md" }),
            "Report contains only intended text files"
        );
        foreach (var entry in zip.Entries)
        {
            using var reader = new StreamReader(entry.Open());
            var content = await reader.ReadToEndAsync();
            Program.Check(
                !content.Contains("SecretUser")
                    && !content.Contains("Private vacation")
                    && !content.Contains("S-1-5-21")
                    && !content.Contains("C:\\"),
                "Privacy redaction in " + entry.FullName
            );
            if (entry.FullName == "session.json")
            {
                Program.Check(content.Contains('\n'), "Exported session JSON remains readable and indented");
                using var json = JsonDocument.Parse(content);
                Program.Check(json.RootElement.GetProperty("SchemaVersion").GetInt32() == 2, "Report schema recorded");
                Program.Check(content.Contains("LockScreen___1920_1080.jpg"), "Cache filename preserved for diagnosis");
                Program.Check(
                    json.RootElement.GetProperty("ApplyResult").GetProperty("Files").GetArrayLength() == 1,
                    "Per-file apply evidence included"
                );
            }
        }
        Program.Check(session.SourcePath.Contains("SecretUser"), "Export preserves live session values");
        var clone = JsonSerializer.Deserialize<DiagnosticSession>(JsonSerializer.Serialize(session))!;
        Program.Check(clone.ApplyResult!.Files.Count == 1, "Snapshot serialization retains apply file evidence");
        var redactor = new DiagnosticRedactor(session);
        var first = redactor.Redact(@"D:\OtherUser\other.gif");
        Program.Check(
            first == redactor.Redact(@"D:\OtherUser\other.gif") && !first.Contains("OtherUser"),
            "Personal path aliases are stable"
        );
        var copied = DiagnosticReportWriter.CreateSummary(session);
        Program.Check(!copied.Contains("SecretUser") && !copied.Contains("Private vacation"), "Copied summary is redacted");
    }
}
