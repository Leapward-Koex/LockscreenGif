using LockscreenGif.Privileged;

namespace LockscreenGif.Models.Diagnostics;

public sealed class DiagnosticActionEvent
{
    public DateTimeOffset Timestamp { get; set; }
    public string Action { get; set; } = "";
    public string Outcome { get; set; } = "";
    public string? Detail { get; set; }
    public WindowsImageFeatureResult? WindowsImageFeature { get; set; }
}
