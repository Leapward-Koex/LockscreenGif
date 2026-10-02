using System.Text.Json;
using LockscreenGif.Models.Diagnostics;
using LockscreenGif.Privileged;

namespace LockscreenGif.Services.Diagnostics;

/// <summary>Bounded app-lifetime action evidence, independent of any particular GIF apply or diagnostic test.</summary>
public static class DiagnosticsActionLog
{
    public const int MaximumActions = 100;
    private const int MaximumDetailLength = 2048;
    private static readonly object Gate = new();
    private static readonly Queue<DiagnosticActionEvent> Actions = new();

    public static void Record(string action, string outcome, string? detail = null, WindowsImageFeatureResult? feature = null)
    {
        var entry = new DiagnosticActionEvent
        {
            Timestamp = DateTimeOffset.UtcNow,
            Action = Limit(action, 128) ?? "",
            Outcome = Limit(outcome, 128) ?? "",
            Detail = Limit(detail, MaximumDetailLength),
            WindowsImageFeature = feature is null ? null : Clone(feature),
        };
        if (entry.WindowsImageFeature is { } evidence)
        {
            evidence.Error = Limit(evidence.Error, MaximumDetailLength);
            foreach (var state in new[] { evidence.Before, evidence.After })
            {
                if (state is not null)
                {
                    state.QueryError = Limit(state.QueryError, MaximumDetailLength);
                    state.OverrideError = Limit(state.OverrideError, MaximumDetailLength);
                }
            }
        }

        lock (Gate)
        {
            Actions.Enqueue(entry);
            while (Actions.Count > MaximumActions)
            {
                Actions.Dequeue();
            }
        }

        Logger.Info("Prerequisite action: " + JsonSerializer.Serialize(entry));
    }

    public static List<DiagnosticActionEvent> Snapshot()
    {
        lock (Gate)
        {
            return Actions.Select(Clone).ToList();
        }
    }

    private static T Clone<T>(T value) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value))!;

    private static string? Limit(string? value, int maximum) =>
        value is { Length: > 0 } && value.Length > maximum ? value[..maximum] + " [truncated]" : value;
}
