namespace LockscreenGif.Models;

public enum LockscreenApplyFailureReason
{
    Unknown,
    SourceReadFailed,
    InvalidSource,
    WindowsApiFailed,
    CacheInaccessible,
    CacheMissing,
    NoDestinations,
    CacheDiscoveryFailed,
    CopyFailed,
    VerificationFailed,
}

public sealed class LockscreenApplyResult
{
    public bool Success { get; set; }
    public bool Cancelled { get; set; }
    public bool ApiRequested { get; set; }
    public bool ApiCompleted { get; set; }
    public LockscreenGif.Privileged.WindowsImageFeatureState? WindowsImageFeatureAtApply { get; set; }

    // Metadata from the held source stream, not a sum of cache copies or a display resolution.
    public long? SourceSizeBytes { get; set; }
    public int? SourceWidth { get; set; }
    public int? SourceHeight { get; set; }
    public string? Error { get; set; }
    public LockscreenApplyFailureReason? FailureReason { get; set; }

    // Preserve the original failure for the operation boundary without exporting private exception details.
    [System.Text.Json.Serialization.JsonIgnore]
    public Exception? FailureException { get; set; }
    public List<LockscreenFileResult> Files { get; set; } = [];
}

public sealed class LockscreenFileResult
{
    public string Path { get; init; } = string.Empty;
    public bool Copied { get; set; }
    public bool Verified { get; set; }
    public DateTimeOffset? VerifiedAt { get; set; }
    public string? Error { get; set; }

    [System.Text.Json.Serialization.JsonIgnore]
    public Exception? FailureException { get; set; }
    public string? Sha256 { get; set; }
}

public sealed class LockscreenApplyEvent
{
    public string Stage { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;
    public string? Path { get; init; }
    public string Severity { get; init; } = "Info";
}
