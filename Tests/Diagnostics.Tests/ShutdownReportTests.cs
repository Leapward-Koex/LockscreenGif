using System.IO.Compression;
using System.Text.Json;
using LockscreenGif.Models.Diagnostics;
using LockscreenGif.Privileged;
using LockscreenGif.Services.Diagnostics;

namespace Diagnostics.Tests;

internal static class ShutdownReportTests
{
    public static async Task RunAsync(string directory)
    {
        var at = DateTimeOffset.UtcNow;
        var session = new DiagnosticSession
        {
            ProcessTrace = new()
            {
                State = "Incomplete",
                StartedAt = at,
                EndedAt = at.AddSeconds(8),
                Shutdown = new()
                {
                    StopRequestedAt = at.AddSeconds(1),
                    NativeStopReturnedAt = at.AddSeconds(2),
                    NativeStopStatus = 0,
                    NativeStopElapsedMilliseconds = 10,
                    NativeStopBuffers = new(32, 30, 4, 0, 0),
                    DrainDeadlineExceededAt = at.AddSeconds(7),
                    StageAtDeadline = "Processing",
                    WorkerStage = "Finished",
                    ConsumerCompletedNormally = false,
                    ConsumerReturnedAt = at.AddSeconds(8),
                    CleanupCompletedAt = at.AddSeconds(8),
                    AtNativeStopReturn = new() { CallbacksStarted = 50, CallbacksFinished = 50 },
                    AtDrainDeadline = new() { CallbacksStarted = 70, CallbacksFinished = 69 },
                    Current = new()
                    {
                        CallbacksStarted = 70,
                        CallbacksFinished = 70,
                        LatestEventTimestamp = at.AddSeconds(1),
                    },
                    ConsumerFailureType = @"C:\Users\PrivatePerson\secret.txt",
                },
            },
        };
        var path = Path.Combine(directory, "shutdown-report.zip");
        Program.Check(!File.Exists(path), "Shutdown telemetry does not create an export automatically");
        await DiagnosticReportWriter.ExportAsync(session, path);
        using var zip = ZipFile.OpenRead(path);
        using var reader = new StreamReader(zip.GetEntry("session.json")!.Open());
        var json = await reader.ReadToEndAsync();
        var parsed = JsonSerializer.Deserialize<DiagnosticSession>(json)!;
        Program.Check(
            parsed.SchemaVersion == 2
                && parsed.ProcessTrace.Shutdown!.StopRequestedAt == at.AddSeconds(1)
                && parsed.ProcessTrace.Shutdown.AtDrainDeadline!.CallbackInProgress
                && parsed.ProcessTrace.Shutdown.AtNativeStopReturn!.CallbacksFinished == 50
                && parsed.ProcessTrace.Shutdown.NativeStopBuffers!.BuffersWritten == 4,
            "Schema 2 round-trip preserves separate shutdown checkpoints, progress and native statistics"
        );
        Program.Check(
            !json.Contains("PrivatePerson")
                && !json.Contains("secret.txt")
                && session.ProcessTrace.Shutdown!.ConsumerFailureType!.Contains("PrivatePerson"),
            "Shutdown fields pass through export redaction without mutating the current session"
        );
        using var summaryReader = new StreamReader(zip.GetEntry("summary.md")!.Open());
        var summary = await summaryReader.ReadToEndAsync();
        Program.Check(
            summary.Contains("worker cleanup time")
                && summary.Contains("stage then: Processing")
                && summary.Contains("not counts of buffers processed")
                && summary.Contains("69 finished"),
            "Summary distinguishes collector lifetime, deadline progress and native buffer statistics"
        );
        Program.Check(
            DiagnosticTraceFindings.Analyze(session).First().Severity == "Warning",
            "Zero drops and completed cleanup never hide an incomplete trace"
        );
        Program.Check(
            JsonSerializer.Deserialize<ProcessTraceEvidence>("{}")!.Shutdown is null,
            "Legacy trace evidence leaves shutdown measurements unavailable"
        );
    }
}
