using System.Text;
using LockscreenGif.Models.Diagnostics;
using LockscreenGif.Privileged;

namespace LockscreenGif.Services.Diagnostics;

internal static class DiagnosticWindowsImageFeature
{
    internal const string EnvironmentKey = "Windows image feature";

    internal static IEnumerable<DiagnosticFinding> Analyze(DiagnosticSession session)
    {
        if (session.ApplyResult is { } apply)
        {
            var state = apply.WindowsImageFeatureAtApply;
            var known = state is { QueryStatus: 0, QueryError: null, RuntimeState: 1 or 2 };
            var title =
                !known ? "Windows image feature status unknown"
                : state!.RuntimeState == 1 ? "Windows image feature was disabled at apply"
                : "Windows image feature was enabled at apply";
            var detail = state is null
                ? "This report has no read-only Windows image feature snapshot at apply. Missing evidence in older reports is unknown."
                : WindowsImageFeature.Describe(state)
                    + ". Applying the GIF only read this configuration; it did not change the feature. "
                    + OverrideDescription(state)
                    + " Configuration readback does not prove visible animation; observe the lock and sign-in screens separately.";
            yield return new(title, detail, known && state!.RuntimeState == 2 ? "Warning" : "Info", "Configuration evidence");
        }

        if (
            session.Environment.TryGetValue("Animation effects", out var animations)
            && bool.TryParse(animations, out var enabled)
            && !enabled
        )
        {
            yield return new(
                "Windows Animation effects were off",
                "The baseline recorded Animation effects off. The feature workaround was demonstrated with this setting on; disabled effects can still prevent animation. Enable Animation effects in Windows Settings > Accessibility > Visual effects and repeat the test.",
                "Warning",
                "Configuration evidence"
            );
        }
    }

    internal static void AppendTo(StringBuilder text, DiagnosticSession session)
    {
        text.AppendLine();
        text.AppendLine("## Windows image compatibility at apply");
        text.AppendLine();
        if (session.ApplyResult?.WindowsImageFeatureAtApply is not { } state)
        {
            text.AppendLine("Read-only feature evidence at apply was not recorded; configuration is unknown.");
        }
        else
        {
            text.AppendLine(WindowsImageFeature.Describe(state));
            text.AppendLine(OverrideDescription(state));
        }

        text.AppendLine(
            "GIF Apply reads this feature for diagnostics and never changes it. Explicit prerequisite and Settings actions are recorded separately below."
        );
        text.AppendLine($"Baseline Animation effects: {session.Environment.GetValueOrDefault("Animation effects", "Not recorded")}.");
        text.AppendLine(
            "Runtime results describe the querying caller. The persistent override is separate configuration; neither establishes the state used by LogonUI or proves visible animation."
        );
        text.AppendLine(
            "Each snapshot retains the feature ID selected when it was captured. Later feature-ID edits or resets do not rewrite earlier evidence or change either feature's Windows state."
        );

        text.AppendLine();
        text.AppendLine("## Prerequisite and Settings actions");
        text.AppendLine();
        text.AppendLine(
            "Recent actions from this app lifetime, captured when creating the report (maximum 100). They can precede or follow this test; they are not GIF apply operations. Older actions may have been omitted."
        );
        if (session.PrerequisiteActions.Count == 0)
        {
            text.AppendLine("No actions recorded in this report. Older reports may not contain action history.");
        }

        foreach (var action in session.PrerequisiteActions)
        {
            text.AppendLine($"- {action.Timestamp:O}: {action.Action}; outcome: {action.Outcome}. {action.Detail}");
            if (action.WindowsImageFeature is not { } feature)
            {
                continue;
            }

            text.AppendLine(
                $"  Feature {feature.FeatureId}; requested state: {feature.DesiredState}; result: {feature.Outcome}; attempted: {feature.ChangeAttempted}; changed: {feature.Changed}; runtime changed: {feature.RuntimeChanged}; change outcome unknown: {feature.ChangeOutcomeUnknown}."
            );
            text.AppendLine(
                $"  Native set status: {(feature.NativeSetStatus is int status ? $"0x{unchecked((uint)status):X8}" : "Not recorded")}."
            );
            text.AppendLine($"  Before: {Describe(feature.Before)}");
            text.AppendLine($"  After: {Describe(feature.After)}");
            if (!string.IsNullOrWhiteSpace(feature.Error))
            {
                text.AppendLine($"  Error: {feature.Error}");
            }

            if (feature.ChangeOutcomeUnknown)
            {
                text.AppendLine("  The final action outcome is unknown; Changed=false does not establish that no mutation occurred.");
            }

            if (feature.ChangeOutcomeUnknown || feature.Outcome is "Failed" or "Cancelled" or "NeedsChange")
            {
                text.AppendLine("  Re-check the setting and complete the explicit feature action. Applying the GIF will not repair it.");
            }
        }
    }

    private static string Describe(WindowsImageFeatureState? state) =>
        state is null ? "Not recorded." : WindowsImageFeature.Describe(state);

    private static string OverrideDescription(WindowsImageFeatureState state) =>
        state.OverrideExists == true
        && state.OverrideError is null
        && state.OverrideState is 1 or 2
        && state.OverrideState != state.RuntimeState
            ? "The stored override differs from the caller runtime state; this snapshot alone does not identify which action wrote it."
            : "The stored override is separate configuration evidence; it does not establish that a feature change occurred during this test.";
}
