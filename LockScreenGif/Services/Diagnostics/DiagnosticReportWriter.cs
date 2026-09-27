using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using LockscreenGif.Models.Diagnostics;

namespace LockscreenGif.Services.Diagnostics;

public static class DiagnosticReportWriter
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    /// <summary>Export a caller-owned immutable snapshot, never the active mutable session.</summary>
    public static async Task ExportAsync(
        DiagnosticSession session,
        string destinationPath,
        DiagnosticSession? comparison = null,
        Func<string>? getLogDirectory = null
    )
    {
        var redactor = new DiagnosticRedactor(session, comparison);
        var node = JsonSerializer.SerializeToNode(session, JsonOptions)!.AsObject();
        node["SchemaVersion"] = session.SchemaVersion;
        node["Privacy"] = "Personal paths and identifiers redacted. No source media, screenshots, or dumps included.";
        var redactedSession = redactor.Redact(node)!.ToJsonString(JsonOptions);
        var timeline = new StringBuilder();
        foreach (var entry in session.Events)
        {
            timeline.AppendLine(redactor.Redact(JsonSerializer.SerializeToNode(entry))!.ToJsonString());
        }
        var summary = BuildSummary(session);
        if (getLogDirectory is not null)
        {
            summary +=
                "\n\n## Application logs\n\nRecent redacted application logs are included under `logs/`. "
                + "See `logs/manifest.json` for collection status, unavailable files, and size limits. "
                + "These logs are captured at export time and may include activity outside this diagnostic session.\n";
        }
        // Write alongside the destination and move only after the ZIP is complete.
        var tempPath = destinationPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var file = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                using var zip = new ZipArchive(file, ZipArchiveMode.Create, leaveOpen: true);
                await WriteEntryAsync(zip, "summary.md", redactor.Redact(summary));
                await WriteEntryAsync(zip, "session.json", redactedSession);
                await WriteEntryAsync(zip, "events.jsonl", timeline.ToString());
                if (comparison is not null)
                {
                    var comparisonRedactor = redactor;
                    await WriteEntryAsync(zip, "comparison/summary.md", comparisonRedactor.Redact(BuildSummary(comparison)));
                    await WriteEntryAsync(
                        zip,
                        "comparison/session.json",
                        comparisonRedactor.Redact(JsonSerializer.SerializeToNode(comparison))!.ToJsonString(JsonOptions)
                    );
                    await WriteEntryAsync(
                        zip,
                        "comparison/conditions.txt",
                        $"Same source hash: {!string.IsNullOrWhiteSpace(session.Gif?.Sha256) && session.Gif.Sha256 == comparison.Gif?.Sha256}\n"
                            + $"Windows API: {comparison.UseWindowsApi} -> {session.UseWindowsApi}\n"
                            + "The baseline was not automatically reset. File evidence and user observations must be considered together."
                    );
                }
                if (getLogDirectory is not null)
                {
                    await DiagnosticLogAttachments.WriteAsync(zip, getLogDirectory, redactor);
                }
            }
            File.Move(tempPath, destinationPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
    }

    public static string CreateSummary(DiagnosticSession session) => new DiagnosticRedactor(session).Redact(BuildSummary(session));

    private static string BuildSummary(DiagnosticSession session)
    {
        var text = new StringBuilder();
        text.AppendLine("# LockscreenGif diagnostic report");
        text.AppendLine();
        text.AppendLine($"Session: {session.Id}  ");
        text.AppendLine($"Started: {session.StartedAt:O}  ");
        text.AppendLine($"Ended: {session.EndedAt?.ToString("O") ?? "Not finished"}  ");
        text.AppendLine($"State: {session.Phase}  ");
        text.AppendLine($"Lock observed: {session.LockObserved}; unlock observed: {session.UnlockObserved}  ");
        text.AppendLine($"Monitoring complete: {session.MonitoringComplete}  ");
        text.AppendLine($"Windows API step: {(session.UseWindowsApi ? "On" : "Off")}  ");
        text.AppendLine($"Source: {(session.UseReference ? "Bundled reference animation" : "Selected GIF")}  ");
        if (session.ComparisonSessionId is not null)
        {
            text.AppendLine($"Compared with: {session.ComparisonSessionId}  ");
        }

        text.AppendLine($"User observation: {session.Observation ?? "Not supplied"}  ");
        if (!string.IsNullOrWhiteSpace(session.ObservationSurface))
        {
            text.AppendLine($"Observation surface: {session.ObservationSurface}");
        }

        if (session.Error is not null)
        {
            text.AppendLine($"Session error: {session.Error}");
        }

        if (session.Gif is { } gif)
        {
            text.AppendLine();
            text.AppendLine(
                $"GIF: {gif.Width} × {gif.Height}; {gif.FrameCount} frames; {gif.SizeBytes:N0} bytes; {gif.DurationSeconds:0.##} seconds."
            );
            text.AppendLine(
                $"Loop metadata: {(gif.LoopCount is null ? "Absent" : gif.LoopCount == 0 ? "Infinite" : gif.LoopCount.ToString())}. SHA-256: {gif.Sha256}"
            );
            if (gif.Error is not null)
            {
                text.AppendLine($"Inspection error: {gif.Error}");
            }

            foreach (var warning in gif.Warnings)
            {
                text.AppendLine($"- {warning}");
            }
        }
        if (session.ApplyResult is { } apply)
        {
            text.AppendLine();
            text.AppendLine(
                $"Apply: {(apply.Success ? "Completed" : apply.Cancelled ? "Cancelled" : "Failed or partial")}; "
                    + $"{apply.Files.Count} intended files, {apply.Files.Count(file => file.Copied)} copied, "
                    + $"{apply.Files.Count(file => file.Verified)} verified."
            );
            if (apply.ApiRequested)
            {
                text.AppendLine($"Windows image-setting API completed: {apply.ApiCompleted}.");
            }

            if (apply.Error is not null)
            {
                text.AppendLine($"Apply error: {apply.Error}");
            }

            foreach (var file in apply.Files.Where(file => file.Error is not null))
            {
                text.AppendLine($"- {file.Path}: {file.Error}");
            }
        }
        text.AppendLine();
        var trace = session.ProcessTrace;
        text.AppendLine(
            $"Process tracing: {trace.State}; collector lifetime: {trace.StartedAt:O} to {trace.EndedAt:O}. The end is worker cleanup time, not a guarantee of event coverage."
        );
        DiagnosticTraceShutdownSummary.AppendTo(text, trace.Shutdown);
        text.AppendLine(
            $"Collection gaps: ETW losses {trace.EventsLost}, transport losses {trace.QueueDropped}, unmatched operations {trace.UnmatchedOperations}, unresolved processes {trace.UnresolvedProcesses}, omitted operations {trace.OmittedOperations}, omitted aggregates {trace.OmittedAggregates}."
        );
        text.AppendLine(
            $"Unscoped system activity: unresolved paths {trace.UnresolvedPaths}, unmatched completions {trace.UnmatchedCompletions}. These counts do not establish a failed GIF access."
        );
        text.AppendLine();
        text.AppendLine(
            "Read totals in session.json cover the entire collection, including previous file contents and inspection activity. The image-access finding requires an independent application read beginning after that copy was verified; System I/O alone does not establish this."
        );
        text.AppendLine();
        text.AppendLine("## Findings");
        text.AppendLine();
        if (session.Findings.Count == 0)
        {
            text.AppendLine("No findings recorded yet.");
        }

        foreach (var finding in session.Findings)
        {
            text.AppendLine($"- **{finding.Title}** ({finding.Severity}; confidence: {finding.Confidence}): {finding.Detail}");
            foreach (var child in finding.Children)
            {
                text.AppendLine($"  - {child.Title}");
                text.AppendLine($"    ({child.Severity}): {child.Detail.Replace("\n", "\n    ")}");
            }
        }
        text.AppendLine();
        text.AppendLine("## Environment");
        text.AppendLine();
        foreach (var pair in session.Environment)
        {
            text.AppendLine($"- {pair.Key}: {pair.Value}");
        }

        text.AppendLine();
        text.AppendLine(
            $"Recorded {session.Events.Count} events and {session.Snapshots.Count} cache snapshots. The JSON files include per-file evidence and apply outcomes."
        );
        text.AppendLine();
        text.AppendLine(
            "File changes alone do not identify a writer. Process tracing records observed operations; file access does not prove decoding or visible playback. The reference animation is a controlled test source, not a guarantee of compatibility."
        );
        text.AppendLine();
        text.AppendLine(
            "Report schema: 2. Personal paths and identifiers are redacted. No source GIF, screenshots, or crash dumps are included. Review the report before sharing it."
        );
        return text.ToString();
    }

    private static async Task WriteEntryAsync(ZipArchive zip, string name, string content)
    {
        await using var stream = zip.CreateEntry(name, CompressionLevel.Optimal).Open();
        await using var writer = new StreamWriter(stream, new UTF8Encoding(false));
        await writer.WriteAsync(content);
    }
}
