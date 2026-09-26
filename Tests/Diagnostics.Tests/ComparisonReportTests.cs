using System.IO.Compression;
using LockscreenGif.Models.Diagnostics;
using LockscreenGif.Services.Diagnostics;

namespace Diagnostics.Tests;

internal static class ComparisonReportTests
{
    internal static async Task RunAsync(string directory)
    {
        var previous = new DiagnosticSession
        {
            SourceName = "Previous private.gif",
            SourcePath = @"D:\Original\Previous private.gif",
            Gif = new() { Sha256 = "MATCH" },
            UseWindowsApi = false,
            Events = [new(DateTimeOffset.UtcNow, 0, "File", @"D:\Someone\other.gif")],
        };
        var current = new DiagnosticSession
        {
            ComparisonSessionId = previous.Id,
            SourceName = "Current private.gif",
            SourcePath = @"D:\Original\Current private.gif",
            Gif = new() { Sha256 = "MATCH" },
            UseWindowsApi = true,
            Events = [new(DateTimeOffset.UtcNow, 0, "File", @"D:\Someone\other.gif")],
        };
        var destination = Path.Combine(directory, "comparison.zip");
        await DiagnosticReportWriter.ExportAsync(current, destination, previous);
        using (var zip = ZipFile.OpenRead(destination))
        {
            Program.Check(
                zip.Entries.Count == 6
                    && zip.GetEntry("comparison/summary.md") is not null
                    && zip.GetEntry("comparison/session.json") is not null
                    && zip.GetEntry("comparison/conditions.txt") is not null,
                "Comparison export contains both sessions and conditions"
            );
            foreach (var entry in zip.Entries)
            {
                using var reader = new StreamReader(entry.Open());
                var value = await reader.ReadToEndAsync();
                Program.Check(
                    !value.Contains("private") && !value.Contains("Someone") && !value.Contains("D:\\"),
                    "Comparison entry is redacted: " + entry.FullName
                );
            }
            using var conditions = new StreamReader(zip.GetEntry("comparison/conditions.txt")!.Open());
            var text = await conditions.ReadToEndAsync();
            Program.Check(
                text.Contains("Same source hash: True") && text.Contains("False -> True") && text.Contains("not automatically reset"),
                "Comparison records source, toggle, and baseline conditions"
            );
        }
        current.Gif.Sha256 = previous.Gif.Sha256 = "";
        await DiagnosticReportWriter.ExportAsync(current, destination, previous);
        using var unknown = ZipFile.OpenRead(destination);
        using var unknownConditions = new StreamReader(unknown.GetEntry("comparison/conditions.txt")!.Open());
        Program.Check(
            (await unknownConditions.ReadToEndAsync()).Contains("Same source hash: False"),
            "Missing source hashes never establish a comparison match"
        );
    }
}
