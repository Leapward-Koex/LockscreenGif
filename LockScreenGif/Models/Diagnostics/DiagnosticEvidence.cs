namespace LockscreenGif.Models.Diagnostics;

public sealed record DiagnosticEvent(
    DateTimeOffset Timestamp,
    long ElapsedMilliseconds,
    string Category,
    string Message,
    string Severity = "Info"
);

public sealed record DiagnosticFinding(string Title, string Detail, string Severity = "Info", string Confidence = "Observed")
{
    public List<DiagnosticFinding> Children { get; init; } = [];
}

public sealed record DiagnosticCheck(string Name, string Status, string Detail);

public sealed record EnvironmentEvidence(DateTimeOffset Timestamp, string Reason, Dictionary<string, string> Values);

public sealed class GifInspection
{
    public string Format { get; set; } = "Unknown";
    public string Sha256 { get; set; } = "";
    public long SizeBytes { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public int FrameCount { get; set; }
    public int? LoopCount { get; set; }
    public double DurationSeconds { get; set; }
    public string? Error { get; set; }
    public bool IsAnimated => FrameCount > 1;
    public List<string> Warnings { get; set; } = new();
}

public sealed class CacheSnapshot
{
    public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.UtcNow;
    public string Reason { get; set; } = "";
    public bool Complete { get; set; } = true;
    public List<string> Errors { get; set; } = new();
    public List<CacheFileEvidence> Files { get; set; } = new();
}

public sealed class CacheFileEvidence
{
    public string Path { get; set; } = "";
    public long Length { get; set; }
    public DateTime LastWriteUtc { get; set; }
    public DateTime CreationUtc { get; set; }
    public string? Sha256 { get; set; }
    public string Format { get; set; } = "Unknown";
    public bool Stable { get; set; }
    public string? Error { get; set; }
    public string HashSource { get; set; } = "Not read";
    public DateTimeOffset? HashReadAt { get; set; }
}
