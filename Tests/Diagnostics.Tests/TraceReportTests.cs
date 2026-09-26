using System.IO.Compression;
using System.Text.Json;
using LockscreenGif.Models.Diagnostics;
using LockscreenGif.Privileged;
using LockscreenGif.Services.Diagnostics;

namespace Diagnostics.Tests;

internal static class TraceReportTests
{
    public static async Task RunAsync(string directory)
    {
        const string cache = @"C:\SystemData\S-1-5-21-111-222-333-1001\ReadOnly";
        const string path = cache + @"\LockScreen_A\LockScreen.jpg";
        const string source = @"C:\Users\PrivatePerson\Secret animation.gif";
        var now = DateTimeOffset.UtcNow;
        var session = new DiagnosticSession
        {
            SourceName = "Secret animation.gif",
            SourcePath = source,
            Gif = new() { Sha256 = "AAAA" },
            Environment = new() { ["Cache directory"] = cache },
            ApplyResult = new()
            {
                Files =
                [
                    new()
                    {
                        Path = path,
                        Copied = true,
                        Verified = true,
                        VerifiedAt = now,
                        Sha256 = "AAAA",
                    },
                ],
            },
            ProcessTrace = new()
            {
                State = "Completed",
                StartedAt = now,
                EndedAt = now.AddSeconds(2),
            },
        };
        var trace = session.ProcessTrace;
        TraceAggregate Reader(bool own, bool resolved) =>
            new()
            {
                Path = path,
                ProcessId = 77,
                ProcessInstance = 2,
                ProcessName = "Reader.exe",
                Reads = 1,
                ReadBytes = 32,
                IsApp = own,
                AttributionResolved = resolved,
                LastReadStartedAt = now.AddSeconds(1),
                LastReadCompletedAt = now.AddSeconds(1.01),
            };
        var own = Reader(true, true);
        var unknown = Reader(false, false);
        trace.Files = [own, unknown];
        var findings = DiagnosticTraceFindings.Analyze(session).ToList();
        var reads = findings.Single(f => f.Title == "External image reads");
        Program.Check(
            reads.Severity == "Info" && reads.Confidence == "Inconclusive" && reads.Children.Single().Severity == "Info",
            "App reads and unresolved identities cannot establish an external read"
        );
        trace.Files.Add(Reader(false, true));
        findings = DiagnosticTraceFindings.Analyze(session).ToList();
        reads = findings.Single(f => f.Title == "External image reads");
        Program.Check(
            reads.Severity == "Success"
                && reads.Children.Single().Detail.Contains("PID 77")
                && reads.Detail.Contains("cannot confirm visible animation"),
            "Per-image external read passes identify the reader without claiming playback"
        );
        trace.EventsLost = 1;
        Program.Check(
            DiagnosticTraceFindings.Analyze(session).First().Severity == "Warning",
            "Loss counters override an otherwise completed coverage status"
        );
        var external = trace.Files.Last();
        external.Failures = 1;
        external.FastIoFallbacks = 1;
        external.FailureTiming = new TraceActivityTiming().Add(
            new(
                now.AddSeconds(1),
                path,
                "Open",
                77,
                2,
                "Reader.exe",
                1,
                false,
                true,
                null,
                null,
                0xc0000022,
                CompletedAt: now.AddSeconds(1.01)
            )
        );
        external.LastFailure = 0xc0000022;
        external.LastFailedOperation = "Open";
        external.Modifications = 1;
        external.LastAt = now.AddSeconds(1);
        external.ModificationTiming = new TraceActivityTiming().Add(
            new(now, path, "Write", 77, 2, "Reader.exe", 1, false, true, 32, 32, 0, CompletedAt: now.AddSeconds(1))
        );
        trace.Operations =
        [
            new(now, path, "Write", 77, 2, "Reader.exe", 1, false, true, 32, 32, 0, CompletedAt: now.AddSeconds(1)),
            new(
                now,
                source,
                "Read",
                77,
                2,
                @"C:\Users\PrivatePerson\Reader.exe",
                1,
                false,
                true,
                32,
                32,
                0,
                NewPath: @"\\PrivateServer\SecretShare\new.gif",
                CompletedAt: now.AddSeconds(1)
            ),
        ];
        session.Snapshots =
        [
            new()
            {
                Timestamp = now,
                Files =
                [
                    new()
                    {
                        Path = path,
                        Stable = true,
                        HashSource = "Full read",
                        HashReadAt = now,
                        Sha256 = "AAAA",
                    },
                ],
            },
            new()
            {
                Timestamp = now.AddSeconds(2),
                Files =
                [
                    new()
                    {
                        Path = path,
                        Stable = true,
                        HashSource = "Full read",
                        HashReadAt = now.AddSeconds(2),
                        Sha256 = "BBBB",
                    },
                ],
            },
        ];
        findings = DiagnosticTraceFindings.Analyze(session).ToList();
        Program.Check(
            findings.Single(f => f.Title == "Image access failures").Children.Single().Detail.Contains("0xC0000022"),
            "Access failures retain the operation and Windows status"
        );
        var modification = findings.Single(f => f.Title == "Other file activity").Children.Single();
        Program.Check(
            modification.Severity == "Info"
                && modification.Detail.Contains("differed")
                && modification.Detail.Contains("do not establish changed image contents"),
            "Post-verification hash differences provide context while operation findings remain informational"
        );
        session.Findings = findings;
        var report = Path.Combine(directory, "trace-report.zip");
        await DiagnosticReportWriter.ExportAsync(session, report);
        using var zip = ZipFile.OpenRead(report);
        using var reader = new StreamReader(zip.GetEntry("session.json")!.Open());
        var json = await reader.ReadToEndAsync();
        Program.Check(
            !json.Contains("PrivatePerson")
                && !json.Contains("PrivateServer")
                && !json.Contains("SecretShare")
                && !json.Contains("Secret animation")
                && !json.Contains("S-1-5-21"),
            "Process, source, cache and rename paths are redacted in exported trace fields"
        );
        using var document = JsonDocument.Parse(json);
        var exported = document.RootElement;
        Program.Check(
            exported.GetProperty("ApplyResult").GetProperty("Files")[0].GetProperty("VerifiedAt").GetDateTimeOffset() == now
                && exported.GetProperty("ProcessTrace").GetProperty("Files")[2].GetProperty("LastReadStartedAt").GetDateTimeOffset()
                    == now.AddSeconds(1)
                && exported.GetProperty("ProcessTrace").GetProperty("Files")[2].GetProperty("LastReadCompletedAt").GetDateTimeOffset()
                    == now.AddSeconds(1.01)
                && exported.GetProperty("ProcessTrace").GetProperty("Files")[2].GetProperty("FastIoFallbacks").GetInt64() == 1
                && exported
                    .GetProperty("ProcessTrace")
                    .GetProperty("Files")[2]
                    .GetProperty("FailureTiming")
                    .GetProperty("LatestStarted")
                    .GetProperty("StartedAt")
                    .GetDateTimeOffset() == now.AddSeconds(1)
                && exported
                    .GetProperty("ProcessTrace")
                    .GetProperty("Files")[2]
                    .GetProperty("ModificationTiming")
                    .GetProperty("LatestCompleted")
                    .GetProperty("Status")
                    .GetUInt32() == 0,
            "Exports preserve verification and read boundaries needed to distinguish file generations"
        );
        Program.Check(
            document.RootElement.GetProperty("SchemaVersion").GetInt32() == 2
                && document.RootElement.GetProperty("ProcessTrace").GetProperty("Operations").GetArrayLength() == 2,
            "Schema 2 exports detailed trace operations only through the report action"
        );
    }
}
