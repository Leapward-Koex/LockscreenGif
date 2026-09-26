using System.IO.Compression;
using System.Text.Json;
using LockscreenGif.Models.Diagnostics;
using LockscreenGif.Services.Diagnostics;

namespace Diagnostics.Tests;

// Optional local inspection of existing exports. Reports are never extracted or saved as fixtures.
internal static class ReportReplay
{
    internal static void Analyze(string path)
    {
        using var archive = ZipFile.OpenRead(path);
        var entry = archive.Entries.Single(item => item.FullName == "session.json");
        if (entry.Length > 128 * 1024 * 1024)
        {
            throw new InvalidDataException("Report exceeds the 128 MiB analysis limit.");
        }

        using var stream = entry.Open();
        var session = JsonSerializer.Deserialize<DiagnosticSession>(stream) ?? throw new InvalidDataException("Report is empty.");
        session.Findings = DiagnosticAnalyzer.Analyze(session);
        Console.WriteLine(
            JsonSerializer.Serialize(
                new
                {
                    Report = Path.GetFileName(path),
                    Findings = session.Findings.Select(finding => new
                    {
                        finding.Title,
                        finding.Severity,
                        finding.Confidence,
                        ChildCount = finding.Children.Count,
                        WarningChildren = finding.Children.Count(child => child.Severity is "Warning" or "Error"),
                    }),
                }
            )
        );
    }
}
