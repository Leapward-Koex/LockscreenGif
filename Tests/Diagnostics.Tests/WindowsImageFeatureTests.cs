using System.IO.Compression;
using System.Text.Json;
using LockscreenGif.Models.Diagnostics;
using LockscreenGif.Privileged;
using LockscreenGif.Services.Diagnostics;

namespace Diagnostics.Tests;

internal static class WindowsImageFeatureTests
{
    internal static async Task RunAsync(string directory)
    {
        var session = new DiagnosticSession { ApplyResult = new() { Success = true } };
        var missing = DiagnosticWindowsImageFeature.Analyze(session).Single();
        Program.Check(missing.Severity == "Info" && missing.Title.EndsWith("unknown"), "legacy feature evidence remains unknown");

        session.ApplyResult.WindowsImageFeatureAtApply = new()
        {
            QueryStatus = 0,
            RuntimeState = 2,
            RuntimePriority = 0,
            OverrideExists = true,
            OverrideState = 1,
            OverrideOptions = 0,
        };
        session.Environment["Animation effects"] = "False";
        var findings = DiagnosticAnalyzer.Analyze(session);
        Program.Check(
            findings.Any(f =>
                f.Title == "Windows image feature was enabled at apply"
                && f.Severity == "Warning"
                && f.Detail.Contains("only read this configuration; it did not change the feature")
            ) && findings.All(f => f.Title != "Lock/unlock test deferred"),
            "enabled feature is a read-only observation and does not defer the lock cycle"
        );
        Program.Check(
            findings.Any(f => f.Title == "Windows Animation effects were off"),
            "effects-off baseline remains independent evidence"
        );
        var summary = DiagnosticReportWriter.CreateSummary(session);
        Program.Check(
            summary.Contains("RuntimeState=Enabled(2)")
                && summary.Contains("NextBootOverrideState=Disabled(1)")
                && summary.Contains("stored override differs")
                && !summary.Contains("restart", StringComparison.OrdinalIgnoreCase),
            "summary keeps runtime and override separate without restart requirements"
        );
        session.Environment.Clear();
        session.ApplyResult.WindowsImageFeatureAtApply.RuntimeState = 1;
        Program.Check(DiagnosticWindowsImageFeature.Analyze(session).Single().Severity == "Info", "disabled state is not playback success");
        session.ApplyResult.WindowsImageFeatureAtApply.QueryStatus = unchecked((int)0xC0000022);
        Program.Check(
            DiagnosticWindowsImageFeature.Analyze(session).Single().Title.EndsWith("unknown"),
            "failed native query cannot classify stale runtime state"
        );

        var feature = new WindowsImageFeatureResult
        {
            DesiredState = "Disabled",
            Outcome = "Failed",
            ChangeAttempted = true,
            Changed = false,
            RuntimeChanged = false,
            NativeSetStatus = unchecked((int)0xC0000022),
            ChangeOutcomeUnknown = true,
            Error = @"Synthetic failure for C:\Users\PrivatePerson\PrivateSource.gif",
            Before = new()
            {
                QueryStatus = 0,
                RuntimeState = 2,
                OverrideExists = false,
            },
            After = new()
            {
                QueryStatus = 0,
                RuntimeState = 1,
                OverrideExists = true,
                OverrideState = 1,
                OverrideOptions = 0,
            },
        };
        DiagnosticsActionLog.Record(
            "DisableWindowsImageFeature",
            "Failed",
            @"Action for C:\Users\PrivatePerson\PrivateSource.gif",
            feature
        );
        feature.After!.RuntimeState = 2;
        var captured = DiagnosticsActionLog.Snapshot();
        Program.Check(captured[^1].WindowsImageFeature!.After!.RuntimeState == 1, "recording action takes an independent evidence copy");
        captured[^1].WindowsImageFeature!.After!.RuntimeState = 0;
        Program.Check(
            DiagnosticsActionLog.Snapshot()[^1].WindowsImageFeature!.After!.RuntimeState == 1,
            "action snapshots cannot mutate retained evidence"
        );
        session.PrerequisiteActions = DiagnosticsActionLog.Snapshot();
        summary = DiagnosticReportWriter.CreateSummary(session);
        Program.Check(
            summary.Contains("DisableWindowsImageFeature")
                && summary.Contains("Native set status: 0xC0000022")
                && summary.Contains("Changed=false does not establish that no mutation occurred")
                && summary.Contains("Applying the GIF will not repair it")
                && !summary.Contains("PrivatePerson")
                && !summary.Contains("PrivateSource"),
            "summary retains explicit action errors and uncertain mutation separately with privacy redaction"
        );
        var destination = Path.Combine(directory, "windows-image-feature.zip");
        await DiagnosticReportWriter.ExportAsync(session, destination);
        using (var zip = ZipFile.OpenRead(destination))
        using (var reader = new StreamReader(zip.GetEntry("session.json")!.Open()))
        using (var json = JsonDocument.Parse(await reader.ReadToEndAsync()))
        {
            var apply = json.RootElement.GetProperty("ApplyResult");
            var action = json.RootElement.GetProperty("PrerequisiteActions").EnumerateArray().Last();
            Program.Check(
                apply.GetProperty("WindowsImageFeatureAtApply").GetProperty("RuntimeState").GetUInt32() == 1
                    && !apply.TryGetProperty("WindowsImageFeature", out _)
                    && action.GetProperty("WindowsImageFeature").GetProperty("NativeSetStatus").GetInt32() == unchecked((int)0xC0000022)
                    && action.GetProperty("WindowsImageFeature").GetProperty("After").GetProperty("RuntimeState").GetUInt32() == 1,
                "ZIP separates read-only apply snapshot from native-set action result"
            );
        }
        for (var i = 0; i <= DiagnosticsActionLog.MaximumActions; i++)
        {
            DiagnosticsActionLog.Record("OpenLockscreenSettings", "Succeeded", i.ToString());
        }
        var bounded = DiagnosticsActionLog.Snapshot();
        Program.Check(
            bounded.Count == 100 && bounded[0].Detail == "1" && bounded[^1].Detail == "100",
            "action history retains only the latest 100 entries"
        );
        DiagnosticsActionLog.Record("OpenLockscreenSettings", "Failed", new string('x', 10000));
        Program.Check(DiagnosticsActionLog.Snapshot()[^1].Detail!.Length < 2100, "action detail size is bounded");
        await CustomFeatureIdsAsync(directory);
    }

