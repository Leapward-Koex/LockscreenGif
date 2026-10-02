using LockscreenGif.Models;

namespace LockscreenGif.Models.Diagnostics;

public sealed class DiagnosticSession
{
    public int SchemaVersion { get; set; } = 2;
    public LockscreenGif.Privileged.ProcessTraceEvidence ProcessTrace { get; set; } = new();
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? EndedAt { get; set; }
    public string Phase { get; set; } = "Preparing";
    public string SourceName { get; set; } = "";
    public string? SourcePath { get; set; }
    public bool UseWindowsApi { get; set; }
    public bool UseReference { get; set; }
    public string? Observation { get; set; }
    public string? ObservationSurface { get; set; }
    public string? Error { get; set; }
    public string? ComparisonSessionId { get; set; }
    public bool LockObserved { get; set; }
    public bool UnlockObserved { get; set; }
    public bool MonitoringComplete { get; set; } = true;
    public bool EvidenceTruncated { get; set; }
    public int OmittedSnapshotCount { get; set; }
    public int OmittedEnvironmentObservationCount { get; set; }
    public Dictionary<string, string> Environment { get; set; } = new();
    public List<EnvironmentEvidence> EnvironmentObservations { get; set; } = new();
    public GifInspection? Gif { get; set; }
    public LockscreenApplyResult? ApplyResult { get; set; }
    public List<DiagnosticEvent> Events { get; set; } = new();
    public List<DiagnosticActionEvent> PrerequisiteActions { get; set; } = new();
    public List<CacheSnapshot> Snapshots { get; set; } = new();
    public List<DiagnosticFinding> Findings { get; set; } = new();
}