    private static async Task CustomFeatureIdsAsync(string directory)
    {
        const uint applyId = 12345678;
        const uint actionId = 87654321;
        var session = new DiagnosticSession
        {
            ApplyResult = new()
            {
                Success = true,
                WindowsImageFeatureAtApply = new()
                {
                    FeatureId = applyId,
                    QueryStatus = 0,
                    RuntimeState = 2,
                    OverrideExists = false,
                },
            },
            Environment = new() { [DiagnosticWindowsImageFeature.EnvironmentKey] = "Feature=12345678; RuntimeState=Enabled(2)" },
        };
        DiagnosticsActionLog.Record(
            "ChangeWindowsImageFeatureId",
            "Succeeded",
            "Selected ID: 38943831 -> 12345678; Windows state unchanged."
        );
        var action = new WindowsImageFeatureResult
        {
            FeatureId = actionId,
            Outcome = "Disabled",
            Before = new()
            {
                FeatureId = actionId,
                QueryStatus = 0,
                RuntimeState = 2,
            },
            After = new()
            {
                FeatureId = actionId,
                QueryStatus = 0,
                RuntimeState = 1,
            },
        };
        DiagnosticsActionLog.Record("DisableWindowsImageFeature", "Succeeded", feature: action);
        action.FeatureId = 99999999;
        action.After.FeatureId = 99999999;
        DiagnosticsActionLog.Record(
            "ResetWindowsImageFeatureId",
            "Succeeded",
            "Selected ID: 87654321 -> 38943831; Windows state unchanged."
        );
        session.PrerequisiteActions = DiagnosticsActionLog.Snapshot();
        var summary = DiagnosticReportWriter.CreateSummary(session);
        Program.Check(
            summary.Contains("Feature=12345678")
                && summary.Contains("Feature 87654321")
                && summary.Contains("Feature=87654321")
                && summary.Contains("ResetWindowsImageFeatureId")
                && !summary.Contains("Feature=99999999"),
            "summary retains captured custom IDs independently of later preference changes and resets"
        );
        var destination = Path.Combine(directory, "custom-feature-ids.zip");
        await DiagnosticReportWriter.ExportAsync(session, destination);
        using var zip = ZipFile.OpenRead(destination);
        using var reader = new StreamReader(zip.GetEntry("session.json")!.Open());
        using var json = JsonDocument.Parse(await reader.ReadToEndAsync());
        var exportedAction = json
            .RootElement.GetProperty("PrerequisiteActions")
            .EnumerateArray()
            .Last(entry => entry.GetProperty("Action").GetString() == "DisableWindowsImageFeature")
            .GetProperty("WindowsImageFeature");
        Program.Check(
            json.RootElement.GetProperty("ApplyResult").GetProperty("WindowsImageFeatureAtApply").GetProperty("FeatureId").GetUInt32()
                == applyId
                && exportedAction.GetProperty("FeatureId").GetUInt32() == actionId
                && exportedAction.GetProperty("After").GetProperty("FeatureId").GetUInt32() == actionId,
            "export preserves the actual ID of each apply snapshot and explicit feature action"
        );
    }
}
